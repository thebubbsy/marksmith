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

        var tempDir = destDir.TrimEnd('/', '\\') + "-extract-tmp";
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        Directory.CreateDirectory(tempDir);
        try
        {
            if (isTarGz)
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
