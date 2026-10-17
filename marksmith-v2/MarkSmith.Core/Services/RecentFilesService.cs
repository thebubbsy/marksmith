using System.Text.Json;

namespace MarkSmith.Services;

public sealed class RecentFilesService
{
    private const int MaxRecentFiles = 10;

    private static readonly string ConfigDir = AppPaths.ConfigDir;

    private static readonly string RecentFilesPath = Path.Combine(ConfigDir, "recent_files.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public List<string> Load()
    {
        try
        {
            if (File.Exists(RecentFilesPath))
            {
                var json = File.ReadAllText(RecentFilesPath);
                var files = JsonSerializer.Deserialize<List<string>>(json, JsonOpts);
                if (files is not null) return files.Take(MaxRecentFiles).ToList();
            }
        }
        catch
        {
            // Corrupt recent-files list — start fresh instead of surfacing an error for a non-critical feature.
        }
        return new List<string>();
    }

    public List<string> AddToRecent(string filePath)
    {
        var files = Load();
        var full = Path.GetFullPath(filePath);
        files.RemoveAll(f => string.Equals(f, full, PathEquality.Comparison));
        files.Insert(0, full);
        files = files.Take(MaxRecentFiles).ToList();

        // Persisting the recent list must never break the file-open flow it's part of — a
        // read-only/full/locked config dir should degrade to "recents not saved", not throw out of
        // the caller's open-document path. (Load is already guarded the same way.)
        try { AtomicFile.WriteAllText(RecentFilesPath, JsonSerializer.Serialize(files, JsonOpts)); }
        catch { /* non-critical feature; keep the in-memory list */ }
        return files;
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(RecentFilesPath))
            {
                AtomicFile.WriteAllText(RecentFilesPath, "[]");
            }
        }
        catch { /* non-critical feature */ }
    }

    public static IEnumerable<string> FilterRecentFiles(IEnumerable<string>? files, string? filter)
    {
        if (files is null) return Enumerable.Empty<string>();
        if (string.IsNullOrWhiteSpace(filter)) return files;

        var term = filter.Trim();
        return files.Where(f =>
            (Path.GetFileName(f)?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
            f.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Formats the recent documents count indicator (e.g. "3 docs", "2/5 docs" when filtered, or "0 docs").
    /// </summary>
    public static string FormatRecentFilesCount(int displayedCount, int totalCount, string? filter = null)
    {
        if (totalCount <= 0) return "0 docs";
        if (!string.IsNullOrWhiteSpace(filter) && displayedCount != totalCount)
        {
            return $"{displayedCount}/{totalCount} docs";
        }
        return $"{totalCount} {(totalCount == 1 ? "doc" : "docs")}";
    }

    /// <summary>
    /// Extracts file names only (without directory path) from a sequence of full file paths.
    /// </summary>
    public static List<string> ExtractFileNames(IEnumerable<string>? files)
    {
        if (files is null) return new List<string>();
        return files.Select(f => Path.GetFileName(f)).Where(name => !string.IsNullOrEmpty(name)).ToList();
    }

    /// <summary>
    /// Extracts distinct parent directory paths from a sequence of file paths,
    /// preserving original appearance order and ignoring invalid or empty paths.
    /// </summary>
    public static List<string> ExtractParentDirectories(IEnumerable<string>? files)
    {
        if (files is null) return new List<string>();
        var dirs = new List<string>();
        var seen = new HashSet<string>(PathEquality.Comparer);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            try
            {
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir) && seen.Add(dir))
                {
                    dirs.Add(dir);
                }
            }
            catch
            {
                // Ignore malformed paths
            }
        }
        return dirs;
    }

    /// <summary>
    /// Formats a sequence of file paths into GitHub-Flavored Markdown bullet links (- [FileName](FilePath)).
    /// </summary>
    public static string FormatAsMarkdownList(IEnumerable<string>? files)
    {
        if (files is null) return string.Empty;
        var lines = new List<string>();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            var name = Path.GetFileName(file);
            if (string.IsNullOrEmpty(name)) name = file;
            lines.Add($"- [{name}]({file})");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Formats a high-level summary of the recent documents library (e.g. "5 documents across 3 directories (.md, .txt)").
    /// </summary>
    public static string FormatRecentLibrarySummary(IEnumerable<string>? files)
    {
        if (files is null) return "0 documents in recent history";
        var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (list.Count == 0) return "0 documents in recent history";

        var dirs = ExtractParentDirectories(list);
        var exts = list.Select(f =>
        {
            try { return Path.GetExtension(f)?.ToLowerInvariant() ?? ""; }
            catch { return ""; }
        })
        .Where(e => !string.IsNullOrEmpty(e))
        .Distinct()
        .ToList();

        var extLabel = exts.Count > 0 ? $" ({string.Join(", ", exts)})" : "";
        var dirLabel = dirs.Count == 1 ? "1 directory" : $"{dirs.Count} directories";
        var docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        return $"{docLabel} across {dirLabel}{extLabel}";
    }

    /// <summary>
    /// Formats a summary of document counts grouped by file extension (e.g. "4 documents: 3 .md, 1 .txt").
    /// </summary>
    public static string FormatRecentExtensionBreakdown(IEnumerable<string>? files)
    {
        if (files is null) return "0 documents in recent history";
        var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (list.Count == 0) return "0 documents in recent history";

        var groups = list
            .Select(f =>
            {
                try
                {
                    var ext = Path.GetExtension(f);
                    return string.IsNullOrEmpty(ext) ? "(no extension)" : ext.ToLowerInvariant();
                }
                catch { return "(no extension)"; }
            })
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();

        var docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        return $"{docLabel}: {string.Join(", ", groups)}";
    }

    /// <summary>
    /// Analyzes recent document file paths and formats a storage drive / root volume distribution summary (e.g. "4 documents across 2 storage roots: C: (3), D: (1)").
    /// </summary>
    public static string FormatRecentDriveDistribution(IEnumerable<string>? files)
    {
        if (files is null) return "0 documents in recent history";
        var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (list.Count == 0) return "0 documents in recent history";

        var driveGroups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in list)
        {
            string root = string.Empty;
            try
            {
                var pathRoot = Path.GetPathRoot(file);
                if (!string.IsNullOrEmpty(pathRoot))
                {
                    root = pathRoot.TrimEnd('\\', '/').ToUpperInvariant();
                }
            }
            catch
            {
                // Fallback for non-standard paths
            }

            if (string.IsNullOrEmpty(root))
            {
                root = "Relative";
            }

            driveGroups[root] = driveGroups.TryGetValue(root, out var count) ? count + 1 : 1;
        }

        var ordered = driveGroups.OrderByDescending(g => g.Value).ThenBy(g => g.Key).ToList();
        var breakdown = string.Join(", ", ordered.Select(g => $"{g.Key} ({g.Value})"));

        if (ordered.Count == 1)
        {
            var singleDocLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
            return $"All {singleDocLabel} on {ordered[0].Key}";
        }

        var docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        return $"{docLabel} across {ordered.Count} storage roots: {breakdown}";
    }

    /// <summary>
    /// Analyzes the last modified timestamp of recent files and formats a recency/age distribution summary
    /// (e.g. "4 documents: 2 today, 1 this week, 1 older").
    /// </summary>
    public static string FormatRecentAgeDistribution(
        IEnumerable<string>? files,
        DateTime? referenceTime = null,
        Func<string, DateTime?>? lastWriteTimeProvider = null)
    {
        if (files is null) return "0 documents in recent history";
        var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (list.Count == 0) return "0 documents in recent history";

        var now = referenceTime ?? DateTime.Now;
        var todayStart = now.Date;
        var weekStart = todayStart.AddDays(-7);

        int todayCount = 0;
        int weekCount = 0;
        int olderCount = 0;
        int unverifiedCount = 0;

        foreach (var file in list)
        {
            DateTime? mtime = null;
            if (lastWriteTimeProvider != null)
            {
                mtime = lastWriteTimeProvider(file);
            }
            else
            {
                try
                {
                    if (File.Exists(file))
                    {
                        mtime = File.GetLastWriteTime(file);
                    }
                }
                catch
                {
                    // Inaccessible or invalid path format
                }
            }

            if (!mtime.HasValue)
            {
                unverifiedCount++;
            }
            else if (mtime.Value >= todayStart)
            {
                todayCount++;
            }
            else if (mtime.Value >= weekStart)
            {
                weekCount++;
            }
            else
            {
                olderCount++;
            }
        }

        var parts = new List<string>();
        if (todayCount > 0) parts.Add($"{todayCount} today");
        if (weekCount > 0) parts.Add($"{weekCount} this week");
        if (olderCount > 0) parts.Add($"{olderCount} older");
        if (unverifiedCount > 0) parts.Add($"{unverifiedCount} unverified");

        var docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        if (parts.Count == 0) return $"{docLabel}: status unknown";
        return $"{docLabel}: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Audits the character lengths of recent file paths and formats an audit distribution summary,
    /// flagging any paths exceeding the standard Windows MAX_PATH threshold.
    /// </summary>
    public static string FormatRecentPathLengthDistribution(IEnumerable<string>? files, int warnThreshold = 260)
    {
        if (files is null) return "0 documents in recent history";
        var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
        if (list.Count == 0) return "0 documents in recent history";

        int minLen = list.Min(f => f.Length);
        int maxLen = list.Max(f => f.Length);
        double avgLen = list.Average(f => f.Length);
        int exceedingCount = list.Count(f => f.Length >= warnThreshold);

        var docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        string warningStr = exceedingCount > 0
            ? $" · ⚠ {exceedingCount} path{(exceedingCount == 1 ? "" : "s")} >= {warnThreshold} chars"
            : $" · 0 paths >= {warnThreshold} chars (All safe)";

        return $"Path lengths ({docLabel}): Min {minLen} chars, Max {maxLen} chars, Avg {avgLen:0.#} chars{warningStr}";
    }
}
