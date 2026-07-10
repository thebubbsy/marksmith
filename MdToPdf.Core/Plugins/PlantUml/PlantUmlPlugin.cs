using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MdToPdf.Plugins.PlantUml;

// Renders ```plantuml / ```puml fences via the real PlantUML engine, out-of-process. There is no
// mature pure-JS PlantUML renderer the way Mermaid has mermaid.min.js — full fidelity only exists
// in the actual Java implementation — so this plugin downloads (on explicit user opt-in, never
// bundled) a private Eclipse Temurin JRE and the MIT-licensed plantuml.jar into its own plugin
// directory, isolated from any Java the user's system may or may not already have. One `java`
// process is spawned per unique diagram (not a shared long-lived pipe process — simpler and more
// robust than multiplexing several SVG outputs off one stdin/stdout stream), and PluginManager
// caches results by content hash so the live preview's per-keystroke re-render doesn't repeatedly
// pay JVM startup cost for unchanged diagrams.
public sealed class PlantUmlPlugin : IDiagramPlugin
{
    public string Id => "plantuml";
    public string Name => "PlantUML Diagrams";
    public string Description =>
        "Renders ```plantuml and ```puml code blocks as diagrams. Downloads a private Java " +
        "runtime + the PlantUML engine (~90 MB) on install, isolated from any Java already on " +
        "your system — nothing is bundled until you opt in.";
    public string Version => "1.0.0";
    public IReadOnlyList<string> FenceLanguages { get; } = new[] { "plantuml", "puml" };

    private static string RootDir => PluginManager.PluginsRoot("plantuml");
    private static string JreDir => Path.Combine(RootDir, "jre");
    private static string JarPath => Path.Combine(RootDir, "plantuml.jar");
    private static string JavaExe => Path.Combine(JreDir, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");

    public PluginInstallState State =>
        File.Exists(JavaExe) && File.Exists(JarPath) ? PluginInstallState.Installed : PluginInstallState.NotInstalled;

    public async Task InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RootDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Marksmith-PluginInstaller/1.0 (+https://github.com/thebubbsy/marksmith)");

        var (os, arch) = PlatformIds();

        var (jreUrl, jreSize) = await GetJreDownloadInfoAsync(http, os, arch, cancellationToken);
        var jreArchive = Path.Combine(RootDir, "jre-download" + (os == "windows" ? ".zip" : ".tar.gz"));
        try
        {
            await DownloadAsync(http, jreUrl, jreArchive, jreSize, p => progress?.Report(p * 0.70), cancellationToken);
            ExtractJre(jreArchive, os);
        }
        finally
        {
            if (File.Exists(jreArchive)) File.Delete(jreArchive);
        }

        var (jarUrl, jarSize) = await GetJarDownloadInfoAsync(http, cancellationToken);
        await DownloadAsync(http, jarUrl, JarPath, jarSize, p => progress?.Report(0.70 + p * 0.30), cancellationToken);

        if (State != PluginInstallState.Installed)
            throw new InvalidOperationException("PlantUML install completed but java/plantuml.jar are still missing.");

        progress?.Report(1.0);
    }

    public void Uninstall()
    {
        if (Directory.Exists(RootDir)) Directory.Delete(RootDir, recursive: true);
    }

    public string? RenderToSvg(string diagramSource)
    {
        if (State != PluginInstallState.Installed) return null;
        try
        {
            var text = diagramSource.Trim();
            if (!text.Contains("@start", StringComparison.Ordinal)) text = "@startuml\n" + text + "\n@enduml";

            var psi = new ProcessStartInfo
            {
                FileName = JavaExe,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // -Playout=smetana: PlantUML's built-in pure-Java graph layout engine, used instead of
            // the native Graphviz `dot` binary this plugin deliberately does not bundle (one fewer
            // per-platform native dependency to download/manage).
            foreach (var arg in new[] { "-Djava.awt.headless=true", "-jar", JarPath, "-tsvg", "-pipe", "-charset", "UTF-8", "-Playout=smetana" })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            process.StandardInput.Write(text);
            process.StandardInput.Close();

            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            var svg = stdoutTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(svg)) return null;

            var start = svg.IndexOf("<svg", StringComparison.Ordinal);
            var end = svg.LastIndexOf("</svg>", StringComparison.Ordinal);
            if (start < 0 || end < 0) return null;
            svg = svg.Substring(start, end + "</svg>".Length - start);

            // Defensive: PlantUML doesn't emit <script> in SVG output, but never trust generated
            // markup that ends up injected raw into the preview/export HTML.
            return Regex.Replace(svg, "<script.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    private static (string os, string arch) PlatformIds()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "mac"
            : "linux";
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        return (os, arch);
    }

    // Eclipse Temurin (Adoptium) — official, MIT-licensed OpenJDK builds; JRE-only image to avoid
    // bundling compiler/dev tooling this plugin never uses.
    private static async Task<(string url, long size)> GetJreDownloadInfoAsync(HttpClient http, string os, string arch, CancellationToken ct)
    {
        var apiUrl = $"https://api.adoptium.net/v3/assets/latest/17/hotspot?image_type=jre&os={os}&architecture={arch}";
        using var response = await http.GetAsync(apiUrl, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var package = doc.RootElement[0].GetProperty("binary").GetProperty("package");
        return (package.GetProperty("link").GetString()!, package.GetProperty("size").GetInt64());
    }

    // The `-mit` variant of plantuml.jar: same engine, MIT license instead of the default's GPL —
    // safe to redistribute alongside a closed-source commercial app.
    private static async Task<(string url, long size)> GetJarDownloadInfoAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync("https://api.github.com/repos/plantuml/plantuml/releases/latest", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (Regex.IsMatch(name, @"^plantuml-mit-[\d.]+\.jar$"))
                return (asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64());
        }
        throw new InvalidOperationException("Could not find a plantuml-mit-*.jar asset in the latest PlantUML release.");
    }

    private static async Task DownloadAsync(HttpClient http, string url, string destPath, long expectedSize, Action<double> onProgress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destPath);
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (total > 0) onProgress((double)readTotal / total);
        }
    }

    private static void ExtractJre(string archivePath, string os)
    {
        var tempExtract = Path.Combine(RootDir, "jre-extract-tmp");
        if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true);
        Directory.CreateDirectory(tempExtract);

        if (os == "windows")
        {
            ZipFile.ExtractToDirectory(archivePath, tempExtract);
        }
        else
        {
            using var gzip = new GZipStream(File.OpenRead(archivePath), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, tempExtract, overwriteFiles: true);
        }

        // Adoptium archives contain one top-level "jdk-17.x+y-jre" folder; flatten it so JavaExe's
        // path (JreDir/bin/java[.exe]) is stable across versions.
        var topLevel = Directory.GetDirectories(tempExtract);
        var sourceRoot = topLevel.Length == 1 ? topLevel[0] : tempExtract;

        if (Directory.Exists(JreDir)) Directory.Delete(JreDir, recursive: true);
        Directory.Move(sourceRoot, JreDir);
        Directory.Delete(tempExtract, recursive: true);

        if (!OperatingSystem.IsWindows())
        {
            var javaBin = Path.Combine(JreDir, "bin", "java");
            if (File.Exists(javaBin)) File.SetUnixFileMode(javaBin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
