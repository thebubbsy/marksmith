using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using MarkSmith.Services.DeltaUpdate;

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
            // The app's real version lives on the EXE: MarkSmith.Desktop stamps Version/FileVersion
            // as <base>-dev.<auto-incrementing build> for local builds and clean SemVer for shipped
            // releases. Core.dll used to carry its OWN hardcoded 2.0.0.x (a stale product-line
            // version), and reading THIS assembly's file version made the updater + About report
            // 2.0.0.x — which is why the update banner fired on every launch ("a newer version is
            // available") even for the newest dev builds.
            // Read the ENTRY assembly (the app) first; fall back to this assembly only when the
            // entry isn't available (e.g. library-only hosts).
            foreach (var asm in new[] { System.Reflection.Assembly.GetEntryAssembly(), typeof(UpdateService).Assembly })
            {
                if (asm is null) continue;
                try
                {
                    var loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc))
                    {
                        var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(loc).FileVersion;
                        if (!string.IsNullOrWhiteSpace(fv) && fv != "0.0.0.0") return fv;
                    }
                }
                catch { }
                var v = asm.GetName().Version;
                if (v is not null && v != new Version(0, 0, 0, 0)) return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            return "1.0.0";
        }
    }

    /// <summary>Full SemVer stamp for display (About/Settings): carries the "-dev.<utc>"
    /// prerelease suffix on local builds (e.g. 2.18.0-dev.8161030) and clean SemVer on shipped
    /// releases (2.17.0). Falls back to <see cref="CurrentVersion"/> when unstampable.</summary>
    public string CurrentDisplayVersion
    {
        get
        {
            foreach (var asm in new[] { System.Reflection.Assembly.GetEntryAssembly(), typeof(UpdateService).Assembly })
            {
                if (asm is null) continue;
                var iv = InformationalVersionOf(asm);
                if (!string.IsNullOrWhiteSpace(iv) && iv != "0.0.0") return iv;
            }
            return CurrentVersion;
        }
    }

    /// <summary>True when the running binary carries a SemVer prerelease suffix — i.e. it is a
    /// locally-built dev copy (2.18.0-dev.<utc>), not a shipped release. release.yml stamps clean
    /// SemVer via -p:Version=<tag>, so no released build ever matches. Dev builds short-circuit
    /// the update check: their version is ahead of (or between) stable tags BY DESIGN, so
    /// comparing them against the releases feed can only produce false "update available" noise.
    /// See docs/VERSIONING.md.</summary>
    public static bool IsDevelopmentBuild
    {
        get
        {
            foreach (var asm in new[] { System.Reflection.Assembly.GetEntryAssembly(), typeof(UpdateService).Assembly })
            {
                if (asm is null) continue;
                var iv = InformationalVersionOf(asm);
                if (!string.IsNullOrWhiteSpace(iv)) return IsPrerelease(iv);
            }
            return false;
        }
    }

    private static string? InformationalVersionOf(System.Reflection.Assembly asm)
    {
        try
        {
            var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)
                System.Reflection.AssemblyInformationalVersionAttribute.GetCustomAttribute(
                    asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            var iv = attr?.InformationalVersion ?? "";
            // The SDK appends "+<commit>" build metadata in some CI setups — it is not a version.
            var plus = iv.IndexOf('+');
            return plus >= 0 ? iv[..plus] : iv;
        }
        catch { return null; }
    }

    /// <summary>SemVer prerelease test: a '-' AFTER the numeric core (and not part of '+' build
    /// metadata, which is stripped by the caller) marks a prerelease. "2.18.0-dev.8161030" → true;
    /// "2.17.0" / "2.17.0.0" → false.</summary>
    internal static bool IsPrerelease(string version)
    {
        var v = version.Trim().TrimStart('v', 'V').Trim();
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus]; // build metadata never marks a prerelease
        return v.Contains('-');
    }

    public sealed record Result(bool Ok, bool UpdateAvailable, string LatestTag, string ReleaseUrl, string DownloadUrl, string Message);

    public async Task<Result> CheckAsync()
    {
        // Dev builds (prerelease-stamped, e.g. 2.18.0-dev.8161030) are by definition ahead of —
        // or between — stable releases, so the releases feed can never offer them anything. Skip
        // the network call entirely instead of letting Compare() misfire against the latest tag.
        if (IsDevelopmentBuild)
            return new(true, false, "", ReleasesUrl, "",
                $"Development build ({CurrentDisplayVersion}) — update checks are disabled for non-release builds.");

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

                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    var bytesRead = 0;
                    var totalRead = 0L;

                    while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        totalRead += bytesRead;
                        if (totalBytes > 0 && progress != null)
                        {
                            progress.Report((double)totalRead / totalBytes * 100.0);
                        }
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
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

    // ---- Delta updates (see DeltaUpdate/): download only the files that changed. ----

    /// <summary>Downloads ONLY the files that changed since the installed version (delta feed:
    /// file-manifest.json + per-file URLs on the release-dist branch, Pages when available),
    /// stages them under %LOCALAPPDATA%\MarkSmith\update-staging, and applies on the next launch.
    /// Returns false when the delta feed is unavailable OR the install dir is not writable — the
    /// caller then falls back to the full-installer download.</summary>
    public async Task<bool> DownloadDeltaUpdateAsync(string tag, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var manifest = await DeltaUpdateService.FetchManifestAsync(tag, cancellationToken);
            if (manifest is null) return false;
            var installDir = Path.GetDirectoryName(Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location) ?? "";
            if (string.IsNullOrWhiteSpace(installDir)) return false;
            var staging = Path.Combine(DeltaUpdateService.StagingRoot, manifest.Arch, manifest.Release);
            return await DeltaUpdateService.DownloadDeltaAsync(manifest, installDir, staging, progress, cancellationToken);
        }
        catch
        {
            return false; // any delta failure falls back to the installer path
        }
    }

    /// <summary>Applies a previously staged delta update; call once at startup before any UI.
    /// Returns Applied when files were copied in place (launch continues), RestartHandoffSpawned
    /// when a detached handoff will finish the job and the app should exit, or None when nothing
    /// was staged.</summary>
    public static DeltaApplyResult TryApplyPendingDeltaUpdate(out string? message) =>
        DeltaUpdateService.TryApplyPendingDeltaUpdate(out message);

    // Numeric dotted-version compare; returns >0 if a is newer than b. Parts are compared as longs
    // because the build revision is a UTC timestamp (e.g. 2.14.0.202608051200) that overflows int.
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

    private static (long[] Numbers, bool IsPrerelease) Parse(string v)
    {
        v = v.Trim().TrimStart('v', 'V').Trim();
        var dash = v.IndexOf('-');
        var isPre = dash >= 0;
        var core = isPre ? v[..dash] : v;
        var parts = core.Split('.');
        var r = new long[4];
        for (var i = 0; i < 4 && i < parts.Length; i++) long.TryParse(parts[i], out r[i]);
        return (r, isPre);
    }
}
