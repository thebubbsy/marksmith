using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MdToPdf.Plugins;

// Download/extract/provision machinery shared by every manifest-driven plugin: platform ids,
// progress-reporting HTTP download with optional sha256 verification, zip/tar.gz extraction, and
// the "jre" managed-runtime provisioner (private Eclipse Temurin JRE via Adoptium's official API).
internal static class PluginInstall
{
    public static (string Os, string Arch) PlatformIds()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "mac"
            : "linux";
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        return (os, arch);
    }

    public static HttpClient NewHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Marksmith-PluginInstaller/1.0 (+https://github.com/thebubbsy/marksmith)");
        return http;
    }

    public static string JavaExePath(string pluginDir) =>
        Path.Combine(pluginDir, "jre", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");

    // Windows zips put node.exe at the archive root; Linux/macOS tarballs put it under bin/.
    public static string NodeExePath(string pluginDir) => OperatingSystem.IsWindows()
        ? Path.Combine(pluginDir, "node", "node.exe")
        : Path.Combine(pluginDir, "node", "bin", "node");

    public static async Task InstallNodeAsync(HttpClient http, string pluginDir,
        Action<double> onProgress, CancellationToken ct)
    {
        // nodejs.org's official dist index; first entry whose lts field isn't false is the
        // newest LTS. (No pinned version: like the JRE provisioner, runtimes track upstream.)
        using var indexResponse = await http.GetAsync("https://nodejs.org/dist/index.json", ct);
        indexResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await indexResponse.Content.ReadAsStreamAsync(ct));
        string? version = null;
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var lts = entry.GetProperty("lts");
            if (lts.ValueKind != JsonValueKind.False) { version = entry.GetProperty("version").GetString(); break; }
        }
        if (version == null) throw new InvalidOperationException("No LTS release found in nodejs.org dist index.");

        var (os, arch) = PlatformIds();
        var (platform, ext) = os switch
        {
            "windows" => ($"win-{(arch == "aarch64" ? "arm64" : "x64")}", ".zip"),
            "mac" => ($"darwin-{(arch == "aarch64" ? "arm64" : "x64")}", ".tar.gz"),
            _ => ($"linux-{(arch == "aarch64" ? "arm64" : "x64")}", ".tar.xz"),
        };
        var url = $"https://nodejs.org/dist/{version}/node-{version}-{platform}{ext}";

        var archive = Path.Combine(pluginDir, "node-download" + ext);
        try
        {
            await DownloadAsync(http, url, archive, expectedSize: 0, expectedSha256: null, onProgress, ct);
            var nodeDir = Path.Combine(pluginDir, "node");
            if (Directory.Exists(nodeDir)) Directory.Delete(nodeDir, recursive: true);
            ExtractArchive(archive, nodeDir, stripRoot: true);
            if (!OperatingSystem.IsWindows())
                MakeExecutable(Path.Combine(nodeDir, "bin", "node"));
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    public static async Task InstallJreAsync(HttpClient http, string pluginDir, int majorVersion,
        Action<double> onProgress, CancellationToken ct)
    {
        var (os, arch) = PlatformIds();
        var apiUrl = $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot?image_type=jre&os={os}&architecture={arch}";
        using var response = await http.GetAsync(apiUrl, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var package = doc.RootElement[0].GetProperty("binary").GetProperty("package");
        var url = package.GetProperty("link").GetString()!;
        var size = package.GetProperty("size").GetInt64();

        var archive = Path.Combine(pluginDir, "jre-download" + (os == "windows" ? ".zip" : ".tar.gz"));
        try
        {
            await DownloadAsync(http, url, archive, size, expectedSha256: null, onProgress, ct);

            // Adoptium archives contain one top-level "jdk-17.x+y-jre" folder; strip it so the java
            // executable's path (jre/bin/java[.exe]) is stable across JRE versions.
            var jreDir = Path.Combine(pluginDir, "jre");
            if (Directory.Exists(jreDir)) Directory.Delete(jreDir, recursive: true);
            ExtractArchive(archive, jreDir, stripRoot: true);

            if (!OperatingSystem.IsWindows())
                MakeExecutable(Path.Combine(jreDir, "bin", "java"));
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    public static async Task InstallArtifactAsync(HttpClient http, string pluginDir, PluginArtifact artifact,
        Action<double> onProgress, CancellationToken ct)
    {
        if (artifact.Source == "npm")
        {
            await InstallNpmPackageAsync(pluginDir, artifact, ct);
            onProgress(1.0);
            return;
        }

        var (url, size) = artifact.Source switch
        {
            "github-latest" => await ResolveGithubLatestAsync(http, artifact, ct),
            "url" when !string.IsNullOrWhiteSpace(artifact.Url) => (artifact.Url!, 0L),
            _ => throw new InvalidOperationException($"Artifact '{artifact.Name}': unsupported source '{artifact.Source}'."),
        };

        var target = Path.Combine(pluginDir, artifact.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (!artifact.Extract)
        {
            await DownloadAsync(http, url, target, size, artifact.Sha256, onProgress, ct);
            if (!OperatingSystem.IsWindows()) MakeExecutable(target);
            return;
        }

        var archive = target + ".download";
        try
        {
            await DownloadAsync(http, url, archive, size, artifact.Sha256, onProgress, ct);
            ExtractArchive(archive, pluginDir, artifact.StripRoot, archiveNameHint: artifact.Name);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    // npm install via the plugin's own provisioned Node runtime (node ships npm as
    // node_modules/npm/bin/npm-cli.js inside the dist archive — no global npm assumed). The
    // package tree lands in <plugin>/npm/node_modules/<package>.
    public static async Task InstallNpmPackageAsync(string pluginDir, PluginArtifact artifact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.Package))
            throw new InvalidOperationException("npm artifact requires \"package\".");
        var nodeExe = NodeExePath(pluginDir);
        if (!File.Exists(nodeExe))
            throw new InvalidOperationException("npm artifact requires runtime { \"kind\": \"node\" } (declare it before the artifact).");

        var nodeDir = Path.GetDirectoryName(nodeExe)!;
        var npmCli = OperatingSystem.IsWindows()
            ? Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js")
            : Path.Combine(nodeDir, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npmCli))
            throw new InvalidOperationException($"npm-cli.js not found in the provisioned Node runtime ({npmCli}).");

        var prefix = Path.Combine(pluginDir, "npm");
        Directory.CreateDirectory(prefix);
        var spec = artifact.Package + (string.IsNullOrWhiteSpace(artifact.PackageVersion) ? "" : "@" + artifact.PackageVersion);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = nodeExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = prefix,
        };
        foreach (var a in new[] { npmCli, "install", spec, "--prefix", prefix, "--no-audit", "--no-fund", "--loglevel", "error" })
            psi.ArgumentList.Add(a);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start npm.");
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"npm install {spec} failed: {(await stderrTask).Trim()}");
    }

    private static async Task<(string url, long size)> ResolveGithubLatestAsync(HttpClient http, PluginArtifact artifact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.Repo) || string.IsNullOrWhiteSpace(artifact.AssetPattern))
            throw new InvalidOperationException($"Artifact '{artifact.Name}': github-latest requires repo and assetPattern.");

        using var response = await http.GetAsync($"https://api.github.com/repos/{artifact.Repo}/releases/latest", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (Regex.IsMatch(name, artifact.AssetPattern!))
                return (asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64());
        }
        throw new InvalidOperationException($"No asset matching '{artifact.AssetPattern}' in the latest {artifact.Repo} release.");
    }

    private static async Task DownloadAsync(HttpClient http, string url, string destPath, long expectedSize,
        string? expectedSha256, Action<double> onProgress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var dest = File.Create(destPath))
        {
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

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            await using var stream = File.OpenRead(destPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                File.Delete(destPath);
                throw new InvalidOperationException($"Checksum mismatch for {Path.GetFileName(destPath)} — expected {expectedSha256}, got {actual}. Download discarded.");
            }
        }
    }

    private static void ExtractArchive(string archivePath, string destDir, bool stripRoot, string? archiveNameHint = null)
    {
        var isTarGz = archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                      (archiveNameHint?.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ?? false);
        var isTarXz = archivePath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ||
                      (archiveNameHint?.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ?? false);

        var tempDir = destDir.TrimEnd('/', '\\') + "-extract-tmp";
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        Directory.CreateDirectory(tempDir);
        try
        {
            if (isTarXz)
            {
                // .NET has no built-in XZ decoder — SharpCompress supplies the XZStream; the tar
                // inside is then handled by the same System.Formats.Tar as the .tar.gz path.
                // Unlocks Typst/Node Linux+macOS releases, which ship exclusively as .tar.xz.
                // The FileStream needs its own using: XZStream does NOT dispose the wrapped stream,
                // which left the archive locked and made the post-extract delete fail.
                using var archiveStream = File.OpenRead(archivePath);
                using var xz = new SharpCompress.Compressors.Xz.XZStream(archiveStream);
                TarFile.ExtractToDirectory(xz, tempDir, overwriteFiles: true);
            }
            else if (isTarGz)
            {
                using var gzip = new GZipStream(File.OpenRead(archivePath), CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, tempDir, overwriteFiles: true);
            }
            else
            {
                ZipFile.ExtractToDirectory(archivePath, tempDir);
            }

            // macOS-built tarballs (e.g. D2's releases) carry AppleDouble "._name" companions and
            // .DS_Store at every level — pure metadata junk. Ignore it both when deciding whether
            // there's a single wrapping root to strip and when copying files out.
            static bool IsAppleJunk(string path)
            {
                var name = Path.GetFileName(path);
                return name.StartsWith("._", StringComparison.Ordinal) || name == ".DS_Store";
            }

            var sourceRoot = tempDir;
            if (stripRoot)
            {
                var topLevel = Directory.GetDirectories(tempDir).Where(d => !IsAppleJunk(d)).ToArray();
                if (topLevel.Length == 1 && !Directory.GetFiles(tempDir).Any(f => !IsAppleJunk(f)))
                    sourceRoot = topLevel[0];
            }

            Directory.CreateDirectory(destDir);
            foreach (var dir in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                if (IsAppleJunk(dir)) continue;
                Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceRoot, dir)));
            }
            foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                if (IsAppleJunk(file)) continue;
                File.Copy(file, Path.Combine(destDir, Path.GetRelativePath(sourceRoot, file)), overwrite: true);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
