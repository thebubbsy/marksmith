using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace MarkSmith.Services.DeltaUpdate;

/// <summary>Result of trying to apply a staged delta update at startup.</summary>
public enum DeltaApplyResult
{
    /// <summary>Nothing was staged — a normal launch.</summary>
    None,
    /// <summary>A staged update was applied in place; launch continues normally.</summary>
    Applied,
    /// <summary>The running exe itself changed: a detached handoff was spawned that will apply the
    /// update and restart the app — the current process should exit immediately.</summary>
    RestartHandoffSpawned
}

/// <summary>Orchestrates the delta-update flow: fetch the release manifest, diff against the local
/// install, download only the changed files into a staging dir, and apply on the next launch.</summary>
public static class DeltaUpdateService
{
    /// <summary>Default feed: the release-dist branch (works with zero repo-settings dependency).
    /// Files resolve as {FeedRoot}/{release}/{arch}/{path}.</summary>
    public const string FeedRoot = "https://raw.githubusercontent.com/thebubbsy/marksmith/release-dist/update";

    /// <summary>GitHub Pages feed — preferred when the repo owner has Pages enabled; tried first,
    /// falls back to FeedRoot. Same URL layout.</summary>
    public const string PagesFeedRoot = "https://thebubbsy.github.io/marksmith/update";

    public static string ArchSuffix =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public static string StagingRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith", "update-staging");

    /// <summary>A single path segment for use in URLs and staging paths: no separators, no '.',
    /// no '..', no drive letter, non-empty. A malicious manifest cannot relocate staging outside
    /// StagingRoot or point file URLs outside the feed.</summary>
    public static bool IsSafeSegment(string s) =>
        !string.IsNullOrWhiteSpace(s) &&
        !s.Contains('/') && !s.Contains('\\') && !s.Contains(':') &&
        s != "." && s != "..";

    /// <summary>Fetches + validates the manifest for a release tag, from Pages first then the raw
    /// branch. Null when neither feed serves a valid manifest.</summary>
    public static async Task<DeltaManifest?> FetchManifestAsync(string tag, CancellationToken ct = default)
    {
        var version = tag.TrimStart('v', 'V');
        var arch = ArchSuffix;
        foreach (var baseUrl in new[] { PagesFeedRoot, FeedRoot })
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Marksmith-DeltaUpdate");
                var url = $"{baseUrl}/{Uri.EscapeDataString(version)}/{arch}/{DeltaManifest.ManifestFileName}";
                var json = await http.GetStringAsync(url, ct);
                var manifest = DeltaManifest.Parse(json);
                if (!IsSafeSegment(manifest.Arch) || !IsSafeSegment(manifest.Release)) continue;
                // SECURITY: only https feeds are acceptable, and the base_url must match the feed
                // we actually pulled the manifest from — a manifest claiming a foreign (or plain
                // http) base could redirect file downloads anywhere.
                if (!manifest.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    manifest.BaseUrl = baseUrl;
                return manifest;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                // Try the next feed; a completely broken feed returns null -> installer fallback.
            }
        }
        return null;
    }

    /// <summary>Downloads only the changed files into the staging dir + writes the apply manifest.
    /// Returns false when the install dir is not writable (routes the caller to the full-installer
    /// fallback) or when any file download/verification fails.</summary>
    public static async Task<bool> DownloadDeltaAsync(DeltaManifest manifest, string installDir, string stagingDir,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // Writable-install-dir gate: a read-only install (e.g. Program Files under a standard user)
        // cannot be patched in place — the caller falls back to the elevated installer path.
        try
        {
            var probe = Path.Combine(installDir, $".delta-write-probe-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probe, "probe", ct);
            File.Delete(probe);
        }
        catch
        {
            return false;
        }

        var local = await HashLocalFilesAsync(installDir, ct);
        var delta = DeltaPlan.ComputeDelta(manifest, local);
        if (delta.ChangedOrAdded.Count == 0)
        {
            // Already current — nothing to stage; report success so the caller skips the installer.
            return true;
        }

        var filesDir = Path.Combine(stagingDir, "files");
        Directory.CreateDirectory(filesDir);

        var apply = new ApplyManifest
        {
            Release = manifest.Release,
            Arch = manifest.Arch,
            BaseUrl = manifest.BaseUrl,
            Changed = delta.ChangedOrAdded.ToList(),
            Removed = delta.Removed.ToList(),
        };
        // Deliberately NOT written yet — see below (written only after every download verified, so
        // a crash mid-download leaves no 'ready' staging dir for the next launch to apply).

        var totalBytes = delta.ChangedOrAdded.Sum(f => Math.Max(1, f.Size));
        var downloaded = 0L;
        var failed = 0;
        using var semaphore = new SemaphoreSlim(4);
        var tasks = delta.ChangedOrAdded.Select(async f =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var url = $"{manifest.BaseUrl}/{Uri.EscapeDataString(manifest.Release)}/{manifest.Arch}/{f.Path.Replace('\\', '/')}";
                var bytes = await DownloadWithRetryAsync(url, f.Sha256, ct);
                var dest = Path.Combine(filesDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await File.WriteAllBytesAsync(dest, bytes, ct);
                var written = Interlocked.Add(ref downloaded, f.Size);
                progress?.Report((double)written / totalBytes * 100.0);
            }
            catch
            {
                Interlocked.Increment(ref failed);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks);

        if (failed > 0)
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
            return false;
        }

        // Only a fully downloaded + hash-verified staging dir gets the apply manifest — the
        // 'ready' signal for the next launch. A crash before this line leaves nothing to apply.
        await File.WriteAllTextAsync(
            Path.Combine(stagingDir, "apply-manifest.json"),
            JsonSerializer.Serialize(apply, DeltaJson.Options), ct);
        return true;
    }

    /// <summary>Applies a previously staged delta update. Called once at startup, before any UI.
    /// See DeltaApplyResult for the three outcomes.</summary>
    public static DeltaApplyResult TryApplyPendingDeltaUpdate(out string? message)
    {
        message = null;
        if (!Directory.Exists(StagingRoot)) return DeltaApplyResult.None;

        var newest = Directory.GetDirectories(StagingRoot)
            .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
            .FirstOrDefault();
        if (newest is null) return DeltaApplyResult.None;

        var applyPath = Path.Combine(newest, "apply-manifest.json");
        if (!File.Exists(applyPath)) return DeltaApplyResult.None;

        ApplyManifest apply;
        try
        {
            apply = JsonSerializer.Deserialize<ApplyManifest>(File.ReadAllText(applyPath), DeltaJson.Options)
                ?? throw new InvalidDataException();
        }
        catch
        {
            return DeltaApplyResult.None; // corrupt staging — never block launch
        }

        // SECURITY: the staging dir is user-writable, so apply-manifest.json must be treated as
        // hostile input. Re-validate EVERY path at apply time — a hand-edited manifest with a
        // "../" entry would otherwise overwrite/delete arbitrary files outside the install dir
        // (compounded by the self-elevating handoff = local privilege escalation). An invalid
        // manifest is simply dropped; the launch continues normally.
        if (!IsApplyManifestSafe(apply)) return DeltaApplyResult.None;

        var installDir = InstallDir();
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return DeltaApplyResult.None;

        var currentExe = Path.GetFileName(Environment.ProcessPath ?? "");
        var exeChanged = apply.Changed.Any(f => string.Equals(Path.GetFileName(f.Path), currentExe, StringComparison.OrdinalIgnoreCase))
                         || apply.Removed.Any(r => string.Equals(Path.GetFileName(r), currentExe, StringComparison.OrdinalIgnoreCase));
        if (exeChanged)
        {
            // The running exe cannot be overwritten: spawn a detached handoff that waits for this
            // process to exit, applies the update, and restarts the app.
            var cmd = UpdateHandoff.BuildCmd(apply, newest, installDir, Environment.ProcessId, currentExe);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cmd,
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                });
                message = $"Update {apply.Release} will be applied on restart.";
                return DeltaApplyResult.RestartHandoffSpawned;
            }
            catch
            {
                return DeltaApplyResult.None; // could not spawn the handoff — launch normally
            }
        }

        try
        {
            ApplyFiles(apply, newest, installDir);
            message = $"Update {apply.Release} applied.";
            return DeltaApplyResult.Applied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A loaded DLL is locked, or the dir became read-only — hand off to the delayed copy.
            var cmd = UpdateHandoff.BuildCmd(apply, newest, installDir, Environment.ProcessId, currentExe);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cmd,
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                });
                message = $"Update {apply.Release} will be applied on restart.";
                return DeltaApplyResult.RestartHandoffSpawned;
            }
            catch
            {
                return DeltaApplyResult.None;
            }
        }
    }

    /// <summary>Pure apply-time security gate: every path in a (user-writable, thus hostile)
    /// apply manifest must be a safe relative path. Nested paths are legitimate; '..', absolute
    /// paths and drive letters are rejected.</summary>
    public static bool IsApplyManifestSafe(ApplyManifest apply) =>
        apply.Changed.All(f => DeltaManifest.IsSafeRelativePath(f.Path)) &&
        apply.Removed.All(DeltaManifest.IsSafeRelativePath);

    private static void ApplyFiles(ApplyManifest apply, string staging, string installDir)
    {
        var filesDir = Path.Combine(staging, "files");
        foreach (var f in apply.Changed)
        {
            var rel = f.Path.Replace('/', Path.DirectorySeparatorChar);
            var src = Path.Combine(filesDir, rel);
            var dst = Path.Combine(installDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
        foreach (var r in apply.Removed)
        {
            var p = Path.Combine(installDir, r.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) File.Delete(p);
        }
        try { Directory.Delete(staging, recursive: true); } catch { }
    }

    /// <summary>Hashes every file under the install dir (parallel, bounded). Keys are normalized
    /// relative paths; the staging dir is excluded (it lives under %LOCALAPPDATA%, not the install).</summary>
    public static async Task<Dictionary<string, string>> HashLocalFilesAsync(string installDir, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(installDir)) return result;

        var files = Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories).ToList();
        using var semaphore = new SemaphoreSlim(4);
        var tasks = files.Select(async f =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                using var stream = File.OpenRead(f);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                var rel = Path.GetRelativePath(installDir, f).Replace('\\', '/');
                lock (result) result[rel] = hash;
            }
            catch
            {
                // Unreadable/locked file — treated as absent (will be re-downloaded or skipped).
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
        return result;
    }

    private static async Task<byte[]> DownloadWithRetryAsync(string url, string expectedSha256, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Marksmith-DeltaUpdate");
        var bytes = await http.GetByteArrayAsync(url, ct);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Hash mismatch for {url}");
        return bytes;
    }

    private static string? InstallDir() =>
        Path.GetDirectoryName(Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location);
}
