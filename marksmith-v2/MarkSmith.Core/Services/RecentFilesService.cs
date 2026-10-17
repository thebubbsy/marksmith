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

    /// <summary>
    /// Audits the file sizes and total disk footprint of recent documents.
    /// Formats an audit summary detailing total footprint, min, max, and average file size.
    /// Supports an optional custom fileSizeProvider for deterministic testing.
    /// </summary>
    public static string FormatRecentSizeDistribution(
        IEnumerable<string>? files,
        Func<string, long?>? fileSizeProvider = null)
    {
        if (files is null) return "0 documents in recent history";
        var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
        if (list.Count == 0) return "0 documents in recent history";

        var sizes = new List<long>();
        int unverifiedCount = 0;

        foreach (var file in list)
        {
            long? size = null;
            if (fileSizeProvider != null)
            {
                size = fileSizeProvider(file);
            }
            else
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists)
                    {
                        size = fi.Length;
                    }
                }
                catch
                {
                    // Inaccessible or invalid path
                }
            }

            if (size.HasValue)
            {
                sizes.Add(size.Value);
            }
            else
            {
                unverifiedCount++;
            }
        }

        var docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        if (sizes.Count == 0)
        {
            return $"Disk footprint ({docLabel}): 0 bytes verified ({unverifiedCount} unverified)";
        }

        long totalBytes = sizes.Sum();
        long minBytes = sizes.Min();
        long maxBytes = sizes.Max();
        double avgBytes = sizes.Average();

        string totalStr = FormatBytes(totalBytes);
        string minStr = FormatBytes(minBytes);
        string maxStr = FormatBytes(maxBytes);
        string avgStr = FormatBytes((long)Math.Round(avgBytes));

        string unverifiedStr = unverifiedCount > 0 ? $" · {unverifiedCount} unverified" : "";

        return $"Disk footprint ({docLabel}): Total {totalStr} · Min {minStr}, Max {maxStr}, Avg {avgStr}{unverifiedStr}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{(bytes / (1024.0 * 1024)):0.#} MB";
        return $"{(bytes / (1024.0 * 1024 * 1024)):0.#} GB";
    }

    /// <summary>
    /// Audits recent document folder spread, subfolder depth, and directory dispersion.
    /// Returns unique directory count, min/max/average depth, and formatted summary.
    /// </summary>
    public static string FormatRecentDirectorySpread(IEnumerable<string>? files)
    {
        if (files is null) return "0 documents in recent history";
        var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
        if (list.Count == 0) return "0 documents in recent history";

        var dirGroups = list
            .Select(f =>
            {
                try
                {
                    var dir = Path.GetDirectoryName(f);
                    return string.IsNullOrEmpty(dir) ? "(root / relative)" : dir;
                }
                catch
                {
                    return "(unknown)";
                }
            })
            .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();

        var depths = list.Select(f =>
        {
            try
            {
                var dir = Path.GetDirectoryName(f) ?? "";
                var parts = dir.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length;
            }
            catch
            {
                return 0;
            }
        }).ToList();

        int totalDirs = dirGroups.Count;
        int minDepth = depths.Count > 0 ? depths.Min() : 0;
        int maxDepth = depths.Count > 0 ? depths.Max() : 0;
        double avgDepth = depths.Count > 0 ? depths.Average() : 0.0;

        var topGroup = dirGroups[0];
        string topFolderDisplay;
        try
        {
            topFolderDisplay = Path.GetFileName(topGroup.Key);
            if (string.IsNullOrEmpty(topFolderDisplay)) topFolderDisplay = topGroup.Key;
        }
        catch
        {
            topFolderDisplay = topGroup.Key;
        }

        string docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        string folderLabel = totalDirs == 1 ? "1 folder" : $"{totalDirs} folders";

        string spreadClassification = totalDirs switch
        {
            1 => "Centralized",
            <= 3 => "Clustered",
            _ => "Scattered"
        };

        double topPct = (double)topGroup.Count() / list.Count * 100.0;
        string topSummary = $"Primary: '{topFolderDisplay}' ({topGroup.Count()}/{list.Count}, {topPct:0.#}%)";

        return $"Directory spread ({docLabel} across {folderLabel} · {spreadClassification}): Depth: Min {minDepth}, Max {maxDepth}, Avg {avgDepth:0.#} · {topSummary}";
    }

    /// <summary>
    /// Audits file naming conventions, casing patterns, and file extensions across recent document paths.
    /// </summary>
    public static string FormatRecentFileNamingPatterns(IEnumerable<string>? filePaths)
    {
        if (filePaths == null) return "Naming patterns: 0 documents (No recent files)";
        var list = filePaths.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (list.Count == 0) return "Naming patterns: 0 documents (No recent files)";

        int datePrefixed = 0;
        int kebabCase = 0;
        int snakeCase = 0;
        int pascalOrCamel = 0;
        int spaced = 0;
        int other = 0;

        var extCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dateRx = new System.Text.RegularExpressions.Regex(@"^\d{4}[-_]\d{2}[-_]\d{2}", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));

        foreach (var path in list)
        {
            string nameWithoutExt;
            string ext;
            try
            {
                nameWithoutExt = Path.GetFileNameWithoutExtension(path) ?? "";
                ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            }
            catch
            {
                nameWithoutExt = path;
                ext = "";
            }

            if (string.IsNullOrEmpty(ext)) ext = "(none)";
            extCounts[ext] = extCounts.TryGetValue(ext, out int ec) ? ec + 1 : 1;

            if (string.IsNullOrEmpty(nameWithoutExt))
            {
                other++;
                continue;
            }

            try
            {
                if (dateRx.IsMatch(nameWithoutExt))
                {
                    datePrefixed++;
                }
                else if (nameWithoutExt.Contains(' '))
                {
                    spaced++;
                }
                else if (nameWithoutExt.Contains('-') && !nameWithoutExt.Contains('_') && nameWithoutExt.ToLowerInvariant() == nameWithoutExt)
                {
                    kebabCase++;
                }
                else if (nameWithoutExt.Contains('_') && !nameWithoutExt.Contains('-') && nameWithoutExt.ToLowerInvariant() == nameWithoutExt)
                {
                    snakeCase++;
                }
                else if (!nameWithoutExt.Contains('-') && !nameWithoutExt.Contains('_') && nameWithoutExt.Any(char.IsUpper) && nameWithoutExt.Any(char.IsLower))
                {
                    pascalOrCamel++;
                }
                else
                {
                    other++;
                }
            }
            catch
            {
                other++;
            }
        }

        var patterns = new List<(string Name, int Tally)>
        {
            ("kebab-case", kebabCase),
            ("date-prefixed", datePrefixed),
            ("snake_case", snakeCase),
            ("Pascal/camelCase", pascalOrCamel),
            ("spaced title", spaced),
            ("other", other)
        }.Where(p => p.Tally > 0).OrderByDescending(p => p.Tally).ToList();

        (string Name, int Tally) topPattern = patterns.Count > 0 ? patterns[0] : ("unspecified", 0);
        double topPct = (double)topPattern.Tally / list.Count * 100.0;

        var patternParts = patterns.Select(p => $"{p.Tally} {p.Name}");
        string patternSummary = string.Join(", ", patternParts);

        var topExts = extCounts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value} {kv.Key}");
        string extSummary = string.Join(", ", topExts);

        string docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        return $"Naming patterns ({docLabel} · Dominant: {topPattern.Name} {topPct:0.#}%): {patternSummary} · Extensions: {extSummary}";
    }
}
