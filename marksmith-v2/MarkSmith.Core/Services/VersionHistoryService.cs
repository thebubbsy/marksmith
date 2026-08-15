using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarkSmith.Services;

/// <summary>A single stored version of a markdown file.</summary>
public sealed record VersionEntry(
    string Id, string FilePath, string Hash, DateTimeOffset CreatedAt, string Source, int Bytes);

/// <summary>
/// Local version-history database for markdown files. Created LAZILY on the first capture — there
/// is no file on disk until the user actually starts working. Layout:
///   %LOCALAPPDATA%\MarkSmith\history\versions.json   (the index — one list of versions per file)
///   %LOCALAPPDATA%\MarkSmith\history\blobs\&lt;sha256&gt;.md  (content snapshots, deduped by hash)
/// Zero external dependencies: plain JSON index + content-addressed blobs. All mutations run under
/// a single gate so concurrent captures (open + export racing) can never corrupt the index.
/// </summary>
public sealed class VersionHistoryService
{
    public const int DefaultMaxVersionsPerFile = 100;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _dbDir;
    private readonly string _indexPath;
    private readonly string _blobDir;
    private readonly int _maxVersionsPerFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VersionHistoryService(string? dbDir = null, int maxVersionsPerFile = DefaultMaxVersionsPerFile)
    {
        _dbDir = dbDir ?? Path.Combine(AppPaths.ConfigDir, "history");
        _indexPath = Path.Combine(_dbDir, "versions.json");
        _blobDir = Path.Combine(_dbDir, "blobs");
        _maxVersionsPerFile = maxVersionsPerFile;
    }

    /// <summary>The database exists on disk yet (false until the first capture creates it).</summary>
    public bool Exists => File.Exists(_indexPath);

    public string DatabasePath => _indexPath;

    /// <summary>Captures a new version of a file's content. Returns false when nothing changed
    /// (identical to the latest version) or the path is blank — no version row, no disk write.</summary>
    public async Task<bool> CaptureAsync(string filePath, string content, string source = "auto")
    {
        if (string.IsNullOrWhiteSpace(filePath) || content is null) return false;
        var key = Normalize(filePath);
        var hash = ComputeSha256(content);

        await _gate.WaitAsync();
        try
        {
            var index = await LoadIndexAsync();
            index.TryGetValue(key, out var versions);
            if (versions is { Count: > 0 } && versions[0].Hash == hash) return false;

            Directory.CreateDirectory(_blobDir);
            var blobPath = Path.Combine(_blobDir, hash + ".md");
            if (!File.Exists(blobPath))
            {
                // Write to a temp name first so a crash mid-write can never leave a truncated blob
                // that the File.Exists dedup would then treat as the real snapshot forever.
                var tmpBlob = blobPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                await File.WriteAllTextAsync(tmpBlob, content);
                File.Move(tmpBlob, blobPath, overwrite: false);
            }

            versions ??= new List<VersionEntry>();
            versions.Insert(0, new VersionEntry(
                NewId(hash), key, hash, DateTimeOffset.Now, source, content.Length));
            if (versions.Count > _maxVersionsPerFile)
                versions.RemoveRange(_maxVersionsPerFile, versions.Count - _maxVersionsPerFile);

            index[key] = versions;
            await SaveIndexAsync(index);
            await CollectGarbageBlobsAsync(index);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>All versions for a file, newest first (empty list when the file has no history).</summary>
    public async Task<List<VersionEntry>> GetVersionsAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new List<VersionEntry>();
        var key = Normalize(filePath);
        await _gate.WaitAsync();
        try
        {
            var index = await LoadIndexAsync();
            return index.TryGetValue(key, out var versions)
                ? versions.Select(v => v).ToList()
                : new List<VersionEntry>();
        }
        finally { _gate.Release(); }
    }

    /// <summary>The stored content of a specific version (by id), or null if unknown.</summary>
    public async Task<string?> GetContentAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        await _gate.WaitAsync();
        try
        {
            var index = await LoadIndexAsync();
            foreach (var versions in index.Values)
            {
                var entry = versions.FirstOrDefault(v => v.Id == id);
                if (entry is null) continue;
                var blobPath = Path.Combine(_blobDir, entry.Hash + ".md");
                return File.Exists(blobPath) ? await File.ReadAllTextAsync(blobPath) : null;
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>One file in the global edit history (every file ever touched).</summary>
    public sealed record FileHistorySummary(
        string FilePath, string FileName, int VersionCount, DateTimeOffset LastModified, string LatestSource);

    /// <summary>Every file that has ever been captured, newest last-modified first — the 'all my
    /// edits' hub view. Empty when the database has not been created yet.</summary>
    public async Task<List<FileHistorySummary>> GetOverviewAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var index = await LoadIndexAsync();
            var summaries = index
                .Where(kv => kv.Value is { Count: > 0 })
                .Select(kv => new FileHistorySummary(
                    kv.Key,
                    SafeFileName(kv.Key),
                    kv.Value.Count,
                    kv.Value[0].CreatedAt,
                    kv.Value[0].Source))
                .OrderByDescending(s => s.LastModified)
                .ToList();
            return summaries;
        }
        finally { _gate.Release(); }
    }

    private static string SafeFileName(string filePath)
    {
        try { return Path.GetFileName(filePath); }
        catch { return filePath; }
    }

    /// <summary>Deletes every stored version for a file. Returns how many were removed.</summary>
    public async Task<int> PurgeAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return 0;
        var key = Normalize(filePath);
        await _gate.WaitAsync();
        try
        {
            var index = await LoadIndexAsync();
            if (!index.Remove(key, out var removed)) return 0;
            await SaveIndexAsync(index);
            await CollectGarbageBlobsAsync(index);
            return removed.Count;
        }
        finally { _gate.Release(); }
    }

    // ---- internals ----

    private static string Normalize(string filePath)
    {
        try { return Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant(); }
        catch { return filePath.ToLowerInvariant(); }
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NewId(string hash) =>
        "v" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + hash[..8];

    private async Task<Dictionary<string, List<VersionEntry>>> LoadIndexAsync()
    {
        if (!File.Exists(_indexPath)) return new Dictionary<string, List<VersionEntry>>();
        try
        {
            await using var stream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var index = await JsonSerializer.DeserializeAsync<Dictionary<string, List<VersionEntry>>>(stream, ReadOpts);
            return index ?? new Dictionary<string, List<VersionEntry>>();
        }
        catch (Exception ex)
        {
            // NEVER return an empty index on a read failure: CaptureAsync would then overwrite the
            // whole database with a single new version (a transient AV-scan lock or a torn read
            // would silently destroy every version). Back the file up and throw so the caller's
            // best-effort wrapper aborts the capture instead.
            try
            {
                if (File.Exists(_indexPath))
                    File.Move(_indexPath, _indexPath + ".corrupt-" + Guid.NewGuid().ToString("N")[..8]);
            }
            catch { /* best-effort */ }
            throw new IOException("Version history index is unreadable — backed it up and refused to overwrite it.", ex);
        }
    }

    /// <summary>Deletes blob files no longer referenced by any index row (pruned versions leave
    /// orphaned snapshots behind — this keeps the store bounded).</summary>
    private async Task CollectGarbageBlobsAsync(Dictionary<string, List<VersionEntry>> index)
    {
        try
        {
            if (!Directory.Exists(_blobDir)) return;
            var referenced = index.Values
                .SelectMany(v => v)
                .Select(v => v.Hash)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_blobDir, "*.md"))
            {
                var hash = Path.GetFileNameWithoutExtension(file);
                if (!referenced.Contains(hash))
                {
                    try { File.Delete(file); } catch { /* best-effort */ }
                }
            }
        }
        catch { /* garbage collection is best-effort */ }
    }

    private async Task SaveIndexAsync(Dictionary<string, List<VersionEntry>> index)
    {
        Directory.CreateDirectory(_dbDir);
        // Unique temp name per process so two app instances sharing the store can't race on the
        // same .tmp file; the final move is an atomic replace on NTFS.
        var tmp = _indexPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(index, JsonOpts));
        File.Move(tmp, _indexPath, overwrite: true);
    }
}
