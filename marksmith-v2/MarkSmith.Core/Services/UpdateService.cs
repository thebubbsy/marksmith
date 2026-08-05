using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MarkSmith.Services;

// Checks GitHub Releases for a newer version and supports silent in-app updates.
public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/thebubbsy/marksmith/releases/latest";
    public const string RepoUrl = "https://github.com/thebubbsy/marksmith";
    public const string ReleasesUrl = RepoUrl + "/releases";

    public string CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public sealed record Result(bool Ok, bool UpdateAvailable, string LatestTag, string ReleaseUrl, string DownloadUrl, string Message);

    public async Task<Result> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Marksmith-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync(LatestReleaseApi);
            return EvaluateReleaseJson(json, CurrentVersion);
        }
        catch (HttpRequestException)
        {
            return new(false, false, "", ReleasesUrl, "",
                "Couldn't reach the releases feed — the repository may be private, or you're offline.");
        }
        catch (Exception ex)
        {
            return new(false, false, "", ReleasesUrl, "", $"Update check failed: {ex.Message}");
        }
    }

    // Parses a GitHub "releases/latest" JSON payload and decides whether an update is available.
    internal static Result EvaluateReleaseJson(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesUrl : ReleasesUrl;

        if (string.IsNullOrWhiteSpace(tag))
            return new(false, false, "", ReleasesUrl, "", "The releases feed returned no tag information.");

        var downloadUrl = "";
        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var assetUrl = asset.TryGetProperty("browser_download_url", out var du) ? du.GetString() ?? "" : "";
                if (name.StartsWith("Marksmith-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.Contains(arch, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = assetUrl;
                        break;
                    }
                    if (string.IsNullOrEmpty(downloadUrl)) downloadUrl = assetUrl;
                }
            }
        }

        var latest = tag.TrimStart('v', 'V');
        if (Compare(latest, currentVersion) > 0)
            return new(true, true, tag, url, downloadUrl, $"Update available — {tag}. You have {currentVersion}.");
        return new(true, false, tag, url, downloadUrl, $"You're up to date (v{currentVersion}).");
    }

    // Downloads the installer asset silently and executes it with zero UI prompts (/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /SP-).
    public async Task<bool> DownloadAndInstallAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl)) return false;

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "MarksmithUpdates");
            Directory.CreateDirectory(tempDir);
            var setupPath = Path.Combine(tempDir, "Marksmith-Setup-Latest.exe");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Marksmith-UpdateInstaller");
                // ConfigureAwait(false) throughout: this method is called from the UI thread's
                // RelayCommand, and the download loop must run on the threadpool — otherwise the
                // UI SynchronizationContext is captured, ReadAsync continuations for buffered data
                // run INLINE on the UI thread, and the synchronous FileStream write (no
                // FileOptions.Asynchronous) blocks it per 8 KB chunk: the app freezes for the whole
                // download and the progress bar (posted to the frozen UI queue) never paints past
                // the first percent. Progress<T> still marshals its callbacks to the UI thread.
                using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var fileStream = new FileStream(setupPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);

                var buffer = new byte[64 * 1024];
                var bytesRead = 0;
                var totalRead = 0L;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                    totalRead += bytesRead;
                    if (totalBytes > 0 && progress != null)
                    {
                        progress.Report((double)totalRead / totalBytes * 100.0);
                    }
                }
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /SP-",
                UseShellExecute = true
            };

            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return proc.ExitCode == 0;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static void RelaunchApplication()
    {
        try
        {
            var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = currentExe,
                    UseShellExecute = true
                });
            }
        }
        catch { }
        Environment.Exit(0);
    }

    // Numeric dotted-version compare; returns >0 if a is newer than b.
    internal static int Compare(string a, string b)
    {
        var (na, pra) = Parse(a);
        var (nb, prb) = Parse(b);
        for (var i = 0; i < 4; i++)
        {
            var c = na[i].CompareTo(nb[i]);
            if (c != 0) return c;
        }
        if (pra == prb) return 0;
        if (pra && !prb) return -1;
        if (!pra && prb) return 1;
        return 0;
    }

    private static (int[] Numbers, bool IsPrerelease) Parse(string v)
    {
        v = v.Trim().TrimStart('v', 'V').Trim();
        var dash = v.IndexOf('-');
        var isPre = dash >= 0;
        var core = isPre ? v[..dash] : v;
        var parts = core.Split('.');
        var r = new int[4];
        for (var i = 0; i < 4 && i < parts.Length; i++) int.TryParse(parts[i], out r[i]);
        return (r, isPre);
    }
}
