using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarkSmith.Services;

/// <summary>A single stored version of a markdown file.</summary>
public sealed record VersionEntry(
    string Id,
    string FilePath,
    string Hash,
    DateTimeOffset CreatedAt,
    string Source,
    int Bytes,
    string? Label = null,
    bool IsStarred = false,
    int LinesAdded = 0,
    int LinesRemoved = 0);

/// <summary>
/// Local version-history database for markdown files. Created LAZILY on the first capture — there
/// is no file on disk until the user actually starts working. Layout:
///   %LOCALAPPDATA%\MarkSmith\history\versions.json   (the index — one list of versions per file)
///   %LOCALAPPDATA%\MarkSmith\history\blobs\<sha256>.md  (content snapshots, deduped by hash)
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
    public async Task<bool> CaptureAsync(string filePath, string content, string source = "auto", string? label = null, bool isStarred = false)
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

            int added = 0, removed = 0;
            if (versions is { Count: > 0 })
            {
                var prevBlob = Path.Combine(_blobDir, versions[0].Hash + ".md");
                if (File.Exists(prevBlob))
                {
                    var prevContent = await File.ReadAllTextAsync(prevBlob);
                    var diffLines = LineDiff.Diff(prevContent, content);
                    added = diffLines.Count(l => l.Kind == LineDiff.Kind.Added);
                    removed = diffLines.Count(l => l.Kind == LineDiff.Kind.Removed);
                }
            }
            else
            {
                added = content.Split('\n').Length;
            }

            versions ??= new List<VersionEntry>();
            versions.Insert(0, new VersionEntry(
                NewId(hash), key, hash, DateTimeOffset.Now, source, content.Length, label, isStarred, added, removed));
            if (versions.Count > _maxVersionsPerFile)
                versions.RemoveRange(_maxVersionsPerFile, versions.Count - _maxVersionsPerFile);

            index[key] = versions;
            await SaveIndexAsync(index);
            await CollectGarbageBlobsAsync(index);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Updates the custom label or bookmark description for a stored version.</summary>
    public async Task<bool> SetLabelAsync(string id, string? label)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        await _gate.WaitAsync();
        try
        {
            var index = await LoadIndexAsync();
            foreach (var kvp in index)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    if (kvp.Value[i].Id == id)
                    {
                        kvp.Value[i] = kvp.Value[i] with { Label = label };
                        await SaveIndexAsync(index);
                        return true;
                    }
                }
            }
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Toggles the starred / pinned bookmark state of a version.</summary>
    public async Task<bool> ToggleStarAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        await _gate.WaitAsync();
        try
        {
            var index = await LoadIndexAsync();
            foreach (var kvp in index)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    if (kvp.Value[i].Id == id)
                    {
                        bool newStar = !kvp.Value[i].IsStarred;
                        kvp.Value[i] = kvp.Value[i] with { IsStarred = newStar };
                        await SaveIndexAsync(index);
                        return newStar;
                    }
                }
            }
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Deletes a single snapshot by ID.</summary>
    public async Task<bool> DeleteVersionAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        await _gate.WaitAsync();
        try
        {
            var index = await LoadIndexAsync();
            foreach (var kvp in index)
            {
                int removed = kvp.Value.RemoveAll(v => v.Id == id);
                if (removed > 0)
                {
                    await SaveIndexAsync(index);
                    await CollectGarbageBlobsAsync(index);
                    return true;
                }
            }
            return false;
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

    public enum DiffExportFormat
    {
        Patch,
        Html,
        Markdown
    }

    /// <summary>Generates a formatted diff report between two historical versions in Patch, HTML, or Markdown.</summary>
    public async Task<string> ExportDiffReportAsync(string filePath, string oldVersionId, string newVersionId, DiffExportFormat format)
    {
        var oldText = await GetContentAsync(oldVersionId) ?? "";
        var newText = await GetContentAsync(newVersionId) ?? "";
        var lines = LineDiff.Diff(oldText, newText);
        var fileName = SafeFileName(filePath);

        if (format == DiffExportFormat.Patch)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"--- a/{fileName}");
            sb.AppendLine($"+++ b/{fileName}");
            var hunks = LineDiff.ExtractHunks(lines);
            if (hunks.Count == 0)
            {
                sb.AppendLine("@@ -1,0 +1,0 @@");
            }
            foreach (var h in hunks)
            {
                sb.AppendLine(h.Header);
                foreach (var l in h.Lines)
                {
                    char prefix = l.Kind switch { LineDiff.Kind.Added => '+', LineDiff.Kind.Removed => '-', _ => ' ' };
                    sb.AppendLine($"{prefix}{l.Text}");
                }
            }
            return sb.ToString();
        }
        else if (format == DiffExportFormat.Markdown)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Diff Report: `{fileName}`");
            sb.AppendLine($"*Compared version `{oldVersionId}` with `{newVersionId}`*");
            sb.AppendLine();
            sb.AppendLine("```diff");
            foreach (var l in lines)
            {
                if (l.Kind == LineDiff.Kind.Added) sb.AppendLine($"+ {l.Text}");
                else if (l.Kind == LineDiff.Kind.Removed) sb.AppendLine($"- {l.Text}");
                else sb.AppendLine($"  {l.Text}");
            }
            sb.AppendLine("```");
            return sb.ToString();
        }
        else // Html
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>Diff Report - " + System.Net.WebUtility.HtmlEncode(fileName) + "</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; background: #0D1117; color: #C9D1D9; margin: 0; padding: 24px; }");
            sb.AppendLine("h2 { margin-top: 0; color: #58A6FF; }");
            sb.AppendLine("table { width: 100%; border-collapse: collapse; font-family: 'Cascadia Mono', Consolas, monospace; font-size: 12px; }");
            sb.AppendLine("td { padding: 2px 8px; vertical-align: top; }");
            sb.AppendLine(".num { width: 40px; color: #6E7681; text-align: right; user-select: none; }");
            sb.AppendLine(".add { background: rgba(46, 160, 67, 0.15); color: #3FB950; }");
            sb.AppendLine(".rem { background: rgba(248, 81, 73, 0.15); color: #F85149; }");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine($"<h2>Diff Report: {System.Net.WebUtility.HtmlEncode(fileName)}</h2>");
            sb.AppendLine($"<p style=\"color:#8B949E; font-size:12px;\">From version <code>{oldVersionId}</code> &rarr; <code>{newVersionId}</code></p>");
            sb.AppendLine("<table>");
            foreach (var l in lines)
            {
                string cls = l.Kind switch { LineDiff.Kind.Added => "add", LineDiff.Kind.Removed => "rem", _ => "" };
                string sign = l.Kind switch { LineDiff.Kind.Added => "+", LineDiff.Kind.Removed => "-", _ => " " };
                sb.AppendLine($"<tr class=\"{cls}\"><td class=\"num\">{l.OldNumber?.ToString() ?? ""}</td><td class=\"num\">{l.NewNumber?.ToString() ?? ""}</td><td style=\"width:15px;\">{sign}</td><td>{System.Net.WebUtility.HtmlEncode(l.Text)}</td></tr>");
            }
            sb.AppendLine("</table></body></html>");
            return sb.ToString();
        }
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
        if (filePath.StartsWith("scratch://", StringComparison.OrdinalIgnoreCase))
            return "Scratch Workspace";
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
        if (string.IsNullOrWhiteSpace(filePath)) return string.Empty;
        var trimmed = filePath.Trim();
        if (trimmed.StartsWith("scratch://", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(trimmed), "workspace-session.md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "workspace-session.md", StringComparison.OrdinalIgnoreCase))
        {
            return "scratch://workspace-session.md";
        }
        try { return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant(); }
        catch { return trimmed.ToLowerInvariant(); }
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
        if (!File.Exists(_indexPath)) return new Dictionary<string, List<VersionEntry>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var stream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var raw = await JsonSerializer.DeserializeAsync<Dictionary<string, List<VersionEntry>>>(stream, ReadOpts);
            var canonical = new Dictionary<string, List<VersionEntry>>(StringComparer.OrdinalIgnoreCase);
            if (raw != null)
            {
                foreach (var kvp in raw)
                {
                    var normKey = Normalize(kvp.Key);
                    if (string.IsNullOrWhiteSpace(normKey)) continue;

                    if (!canonical.TryGetValue(normKey, out var list))
                    {
                        list = new List<VersionEntry>();
                        canonical[normKey] = list;
                    }

                    foreach (var v in kvp.Value)
                    {
                        if (!list.Any(existing => existing.Id == v.Id || (existing.Hash == v.Hash && Math.Abs((existing.CreatedAt - v.CreatedAt).TotalSeconds) < 2)))
                        {
                            list.Add(v with { FilePath = normKey });
                        }
                    }
                }

                foreach (var list in canonical.Values)
                {
                    list.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
                }
            }
            return canonical;
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
    private Task CollectGarbageBlobsAsync(Dictionary<string, List<VersionEntry>> index)
    {
        try
        {
            if (!Directory.Exists(_blobDir)) return Task.CompletedTask;
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
        return Task.CompletedTask;
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
