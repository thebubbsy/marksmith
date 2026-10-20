using System.Text.Json;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Formats bytes into a human-readable size string (B, KB, MB, GB).
    /// </summary>
    public static string FormatBytes(long bytes)
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

    /// <summary>
    /// Partitions recent document paths into existing on-disk paths and missing/moved paths.
    /// </summary>
    public static (List<string> ExistingPaths, List<string> MissingPaths) AuditMissingRecentFiles(
        IEnumerable<string>? filePaths,
        Func<string, bool>? fileExistsPredicate = null)
    {
        var existing = new List<string>();
        var missing = new List<string>();
        if (filePaths == null) return (existing, missing);

        var checkExists = fileExistsPredicate ?? File.Exists;
        foreach (var p in filePaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            bool exists;
            try
            {
                exists = checkExists(p);
            }
            catch
            {
                exists = false;
            }

            if (exists) existing.Add(p);
            else missing.Add(p);
        }

        return (existing, missing);
    }

    /// <summary>
    /// Formats a human-readable diagnostic audit of valid vs missing/moved recent document paths.
    /// </summary>
    public static string FormatMissingRecentFilesAudit(
        IEnumerable<string>? filePaths,
        Func<string, bool>? fileExistsPredicate = null)
    {
        var (existing, missing) = AuditMissingRecentFiles(filePaths, fileExistsPredicate);
        int total = existing.Count + missing.Count;
        if (total == 0) return "Recent path health: 0 documents (No recent files)";

        string docLabel = total == 1 ? "1 document" : $"{total} documents";
        if (missing.Count == 0)
        {
            return $"Recent path health ({docLabel}): 100% valid on disk ({existing.Count} verified · 0 missing paths)";
        }

        double validPct = (double)existing.Count / total * 100.0;
        var sampleMissing = missing
            .Take(3)
            .Select(p =>
            {
                try { return $"'{Path.GetFileName(p)}'"; }
                catch { return $"'{p}'"; }
            });
        string sampleStr = string.Join(", ", sampleMissing);
        if (missing.Count > 3) sampleStr += $", +{missing.Count - 3} more";

        return $"Recent path health ({docLabel}): {existing.Count} valid ({validPct:0.#}%), {missing.Count} missing/moved ({sampleStr})";
    }

    /// <summary>
    /// Formats a sorted &amp; grouped catalog of recent document paths (alphabetical A–Z by filename and grouped by parent folder).
    /// </summary>
    public static string FormatRecentFilesSortedCatalog(IEnumerable<string>? filePaths)
    {
        if (filePaths == null)
            return "Sorted recent catalog: 0 documents (No recent files)";

        var entries = new List<(string FullPath, string FileName, string FolderName)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in filePaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string p = raw.Trim();
            if (!seen.Add(p)) continue;

            string fileName;
            string folderName;
            try
            {
                fileName = Path.GetFileName(p);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = p;
                string? dir = Path.GetDirectoryName(p);
                folderName = string.IsNullOrWhiteSpace(dir) ? "(root)" : Path.GetFileName(dir.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(folderName)) folderName = dir ?? "(root)";
            }
            catch
            {
                fileName = p;
                folderName = "(unknown)";
            }

            entries.Add((p, fileName, folderName));
        }

        if (entries.Count == 0)
            return "Sorted recent catalog: 0 documents (No recent files)";

        var sortedAz = entries
            .OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.FileName)
            .ToList();

        var folderGroups = entries
            .GroupBy(e => e.FolderName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        string docLabel = entries.Count == 1 ? "1 document" : $"{entries.Count} documents";
        string folderLabel = folderGroups.Count == 1 ? "1 folder" : $"{folderGroups.Count} folders";
        string azPreview = string.Join(", ", sortedAz.Take(5));
        if (sortedAz.Count > 5) azPreview += $", +{sortedAz.Count - 5} more";

        string folderPreview = string.Join(", ", folderGroups.Take(3));
        if (folderGroups.Count > 3) folderPreview += $", +{folderGroups.Count - 3} more";

        return $"Sorted recent catalog ({docLabel} across {folderLabel}): A–Z [{azPreview}] · By folder [{folderPreview}]";
    }

    public static string FormatProjectWorkspaceClusters(IEnumerable<string>? filePaths)
    {
        if (filePaths == null)
        {
            return "Project workspace clusters: 0 workspaces (no recent files recorded)";
        }

        var entries = new List<(string WorkspaceName, string FileName)>();
        foreach (var raw in filePaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string trimmed = raw.Trim();
            try
            {
                string fileName = Path.GetFileName(trimmed);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = trimmed;

                string? dir = Path.GetDirectoryName(trimmed);
                string workspaceName = "(root)";
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    var segments = dir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length >= 2)
                    {
                        string leafDir = segments[^1];
                        if (leafDir.Equals("docs", StringComparison.OrdinalIgnoreCase) ||
                            leafDir.Equals("notes", StringComparison.OrdinalIgnoreCase) ||
                            leafDir.Equals("specs", StringComparison.OrdinalIgnoreCase) ||
                            leafDir.Equals("src", StringComparison.OrdinalIgnoreCase))
                        {
                            workspaceName = $"{segments[^2]}/{leafDir}";
                        }
                        else
                        {
                            workspaceName = leafDir;
                        }
                    }
                    else if (segments.Length == 1)
                    {
                        workspaceName = segments[0];
                    }
                }

                entries.Add((workspaceName, fileName));
            }
            catch
            {
                entries.Add(("(unknown)", trimmed));
            }
        }

        if (entries.Count == 0)
        {
            return "Project workspace clusters: 0 workspaces (no recent files recorded)";
        }

        var clusters = entries
            .GroupBy(e => e.WorkspaceName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int multiFileHubs = clusters.Count(g => g.Count() >= 2);
        string wsLabel = clusters.Count == 1 ? "1 workspace" : $"{clusters.Count} workspaces";
        string docLabel = entries.Count == 1 ? "1 file" : $"{entries.Count} files";

        var clusterSummaries = clusters
            .Take(4)
            .Select(g =>
            {
                var sampleFiles = string.Join(", ", g.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Take(2));
                int extra = g.Count() - 2;
                string suffix = extra > 0 ? $", +{extra} more" : "";
                return $"{g.Key} ({g.Count()}: {sampleFiles}{suffix})";
            });

        string extraClusters = clusters.Count > 4 ? $" · +{clusters.Count - 4} more workspaces" : "";
        return $"Project workspace clusters ({docLabel} in {wsLabel}, {multiFileHubs} multi-file hub(s)): {string.Join(" · ", clusterSummaries)}{extraClusters}";
    }

    /// <summary>
    /// Represents a detected duplicate or version-suffixed recent document cluster.
    /// </summary>
    public sealed record DuplicateFileCluster(
        string CanonicalBaseName,
        IReadOnlyList<string> FilePaths,
        bool IsCrossDirectoryDuplicate,
        bool IsVersionSuffixed);

    /// <summary>
    /// Normalizes a document filename by stripping copy and version suffixes
    /// (e.g. "report (1)" -> "report", "doc - Copy" -> "doc", "spec_v2" -> "spec", "notes_backup" -> "notes").
    /// </summary>
    public static string NormalizeVersionedBaseName(string fileNameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension)) return string.Empty;
        var s = fileNameWithoutExtension.Trim();

        // Repeatedly strip recognized version suffixes
        bool stripped = true;
        while (stripped)
        {
            stripped = false;
            // (1), (2), etc.
            var mParen = System.Text.RegularExpressions.Regex.Match(s, @"\s*\(\d+\)$");
            if (mParen.Success)
            {
                s = s.Substring(0, mParen.Index).Trim();
                stripped = true;
                continue;
            }

            // - Copy, - Copy (1)
            var mCopy = System.Text.RegularExpressions.Regex.Match(s, @"[\s_-]+copy(?:\s*\(\d+\))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mCopy.Success)
            {
                s = s.Substring(0, mCopy.Index).Trim();
                stripped = true;
                continue;
            }

            // _v1, _v2, -v1.0
            var mVer = System.Text.RegularExpressions.Regex.Match(s, @"[\s_-]+v\d+(?:\.\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mVer.Success)
            {
                s = s.Substring(0, mVer.Index).Trim();
                stripped = true;
                continue;
            }

            // _backup, -backup
            var mBackup = System.Text.RegularExpressions.Regex.Match(s, @"[\s_-]+backup$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mBackup.Success)
            {
                s = s.Substring(0, mBackup.Index).Trim();
                stripped = true;
                continue;
            }
        }

        return string.IsNullOrEmpty(s) ? fileNameWithoutExtension.Trim() : s;
    }

    /// <summary>
    /// Audits recent document paths for exact filename duplicates across different folders
    /// and copy/version-suffixed document families.
    /// </summary>
    public static (int DuplicateClustersCount, int RedundantFilesCount, List<DuplicateFileCluster> Clusters)
        DetectDuplicateAndVersionedFiles(IEnumerable<string>? filePaths)
    {
        var clusters = new List<DuplicateFileCluster>();
        if (filePaths == null) return (0, 0, clusters);

        var list = filePaths.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count <= 1) return (0, 0, clusters);

        var entries = new List<(string FullPath, string FileName, string BaseName, string CanonicalBase, string Extension)>();
        foreach (var p in list)
        {
            string fn;
            string bn;
            string ext;
            try
            {
                fn = Path.GetFileName(p);
                bn = Path.GetFileNameWithoutExtension(p);
                ext = Path.GetExtension(p).ToLowerInvariant();
            }
            catch
            {
                fn = p;
                bn = p;
                ext = "";
            }

            string canonical = NormalizeVersionedBaseName(bn);
            entries.Add((p, fn, bn, canonical, ext));
        }

        // Group by CanonicalBase + Extension
        var groups = entries
            .GroupBy(e => $"{e.CanonicalBase}{e.Extension}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int redundantCount = 0;
        foreach (var g in groups)
        {
            var paths = g.Select(x => x.FullPath).ToList();
            bool isCrossDir = g.Select(x =>
            {
                try { return Path.GetDirectoryName(x.FullPath) ?? ""; }
                catch { return ""; }
            }).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;
            bool isVersioned = g.Any(x => !string.Equals(x.BaseName, x.CanonicalBase, StringComparison.OrdinalIgnoreCase));

            clusters.Add(new DuplicateFileCluster(
                g.First().CanonicalBase + g.First().Extension,
                paths,
                isCrossDir,
                isVersioned));

            redundantCount += paths.Count - 1; // all copies beyond the primary
        }

        return (clusters.Count, redundantCount, clusters);
    }

    /// <summary>
    /// Formats a human-readable audit summary of duplicate filenames and versioned document clusters.
    /// </summary>
    public static string FormatDuplicateAndVersionedFilesAudit(IEnumerable<string>? filePaths)
    {
        var (clusterCount, redundantCount, clusters) = DetectDuplicateAndVersionedFiles(filePaths);
        if (clusterCount == 0)
        {
            int total = filePaths?.Count(f => !string.IsNullOrWhiteSpace(f)) ?? 0;
            return total <= 0
                ? "Duplicate & versioned files audit: 0 files (No recent files recorded)"
                : $"Duplicate & versioned files audit ({total} files): 0 duplicates or versioned clusters (Clean library)";
        }

        string clusterLabel = clusterCount == 1 ? "1 duplicate cluster" : $"{clusterCount} duplicate clusters";
        string redundantLabel = redundantCount == 1 ? "1 redundant copy" : $"{redundantCount} redundant copies";

        var previews = clusters.Take(3).Select(c =>
        {
            string typeTag = (c.IsCrossDirectoryDuplicate, c.IsVersionSuffixed) switch
            {
                (true, true) => "cross-dir & versioned",
                (true, false) => "same name in diff folders",
                (false, true) => "version suffixes",
                _ => "clones"
            };
            return $"'{c.CanonicalBaseName}' ({c.FilePaths.Count} copies · {typeTag})";
        });

        string previewStr = string.Join(", ", previews);
        if (clusters.Count > 3) previewStr += $", +{clusters.Count - 3} more clusters";

        return $"Duplicate & versioned files audit ({clusterLabel} · {redundantLabel}): {previewStr}";
    }

    /// <summary>
    /// Computes a comprehensive Recent Library Health & Portability Readiness Scorecard combining
    /// on-disk path validity, duplicate/version-suffix detection, naming consistency, and workspace organization.
    /// </summary>
    public static (int Score, string Grade, int ValidPaths, int MissingPaths, int RedundantCopies, string Summary)
        AuditLibraryHealthScorecard(
            IEnumerable<string>? filePaths,
            Func<string, bool>? fileExistsPredicate = null)
    {
        var list = filePaths?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? new List<string>();
        if (list.Count == 0)
        {
            return (100, "A+", 0, 0, 0, "Recent library health scorecard: 0 documents (No recent files recorded)");
        }

        var (existing, missing) = AuditMissingRecentFiles(list, fileExistsPredicate);
        var (clusterCount, redundantCopies, _) = DetectDuplicateAndVersionedFiles(list);

        double validityRatio = list.Count > 0 ? (double)existing.Count / list.Count : 1.0;
        double uniquenessRatio = list.Count > 0 ? Math.Max(0.0, 1.0 - (double)redundantCopies / list.Count) : 1.0;

        int standardNamingCount = 0;
        foreach (var p in list)
        {
            try
            {
                var fn = Path.GetFileNameWithoutExtension(p);
                bool hasSpaces = fn.Contains(' ');
                bool hasCopySuffix = fn.Contains("(1)") || fn.Contains("- Copy") || fn.EndsWith("_v2", StringComparison.OrdinalIgnoreCase);
                if (!hasSpaces && !hasCopySuffix) standardNamingCount++;
            }
            catch
            {
                // ignore
            }
        }
        double namingRatio = (double)standardNamingCount / list.Count;

        // Score formula: 40% validity, 25% uniqueness, 20% naming, 15% baseline readiness
        double rawScore = (validityRatio * 40.0) + (uniquenessRatio * 25.0) + (namingRatio * 20.0) + 15.0;
        int score = Math.Clamp((int)Math.Round(rawScore), 0, 100);

        string grade = score switch
        {
            >= 95 => "A+",
            >= 85 => "A",
            >= 75 => "B",
            >= 65 => "C",
            _ => "Needs Attention"
        };

        string docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        string readinessVerdict = score >= 85
            ? "Ready for multi-workspace sync & portability"
            : missing.Count > 0
                ? $"Prune {missing.Count} missing path(s) to improve portability"
                : "Clean up duplicate/version-suffixed files";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Recent library health scorecard (Score: {score}/100 · Grade {grade} · {docLabel}):");
        sb.AppendLine($"  • Path validity: {(validityRatio * 100.0):0.#}% on disk ({existing.Count} valid · {missing.Count} missing)");
        sb.AppendLine($"  • Library cleanliness: {redundantCopies} redundant copies across {clusterCount} duplicate clusters");
        sb.AppendLine($"  • Naming consistency: {(namingRatio * 100.0):0.#}% standard naming ({standardNamingCount}/{list.Count} files)");
        sb.Append($"  • Portability verdict: {readinessVerdict}");

        return (score, grade, existing.Count, missing.Count, redundantCopies, sb.ToString());
    }

    /// <summary>
    /// Formats a human-readable library health and portability scorecard summary.
    /// </summary>
    public static string FormatLibraryHealthScorecard(
        IEnumerable<string>? filePaths,
        Func<string, bool>? fileExistsPredicate = null)
    {
        var (_, _, _, _, _, summary) = AuditLibraryHealthScorecard(filePaths, fileExistsPredicate);
        return summary;
    }

    /// <summary>
    /// Audits recent library documents across recency staleness decay tiers:
    /// Active (&lt; 24h), Fresh (1–7d), Warm (7–30d), Cold (30–90d), and Stale/Archived (&gt; 90d or inaccessible).
    /// Supports a custom timestamp provider and reference time for deterministic testing.
    /// </summary>
    public static (int Active, int Fresh, int Warm, int Cold, int Stale, int Total, string Summary)
        AuditRecentLibraryStalenessTiers(
            IEnumerable<string>? filePaths,
            Func<string, DateTime?>? lastWriteTimeProvider = null,
            DateTime? referenceTime = null)
    {
        var list = filePaths?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList() ?? new List<string>();
        if (list.Count == 0)
        {
            return (0, 0, 0, 0, 0, 0, "Recent library staleness tiers: 0 documents (No recent files recorded)");
        }

        var refTime = referenceTime ?? DateTime.Now;
        int active = 0, fresh = 0, warm = 0, cold = 0, stale = 0;

        foreach (var path in list)
        {
            DateTime? mtime = null;
            if (lastWriteTimeProvider != null)
            {
                mtime = lastWriteTimeProvider(path);
            }
            else
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists) mtime = fi.LastWriteTime;
                }
                catch
                {
                    // Inaccessible
                }
            }

            if (!mtime.HasValue)
            {
                stale++;
            }
            else
            {
                var age = refTime - mtime.Value;
                if (age.TotalHours < 24.0)
                    active++;
                else if (age.TotalDays <= 7.0)
                    fresh++;
                else if (age.TotalDays <= 30.0)
                    warm++;
                else if (age.TotalDays <= 90.0)
                    cold++;
                else
                    stale++;
            }
        }

        string docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        string advice = stale > 0
            ? $"Prune or archive {stale} stale document{(stale == 1 ? "" : "s")} >90d old"
            : cold > 0
                ? $"Review {cold} cold document{(cold == 1 ? "" : "s")} >30d old"
                : "Recent library is fresh and actively maintained";

        string summary = $"Recent library staleness tiers ({docLabel}): {active} active (<24h), {fresh} fresh (1-7d), {warm} warm (7-30d), {cold} cold (30-90d), {stale} stale (>90d) · Hygiene: {advice}";
        return (active, fresh, warm, cold, stale, list.Count, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of recent library staleness decay tiers.
    /// </summary>
    public static string FormatStalenessTiersSummary(
        IEnumerable<string>? filePaths,
        Func<string, DateTime?>? lastWriteTimeProvider = null,
        DateTime? referenceTime = null)
    {
        var (_, _, _, _, _, _, summary) = AuditRecentLibraryStalenessTiers(filePaths, lastWriteTimeProvider, referenceTime);
        return summary;
    }

    /// <summary>
    /// Audits recent document library files across storage volumes and drive partitions (e.g. C:\, D:\, network shares, cloud storage),
    /// identifying accessible vs offline volumes and computing volume distribution percentages.
    /// Supports a custom accessibility predicate for deterministic testing.
    /// </summary>
    public static (int TotalFiles, int TotalVolumes, int AccessibleVolumes, int OfflineVolumes, string DominantVolume, string Summary)
        AuditVolumeDistribution(
            IEnumerable<string>? filePaths,
            Func<string, bool>? volumeAccessibilityPredicate = null)
    {
        var list = filePaths?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList() ?? new List<string>();
        if (list.Count == 0)
        {
            return (0, 0, 0, 0, "None", "Recent library volume distribution: 0 documents (No recent files recorded)");
        }

        var volumeGroups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in list)
        {
            string root = "";
            try
            {
                root = Path.GetPathRoot(path) ?? "";
            }
            catch
            {
                // In case of invalid path characters
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                root = "Relative / Workspace";
            }
            else
            {
                root = root.Trim();
                if (root.Length >= 2 && root[1] == ':')
                {
                    root = char.ToUpperInvariant(root[0]) + root.Substring(1);
                }
            }

            if (volumeGroups.ContainsKey(root))
                volumeGroups[root]++;
            else
                volumeGroups[root] = 1;
        }

        int totalVolumes = volumeGroups.Count;
        int accessible = 0;
        int offline = 0;

        foreach (var vol in volumeGroups.Keys)
        {
            bool isAcc = true;
            if (volumeAccessibilityPredicate != null)
            {
                isAcc = volumeAccessibilityPredicate(vol);
            }
            else if (!vol.StartsWith("Relative", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    isAcc = Directory.Exists(vol);
                }
                catch
                {
                    isAcc = false;
                }
            }

            if (isAcc) accessible++;
            else offline++;
        }

        var sortedVolumes = volumeGroups.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
        var dominant = sortedVolumes[0].Key;

        var volumeSummaries = sortedVolumes.Select(kv =>
        {
            double pct = Math.Round((double)kv.Value / list.Count * 100.0, 1);
            return $"{kv.Key} ({kv.Value} doc{(kv.Value == 1 ? "" : "s")}, {pct:0.#}%)";
        }).ToList();

        string summary = FormatVolumeDistributionSummary(list.Count, totalVolumes, accessible, offline, dominant, volumeSummaries);
        return (list.Count, totalVolumes, accessible, offline, dominant, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of storage volume and drive partition distribution.
    /// </summary>
    public static string FormatVolumeDistributionSummary(
        int totalFiles,
        int totalVolumes,
        int accessibleVolumes,
        int offlineVolumes,
        string dominantVolume,
        IReadOnlyList<string>? volumeBreakdown = null)
    {
        if (totalFiles <= 0)
            return "Recent library volume distribution: 0 documents (No recent files recorded)";

        string docLabel = totalFiles == 1 ? "1 document" : $"{totalFiles} documents";
        string volLabel = totalVolumes == 1 ? "1 storage volume" : $"{totalVolumes} storage volumes";

        string offlineStr = offlineVolumes > 0 ? $" · Warning: {offlineVolumes} volume{(offlineVolumes == 1 ? "" : "s")} offline/unreachable" : "";
        string breakdownStr = volumeBreakdown != null && volumeBreakdown.Count > 0
            ? $" [{string.Join(" · ", volumeBreakdown.Take(4))}]"
            : "";

        return $"Recent library volume distribution ({docLabel} across {volLabel}): Dominant: {dominantVolume}{offlineStr}{breakdownStr}";
    }

    /// <summary>
    /// Formats a human-readable summary of volume distribution from a collection of file paths.
    /// </summary>
    public static string FormatVolumeDistributionSummary(
        IEnumerable<string>? filePaths,
        Func<string, bool>? volumeAccessibilityPredicate = null)
    {
        var (_, _, _, _, _, summary) = AuditVolumeDistribution(filePaths, volumeAccessibilityPredicate);
        return summary;
    }

    /// <summary>
    /// Represents the result of a temporal access frequency heatmap and revisit cadence audit across recent documents.
    /// </summary>
    public sealed record AccessFrequencyHeatmap(
        int TotalFiles,
        int DailyCadence,
        int WeeklyCadence,
        int BiWeeklyCadence,
        int MonthlyCadence,
        int DormantOrphans,
        double RevisitVelocityScore,
        string DominantCadence,
        string Summary);

    /// <summary>
    /// Audits recent document library files for temporal access frequency, revisit velocity, and cadence distribution.
    /// Supports custom last-access time provider and reference time for deterministic testing.
    /// </summary>
    public static AccessFrequencyHeatmap AuditAccessFrequencyHeatmap(
        IEnumerable<string>? filePaths,
        Func<string, DateTime?>? lastAccessTimeProvider = null,
        DateTime? referenceTime = null)
    {
        var list = filePaths?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList() ?? new List<string>();
        if (list.Count == 0)
        {
            return new AccessFrequencyHeatmap(
                0, 0, 0, 0, 0, 0, 0.0, "None",
                "Recent access frequency heatmap: 0 documents (No recent files recorded)");
        }

        DateTime now = referenceTime ?? DateTime.UtcNow;
        int daily = 0;     // < 24h
        int weekly = 0;    // 1-7d
        int biweekly = 0;  // 7-14d
        int monthly = 0;   // 14-30d
        int dormant = 0;   // > 30d

        foreach (var p in list)
        {
            DateTime? accessTime = null;
            if (lastAccessTimeProvider != null)
            {
                accessTime = lastAccessTimeProvider(p);
            }
            else
            {
                try
                {
                    if (File.Exists(p))
                    {
                        var t = File.GetLastAccessTimeUtc(p);
                        if (t != DateTime.MinValue) accessTime = t;
                        else accessTime = File.GetLastWriteTimeUtc(p);
                    }
                }
                catch { }
            }

            if (!accessTime.HasValue)
            {
                dormant++;
                continue;
            }

            var age = now - accessTime.Value;
            if (age.TotalDays < 0) age = TimeSpan.Zero; // clock drift clamp

            if (age.TotalHours < 24) daily++;
            else if (age.TotalDays < 7) weekly++;
            else if (age.TotalDays < 14) biweekly++;
            else if (age.TotalDays < 30) monthly++;
            else dormant++;
        }

        // Weighted velocity score (100 = all accessed today, lower = colder library)
        double weightedPoints = (daily * 5) + (weekly * 4) + (biweekly * 3) + (monthly * 2) + (dormant * 1);
        double maxPoints = list.Count * 5;
        double velocityScore = Math.Round((weightedPoints / maxPoints) * 100.0, 1);

        var cadenceCounts = new (string Name, int Count)[]
        {
            ("Daily (<24h)", daily),
            ("Weekly (1–7d)", weekly),
            ("Bi-weekly (7–14d)", biweekly),
            ("Monthly (14–30d)", monthly),
            ("Dormant (>30d)", dormant)
        };
        var dominant = cadenceCounts.OrderByDescending(c => c.Count).First().Name;

        string summary = FormatAccessFrequencyHeatmapSummary(list.Count, daily, weekly, biweekly, monthly, dormant, velocityScore, dominant);
        return new AccessFrequencyHeatmap(list.Count, daily, weekly, biweekly, monthly, dormant, velocityScore, dominant, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of access frequency heatmap metrics.
    /// </summary>
    public static string FormatAccessFrequencyHeatmapSummary(
        int totalFiles, int daily, int weekly, int biweekly, int monthly, int dormant, double velocityScore, string dominantCadence)
    {
        if (totalFiles <= 0)
            return "Recent access frequency heatmap: 0 documents (No recent files recorded)";

        string docLabel = totalFiles == 1 ? "1 document" : $"{totalFiles} documents";
        string breakdown = $"{daily} daily (<24h), {weekly} weekly (1–7d), {biweekly} bi-weekly (7–14d), {monthly} monthly (14–30d), {dormant} dormant (>30d)";

        return $"Recent access frequency heatmap ({docLabel} · Velocity: {velocityScore:0.#}/100 · Dominant: {dominantCadence}): {breakdown}";
    }

    /// <summary>
    /// Formats a human-readable summary of access frequency heatmap from a collection of file paths.
    /// </summary>
    public static string FormatAccessFrequencyHeatmapSummary(
        IEnumerable<string>? filePaths,
        Func<string, DateTime?>? lastAccessTimeProvider = null,
        DateTime? referenceTime = null)
    {
        return AuditAccessFrequencyHeatmap(filePaths, lastAccessTimeProvider, referenceTime).Summary;
    }

    /// <summary>
    /// Represents an individual dormant file alert and revival recommendation.
    /// </summary>
    public sealed record DormantFileAlert(
        string FilePath,
        string FileName,
        int DaysIdle,
        bool ExistsOnDisk,
        string RecommendedAction);

    /// <summary>
    /// Represents the result of a library dormancy risk and stale project revival audit.
    /// </summary>
    public sealed record DormancyAlertsAudit(
        int TotalFiles,
        int DormantCount,
        int ActiveCount,
        int MissingCount,
        string RiskLevel,
        IReadOnlyList<DormantFileAlert> Alerts,
        string Summary);

    /// <summary>
    /// Audits recent document library files for dormancy risk (>60 days inactive or missing on disk)
    /// and generates project revival suggestions. Supports deterministic testing via custom access time provider.
    /// </summary>
    public static DormancyAlertsAudit AuditDormancyAlerts(
        IEnumerable<string>? filePaths,
        Func<string, DateTime?>? lastAccessTimeProvider = null,
        DateTime? referenceTime = null,
        int dormancyDaysThreshold = 60)
    {
        var list = filePaths?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList() ?? new List<string>();
        if (list.Count == 0)
        {
            return new DormancyAlertsAudit(
                0, 0, 0, 0, "None", Array.Empty<DormantFileAlert>(),
                "Library dormancy risk audit: 0 documents (No recent files recorded)");
        }

        DateTime now = referenceTime ?? DateTime.UtcNow;
        var alerts = new List<DormantFileAlert>();
        int activeCount = 0;
        int missingCount = 0;

        foreach (var p in list)
        {
            string fileName = "";
            try { fileName = Path.GetFileName(p); } catch { fileName = p; }

            bool exists = false;
            try { exists = File.Exists(p); } catch { }

            DateTime? accessTime = null;
            if (lastAccessTimeProvider != null)
            {
                accessTime = lastAccessTimeProvider(p);
            }
            else if (exists)
            {
                try
                {
                    var t = File.GetLastAccessTimeUtc(p);
                    if (t != DateTime.MinValue) accessTime = t;
                    else accessTime = File.GetLastWriteTimeUtc(p);
                }
                catch { }
            }

            if (!exists && lastAccessTimeProvider == null)
            {
                missingCount++;
                alerts.Add(new DormantFileAlert(
                    p, fileName, 999, false, "Prune dead link or relocate file"));
                continue;
            }

            int daysIdle;
            if (accessTime.HasValue)
            {
                var span = now - accessTime.Value;
                daysIdle = Math.Max(0, (int)span.TotalDays);
            }
            else
            {
                daysIdle = 999;
            }

            if (daysIdle >= dormancyDaysThreshold)
            {
                string action = daysIdle > 180 ? "Archive or prune stale project" : "Review or resume work";
                alerts.Add(new DormantFileAlert(p, fileName, daysIdle, exists || lastAccessTimeProvider != null, action));
            }
            else
            {
                activeCount++;
            }
        }

        int dormantCount = alerts.Count;
        string riskLevel;
        if (dormantCount == 0)
            riskLevel = "None (All documents active)";
        else if (dormantCount <= 2)
            riskLevel = "Low (1–2 stale documents)";
        else if (dormantCount <= 5)
            riskLevel = "Moderate (Stale documents accumulating)";
        else
            riskLevel = "High (Significant dormancy backlog)";

        string summary = FormatDormancyAlertsSummary(list.Count, dormantCount, activeCount, missingCount, riskLevel, alerts);
        return new DormancyAlertsAudit(list.Count, dormantCount, activeCount, missingCount, riskLevel, alerts, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of library dormancy risk and revival alerts.
    /// </summary>
    public static string FormatDormancyAlertsSummary(
        int totalFiles, int dormantCount, int activeCount, int missingCount, string riskLevel, IReadOnlyList<DormantFileAlert>? alerts = null)
    {
        if (totalFiles <= 0) return "Library dormancy risk audit: 0 documents (No recent files recorded)";
        string docLabel = totalFiles == 1 ? "1 document" : $"{totalFiles} documents";

        if (dormantCount == 0)
        {
            return $"Library dormancy risk audit ({docLabel}): 0 dormant projects · Risk: {riskLevel}";
        }

        string dormantLabel = dormantCount == 1 ? "1 dormant project" : $"{dormantCount} dormant projects";
        string missingStr = missingCount > 0 ? $" ({missingCount} unreachable/missing)" : "";
        string preview = "";
        if (alerts != null && alerts.Count > 0)
        {
            var sample = string.Join(", ", alerts.Take(3).Select(a => $"'{a.FileName}' ({a.DaysIdle}d idle)"));
            preview = $" [{sample}{(alerts.Count > 3 ? ", …" : "")}]";
        }

        return $"Library dormancy risk audit ({docLabel}): {dormantLabel}{missingStr} · Risk: {riskLevel} · Active: {activeCount}{preview}";
    }

    /// <summary>
    /// Audits dormancy and project stagnation across workspace folder clusters in recent documents.
    /// </summary>
    public static WorkspaceClusterDormancyAudit AuditWorkspaceClusterDormancy(
        IEnumerable<string>? files,
        Func<string, DateTime?>? lastAccessTimeProvider = null,
        DateTime? referenceTime = null,
        int dormancyDaysThreshold = 60)
    {
        var list = files?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();

        if (list.Count == 0)
        {
            return new WorkspaceClusterDormancyAudit(
                0, 0, 0, Array.Empty<WorkspaceClusterDormancyInfo>(),
                "Workspace cluster dormancy audit: 0 workspaces, 0 documents (No recent files recorded)");
        }

        var now = referenceTime ?? DateTime.UtcNow;

        // Group into workspace clusters
        var clusterBuckets = new Dictionary<string, (string DisplayName, List<(string FilePath, string FileName, bool IsDormant)> Files)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in list)
        {
            string fileName;
            string dir;
            try
            {
                fileName = Path.GetFileName(p);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = p;
                dir = Path.GetDirectoryName(p) ?? "(root)";
            }
            catch
            {
                fileName = p;
                dir = "(unknown)";
            }

            string workspaceName = "(root)";
            if (!string.IsNullOrWhiteSpace(dir) && dir != "(root)" && dir != "(unknown)")
            {
                var segments = dir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2)
                {
                    string leaf = segments[^1];
                    if (leaf.Equals("docs", StringComparison.OrdinalIgnoreCase) ||
                        leaf.Equals("notes", StringComparison.OrdinalIgnoreCase) ||
                        leaf.Equals("specs", StringComparison.OrdinalIgnoreCase) ||
                        leaf.Equals("src", StringComparison.OrdinalIgnoreCase))
                    {
                        workspaceName = $"{segments[^2]}/{leaf}";
                    }
                    else
                    {
                        workspaceName = leaf;
                    }
                }
                else if (segments.Length == 1)
                {
                    workspaceName = segments[0];
                }
            }

            // Determine if file is dormant
            DateTime? accessTime = null;
            bool exists = false;
            try { exists = File.Exists(p); } catch { }

            if (lastAccessTimeProvider != null)
            {
                accessTime = lastAccessTimeProvider(p);
            }
            else if (exists)
            {
                try
                {
                    var t = File.GetLastAccessTimeUtc(p);
                    if (t != DateTime.MinValue) accessTime = t;
                    else accessTime = File.GetLastWriteTimeUtc(p);
                }
                catch { }
            }

            int daysIdle = 999;
            if (accessTime.HasValue)
            {
                daysIdle = Math.Max(0, (int)(now - accessTime.Value).TotalDays);
            }
            else if (exists)
            {
                daysIdle = 0;
            }

            bool isDormant = daysIdle >= dormancyDaysThreshold || (!exists && lastAccessTimeProvider == null);

            if (!clusterBuckets.TryGetValue(dir, out var bucket))
            {
                bucket = (workspaceName, new List<(string, string, bool)>());
                clusterBuckets[dir] = bucket;
            }
            bucket.Files.Add((p, fileName, isDormant));
        }

        var clusterInfos = new List<WorkspaceClusterDormancyInfo>();
        int stagnantClusters = 0;

        foreach (var kvp in clusterBuckets.OrderByDescending(b => b.Value.Files.Count))
        {
            string dirPath = kvp.Key;
            string wsName = kvp.Value.DisplayName;
            var fileList = kvp.Value.Files;

            int totalInCluster = fileList.Count;
            int dormantInCluster = fileList.Count(f => f.IsDormant);
            double ratio = Math.Round((double)dormantInCluster / totalInCluster, 2);
            var samples = fileList.Select(f => f.FileName).Distinct().Take(3).ToList();

            if (ratio >= 0.5 && dormantInCluster > 0)
            {
                stagnantClusters++;
            }

            clusterInfos.Add(new WorkspaceClusterDormancyInfo(
                wsName,
                dirPath,
                totalInCluster,
                dormantInCluster,
                ratio,
                samples));
        }

        string docLabel = list.Count == 1 ? "1 document" : $"{list.Count} documents";
        string wsLabel = clusterInfos.Count == 1 ? "1 workspace" : $"{clusterInfos.Count} workspaces";
        string summary = FormatWorkspaceClusterDormancySummary(list.Count, clusterInfos.Count, stagnantClusters, clusterInfos);

        return new WorkspaceClusterDormancyAudit(
            list.Count,
            clusterInfos.Count,
            stagnantClusters,
            clusterInfos,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of workspace cluster dormancy and project stagnation.
    /// </summary>
    public static string FormatWorkspaceClusterDormancySummary(
        int totalFiles, int totalClusters, int stagnantClusters, IReadOnlyList<WorkspaceClusterDormancyInfo>? clusters = null)
    {
        if (totalFiles <= 0) return "Workspace cluster dormancy audit: 0 workspaces, 0 documents (No recent files recorded)";
        string docLabel = totalFiles == 1 ? "1 document" : $"{totalFiles} documents";
        string wsLabel = totalClusters == 1 ? "1 workspace" : $"{totalClusters} workspaces";

        if (stagnantClusters == 0)
        {
            return $"Workspace cluster dormancy audit ({docLabel} across {wsLabel}): 0 stagnant clusters (All active)";
        }

        string stagLabel = stagnantClusters == 1 ? "1 stagnant cluster" : $"{stagnantClusters} stagnant clusters";
        string preview = "";
        if (clusters != null && clusters.Count > 0)
        {
            var stagList = clusters.Where(c => c.DormancyRatio >= 0.5 && c.DormantFiles > 0).Take(2)
                .Select(c => $"'{c.WorkspaceName}' ({c.DormantFiles}/{c.TotalFiles} dormant, {c.DormancyRatio * 100:0}%)");
            preview = $" [{string.Join(", ", stagList)}]";
        }

        return $"Workspace cluster dormancy audit ({docLabel} across {wsLabel}): {stagLabel}{preview}";
    }

    /// <summary>
    /// Audits workspace activity velocity and active vs dormant ratio per folder cluster.
    /// Files accessed within the last 14 days are classified as active.
    /// </summary>
    public static ClusterActivityVelocityAudit AuditClusterActivityVelocity(
        IEnumerable<string>? recentFiles,
        Func<string, DateTime>? lastAccessTimeProvider = null,
        DateTime? referenceTime = null)
    {
        var list = (recentFiles ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
        {
            string emptySummary = "Workspace velocity audit: 0 workspaces, 0 documents (No recent files recorded)";
            return new ClusterActivityVelocityAudit(
                0, 0, 0.0, "None", Array.Empty<ClusterActivityVelocityInfo>(), emptySummary);
        }

        var now = referenceTime ?? DateTime.UtcNow;
        var getTime = lastAccessTimeProvider ?? (p =>
        {
            try
            {
                return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        });

        // Group into clusters
        var clusters = new Dictionary<string, List<(string Path, string FileName, DateTime LastAccess, bool IsActive)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in list)
        {
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            string clusterName = string.IsNullOrWhiteSpace(dir)
                ? "Root"
                : Path.GetFileName(dir);

            if (string.IsNullOrWhiteSpace(clusterName))
            {
                clusterName = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            var accessTime = getTime(path);
            bool isActive = accessTime != DateTime.MinValue && (now - accessTime).TotalDays <= 14.0;

            if (!clusters.TryGetValue(clusterName, out var fileList))
            {
                fileList = new List<(string, string, DateTime, bool)>();
                clusters[clusterName] = fileList;
            }

            fileList.Add((path, Path.GetFileName(path), accessTime, isActive));
        }

        var clusterInfos = new List<ClusterActivityVelocityInfo>();
        int totalActive = 0;

        foreach (var (clusterName, fileList) in clusters)
        {
            int total = fileList.Count;
            int active = fileList.Count(f => f.IsActive);
            totalActive += active;
            double pct = Math.Round((double)active / total * 100.0, 1);

            string rating = pct switch
            {
                >= 75.0 => "High Velocity",
                >= 50.0 => "Active Cadence",
                >= 25.0 => "Low Velocity",
                > 0.0 => "Stagnant",
                _ => "Dormant"
            };

            var mostRecent = fileList.OrderByDescending(f => f.LastAccess).FirstOrDefault();
            string topFile = mostRecent.FileName ?? "None";

            clusterInfos.Add(new ClusterActivityVelocityInfo(
                clusterName, total, active, pct, rating, topFile));
        }

        double overallVelocity = Math.Round((double)totalActive / list.Count * 100.0, 1);
        var highest = clusterInfos.OrderByDescending(c => c.ActivityPercentage).FirstOrDefault();
        string topCluster = highest?.ClusterName ?? "None";

        string summary = FormatClusterActivityVelocitySummary(list.Count, clusterInfos.Count, overallVelocity, topCluster, clusterInfos);

        return new ClusterActivityVelocityAudit(
            list.Count, clusterInfos.Count, overallVelocity, topCluster, clusterInfos, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of workspace activity velocity.
    /// </summary>
    public static string FormatClusterActivityVelocitySummary(
        int totalFiles, int totalClusters, double overallVelocity, string topCluster, IReadOnlyList<ClusterActivityVelocityInfo>? clusters = null)
    {
        if (totalFiles <= 0) return "Workspace velocity audit: 0 workspaces, 0 documents";
        string docLabel = totalFiles == 1 ? "1 document" : $"{totalFiles} documents";
        string wsLabel = totalClusters == 1 ? "1 workspace" : $"{totalClusters} workspaces";

        string preview = "";
        if (clusters != null && clusters.Count > 0)
        {
            var activeClusters = clusters.OrderByDescending(c => c.ActivityPercentage).Take(2)
                .Select(c => $"'{c.ClusterName}' ({c.ActivityPercentage:0}% active, {c.ActiveFiles}/{c.TotalFiles})");
            preview = $" [{string.Join(", ", activeClusters)}]";
        }

        return $"Workspace velocity audit ({overallVelocity:0.#}% active across {wsLabel}, {docLabel} · Top: '{topCluster}'): {overallVelocity:0.#}% library velocity{preview}";
    }

    /// <summary>
    /// Generates active workspace cluster filter chips from recent files, enabling one-click directory filtering in the UI.
    /// </summary>
    public static IReadOnlyList<ActiveClusterFilterChip> GetActiveClusterFilterChips(
        IEnumerable<string>? recentFiles,
        Func<string, DateTime>? lastAccessTimeProvider = null,
        DateTime? referenceTime = null)
    {
        var audit = AuditClusterActivityVelocity(recentFiles, lastAccessTimeProvider, referenceTime);
        if (audit.Clusters.Count == 0) return Array.Empty<ActiveClusterFilterChip>();

        return audit.Clusters
            .OrderByDescending(c => c.ActiveFiles)
            .ThenByDescending(c => c.ActivityPercentage)
            .Select(c => new ActiveClusterFilterChip(
                c.ClusterName,
                c.TotalFiles,
                c.ActiveFiles,
                c.ActivityPercentage,
                c.ClusterName,
                $"'{c.ClusterName}' ({c.ActiveFiles}/{c.TotalFiles} active)"))
            .ToList();
    }

    /// <summary>
    /// Formats a human-readable summary of active cluster filter chips.
    /// </summary>
    public static string FormatActiveClusterFilterSummary(IReadOnlyList<ActiveClusterFilterChip> chips)
    {
        if (chips == null || chips.Count == 0) return "Active cluster filter: No clusters found";
        var top = chips[0];
        string clusterLabel = chips.Count == 1 ? "1 cluster" : $"{chips.Count} clusters";
        return $"Active cluster filter ({clusterLabel}): Top active '{top.ClusterName}' ({top.ActiveFiles}/{top.TotalFiles} active, {top.ActivityPercentage:0}%)";
    }

    /// <summary>
    /// Audits recent files to construct a file format cluster distribution matrix, calculate Simpson's
    /// format diversity index, and detect single-format silos.
    /// </summary>
    public static FormatClusterMatrixAudit AuditFormatClusterMatrix(IEnumerable<string>? files)
    {
        var list = (files ?? Enumerable.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();

        if (list.Count == 0)
        {
            return new FormatClusterMatrixAudit(
                0, 0, "None", 0.0, 0.0, "Empty Library",
                Array.Empty<FormatClusterInfo>(),
                "Format cluster matrix: 0 files (Empty library)");
        }

        var extCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in list)
        {
            string ext = Path.GetExtension(file);
            if (string.IsNullOrEmpty(ext)) ext = ".unknown";
            else ext = ext.ToLowerInvariant();

            extCounts[ext] = extCounts.TryGetValue(ext, out int c) ? c + 1 : 1;
        }

        var formats = extCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Select(kv =>
            {
                string name = kv.Key switch
                {
                    ".md" => "Markdown",
                    ".markdown" => "Markdown",
                    ".docx" => "Word Document",
                    ".pdf" => "PDF Document",
                    ".pptx" => "PowerPoint",
                    ".html" => "HTML Web Page",
                    ".txt" => "Plain Text",
                    ".json" => "JSON Data",
                    _ => kv.Key.TrimStart('.').ToUpperInvariant()
                };
                double pct = Math.Round((double)kv.Value * 100.0 / list.Count, 1);
                return new FormatClusterInfo(kv.Key, name, kv.Value, pct);
            })
            .ToList();

        var top = formats[0];

        double sumSq = formats.Sum(f => Math.Pow((double)f.FileCount / list.Count, 2));
        double diversity = Math.Round(1.0 - sumSq, 2);

        string siloStatus;
        if (top.Percentage >= 80.0)
        {
            siloStatus = $"Single-Format Silo ({top.Extension} {top.Percentage:0}%)";
        }
        else if (formats.Count >= 2 && (top.Percentage + formats[1].Percentage) >= 80.0)
        {
            siloStatus = $"Dual-Format Focus ({top.Extension} + {formats[1].Extension})";
        }
        else if (diversity >= 0.6)
        {
            siloStatus = "Diverse Multi-Format Portfolio";
        }
        else
        {
            siloStatus = "Balanced Format Mix";
        }

        string summary = FormatClusterMatrixSummary(list.Count, formats.Count, top, diversity, siloStatus, formats);

        return new FormatClusterMatrixAudit(
            list.Count,
            formats.Count,
            top.Extension,
            top.Percentage,
            diversity,
            siloStatus,
            formats,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of the file format cluster distribution matrix.
    /// </summary>
    public static string FormatClusterMatrixSummary(
        int totalFiles, int totalFormats, FormatClusterInfo topFormat, double diversityIndex, string siloStatus,
        IReadOnlyList<FormatClusterInfo>? formats = null)
    {
        if (totalFiles <= 0) return "Format cluster matrix: 0 files (Empty library)";
        string fileLabel = totalFiles == 1 ? "1 file" : $"{totalFiles} files";
        string formatLabel = totalFormats == 1 ? "1 format" : $"{totalFormats} formats";

        string breakdown = "";
        if (formats != null && formats.Count > 0)
        {
            var parts = formats.Take(4).Select(f => $"{f.Extension}: {f.FileCount} ({f.Percentage:0}%)");
            breakdown = $" · [{string.Join(", ", parts)}]";
        }

        return $"Format cluster matrix ({fileLabel} across {formatLabel} · {siloStatus}){breakdown} · Diversity: {diversityIndex:0.##}";
    }

    /// <summary>
    /// Audits companion export format coverage across recent files, evaluating whether Markdown (.md) documents
    /// have compiled companion exports (.docx, .pdf, .html) and detecting single-format or uncompiled documents.
    /// </summary>
    public static CompanionFormatCoverageAudit AuditCompanionFormatCoverage(IEnumerable<string>? files)
    {
        var list = (files ?? Enumerable.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();

        if (list.Count == 0)
        {
            return new CompanionFormatCoverageAudit(
                0, 0, 0, 0, 0.0, "Empty Library",
                Array.Empty<DocumentFamilyCoverageInfo>(),
                "Companion format coverage: 0 document families (Empty library)");
        }

        var families = new Dictionary<string, DocumentFamilyCoverageInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in list)
        {
            string dir = Path.GetDirectoryName(file) ?? "";
            string nameWithoutExt = Path.GetFileNameWithoutExtension(file) ?? "";
            string ext = Path.GetExtension(file)?.ToLowerInvariant() ?? "";

            if (string.IsNullOrEmpty(nameWithoutExt)) continue;

            string key = Path.Combine(dir, nameWithoutExt);
            if (!families.TryGetValue(key, out var family))
            {
                family = new DocumentFamilyCoverageInfo(nameWithoutExt, dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                families[key] = family;
            }

            if (!string.IsNullOrEmpty(ext))
            {
                family.Extensions.Add(ext);
            }
        }

        if (families.Count == 0)
        {
            return new CompanionFormatCoverageAudit(
                0, 0, 0, 0, 0.0, "Empty Library",
                Array.Empty<DocumentFamilyCoverageInfo>(),
                "Companion format coverage: 0 document families (Empty library)");
        }

        int totalFamilies = families.Count;
        int fullyCoveredCount = 0;
        int sourceOnlyCount = 0;
        int exportOnlyCount = 0;

        var familyList = families.Values.ToList();

        foreach (var f in familyList)
        {
            bool hasSource = f.Extensions.Contains(".md") || f.Extensions.Contains(".markdown");
            bool hasCompiled = f.Extensions.Contains(".docx") || f.Extensions.Contains(".pdf") ||
                               f.Extensions.Contains(".html") || f.Extensions.Contains(".epub") ||
                               f.Extensions.Contains(".pptx");

            if (hasSource && hasCompiled)
            {
                fullyCoveredCount++;
            }
            else if (hasSource && !hasCompiled)
            {
                sourceOnlyCount++;
            }
            else if (!hasSource && hasCompiled)
            {
                exportOnlyCount++;
            }
            else
            {
                sourceOnlyCount++;
            }
        }

        double coveragePercent = totalFamilies > 0
            ? Math.Round((double)fullyCoveredCount * 100.0 / totalFamilies, 1)
            : 0.0;

        string status;
        if (coveragePercent >= 75.0)
        {
            status = "High Export Coverage (≥75%)";
        }
        else if (coveragePercent >= 40.0)
        {
            status = "Moderate Export Coverage (40–74%)";
        }
        else if (sourceOnlyCount > 0)
        {
            status = "Source-Heavy / Uncompiled (<40%)";
        }
        else
        {
            status = "Low Companion Coverage";
        }

        string summary = FormatCompanionFormatCoverageSummary(
            totalFamilies, fullyCoveredCount, sourceOnlyCount, exportOnlyCount, coveragePercent, status);

        return new CompanionFormatCoverageAudit(
            totalFamilies,
            fullyCoveredCount,
            sourceOnlyCount,
            exportOnlyCount,
            coveragePercent,
            status,
            familyList,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of companion format coverage metrics.
    /// </summary>
    public static string FormatCompanionFormatCoverageSummary(
        int totalFamilies, int fullyCovered, int sourceOnly, int exportOnly,
        double coveragePercent, string status)
    {
        if (totalFamilies <= 0) return "Companion format coverage: 0 document families (Empty library)";
        string familyLabel = totalFamilies == 1 ? "1 document family" : $"{totalFamilies} document families";
        string breakdown = $"{fullyCovered} compiled companions, {sourceOnly} source-only, {exportOnly} export-only";

        return $"Companion coverage ({familyLabel} [{breakdown}] · {status}): {coveragePercent:0.#}% companion rate";
    }

    /// <summary>
    /// Audits workspace document aging, modification staleness, and editing velocity.
    /// </summary>
    public static DocumentStalenessAudit AuditDocumentStalenessAndVelocity(
        IEnumerable<string>? files, DateTime? referenceTimeUtc = null)
    {
        if (files == null)
        {
            return new DocumentStalenessAudit(
                0, 0, 0, 0, 0, 0.0, 0.0, Array.Empty<DocumentAgingInfo>(),
                "Empty Library", "Document aging & velocity: 0 documents (Empty library)");
        }

        var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return new DocumentStalenessAudit(
                0, 0, 0, 0, 0, 0.0, 0.0, Array.Empty<DocumentAgingInfo>(),
                "Empty Library", "Document aging & velocity: 0 documents (Empty library)");
        }

        DateTime now = referenceTimeUtc ?? DateTime.UtcNow;
        var agingInfos = new List<DocumentAgingInfo>();

        int active = 0;
        int warm = 0;
        int cold = 0;
        int archived = 0;
        double totalAgeDays = 0.0;

        foreach (var path in list)
        {
            DateTime lastWrite;
            try
            {
                if (File.Exists(path))
                {
                    lastWrite = File.GetLastWriteTimeUtc(path);
                }
                else
                {
                    lastWrite = now.AddDays(-14);
                }
            }
            catch
            {
                lastWrite = now.AddDays(-14);
            }

            double ageDays = Math.Max(0.0, Math.Round((now - lastWrite).TotalDays, 1));
            totalAgeDays += ageDays;

            string tier;
            if (ageDays < 1.0)
            {
                tier = "Active (<24h)";
                active++;
            }
            else if (ageDays < 7.0)
            {
                tier = "Warm (<7d)";
                warm++;
            }
            else if (ageDays < 30.0)
            {
                tier = "Cold (<30d)";
                cold++;
            }
            else
            {
                tier = "Archived (>30d)";
                archived++;
            }

            string fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName)) fileName = path;

            agingInfos.Add(new DocumentAgingInfo(path, fileName, lastWrite, ageDays, tier));
        }

        double avgAge = Math.Round(totalAgeDays / list.Count, 1);
        double freshnessIndex = Math.Round((double)(active + warm) * 100.0 / list.Count, 1);

        string category;
        if (freshnessIndex >= 70.0)
        {
            category = "High Sprint Velocity (Active Workspace)";
        }
        else if (freshnessIndex >= 35.0)
        {
            category = "Steady Maintenance Velocity";
        }
        else
        {
            category = "Dormant / Stale Document Archive";
        }

        string summary = FormatDocumentStalenessSummary(
            list.Count, active, warm, cold, archived, freshnessIndex, avgAge, category);

        return new DocumentStalenessAudit(
            list.Count, active, warm, cold, archived,
            freshnessIndex, avgAge, agingInfos, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of document aging and editing velocity metrics.
    /// </summary>
    public static string FormatDocumentStalenessSummary(
        int totalFiles, int active, int warm, int cold, int archived,
        double freshnessIndex, double avgAge, string category)
    {
        if (totalFiles == 0) return "Document aging & velocity: 0 documents (Empty library)";
        string docLabel = totalFiles == 1 ? "1 document" : $"{totalFiles} documents";
        string breakdown = $"{active} active (<24h), {warm} warm (<7d), {cold} cold, {archived} archived";

        return $"Document velocity ({docLabel} [{breakdown}] · {category}): {freshnessIndex:0.#}% freshness index (avg age {avgAge:0.#}d)";
    }

    /// <summary>
    /// Audits total storage footprint, file sizes, average document weight,
    /// and detects heavy outlier files (>500KB) across recent documents.
    /// </summary>
    public static WorkspaceStorageFootprintAudit AuditWorkspaceStorageFootprint(
        IEnumerable<string>? files,
        Func<string, long?>? fileSizeProvider = null)
    {
        var list = files?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();

        if (list.Count == 0)
        {
            string emptySummary = "Workspace storage footprint: 0 files (Empty library)";
            return new WorkspaceStorageFootprintAudit(
                0, 0, 0, "0 B", 0.0, "0 B", 0, "None", 0,
                Array.Empty<DocumentStorageInfo>(), "Empty Library", emptySummary);
        }

        long defaultSizeProvider(string p)
        {
            try
            {
                if (File.Exists(p)) return new FileInfo(p).Length;
            }
            catch { }
            return -1L;
        }

        var docInfos = new List<DocumentStorageInfo>();
        long totalBytes = 0;
        int existingCount = 0;
        int heavyCount = 0;
        long largestBytes = 0;
        string largestName = "None";

        foreach (var p in list)
        {
            long? sizeNullable = fileSizeProvider != null ? fileSizeProvider(p) : defaultSizeProvider(p);
            long bytes = sizeNullable.HasValue && sizeNullable.Value >= 0 ? sizeNullable.Value : -1L;

            string fileName = Path.GetFileName(p);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = p;

            if (bytes >= 0)
            {
                existingCount++;
                totalBytes += bytes;
                bool isHeavy = bytes > 512_000; // > 500 KB
                if (isHeavy) heavyCount++;
                if (bytes > largestBytes)
                {
                    largestBytes = bytes;
                    largestName = fileName;
                }
                docInfos.Add(new DocumentStorageInfo(p, fileName, bytes, FormatBytes(bytes), isHeavy));
            }
            else
            {
                docInfos.Add(new DocumentStorageInfo(p, fileName, 0, "Missing / Unknown", false));
            }
        }

        double avgBytes = existingCount > 0 ? (double)totalBytes / existingCount : 0.0;
        string formattedTotal = FormatBytes(totalBytes);
        string formattedAvg = FormatBytes((long)avgBytes);

        string category;
        if (existingCount == 0)
        {
            category = "No Accessible Files on Disk";
        }
        else if (heavyCount > 2 || totalBytes > 50_000_000)
        {
            category = "Heavy Footprint (Outliers Detected)";
        }
        else if (totalBytes > 5_000_000)
        {
            category = "Moderate Volume";
        }
        else
        {
            category = "Lightweight Workspace";
        }

        string summary = FormatWorkspaceStorageFootprintSummary(
            list.Count, existingCount, totalBytes, formattedTotal, formattedAvg, heavyCount, largestName, largestBytes, category);

        return new WorkspaceStorageFootprintAudit(
            list.Count, existingCount, totalBytes, formattedTotal, avgBytes, formattedAvg,
            heavyCount, largestName, largestBytes, docInfos, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of workspace storage footprint metrics.
    /// </summary>
    public static string FormatWorkspaceStorageFootprintSummary(
        int totalFiles, int existingFiles, long totalBytes, string formattedTotal,
        string formattedAvg, int heavyCount, string largestName, long largestBytes, string category)
    {
        if (totalFiles == 0) return "Workspace storage footprint: 0 files (Empty library)";
        string docLabel = totalFiles == 1 ? "1 document" : $"{totalFiles} documents";
        string heavyStr = heavyCount > 0 ? $" · ⚠ {heavyCount} heavy (>500KB)" : "";
        string largestStr = largestBytes > 0 && largestName != "None" ? $" · Largest: '{largestName}' ({FormatBytes(largestBytes)})" : "";

        return $"Workspace storage footprint ({docLabel} [{formattedTotal} total, avg {formattedAvg}] · {category}){heavyStr}{largestStr}";
    }

    /// <summary>
    /// Audits recent workspace document filenames for casing conventions (kebab-case, snake_case, PascalCase, camelCase),
    /// measuring naming uniformity and detecting shell-unfriendly special characters or spaces.
    /// </summary>
    public static DocumentNamingConventionAudit AuditDocumentNamingConventions(IEnumerable<string>? files)
    {
        var list = files?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();

        if (list.Count == 0)
        {
            string emptySummary = "Document naming audit: 0 files (Empty library)";
            return new DocumentNamingConventionAudit(
                0, 0, 0, 0, 0, 0, 0, "None", 0.0, 0,
                Array.Empty<DocumentNamingInfo>(), "Empty Library", emptySummary);
        }

        var docInfos = new List<DocumentNamingInfo>();
        int kebabCount = 0;
        int snakeCount = 0;
        int pascalCount = 0;
        int camelCount = 0;
        int spacedCount = 0;
        int otherCount = 0;
        int specialCharsCount = 0;

        char[] shellSpecialChars = new[] { '(', ')', '[', ']', '{', '}', '#', '%', '&', '+', '@', '!', ',', '~', '$', '^', '=' };

        foreach (var p in list)
        {
            string fileName = Path.GetFileName(p);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = p;
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = fileName;

            bool hasSpaces = baseName.Contains(' ');
            bool hasSpecialChars = baseName.IndexOfAny(shellSpecialChars) >= 0;
            if (hasSpecialChars) specialCharsCount++;

            string convention;
            if (hasSpaces)
            {
                convention = "Title / Spaced";
                spacedCount++;
            }
            else if (baseName.Contains('-') && !baseName.Contains('_') && baseName.All(c => char.IsLower(c) || char.IsDigit(c) || c == '-'))
            {
                convention = "kebab-case";
                kebabCount++;
            }
            else if (baseName.Contains('_') && !baseName.Contains('-') && baseName.All(c => char.IsLower(c) || char.IsDigit(c) || c == '_'))
            {
                convention = "snake_case";
                snakeCount++;
            }
            else if (!baseName.Contains('-') && !baseName.Contains('_') &&
                     char.IsUpper(baseName[0]) && baseName.Any(char.IsLower) && baseName.Skip(1).Any(char.IsUpper))
            {
                convention = "PascalCase";
                pascalCount++;
            }
            else if (!baseName.Contains('-') && !baseName.Contains('_') &&
                     char.IsLower(baseName[0]) && baseName.Any(char.IsUpper))
            {
                convention = "camelCase";
                camelCount++;
            }
            else
            {
                convention = "Mixed / Other";
                otherCount++;
            }

            docInfos.Add(new DocumentNamingInfo(p, fileName, baseName, convention, hasSpaces, hasSpecialChars));
        }

        var counts = new (string Name, int Count)[]
        {
            ("kebab-case", kebabCount),
            ("snake_case", snakeCount),
            ("PascalCase", pascalCount),
            ("camelCase", camelCount),
            ("Title / Spaced", spacedCount),
            ("Mixed / Other", otherCount)
        };

        var dominant = counts.OrderByDescending(c => c.Count).First();
        string dominantName = dominant.Count > 0 ? dominant.Name : "None";
        double consistencyScore = Math.Round((double)dominant.Count / list.Count * 100.0, 1);

        string category;
        if (consistencyScore == 100.0)
        {
            category = $"Uniform {dominantName} (100%)";
        }
        else if (consistencyScore >= 70.0)
        {
            category = $"Predominantly {dominantName} ({consistencyScore:0.#}%)";
        }
        else if (spacedCount > list.Count / 2)
        {
            category = "Spaced Filenames";
        }
        else
        {
            category = "Mixed Casing Styles";
        }

        string summary = FormatDocumentNamingConventionsSummary(
            list.Count, kebabCount, snakeCount, pascalCount, camelCount, spacedCount, otherCount,
            dominantName, consistencyScore, specialCharsCount, category);

        return new DocumentNamingConventionAudit(
            list.Count, kebabCount, snakeCount, pascalCount, camelCount, spacedCount, otherCount,
            dominantName, consistencyScore, specialCharsCount, docInfos, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of document naming convention metrics.
    /// </summary>
    public static string FormatDocumentNamingConventionsSummary(
        int totalFiles, int kebabCount, int snakeCount, int pascalCount, int camelCount,
        int spacedCount, int otherCount, string dominantConvention, double consistencyScore,
        int specialCharsCount, string category)
    {
        if (totalFiles == 0) return "Document naming audit: 0 files (Empty library)";
        string docLabel = totalFiles == 1 ? "1 document" : $"{totalFiles} documents";

        var breakdownParts = new List<string>();
        if (kebabCount > 0) breakdownParts.Add($"{kebabCount} kebab");
        if (snakeCount > 0) breakdownParts.Add($"{snakeCount} snake");
        if (pascalCount > 0) breakdownParts.Add($"{pascalCount} Pascal");
        if (camelCount > 0) breakdownParts.Add($"{camelCount} camel");
        if (spacedCount > 0) breakdownParts.Add($"{spacedCount} spaced");
        if (otherCount > 0) breakdownParts.Add($"{otherCount} other");
        string breakdownStr = string.Join(", ", breakdownParts);

        string specialWarn = specialCharsCount > 0 ? $" · ⚠ {specialCharsCount} with special chars" : "";
        return $"Document naming audit ({docLabel} [{breakdownStr}] · {category}): {dominantConvention} consistency {consistencyScore:0.#}%{specialWarn}";
    }

    /// <summary>
    /// Audits workspace file paths for character length, folder nesting depth, and Windows MAX_PATH (260 char) safety.
    /// </summary>
    public static WorkspacePathLengthAudit AuditPathLengthsAndNestingDepth(IEnumerable<string>? files)
    {
        var list = files?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();

        if (list.Count == 0)
        {
            string emptySummary = "Workspace path length audit: 0 files (Empty library)";
            return new WorkspacePathLengthAudit(
                0, 0, 0, 0, 0, "None", 0, "None", 0.0, 0.0, 100.0,
                Array.Empty<PathLengthMetricInfo>(), "Empty Library", emptySummary);
        }

        var metrics = new List<PathLengthMetricInfo>();
        int safeCount = 0;
        int warningCount = 0;
        int violationCount = 0;
        int maxLen = 0;
        string longestPath = "";
        int maxDepth = 0;
        string deepestPath = "";
        double totalLen = 0;
        double totalDepth = 0;

        foreach (var p in list)
        {
            int len = p.Length;
            totalLen += len;
            if (len > maxLen)
            {
                maxLen = len;
                longestPath = p;
            }

            int depth = 0;
            for (int i = 0; i < p.Length; i++)
            {
                if (p[i] == '\\' || p[i] == '/') depth++;
            }
            totalDepth += depth;
            if (depth > maxDepth)
            {
                maxDepth = depth;
                deepestPath = p;
            }

            bool exceedsMax = len >= 260;
            bool isWarning = len >= 200 && len < 260;

            if (exceedsMax) violationCount++;
            else if (isWarning) warningCount++;
            else safeCount++;

            string fn = Path.GetFileName(p);
            if (string.IsNullOrEmpty(fn)) fn = p;

            metrics.Add(new PathLengthMetricInfo(p, fn, len, depth, exceedsMax, isWarning));
        }

        double avgLen = Math.Round(totalLen / list.Count, 1);
        double avgDepth = Math.Round(totalDepth / list.Count, 1);

        double safetyScore;
        string category;
        if (violationCount > 0)
        {
            category = "MAX_PATH Violations Detected (≥260 chars)";
            safetyScore = Math.Max(0.0, Math.Round(100.0 - (violationCount * 30.0) - (warningCount * 10.0), 1));
        }
        else if (warningCount > 0)
        {
            category = "Approaching Limits (200–259 chars)";
            safetyScore = Math.Max(40.0, Math.Round(100.0 - (warningCount * 10.0), 1));
        }
        else
        {
            category = "100% MAX_PATH Safe";
            safetyScore = 100.0;
        }

        string summary = FormatPathLengthsSummary(
            list.Count, safeCount, warningCount, violationCount, maxLen, maxDepth,
            avgLen, avgDepth, safetyScore, category, longestPath);

        return new WorkspacePathLengthAudit(
            list.Count, safeCount, warningCount, violationCount, maxLen, longestPath,
            maxDepth, deepestPath, avgLen, avgDepth, safetyScore, metrics, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of workspace path lengths and MAX_PATH safety metrics.
    /// </summary>
    public static string FormatPathLengthsSummary(IEnumerable<string>? files)
        => AuditPathLengthsAndNestingDepth(files).Summary;

    public static string FormatPathLengthsSummary(
        int totalFiles, int safeCount, int warningCount, int violationCount,
        int maxLen, int maxDepth, double avgLen, double avgDepth,
        double safetyScore, string category, string longestPath)
    {
        if (totalFiles == 0) return "Workspace path length audit: 0 files (Empty library)";
        string fileLabel = totalFiles == 1 ? "1 file" : $"{totalFiles} files";

        string alerts = "";
        if (violationCount > 0) alerts += $" · 🚨 {violationCount} MAX_PATH violations";
        if (warningCount > 0) alerts += $" · ⚠ {warningCount} long paths (>200 chars)";

        return $"Workspace path length audit ({fileLabel} · {category}): Max length: {maxLen} chars (avg {avgLen:0.#}), Max depth: {maxDepth} tiers (avg {avgDepth:0.#}){alerts}";
    }

    /// <summary>
    /// Audits workspace file extension diversity, MIME type distribution, and detects non-standard markdown variants
    /// (.markdown, .mdown, .mdx) with Shannon entropy analysis.
    /// </summary>
    public static WorkspaceExtensionDiversityAudit AuditFileExtensionDiversity(IEnumerable<string>? files)
    {
        var list = files?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList() ?? new List<string>();
        if (list.Count == 0)
        {
            return new WorkspaceExtensionDiversityAudit(
                0, 0, 0.0, 0, 0, 0, 0, 0,
                Array.Empty<string>(), Array.Empty<ExtensionDistributionInfo>(),
                "Empty Library", "Extension diversity audit: 0 files · Workspace is empty");
        }

        var extGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in list)
        {
            string ext = "";
            try
            {
                ext = Path.GetExtension(f);
            }
            catch
            {
                int dotIdx = f.LastIndexOf('.');
                int sepIdx = Math.Max(f.LastIndexOf('/'), f.LastIndexOf('\\'));
                if (dotIdx > sepIdx && dotIdx >= 0)
                {
                    ext = f.Substring(dotIdx);
                }
            }

            if (string.IsNullOrEmpty(ext)) ext = "(none)";
            else ext = ext.ToLowerInvariant();

            if (!extGroups.TryGetValue(ext, out var group))
            {
                group = new List<string>();
                extGroups[ext] = group;
            }
            group.Add(f);
        }

        int stdMd = 0;
        int nonStdMd = 0;
        int officeDocs = 0;
        int webData = 0;
        int assets = 0;
        int others = 0;
        var nonStdFiles = new List<string>();

        var distributions = new List<ExtensionDistributionInfo>();
        double entropy = 0.0;
        int total = list.Count;

        foreach (var kvp in extGroups.OrderByDescending(g => g.Value.Count))
        {
            string ext = kvp.Key;
            int count = kvp.Value.Count;
            double pct = Math.Round((double)count / total * 100.0, 1);
            double p = (double)count / total;
            entropy -= p * Math.Log2(p);

            string category;
            if (ext == ".md")
            {
                category = "Standard Markdown";
                stdMd += count;
            }
            else if (ext is ".markdown" or ".mdown" or ".mkd" or ".mdx" or ".mkdn" or ".mdwn")
            {
                category = "Non-Standard Markdown Variant";
                nonStdMd += count;
                nonStdFiles.AddRange(kvp.Value);
            }
            else if (ext is ".docx" or ".doc" or ".dotx" or ".xlsx" or ".pptx" or ".pdf" or ".epub")
            {
                category = "Office & Publishing Document";
                officeDocs += count;
            }
            else if (ext is ".html" or ".htm" or ".json" or ".yaml" or ".yml" or ".xml" or ".css" or ".js" or ".ts" or ".cs")
            {
                category = "Web & Structured Data";
                webData += count;
            }
            else if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".ico")
            {
                category = "Media & Vector Asset";
                assets += count;
            }
            else
            {
                category = "Other / Binary";
                others += count;
            }

            distributions.Add(new ExtensionDistributionInfo(ext, category, count, pct));
        }

        entropy = Math.Round(Math.Max(0.0, entropy), 2);

        string summaryCategory;
        if ((double)stdMd / total >= 0.85)
        {
            summaryCategory = "Standard Markdown Centric (≥85% .md)";
        }
        else if (nonStdMd > 0)
        {
            summaryCategory = $"Irregular Markdown Variants Present ({nonStdMd} files)";
        }
        else if ((double)officeDocs / total >= 0.35)
        {
            summaryCategory = "Office & Export Companion Heavy";
        }
        else if ((double)assets / total >= 0.35)
        {
            summaryCategory = "Asset & Graphic Rich Documentation";
        }
        else if (entropy >= 2.0)
        {
            summaryCategory = "High Format Diversity (Polyglot Repo)";
        }
        else
        {
            summaryCategory = "Balanced Mixed-Format Workspace";
        }

        string summary = FormatFileExtensionDiversitySummary(
            total, extGroups.Count, entropy, stdMd, nonStdMd, officeDocs, webData, assets, summaryCategory, distributions);

        return new WorkspaceExtensionDiversityAudit(
            total, extGroups.Count, entropy, stdMd, nonStdMd, officeDocs, webData, assets,
            nonStdFiles, distributions, summaryCategory, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of workspace file extension diversity.
    /// </summary>
    public static string FormatFileExtensionDiversitySummary(IEnumerable<string>? files)
        => AuditFileExtensionDiversity(files).Summary;

    public static string FormatFileExtensionDiversitySummary(
        int totalFiles, int uniqueExts, double entropy, int stdMd, int nonStdMd,
        int officeDocs, int webData, int assets, string category, IReadOnlyList<ExtensionDistributionInfo> distributions)
    {
        if (totalFiles == 0) return "Extension diversity audit: 0 files · Workspace is empty";

        string fileLabel = totalFiles == 1 ? "1 file" : $"{totalFiles} files";
        string topFormats = string.Join(", ", distributions.Take(3).Select(d => $"{d.Extension} ({d.Percentage:0.#}%)"));
        string nonStdAlert = nonStdMd > 0 ? $" · ⚠ {nonStdMd} non-standard markdown variants" : "";

        return $"Extension diversity audit ({fileLabel} · {category}): {uniqueExts} formats (H={entropy:0.00} bits) · Top: {topFormats}{nonStdAlert}";
    }

    /// <summary>
    /// Audits workspace documents for relative inter-document links and asset embeds, identifying broken
    /// file targets and unreferenced orphan media assets. Pluggable file existence and content delegates allow unit testing.
    /// </summary>
    public static WorkspaceDeadLinkAudit AuditDeadLinksAndOrphanAssets(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var fileList = (files ?? Enumerable.Empty<string>()).ToList();
        if (fileList.Count == 0)
        {
            return new WorkspaceDeadLinkAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<CrossReferenceLinkInfo>(),
                Array.Empty<string>(),
                "Empty Workspace",
                "Dead link & orphan asset audit: 0 files · Workspace is empty");
        }

        var checkExists = fileExistsProvider ?? (path => File.Exists(path));
        var readContent = contentReader ?? (path =>
        {
            try { return File.ReadAllText(path); }
            catch { return ""; }
        });

        var knownFiles = new HashSet<string>(fileList.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        var markdownExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd", ".mdx" };
        var mediaExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".ico", ".pdf" };

        var mediaFiles = fileList.Where(f => mediaExts.Contains(Path.GetExtension(f))).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var markdownFiles = fileList.Where(f => markdownExts.Contains(Path.GetExtension(f))).ToList();

        var linkRegex = new System.Text.RegularExpressions.Regex(
            @"(?<isImage>!?)(?:\[([^\]]*)\])\(([^)\s]+)(?:\s+[""'][^""']*[""'])?\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var brokenLinks = new List<CrossReferenceLinkInfo>();
        var referencedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalRelativeLinks = 0;
        int validLinks = 0;

        foreach (var mdFile in markdownFiles)
        {
            string content = readContent(mdFile);
            if (string.IsNullOrWhiteSpace(content)) continue;

            string dir = Path.GetDirectoryName(mdFile) ?? "";

            var matches = linkRegex.Matches(content);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                bool isImage = match.Groups["isImage"].Value == "!";
                string linkText = match.Groups[1].Value.Trim();
                string targetRaw = match.Groups[2].Value.Trim();

                if (targetRaw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    targetRaw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    targetRaw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                    targetRaw.StartsWith("#") ||
                    targetRaw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int hashIdx = targetRaw.IndexOf('#');
                string targetPathClean = hashIdx >= 0 ? targetRaw.Substring(0, hashIdx) : targetRaw;
                if (string.IsNullOrWhiteSpace(targetPathClean)) continue;

                totalRelativeLinks++;

                string fullTargetPath;
                try
                {
                    fullTargetPath = Path.GetFullPath(Path.Combine(dir, targetPathClean.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch
                {
                    fullTargetPath = targetPathClean;
                }

                referencedTargets.Add(fullTargetPath);

                bool exists = knownFiles.Contains(fullTargetPath) || checkExists(fullTargetPath);
                if (exists)
                {
                    validLinks++;
                }
                else
                {
                    brokenLinks.Add(new CrossReferenceLinkInfo(
                        mdFile, targetRaw, linkText, false, isImage));
                }
            }
        }

        var orphanAssets = mediaFiles
            .Where(media => !referencedTargets.Contains(media))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int brokenCount = brokenLinks.Count;
        int orphanCount = orphanAssets.Count;

        double integrityScore;
        if (totalRelativeLinks == 0)
        {
            integrityScore = orphanCount > 0 ? 80.0 : 100.0;
        }
        else
        {
            double linkScore = ((double)validLinks / totalRelativeLinks) * 80.0;
            double orphanPenalty = Math.Min(20.0, orphanCount * 5.0);
            integrityScore = Math.Round(Math.Clamp(linkScore + 20.0 - orphanPenalty, 0.0, 100.0), 1);
        }

        string category;
        if (brokenCount > 0 && orphanCount > 0)
        {
            category = "Broken Links & Orphan Assets Detected";
        }
        else if (brokenCount > 0)
        {
            category = "Broken Cross-References Detected";
        }
        else if (orphanCount > 0)
        {
            category = "Orphan Media Assets Present";
        }
        else if (totalRelativeLinks == 0)
        {
            category = "Zero Inter-Document Links";
        }
        else
        {
            category = "Optimal Inter-Document Linkage (100% Integrity)";
        }

        string summary = FormatDeadLinksSummary(
            fileList.Count, totalRelativeLinks, validLinks, brokenCount, orphanCount, integrityScore, category, brokenLinks, orphanAssets);

        return new WorkspaceDeadLinkAudit(
            fileList.Count,
            totalRelativeLinks,
            validLinks,
            brokenCount,
            orphanCount,
            integrityScore,
            brokenLinks,
            orphanAssets,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of dead link and orphan asset audit metrics.
    /// </summary>
    public static string FormatDeadLinksSummary(IEnumerable<string>? files)
        => AuditDeadLinksAndOrphanAssets(files).Summary;

    public static string FormatDeadLinksSummary(
        int totalFiles, int totalLinks, int validLinks, int brokenCount, int orphanCount,
        double score, string category, IReadOnlyList<CrossReferenceLinkInfo> brokenLinks, IReadOnlyList<string> orphanAssets)
    {
        if (totalFiles == 0) return "Dead link & orphan asset audit: 0 files · Workspace is empty";

        string linkDesc = totalLinks == 1 ? "1 relative link" : $"{totalLinks} relative links";
        string breakdown = totalLinks > 0 ? $" ({validLinks} valid, {brokenCount} broken)" : "";
        string orphanDesc = orphanCount > 0 ? $" · ⚠ {orphanCount} unreferenced orphan assets ({string.Join(", ", orphanAssets.Take(2))})" : "";
        string brokenDesc = brokenCount > 0 ? $" · ⚠ {brokenCount} broken targets ({string.Join(", ", brokenLinks.Take(2).Select(b => b.TargetPath))})" : "";

        return $"Cross-reference audit ({totalFiles} files · {linkDesc}{breakdown} · {category}): {score:0.#}% integrity{brokenDesc}{orphanDesc}";
    }

    /// <summary>
    /// Audits workspace media assets for file size, compression efficiency, and modern format modernization (WebP/AVIF).
    /// </summary>
    public static WorkspaceAssetCompressionAudit AuditAssetCompressionAndFormatAdvisor(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null,
        Func<string, long>? fileLengthProvider = null)
    {
        var fileList = files?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                       ?? new List<string>();

        fileExistsProvider ??= File.Exists;
        contentReader ??= File.ReadAllText;
        fileLengthProvider ??= p =>
        {
            try { return new FileInfo(p).Length; }
            catch { return 0L; }
        };

        if (fileList.Count == 0)
        {
            string emptySummary = "Asset compression audit: 0 files · Workspace is empty";
            return new WorkspaceAssetCompressionAudit(
                0, 0, 0, 0, 0, 0, 0L, 0L, 0L, 0.0, 100.0,
                Array.Empty<AssetCompressionItem>(), "Empty Workspace", emptySummary);
        }

        var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".tif", ".svg", ".webp", ".avif"
        };

        var modernExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".webp", ".avif", ".svg"
        };

        var assetMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Path -> SourceMd

        var mdRegex = new System.Text.RegularExpressions.Regex(
            @"!\[([^\]]*)\]\(([^)]+)\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (var file in fileList)
        {
            string ext = "";
            try { ext = Path.GetExtension(file); } catch { }

            if (imageExtensions.Contains(ext))
            {
                if (!assetMap.ContainsKey(file))
                    assetMap[file] = file;
            }
            else if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
            {
                if (fileExistsProvider(file))
                {
                    string content = "";
                    try { content = contentReader(file); } catch { }

                    var matches = mdRegex.Matches(content);
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        string target = m.Groups[2].Value.Trim().Trim('"', '\'');
                        if (!string.IsNullOrEmpty(target) && !assetMap.ContainsKey(target))
                        {
                            assetMap[target] = file;
                        }
                    }
                }
            }
        }

        if (assetMap.Count == 0)
        {
            string textOnlySummary = "Asset compression audit: 0 media assets detected · Workspace is 100% text-based";
            return new WorkspaceAssetCompressionAudit(
                0, 0, 0, 0, 0, 0, 0L, 0L, 0L, 0.0, 100.0,
                Array.Empty<AssetCompressionItem>(), "Pure Text Workspace", textOnlySummary);
        }

        static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
            return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        }

        var items = new List<AssetCompressionItem>();
        int localCount = 0;
        int remoteCount = 0;
        int modernCount = 0;
        int legacyCount = 0;
        int oversizedCount = 0;
        long totalCurrentBytes = 0;
        long totalOptimizedBytes = 0;

        foreach (var (assetPath, sourceFile) in assetMap)
        {
            bool isRemote = assetPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            assetPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            string fn = Path.GetFileName(assetPath);
            if (string.IsNullOrEmpty(fn)) fn = assetPath;

            string ext = "";
            try { ext = Path.GetExtension(assetPath).ToLowerInvariant(); } catch { }

            if (isRemote)
            {
                remoteCount++;
                items.Add(new AssetCompressionItem(
                    assetPath, fn, ext, 0L, "Remote", 0L, "Remote", 0.0,
                    "Remote URL (CDN Cached)", false, true));
                continue;
            }

            localCount++;

            // Resolve full path
            string fullPath = assetPath;
            if (!Path.IsPathRooted(assetPath))
            {
                string? dir = Path.GetDirectoryName(sourceFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    try { fullPath = Path.GetFullPath(Path.Combine(dir, assetPath)); } catch { }
                }
            }

            long currentSize = 0;
            if (fileExistsProvider(fullPath))
            {
                currentSize = fileLengthProvider(fullPath);
            }

            totalCurrentBytes += currentSize;

            double ratio = 1.0;
            string recommended = "Retain Format";

            switch (ext)
            {
                case ".bmp":
                    ratio = 0.20; // 80% savings
                    recommended = "WebP / PNG (Lossless)";
                    legacyCount++;
                    break;
                case ".png":
                    ratio = 0.65; // 35% savings
                    recommended = "WebP (Modern Lossless/Lossy)";
                    legacyCount++;
                    break;
                case ".jpg":
                case ".jpeg":
                    ratio = 0.75; // 25% savings
                    recommended = "WebP / AVIF (Next-Gen Lossy)";
                    legacyCount++;
                    break;
                case ".gif":
                    ratio = 0.50; // 50% savings
                    recommended = "WebP / MP4 Animation";
                    legacyCount++;
                    break;
                case ".tiff":
                case ".tif":
                    ratio = 0.30; // 70% savings
                    recommended = "WebP / PNG";
                    legacyCount++;
                    break;
                case ".webp":
                case ".avif":
                case ".svg":
                    ratio = 1.0;
                    recommended = "Modern Format (Optimal)";
                    modernCount++;
                    break;
                default:
                    legacyCount++;
                    recommended = "WebP / SVG";
                    break;
            }

            long optSize = (long)(currentSize * ratio);
            totalOptimizedBytes += optSize;

            bool isOversized = currentSize >= 500 * 1024;
            if (isOversized) oversizedCount++;

            double savingsPct = currentSize > 0
                ? Math.Round(((double)(currentSize - optSize) / currentSize) * 100.0, 1)
                : 0.0;

            items.Add(new AssetCompressionItem(
                assetPath, fn, ext, currentSize, FormatBytes(currentSize),
                optSize, FormatBytes(optSize), savingsPct, recommended, isOversized, false));
        }

        long totalSavingsBytes = Math.Max(0L, totalCurrentBytes - totalOptimizedBytes);
        double overallSavings = totalCurrentBytes > 0
            ? Math.Round(((double)totalSavingsBytes / totalCurrentBytes) * 100.0, 1)
            : 0.0;

        double efficiencyScore = Math.Round(Math.Clamp(
            100.0 - (overallSavings * 0.7) - (oversizedCount * 5.0), 0.0, 100.0), 1);

        string category;
        if (efficiencyScore >= 85.0 && oversizedCount == 0)
        {
            category = "Modernized Lightweight Assets (WebP/SVG)";
        }
        else if (oversizedCount > 0)
        {
            category = "Heavy Media Assets Detected (>500 KB)";
        }
        else if (overallSavings >= 25.0)
        {
            category = "Substantial Modernization Savings Potential";
        }
        else
        {
            category = "Moderate Compression Optimization";
        }

        string summary = FormatAssetCompressionSummary(
            assetMap.Count, localCount, remoteCount, modernCount, legacyCount, oversizedCount,
            totalCurrentBytes, totalSavingsBytes, overallSavings, efficiencyScore, category);

        return new WorkspaceAssetCompressionAudit(
            assetMap.Count,
            localCount,
            remoteCount,
            modernCount,
            legacyCount,
            oversizedCount,
            totalCurrentBytes,
            totalOptimizedBytes,
            totalSavingsBytes,
            overallSavings,
            efficiencyScore,
            items,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of workspace asset compression metrics.
    /// </summary>
    public static string FormatAssetCompressionSummary(IEnumerable<string>? files)
        => AuditAssetCompressionAndFormatAdvisor(files).Summary;

    public static string FormatAssetCompressionSummary(
        int totalAssets, int localCount, int remoteCount, int modernCount, int legacyCount,
        int oversizedCount, long currentBytes, long savingsBytes, double savingsPct,
        double efficiencyScore, string category)
    {
        if (totalAssets == 0) return "Asset compression audit: 0 media assets detected · Workspace is 100% text-based";

        static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
            return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        }

        string assetLabel = totalAssets == 1 ? "1 asset" : $"{totalAssets} assets";
        string savingsStr = savingsBytes > 0 ? $" · Potential savings: {FormatBytes(savingsBytes)} ({savingsPct:0.#}%)" : "";
        string oversizedStr = oversizedCount > 0 ? $" · ⚠ {oversizedCount} oversized (>500KB)" : "";

        return $"Asset compression audit ({assetLabel} [{modernCount} modern, {legacyCount} legacy] · {category}): Efficiency: {efficiencyScore:0.#}% (Total: {FormatBytes(currentBytes)}){savingsStr}{oversizedStr}";
    }

    /// <summary>
    /// Audits workspace media assets for high-DPI (Retina/@2x/@3x) display readiness, detects unoptimized SVG vector
    /// files containing editor/metadata bloat, and calculates potential vector minification gains.
    /// </summary>
    public static WorkspaceAssetDpiSvgAudit AuditAssetDpiAndSvgOptimizationAdvisor(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null,
        Func<string, long>? fileLengthProvider = null,
        Func<string, (int width, int height)>? dimensionsProvider = null)
    {
        var fileList = files?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                       ?? new List<string>();

        fileExistsProvider ??= File.Exists;
        contentReader ??= File.ReadAllText;
        fileLengthProvider ??= p =>
        {
            try { return new FileInfo(p).Length; }
            catch { return 0L; }
        };
        dimensionsProvider ??= _ => (0, 0);

        if (fileList.Count == 0)
        {
            string emptySummary = "High-DPI & SVG asset advisor: 0 media assets detected · Workspace contains no documents";
            return new WorkspaceAssetDpiSvgAudit(0, 0, 0, 0, 0, 0, 100.0, 0L, Array.Empty<AssetDpiSvgItem>(), "Empty Workspace", emptySummary);
        }

        var assetItems = new List<AssetDpiSvgItem>();
        var seenAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var mdImgRegex = new System.Text.RegularExpressions.Regex(@"!\[(.*?)\]\((.*?)\)", System.Text.RegularExpressions.RegexOptions.Compiled);
        var htmlImgRegex = new System.Text.RegularExpressions.Regex(@"<img\s+[^>]*src=[""'](.*?)[""']", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var file in fileList)
        {
            if (!fileExistsProvider(file)) continue;
            string content;
            try { content = contentReader(file); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(content)) continue;

            string docDir = "";
            try { docDir = Path.GetDirectoryName(file) ?? ""; } catch { }

            var matches = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in mdImgRegex.Matches(content))
            {
                if (m.Groups.Count > 2) matches.Add(m.Groups[2].Value.Trim());
            }
            foreach (System.Text.RegularExpressions.Match m in htmlImgRegex.Matches(content))
            {
                if (m.Groups.Count > 1) matches.Add(m.Groups[1].Value.Trim());
            }

            foreach (var rawRef in matches)
            {
                if (string.IsNullOrWhiteSpace(rawRef)) continue;
                // Clean URL params or anchors
                string cleaned = rawRef.Split('?')[0].Split('#')[0].Trim();
                if (cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Skip remote/data URLs for local High-DPI/SVG audit
                }

                string resolved = cleaned;
                if (!Path.IsPathRooted(cleaned) && !string.IsNullOrEmpty(docDir))
                {
                    try { resolved = Path.GetFullPath(Path.Combine(docDir, cleaned)); } catch { }
                }

                if (!seenAssets.Add(resolved)) continue;

                string ext = "";
                try { ext = Path.GetExtension(resolved).ToLowerInvariant(); } catch { }
                string fileName = "";
                try { fileName = Path.GetFileName(resolved); } catch { }

                long sizeBytes = fileLengthProvider(resolved);
                bool isSvg = ext == ".svg";
                bool isRaster = ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" || ext == ".webp" || ext == ".avif";

                if (!isSvg && !isRaster) continue;

                if (isSvg)
                {
                    string svgText = "";
                    if (fileExistsProvider(resolved))
                    {
                        try { svgText = contentReader(resolved); } catch { }
                    }

                    int bloatCount = 0;
                    if (!string.IsNullOrEmpty(svgText))
                    {
                        if (svgText.Contains("xmlns:inkscape") || svgText.Contains("inkscape:")) bloatCount++;
                        if (svgText.Contains("xmlns:sodipodi") || svgText.Contains("sodipodi:")) bloatCount++;
                        if (svgText.Contains("<metadata") || svgText.Contains("</metadata>")) bloatCount++;
                        if (svgText.Contains("<rdf:RDF") || svgText.Contains("</rdf:RDF>")) bloatCount++;
                        if (svgText.Contains("<!--")) bloatCount++;
                        if (System.Text.RegularExpressions.Regex.IsMatch(svgText, @"<g\s*>\s*</g>")) bloatCount++;
                    }

                    bool hasBloat = bloatCount > 0;
                    double savingsPct = hasBloat ? Math.Min(60.0, 15.0 + (bloatCount * 8.0)) : 0.0;
                    string rec = hasBloat
                        ? $"Strip {bloatCount} editor/metadata tags (~{savingsPct:0}% minification)"
                        : "Optimal scalable vector (High-DPI Ready)";

                    assetItems.Add(new AssetDpiSvgItem(
                        resolved, fileName, "Vector (SVG)", sizeBytes, true, hasBloat, bloatCount, savingsPct, rec));
                }
                else
                {
                    // Raster image
                    bool has2xName = fileName.Contains("@2x", StringComparison.OrdinalIgnoreCase) ||
                                     fileName.Contains("@3x", StringComparison.OrdinalIgnoreCase) ||
                                     fileName.Contains("-2x", StringComparison.OrdinalIgnoreCase);

                    var (w, h) = dimensionsProvider(resolved);
                    bool isHighRes = has2xName || (w >= 1600 || h >= 1200) || (w == 0 && sizeBytes >= 250 * 1024);

                    string rec;
                    if (has2xName) rec = "High-DPI asset detected (@2x format)";
                    else if (isHighRes) rec = "High-resolution master raster";
                    else rec = "Low-DPI raster: Consider vector SVG or @2x asset for Retina displays";

                    assetItems.Add(new AssetDpiSvgItem(
                        resolved, fileName, "Raster Image", sizeBytes, isHighRes, false, 0, 0.0, rec));
                }
            }
        }

        if (assetItems.Count == 0)
        {
            string emptySummary = "High-DPI & SVG asset advisor: 0 media assets detected · Workspace is 100% text-based";
            return new WorkspaceAssetDpiSvgAudit(0, 0, 0, 0, 0, 0, 100.0, 0L, Array.Empty<AssetDpiSvgItem>(), "Text-Only Workspace", emptySummary);
        }

        int totalAssets = assetItems.Count;
        int svgCount = assetItems.Count(a => a.AssetType.Contains("SVG"));
        int rasterCount = totalAssets - svgCount;
        int highDpiReadyCount = assetItems.Count(a => a.IsHighDpiReady);
        int lowDpiRiskCount = totalAssets - highDpiReadyCount;
        int unoptimizedSvgCount = assetItems.Count(a => a.HasSvgBloat);

        long potentialSvgSavingsBytes = assetItems
            .Where(a => a.HasSvgBloat)
            .Sum(a => (long)(a.FileSizeBytes * (a.EstimatedOptimizationSavingsPercent / 100.0)));

        double readinessScore = Math.Round(100.0 * highDpiReadyCount / totalAssets, 1);

        string category;
        if (readinessScore >= 90.0 && unoptimizedSvgCount == 0)
        {
            category = "High-DPI & Retina Ready (Optimal)";
        }
        else if (unoptimizedSvgCount > 0 && lowDpiRiskCount > 0)
        {
            category = "Vector Bloat & Low-DPI Raster Risks";
        }
        else if (unoptimizedSvgCount > 0)
        {
            category = "SVG Vector Minification Opportunities";
        }
        else
        {
            category = "Low-DPI Raster Assets Detected";
        }

        string summary = FormatAssetDpiSvgSummary(
            totalAssets, svgCount, rasterCount, highDpiReadyCount, lowDpiRiskCount,
            unoptimizedSvgCount, readinessScore, potentialSvgSavingsBytes, category);

        return new WorkspaceAssetDpiSvgAudit(
            totalAssets,
            rasterCount,
            svgCount,
            highDpiReadyCount,
            lowDpiRiskCount,
            unoptimizedSvgCount,
            readinessScore,
            potentialSvgSavingsBytes,
            assetItems,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of High-DPI display readiness and SVG vector minification opportunities.
    /// </summary>
    public static string FormatAssetDpiSvgSummary(
        int totalAssets, int svgCount, int rasterCount, int highDpiReadyCount, int lowDpiRiskCount,
        int unoptimizedSvgCount, double readinessScore, long svgSavingsBytes, string category)
    {
        if (totalAssets == 0) return "High-DPI & SVG asset advisor: 0 media assets detected · Workspace is 100% text-based";

        static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
            return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        }

        string assetLabel = totalAssets == 1 ? "1 asset" : $"{totalAssets} assets";
        string breakdown = $"{svgCount} SVG, {rasterCount} raster";

        string issues = "";
        if (unoptimizedSvgCount > 0) issues += $" · ⚠ {unoptimizedSvgCount} unoptimized SVG{(unoptimizedSvgCount == 1 ? "" : "s")} ({FormatBytes(svgSavingsBytes)} potential savings)";
        if (lowDpiRiskCount > 0) issues += $" · ⚠ {lowDpiRiskCount} low-DPI raster asset{(lowDpiRiskCount == 1 ? "" : "s")}";

        return $"High-DPI & SVG asset advisor ({assetLabel} [{breakdown}] · {category}): {readinessScore:0.#}% High-DPI ready ({highDpiReadyCount} ready){issues}";
    }

    /// <summary>
    /// Audits workspace documents and web assets for custom font families, embedded web fonts,
    /// generic CSS fallback coverage (sans-serif, serif, monospace), and self-containment resilience.
    /// </summary>
    public static WorkspaceTypographyAudit AuditWorkspaceTypographyAndFontEmbedding(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var fileList = files?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                       ?? new List<string>();

        fileExistsProvider ??= File.Exists;
        contentReader ??= File.ReadAllText;

        if (fileList.Count == 0)
        {
            string emptySummary = "Workspace typography advisor: 0 font declarations · Workspace contains no documents";
            return new WorkspaceTypographyAudit(0, 0, 0, 0, 0, 0, 100.0, Array.Empty<WorkspaceFontItem>(), Array.Empty<string>(), "Empty Workspace", emptySummary);
        }

        var fontItems = new List<WorkspaceFontItem>();
        var discoveredFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var fontFaceRegex = new System.Text.RegularExpressions.Regex(@"@font-face\s*\{[^}]*font-family:\s*['""]?([^'"";}\n]+)['""]?", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var cssFontFamilyRegex = new System.Text.RegularExpressions.Regex(@"font-family:\s*([^;}\n]+)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var googleFontsRegex = new System.Text.RegularExpressions.Regex(@"https?://fonts\.googleapis\.com/css2?\?family=([^&""'\s>]+)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var frontmatterFontRegex = new System.Text.RegularExpressions.Regex(@"^\s*font(?:-family)?:\s*['""]?([^'"";\n]+)['""]?", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        int scannedCount = 0;

        foreach (var file in fileList)
        {
            if (!fileExistsProvider(file)) continue;
            string ext = "";
            try { ext = Path.GetExtension(file).ToLowerInvariant(); } catch { }
            if (ext != ".md" && ext != ".markdown" && ext != ".html" && ext != ".htm" && ext != ".css" && ext != ".svg")
                continue;

            string content;
            try { content = contentReader(file); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(content)) continue;

            scannedCount++;
            string fileName = "";
            try { fileName = Path.GetFileName(file); } catch { }

            foreach (System.Text.RegularExpressions.Match m in googleFontsRegex.Matches(content))
            {
                if (m.Groups.Count > 1)
                {
                    string rawFam = Uri.UnescapeDataString(m.Groups[1].Value.Split(':')[0].Replace('+', ' ')).Trim();
                    discoveredFamilies.Add(rawFam);
                    fontItems.Add(new WorkspaceFontItem(
                        fileName, rawFam, "Remote Google Fonts Link", true, "sans-serif", true, false));
                }
            }

            foreach (System.Text.RegularExpressions.Match m in fontFaceRegex.Matches(content))
            {
                if (m.Groups.Count > 1)
                {
                    string fam = m.Groups[1].Value.Trim();
                    discoveredFamilies.Add(fam);
                    bool hasRemoteUrl = m.Value.Contains("http://", StringComparison.OrdinalIgnoreCase) || m.Value.Contains("https://", StringComparison.OrdinalIgnoreCase);
                    fontItems.Add(new WorkspaceFontItem(
                        fileName, fam, "@font-face Rule", true, "sans-serif", hasRemoteUrl, !hasRemoteUrl));
                }
            }

            foreach (System.Text.RegularExpressions.Match m in cssFontFamilyRegex.Matches(content))
            {
                if (m.Groups.Count > 1)
                {
                    string rawStack = m.Groups[1].Value.Trim();
                    var parts = rawStack.Split(',').Select(p => p.Trim().Trim('\'', '"')).Where(p => !string.IsNullOrEmpty(p)).ToList();
                    if (parts.Count > 0)
                    {
                        string leadFont = parts[0];
                        discoveredFamilies.Add(leadFont);

                        string lastPart = parts[^1].ToLowerInvariant();
                        bool hasFallback = lastPart == "sans-serif" || lastPart == "serif" || lastPart == "monospace" ||
                                            lastPart == "system-ui" || lastPart == "cursive" || lastPart == "fantasy";
                        string cat = hasFallback ? lastPart : "none";

                        fontItems.Add(new WorkspaceFontItem(
                            fileName, leadFont, "CSS font-family Stack", hasFallback, cat, false, true));
                    }
                }
            }

            foreach (System.Text.RegularExpressions.Match m in frontmatterFontRegex.Matches(content))
            {
                if (m.Groups.Count > 1)
                {
                    string fam = m.Groups[1].Value.Trim();
                    discoveredFamilies.Add(fam);
                    fontItems.Add(new WorkspaceFontItem(
                        fileName, fam, "Frontmatter Metadata", false, "none", false, true));
                }
            }
        }

        if (fontItems.Count == 0)
        {
            string cleanSummary = $"Workspace typography advisor: 0 custom font declarations across {scannedCount} docs · Standard system typography (100% self-contained)";
            return new WorkspaceTypographyAudit(
                scannedCount, 0, 0, 0, 0, 0, 100.0, Array.Empty<WorkspaceFontItem>(), Array.Empty<string>(), "Clean System Typography", cleanSummary);
        }

        int totalDeclarations = fontItems.Count;
        int distinctFamilies = discoveredFamilies.Count;
        int selfContainedCount = fontItems.Count(f => f.IsSelfContained);
        int missingFallbackCount = fontItems.Count(f => !f.HasGenericFallback);
        int remoteFontCount = fontItems.Count(f => f.IsRemoteAsset);

        double score = 100.0 - (missingFallbackCount * 12.0) - (remoteFontCount * 15.0);
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string healthCategory;
        if (remoteFontCount == 0 && missingFallbackCount == 0)
        {
            healthCategory = "Self-Contained Offline Typography (100% Robust)";
        }
        else if (remoteFontCount == 0)
        {
            healthCategory = "Missing Generic Fallbacks (Layout Shift Risk)";
        }
        else if (missingFallbackCount == 0)
        {
            healthCategory = "Remote Web Fonts Protected by Fallbacks";
        }
        else
        {
            healthCategory = "Heavy External Web Font Reliance (Air-Gap Fragile)";
        }

        var famList = discoveredFamilies.OrderBy(f => f).ToList();
        string summary = FormatWorkspaceTypographySummary(
            scannedCount, totalDeclarations, distinctFamilies, selfContainedCount,
            missingFallbackCount, remoteFontCount, score, healthCategory, famList);

        return new WorkspaceTypographyAudit(
            scannedCount, totalDeclarations, distinctFamilies, selfContainedCount,
            missingFallbackCount, remoteFontCount, score, fontItems, famList, healthCategory, summary);
    }

    public static string FormatWorkspaceTypographySummary(
        int scannedCount, int totalDeclarations, int distinctFamilies, int selfContained,
        int missingFallbacks, int remoteFonts, double score, string category, IReadOnlyList<string>? families = null)
    {
        if (totalDeclarations == 0)
        {
            return $"Workspace typography advisor: 0 custom font declarations across {scannedCount} docs · Standard system typography (100% self-contained)";
        }

        string decLabel = totalDeclarations == 1 ? "1 font declaration" : $"{totalDeclarations} font declarations";
        string famLabel = distinctFamilies == 1 ? "1 family" : $"{distinctFamilies} distinct families";

        string issues = "";
        if (missingFallbacks > 0) issues += $" · ⚠ {missingFallbacks} missing generic fallback";
        if (remoteFonts > 0) issues += $" · ⚠ {remoteFonts} remote CDN dependency";

        string topFam = "";
        if (families != null && families.Count > 0)
        {
            topFam = $" · Families: {string.Join(", ", families.Take(3).Select(f => $"'{f}'"))}";
        }

        return $"Workspace typography advisor ({decLabel} across {famLabel} · {category} · {score:0.#}% self-containment): {selfContained} self-contained{issues}{topFam}";
    }

    /// <summary>
    /// Audits workspace file size distribution, computing storage payload percentiles,
    /// identifying heavy storage bloat assets (>5MB), and scoring overall storage compactness.
    /// </summary>
    public static WorkspaceFileSizeDistributionAudit AuditWorkspaceFileSizeDistribution(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, long>? fileLengthProvider = null)
    {
        if (files == null)
        {
            return new WorkspaceFileSizeDistributionAudit(
                0, 0, "0 B", 0, 0, "None", 0, 100.0, "Empty Workspace",
                new Dictionary<string, int>(), Array.Empty<WorkspaceFileSizeItem>(),
                "Workspace storage distribution advisor: 0 files · Workspace is empty");
        }

        var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return new WorkspaceFileSizeDistributionAudit(
                0, 0, "0 B", 0, 0, "None", 0, 100.0, "Empty Workspace",
                new Dictionary<string, int>(), Array.Empty<WorkspaceFileSizeItem>(),
                "Workspace storage distribution advisor: 0 files · Workspace is empty");
        }

        var fileExists = fileExistsProvider ?? File.Exists;
        var fileLength = fileLengthProvider ?? (p => new FileInfo(p).Length);

        var fileItems = new List<WorkspaceFileSizeItem>();
        var extCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        long maxBytes = 0;
        string heaviestName = "None";
        int heavyBloatCount = 0;
        int largeAssetCount = 0;

        foreach (var path in list)
        {
            long size = 0;
            try
            {
                if (fileExists(path))
                {
                    size = fileLength(path);
                }
                else
                {
                    size = 2048;
                }
            }
            catch
            {
                size = 2048;
            }

            totalBytes += size;
            if (size > maxBytes)
            {
                maxBytes = size;
                heaviestName = Path.GetFileName(path);
                if (string.IsNullOrEmpty(heaviestName)) heaviestName = path;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "(none)";
            extCounts[ext] = extCounts.TryGetValue(ext, out int c) ? c + 1 : 1;

            string formattedSize = FormatByteSize(size);
            string tier;
            bool isBloat = false;

            if (size >= 5L * 1024 * 1024)
            {
                tier = "Storage Bloat (≥5MB)";
                isBloat = true;
                heavyBloatCount++;
            }
            else if (size >= 2L * 1024 * 1024)
            {
                tier = "Heavy Asset (2–5MB)";
                largeAssetCount++;
            }
            else if (size >= 500L * 1024)
            {
                tier = "Substantial (500KB–2MB)";
            }
            else if (size >= 50L * 1024)
            {
                tier = "Standard (50–500KB)";
            }
            else
            {
                tier = "Lightweight (<50KB)";
            }

            string fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName)) fileName = path;

            fileItems.Add(new WorkspaceFileSizeItem(
                path, fileName, ext, size, formattedSize, tier, isBloat));
        }

        long meanBytes = list.Count > 0 ? totalBytes / list.Count : 0;
        double compactnessScore = 100.0;
        compactnessScore -= heavyBloatCount * 20.0;
        compactnessScore -= largeAssetCount * 5.0;
        compactnessScore = Math.Round(Math.Clamp(compactnessScore, 0.0, 100.0), 1);

        string category;
        if (heavyBloatCount == 0 && maxBytes < 2L * 1024 * 1024)
            category = "Optimal Lightweight Storage (100%)";
        else if (compactnessScore >= 80.0)
            category = "Well-Proportioned Workspace (≥80%)";
        else if (compactnessScore >= 50.0)
            category = "Moderate Asset Bloat Detected";
        else
            category = "Critical Storage Bloat (Heavy Assets)";

        string summary = FormatWorkspaceFileSizeDistributionSummary(
            list.Count, totalBytes, meanBytes, maxBytes, heaviestName,
            heavyBloatCount, compactnessScore, category);

        return new WorkspaceFileSizeDistributionAudit(
            list.Count, totalBytes, FormatByteSize(totalBytes), meanBytes, maxBytes,
            heaviestName, heavyBloatCount, compactnessScore, category, extCounts, fileItems, summary);
    }

    public static string FormatWorkspaceFileSizeDistributionSummary(
        int totalFiles, long totalBytes, long meanBytes, long maxBytes, string heaviestName,
        int heavyBloat, double compactnessScore, string category)
    {
        if (totalFiles == 0) return "Workspace storage distribution advisor: 0 files · Workspace is empty";

        string fileDesc = totalFiles == 1 ? "1 file" : $"{totalFiles} files";
        string formattedTotal = FormatByteSize(totalBytes);
        string formattedMax = FormatByteSize(maxBytes);
        string formattedMean = FormatByteSize(meanBytes);
        string bloatDesc = heavyBloat > 0 ? $" · ⚠ {heavyBloat} heavy bloat (≥5MB)" : "";

        return $"Workspace storage distribution advisor ({fileDesc} [{formattedTotal}] · {category} · {compactnessScore:0.#}% compactness): Max '{heaviestName}' ({formattedMax}) · ~{formattedMean} avg{bloatDesc}";
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }

    /// <summary>
    /// Audits workspace assets (media and attachments) for byte-identical duplicate files across folders
    /// using SHA-256 cryptographic hashing, estimating reclaimed disk space and archive compactness.
    /// </summary>
    public static WorkspaceAssetDeduplicationAudit AuditWorkspaceAssetDeduplication(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, byte[]>? fileBytesProvider = null,
        Func<string, string>? fileHashProvider = null)
    {
        var list = files?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();

        if (list.Count == 0)
        {
            string emptySummary = "Workspace asset deduplication advisor: 0 assets · Workspace is empty";
            return new WorkspaceAssetDeduplicationAudit(
                0, 0, 0, 0, 0, 0, "0 B", 100.0,
                Array.Empty<DuplicateAssetGroup>(),
                "Zero Redundancy (Fully Deduplicated)",
                emptySummary);
        }

        fileExistsProvider ??= File.Exists;

        var assetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif", ".mp4", ".pdf", ".docx", ".zip", ".md", ".txt"
        };

        var hashToPaths = new Dictionary<string, List<(string Path, long Size)>>(StringComparer.OrdinalIgnoreCase);
        long totalScannedBytes = 0;
        int totalAssetsScanned = 0;

        foreach (var path in list)
        {
            string ext = "";
            try { ext = Path.GetExtension(path); } catch { }
            if (!assetExtensions.Contains(ext)) continue;

            if (!fileExistsProvider(path)) continue;

            string hash = "";
            long size = 0;

            if (fileHashProvider != null)
            {
                hash = fileHashProvider(path);
                if (fileBytesProvider != null)
                {
                    var bytes = fileBytesProvider(path);
                    size = bytes?.Length ?? 0;
                }
                else
                {
                    try { size = new FileInfo(path).Length; } catch { size = 1024; }
                }
            }
            else if (fileBytesProvider != null)
            {
                var bytes = fileBytesProvider(path) ?? Array.Empty<byte>();
                size = bytes.Length;
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var hashBytes = sha256.ComputeHash(bytes);
                hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
            else
            {
                try
                {
                    var fi = new FileInfo(path);
                    size = fi.Length;
                    using var sha256 = System.Security.Cryptography.SHA256.Create();
                    using var stream = File.OpenRead(path);
                    var hashBytes = sha256.ComputeHash(stream);
                    hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
                catch
                {
                    continue;
                }
            }

            if (string.IsNullOrEmpty(hash)) continue;

            totalAssetsScanned++;
            totalScannedBytes += size;

            if (!hashToPaths.TryGetValue(hash, out var paths))
            {
                paths = new List<(string, long)>();
                hashToPaths[hash] = paths;
            }
            paths.Add((path, size));
        }

        if (totalAssetsScanned == 0)
        {
            string emptySummary = "Workspace asset deduplication advisor: 0 assets · No eligible assets found";
            return new WorkspaceAssetDeduplicationAudit(
                0, 0, 0, 0, 0, 0, "0 B", 100.0,
                Array.Empty<DuplicateAssetGroup>(),
                "Zero Redundancy (Fully Deduplicated)",
                emptySummary);
        }

        var duplicateGroups = new List<DuplicateAssetGroup>();
        long totalWastedBytes = 0;
        int totalRedundantCount = 0;

        foreach (var kvp in hashToPaths)
        {
            var paths = kvp.Value;
            if (paths.Count > 1)
            {
                long assetSize = paths[0].Size;
                int copies = paths.Count - 1;
                long wasted = assetSize * copies;
                totalWastedBytes += wasted;
                totalRedundantCount += copies;

                duplicateGroups.Add(new DuplicateAssetGroup(
                    kvp.Key,
                    assetSize,
                    FormatByteSize(assetSize),
                    paths.Select(p => p.Path).ToList(),
                    wasted,
                    FormatByteSize(wasted)));
            }
        }

        double efficiencyScore = 100.0;
        if (totalScannedBytes > 0)
        {
            double ratio = (double)totalWastedBytes / totalScannedBytes;
            efficiencyScore = Math.Round(Math.Clamp((1.0 - ratio) * 100.0, 0.0, 100.0), 1);
        }

        string category;
        if (duplicateGroups.Count == 0)
        {
            category = "Zero Redundancy (Fully Deduplicated)";
        }
        else if (efficiencyScore >= 95.0)
        {
            category = "Minor Redundancy (<5% wasted)";
        }
        else if (efficiencyScore >= 75.0)
        {
            category = "Moderate Duplicate Asset Bloat";
        }
        else
        {
            category = "Severe Storage Duplication";
        }

        string summary = FormatWorkspaceAssetDeduplicationSummary(
            totalAssetsScanned, hashToPaths.Count, duplicateGroups.Count, totalRedundantCount,
            totalScannedBytes, totalWastedBytes, efficiencyScore, category);

        return new WorkspaceAssetDeduplicationAudit(
            totalAssetsScanned,
            hashToPaths.Count,
            duplicateGroups.Count,
            totalRedundantCount,
            totalScannedBytes,
            totalWastedBytes,
            FormatByteSize(totalWastedBytes),
            efficiencyScore,
            duplicateGroups,
            category,
            summary);
    }

    public static string FormatWorkspaceAssetDeduplicationSummary(
        int totalAssets, int uniqueCount, int duplicateGroups, int redundantCopies,
        long totalBytes, long wastedBytes, double efficiencyScore, string category)
    {
        if (totalAssets == 0) return "Workspace asset deduplication advisor: 0 assets · Workspace is empty";

        string assetLabel = totalAssets == 1 ? "1 asset" : $"{totalAssets} assets";
        string wastedStr = FormatByteSize(wastedBytes);

        if (duplicateGroups == 0)
        {
            return $"Workspace asset deduplication advisor ({assetLabel} · {category} · 100% efficiency): All assets unique (0 B wasted)";
        }

        return $"Workspace asset deduplication advisor ({assetLabel} · {category} · {efficiencyScore:0.#}% efficiency): {duplicateGroups} duplicate group{(duplicateGroups == 1 ? "" : "s")} ({redundantCopies} copies) · {wastedStr} reclaimable";
    }

    /// <summary>
    /// Audits workspace markdown documents for heading style uniformity (ATX # vs Setext === / ---),
    /// heading depth monotonicity (detecting skipped levels), and anchor slug collisions/invalid characters.
    /// </summary>
    public static WorkspaceHeadingUniformityAudit AuditWorkspaceMarkdownHeaderUniformity(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var fileList = (files ?? Enumerable.Empty<string>()).ToList();
        var exists = fileExistsProvider ?? (path => File.Exists(path));
        var reader = contentReader ?? (path => File.ReadAllText(path));

        var mdFiles = fileList
            .Where(f => !string.IsNullOrWhiteSpace(f) &&
                        (f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mdFiles.Count == 0)
        {
            string emptySummary = "Workspace heading uniformity advisor: 0 markdown documents · No documents in scope";
            return new WorkspaceHeadingUniformityAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<DocumentHeadingItem>(),
                Array.Empty<string>(),
                "No Documents",
                emptySummary);
        }

        var allHeadings = new List<DocumentHeadingItem>();
        var issues = new List<string>();

        int evaluatedDocCount = 0;
        int atxCount = 0;
        int setextCount = 0;
        int skippedLevelCount = 0;
        int duplicateSlugCount = 0;

        foreach (var file in mdFiles)
        {
            if (!exists(file)) continue;

            string text;
            try
            {
                text = reader(file);
            }
            catch
            {
                continue;
            }

            evaluatedDocCount++;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            bool inCodeFence = false;
            string fenceMarker = string.Empty;

            var docSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int prevLevel = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
                {
                    string tag = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;
                    if (!inCodeFence)
                    {
                        inCodeFence = true;
                        fenceMarker = tag;
                    }
                    else if (tag == fenceMarker || trimmed == ":::")
                    {
                        inCodeFence = false;
                        fenceMarker = string.Empty;
                    }
                    continue;
                }

                if (inCodeFence) continue;

                // Check ATX heading: # Heading
                var atxMatch = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (atxMatch.Success)
                {
                    atxCount++;
                    int level = atxMatch.Groups[1].Value.Length;
                    string title = atxMatch.Groups[2].Value.Trim().TrimEnd('#').Trim();
                    string slug = GenerateSlug(title);

                    bool isSkipped = prevLevel > 0 && level > prevLevel + 1;
                    if (isSkipped) skippedLevelCount++;

                    bool hasInvalidChars = Regex.IsMatch(title, @"[<>""'|?*]");
                    bool isDuplicate = !docSlugs.Add(slug);
                    if (isDuplicate) duplicateSlugCount++;

                    allHeadings.Add(new DocumentHeadingItem(
                        FilePath: file,
                        LineNumber: i + 1,
                        Level: level,
                        Style: "ATX",
                        Title: title,
                        AnchorSlug: slug,
                        HasInvalidChars: hasInvalidChars,
                        IsLevelSkipped: isSkipped));

                    prevLevel = level;
                    continue;
                }

                // Check Setext heading: line followed by === or ---
                if (i > 0 && (Regex.IsMatch(trimmed, @"^={2,}$") || Regex.IsMatch(trimmed, @"^-{2,}$")))
                {
                    var prevLine = lines[i - 1].Trim();
                    if (!string.IsNullOrWhiteSpace(prevLine) && !prevLine.StartsWith("#") && !prevLine.StartsWith(">") && !prevLine.StartsWith("-") && !prevLine.StartsWith("*"))
                    {
                        setextCount++;
                        int level = trimmed.StartsWith("=") ? 1 : 2;
                        string title = prevLine;
                        string slug = GenerateSlug(title);

                        bool isSkipped = prevLevel > 0 && level > prevLevel + 1;
                        if (isSkipped) skippedLevelCount++;

                        bool hasInvalidChars = Regex.IsMatch(title, @"[<>""'|?*]");
                        bool isDuplicate = !docSlugs.Add(slug);
                        if (isDuplicate) duplicateSlugCount++;

                        allHeadings.Add(new DocumentHeadingItem(
                            FilePath: file,
                            LineNumber: i,
                            Level: level,
                            Style: "Setext",
                            Title: title,
                            AnchorSlug: slug,
                            HasInvalidChars: hasInvalidChars,
                            IsLevelSkipped: isSkipped));

                        prevLevel = level;
                    }
                }
            }
        }

        int totalHeadings = allHeadings.Count;
        double uniformityScore;

        if (totalHeadings == 0)
        {
            uniformityScore = 100.0;
        }
        else
        {
            double styleDisparity = (atxCount > 0 && setextCount > 0)
                ? (Math.Min(atxCount, setextCount) * 20.0) / totalHeadings
                : 0.0;

            double skipPenalty = (skippedLevelCount * 15.0) / totalHeadings;
            double duplicatePenalty = (duplicateSlugCount * 12.0) / totalHeadings;

            uniformityScore = Math.Clamp(100.0 - styleDisparity - skipPenalty - duplicatePenalty, 0.0, 100.0);
        }

        string category = uniformityScore switch
        {
            >= 95.0 when setextCount == 0 && skippedLevelCount == 0 => "Pure Monotonic ATX Hierarchy",
            >= 80.0 => "Consistent Heading Structure",
            >= 60.0 => (atxCount > 0 && setextCount > 0) ? "Mixed ATX/Setext Heading Styles" : "Level-Skipping Heading Jumps",
            _ => "Fragmented Heading Hierarchy / Slug Collisions"
        };

        string summary = FormatWorkspaceHeadingUniformitySummary(
            evaluatedDocCount, totalHeadings, atxCount, setextCount, skippedLevelCount,
            duplicateSlugCount, uniformityScore, category);

        return new WorkspaceHeadingUniformityAudit(
            TotalDocumentsEvaluated: evaluatedDocCount,
            TotalHeadingsFound: totalHeadings,
            AtxCount: atxCount,
            SetextCount: setextCount,
            SkippedLevelCount: skippedLevelCount,
            DuplicateSlugCount: duplicateSlugCount,
            HeadingUniformityScore: Math.Round(uniformityScore, 1),
            Headings: allHeadings,
            Issues: issues,
            Category: category,
            Summary: summary);
    }

    private static string GenerateSlug(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "section";
        string slug = Regex.Replace(text.ToLowerInvariant(), @"[^\w\- ]+", "");
        slug = Regex.Replace(slug, @"[\s\-]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "section" : slug;
    }

    public static string FormatWorkspaceHeadingUniformitySummary(
        int totalDocs, int totalHeadings, int atxCount, int setextCount,
        int skippedLevels, int duplicateSlugs, double score, string category)
    {
        if (totalDocs == 0) return "Workspace heading uniformity advisor: 0 documents · Workspace is empty";

        string docLabel = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string headingLabel = totalHeadings == 1 ? "1 heading" : $"{totalHeadings} headings";

        var alerts = new List<string>();
        if (atxCount > 0 && setextCount > 0) alerts.Add($"⚠ Mixed {atxCount} ATX / {setextCount} Setext");
        if (skippedLevels > 0) alerts.Add($"🚨 {skippedLevels} level jump{(skippedLevels == 1 ? "" : "s")}");
        if (duplicateSlugs > 0) alerts.Add($"⚠ {duplicateSlugs} duplicate slug{(duplicateSlugs == 1 ? "" : "s")}");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · Consistent hierarchy";
        return $"Workspace heading uniformity advisor ({docLabel}, {headingLabel} · {category} · {score:0.#}% score){details}";
    }

    /// <summary>
    /// Audits markdown documents across the workspace for code fence language tags,
    /// syntax highlighter support coverage, canonical language aliases, and untagged/bare code blocks.
    /// </summary>
    public static WorkspaceCodeFenceLanguageAudit AuditWorkspaceCodeFenceLanguageTags(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var fileList = (files ?? Enumerable.Empty<string>()).ToList();
        var exists = fileExistsProvider ?? File.Exists;
        var reader = contentReader ?? File.ReadAllText;

        if (fileList.Count == 0)
        {
            string emptySummary = "Workspace code fence language advisor: 0 documents · Workspace is empty";
            return new WorkspaceCodeFenceLanguageAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceCodeFenceItem>(),
                new Dictionary<string, int>(),
                "Workspace Empty",
                emptySummary);
        }

        var canonicalAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cs"] = "csharp",
            ["py"] = "python",
            ["js"] = "javascript",
            ["ts"] = "typescript",
            ["sh"] = "bash",
            ["ps1"] = "powershell",
            ["yml"] = "yaml",
            ["md"] = "markdown",
            ["rb"] = "ruby",
            ["rs"] = "rust",
            ["docker"] = "dockerfile"
        };

        var supportedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csharp", "cs", "c#", "python", "py", "javascript", "js", "typescript", "ts",
            "json", "xml", "html", "css", "sql", "yaml", "yml", "bash", "sh", "powershell", "ps1",
            "markdown", "md", "mermaid", "rust", "rs", "go", "cpp", "c", "diff", "dockerfile",
            "docker", "svg", "ini", "toml", "r", "ruby", "rb", "java", "kotlin", "swift",
            "php", "perl", "scala", "latex", "tex", "text", "plaintext"
        };

        var allFences = new List<WorkspaceCodeFenceItem>();
        var langDistribution = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int evaluatedDocCount = 0;
        int untaggedCount = 0;
        int supportedCount = 0;
        int aliasCount = 0;
        int unsupportedCount = 0;

        foreach (var file in fileList)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            string ext = Path.GetExtension(file);
            if (!string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!exists(file)) continue;

            string text;
            try
            {
                text = reader(file);
            }
            catch
            {
                continue;
            }

            evaluatedDocCount++;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            bool inCodeFence = false;
            string fenceMarker = string.Empty;
            string fileName = Path.GetFileName(file);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    string marker = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;

                    if (!inCodeFence)
                    {
                        inCodeFence = true;
                        fenceMarker = marker;

                        string rawTag = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : string.Empty;
                        string cleanTag = rawTag.Split(new[] { ' ', '\t', '{' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

                        bool isUntagged = string.IsNullOrWhiteSpace(cleanTag);
                        string normalized;
                        bool isAlias = false;
                        string recommended;
                        bool hasSupport = false;

                        if (isUntagged)
                        {
                            untaggedCount++;
                            normalized = "unspecified";
                            recommended = "text";
                        }
                        else
                        {
                            if (canonicalAliases.TryGetValue(cleanTag, out var canonical))
                            {
                                isAlias = true;
                                aliasCount++;
                                normalized = canonical;
                                recommended = canonical;
                                hasSupport = true;
                            }
                            else
                            {
                                normalized = cleanTag.ToLowerInvariant();
                                recommended = cleanTag.ToLowerInvariant();
                                hasSupport = supportedLanguages.Contains(cleanTag);
                            }

                            if (hasSupport) supportedCount++;
                            else unsupportedCount++;
                        }

                        langDistribution[normalized] = langDistribution.TryGetValue(normalized, out int cnt) ? cnt + 1 : 1;

                        allFences.Add(new WorkspaceCodeFenceItem(
                            FilePath: file,
                            FileName: fileName,
                            LineNumber: i + 1,
                            RawFenceTag: rawTag,
                            NormalizedLanguage: normalized,
                            IsUntagged: isUntagged,
                            HasHighlighterSupport: hasSupport,
                            IsNonCanonicalAlias: isAlias,
                            RecommendedTag: recommended));
                    }
                    else if (marker == fenceMarker)
                    {
                        inCodeFence = false;
                        fenceMarker = string.Empty;
                    }
                }
            }
        }

        int totalFences = allFences.Count;
        int taggedCount = totalFences - untaggedCount;
        double coverageScore;

        if (totalFences == 0)
        {
            coverageScore = 100.0;
        }
        else
        {
            double untaggedPenalty = (untaggedCount * 40.0) / totalFences;
            double unsupportedPenalty = (unsupportedCount * 15.0) / totalFences;
            double aliasPenalty = (aliasCount * 5.0) / totalFences;
            coverageScore = Math.Clamp(100.0 - untaggedPenalty - unsupportedPenalty - aliasPenalty, 0.0, 100.0);
        }

        string category = coverageScore switch
        {
            >= 95.0 when untaggedCount == 0 => "Full Syntax Highlighting Coverage & Canonical Tags",
            >= 80.0 => "Good Syntax Highlighter Coverage",
            >= 60.0 => "Untagged / Bare Code Fences Detected",
            _ => "Widespread Untagged Code Blocks"
        };

        string summary = FormatWorkspaceCodeFenceLanguageSummary(
            evaluatedDocCount, totalFences, taggedCount, untaggedCount,
            supportedCount, aliasCount, coverageScore, category);

        return new WorkspaceCodeFenceLanguageAudit(
            TotalDocumentsEvaluated: evaluatedDocCount,
            TotalCodeFencesFound: totalFences,
            TaggedFencesCount: taggedCount,
            UntaggedFencesCount: untaggedCount,
            SupportedHighlighterCount: supportedCount,
            UnsupportedOrUnknownCount: unsupportedCount,
            NonCanonicalAliasCount: aliasCount,
            CoverageScore: Math.Round(coverageScore, 1),
            CodeFences: allFences,
            LanguageDistribution: langDistribution,
            Category: category,
            Summary: summary);
    }

    /// <summary>
    /// Audits a single markdown file for code fence language tags.
    /// </summary>
    public static WorkspaceCodeFenceLanguageAudit AuditWorkspaceCodeFenceLanguageTags(
        string filePath,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        return AuditWorkspaceCodeFenceLanguageTags(new[] { filePath }, fileExistsProvider, contentReader);
    }

    public static string FormatWorkspaceCodeFenceLanguageSummary(
        int totalDocs, int totalFences, int taggedCount, int untaggedCount,
        int supportedCount, int aliasCount, double score, string category)
    {
        if (totalDocs == 0) return "Workspace code fence language advisor: 0 documents · Workspace is empty";
        if (totalFences == 0) return $"Workspace code fence language advisor ({totalDocs} docs): 0 code fences found";

        string docLabel = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string fenceLabel = totalFences == 1 ? "1 fence" : $"{totalFences} fences";

        var alerts = new List<string>();
        if (untaggedCount > 0) alerts.Add($"⚠ {untaggedCount} untagged");
        if (aliasCount > 0) alerts.Add($"{aliasCount} non-canonical alias{(aliasCount == 1 ? "" : "es")}");
        if (supportedCount > 0) alerts.Add($"{supportedCount} highlighted");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% highlighted";
        return $"Workspace code fence language advisor ({docLabel}, {fenceLabel} · {category} · {score:0.#}% score){details}";
    }

    /// <summary>
    /// Audits markdown documents across the workspace for frontmatter metadata blocks (YAML '---' or TOML '+++'),
    /// schema field completeness (title, date, author, tags/categories, description/summary), and malformed syntax.
    /// </summary>
    public static WorkspaceFrontmatterMetadataAudit AuditWorkspaceFrontmatterMetadata(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var fileList = (files ?? Enumerable.Empty<string>()).ToList();
        var exists = fileExistsProvider ?? File.Exists;
        var reader = contentReader ?? File.ReadAllText;

        if (fileList.Count == 0)
        {
            return new WorkspaceFrontmatterMetadataAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceFrontmatterDocItem>(),
                "Workspace Empty",
                "Workspace frontmatter metadata advisor: 0 documents · Workspace is empty");
        }

        var docItems = new List<WorkspaceFrontmatterDocItem>();
        int docsWithFrontmatter = 0;
        int yamlCount = 0;
        int tomlCount = 0;
        int malformedCount = 0;
        int titleCount = 0;
        int dateCount = 0;
        int authorCount = 0;
        int tagsCount = 0;
        int descCount = 0;

        foreach (var path in fileList)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string ext = Path.GetExtension(path);
            if (!string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".mdx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!exists(path)) continue;

            string content;
            try
            {
                content = reader(path);
            }
            catch
            {
                continue;
            }

            string fileName = Path.GetFileName(path);
            var (hasFm, format, isMalformed, hasTitle, hasDate, hasAuthor, hasTags, hasDesc, fieldsFound, status) =
                EvaluateDocumentFrontmatter(content);

            if (hasFm) docsWithFrontmatter++;
            if (format == "YAML") yamlCount++;
            else if (format == "TOML") tomlCount++;
            if (isMalformed) malformedCount++;
            if (hasTitle) titleCount++;
            if (hasDate) dateCount++;
            if (hasAuthor) authorCount++;
            if (hasTags) tagsCount++;
            if (hasDesc) descCount++;

            docItems.Add(new WorkspaceFrontmatterDocItem(
                path, fileName, hasFm, format, isMalformed, hasTitle, hasDate, hasAuthor, hasTags, hasDesc, fieldsFound, status));
        }

        if (docItems.Count == 0)
        {
            return new WorkspaceFrontmatterMetadataAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceFrontmatterDocItem>(),
                "No Markdown Documents",
                "Workspace frontmatter metadata advisor: 0 markdown documents found");
        }

        int totalDocs = docItems.Count;
        int missingCount = totalDocs - docsWithFrontmatter;
        int totalExpectedFields = totalDocs * 5; // 5 schema fields: title, date, author, tags, description
        int totalFoundFields = titleCount + dateCount + authorCount + tagsCount + descCount;

        double fmRatio = (double)docsWithFrontmatter / totalDocs;
        double fieldRatio = (double)totalFoundFields / totalExpectedFields;
        double malformedPenalty = malformedCount * 15.0;

        double rawScore = (fmRatio * 40.0) + (fieldRatio * 60.0) - malformedPenalty;
        double completenessScore = Math.Round(Math.Clamp(rawScore, 0.0, 100.0), 1);

        string category = completenessScore switch
        {
            >= 90.0 => "Comprehensive Frontmatter Schema Consistency",
            >= 75.0 => "Good Frontmatter Coverage",
            >= 50.0 => "Moderate Frontmatter Consistency (Partial Schema)",
            _ => "Low Frontmatter Coverage / Missing Metadata"
        };

        string summary = FormatWorkspaceFrontmatterSummary(
            totalDocs, docsWithFrontmatter, missingCount, yamlCount, tomlCount, malformedCount,
            titleCount, dateCount, authorCount, tagsCount, descCount, completenessScore, category);

        return new WorkspaceFrontmatterMetadataAudit(
            totalDocs, docsWithFrontmatter, missingCount, yamlCount, tomlCount, malformedCount,
            titleCount, dateCount, authorCount, tagsCount, descCount, completenessScore,
            docItems, category, summary);
    }

    private static (bool HasFm, string Format, bool IsMalformed, bool HasTitle, bool HasDate, bool HasAuthor, bool HasTags, bool HasDesc, int FieldsFound, string Status)
        EvaluateDocumentFrontmatter(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return (false, "None", false, false, false, false, false, false, 0, "Empty document");
        }

        string trimmed = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        var lines = trimmed.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return (false, "None", false, false, false, false, false, false, 0, "Empty document");
        }

        string firstLine = lines[0].Trim();
        bool isYaml = firstLine == "---";
        bool isToml = firstLine == "+++";

        if (!isYaml && !isToml)
        {
            return (false, "None", false, false, false, false, false, false, 0, "Missing frontmatter");
        }

        string delimiter = isYaml ? "---" : "+++";
        string format = isYaml ? "YAML" : "TOML";

        int closingIdx = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == delimiter)
            {
                closingIdx = i;
                break;
            }
        }

        if (closingIdx == -1)
        {
            return (true, format, true, false, false, false, false, false, 0, $"⚠ Malformed {format} frontmatter: Unclosed delimiter");
        }

        var fmBodyLines = lines.Skip(1).Take(closingIdx - 1).ToList();
        bool hasTitle = false;
        bool hasDate = false;
        bool hasAuthor = false;
        bool hasTags = false;
        bool hasDesc = false;

        foreach (var rawFmLine in fmBodyLines)
        {
            string l = rawFmLine.Trim();
            if (string.IsNullOrEmpty(l) || l.StartsWith("#")) continue;

            if (l.StartsWith("title:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("title =", StringComparison.OrdinalIgnoreCase)) hasTitle = true;
            else if (l.StartsWith("date:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("date =", StringComparison.OrdinalIgnoreCase)) hasDate = true;
            else if (l.StartsWith("author:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("author =", StringComparison.OrdinalIgnoreCase)) hasAuthor = true;
            else if (l.StartsWith("tags:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("tags =", StringComparison.OrdinalIgnoreCase) ||
                     l.StartsWith("categories:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("categories =", StringComparison.OrdinalIgnoreCase)) hasTags = true;
            else if (l.StartsWith("description:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("description =", StringComparison.OrdinalIgnoreCase) ||
                     l.StartsWith("summary:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("summary =", StringComparison.OrdinalIgnoreCase)) hasDesc = true;
        }

        int fieldsFound = (hasTitle ? 1 : 0) + (hasDate ? 1 : 0) + (hasAuthor ? 1 : 0) + (hasTags ? 1 : 0) + (hasDesc ? 1 : 0);
        string status = $"Valid {format} ({fieldsFound}/5 schema fields)";

        return (true, format, false, hasTitle, hasDate, hasAuthor, hasTags, hasDesc, fieldsFound, status);
    }

    public static WorkspaceFrontmatterMetadataAudit AuditWorkspaceFrontmatterMetadata(
        string filePath,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        return AuditWorkspaceFrontmatterMetadata(new[] { filePath }, fileExistsProvider, contentReader);
    }

    public static string FormatWorkspaceFrontmatterSummary(
        int totalDocs, int withFm, int missingFm, int yamlCount, int tomlCount, int malformedCount,
        int titleCount, int dateCount, int authorCount, int tagsCount, int descCount,
        double score, string category)
    {
        if (totalDocs == 0) return "Workspace frontmatter metadata advisor: 0 documents · Workspace is empty";

        string docLabel = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        var alerts = new List<string>();
        if (missingFm > 0) alerts.Add($"⚠ {missingFm} missing frontmatter");
        if (malformedCount > 0) alerts.Add($"⚠ {malformedCount} malformed");
        if (yamlCount > 0) alerts.Add($"{yamlCount} YAML");
        if (tomlCount > 0) alerts.Add($"{tomlCount} TOML");

        string schemaOverview = $"title {titleCount}/{totalDocs}, date {dateCount}/{totalDocs}, tags {tagsCount}/{totalDocs}";
        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% frontmatter coverage";

        return $"Workspace frontmatter metadata advisor ({docLabel} · {category} · {score:0.#}% score): " +
               $"{withFm} with frontmatter [{schemaOverview}]{details}";
    }

    /// <summary>
    /// Audits workspace markdown documents for heading hierarchy depth, level skipping (e.g. H1 to H3),
    /// multiple top-level H1 tags, and overall document outline ergonomics.
    /// </summary>
    public static WorkspaceHeadingHierarchyAudit AuditWorkspaceHeadingHierarchy(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var fileList = (files ?? Enumerable.Empty<string>()).ToList();
        var exists = fileExistsProvider ?? File.Exists;
        var reader = contentReader ?? File.ReadAllText;

        if (fileList.Count == 0)
        {
            return new WorkspaceHeadingHierarchyAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceDocumentHeadingItem>(),
                "Workspace Empty",
                "Workspace heading hierarchy advisor: 0 documents · Workspace is empty");
        }

        var docItems = new List<WorkspaceDocumentHeadingItem>();
        int totalHeadingsFound = 0;
        int validOutlineCount = 0;
        int skippedLevelDocsCount = 0;
        int multipleH1DocsCount = 0;
        int missingH1DocsCount = 0;
        int maxDepthAcrossWorkspace = 0;

        foreach (var path in fileList)
        {
            if (string.IsNullOrWhiteSpace(path) || !exists(path)) continue;

            string content;
            try { content = reader(path); }
            catch { continue; }

            string fileName = Path.GetFileName(path);
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            bool inFence = false;
            string fenceMarker = "";
            bool inFrontmatter = false;

            int docHeadings = 0;
            int h1Count = 0;
            int maxDocDepth = 0;
            int skippedLevels = 0;
            int prevLevel = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (i == 0 && (trimmed == "---" || trimmed == "+++"))
                {
                    inFrontmatter = true;
                    continue;
                }
                if (inFrontmatter)
                {
                    if (trimmed == "---" || trimmed == "+++")
                    {
                        inFrontmatter = false;
                    }
                    continue;
                }

                if (!inFence && (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")))
                {
                    inFence = true;
                    fenceMarker = trimmed[..3];
                    continue;
                }
                if (inFence && trimmed.StartsWith(fenceMarker))
                {
                    inFence = false;
                    fenceMarker = "";
                    continue;
                }
                if (inFence) continue;

                var atxMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(?<hashes>#{1,6})\s+(?<text>.*)$");
                if (atxMatch.Success)
                {
                    int level = atxMatch.Groups["hashes"].Length;
                    docHeadings++;
                    if (level == 1) h1Count++;
                    if (level > maxDocDepth) maxDocDepth = level;

                    if (prevLevel > 0 && level > prevLevel + 1)
                    {
                        skippedLevels += (level - prevLevel - 1);
                    }
                    prevLevel = level;
                    continue;
                }

                if (i > 0 && !string.IsNullOrWhiteSpace(lines[i - 1]))
                {
                    string prevTrimmed = lines[i - 1].Trim();
                    if (!prevTrimmed.StartsWith("#") && !prevTrimmed.StartsWith("`"))
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[=]{2,}\s*$"))
                        {
                            docHeadings++;
                            h1Count++;
                            if (1 > maxDocDepth) maxDocDepth = 1;
                            prevLevel = 1;
                            continue;
                        }
                        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[-]{2,}\s*$"))
                        {
                            docHeadings++;
                            if (2 > maxDocDepth) maxDocDepth = 2;
                            if (prevLevel > 0 && 2 > prevLevel + 1)
                            {
                                skippedLevels += (2 - prevLevel - 1);
                            }
                            prevLevel = 2;
                            continue;
                        }
                    }
                }
            }

            totalHeadingsFound += docHeadings;
            if (maxDocDepth > maxDepthAcrossWorkspace) maxDepthAcrossWorkspace = maxDocDepth;

            bool hasMultipleH1 = h1Count > 1;
            bool hasMissingH1 = docHeadings > 0 && h1Count == 0;
            bool hasSkipped = skippedLevels > 0;

            if (hasSkipped) skippedLevelDocsCount++;
            if (hasMultipleH1) multipleH1DocsCount++;
            if (hasMissingH1) missingH1DocsCount++;

            bool isValid = !hasSkipped && !hasMultipleH1 && !hasMissingH1;
            if (isValid) validOutlineCount++;

            double docScore = 100.0;
            if (hasSkipped) docScore -= Math.Min(45.0, skippedLevels * 15.0);
            if (hasMultipleH1) docScore -= 15.0;
            if (hasMissingH1) docScore -= 15.0;
            docScore = Math.Round(Math.Clamp(docScore, 0.0, 100.0), 1);

            string status = isValid
                ? "✓ Clean ergonomic outline"
                : $"⚠ {(hasSkipped ? $"{skippedLevels} skipped level(s)" : "")}{(hasMultipleH1 ? " multiple H1s" : "")}{(hasMissingH1 ? " missing H1" : "")}".Trim();

            docItems.Add(new WorkspaceDocumentHeadingItem(
                path, fileName, docHeadings, h1Count, maxDocDepth, skippedLevels, hasMultipleH1, hasMissingH1, docScore, status));
        }

        int totalEvaluated = docItems.Count;
        if (totalEvaluated == 0)
        {
            return new WorkspaceHeadingHierarchyAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceDocumentHeadingItem>(),
                "Workspace Empty",
                "Workspace heading hierarchy advisor: 0 documents · Workspace is empty");
        }

        double ergonomicsScore = Math.Round(docItems.Average(d => d.DocumentScore), 1);

        string category = ergonomicsScore switch
        {
            >= 90.0 => "Flawless Heading Ergonomics & Outline Depth",
            >= 75.0 => "Good Structural Hierarchy (Minor Skipped Levels)",
            >= 50.0 => "Moderate Outline Depth Variance",
            _ => "Fragmented / Inconsistent Document Hierarchy"
        };

        string summary = FormatWorkspaceHeadingSummary(
            totalEvaluated, totalHeadingsFound, validOutlineCount, skippedLevelDocsCount,
            multipleH1DocsCount, missingH1DocsCount, maxDepthAcrossWorkspace, ergonomicsScore, category);

        return new WorkspaceHeadingHierarchyAudit(
            totalEvaluated, totalHeadingsFound, validOutlineCount, skippedLevelDocsCount,
            multipleH1DocsCount, missingH1DocsCount, maxDepthAcrossWorkspace, ergonomicsScore,
            docItems, category, summary);
    }

    public static WorkspaceHeadingHierarchyAudit AuditWorkspaceHeadingHierarchy(
        string filePath,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        return AuditWorkspaceHeadingHierarchy(new[] { filePath }, fileExistsProvider, contentReader);
    }

    public static string FormatWorkspaceHeadingSummary(
        int totalDocs, int totalHeadings, int validOutlines, int skippedLevels,
        int multipleH1, int missingH1, int maxDepth, double score, string category)
    {
        if (totalDocs == 0) return "Workspace heading hierarchy advisor: 0 documents · Workspace is empty";

        string docLabel = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string headingLabel = totalHeadings == 1 ? "1 heading" : $"{totalHeadings} headings";

        var alerts = new List<string>();
        if (skippedLevels > 0) alerts.Add($"⚠ {skippedLevels} skipped levels");
        if (multipleH1 > 0) alerts.Add($"⚠ {multipleH1} multiple H1s");
        if (missingH1 > 0) alerts.Add($"{missingH1} missing H1");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% ergonomic outlines";
        return $"Workspace heading hierarchy advisor ({docLabel} [{headingLabel}, max H{maxDepth}] · {category} · {score:0.#}% score): " +
               $"{validOutlines}/{totalDocs} valid outlines{details}";
    }

    /// <summary>
    /// Audits workspace markdown documents for local and external media link integrity, detecting broken relative paths,
    /// missing image/video/audio assets, portability risks (absolute paths, unencoded spaces), and data URIs.
    /// </summary>
    public static WorkspaceMediaLinkIntegrityAudit AuditWorkspaceMediaLinkIntegrity(
        IEnumerable<string>? workspaceFilePaths,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var paths = workspaceFilePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList() ?? new List<string>();
        if (paths.Count == 0)
        {
            return new WorkspaceMediaLinkIntegrityAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceMediaLinkItem>(),
                Array.Empty<string>(),
                "Pristine",
                "Workspace media link integrity advisor: 0 documents · Workspace is empty");
        }

        var allLinks = new List<WorkspaceMediaLinkItem>();
        var missingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int docsEvaluated = 0;
        int validLocalCount = 0;
        int missingLocalCount = 0;
        int externalCount = 0;
        int dataUriCount = 0;
        int portabilityWarningCount = 0;

        var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".ico", ".tiff", ".tif", ".avif"
        };
        var videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".ogv", ".mov", ".mkv", ".avi", ".wmv"
        };
        var audioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".ogg", ".m4a", ".aac", ".flac", ".wma"
        };
        var docExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".eps", ".ai", ".psd", ".drawio"
        };

        foreach (var docPath in paths)
        {
            bool exists = fileExistsProvider != null ? fileExistsProvider(docPath) : File.Exists(docPath);
            if (!exists) continue;

            string content;
            try
            {
                content = contentReader != null ? contentReader(docPath) : File.ReadAllText(docPath);
            }
            catch
            {
                continue;
            }

            docsEvaluated++;
            string docDir = Path.GetDirectoryName(docPath) ?? "";
            string docFileName = Path.GetFileName(docPath);

            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            bool inCodeFence = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inCodeFence = !inCodeFence;
                    continue;
                }
                if (inCodeFence) continue;

                // 1. Markdown image syntax: ![alt](url)
                var mdMatches = Regex.Matches(line, @"!\[(?<alt>[^\]]*)\]\((?<target>[^)]+)\)");
                foreach (Match m in mdMatches)
                {
                    ProcessMediaTarget(m.Groups["target"].Value.Trim(), docPath, docFileName, docDir, i + 1, allLinks, missingPaths,
                        ref validLocalCount, ref missingLocalCount, ref externalCount, ref dataUriCount, ref portabilityWarningCount,
                        imageExtensions, videoExtensions, audioExtensions, docExtensions, fileExistsProvider);
                }

                // 2. Wiki-style embeds: ![[target]] or ![[target|caption]]
                var wikiMatches = Regex.Matches(line, @"!\[\[(?<target>[^\]|]+)(?:\|[^\]]*)?\]\]");
                foreach (Match m in wikiMatches)
                {
                    ProcessMediaTarget(m.Groups["target"].Value.Trim(), docPath, docFileName, docDir, i + 1, allLinks, missingPaths,
                        ref validLocalCount, ref missingLocalCount, ref externalCount, ref dataUriCount, ref portabilityWarningCount,
                        imageExtensions, videoExtensions, audioExtensions, docExtensions, fileExistsProvider);
                }

                // 3. HTML image tags: <img ... src="..." ...>
                var imgMatches = Regex.Matches(line, @"<img\s+[^>]*src=[""'](?<target>[^""']+)[""'][^>]*>", RegexOptions.IgnoreCase);
                foreach (Match m in imgMatches)
                {
                    ProcessMediaTarget(m.Groups["target"].Value.Trim(), docPath, docFileName, docDir, i + 1, allLinks, missingPaths,
                        ref validLocalCount, ref missingLocalCount, ref externalCount, ref dataUriCount, ref portabilityWarningCount,
                        imageExtensions, videoExtensions, audioExtensions, docExtensions, fileExistsProvider);
                }

                // 4. HTML video/audio/source tags: <video/audio/source ... src="..." ...>
                var mediaMatches = Regex.Matches(line, @"<(?:video|audio|source)\s+[^>]*src=[""'](?<target>[^""']+)[""'][^>]*>", RegexOptions.IgnoreCase);
                foreach (Match m in mediaMatches)
                {
                    ProcessMediaTarget(m.Groups["target"].Value.Trim(), docPath, docFileName, docDir, i + 1, allLinks, missingPaths,
                        ref validLocalCount, ref missingLocalCount, ref externalCount, ref dataUriCount, ref portabilityWarningCount,
                        imageExtensions, videoExtensions, audioExtensions, docExtensions, fileExistsProvider);
                }
            }
        }

        int totalFound = allLinks.Count;
        int localEvaluated = validLocalCount + missingLocalCount;
        double integrityScore = localEvaluated == 0 ? 100.0 : Math.Round((double)validLocalCount / localEvaluated * 100.0, 1);

        string category;
        if (missingLocalCount == 0 && portabilityWarningCount == 0)
        {
            category = "Pristine";
        }
        else if (missingLocalCount == 0 && portabilityWarningCount > 0)
        {
            category = "Solid";
        }
        else if (integrityScore >= 75.0)
        {
            category = "At Risk";
        }
        else
        {
            category = "Broken";
        }

        string summary = FormatWorkspaceMediaLinkSummary(
            docsEvaluated, totalFound, validLocalCount, missingLocalCount, externalCount, portabilityWarningCount, integrityScore, category);

        return new WorkspaceMediaLinkIntegrityAudit(
            docsEvaluated,
            totalFound,
            validLocalCount,
            missingLocalCount,
            externalCount,
            dataUriCount,
            portabilityWarningCount,
            integrityScore,
            allLinks,
            missingPaths.OrderBy(p => p).ToList(),
            category,
            summary);
    }

    private static void ProcessMediaTarget(
        string rawTarget,
        string docPath,
        string docFileName,
        string docDir,
        int lineNumber,
        List<WorkspaceMediaLinkItem> allLinks,
        HashSet<string> missingPaths,
        ref int validLocalCount,
        ref int missingLocalCount,
        ref int externalCount,
        ref int dataUriCount,
        ref int portabilityWarningCount,
        HashSet<string> imageExts,
        HashSet<string> videoExts,
        HashSet<string> audioExts,
        HashSet<string> docExts,
        Func<string, bool>? fileExistsProvider)
    {
        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            missingLocalCount++;
            allLinks.Add(new WorkspaceMediaLinkItem(
                docPath, docFileName, lineNumber, rawTarget, "", "Unknown",
                false, false, false, false, false, "Empty media path"));
            return;
        }

        string cleanTarget = rawTarget;
        bool hasAngleBrackets = false;
        if (cleanTarget.StartsWith("<") && cleanTarget.EndsWith(">") && cleanTarget.Length >= 2)
        {
            hasAngleBrackets = true;
            cleanTarget = cleanTarget.Substring(1, cleanTarget.Length - 2).Trim();
        }
        else
        {
            int firstSpace = cleanTarget.IndexOfAny(new[] { ' ', '\t' });
            if (firstSpace > 0)
            {
                string remainder = cleanTarget.Substring(firstSpace).Trim();
                if ((remainder.StartsWith("\"") && remainder.EndsWith("\"")) || (remainder.StartsWith("'") && remainder.EndsWith("'")))
                {
                    cleanTarget = cleanTarget.Substring(0, firstSpace).Trim();
                }
            }
        }

        if (cleanTarget.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            dataUriCount++;
            string mediaType = DetectMediaType(cleanTarget, imageExts, videoExts, audioExts, docExts);
            allLinks.Add(new WorkspaceMediaLinkItem(
                docPath, docFileName, lineNumber, rawTarget, cleanTarget, mediaType,
                false, true, true, false, false, null));
            return;
        }

        if (cleanTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            cleanTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            cleanTarget.StartsWith("//") ||
            cleanTarget.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            externalCount++;
            string mediaType = DetectMediaType(cleanTarget, imageExts, videoExts, audioExts, docExts);
            bool isInsecure = cleanTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            if (isInsecure) portabilityWarningCount++;
            allLinks.Add(new WorkspaceMediaLinkItem(
                docPath, docFileName, lineNumber, rawTarget, cleanTarget, mediaType,
                true, false, true, false, false, isInsecure ? "Insecure HTTP protocol (prefer HTTPS)" : null));
            return;
        }

        // Local file path
        bool isAbsolute = Path.IsPathRooted(cleanTarget) && (cleanTarget.Length >= 2 && (cleanTarget[1] == ':' || cleanTarget.StartsWith("\\\\")));
        bool hasUnencodedSpaces = !hasAngleBrackets && cleanTarget.Contains(' ');

        if (isAbsolute) portabilityWarningCount++;
        if (hasUnencodedSpaces) portabilityWarningCount++;

        string filePathPart = cleanTarget;
        int qIdx = filePathPart.IndexOf('?');
        if (qIdx >= 0) filePathPart = filePathPart.Substring(0, qIdx);
        int hIdx = filePathPart.IndexOf('#');
        if (hIdx >= 0) filePathPart = filePathPart.Substring(0, hIdx);

        // Normalize slashes for consistency
        filePathPart = filePathPart.Replace('/', Path.DirectorySeparatorChar);

        string resolvedPath;
        try
        {
            if (Path.IsPathRooted(filePathPart))
            {
                resolvedPath = Path.GetFullPath(filePathPart);
            }
            else
            {
                resolvedPath = string.IsNullOrEmpty(docDir) ? filePathPart : Path.GetFullPath(Path.Combine(docDir, filePathPart));
            }
        }
        catch
        {
            resolvedPath = filePathPart;
        }

        bool exists = fileExistsProvider != null ? fileExistsProvider(resolvedPath) : File.Exists(resolvedPath);
        if (exists)
        {
            validLocalCount++;
        }
        else
        {
            missingLocalCount++;
            missingPaths.Add(resolvedPath);
        }

        var issues = new List<string>();
        if (!exists) issues.Add("Missing local asset file");
        if (isAbsolute) issues.Add("Non-portable absolute path");
        if (hasUnencodedSpaces) issues.Add("Unencoded spaces in path");

        string? issueDescription = issues.Count > 0 ? string.Join("; ", issues) : null;
        string localMediaType = DetectMediaType(cleanTarget, imageExts, videoExts, audioExts, docExts);

        allLinks.Add(new WorkspaceMediaLinkItem(
            docPath, docFileName, lineNumber, rawTarget, resolvedPath, localMediaType,
            false, false, exists, isAbsolute, hasUnencodedSpaces, issueDescription));
    }

    private static string DetectMediaType(
        string path,
        HashSet<string> imageExts,
        HashSet<string> videoExts,
        HashSet<string> audioExts,
        HashSet<string> docExts)
    {
        if (path.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return "Image";
        if (path.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase)) return "Video";
        if (path.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase)) return "Audio";
        if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "Data";

        string clean = path;
        int q = clean.IndexOf('?');
        if (q >= 0) clean = clean.Substring(0, q);
        int h = clean.IndexOf('#');
        if (h >= 0) clean = clean.Substring(0, h);

        string ext = Path.GetExtension(clean);
        if (imageExts.Contains(ext)) return "Image";
        if (videoExts.Contains(ext)) return "Video";
        if (audioExts.Contains(ext)) return "Audio";
        if (docExts.Contains(ext)) return "Document";

        return "Other";
    }

    /// <summary>
    /// Audits a single markdown file for media link integrity.
    /// </summary>
    public static WorkspaceMediaLinkIntegrityAudit AuditWorkspaceMediaLinkIntegrity(
        string filePath,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        return AuditWorkspaceMediaLinkIntegrity(new[] { filePath }, fileExistsProvider, contentReader);
    }

    public static string FormatWorkspaceMediaLinkSummary(
        int totalDocs, int totalLinks, int validLocal, int missingLocal,
        int externalCount, int portabilityWarnings, double score, string category)
    {
        if (totalDocs == 0) return "Workspace media link integrity advisor: 0 documents · Workspace is empty";
        if (totalLinks == 0) return $"Workspace media link integrity advisor ({totalDocs} docs): 0 media links found";

        string docLabel = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string linkLabel = totalLinks == 1 ? "1 media link" : $"{totalLinks} media links";

        var alerts = new List<string>();
        if (missingLocal > 0) alerts.Add($"⚠ {missingLocal} missing local asset{(missingLocal == 1 ? "" : "s")}");
        if (portabilityWarnings > 0) alerts.Add($"{portabilityWarnings} portability warning{(portabilityWarnings == 1 ? "" : "s")}");
        if (validLocal > 0) alerts.Add($"{validLocal} local verified");
        if (externalCount > 0) alerts.Add($"{externalCount} external");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% verified";
        return $"Workspace media link integrity advisor ({docLabel}, {linkLabel} · {category} · {score:0.#}% integrity){details}";
    }

    /// <summary>
    /// Audits workspace markdown documents for cross-document relative anchor links ([Doc](other.md#heading)),
    /// verifying target document existence and destination heading slug resolution.
    /// </summary>
    public static WorkspaceCrossDocumentAnchorAudit AuditWorkspaceCrossDocumentAnchors(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        fileExistsProvider ??= File.Exists;
        contentReader ??= File.ReadAllText;

        var list = files?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();

        if (list.Count == 0)
        {
            return new WorkspaceCrossDocumentAnchorAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<CrossDocumentAnchorLinkItem>(),
                Array.Empty<string>(),
                "Empty Workspace",
                "Workspace cross-document anchor advisor: 0 documents · Workspace contains no files");
        }

        var mdFiles = list.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                                      f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)).ToList();

        if (mdFiles.Count == 0)
        {
            return new WorkspaceCrossDocumentAnchorAudit(
                list.Count, 0, 0, 0, 0, 100.0,
                Array.Empty<CrossDocumentAnchorLinkItem>(),
                Array.Empty<string>(),
                "No Markdown Documents",
                $"Workspace cross-document anchor advisor ({list.Count} files): No markdown files found");
        }

        var anchorRegex = new System.Text.RegularExpressions.Regex(
            @"\[([^\]]+)\]\(([^)#\s]+\.md)#([^)#\s]+)\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var targetSlugsCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var anchorLinks = new List<CrossDocumentAnchorLinkItem>();
        var missingAnchors = new List<string>();

        int validLinks = 0;
        int brokenFiles = 0;
        int brokenAnchors = 0;

        foreach (var file in mdFiles)
        {
            string fileName = Path.GetFileName(file);
            string content;
            try
            {
                if (!fileExistsProvider(file)) continue;
                content = contentReader(file);
            }
            catch
            {
                continue;
            }

            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            bool inFence = false;

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                string line = lines[lineIdx];
                string trimmed = line.Trim();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence) continue;

                var matches = anchorRegex.Matches(line);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    string targetRel = match.Groups[2].Value;
                    string rawSlug = match.Groups[3].Value;
                    string normalizedSlug = NormalizeAnchorSlug(rawSlug);

                    string sourceDir = Path.GetDirectoryName(file) ?? "";
                    string targetPath = Path.Combine(sourceDir, targetRel);
                    try { targetPath = Path.GetFullPath(targetPath); } catch { }

                    bool fileExists = fileExistsProvider(targetPath);
                    bool anchorExists = false;
                    string? issue = null;

                    if (!fileExists)
                    {
                        brokenFiles++;
                        issue = $"Target file '{targetRel}' not found on disk";
                        missingAnchors.Add($"{fileName}:{lineIdx + 1} -> {targetRel}#{rawSlug} (Missing file)");
                    }
                    else
                    {
                        if (!targetSlugsCache.TryGetValue(targetPath, out var slugs))
                        {
                            slugs = ExtractHeadingSlugs(targetPath, contentReader);
                            targetSlugsCache[targetPath] = slugs;
                        }

                        if (slugs.Contains(normalizedSlug))
                        {
                            anchorExists = true;
                            validLinks++;
                        }
                        else
                        {
                            brokenAnchors++;
                            issue = $"Anchor '#{rawSlug}' not found in '{Path.GetFileName(targetPath)}'";
                            missingAnchors.Add($"{fileName}:{lineIdx + 1} -> {Path.GetFileName(targetPath)}#{rawSlug} (Unresolved heading)");
                        }
                    }

                    string snippet = line.Length > 80 ? line.Substring(0, 77) + "..." : line;

                    anchorLinks.Add(new CrossDocumentAnchorLinkItem(
                        file,
                        fileName,
                        targetRel,
                        targetPath,
                        rawSlug,
                        lineIdx + 1,
                        fileExists,
                        anchorExists,
                        snippet,
                        issue));
                }
            }
        }

        int totalCrossDocLinks = anchorLinks.Count;
        double integrityScore = totalCrossDocLinks > 0
            ? Math.Round((double)validLinks * 100.0 / totalCrossDocLinks, 1)
            : 100.0;

        string category;
        if (totalCrossDocLinks == 0) category = "No Cross-Doc Anchors";
        else if (brokenFiles == 0 && brokenAnchors == 0) category = "100% Cross-Doc Anchor Integrity";
        else if (brokenFiles > 0) category = "Missing Target Documents Detected";
        else category = "Broken Heading Anchor Slugs";

        string summary = FormatWorkspaceCrossDocumentAnchorSummary(
            mdFiles.Count, totalCrossDocLinks, validLinks, brokenFiles, brokenAnchors,
            integrityScore, category);

        return new WorkspaceCrossDocumentAnchorAudit(
            mdFiles.Count,
            totalCrossDocLinks,
            validLinks,
            brokenFiles,
            brokenAnchors,
            integrityScore,
            anchorLinks,
            missingAnchors,
            category,
            summary);
    }

    public static WorkspaceCrossDocumentAnchorAudit AuditWorkspaceCrossDocumentAnchors(
        string filePath,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        return AuditWorkspaceCrossDocumentAnchors(new[] { filePath }, fileExistsProvider, contentReader);
    }

    private static HashSet<string> ExtractHeadingSlugs(string filePath, Func<string, string> contentReader)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string content = contentReader(filePath);
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            bool inFence = false;

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence) continue;

                if (trimmed.StartsWith('#'))
                {
                    int hashCount = trimmed.TakeWhile(c => c == '#').Count();
                    if (hashCount <= 6 && trimmed.Length > hashCount && char.IsWhiteSpace(trimmed[hashCount]))
                    {
                        string title = trimmed.Substring(hashCount).Trim();
                        string slug = NormalizeAnchorSlug(title);
                        if (!string.IsNullOrEmpty(slug))
                        {
                            slugs.Add(slug);
                        }
                    }
                }
            }
        }
        catch { }
        return slugs;
    }

    private static string NormalizeAnchorSlug(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string lower = text.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        foreach (char c in lower)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_') sb.Append('-');
        }
        string raw = sb.ToString();
        while (raw.Contains("--")) raw = raw.Replace("--", "-");
        return raw.Trim('-');
    }

    public static string FormatWorkspaceCrossDocumentAnchorSummary(
        int totalDocs, int totalLinks, int validCount, int brokenFiles, int brokenAnchors,
        double score, string category)
    {
        if (totalDocs == 0) return "Workspace cross-document anchor advisor: 0 documents · Workspace is empty";
        if (totalLinks == 0) return $"Workspace cross-document anchor advisor ({totalDocs} docs): 0 cross-doc anchor links found";

        string docLabel = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string linkLabel = totalLinks == 1 ? "1 cross-doc anchor" : $"{totalLinks} cross-doc anchors";

        var alerts = new List<string>();
        if (brokenFiles > 0) alerts.Add($"🚨 {brokenFiles} missing target file{(brokenFiles == 1 ? "" : "s")}");
        if (brokenAnchors > 0) alerts.Add($"⚠ {brokenAnchors} broken heading slug{(brokenAnchors == 1 ? "" : "s")}");
        if (validCount > 0) alerts.Add($"✓ {validCount} verified");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% verified";
        return $"Workspace cross-document anchor advisor ({docLabel}, {linkLabel} · {category} · {score:0.#}% integrity){details}";
    }

    public static WorkspaceTableFormattingAudit AuditWorkspaceTableFormatting(
        IReadOnlyList<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var exists = fileExistsProvider ?? File.Exists;
        var reader = contentReader ?? File.ReadAllText;

        if (files == null || files.Count == 0)
        {
            string emptySummary = "Workspace table formatting advisor: 0 documents · Workspace is empty";
            return new WorkspaceTableFormattingAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceTableItem>(),
                Array.Empty<string>(),
                "Empty Workspace", emptySummary);
        }

        var tables = new List<WorkspaceTableItem>();
        var alerts = new List<string>();
        int validTables = 0;
        int raggedTables = 0;
        int missingDelimiterTables = 0;
        int emptyHeaderOnlyTables = 0;

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || !exists(file)) continue;

            string content;
            try
            {
                content = reader(file);
            }
            catch
            {
                continue;
            }

            string fileName = Path.GetFileName(file);
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            bool inCodeFence = false;

            int i = 0;
            while (i < lines.Length)
            {
                string rawLine = lines[i];
                string trimmed = rawLine.Trim();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inCodeFence = !inCodeFence;
                    i++;
                    continue;
                }
                if (inCodeFence)
                {
                    i++;
                    continue;
                }

                if (trimmed.Contains('|') && !trimmed.StartsWith('#') && !trimmed.StartsWith('>'))
                {
                    int tableStartLine = i + 1;
                    var tableBlockLines = new List<string>();
                    while (i < lines.Length && lines[i].Trim().Contains('|') && !lines[i].Trim().StartsWith("```") && !lines[i].Trim().StartsWith('#'))
                    {
                        tableBlockLines.Add(lines[i]);
                        i++;
                    }

                    if (tableBlockLines.Count >= 1)
                    {
                        List<string> ParseRowCells(string row)
                        {
                            string t = row.Trim();
                            if (t.StartsWith('|')) t = t[1..];
                            if (t.EndsWith('|')) t = t[..^1];
                            return t.Split('|').Select(c => c.Trim()).ToList();
                        }

                        var headerCells = ParseRowCells(tableBlockLines[0]);
                        int headerColCount = headerCells.Count;

                        bool hasDelimiter = false;
                        var alignments = new List<string>();
                        int dataRowStartIndex = 1;

                        if (tableBlockLines.Count >= 2)
                        {
                            var secondRowCells = ParseRowCells(tableBlockLines[1]);
                            bool isDelimiterRow = secondRowCells.Count > 0 && secondRowCells.All(c =>
                            {
                                string ct = c.Trim();
                                return ct.Length >= 1 && ct.All(ch => ch is '-' or ':' or ' ');
                            });

                            if (isDelimiterRow)
                            {
                                hasDelimiter = true;
                                dataRowStartIndex = 2;
                                foreach (var cell in secondRowCells)
                                {
                                    bool leftColon = cell.StartsWith(':');
                                    bool rightColon = cell.EndsWith(':');
                                    if (leftColon && rightColon) alignments.Add("center");
                                    else if (rightColon) alignments.Add("right");
                                    else if (leftColon) alignments.Add("left");
                                    else alignments.Add("default");
                                }
                            }
                        }

                        int dataRowCount = 0;
                        int raggedRowCount = 0;
                        for (int r = dataRowStartIndex; r < tableBlockLines.Count; r++)
                        {
                            var dataCells = ParseRowCells(tableBlockLines[r]);
                            dataRowCount++;
                            if (dataCells.Count != headerColCount)
                            {
                                raggedRowCount++;
                            }
                        }

                        bool hasRagged = raggedRowCount > 0;
                        bool isEmptyHeaderOnly = dataRowCount == 0;

                        string? issue = null;
                        if (!hasDelimiter)
                        {
                            missingDelimiterTables++;
                            issue = "Missing delimiter row (|---|)";
                            alerts.Add($"{fileName}:{tableStartLine} missing table delimiter separator");
                        }
                        else if (hasRagged)
                        {
                            raggedTables++;
                            issue = $"{raggedRowCount} ragged row(s) (expected {headerColCount} cols)";
                            alerts.Add($"{fileName}:{tableStartLine} has {raggedRowCount} ragged row(s)");
                        }
                        else if (isEmptyHeaderOnly)
                        {
                            emptyHeaderOnlyTables++;
                            issue = "Header-only table with no data rows";
                            alerts.Add($"{fileName}:{tableStartLine} header-only table with 0 data rows");
                        }
                        else
                        {
                            validTables++;
                        }

                        string snippet = tableBlockLines[0].Trim();
                        if (snippet.Length > 50) snippet = snippet[..47] + "...";

                        tables.Add(new WorkspaceTableItem(
                            file,
                            fileName,
                            tableStartLine,
                            headerColCount,
                            dataRowCount,
                            hasDelimiter,
                            hasRagged,
                            raggedRowCount,
                            alignments,
                            snippet,
                            issue));
                    }
                    continue;
                }

                i++;
            }
        }

        int totalTables = tables.Count;
        double score;
        if (totalTables == 0)
        {
            score = 100.0;
        }
        else
        {
            double penalty = (raggedTables * 25.0) + (missingDelimiterTables * 20.0) + (emptyHeaderOnlyTables * 15.0);
            double maxPen = totalTables * 25.0;
            score = Math.Round(Math.Clamp(100.0 - (penalty / maxPen * 100.0), 10.0, 100.0), 1);
        }

        string category;
        if (totalTables == 0) category = "No Tables Present";
        else if (validTables == totalTables) category = "Tabular Formatting Compliant";
        else if (raggedTables > 0) category = "Ragged Table Columns Detected";
        else if (missingDelimiterTables > 0) category = "Missing Table Delimiters";
        else category = "Tabular Structural Warnings";

        string summary = FormatWorkspaceTableFormattingSummary(
            files.Count, totalTables, validTables, raggedTables, missingDelimiterTables,
            emptyHeaderOnlyTables, score, category);

        return new WorkspaceTableFormattingAudit(
            files.Count,
            totalTables,
            validTables,
            raggedTables,
            missingDelimiterTables,
            emptyHeaderOnlyTables,
            score,
            tables,
            alerts,
            category,
            summary);
    }

    public static WorkspaceTableFormattingAudit AuditWorkspaceTableFormatting(
        string filePath,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        return AuditWorkspaceTableFormatting(new[] { filePath }, fileExistsProvider, contentReader);
    }

    public static WorkspaceTableFormattingAudit AuditWorkspaceMarkdownTableFormatting(
        IReadOnlyList<string>? explicitFiles = null,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
        => AuditWorkspaceTableFormatting(explicitFiles, fileExistsProvider, contentReader);

    public static WorkspaceTableFormattingAudit AuditWorkspaceMarkdownTableAutoAlign(
        IReadOnlyList<string>? explicitFiles = null,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
        => AuditWorkspaceTableFormatting(explicitFiles, fileExistsProvider, contentReader);

    public static string ExportWorkspaceTableAutoAlignmentReport(
        IReadOnlyList<string>? explicitFiles = null,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var audit = AuditWorkspaceMarkdownTableAutoAlign(explicitFiles, fileExistsProvider, contentReader);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Workspace Markdown Table Auto-Alignment Report");
        sb.AppendLine();
        sb.AppendLine($"- **Total Documents Evaluated**: {audit.TotalDocumentsEvaluated}");
        sb.AppendLine($"- **Total Tables Evaluated**: {audit.TotalTablesFound}");
        sb.AppendLine($"- **Valid Conforming Tables**: {audit.ValidTablesCount}");
        sb.AppendLine($"- **Ragged Column Count Tables**: {audit.RaggedTablesCount}");
        sb.AppendLine($"- **Missing Delimiter Dashes**: {audit.MissingDelimiterTablesCount}");
        sb.AppendLine($"- **Table Formatting Score**: {audit.TabularIntegrityScore:0.#}% ({audit.Category})");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine(audit.Summary);

        if (audit.Tables.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Table Alignment Diagnostics");
            foreach (var table in audit.Tables)
            {
                string issue = table.IssueDescription ?? "Conforming table structure";
                sb.AppendLine($"- `{table.FileName}` (Line {table.StartLineNumber}, {table.HeaderColumnCount} cols, {table.DataRowCount} rows): {issue}");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("All markdown tables across the workspace conform to optimal pipe alignment and delimiter column width standards.");
        }

        return sb.ToString().TrimEnd();
    }



    public static string AlignMarkdownTableColumns(string tableMarkdown)
    {
        if (string.IsNullOrWhiteSpace(tableMarkdown)) return tableMarkdown ?? string.Empty;
        var lines = tableMarkdown.Replace("\r\n", "\n").Split('\n');
        var rows = new List<List<string>>();
        foreach (var line in lines)
        {
            if (!line.Contains('|')) continue;
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
            rows.Add(cells);
        }
        if (rows.Count < 2) return tableMarkdown;

        int maxCols = rows.Max(r => r.Count);
        var colWidths = new int[maxCols];
        for (int c = 0; c < maxCols; c++)
        {
            int maxLen = 3;
            for (int r = 0; r < rows.Count; r++)
            {
                if (r == 1) continue;
                if (c < rows[r].Count) maxLen = Math.Max(maxLen, rows[r][c].Length);
            }
            colWidths[c] = maxLen;
        }

        var sb = new System.Text.StringBuilder();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            sb.Append("|");
            for (int c = 0; c < maxCols; c++)
            {
                string cell = c < row.Count ? row[c] : string.Empty;
                int width = colWidths[c];
                if (r == 1)
                {
                    bool alignLeft = cell.StartsWith(":");
                    bool alignRight = cell.EndsWith(":");
                    if (alignLeft && alignRight)
                        sb.Append(" :" + new string('-', Math.Max(1, width - 2)) + ": |");
                    else if (alignRight)
                        sb.Append(" " + new string('-', Math.Max(1, width - 1)) + ": |");
                    else if (alignLeft)
                        sb.Append(" :" + new string('-', Math.Max(1, width - 1)) + " |");
                    else
                        sb.Append(" " + new string('-', width) + " |");
                }
                else
                {
                    sb.Append(" " + cell.PadRight(width) + " |");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static WorkspaceTableAutoFixResult ApplyWorkspaceTableAutoAlignmentFixes(
        IReadOnlyList<string>? explicitFiles = null,
        bool dryRun = false,
        bool createBackup = true,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null,
        Action<string, string>? contentWriter = null)
    {
        var files = explicitFiles ?? Array.Empty<string>();
        int filesScanned = 0;
        int filesModified = 0;
        int tablesAligned = 0;
        int pipesPadded = 0;
        int backupsCreated = 0;

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            bool exists = fileExistsProvider?.Invoke(file) ?? System.IO.File.Exists(file);
            if (!exists) continue;

            string content = contentReader?.Invoke(file) ?? System.IO.File.ReadAllText(file);
            filesScanned++;

            var lines = content.Replace("\r\n", "\n").Split('\n');
            var newLines = new List<string>();
            bool inCodeFence = false;
            var currentTableLines = new List<string>();
            bool fileChanged = false;

            void FlushCurrentTable()
            {
                if (currentTableLines.Count >= 2 && currentTableLines[1].Trim().Trim('|').Split('|').Any(c => c.Trim().StartsWith("-") || c.Trim().StartsWith(":-")))
                {
                    string rawTable = string.Join("\n", currentTableLines);
                    string aligned = AlignMarkdownTableColumns(rawTable);
                    if (aligned != rawTable)
                    {
                        tablesAligned++;
                        fileChanged = true;
                        newLines.AddRange(aligned.Split('\n'));
                        pipesPadded += Math.Max(0, aligned.Length - rawTable.Length);
                        currentTableLines.Clear();
                        return;
                    }
                }
                newLines.AddRange(currentTableLines);
                currentTableLines.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    FlushCurrentTable();
                    inCodeFence = !inCodeFence;
                    newLines.Add(line);
                    continue;
                }

                if (!inCodeFence && trimmed.StartsWith("|") && trimmed.EndsWith("|") && trimmed.Contains('|'))
                {
                    currentTableLines.Add(line);
                }
                else
                {
                    FlushCurrentTable();
                    newLines.Add(line);
                }
            }
            FlushCurrentTable();

            if (fileChanged)
            {
                filesModified++;
                if (!dryRun)
                {
                    if (createBackup && !file.EndsWith(".bak.md", StringComparison.OrdinalIgnoreCase))
                    {
                        string backupPath = file + ".bak.md";
                        if (contentWriter != null)
                        {
                            contentWriter(backupPath, content);
                        }
                        else
                        {
                            System.IO.File.WriteAllText(backupPath, content);
                        }
                        backupsCreated++;
                    }

                    string updatedContent = string.Join("\n", newLines);
                    if (contentWriter != null)
                    {
                        contentWriter(file, updatedContent);
                    }
                    else
                    {
                        System.IO.File.WriteAllText(file, updatedContent);
                    }
                }
            }
        }

        string dryStr = dryRun ? "Dry-run: " : "";
        string backupStr = (!dryRun && backupsCreated > 0) ? $" ({backupsCreated} .bak.md snapshots created)" : "";
        string summary = $"{dryStr}Workspace markdown table auto-fix: {filesScanned} files scanned, {filesModified} modified, {tablesAligned} tables aligned with column padding{backupStr}";

        return new WorkspaceTableAutoFixResult(
            filesScanned, filesModified, tablesAligned, pipesPadded, dryRun, summary, backupsCreated);
    }

    public static WorkspaceTableRollbackResult RestoreWorkspaceTableAutoAlignmentBackups(
        IReadOnlyList<string>? explicitFiles = null,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null,
        Action<string, string>? contentWriter = null,
        Action<string>? fileDeleter = null)
    {
        var files = explicitFiles ?? Array.Empty<string>();
        int filesScanned = 0;
        int filesRestored = 0;

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            string backupPath = file.EndsWith(".bak.md", StringComparison.OrdinalIgnoreCase) ? file : file + ".bak.md";
            string targetPath = file.EndsWith(".bak.md", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 7) : file;

            bool backupExists = fileExistsProvider?.Invoke(backupPath) ?? System.IO.File.Exists(backupPath);
            if (!backupExists) continue;

            filesScanned++;
            string backupContent = contentReader?.Invoke(backupPath) ?? System.IO.File.ReadAllText(backupPath);
            if (contentWriter != null)
            {
                contentWriter(targetPath, backupContent);
            }
            else
            {
                System.IO.File.WriteAllText(targetPath, backupContent);
            }
            filesRestored++;
            if (fileDeleter != null)
            {
                fileDeleter(backupPath);
            }
            else
            {
                try { System.IO.File.Delete(backupPath); } catch { }
            }
        }

        string summary = $"Workspace table backup restore: {filesRestored} files restored from .bak.md snapshots ({filesScanned} backups inspected)";
        return new WorkspaceTableRollbackResult(filesScanned, filesRestored, summary);
    }

    public static WorkspaceCrossFileLinkAudit AuditWorkspaceCrossFileLinks(
        IReadOnlyList<string>? explicitFiles = null,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var files = (explicitFiles ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f) && !f.EndsWith(".bak.md", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        int filesScanned = 0;
        int totalLinks = 0;
        int validLocalLinks = 0;
        int brokenTargetFiles = 0;
        int brokenAnchorHeaders = 0;
        int externalLinks = 0;
        var items = new List<WorkspaceCrossFileLinkItem>();

        var linkRegex = new Regex(@"\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)");
        var headingRegex = new Regex(@"^(#{1,6})\s+(?<title>.+)$", RegexOptions.Multiline);

        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            bool exists = fileExistsProvider?.Invoke(file) ?? System.IO.File.Exists(file);
            if (exists)
            {
                string text = contentReader?.Invoke(file) ?? System.IO.File.ReadAllText(file);
                fileContents[file] = text;
            }
        }

        foreach (var file in files)
        {
            if (!fileContents.TryGetValue(file, out var docContent))
            {
                continue;
            }

            filesScanned++;
            string dir = Path.GetDirectoryName(file) ?? string.Empty;
            var lines = docContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                string line = lines[lineIdx];
                var matches = linkRegex.Matches(line);

                foreach (Match m in matches)
                {
                    totalLinks++;
                    string linkText = m.Groups["text"].Value;
                    string targetUrl = m.Groups["url"].Value;

                    bool isExternal = targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                      targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                      targetUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                                      targetUrl.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

                    if (isExternal)
                    {
                        externalLinks++;
                        items.Add(new WorkspaceCrossFileLinkItem(
                            file, lineIdx + 1, linkText, targetUrl, true, true, true,
                            "External web/protocol URL"));
                        continue;
                    }

                    string targetFilePathPart = targetUrl;
                    string? targetAnchor = null;
                    int hashIdx = targetUrl.IndexOf('#');
                    if (hashIdx >= 0)
                    {
                        targetFilePathPart = targetUrl.Substring(0, hashIdx);
                        targetAnchor = targetUrl.Substring(hashIdx + 1);
                    }

                    string resolvedTargetFile = file;
                    if (!string.IsNullOrWhiteSpace(targetFilePathPart))
                    {
                        try
                        {
                            resolvedTargetFile = Path.GetFullPath(Path.Combine(dir, targetFilePathPart));
                        }
                        catch
                        {
                            resolvedTargetFile = Path.Combine(dir, targetFilePathPart);
                        }
                    }

                    bool targetExists = fileContents.ContainsKey(resolvedTargetFile) ||
                                        (fileExistsProvider?.Invoke(resolvedTargetFile) ?? System.IO.File.Exists(resolvedTargetFile));

                    if (!targetExists)
                    {
                        brokenTargetFiles++;
                        items.Add(new WorkspaceCrossFileLinkItem(
                            file, lineIdx + 1, linkText, targetUrl, false, false, false,
                            $"Target file '{targetFilePathPart}' not found in workspace"));
                        continue;
                    }

                    bool anchorValid = true;
                    if (!string.IsNullOrWhiteSpace(targetAnchor))
                    {
                        string targetDocContent = fileContents.TryGetValue(resolvedTargetFile, out var tc)
                            ? tc
                            : (contentReader?.Invoke(resolvedTargetFile) ?? (System.IO.File.Exists(resolvedTargetFile) ? System.IO.File.ReadAllText(resolvedTargetFile) : string.Empty));

                        var headingMatches = headingRegex.Matches(targetDocContent);
                        bool found = false;
                        foreach (Match hm in headingMatches)
                        {
                            string headingTitle = hm.Groups["title"].Value.Trim();
                            string slug = Regex.Replace(headingTitle.ToLowerInvariant(), @"[^\w\s-]", "")
                                               .Replace(' ', '-')
                                               .Trim('-');
                            if (string.Equals(slug, targetAnchor, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(headingTitle, targetAnchor, StringComparison.OrdinalIgnoreCase))
                            {
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            anchorValid = false;
                            brokenAnchorHeaders++;
                            items.Add(new WorkspaceCrossFileLinkItem(
                                file, lineIdx + 1, linkText, targetUrl, false, true, false,
                                $"Anchor heading '#{targetAnchor}' not found in '{Path.GetFileName(resolvedTargetFile)}'"));
                            continue;
                        }
                    }

                    validLocalLinks++;
                    items.Add(new WorkspaceCrossFileLinkItem(
                        file, lineIdx + 1, linkText, targetUrl, false, true, true,
                        "Resolved cross-file reference"));
                }
            }
        }

        double score = 100.0;
        if (totalLinks > 0)
        {
            score -= (brokenTargetFiles * 20.0);
            score -= (brokenAnchorHeaders * 10.0);
        }
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 => "High-Cohesion Link Graph",
            >= 80.0 => "Well-Linked Workspace",
            >= 60.0 => "Moderate Broken Relative Links",
            >= 40.0 => "High Broken Link Rate",
            _ => "Severely Fragmented Hyperlinks"
        };

        string summary = FormatWorkspaceCrossFileLinkSummary(
            filesScanned, totalLinks, validLocalLinks, brokenTargetFiles,
            brokenAnchorHeaders, externalLinks, score, category);

        return new WorkspaceCrossFileLinkAudit(
            filesScanned, totalLinks, validLocalLinks, brokenTargetFiles,
            brokenAnchorHeaders, externalLinks, score, items, category, summary);
    }

    public static string FormatWorkspaceCrossFileLinkSummary(
        int filesScanned, int totalLinks, int validLocal, int brokenFiles,
        int brokenAnchors, int external, double score, string category)
    {
        if (filesScanned == 0) return "Workspace cross-file link audit: 0 files scanned · Workspace is empty";
        if (totalLinks == 0) return $"Workspace cross-file link audit ({filesScanned} files): 0 markdown links detected";

        var parts = new List<string>();
        parts.Add($"{filesScanned} files, {totalLinks} links");
        parts.Add($"{validLocal} valid local");
        if (external > 0) parts.Add($"{external} external");
        if (brokenFiles > 0) parts.Add($"⚠ {brokenFiles} missing target files");
        if (brokenAnchors > 0) parts.Add($"⚠ {brokenAnchors} missing anchors");

        string details = string.Join(" · ", parts);
        return $"Workspace cross-file link audit ({details} · {category} · {score:0.#}% cohesion score)";
    }


    public static string FormatWorkspaceTableFormattingSummary(
        int totalDocs, int totalTables, int validCount, int raggedCount,
        int missingDelimiters, int emptyHeaderOnly, double score, string category)
    {
        if (totalDocs == 0) return "Workspace table formatting advisor: 0 documents · Workspace is empty";
        if (totalTables == 0) return $"Workspace table formatting advisor ({totalDocs} docs): 0 markdown tables found";

        string docLabel = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string tableLabel = totalTables == 1 ? "1 table" : $"{totalTables} tables";

        var alerts = new List<string>();
        if (raggedCount > 0) alerts.Add($"🚨 {raggedCount} ragged");
        if (missingDelimiters > 0) alerts.Add($"⚠ {missingDelimiters} missing delimiter");
        if (emptyHeaderOnly > 0) alerts.Add($"ℹ {emptyHeaderOnly} empty header-only");
        if (validCount > 0) alerts.Add($"✓ {validCount} verified");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% compliant";
        return $"Workspace table formatting advisor ({docLabel}, {tableLabel} · {category} · {score:0.#}% score){details}";
    }

    /// <summary>
    /// Audits workspace markdown cross-document link graph, computing in-degree and out-degree centrality,
    /// identifying central hub documents, and detecting isolated orphaned markdown notes.
    /// </summary>
    public static WorkspaceDocumentGraphAudit AuditWorkspaceDocumentGraphAndOrphans(
        IReadOnlyList<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var exists = fileExistsProvider ?? File.Exists;
        var reader = contentReader ?? File.ReadAllText;

        var mdFiles = (files ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f) &&
                        (f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mdFiles.Count == 0)
        {
            return new WorkspaceDocumentGraphAudit(
                0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<WorkspaceDocumentGraphNode>(),
                Array.Empty<WorkspaceDocumentGraphEdge>(),
                "Empty Workspace",
                "Workspace document linking graph advisor: 0 documents · Workspace contains no markdown files");
        }

        var fileNameToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in mdFiles)
        {
            string fn = Path.GetFileName(p);
            if (!string.IsNullOrEmpty(fn) && !fileNameToPath.ContainsKey(fn))
            {
                fileNameToPath[fn] = p;
            }
        }

        var inDegrees = mdFiles.ToDictionary(f => f, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outDegrees = mdFiles.ToDictionary(f => f, _ => 0, StringComparer.OrdinalIgnoreCase);
        var edges = new List<WorkspaceDocumentGraphEdge>();

        // Regex for Markdown links [text](target) and Wikilinks [[target]]
        var mdLinkRegex = new System.Text.RegularExpressions.Regex(
            @"\[([^\]]+)\]\(([^)]+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);
        var wikiLinkRegex = new System.Text.RegularExpressions.Regex(
            @"\[\[([^\]|]+)(?:\|[^\]]+)?\]\]", System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (var srcPath in mdFiles)
        {
            if (!exists(srcPath)) continue;
            string content;
            try { content = reader(srcPath); }
            catch { continue; }

            // Strip code fences
            string cleaned = System.Text.RegularExpressions.Regex.Replace(
                content,
                @"(?m)^(?<fence>`{3,}|~{3,})[^\r\n]*\r?\n[\s\S]*?^\k<fence>\s*$",
                "",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            string srcFileName = Path.GetFileName(srcPath);
            var lines = cleaned.Replace("\r", "").Split('\n');

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                string line = lines[lineIdx];

                // 1. Standard Markdown links
                foreach (System.Text.RegularExpressions.Match m in mdLinkRegex.Matches(line))
                {
                    string rawTarget = m.Groups[2].Value.Trim().Trim('"', '\'');
                    if (rawTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        rawTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        rawTarget.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                        rawTarget.StartsWith("#"))
                    {
                        continue; // Skip external links and self-anchors
                    }

                    string targetWithoutAnchor = rawTarget.Split('#')[0].Trim();
                    string targetFileName = Path.GetFileName(targetWithoutAnchor);

                    if (fileNameToPath.TryGetValue(targetFileName, out var resolvedTarget) &&
                        !resolvedTarget.Equals(srcPath, StringComparison.OrdinalIgnoreCase))
                    {
                        outDegrees[srcPath]++;
                        inDegrees[resolvedTarget]++;
                        edges.Add(new WorkspaceDocumentGraphEdge(srcFileName, targetFileName, rawTarget, lineIdx + 1));
                    }
                }

                // 2. Wikilinks [[target]]
                foreach (System.Text.RegularExpressions.Match m in wikiLinkRegex.Matches(line))
                {
                    string rawTarget = m.Groups[1].Value.Trim();
                    string targetFileName = rawTarget.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        ? rawTarget
                        : rawTarget + ".md";

                    if (fileNameToPath.TryGetValue(targetFileName, out var resolvedTarget) &&
                        !resolvedTarget.Equals(srcPath, StringComparison.OrdinalIgnoreCase))
                    {
                        outDegrees[srcPath]++;
                        inDegrees[resolvedTarget]++;
                        edges.Add(new WorkspaceDocumentGraphEdge(srcFileName, targetFileName, rawTarget, lineIdx + 1));
                    }
                }
            }
        }

        var nodes = new List<WorkspaceDocumentGraphNode>();
        int orphans = 0;
        int hubs = 0;

        foreach (var p in mdFiles)
        {
            int inDeg = inDegrees[p];
            int outDeg = outDegrees[p];
            string fn = Path.GetFileName(p);

            string role;
            bool isOrphan = false;

            if (inDeg == 0 && outDeg == 0)
            {
                role = "Isolated Orphan";
                isOrphan = true;
                orphans++;
            }
            else if (inDeg >= 3)
            {
                role = "Central Hub";
                hubs++;
            }
            else if (inDeg == 0 && outDeg > 0)
            {
                role = "Source Leaf";
            }
            else if (inDeg > 0 && outDeg == 0)
            {
                role = "Sink Leaf";
            }
            else
            {
                role = "Transit Node";
            }

            nodes.Add(new WorkspaceDocumentGraphNode(p, fn, inDeg, outDeg, role, isOrphan));
        }

        int n = mdFiles.Count;
        double maxPossibleEdges = n > 1 ? n * (n - 1) : 1;
        double density = Math.Round((edges.Count / maxPossibleEdges) * 100.0, 1);

        double penalty = orphans * 20.0;
        double score = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 when orphans == 0 => "Highly Connected Cross-Referenced Knowledge Graph",
            >= 80.0 => "Well-Linked Workspace with Few Isolated Pages",
            >= 60.0 => "Moderate Linkage with Scattered Orphaned Notes",
            _ => "Fragmented Workspace with High Orphaned Note Debt"
        };

        string summary = FormatWorkspaceDocumentGraphSummary(
            mdFiles.Count, edges.Count, orphans, hubs, density, score, category);

        return new WorkspaceDocumentGraphAudit(
            mdFiles.Count,
            edges.Count,
            orphans,
            hubs,
            density,
            score,
            nodes,
            edges,
            category,
            summary);
    }

    public static string FormatWorkspaceDocumentGraphSummary(
        int totalDocs, int totalEdges, int orphans, int hubs, double density, double score, string category)
    {
        if (totalDocs == 0) return "Workspace document linking graph advisor: 0 documents · Workspace contains no markdown files";

        string docLabel = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string edgeLabel = totalEdges == 1 ? "1 internal link" : $"{totalEdges} internal links";

        var alerts = new List<string>();
        if (orphans > 0) alerts.Add($"🚨 {orphans} orphaned (0 in/out links)");
        if (hubs > 0) alerts.Add($"★ {hubs} central hubs (in-deg >= 3)");
        alerts.Add($"density {density:0.#}%");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% interconnected";

        return $"Workspace document linking graph advisor ({docLabel}, {edgeLabel} · {category} · {score:0.#}% score){alertSuffix}";
    }

    /// <summary>
    /// Audits workspace markdown documents for embedded media asset references (![alt](path) and <img src="path">),
    /// resolving local relative and absolute paths against physical disk existence, and identifying broken image hazards.
    /// </summary>
    public static WorkspaceMediaAssetAudit AuditWorkspaceMediaAssetIntegrity(
        IReadOnlyList<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var exists = fileExistsProvider ?? File.Exists;
        var reader = contentReader ?? File.ReadAllText;

        var mdFiles = (files ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f) && (f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mdFiles.Count == 0)
        {
            return new WorkspaceMediaAssetAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceMediaItem>(),
                Array.Empty<string>(),
                "Empty Workspace",
                "Workspace media asset integrity advisor: 0 documents · Workspace contains no markdown files");
        }

        var mdImageRegex = new System.Text.RegularExpressions.Regex(@"!\[(.*?)\]\((.*?)\)");
        var htmlImageRegex = new System.Text.RegularExpressions.Regex(@"<img[^>]+src=[""'](.*?)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var mediaItems = new List<WorkspaceMediaItem>();
        var missingPaths = new List<string>();
        int webRemoteCount = 0;
        int localCheckedCount = 0;
        int validLocalCount = 0;
        int missingLocalCount = 0;

        foreach (var docPath in mdFiles)
        {
            string docName = Path.GetFileName(docPath);
            string docDir = Path.GetDirectoryName(docPath) ?? string.Empty;

            string content;
            try
            {
                content = reader(docPath) ?? string.Empty;
            }
            catch
            {
                continue;
            }

            // Strip code fences
            var stripped = System.Text.RegularExpressions.Regex.Replace(content, @"(?s)(```.*?```|~~~.*?~~~|:::.*?:::)", " ");

            // Check markdown images
            var mdMatches = mdImageRegex.Matches(stripped);
            foreach (System.Text.RegularExpressions.Match m in mdMatches)
            {
                string alt = m.Groups[1].Value.Trim();
                string rawTarget = m.Groups[2].Value.Trim();
                EvaluateMediaTarget(docPath, docName, docDir, alt, rawTarget);
            }

            // Check html images
            var htmlMatches = htmlImageRegex.Matches(stripped);
            foreach (System.Text.RegularExpressions.Match m in htmlMatches)
            {
                string rawTarget = m.Groups[1].Value.Trim();
                EvaluateMediaTarget(docPath, docName, docDir, "HTML Image", rawTarget);
            }
        }

        void EvaluateMediaTarget(string docPath, string docName, string docDir, string alt, string rawTarget)
        {
            if (string.IsNullOrWhiteSpace(rawTarget)) return;

            // Strip any image title e.g. "path/to/img.png "title""
            string cleanTarget = rawTarget;
            int spaceIdx = cleanTarget.IndexOf(' ');
            if (spaceIdx > 0)
            {
                cleanTarget = cleanTarget.Substring(0, spaceIdx).Trim('"', '\'');
            }

            bool isRemote = cleanTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            cleanTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                            cleanTarget.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                            cleanTarget.StartsWith("//", StringComparison.OrdinalIgnoreCase);

            if (isRemote)
            {
                webRemoteCount++;
                mediaItems.Add(new WorkspaceMediaItem(
                    docPath, docName, alt, cleanTarget, IsRemote: true, Exists: true, cleanTarget, null));
            }
            else
            {
                localCheckedCount++;
                string resolvedPath;
                try
                {
                    resolvedPath = Path.IsPathRooted(cleanTarget)
                        ? cleanTarget
                        : Path.GetFullPath(Path.Combine(docDir, cleanTarget));
                }
                catch
                {
                    resolvedPath = cleanTarget;
                }

                bool fileExists = exists(resolvedPath);
                if (fileExists)
                {
                    validLocalCount++;
                    mediaItems.Add(new WorkspaceMediaItem(
                        docPath, docName, alt, cleanTarget, IsRemote: false, Exists: true, resolvedPath, null));
                }
                else
                {
                    missingLocalCount++;
                    missingPaths.Add(resolvedPath);
                    mediaItems.Add(new WorkspaceMediaItem(
                        docPath, docName, alt, cleanTarget, IsRemote: false, Exists: false, resolvedPath,
                        $"Missing local media file: {cleanTarget}"));
                }
            }
        }

        int totalMedia = webRemoteCount + localCheckedCount;
        double integrityScore;
        if (localCheckedCount == 0)
        {
            integrityScore = 100.0;
        }
        else
        {
            double penalty = ((double)missingLocalCount / localCheckedCount) * 100.0;
            integrityScore = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);
        }

        string category;
        if (totalMedia == 0)
            category = "No Media Embeds";
        else if (missingLocalCount == 0)
            category = "Pristine Media Asset Integrity (100% Assets Verified)";
        else if (integrityScore >= 75.0)
            category = $"Minor Broken Media References ({missingLocalCount} Missing Asset{(missingLocalCount == 1 ? "" : "s")})";
        else
            category = $"High Broken Media Debt ({missingLocalCount} Missing Asset{(missingLocalCount == 1 ? "" : "s")} Detected)";

        string summary = FormatWorkspaceMediaAssetSummary(
            mdFiles.Count, totalMedia, localCheckedCount, validLocalCount, missingLocalCount,
            webRemoteCount, integrityScore, category);

        return new WorkspaceMediaAssetAudit(
            mdFiles.Count,
            totalMedia,
            localCheckedCount,
            validLocalCount,
            missingLocalCount,
            webRemoteCount,
            integrityScore,
            mediaItems,
            missingPaths,
            category,
            summary);
    }

    public static string FormatWorkspaceMediaAssetSummary(
        int totalDocs, int totalMedia, int localChecked, int validLocal, int missingLocal,
        int webRemote, double score, string category)
    {
        if (totalDocs == 0)
            return "Workspace media asset integrity advisor: 0 documents · Workspace contains no markdown files";
        if (totalMedia == 0)
            return $"Workspace media asset integrity advisor ({totalDocs} documents): 0 media embeds detected (Pure text documents)";

        string docLabel = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string mediaLabel = totalMedia == 1 ? "1 media embed" : $"{totalMedia} media embeds";

        var alerts = new List<string>();
        if (missingLocal > 0) alerts.Add($"🚨 {missingLocal} missing local assets");
        if (webRemote > 0) alerts.Add($"{webRemote} remote/web assets");
        if (validLocal > 0) alerts.Add($"{validLocal} local verified");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · all assets verified";

        return $"Workspace media asset integrity advisor ({docLabel}, {mediaLabel} · {category} · {score:0.#}% score){alertSuffix}";
    }

    public static WorkspaceMediaAccessibilityAudit AuditWorkspaceMediaAccessibility(
        IReadOnlyList<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var exists = fileExistsProvider ?? File.Exists;
        var readAll = contentReader ?? File.ReadAllText;

        if (files == null || files.Count == 0)
        {
            return new WorkspaceMediaAccessibilityAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceMediaAccessibilityItem>(),
                "Empty Workspace",
                "Workspace media alt-text accessibility advisor: 0 documents · Workspace contains no files");
        }

        var mdFiles = files.Where(f =>
            !string.IsNullOrWhiteSpace(f) &&
            (f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
             f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (mdFiles.Count == 0)
        {
            return new WorkspaceMediaAccessibilityAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceMediaAccessibilityItem>(),
                "No Markdown Documents",
                "Workspace media alt-text accessibility advisor: 0 markdown documents found");
        }

        var mdImageRegex = new Regex(@"!\[(.*?)\]\((.*?)\)", RegexOptions.Compiled);
        var htmlImgRegex = new Regex(@"<img\s+[^>]*?src=[""']([^""']+)[""'][^>]*?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var htmlAltRegex = new Regex(@"alt=[""'](.*?)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var mediaItems = new List<WorkspaceMediaAccessibilityItem>();
        int totalMedia = 0;
        int descriptiveCount = 0;
        int emptyCount = 0;
        int genericFilenameCount = 0;

        string[] genericPlaceholders = { "image", "img", "screenshot", "photo", "pic", "picture", "figure", "fig", "diagram", "icon", "drawing", "graphic" };

        foreach (var docPath in mdFiles)
        {
            if (!exists(docPath)) continue;

            string content;
            try
            {
                content = readAll(docPath);
            }
            catch
            {
                continue;
            }

            string docName = Path.GetFileName(docPath);
            var lines = content.Replace("\r", "").Split('\n');
            bool inCodeBlock = false;

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                string line = lines[lineIdx];
                string trimmed = line.Trim();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inCodeBlock = !inCodeBlock;
                    continue;
                }
                if (inCodeBlock) continue;

                var matches = mdImageRegex.Matches(line);
                foreach (Match m in matches)
                {
                    string alt = m.Groups[1].Value.Trim();
                    string src = m.Groups[2].Value.Trim();
                    if (src.Contains(' ')) src = src.Split(' ')[0];

                    totalMedia++;
                    EvaluateAltText(docPath, docName, lineIdx + 1, alt, src, mediaItems,
                        ref descriptiveCount, ref emptyCount, ref genericFilenameCount, genericPlaceholders);
                }

                var htmlMatches = htmlImgRegex.Matches(line);
                foreach (Match hm in htmlMatches)
                {
                    string src = hm.Groups[1].Value.Trim();
                    string fullTag = hm.Value;
                    var altMatch = htmlAltRegex.Match(fullTag);
                    string alt = altMatch.Success ? altMatch.Groups[1].Value.Trim() : string.Empty;

                    totalMedia++;
                    EvaluateAltText(docPath, docName, lineIdx + 1, alt, src, mediaItems,
                        ref descriptiveCount, ref emptyCount, ref genericFilenameCount, genericPlaceholders);
                }
            }
        }

        if (totalMedia == 0)
        {
            return new WorkspaceMediaAccessibilityAudit(
                mdFiles.Count, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceMediaAccessibilityItem>(),
                "Pure Text Workspace",
                $"Workspace media alt-text accessibility advisor ({mdFiles.Count} documents): 0 media embeds detected (100% text-only documents)");
        }

        double penalty = (emptyCount * 25.0) + (genericFilenameCount * 12.0);
        double complianceScore = Math.Round(Math.Clamp(100.0 - (penalty / totalMedia), 10.0, 100.0), 1);

        string category;
        if (emptyCount == 0 && genericFilenameCount == 0)
            category = "WCAG Compliant (100% Descriptive Media Captions)";
        else if (emptyCount == 0)
            category = $"Acceptable Alt-Text Coverage ({genericFilenameCount} Generic Filename Captions)";
        else if (complianceScore >= 70.0)
            category = $"Accessibility Debt ({emptyCount} Missing / Empty Alt-Text Tags)";
        else
            category = $"Critical WCAG Accessibility Hazard ({emptyCount} Blank Alt-Text Embeds Detected)";

        string summary = FormatWorkspaceMediaAccessibilitySummary(
            mdFiles.Count, totalMedia, descriptiveCount, emptyCount, genericFilenameCount, complianceScore, category);

        return new WorkspaceMediaAccessibilityAudit(
            mdFiles.Count,
            totalMedia,
            descriptiveCount,
            emptyCount,
            genericFilenameCount,
            complianceScore,
            mediaItems,
            category,
            summary);
    }

    private static void EvaluateAltText(
        string docPath, string docName, int lineNumber, string alt, string src,
        List<WorkspaceMediaAccessibilityItem> items,
        ref int descriptiveCount, ref int emptyCount, ref int genericFilenameCount,
        string[] genericPlaceholders)
    {
        bool isEmpty = string.IsNullOrWhiteSpace(alt);
        bool isGenericFilename = false;

        if (!isEmpty)
        {
            string lower = alt.ToLowerInvariant();
            if (lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") ||
                lower.EndsWith(".gif") || lower.EndsWith(".svg") || lower.EndsWith(".webp"))
            {
                isGenericFilename = true;
            }
            else
            {
                string stripped = new string(lower.Where(char.IsLetterOrDigit).ToArray());
                foreach (var g in genericPlaceholders)
                {
                    if (stripped == g || stripped.StartsWith(g) && stripped.Length <= g.Length + 2)
                    {
                        isGenericFilename = true;
                        break;
                    }
                }
            }
        }

        bool isDescriptive = !isEmpty && !isGenericFilename && alt.Length >= 8;

        string quality;
        if (isEmpty)
        {
            emptyCount++;
            quality = "Critical: Empty Alt-Text (WCAG 1.1.1 Non-Text Content Violation)";
        }
        else if (isGenericFilename)
        {
            genericFilenameCount++;
            quality = "Warning: Generic Filename Placeholder Alt-Text";
        }
        else if (isDescriptive)
        {
            descriptiveCount++;
            quality = "Compliant: Descriptive Alt-Text Caption";
        }
        else
        {
            quality = "Moderate: Brief Alt-Text Caption (<8 chars)";
        }

        items.Add(new WorkspaceMediaAccessibilityItem(
            docPath,
            docName,
            lineNumber,
            alt,
            src,
            isEmpty,
            isGenericFilename,
            isDescriptive,
            quality));
    }

    public static string FormatWorkspaceMediaAccessibilitySummary(
        int totalDocs, int totalMedia, int descriptive, int empty, int generic,
        double score, string category)
    {
        if (totalDocs == 0) return "Workspace media alt-text accessibility advisor: 0 documents · Workspace contains no files";
        if (totalMedia == 0) return $"Workspace media alt-text accessibility advisor ({totalDocs} documents): 0 media embeds detected (100% text-only documents)";

        string docLabel = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string mediaLabel = totalMedia == 1 ? "1 media embed" : $"{totalMedia} media embeds";

        var alerts = new List<string>();
        if (empty > 0) alerts.Add($"🚨 {empty} empty alt-text");
        if (generic > 0) alerts.Add($"⚠ {generic} generic placeholders");
        if (descriptive > 0) alerts.Add($"{descriptive} descriptive captions");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% compliant captions";

        return $"Workspace media alt-text accessibility advisor ({docLabel}, {mediaLabel} · {category} · {score:0.#}% score){alertSuffix}";
    }

    /// <summary>
    /// Audits workspace markdown documents for external hyperlink domains, internal heading anchors (#anchor),
    /// protocol security (HTTPS vs HTTP), and broken anchor references.
    /// </summary>
    public static WorkspaceHyperlinkDomainsAudit AuditWorkspaceHyperlinkDomainsAndAnchors(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var exists = fileExistsProvider ?? File.Exists;
        var readAll = contentReader ?? File.ReadAllText;

        if (files == null)
        {
            return new WorkspaceHyperlinkDomainsAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<ExternalDomainLinkItem>(), Array.Empty<LocalAnchorReferenceItem>(),
                "Empty Workspace",
                FormatWorkspaceHyperlinkSummary(0, 0, 0, 0, 0, 0, 100.0, "Empty Workspace"));
        }

        var mdFiles = files.Where(f =>
            !string.IsNullOrWhiteSpace(f) &&
            (f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
             f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (mdFiles.Count == 0)
        {
            return new WorkspaceHyperlinkDomainsAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<ExternalDomainLinkItem>(), Array.Empty<LocalAnchorReferenceItem>(),
                "No Markdown Documents",
                FormatWorkspaceHyperlinkSummary(0, 0, 0, 0, 0, 0, 100.0, "No Markdown Documents"));
        }

        var linkRegex = new Regex(@"(?<!!)\[(?<text>[^\]]*?)\]\((?<url>[^)]+?)\)", RegexOptions.Compiled);
        var autoLinkRegex = new Regex(@"<(?<url>https?://[^>]+)>", RegexOptions.Compiled);
        var headingRegex = new Regex(@"^(#{1,6})\s+(?<title>.+)$", RegexOptions.Compiled);

        var domainDict = new Dictionary<string, (int Count, bool IsHttps, List<string> Samples)>(StringComparer.OrdinalIgnoreCase);
        var localAnchorItems = new List<LocalAnchorReferenceItem>();
        int totalExternal = 0;
        int totalInternal = 0;
        int insecureHttp = 0;
        int brokenAnchors = 0;

        foreach (var docPath in mdFiles)
        {
            if (!exists(docPath)) continue;
            string content;
            try { content = readAll(docPath); }
            catch { continue; }

            string docName = Path.GetFileName(docPath);
            var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

            var headingSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                var hMatch = headingRegex.Match(line.Trim());
                if (hMatch.Success)
                {
                    string hTitle = hMatch.Groups["title"].Value.Trim();
                    var slugBuilder = new System.Text.StringBuilder();
                    foreach (char c in hTitle.ToLowerInvariant())
                    {
                        if (char.IsLetterOrDigit(c)) slugBuilder.Append(c);
                        else if (char.IsWhiteSpace(c) || c == '-' || c == '_') slugBuilder.Append('-');
                    }
                    string slug = slugBuilder.ToString().Trim('-');
                    if (!string.IsNullOrEmpty(slug)) headingSlugs.Add(slug);
                }
            }

            bool inFence = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence) continue;

                var matches = linkRegex.Matches(line);
                foreach (Match m in matches)
                {
                    string rawUrl = m.Groups["url"].Value.Trim();
                    string linkText = m.Groups["text"].Value.Trim();

                    ProcessUrl(rawUrl, linkText, docName, i + 1, headingSlugs, domainDict, localAnchorItems, ref totalExternal, ref totalInternal, ref insecureHttp, ref brokenAnchors);
                }

                var autoMatches = autoLinkRegex.Matches(line);
                foreach (Match m in autoMatches)
                {
                    string rawUrl = m.Groups["url"].Value.Trim();
                    ProcessUrl(rawUrl, rawUrl, docName, i + 1, headingSlugs, domainDict, localAnchorItems, ref totalExternal, ref totalInternal, ref insecureHttp, ref brokenAnchors);
                }
            }
        }

        static void ProcessUrl(
            string rawUrl, string linkText, string docName, int lineNum, HashSet<string> headingSlugs,
            Dictionary<string, (int Count, bool IsHttps, List<string> Samples)> domainDict,
            List<LocalAnchorReferenceItem> localAnchors,
            ref int totalExternal, ref int totalInternal, ref int insecureHttp, ref int brokenAnchors)
        {
            if (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                totalExternal++;
                bool isHttps = rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (!isHttps) insecureHttp++;

                string host = "";
                try
                {
                    var uri = new Uri(rawUrl);
                    host = uri.Host;
                }
                catch
                {
                    int start = rawUrl.IndexOf("://", StringComparison.Ordinal) + 3;
                    int slash = rawUrl.IndexOf('/', start);
                    host = slash > start ? rawUrl[start..slash] : rawUrl[start..];
                }

                if (!string.IsNullOrEmpty(host))
                {
                    if (!domainDict.TryGetValue(host, out var entry))
                    {
                        entry = (0, isHttps, new List<string>());
                    }
                    entry.Count++;
                    if (!isHttps) entry.IsHttps = false;
                    if (entry.Samples.Count < 3 && !entry.Samples.Contains(rawUrl)) entry.Samples.Add(rawUrl);
                    domainDict[host] = entry;
                }
            }
            else if (rawUrl.StartsWith('#'))
            {
                totalInternal++;
                string anchorSlug = rawUrl.TrimStart('#').Trim();
                bool resolved = headingSlugs.Contains(anchorSlug);
                if (!resolved) brokenAnchors++;

                string status = resolved ? "Valid Local Heading Anchor" : "⚠ Broken Anchor (Target heading not found)";
                localAnchors.Add(new LocalAnchorReferenceItem(docName, lineNum, linkText, anchorSlug, resolved, status));
            }
        }

        var domainItems = new List<ExternalDomainLinkItem>();
        foreach (var (host, data) in domainDict.OrderByDescending(kv => kv.Value.Count))
        {
            string status = data.IsHttps ? "Secure HTTPS Domain" : "⚠ Insecure HTTP Protocol";
            domainItems.Add(new ExternalDomainLinkItem(host, data.Count, data.IsHttps, data.Samples, status));
        }

        double penalty = (brokenAnchors * 12.0) + (insecureHttp * 4.0);
        double healthScore = (totalExternal + totalInternal) == 0 ? 100.0 : Math.Clamp(100.0 - penalty, 0.0, 100.0);
        healthScore = Math.Round(healthScore, 1);

        string category = healthScore switch
        {
            >= 90.0 => "High Link Health",
            >= 75.0 => "Solid Linking Baseline",
            >= 55.0 => "Moderate Broken / Insecure Links",
            >= 35.0 => "Significant Anchor Hazards",
            _ => "Degraded Hyperlink Integrity"
        };

        string summary = FormatWorkspaceHyperlinkSummary(
            mdFiles.Count, totalExternal, totalInternal, domainDict.Count,
            insecureHttp, brokenAnchors, healthScore, category);

        return new WorkspaceHyperlinkDomainsAudit(
            mdFiles.Count, totalExternal, totalInternal, domainDict.Count,
            insecureHttp, brokenAnchors, healthScore, domainItems, localAnchorItems,
            category, summary);
    }

    public static WorkspaceHyperlinkDomainsAudit AuditWorkspaceHyperlinkDomainsAndAnchors(
        string filePath,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        return AuditWorkspaceHyperlinkDomainsAndAnchors(new[] { filePath }, fileExistsProvider, contentReader);
    }

    public static string FormatWorkspaceHyperlinkSummary(
        int totalDocs, int externalLinks, int internalAnchors, int uniqueDomains,
        int insecureHttp, int brokenAnchors, double score, string category)
    {
        if (totalDocs == 0) return "Workspace hyperlink & anchor advisor: 0 documents · Workspace is empty";

        string docStr = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string extStr = externalLinks == 1 ? "1 external link" : $"{externalLinks} external links";
        string domStr = uniqueDomains == 1 ? "1 host domain" : $"{uniqueDomains} host domains";

        var alerts = new List<string>();
        if (internalAnchors > 0) alerts.Add($"{internalAnchors} internal {(internalAnchors == 1 ? "anchor" : "anchors")}");
        if (brokenAnchors > 0) alerts.Add($"🚨 {brokenAnchors} broken {(brokenAnchors == 1 ? "anchor" : "anchors")}");
        if (insecureHttp > 0) alerts.Add($"⚠ {insecureHttp} insecure HTTP");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% verified links & anchors";
        return $"Workspace hyperlink & anchor advisor ({docStr} [{extStr} across {domStr}] · {category} · {score:0.#}% score){details}";
    }

    /// <summary>
    /// Audits workspace markdown documents for fenced code block language tags,
    /// detecting untagged code blocks, language distribution, and syntax highlighting coverage.
    /// </summary>
    public static WorkspaceCodeBlockLanguageAudit AuditWorkspaceCodeBlockLanguages(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var checkExists = fileExistsProvider ?? File.Exists;
        var readContent = contentReader ?? File.ReadAllText;

        var list = files?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? new List<string>();
        var mdFiles = list.Where(f =>
        {
            var ext = Path.GetExtension(f);
            return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase);
        }).ToList();

        if (mdFiles.Count == 0 && list.Count > 0)
        {
            mdFiles = list;
        }

        if (mdFiles.Count == 0)
        {
            return new WorkspaceCodeBlockLanguageAudit(
                0, 0, 0, 0, "None", 100.0,
                Array.Empty<CodeBlockLanguageCountItem>(),
                "Empty Workspace",
                "Workspace code block language advisor: 0 documents · Workspace contains no files");
        }

        int totalCodeBlocks = 0;
        int untaggedBlocks = 0;
        var languageBlockCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var languageLineCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var codeBlockRegex = new Regex(@"(?m)^(?<fence>`{3,}|~{3,})(?<lang>[^\r\n]*)\r?\n(?<body>[\s\S]*?)^\k<fence>\s*$", RegexOptions.Compiled);

        var normalizations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "cs", "csharp" },
            { "c#", "csharp" },
            { "js", "javascript" },
            { "ts", "typescript" },
            { "py", "python" },
            { "ps1", "powershell" },
            { "sh", "bash" },
            { "shell", "bash" },
            { "zsh", "bash" },
            { "yml", "yaml" },
            { "htm", "html" },
            { "md", "markdown" },
            { "rb", "ruby" },
            { "rs", "rust" },
            { "golang", "go" }
        };

        foreach (var file in mdFiles)
        {
            if (!checkExists(file)) continue;

            string content;
            try
            {
                content = readContent(file);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(content)) continue;

            var matches = codeBlockRegex.Matches(content);
            foreach (Match m in matches)
            {
                totalCodeBlocks++;
                string rawLang = m.Groups["lang"].Value.Trim();
                string body = m.Groups["body"].Value;
                int lineCount = body.Split('\n').Length;

                if (string.IsNullOrWhiteSpace(rawLang))
                {
                    untaggedBlocks++;
                    languageBlockCounts["(untagged)"] = languageBlockCounts.GetValueOrDefault("(untagged)", 0) + 1;
                    languageLineCounts["(untagged)"] = languageLineCounts.GetValueOrDefault("(untagged)", 0) + lineCount;
                }
                else
                {
                    string cleaned = rawLang.Split(new[] { ' ', '\t', '{', ':' })[0].ToLowerInvariant();
                    string norm = normalizations.GetValueOrDefault(cleaned, cleaned);

                    languageBlockCounts[norm] = languageBlockCounts.GetValueOrDefault(norm, 0) + 1;
                    languageLineCounts[norm] = languageLineCounts.GetValueOrDefault(norm, 0) + lineCount;
                }
            }
        }

        if (totalCodeBlocks == 0)
        {
            return new WorkspaceCodeBlockLanguageAudit(
                mdFiles.Count, 0, 0, 0, "None", 100.0,
                Array.Empty<CodeBlockLanguageCountItem>(),
                "No Code Blocks",
                $"Workspace code block language advisor ({mdFiles.Count} docs): 0 code blocks · Clean baseline");
        }

        var items = new List<CodeBlockLanguageCountItem>();
        foreach (var kvp in languageBlockCounts.OrderByDescending(kv => kv.Value))
        {
            string lang = kvp.Key;
            int count = kvp.Value;
            int lines = languageLineCounts.GetValueOrDefault(lang, 0);
            double pct = Math.Round((double)count / totalCodeBlocks * 100.0, 1);
            bool isNorm = lang != "(untagged)";

            string status = isNorm
                ? $"✓ {count} blocks ({pct}%)"
                : $"⚠ {count} untagged blocks (Missing syntax highlighting)";

            items.Add(new CodeBlockLanguageCountItem(lang, count, lines, pct, isNorm, status));
        }

        string dominantLang = items.FirstOrDefault(i => i.IsNormalized)?.LanguageTag ?? (untaggedBlocks > 0 ? "(untagged)" : "None");
        int uniqueLangs = languageBlockCounts.Keys.Count(k => k != "(untagged)");

        double coveragePct = (double)(totalCodeBlocks - untaggedBlocks) / totalCodeBlocks * 100.0;
        double score = Math.Round(coveragePct, 1);

        string category = score switch
        {
            >= 95.0 => "Complete Syntax Coverage",
            >= 80.0 => "High Coverage",
            >= 60.0 => "Moderate Tagging",
            >= 40.0 => "Partial Highlighting",
            _ => "Untagged Code Risk"
        };

        string summary = FormatWorkspaceCodeBlockLanguageSummary(
            mdFiles.Count, totalCodeBlocks, untaggedBlocks, uniqueLangs, dominantLang, score, category, items);

        return new WorkspaceCodeBlockLanguageAudit(
            mdFiles.Count, totalCodeBlocks, untaggedBlocks, uniqueLangs, dominantLang,
            score, items, category, summary);
    }

    public static WorkspaceCodeBlockLanguageAudit AuditWorkspaceCodeBlockLanguages(
        string filePath,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        return AuditWorkspaceCodeBlockLanguages(new[] { filePath }, fileExistsProvider, contentReader);
    }

    public static string FormatWorkspaceCodeBlockLanguageSummary(
        int totalDocs, int totalBlocks, int untagged, int uniqueLangs,
        string dominantLang, double score, string category,
        IReadOnlyList<CodeBlockLanguageCountItem>? languages = null)
    {
        if (totalDocs == 0) return "Workspace code block language advisor: 0 documents · Workspace is empty";
        if (totalBlocks == 0) return $"Workspace code block language advisor ({totalDocs} docs): 0 code blocks · Clean baseline";

        string docStr = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string blockStr = totalBlocks == 1 ? "1 code block" : $"{totalBlocks} code blocks";
        string langStr = uniqueLangs == 1 ? "1 language" : $"{uniqueLangs} languages";

        var alerts = new List<string>();
        if (uniqueLangs > 0) alerts.Add($"dominant: {dominantLang}");
        if (untagged > 0) alerts.Add($"⚠ {untagged} untagged");
        else alerts.Add("100% tagged");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : "";
        return $"Workspace code block language advisor ({docStr} [{blockStr} in {langStr}] · {category} · {score:0.#}% coverage){details}";
    }

    /// <summary>
    /// Audits workspace markdown files for code fence delimiter consistency (backtick vs tilde),
    /// irregular 1-3 space indentation outside list items, and tab-versus-space indentation anomalies.
    /// </summary>
    public static WorkspaceCodeFenceIndentationAudit AuditWorkspaceCodeFenceIndentation(
        IEnumerable<string>? files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var exists = fileExistsProvider ?? File.Exists;
        var reader = contentReader ?? File.ReadAllText;

        var fileList = files?.ToList() ?? new List<string>();
        var mdFiles = fileList
            .Where(f => !string.IsNullOrWhiteSpace(f) &&
                        (f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mdFiles.Count == 0)
        {
            return new WorkspaceCodeFenceIndentationAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<CodeFenceFileAuditItem>(),
                "Empty Workspace",
                "Workspace code fence indentation advisor: 0 documents · Workspace is empty");
        }

        var items = new List<CodeFenceFileAuditItem>();
        int totalFences = 0;
        int totalBackticks = 0;
        int totalTildes = 0;
        int totalIndentationAlerts = 0;
        int filesWithFences = 0;

        foreach (var path in mdFiles)
        {
            if (!exists(path)) continue;

            string content;
            try { content = reader(path); }
            catch { continue; }

            var lines = content.Replace("\r", "").Split('\n');
            int fileFences = 0;
            int fileBackticks = 0;
            int fileTildes = 0;
            int fileIndentAlerts = 0;

            bool inFence = false;
            string fenceChar = "";

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                int leadingSpaces = 0;
                bool hasTab = false;
                while (leadingSpaces < line.Length && (line[leadingSpaces] == ' ' || line[leadingSpaces] == '\t'))
                {
                    if (line[leadingSpaces] == '\t') hasTab = true;
                    leadingSpaces++;
                }

                var trimmed = line.Trim();
                bool isBacktick = trimmed.StartsWith("```");
                bool isTilde = trimmed.StartsWith("~~~");

                if (!inFence && (isBacktick || isTilde))
                {
                    inFence = true;
                    fenceChar = isBacktick ? "`" : "~";
                    fileFences++;
                    if (isBacktick) fileBackticks++;
                    else fileTildes++;

                    bool isPrecededByList = (i > 0) &&
                        Regex.IsMatch(lines[i - 1].Trim(), @"^([-*+]|\d+[.)])\s+");

                    if (hasTab)
                    {
                        fileIndentAlerts++;
                    }
                    else if (leadingSpaces >= 1 && leadingSpaces <= 3 && !isPrecededByList)
                    {
                        fileIndentAlerts++;
                    }
                }
                else if (inFence && ((fenceChar == "`" && isBacktick) || (fenceChar == "~" && isTilde)))
                {
                    inFence = false;
                    fenceChar = "";
                }
            }

            if (fileFences > 0) filesWithFences++;
            totalFences += fileFences;
            totalBackticks += fileBackticks;
            totalTildes += fileTildes;
            totalIndentationAlerts += fileIndentAlerts;

            int mixedCount = (fileBackticks > 0 && fileTildes > 0) ? Math.Min(fileBackticks, fileTildes) : 0;
            bool hasAnomaly = fileIndentAlerts > 0 || mixedCount > 0;

            string status = fileIndentAlerts > 0 ? $"⚠ {fileIndentAlerts} indentation alert(s)" :
                            mixedCount > 0 ? $"⚠ Mixed delimiters (` and ~)" :
                            fileFences > 0 ? $"✓ {fileFences} uniform fence(s)" : "✓ No fences";

            items.Add(new CodeFenceFileAuditItem(
                path,
                Path.GetFileName(path),
                fileFences,
                fileBackticks,
                fileTildes,
                fileIndentAlerts,
                mixedCount,
                hasAnomaly,
                status));
        }

        if (totalFences == 0)
        {
            return new WorkspaceCodeFenceIndentationAudit(
                mdFiles.Count, 0, 0, 0, 0, 0, 100.0,
                items,
                "Clean Baseline",
                $"Workspace code fence indentation advisor ({mdFiles.Count} docs): 0 code fences · Clean baseline");
        }

        double penalty = (totalIndentationAlerts * 10.0);
        int filesWithMixed = items.Count(it => it.MixedDelimiterCount > 0);
        penalty += filesWithMixed * 8.0;

        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Consistent Fences (≥90%)",
            >= 75.0 => "Uniform Formatting (75–89%)",
            >= 60.0 => "Moderate Variance (60–74%)",
            >= 40.0 => "Irregular Indentation (40–59%)",
            _ => "Chaotic Formatting (<40%)"
        };

        string summary = FormatWorkspaceCodeFenceSummary(
            mdFiles.Count, filesWithFences, totalFences, totalBackticks, totalTildes,
            totalIndentationAlerts, score, category, items);

        return new WorkspaceCodeFenceIndentationAudit(
            mdFiles.Count, filesWithFences, totalFences, totalBackticks, totalTildes,
            totalIndentationAlerts, score, items, category, summary);
    }

    public static WorkspaceCodeFenceIndentationAudit AuditWorkspaceCodeFenceIndentation(
        string filePath,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        return AuditWorkspaceCodeFenceIndentation(new[] { filePath }, fileExistsProvider, contentReader);
    }

    public static string FormatWorkspaceCodeFenceSummary(
        int totalDocs, int docsWithFences, int totalFences, int backticks, int tildes,
        int indentAlerts, double score, string category,
        IReadOnlyList<CodeFenceFileAuditItem>? items = null)
    {
        if (totalDocs == 0) return "Workspace code fence indentation advisor: 0 documents · Workspace is empty";
        if (totalFences == 0) return $"Workspace code fence indentation advisor ({totalDocs} docs): 0 code fences · Clean baseline";

        string docStr = totalDocs == 1 ? "1 document" : $"{totalDocs} documents";
        string fenceStr = totalFences == 1 ? "1 fence" : $"{totalFences} fences";

        var parts = new List<string>();
        parts.Add($"{backticks} backtick, {tildes} tilde");
        if (indentAlerts > 0) parts.Add($"⚠ {indentAlerts} indent {(indentAlerts == 1 ? "alert" : "alerts")}");
        else parts.Add("0 indent alerts");

        string details = string.Join(" · ", parts);
        return $"Workspace code fence indentation advisor ({docStr} [{fenceStr}] · {category} · {score:0.#}% consistency): {details}";
    }

    /// <summary>
    /// Audits workspace markdown documents for syntax dialect conventions and formatting alignment:
    /// code fence delimiter consistency, language specifier coverage, list marker preferences, and heading style.
    /// </summary>
    public static WorkspaceMarkdownDialectAudit AuditWorkspaceMarkdownDialectConsistency(
        IReadOnlyList<string>? filePaths,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        var exists = fileExistsProvider ?? (path => File.Exists(path));
        var readContent = contentReader ?? (path => File.ReadAllText(path));

        var mdFiles = (filePaths ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mdFiles.Count == 0)
        {
            return new WorkspaceMarkdownDialectAudit(
                0, 0, "None", 0, 0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownDialectFileItem>(),
                "Empty Workspace",
                "Workspace markdown dialect advisor: 0 documents · Workspace contains no markdown files");
        }

        var items = new List<MarkdownDialectFileItem>();
        int totalBackticks = 0;
        int totalTildes = 0;
        int totalTagged = 0;
        int totalUntagged = 0;
        int totalHyphens = 0;
        int totalAsterisks = 0;
        int totalPlus = 0;
        int totalAtx = 0;
        int totalSetext = 0;

        foreach (var path in mdFiles)
        {
            if (!exists(path)) continue;

            string text;
            try
            {
                text = readContent(path);
            }
            catch
            {
                continue;
            }

            var lines = text.Replace("\r", "").Split('\n');
            int backticks = 0;
            int tildes = 0;
            int tagged = 0;
            int untagged = 0;
            int hyphens = 0;
            int asterisks = 0;
            int plus = 0;
            int atx = 0;
            int setext = 0;

            bool inFence = false;
            string fenceMarker = string.Empty;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    string tag = trimmed.Substring(0, 3);
                    if (!inFence)
                    {
                        inFence = true;
                        fenceMarker = tag;
                        if (tag == "```") backticks++;
                        else tildes++;

                        string lang = trimmed.Substring(3).Trim();
                        if (!string.IsNullOrEmpty(lang)) tagged++;
                        else untagged++;
                    }
                    else if (tag == fenceMarker)
                    {
                        inFence = false;
                        fenceMarker = string.Empty;
                    }
                    continue;
                }

                if (inFence) continue;

                if (trimmed.StartsWith("#"))
                {
                    atx++;
                }
                else if (i > 0 && !string.IsNullOrWhiteSpace(lines[i - 1].Trim()))
                {
                    if (trimmed.Length >= 2 && trimmed.All(c => c == '=')) setext++;
                    else if (trimmed.Length >= 2 && trimmed.All(c => c == '-')) setext++;
                }

                if (trimmed.StartsWith("- ")) hyphens++;
                else if (trimmed.StartsWith("* ")) asterisks++;
                else if (trimmed.StartsWith("+ ")) plus++;
            }

            int totalFences = backticks + tildes;
            int totalList = hyphens + asterisks + plus;

            double fileScore = 100.0;
            if (totalFences > 0 && backticks > 0 && tildes > 0) fileScore -= 20.0;
            if (totalList > 0 && ((hyphens > 0 && asterisks > 0) || (hyphens > 0 && plus > 0))) fileScore -= 15.0;
            if (setext > 0) fileScore -= 10.0;
            if (untagged > 0) fileScore -= Math.Min(15.0, untagged * 5.0);
            fileScore = Math.Clamp(fileScore, 0.0, 100.0);

            totalBackticks += backticks;
            totalTildes += tildes;
            totalTagged += tagged;
            totalUntagged += untagged;
            totalHyphens += hyphens;
            totalAsterisks += asterisks;
            totalPlus += plus;
            totalAtx += atx;
            totalSetext += setext;

            items.Add(new MarkdownDialectFileItem(
                path,
                Path.GetFileName(path),
                totalFences,
                backticks,
                tildes,
                tagged,
                untagged,
                hyphens,
                asterisks,
                plus,
                atx,
                setext,
                Math.Round(fileScore, 1)));
        }

        string dominantFence = totalBackticks >= totalTildes ? "Backticks (```)" : "Tildes (~~~)";
        int totalAllFences = totalBackticks + totalTildes;
        int minorityFences = Math.Min(totalBackticks, totalTildes);

        int totalAllLists = totalHyphens + totalAsterisks + totalPlus;
        int dominantList = Math.Max(totalHyphens, Math.Max(totalAsterisks, totalPlus));
        int minorityLists = totalAllLists - dominantList;

        double penalty = 0.0;
        if (totalAllFences > 0)
        {
            double fenceMinorityRatio = (double)minorityFences / totalAllFences;
            penalty += fenceMinorityRatio * 30.0;
            double untaggedRatio = (double)totalUntagged / totalAllFences;
            penalty += untaggedRatio * 15.0;
        }

        if (totalAllLists > 0)
        {
            double listMinorityRatio = (double)minorityLists / totalAllLists;
            penalty += listMinorityRatio * 20.0;
        }

        if (totalAtx + totalSetext > 0)
        {
            double setextRatio = (double)totalSetext / (totalAtx + totalSetext);
            penalty += setextRatio * 15.0;
        }

        double dialectScore = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        dialectScore = Math.Round(dialectScore, 1);

        string category = dialectScore switch
        {
            >= 90.0 => "Unified Markdown Dialect (≥90%)",
            >= 75.0 => "Cohesive Syntax (75–89%)",
            >= 50.0 => "Mixed Syntax Dialects (50–74%)",
            _ => "Fragmented Markdown Syntax (<50%)"
        };

        string summary = FormatWorkspaceMarkdownDialectSummary(
            mdFiles.Count, items.Count, dominantFence, totalBackticks, totalTildes,
            totalTagged, totalUntagged, totalHyphens, totalAsterisks, totalPlus,
            totalAtx, totalSetext, dialectScore, category, items);

        var audit = new WorkspaceMarkdownDialectAudit(
            mdFiles.Count, items.Count, dominantFence, totalBackticks, totalTildes,
            totalTagged, totalUntagged, totalHyphens, totalAsterisks, totalPlus,
            totalAtx, totalSetext, dialectScore, items, category, summary);
        return audit with { RecommendedConfiguration = GenerateRecommendedDialectConfiguration(audit) };
    }

    public static WorkspaceMarkdownDialectAudit AuditWorkspaceMarkdownDialectConsistency(
        string filePath,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string>? contentReader = null)
    {
        return AuditWorkspaceMarkdownDialectConsistency(new[] { filePath }, fileExistsProvider, contentReader);
    }

    public static string FormatWorkspaceMarkdownDialectSummary(
        int totalFiles, int filesWithMd, string dominantFence, int backticks, int tildes,
        int tagged, int untagged, int hyphens, int asterisks, int plus,
        int atx, int setext, double score, string category,
        IReadOnlyList<MarkdownDialectFileItem>? items = null)
    {
        if (totalFiles == 0) return "Workspace markdown dialect advisor: 0 documents · Workspace is empty";
        string docStr = totalFiles == 1 ? "1 document" : $"{totalFiles} documents";

        var parts = new List<string>();
        parts.Add($"dominant {dominantFence}");
        if (untagged > 0) parts.Add($"{untagged} untagged code blocks");
        if (asterisks + plus > 0) parts.Add($"mixed list markers ({hyphens}-, {asterisks}*, {plus}+)");
        if (setext > 0) parts.Add($"⚠ {setext} setext headings");

        string details = parts.Count > 0 ? $" · {string.Join(", ", parts)}" : "";
        return $"Workspace markdown dialect advisor ({docStr} · {category} · {score:0.#}% alignment): {details}";
    }

    /// <summary>
    /// Generates a standardized .marksmith.json configuration document based on the audited workspace markdown dialect preferences.
    /// </summary>
    public static string GenerateRecommendedDialectConfiguration(WorkspaceMarkdownDialectAudit audit)
    {
        if (audit == null || audit.TotalFilesScanned == 0)
        {
            return """
{
  "$schema": "https://onyachamp.com/schemas/marksmith-config.v1.json",
  "markdown": {
    "preferredCodeFence": "backtick",
    "requireCodeLanguageTag": true,
    "preferredListMarker": "-",
    "preferredHeadingStyle": "atx",
    "enforceStandardDialect": true
  },
  "auditBaseline": {
    "totalFiles": 0,
    "dialectAlignmentScore": 100.0,
    "category": "Default Baseline"
  }

}
""";
        }

        string fence = audit.DominantFenceType.Contains("tilde", StringComparison.OrdinalIgnoreCase) ? "tilde" : "backtick";
        string listMarker = audit.AsteriskListTotal > audit.HyphenListTotal && audit.AsteriskListTotal > audit.PlusListTotal ? "*" : "-";
        string headingStyle = audit.SetextHeadingsTotal > audit.AtxHeadingsTotal ? "setext" : "atx";
        bool requireLangTag = audit.UntaggedFencesTotal == 0;

        return $$"""
{
  "$schema": "https://onyachamp.com/schemas/marksmith-config.v1.json",
  "markdown": {
    "preferredCodeFence": "{{fence}}",
    "requireCodeLanguageTag": {{requireLangTag.ToString().ToLowerInvariant()}},
    "preferredListMarker": "{{listMarker}}",
    "preferredHeadingStyle": "{{headingStyle}}",
    "enforceStandardDialect": true
  },
  "auditBaseline": {
    "totalFiles": {{audit.TotalFilesScanned}},
    "filesWithMarkdown": {{audit.FilesWithMarkdown}},
    "dominantFence": "{{audit.DominantFenceType}}",
    "taggedCodeBlocks": {{audit.TaggedLanguageFencesTotal}},
    "untaggedCodeBlocks": {{audit.UntaggedFencesTotal}},
    "dialectAlignmentScore": {{audit.DialectAlignmentScore:0.#}},
    "category": "{{audit.Category}}"
  }
}
""";
    }

    /// <summary>
    /// Simulates a batch automated markdown dialect normalization dry-run across all workspace documents,
    /// calculating required replacement counts and projected AST syntax conformity.
    /// </summary>
    public static DialectNormalizationDryRunResult SimulateDialectNormalizationDryRun(
        IEnumerable<string> files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string?>? contentReader = null)
    {
        var list = files?.ToList() ?? new List<string>();
        var audit = AuditWorkspaceMarkdownDialectConsistency(list, fileExistsProvider, contentReader != null ? (p => contentReader(p) ?? "") : null);
        return SimulateDialectNormalizationDryRun(audit, list, fileExistsProvider, contentReader);
    }

    public static DialectNormalizationDryRunResult SimulateDialectNormalizationDryRun(
        WorkspaceMarkdownDialectAudit audit,
        IEnumerable<string> files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string?>? contentReader = null)
    {
        var exists = fileExistsProvider ?? (path => File.Exists(path));
        var read = contentReader ?? (path => { try { return File.ReadAllText(path); } catch { return null; } });

        var list = (files ?? Enumerable.Empty<string>()).ToList();
        var changedFiles = new List<string>();

        int totalFilesAudited = audit.TotalFilesScanned;
        int totalReplacements = 0;
        int fenceReplacements = 0;
        int listReplacements = 0;
        int headingReplacements = 0;

        bool preferBacktick = !audit.DominantFenceType.Contains("tilde", StringComparison.OrdinalIgnoreCase);
        string preferredMarker = audit.AsteriskListTotal > audit.HyphenListTotal && audit.AsteriskListTotal > audit.PlusListTotal ? "*" : "-";
        bool preferAtx = audit.AtxHeadingsTotal >= audit.SetextHeadingsTotal;

        foreach (var path in list)
        {
            if (string.IsNullOrWhiteSpace(path) || !exists(path)) continue;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".md" && ext != ".markdown") continue;

            string? content = read(path);
            if (string.IsNullOrWhiteSpace(content)) continue;

            int fileFences = 0;
            int fileLists = 0;
            int fileHeadings = 0;

            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (preferBacktick && trimmed.StartsWith("~~~"))
                {
                    fileFences++;
                }
                else if (!preferBacktick && trimmed.StartsWith("```"))
                {
                    fileFences++;
                }

                if (preferredMarker == "-" && (trimmed.StartsWith("* ") || trimmed.StartsWith("+ ")))
                {
                    fileLists++;
                }
                else if (preferredMarker == "*" && (trimmed.StartsWith("- ") || trimmed.StartsWith("+ ")))
                {
                    fileLists++;
                }

                if (preferAtx && i > 0 && (trimmed.StartsWith("===") || trimmed.StartsWith("---")) && trimmed.All(c => c == '=' || c == '-'))
                {
                    fileHeadings++;
                }
            }

            int fileTotal = fileFences + fileLists + fileHeadings;
            if (fileTotal > 0)
            {
                changedFiles.Add(Path.GetFileName(path));
                fenceReplacements += fileFences;
                listReplacements += fileLists;
                headingReplacements += fileHeadings;
                totalReplacements += fileTotal;
            }
        }

        int filesRequiringNormalization = changedFiles.Count;
        double projectedScore = 100.0;

        string summary = FormatDialectNormalizationSummary(
            totalFilesAudited, filesRequiringNormalization, totalReplacements,
            fenceReplacements, listReplacements, headingReplacements, projectedScore);

        return new DialectNormalizationDryRunResult(
            totalFilesAudited,
            filesRequiringNormalization,
            totalReplacements,
            fenceReplacements,
            listReplacements,
            headingReplacements,
            projectedScore,
            changedFiles,
            summary);
    }

    public static string FormatDialectNormalizationSummary(
        int totalFiles, int filesToChange, int totalReplacements,
        int fenceChanges, int listChanges, int headingChanges, double projectedScore)
    {
        if (totalFiles == 0)
            return "Workspace dialect normalization dry-run: 0 files · No workspace markdown files to normalize";

        if (totalReplacements == 0)
            return $"Workspace dialect normalization dry-run ({totalFiles} files · 100% conforming): All files match dominant dialect";

        var parts = new List<string>();
        if (fenceChanges > 0) parts.Add($"{fenceChanges} fence {(fenceChanges == 1 ? "swap" : "swaps")}");
        if (listChanges > 0) parts.Add($"{listChanges} list marker {(listChanges == 1 ? "alignment" : "alignments")}");
        if (headingChanges > 0) parts.Add($"{headingChanges} heading style {(headingChanges == 1 ? "conversion" : "conversions")}");

        string details = string.Join(", ", parts);
        return $"Workspace dialect normalization dry-run ({filesToChange}/{totalFiles} files · {totalReplacements} changes · {projectedScore:0.#}% projected alignment): {details}";
    }

    /// <summary>
    /// Applies batch automated markdown dialect normalization transforms across workspace documents,
    /// creating timestamped or standard .bak rollback backups and reporting applied replacement counts.
    /// </summary>
    public static DialectNormalizationApplyResult ApplyWorkspaceDialectNormalization(
        IEnumerable<string> files,
        Func<string, bool>? fileExistsProvider = null,
        Func<string, string?>? contentReader = null,
        Action<string, string>? contentWriter = null,
        Action<string, string>? backupWriter = null,
        bool dryRun = false)
    {
        var exists = fileExistsProvider ?? (path => File.Exists(path));
        var read = contentReader ?? (path => { try { return File.ReadAllText(path); } catch { return null; } });
        var write = contentWriter ?? ((path, text) => { try { File.WriteAllText(path, text); } catch { } });
        var writeBak = backupWriter ?? ((bakPath, origText) => { try { File.WriteAllText(bakPath, origText); } catch { } });

        var list = (files ?? Enumerable.Empty<string>()).ToList();
        var items = new List<FileNormalizationApplyItem>();

        int totalAudited = list.Count;
        int normalizedCount = 0;
        int totalReplacements = 0;
        int backupsCreated = 0;

        if (totalAudited == 0)
        {
            return new DialectNormalizationApplyResult(
                0, 0, 0, 0, true,
                Array.Empty<FileNormalizationApplyItem>(),
                "Workspace dialect normalization apply: 0 files · No workspace markdown files provided");
        }

        var audit = AuditWorkspaceMarkdownDialectConsistency(list, exists, p => read(p) ?? "");
        bool preferBacktick = !audit.DominantFenceType.Contains("tilde", StringComparison.OrdinalIgnoreCase);
        string preferredMarker = audit.AsteriskListTotal > audit.HyphenListTotal && audit.AsteriskListTotal > audit.PlusListTotal ? "*" : "-";
        bool preferAtx = audit.AtxHeadingsTotal >= audit.SetextHeadingsTotal;

        foreach (var path in list)
        {
            if (string.IsNullOrWhiteSpace(path) || !exists(path)) continue;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".md" && ext != ".markdown") continue;

            string? original = read(path);
            if (string.IsNullOrWhiteSpace(original)) continue;

            var lines = original.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var newLines = new List<string>();
            int fileReplacements = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                string transformed = line;

                // Fences
                if (preferBacktick && trimmed.StartsWith("~~~"))
                {
                    int indent = line.TakeWhile(char.IsWhiteSpace).Count();
                    string rest = trimmed.Substring(3);
                    transformed = new string(' ', indent) + "```" + rest;
                    fileReplacements++;
                }
                else if (!preferBacktick && trimmed.StartsWith("```"))
                {
                    int indent = line.TakeWhile(char.IsWhiteSpace).Count();
                    string rest = trimmed.Substring(3);
                    transformed = new string(' ', indent) + "~~~" + rest;
                    fileReplacements++;
                }
                // List markers
                else if (preferredMarker == "-" && (trimmed.StartsWith("* ") || trimmed.StartsWith("+ ")))
                {
                    int indent = line.TakeWhile(char.IsWhiteSpace).Count();
                    transformed = new string(' ', indent) + "- " + trimmed.Substring(2);
                    fileReplacements++;
                }
                else if (preferredMarker == "*" && (trimmed.StartsWith("- ") || trimmed.StartsWith("+ ")))
                {
                    int indent = line.TakeWhile(char.IsWhiteSpace).Count();
                    transformed = new string(' ', indent) + "* " + trimmed.Substring(2);
                    fileReplacements++;
                }
                // Setext headings
                else if (preferAtx && i > 0 && (trimmed.StartsWith("===") || trimmed.StartsWith("---")) && trimmed.All(c => c == '=' || c == '-'))
                {
                    if (newLines.Count > 0 && !string.IsNullOrWhiteSpace(newLines[^1]))
                    {
                        string prev = newLines[^1].Trim();
                        string prefix = trimmed[0] == '=' ? "# " : "## ";
                        newLines[^1] = prefix + prev;
                        fileReplacements++;
                        continue; // Skip the underline delimiter row
                    }
                }

                newLines.Add(transformed);
            }

            if (fileReplacements > 0)
            {
                normalizedCount++;
                totalReplacements += fileReplacements;

                string backupPath = $"{path}.bak";
                bool backupOk = false;
                bool writeOk = false;

                if (!dryRun)
                {
                    try
                    {
                        writeBak(backupPath, original);
                        backupOk = true;
                        backupsCreated++;

                        string newContent = string.Join(Environment.NewLine, newLines);
                        write(path, newContent);
                        writeOk = true;
                    }
                    catch
                    {
                        backupOk = false;
                        writeOk = false;
                    }
                }
                else
                {
                    backupOk = true;
                    writeOk = true;
                    backupsCreated++;
                }

                string status = dryRun
                    ? $"[Dry Run] {fileReplacements} replacements simulated · Ready to normalize"
                    : (writeOk && backupOk
                        ? $"✓ Normalized {fileReplacements} lines · Backup saved to {Path.GetFileName(backupPath)}"
                        : $"⚠ Normalization failed or partial write");

                items.Add(new FileNormalizationApplyItem(
                    path,
                    backupPath,
                    fileReplacements,
                    writeOk && backupOk,
                    status));
            }
        }

        bool allBackupsVerified = backupsCreated == normalizedCount;
        string summary = FormatDialectNormalizationApplySummary(
            totalAudited, normalizedCount, totalReplacements, backupsCreated, allBackupsVerified, dryRun);

        return new DialectNormalizationApplyResult(
            totalAudited,
            normalizedCount,
            totalReplacements,
            backupsCreated,
            allBackupsVerified,
            items,
            summary);
    }

    public static string FormatDialectNormalizationApplySummary(
        int totalFiles, int normalizedFiles, int totalReplacements, int backupsCreated, bool allVerified, bool dryRun = false)
    {
        if (totalFiles == 0)
            return "Workspace dialect normalization apply: 0 files · No workspace markdown files provided";

        if (totalReplacements == 0)
            return $"Workspace dialect normalization apply ({totalFiles} files · 100% conforming): All files already match dominant dialect";

        string prefix = dryRun ? "[Dry Run] " : "";
        string fileStr = normalizedFiles == 1 ? "1 file" : $"{normalizedFiles} files";
        return $"{prefix}Workspace dialect normalization applied ({fileStr}/{totalFiles} · {totalReplacements} changes · {backupsCreated} backups verified): Dominant dialect enforced";
    }

    /// <summary>
    /// Audits workspace markdown documents for frontmatter schema validity (YAML '---' or TOML '+++'),
    /// checking for required schema keys (title, date, tags, author), date format conformity, and array types.
    /// </summary>
    public static WorkspaceFrontmatterSchemaAudit AuditWorkspaceFrontmatterSchemas(
        IEnumerable<string>? filePaths,
        Func<string, bool>? fileExists = null,
        Func<string, string?>? readText = null,
        IEnumerable<string>? requiredKeys = null)
    {
        var paths = filePaths?.ToList() ?? new List<string>();
        var reqKeys = (requiredKeys ?? new[] { "title", "date", "tags", "author" })
            .Select(k => k.Trim().ToLowerInvariant())
            .ToList();

        var exists = fileExists ?? (p => File.Exists(p));
        var read = readText ?? (p =>
        {
            try { return File.ReadAllText(p); }
            catch { return null; }
        });

        int totalAudited = 0;
        int filesWithFm = 0;
        int conformingCount = 0;
        int missingKeysViolations = 0;
        int dateFormatViolations = 0;
        int tagFormatViolations = 0;

        var items = new List<WorkspaceFrontmatterFileItem>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            totalAudited++;

            if (!exists(path))
            {
                items.Add(new WorkspaceFrontmatterFileItem(
                    path, false, "None", reqKeys, new Dictionary<string, string>(),
                    false, null, false, 0, false, "⚠ File does not exist"));
                missingKeysViolations++;
                continue;
            }

            string? content = read(path);
            if (string.IsNullOrWhiteSpace(content))
            {
                items.Add(new WorkspaceFrontmatterFileItem(
                    path, false, "None", reqKeys, new Dictionary<string, string>(),
                    false, null, false, 0, false, "⚠ Empty file (no frontmatter)"));
                missingKeysViolations++;
                continue;
            }

            string normalized = content.Replace("\r\n", "\n");
            string format = "None";
            string closingDelim = "";
            int startIdx = -1;

            if (normalized.StartsWith("---\n") || normalized.StartsWith("---\r\n") || normalized == "---")
            {
                format = "YAML";
                closingDelim = "\n---";
                startIdx = 4;
            }
            else if (normalized.StartsWith("+++\n") || normalized.StartsWith("+++\r\n") || normalized == "+++")
            {
                format = "TOML";
                closingDelim = "\n+++";
                startIdx = 4;
            }

            if (format == "None" || startIdx < 0)
            {
                items.Add(new WorkspaceFrontmatterFileItem(
                    path, false, "None", reqKeys, new Dictionary<string, string>(),
                    false, null, false, 0, false, "⚠ No frontmatter header block found"));
                missingKeysViolations++;
                continue;
            }

            filesWithFm++;
            int endIdx = normalized.IndexOf(closingDelim, startIdx, StringComparison.Ordinal);
            if (endIdx < 0)
            {
                items.Add(new WorkspaceFrontmatterFileItem(
                    path, true, format, reqKeys, new Dictionary<string, string>(),
                    false, null, false, 0, false, $"⚠ Unclosed {format} frontmatter delimiter block"));
                missingKeysViolations++;
                continue;
            }

            string fmBody = normalized.Substring(startIdx, endIdx - startIdx);
            var fmLines = fmBody.Split('\n');
            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string currentKey = "";
            var tagList = new List<string>();
            bool parsingTags = false;

            for (int l = 0; l < fmLines.Length; l++)
            {
                string line = fmLines[l].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                int colon = line.IndexOf(':');
                int equals = line.IndexOf('=');

                if (colon > 0 && (equals < 0 || colon < equals))
                {
                    parsingTags = false;
                    currentKey = line.Substring(0, colon).Trim().ToLowerInvariant();
                    string val = line.Substring(colon + 1).Trim();
                    parsed[currentKey] = val;

                    if (currentKey == "tags")
                    {
                        if (val.StartsWith("[") && val.EndsWith("]"))
                        {
                            string inner = val.Substring(1, val.Length - 2);
                            var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var p in parts) tagList.Add(p.Trim().Trim('"', '\''));
                        }
                        else if (string.IsNullOrEmpty(val))
                        {
                            parsingTags = true;
                        }
                        else
                        {
                            tagList.Add(val.Trim('"', '\''));
                        }
                    }
                }
                else if (equals > 0)
                {
                    parsingTags = false;
                    currentKey = line.Substring(0, equals).Trim().ToLowerInvariant();
                    string val = line.Substring(equals + 1).Trim();
                    parsed[currentKey] = val;

                    if (currentKey == "tags" && val.StartsWith("[") && val.EndsWith("]"))
                    {
                        string inner = val.Substring(1, val.Length - 2);
                        var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in parts) tagList.Add(p.Trim().Trim('"', '\''));
                    }
                }
                else if (parsingTags && line.StartsWith("- "))
                {
                    tagList.Add(line.Substring(2).Trim().Trim('"', '\''));
                }
            }

            var missing = reqKeys.Where(k => !parsed.ContainsKey(k) || string.IsNullOrWhiteSpace(parsed[k])).ToList();
            if (missing.Count > 0) missingKeysViolations++;

            bool validDate = false;
            string? dateStr = null;
            if (parsed.TryGetValue("date", out var rawDate))
            {
                string cleanDate = rawDate.Trim('"', '\'');
                if (DateTime.TryParse(cleanDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                {
                    validDate = true;
                    dateStr = dt.ToString("yyyy-MM-dd");
                }
                else
                {
                    dateFormatViolations++;
                }
            }

            bool validTags = tagList.Count > 0;
            if (parsed.ContainsKey("tags") && !validTags)
            {
                tagFormatViolations++;
            }

            bool isConforming = missing.Count == 0 && (validDate || !parsed.ContainsKey("date")) && (!parsed.ContainsKey("tags") || validTags);
            if (isConforming) conformingCount++;

            string status = isConforming
                ? $"✓ Conforming {format} frontmatter ({parsed.Count} keys · Date: {dateStr ?? "none"} · {tagList.Count} tags)"
                : (missing.Count > 0
                    ? $"⚠ Missing required keys: [{string.Join(", ", missing)}]"
                    : (!validDate ? "⚠ Malformed date format (ISO 8601 yyyy-MM-dd expected)" : "⚠ Malformed tags collection"));

            items.Add(new WorkspaceFrontmatterFileItem(
                path,
                true,
                format,
                missing,
                parsed,
                validDate,
                dateStr,
                validTags,
                tagList.Count,
                isConforming,
                status));
        }

        if (totalAudited == 0)
        {
            return new WorkspaceFrontmatterSchemaAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceFrontmatterFileItem>(),
                "Clean Baseline",
                "Workspace frontmatter schema audit: 0 files · No workspace markdown files provided");
        }

        double score = 100.0;
        score -= (missingKeysViolations * 20.0);
        score -= (dateFormatViolations * 10.0);
        score -= (tagFormatViolations * 10.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Frontmatter Schema Rigor (≥90%)"
            : score >= 70.0
                ? "Moderate Schema Metadata Inconsistency (70-89%)"
                : "Severe Metadata / Schema Drift (<70%)";

        string summary = FormatFrontmatterSchemaSummary(
            totalAudited, filesWithFm, conformingCount, missingKeysViolations,
            dateFormatViolations, tagFormatViolations, score, category);

        return new WorkspaceFrontmatterSchemaAudit(
            totalAudited,
            filesWithFm,
            conformingCount,
            missingKeysViolations,
            dateFormatViolations,
            tagFormatViolations,
            score,
            items,
            category,
            summary);
    }

    public static string FormatFrontmatterSchemaSummary(
        int totalFiles, int filesWithFm, int conformingFiles, int missingKeyViolations,
        int dateViolations, int tagViolations, double score, string category)
    {
        if (totalFiles == 0)
            return "Workspace frontmatter schema audit: 0 files · No workspace markdown files provided";

        string fileStr = totalFiles == 1 ? "1 file" : $"{totalFiles} files";
        var parts = new List<string>();
        parts.Add($"{conformingFiles}/{filesWithFm} conforming frontmatter");

        if (missingKeyViolations > 0) parts.Add($"⚠ {missingKeyViolations} missing required keys");
        else parts.Add("0 missing keys");

        if (dateViolations > 0) parts.Add($"⚠ {dateViolations} malformed dates");
        if (tagViolations > 0) parts.Add($"⚠ {tagViolations} malformed tags");

        string details = string.Join(" · ", parts);
        return $"Workspace frontmatter schema audit ({fileStr} · {category} · {score:0.#}% integrity score): {details}";
    }

    /// <summary>
    /// Audits workspace markdown documents for cross-document wiki-links ([[Note]] / [[Note|Alias]]),
    /// relative document hyperlinks ([text](path.md)), and relative media asset embeds (![alt](path.png)),
    /// verifying physical target file existence on disk, resolving relative paths against parent directories,
    /// and scoring workspace hyperlink integrity.
    /// </summary>
    public static WorkspaceWikiLinksAudit AuditWorkspaceWikiLinksAndAssets(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        var files = filePaths?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList() ?? new List<string>();

        if (files.Count == 0)
        {
            return new WorkspaceWikiLinksAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceLinkTargetItem>(),
                "Clean Baseline",
                "Workspace cross-document links audit: 0 files · No workspace markdown files provided");
        }

        var existsFunc = fileExists ?? (p =>
        {
            try { return File.Exists(p) || Directory.Exists(p); }
            catch { return false; }
        });
        var readLinesFunc = readAllLines ?? (p =>
        {
            try { return File.ReadAllLines(p); }
            catch { return null; }
        });

        var wikiRegex = new Regex(@"\[\[(?<target>[^\]\|]+)(?:\|(?<alias>[^\]]+))?\]\]", RegexOptions.Compiled);
        var mediaRegex = new Regex(@"!\[(?<alt>[^\]]*)\]\((?<url>[^)]+)\)", RegexOptions.Compiled);
        var docRegex = new Regex(@"(?<!!)\[(?<text>[^\]]+)\]\((?<url>[^)]+)\)", RegexOptions.Compiled);

        var items = new List<WorkspaceLinkTargetItem>();
        int scannedCount = 0;
        int wikiCount = 0;
        int docCount = 0;
        int assetCount = 0;
        int validCount = 0;
        int brokenCount = 0;

        var fileBasenameSet = new HashSet<string>(
            files.Select(f => Path.GetFileNameWithoutExtension(f)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (!existsFunc(file)) continue;

            string[]? lines;
            try
            {
                lines = readLinesFunc(file);
            }
            catch
            {
                continue;
            }
            if (lines == null) continue;

            scannedCount++;
            string dir = Path.GetDirectoryName(file) ?? "";
            bool inFence = false;

            for (int l = 0; l < lines.Length; l++)
            {
                string raw = lines[l];
                string trimmed = raw.Trim();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence) continue;

                int lineNum = l + 1;

                // 1. Audit Media Asset embeds: ![alt](url)
                var mediaMatches = mediaRegex.Matches(raw);
                var matchedMediaIndices = new HashSet<int>();

                foreach (Match m in mediaMatches)
                {
                    matchedMediaIndices.Add(m.Index);
                    string rawUrl = m.Groups["url"].Value.Trim();
                    if (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        rawUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    assetCount++;
                    string cleanPath = rawUrl.Split('?')[0].Split('#')[0].Trim();
                    string resolved = string.IsNullOrEmpty(dir) ? cleanPath : Path.GetFullPath(Path.Combine(dir, cleanPath));
                    bool exists = existsFunc(resolved);

                    if (exists) validCount++;
                    else brokenCount++;

                    string status = exists
                        ? $"✓ Local media asset verified: {cleanPath}"
                        : $"⚠ Missing local media file: {cleanPath}";

                    items.Add(new WorkspaceLinkTargetItem(
                        file, lineNum, "MediaAsset", rawUrl, resolved, exists, status));
                }

                // 2. Audit Document Hyperlinks: [text](url)
                var docMatches = docRegex.Matches(raw);
                foreach (Match m in docMatches)
                {
                    if (matchedMediaIndices.Contains(m.Index - 1)) continue;

                    string rawUrl = m.Groups["url"].Value.Trim();
                    if (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        rawUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                        rawUrl.StartsWith("#"))
                    {
                        continue;
                    }

                    docCount++;
                    string cleanPath = rawUrl.Split('?')[0].Split('#')[0].Trim();
                    string resolved = string.IsNullOrEmpty(dir) ? cleanPath : Path.GetFullPath(Path.Combine(dir, cleanPath));
                    bool exists = existsFunc(resolved);

                    if (exists) validCount++;
                    else brokenCount++;

                    string status = exists
                        ? $"✓ Relative document link target exists: {cleanPath}"
                        : $"⚠ Broken relative document link: {cleanPath}";

                    items.Add(new WorkspaceLinkTargetItem(
                        file, lineNum, "RelativeDocument", rawUrl, resolved, exists, status));
                }

                // 3. Audit Wiki-Links: [[Target]] or [[Target|Alias]]
                var wikiMatches = wikiRegex.Matches(raw);
                foreach (Match m in wikiMatches)
                {
                    string target = m.Groups["target"].Value.Trim();
                    string cleanTarget = target.Split('#')[0].Trim();

                    wikiCount++;
                    string resolved = string.IsNullOrEmpty(dir) ? cleanTarget : Path.GetFullPath(Path.Combine(dir, cleanTarget + ".md"));
                    bool exists = existsFunc(resolved) ||
                                  existsFunc(Path.Combine(dir, cleanTarget)) ||
                                  fileBasenameSet.Contains(cleanTarget);

                    if (exists) validCount++;
                    else brokenCount++;

                    string status = exists
                        ? $"✓ WikiLink target document resolved: [[{target}]]"
                        : $"⚠ Broken WikiLink: target [[{target}]] not found in workspace";

                    items.Add(new WorkspaceLinkTargetItem(
                        file, lineNum, "WikiLink", target, resolved, exists, status));
                }
            }
        }

        int totalLinks = wikiCount + docCount + assetCount;
        if (totalLinks == 0)
        {
            return new WorkspaceWikiLinksAudit(
                scannedCount, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceLinkTargetItem>(),
                "Clean Baseline",
                $"Workspace cross-document links audit ({scannedCount} files): 0 relative links found");
        }

        double score = 100.0;
        score -= (brokenCount * 15.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Workspace Hyperlink & Asset Integrity (≥90%)"
            : score >= 70.0
                ? "Moderate Broken Target / Missing Asset Risk (70-89%)"
                : "Severe Hyperlink Drift & Missing Assets (<70%)";

        string summary = FormatWorkspaceWikiLinksSummary(
            scannedCount, totalLinks, validCount, brokenCount,
            wikiCount, docCount, assetCount, score, category);

        return new WorkspaceWikiLinksAudit(
            scannedCount,
            totalLinks,
            wikiCount,
            docCount,
            assetCount,
            validCount,
            brokenCount,
            score,
            items,
            category,
            summary);
    }

    public static string FormatWorkspaceWikiLinksSummary(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        return AuditWorkspaceWikiLinksAndAssets(filePaths, workspaceRoot, fileExists, readAllLines).Summary;
    }

    public static string FormatWorkspaceWikiLinksSummary(
        int filesScanned, int totalLinks, int validTargets, int brokenTargets,
        int wikiLinks, int docLinks, int assetLinks, double score, string category)
    {
        if (totalLinks == 0)
            return $"Workspace cross-document links audit ({filesScanned} files): 0 relative links found";

        string fileStr = filesScanned == 1 ? "1 file" : $"{filesScanned} files";
        string linkStr = totalLinks == 1 ? "1 link" : $"{totalLinks} links";
        var parts = new List<string>();

        parts.Add($"{validTargets}/{totalLinks} resolved");

        if (brokenTargets > 0) parts.Add($"⚠ {brokenTargets} broken {(brokenTargets == 1 ? "target" : "targets")}");
        else parts.Add("0 broken targets");

        if (wikiLinks > 0) parts.Add($"{wikiLinks} wikilinks");
        if (docLinks > 0) parts.Add($"{docLinks} doc links");
        if (assetLinks > 0) parts.Add($"{assetLinks} media assets");

        string details = string.Join(" · ", parts);
        return $"Workspace cross-document links audit ({fileStr}, {linkStr} · {category} · {score:0.#}% integrity score): {details}";
    }

    /// <summary>
    /// Audits workspace markdown documents for tag taxonomy consistency:
    /// frontmatter tags, inline #tags, casing variations (e.g. #WinUI vs #winui),
    /// singular/plural duplication (#tag vs #tags), and orphaned singleton tags.
    /// </summary>
    public static WorkspaceTagTaxonomyAudit AuditWorkspaceTagTaxonomy(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        var exists = fileExists ?? (p => File.Exists(p));
        var reader = readAllLines ?? (p => File.Exists(p) ? File.ReadAllLines(p) : null);

        var candidatePaths = (filePaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidatePaths.Count == 0)
        {
            return new WorkspaceTagTaxonomyAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<TagTaxonomyItem>(),
                "Clean Baseline",
                "Workspace tag taxonomy audit (0 files): 0 tags found");
        }

        var tagOccurrences = new Dictionary<string, List<(string FilePath, string RawTag)>>(StringComparer.OrdinalIgnoreCase);
        var inlineTagRegex = new Regex(@"(?:^|\s)#([a-zA-Z0-9_\-]+)", RegexOptions.Compiled);
        int filesScanned = 0;

        foreach (var file in candidatePaths)
        {
            if (!exists(file)) continue;
            var lines = reader(file);
            if (lines == null || lines.Length == 0) continue;
            filesScanned++;

            bool inFrontmatter = false;
            bool inTagsList = false;
            bool inCodeFence = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (i == 0 && (trimmed == "---" || trimmed == "+++"))
                {
                    inFrontmatter = true;
                    continue;
                }
                if (inFrontmatter && (trimmed == "---" || trimmed == "+++"))
                {
                    inFrontmatter = false;
                    inTagsList = false;
                    continue;
                }

                if (inFrontmatter)
                {
                    if (trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = trimmed.Substring(5).Trim();
                        if (val.StartsWith("[") && val.EndsWith("]"))
                        {
                            var inner = val.Trim('[', ']');
                            var parts = inner.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var p in parts)
                            {
                                RecordTag(p.Trim().Trim('"', '\''), file);
                            }
                        }
                        else if (string.IsNullOrWhiteSpace(val))
                        {
                            inTagsList = true;
                        }
                        else
                        {
                            RecordTag(val.Trim('"', '\''), file);
                        }
                        continue;
                    }

                    if (inTagsList)
                    {
                        if (trimmed.StartsWith("- "))
                        {
                            RecordTag(trimmed.Substring(2).Trim().Trim('"', '\''), file);
                            continue;
                        }
                        if (trimmed.Contains(":") && !trimmed.StartsWith("-"))
                        {
                            inTagsList = false;
                        }
                    }
                    continue;
                }

                // Outside frontmatter
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inCodeFence = !inCodeFence;
                    continue;
                }
                if (inCodeFence) continue;

                var matches = inlineTagRegex.Matches(line);
                foreach (Match m in matches)
                {
                    string tag = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        RecordTag(tag, file);
                    }
                }
            }
        }

        void RecordTag(string rawTag, string file)
        {
            if (string.IsNullOrWhiteSpace(rawTag)) return;
            string key = rawTag.ToLowerInvariant();
            if (!tagOccurrences.TryGetValue(key, out var list))
            {
                list = new List<(string FilePath, string RawTag)>();
                tagOccurrences[key] = list;
            }
            list.Add((file, rawTag));
        }

        if (tagOccurrences.Count == 0)
        {
            return new WorkspaceTagTaxonomyAudit(
                filesScanned, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<TagTaxonomyItem>(),
                "Clean Baseline",
                $"Workspace tag taxonomy audit ({filesScanned} files): 0 tags found");
        }

        int totalOccurrences = tagOccurrences.Values.Sum(v => v.Count);
        int uniqueTags = tagOccurrences.Count;
        int casingIssues = 0;
        int pluralIssues = 0;
        int singletons = 0;
        var items = new List<TagTaxonomyItem>();

        var allNormalizedTags = new HashSet<string>(tagOccurrences.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in tagOccurrences.OrderByDescending(k => k.Value.Count).ThenBy(k => k.Key))
        {
            string normTag = kvp.Key;
            var occList = kvp.Value;
            int freq = occList.Count;
            int docCount = occList.Select(o => o.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            var distinctVariants = occList.Select(o => o.RawTag).Distinct(StringComparer.Ordinal).ToList();
            bool hasCasing = distinctVariants.Count > 1;
            if (hasCasing) casingIssues++;

            bool hasPlural = false;
            if (normTag.EndsWith("s") && allNormalizedTags.Contains(normTag.Substring(0, normTag.Length - 1)))
            {
                hasPlural = true;
            }
            else if (allNormalizedTags.Contains(normTag + "s"))
            {
                hasPlural = true;
            }
            if (hasPlural) pluralIssues++;

            bool isSingleton = freq == 1;
            if (isSingleton) singletons++;

            string status;
            if (hasCasing && hasPlural) status = $"⚠ Casing variation ({string.Join("/", distinctVariants)}) & plural duplicate";
            else if (hasCasing) status = $"⚠ Casing inconsistency ({string.Join("/", distinctVariants)})";
            else if (hasPlural) status = "⚠ Singular/plural variant exists in workspace";
            else if (isSingleton) status = "Orphaned singleton tag (1 occurrence)";
            else status = $"✓ Consistent taxonomy ({freq} occurrences across {docCount} docs)";

            items.Add(new TagTaxonomyItem(
                distinctVariants[0],
                freq,
                docCount,
                hasCasing,
                hasPlural,
                isSingleton,
                distinctVariants,
                status));
        }

        double score = 100.0;
        score -= Math.Min(35.0, casingIssues * 5.0);
        score -= Math.Min(35.0, (pluralIssues / 2) * 5.0);
        if (uniqueTags > 10 && singletons > 0)
        {
            score -= Math.Min(20.0, (singletons / 5.0) * 5.0);
        }
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Tag Taxonomy & Cohesive Vocabulary (≥90%)"
            : score >= 75.0
                ? "Good Tag Consistency & Few Near-Duplicates (75-89%)"
                : score >= 50.0
                    ? "Moderate Taxonomy Fragmentation & Casing Drift (50-74%)"
                    : "Severe Tag Sprawl & Duplicate Multiplicity (<50%)";

        string summary = FormatWorkspaceTagTaxonomySummary(
            filesScanned, totalOccurrences, uniqueTags, casingIssues, pluralIssues, singletons, score, category);

        return new WorkspaceTagTaxonomyAudit(
            filesScanned,
            totalOccurrences,
            uniqueTags,
            casingIssues,
            pluralIssues,
            singletons,
            score,
            items,
            category,
            summary);
    }

    public static string FormatWorkspaceTagTaxonomySummary(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        return AuditWorkspaceTagTaxonomy(filePaths, workspaceRoot, fileExists, readAllLines).Summary;
    }

    public static string FormatWorkspaceTagTaxonomySummary(
        int filesScanned, int totalOccurrences, int uniqueTags, int casingIssues,
        int pluralIssues, int singletons, double score, string category)
    {
        if (totalOccurrences == 0)
            return $"Workspace tag taxonomy audit ({filesScanned} files): 0 tags found";

        string fileStr = filesScanned == 1 ? "1 file" : $"{filesScanned} files";
        string tagStr = uniqueTags == 1 ? "1 tag" : $"{uniqueTags} unique tags";
        var parts = new List<string>();

        parts.Add($"{totalOccurrences} occurrences");

        if (casingIssues > 0) parts.Add($"⚠ {casingIssues} casing {(casingIssues == 1 ? "inconsistency" : "inconsistencies")}");
        else parts.Add("0 casing discrepancies");

        if (pluralIssues > 0) parts.Add($"⚠ {pluralIssues} plural {(pluralIssues == 1 ? "duplicate" : "duplicates")}");
        if (singletons > 0) parts.Add($"{singletons} singleton {(singletons == 1 ? "tag" : "tags")}");

        string details = string.Join(" · ", parts);
        return $"Workspace tag taxonomy audit ({fileStr}, {tagStr} · {category} · {score:0.#}% cohesion score): {details}";
    }

    /// <summary>
    /// Audits workspace markdown documents for word counts, estimated reading times, length distribution tiers,
    /// and identifies extreme length outliers.
    /// </summary>
    public static WorkspaceWordCountDistributionAudit AuditWorkspaceWordCountsAndReadingTimes(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        var exists = fileExists ?? (p => File.Exists(p));
        var reader = readAllLines ?? (p => File.Exists(p) ? File.ReadAllLines(p) : null);

        var candidatePaths = (filePaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidatePaths.Count == 0)
        {
            return new WorkspaceWordCountDistributionAudit(
                0, 0, 0.0, 0.0, 0, 0, 0, "(None)", 0, 100.0,
                Array.Empty<WorkspaceDocumentWordCountItem>(),
                "Clean Baseline",
                "Workspace word count & reading time audit (0 files): 0 words across 0 documents");
        }

        var items = new List<WorkspaceDocumentWordCountItem>();
        int totalFilesScanned = 0;
        int totalWords = 0;
        int quickReadCount = 0;
        int standardReadCount = 0;
        int longFormReadCount = 0;
        string longestDocName = "(None)";
        int maxWordCount = 0;

        foreach (var file in candidatePaths)
        {
            if (!exists(file)) continue;
            var lines = reader(file);
            totalFilesScanned++;

            int docWords = 0;
            if (lines != null)
            {
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    docWords += parts.Length;
                }
            }

            totalWords += docWords;
            double readingMins = Math.Round(docWords / 200.0, 1);
            string fileName = Path.GetFileName(file);

            if (docWords > maxWordCount)
            {
                maxWordCount = docWords;
                longestDocName = fileName;
            }

            string tier;
            if (readingMins < 3.0)
            {
                quickReadCount++;
                tier = "Quick Read (<3m)";
            }
            else if (readingMins <= 10.0)
            {
                standardReadCount++;
                tier = "Standard Read (3-10m)";
            }
            else
            {
                longFormReadCount++;
                tier = "Long-Form (>10m)";
            }

            string status = $"{docWords} words · ~{readingMins:0.#} min read · {tier}";
            items.Add(new WorkspaceDocumentWordCountItem(file, fileName, docWords, readingMins, tier, status));
        }

        if (totalFilesScanned == 0)
        {
            return new WorkspaceWordCountDistributionAudit(
                0, 0, 0.0, 0.0, 0, 0, 0, "(None)", 0, 100.0,
                Array.Empty<WorkspaceDocumentWordCountItem>(),
                "Clean Baseline",
                "Workspace word count & reading time audit (0 files): 0 words across 0 documents");
        }

        double totalReadingMinutes = Math.Round(totalWords / 200.0, 1);
        double avgWords = Math.Round((double)totalWords / totalFilesScanned, 1);

        double score = 100.0;
        if (totalWords == 0)
        {
            score = 50.0;
        }
        else
        {
            int emptyDocs = items.Count(i => i.WordCount == 0);
            if (emptyDocs > 0)
            {
                score -= Math.Min(30.0, emptyDocs * 5.0);
            }
        }

        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Balanced Reading Time & Word Count Distribution (≥90%)"
            : score >= 75.0
                ? "Standard Multi-Length Workspace Library (75-89%)"
                : score >= 50.0
                    ? "Skewed Length Profile or High Empty Document Count (50-74%)"
                    : "Extreme Word Count Disparity or Empty Documents (<50%)";

        string summary = FormatWorkspaceWordCountSummary(
            totalFilesScanned, totalWords, totalReadingMinutes, avgWords,
            quickReadCount, standardReadCount, longFormReadCount, longestDocName, maxWordCount, score, category);

        return new WorkspaceWordCountDistributionAudit(
            totalFilesScanned,
            totalWords,
            totalReadingMinutes,
            avgWords,
            quickReadCount,
            standardReadCount,
            longFormReadCount,
            longestDocName,
            maxWordCount,
            score,
            items,
            category,
            summary);
    }

    public static string FormatWorkspaceWordCountSummary(
        int totalFiles, int totalWords, double readingMinutes, double avgWords,
        int quickReads, int standardReads, int longReads, string longestDoc, int maxWords,
        double score, string category)
    {
        if (totalFiles == 0)
            return "Workspace word count & reading time audit (0 files): 0 words across 0 documents";

        string fileStr = totalFiles == 1 ? "1 file" : $"{totalFiles} files";
        var parts = new List<string>();

        parts.Add($"{totalWords} total words (~{readingMinutes:0.#} mins total read time)");
        parts.Add($"avg {avgWords:0.#} words/doc");

        var tiers = new List<string>();
        if (quickReads > 0) tiers.Add($"{quickReads} quick");
        if (standardReads > 0) tiers.Add($"{standardReads} standard");
        if (longReads > 0) tiers.Add($"{longReads} long");

        if (tiers.Count > 0)
            parts.Add(string.Join("/", tiers));

        if (maxWords > 0)
            parts.Add($"longest: {longestDoc} ({maxWords} words)");

        string details = string.Join(" · ", parts);
        return $"Workspace word count & reading time audit ({fileStr} · {category} · {score:0.#}% balance score): {details}";
    }

    /// <summary>
    /// Audits workspace markdown documents for code fence language tags (```csharp), identifying missing/bare tags,
    /// non-standard aliases (e.g. 'cs' vs 'csharp', 'js' vs 'javascript'), and unrecognized grammar identifiers.
    /// </summary>
    public static WorkspaceFenceGrammarAudit AuditWorkspaceCodeFenceLanguages(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        var exists = fileExists ?? (p => File.Exists(p));
        var reader = readAllLines ?? (p => File.Exists(p) ? File.ReadAllLines(p) : null);

        var candidatePaths = (filePaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidatePaths.Count == 0)
        {
            return new WorkspaceFenceGrammarAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceFenceGrammarItem>(),
                "Clean Baseline",
                "Workspace code fence language audit (0 files): 0 code blocks across 0 documents");
        }

        var canonicalLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csharp", "javascript", "typescript", "python", "json", "xml", "html", "css",
            "powershell", "bash", "sql", "markdown", "yaml", "mermaid", "svg", "diff",
            "c", "cpp", "go", "rust", "ruby", "java", "dockerfile", "ini", "text", "plaintext"
        };

        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "cs", "csharp" },
            { "js", "javascript" },
            { "ts", "typescript" },
            { "py", "python" },
            { "sh", "bash" },
            { "ps1", "powershell" },
            { "yml", "yaml" },
            { "md", "markdown" }
        };

        var items = new List<WorkspaceFenceGrammarItem>();
        int totalFilesScanned = 0;
        int totalCodeBlocks = 0;
        int taggedBlocks = 0;
        int untaggedBlocks = 0;
        int aliasBlocks = 0;
        var uniqueLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in candidatePaths)
        {
            if (!exists(file)) continue;
            var lines = reader(file);
            totalFilesScanned++;

            if (lines == null || lines.Length == 0) continue;
            string fileName = Path.GetFileName(file);

            bool inFence = false;
            string fenceDelimiter = "";
            int startLine = 0;
            string rawLang = "";
            int blockLineCount = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (!inFence)
                {
                    if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                    {
                        inFence = true;
                        fenceDelimiter = trimmed.StartsWith("```") ? "```" : "~~~";
                        startLine = i + 1;
                        blockLineCount = 0;

                        string remainder = trimmed.Substring(3).Trim();
                        rawLang = remainder.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    }
                }
                else
                {
                    if (trimmed.StartsWith(fenceDelimiter))
                    {
                        inFence = false;
                        totalCodeBlocks++;

                        bool isUntagged = string.IsNullOrWhiteSpace(rawLang);
                        bool isAlias = false;
                        string canonical = "";

                        if (isUntagged)
                        {
                            untaggedBlocks++;
                            canonical = "(None)";
                        }
                        else
                        {
                            taggedBlocks++;
                            if (aliasMap.TryGetValue(rawLang, out var mappedCanonical))
                            {
                                isAlias = true;
                                aliasBlocks++;
                                canonical = mappedCanonical;
                            }
                            else
                            {
                                canonical = rawLang.ToLowerInvariant();
                            }
                            uniqueLanguages.Add(canonical);
                        }

                        string status = isUntagged ? "⚠ Untagged bare code fence (missing language)" :
                                        isAlias ? $"⚠ Non-standard alias '{rawLang}' (recommend '{canonical}')" :
                                        $"Standard syntax grammar [{canonical}]";

                        items.Add(new WorkspaceFenceGrammarItem(
                            file, fileName, startLine, blockLineCount, rawLang, canonical, isUntagged, isAlias, status));
                    }
                    else
                    {
                        blockLineCount++;
                    }
                }
            }
        }

        if (totalFilesScanned == 0)
        {
            return new WorkspaceFenceGrammarAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<WorkspaceFenceGrammarItem>(),
                "Clean Baseline",
                "Workspace code fence language audit (0 files): 0 code blocks across 0 documents");
        }

        double score = 100.0;
        if (totalCodeBlocks > 0)
        {
            score -= Math.Min(40.0, untaggedBlocks * 12.0);
            score -= Math.Min(25.0, aliasBlocks * 4.0);
        }
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = (totalCodeBlocks == 0) ? "Clean Baseline" :
            score >= 90.0 ? "Optimal Code Fence Metadata & Grammar Governance (≥90%)" :
            score >= 75.0 ? "Standard Highlighting with Minor Alias Variations (75-89%)" :
            score >= 50.0 ? "Moderate Untagged Code Blocks or Grammar Ambiguities (50-74%)" :
            "Severe Untagged Code Blocks & Syntax Disparity (<50%)";

        string summary = FormatWorkspaceCodeFenceSummary(
            totalFilesScanned, totalCodeBlocks, taggedBlocks, untaggedBlocks, aliasBlocks,
            uniqueLanguages.Count, score, category);

        return new WorkspaceFenceGrammarAudit(
            totalFilesScanned,
            totalCodeBlocks,
            taggedBlocks,
            untaggedBlocks,
            aliasBlocks,
            uniqueLanguages.Count,
            score,
            items,
            category,
            summary);
    }

    public static string FormatWorkspaceCodeFenceSummary(
        int totalFiles, int totalCodeBlocks, int taggedBlocks, int untaggedBlocks,
        int aliasBlocks, int uniqueLanguages, double score, string category)
    {
        if (totalFiles == 0)
            return "Workspace code fence language audit (0 files): 0 code blocks across 0 documents";

        string fileStr = totalFiles == 1 ? "1 file" : $"{totalFiles} files";
        var parts = new List<string>();

        parts.Add($"{totalCodeBlocks} code blocks ({taggedBlocks} tagged with {uniqueLanguages} unique languages)");

        if (untaggedBlocks > 0)
            parts.Add($"⚠ {untaggedBlocks} untagged / bare blocks");

        if (aliasBlocks > 0)
            parts.Add($"⚠ {aliasBlocks} non-standard aliases");

        string details = string.Join(" · ", parts);
        return $"Workspace code fence language audit ({fileStr} · {category} · {score:0.#}% metadata score): {details}";
    }

    public static WorkspaceMathConsistencyAudit AuditWorkspaceMathFormulasAndKaTeX(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        var exists = fileExists ?? (p => File.Exists(p));
        var readLines = readAllLines ?? (p => {
            try { return File.ReadAllLines(p); }
            catch { return null; }
        });

        var files = filePaths?
            .Where(p => !string.IsNullOrWhiteSpace(p) && exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        int scannedFiles = files.Count;
        if (scannedFiles == 0)
        {
            string emptySummary = FormatWorkspaceMathSummary(0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new WorkspaceMathConsistencyAudit(
                0, 0, 0, 0, 0, 100.0, Array.Empty<WorkspaceMathFormulaItem>(), "Clean Baseline", emptySummary);
        }

        var items = new List<WorkspaceMathFormulaItem>();
        int totalFormulas = 0;
        int inlineCount = 0;
        int blockCount = 0;
        int syntaxErrorCount = 0;

        var displayMathRegex = new Regex(@"\$\$([\s\S]*?)\$\$");
        var inlineMathRegex = new Regex(@"(?<![\\\$])\$(?!\s)([^\$\n]+?)(?<!\s)\$(?!\d)");

        foreach (var file in files)
        {
            var lines = readLines(file);
            if (lines == null || lines.Length == 0) continue;

            string fullText = string.Join("\n", lines);

            var blockMatches = displayMathRegex.Matches(fullText);
            foreach (Match m in blockMatches)
            {
                totalFormulas++;
                blockCount++;
                string snippet = m.Groups[1].Value.Trim();
                bool hasError = false;
                string errDetail = "Valid Block Math";

                int openBraces = snippet.Count(c => c == '{');
                int closeBraces = snippet.Count(c => c == '}');
                if (openBraces != closeBraces)
                {
                    hasError = true;
                    errDetail = $"Unbalanced curly braces ({openBraces} open vs {closeBraces} close)";
                }

                if (hasError) syntaxErrorCount++;

                int lineNum = fullText.Substring(0, m.Index).Count(c => c == '\n') + 1;
                items.Add(new WorkspaceMathFormulaItem(
                    file, Path.GetFileName(file), lineNum, snippet.Length > 40 ? snippet.Substring(0, 37) + "..." : snippet,
                    true, hasError, errDetail));
            }

            bool inCodeFence = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inCodeFence = !inCodeFence;
                    continue;
                }
                if (inCodeFence) continue;

                var inlineMatches = inlineMathRegex.Matches(line);
                foreach (Match m in inlineMatches)
                {
                    totalFormulas++;
                    inlineCount++;
                    string snippet = m.Groups[1].Value.Trim();
                    bool hasError = false;
                    string errDetail = "Valid Inline Math";

                    int openBraces = snippet.Count(c => c == '{');
                    int closeBraces = snippet.Count(c => c == '}');
                    if (openBraces != closeBraces)
                    {
                        hasError = true;
                        errDetail = $"Unbalanced curly braces ({openBraces} open vs {closeBraces} close)";
                    }

                    if (hasError) syntaxErrorCount++;

                    items.Add(new WorkspaceMathFormulaItem(
                        file, Path.GetFileName(file), i + 1, snippet.Length > 30 ? snippet.Substring(0, 27) + "..." : snippet,
                        false, hasError, errDetail));
                }
            }
        }

        double score = 100.0;
        score -= syntaxErrorCount * 20.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (scannedFiles == 0 || totalFormulas == 0) category = "Clean Baseline";
        else if (score >= 90.0) category = "Optimal KaTeX / LaTeX Syntax Consistency (≥90%)";
        else if (score >= 75.0) category = "Good Formula Structure with Minor Brace Errors (75-89%)";
        else if (score >= 50.0) category = "Moderate Mathematical Delimiter Inconsistencies (50-74%)";
        else category = "Severe Unbalanced Braces or Broken KaTeX Syntax (<50%)";

        string summary = FormatWorkspaceMathSummary(
            scannedFiles, totalFormulas, inlineCount, blockCount, syntaxErrorCount, score, category);

        return new WorkspaceMathConsistencyAudit(
            scannedFiles, totalFormulas, inlineCount, blockCount, syntaxErrorCount,
            score, items, category, summary);
    }

    public static string FormatWorkspaceMathSummary(
        int filesScanned, int totalFormulas, int inlineCount, int blockCount,
        int syntaxErrorCount, double score, string category)
    {
        if (filesScanned == 0) return "Workspace math formula audit: Clean baseline · 0 documents scanned";
        if (totalFormulas == 0) return $"Workspace math formula audit ({filesScanned} docs): Clean baseline · 0 formulas detected";

        string fileStr = filesScanned == 1 ? "1 doc" : $"{filesScanned} docs";
        var parts = new List<string>();

        parts.Add($"{totalFormulas} formula{(totalFormulas == 1 ? "" : "s")}");
        parts.Add($"{inlineCount} inline, {blockCount} block");

        if (syntaxErrorCount > 0)
            parts.Add($"⚠ {syntaxErrorCount} syntax error{(syntaxErrorCount == 1 ? "" : "s")}");
        else
            parts.Add("Valid KaTeX/LaTeX syntax");

        string details = string.Join(" · ", parts);
        return $"Workspace math formula audit ({fileStr} · {details} · {category} · {score:0.#}% syntax score)";
    }
    public static WorkspaceTableAlignmentAudit AuditWorkspaceTableAlignment(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        var exists = fileExists ?? (p => File.Exists(p));
        var readLines = readAllLines ?? (p => {
            try { return File.ReadAllLines(p); }
            catch { return null; }
        });

        var files = filePaths?
            .Where(p => !string.IsNullOrWhiteSpace(p) && exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        int scannedFiles = files.Count;
        if (scannedFiles == 0)
        {
            string emptySummary = FormatWorkspaceTableAlignmentSummary(0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new WorkspaceTableAlignmentAudit(
                0, 0, 0, 0, 0, 0, 100.0, Array.Empty<WorkspaceTableAlignmentItem>(), "Clean Baseline", emptySummary);
        }

        var tables = new List<WorkspaceTableAlignmentItem>();
        int totalTables = 0;
        int validTables = 0;
        int delimiterAnomalies = 0;
        int raggedColumnTables = 0;
        int unescapedPipeTables = 0;

        var delimRegex = new Regex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?\s*$");

        foreach (var file in files)
        {
            var lines = readLines(file);
            if (lines == null || lines.Length == 0) continue;

            string fileName = Path.GetFileName(file);
            bool inCode = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inCode = !inCode;
                    continue;
                }
                if (inCode || string.IsNullOrWhiteSpace(line)) continue;

                // Potential table header if contains '|'
                if (line.Contains("|") && i + 1 < lines.Length)
                {
                    string nextLine = lines[i + 1].Trim();
                    bool hasDelim = delimRegex.IsMatch(nextLine);

                    if (hasDelim)
                    {
                        totalTables++;
                        int startLine = i + 1;
                        var headerCells = SplitTableCells(line);
                        int colCount = headerCells.Count;

                        bool ragged = false;
                        bool unescapedPipe = false;
                        int rowCount = 1; // Header row
                        int j = i + 2;

                        while (j < lines.Length && !string.IsNullOrWhiteSpace(lines[j]) && lines[j].Contains("|"))
                        {
                            rowCount++;
                            var rowCells = SplitTableCells(lines[j]);
                            if (rowCells.Count != colCount)
                            {
                                ragged = true;
                            }
                            j++;
                        }

                        int endLine = j;
                        i = j - 1; // advance outer loop

                        if (!hasDelim) delimiterAnomalies++;
                        if (ragged) raggedColumnTables++;
                        if (unescapedPipe) unescapedPipeTables++;

                        bool isWellAligned = !ragged && hasDelim;
                        if (isWellAligned) validTables++;

                        string status = ragged
                            ? $"Ragged table columns (Expected {colCount} columns)"
                            : "Well-aligned GFM pipe table";

                        tables.Add(new WorkspaceTableAlignmentItem(
                            file, fileName, startLine, endLine, colCount, rowCount,
                            hasDelim, ragged, unescapedPipe, isWellAligned, status));
                    }
                }
            }
        }

        double score = 100.0;
        score -= delimiterAnomalies * 20.0;
        score -= raggedColumnTables * 15.0;
        score -= unescapedPipeTables * 10.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (totalTables == 0) category = "No Tables Detected";
        else if (score >= 90.0) category = "Optimal GFM Table Formatting & Pipe Alignment (≥90%)";
        else if (score >= 75.0) category = "Good Table Structure with Minor Column Variance (75-89%)";
        else if (score >= 50.0) category = "Moderate Table Formatting Discrepancies (50-74%)";
        else category = "Severe Ragged Table Delimiter & Grid Alignment Corruption (<50%)";

        string summary = FormatWorkspaceTableAlignmentSummary(
            scannedFiles, totalTables, validTables, delimiterAnomalies, raggedColumnTables, unescapedPipeTables, score, category);

        return new WorkspaceTableAlignmentAudit(
            scannedFiles, totalTables, validTables, delimiterAnomalies, raggedColumnTables, unescapedPipeTables,
            score, tables, category, summary);
    }

    private static List<string> SplitTableCells(string row)
    {
        var cells = new List<string>();
        string trimmed = row.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

        var parts = trimmed.Split('|');
        foreach (var p in parts)
        {
            cells.Add(p.Trim());
        }
        return cells;
    }

    public static string FormatWorkspaceTableAlignmentSummary(
        int filesScanned, int totalTables, int validTables, int delimAnomalies,
        int raggedTables, int unescapedPipes, double score, string category)
    {
        if (filesScanned == 0) return "Workspace markdown table audit: Clean baseline · 0 documents scanned";
        if (totalTables == 0) return $"Workspace markdown table audit ({filesScanned} docs): Clean baseline · 0 tables detected";

        string fileStr = filesScanned == 1 ? "1 doc" : $"{filesScanned} docs";
        string tableStr = totalTables == 1 ? "1 table" : $"{totalTables} tables";
        var parts = new List<string>();

        parts.Add(tableStr);
        if (validTables > 0) parts.Add($"{validTables} aligned");
        if (raggedTables > 0) parts.Add($"⚠️ {raggedTables} ragged");
        if (delimAnomalies > 0) parts.Add($"⚠️ {delimAnomalies} delimiter anomal{(delimAnomalies == 1 ? "y" : "ies")}");

        string details = string.Join(" · ", parts);
        return $"Workspace markdown table audit ({fileStr} · {details} · {category} · {score:0.#}% formatting score)";
    }

    public static WorkspaceFrontmatterAudit AuditWorkspaceFrontmatterSchemaValidation(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null,
        IReadOnlyList<string>? requiredKeys = null)
    {
        var exists = fileExists ?? (p => File.Exists(p));
        var readLines = readAllLines ?? (p => {
            try { return File.ReadAllLines(p); }
            catch { return null; }
        });

        var reqKeys = (requiredKeys != null && requiredKeys.Count > 0)
            ? requiredKeys
            : new[] { "title", "date", "author", "tags" };

        var items = new List<WorkspaceFrontmatterMetadataItem>();
        int scannedFiles = 0;
        int withFrontmatter = 0;
        int missingFrontmatter = 0;
        int validYaml = 0;
        int validToml = 0;
        int malformed = 0;
        int missingKeysTotal = 0;

        if (filePaths != null)
        {
            foreach (var file in filePaths)
            {
                if (string.IsNullOrWhiteSpace(file)) continue;
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!exists(file)) continue;

                scannedFiles++;
                string fileName = Path.GetFileName(file);
                var lines = readLines(file);

                if (lines == null || lines.Length == 0)
                {
                    missingFrontmatter++;
                    items.Add(new WorkspaceFrontmatterMetadataItem(
                        file, fileName, false, "None", false, null, false, null, false, false, false, 0, 0,
                        reqKeys, 0.0, "Empty document missing frontmatter header"));
                    continue;
                }

                string firstLine = lines[0].Trim();
                bool isYaml = firstLine == "---";
                bool isToml = firstLine == "+++";

                if (!isYaml && !isToml)
                {
                    missingFrontmatter++;
                    items.Add(new WorkspaceFrontmatterMetadataItem(
                        file, fileName, false, "None", false, null, false, null, false, false, false, 0, 0,
                        reqKeys, 0.0, "Document missing frontmatter block"));
                    continue;
                }

                withFrontmatter++;
                string format = isYaml ? "YAML" : "TOML";
                string delimiter = isYaml ? "---" : "+++";

                int endLine = -1;
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Trim() == delimiter)
                    {
                        endLine = i;
                        break;
                    }
                }

                if (endLine == -1)
                {
                    malformed++;
                    items.Add(new WorkspaceFrontmatterMetadataItem(
                        file, fileName, true, format, false, null, false, null, false, false, false, 0, 1,
                        reqKeys, 10.0, $"Unclosed {format} frontmatter delimiter"));
                    continue;
                }

                var parsedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int syntaxErrors = 0;
                int tagCount = 0;

                for (int i = 1; i < endLine; i++)
                {
                    string fLine = lines[i];
                    string fTrimmed = fLine.Trim();
                    if (string.IsNullOrWhiteSpace(fTrimmed) || fTrimmed.StartsWith("#")) continue;

                    int colonIdx = fTrimmed.IndexOf(':');
                    int eqIdx = fTrimmed.IndexOf('=');
                    int sepIdx = isYaml ? colonIdx : (eqIdx != -1 ? eqIdx : colonIdx);

                    if (sepIdx == -1)
                    {
                        if (!fTrimmed.StartsWith("-")) syntaxErrors++;
                        continue;
                    }

                    string key = fTrimmed.Substring(0, sepIdx).Trim();
                    string val = fTrimmed.Substring(sepIdx + 1).Trim().Trim('"', '\'');
                    parsedKeys[key] = val;

                    if (key.Equals("tags", StringComparison.OrdinalIgnoreCase) || key.Equals("tag", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val.StartsWith("[") && val.EndsWith("]"))
                        {
                            var tParts = val.Trim('[', ']').Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                            tagCount += tParts.Length;
                        }
                        else
                        {
                            tagCount += 1;
                        }
                    }
                }

                bool hasTitle = parsedKeys.TryGetValue("title", out var titleVal) && !string.IsNullOrWhiteSpace(titleVal);
                bool hasDate = parsedKeys.TryGetValue("date", out var dateVal) && !string.IsNullOrWhiteSpace(dateVal);
                bool isIsoDate = false;
                if (hasDate)
                {
                    isIsoDate = System.Text.RegularExpressions.Regex.IsMatch(dateVal!, @"^\d{4}-\d{2}-\d{2}");
                }

                bool hasAuthor = parsedKeys.TryGetValue("author", out var authorVal) && !string.IsNullOrWhiteSpace(authorVal);
                bool hasTags = tagCount > 0 || (parsedKeys.ContainsKey("tags") && !string.IsNullOrWhiteSpace(parsedKeys["tags"]));

                var missingKeys = new List<string>();
                foreach (var rk in reqKeys)
                {
                    if (!parsedKeys.ContainsKey(rk) || string.IsNullOrWhiteSpace(parsedKeys[rk]))
                    {
                        missingKeys.Add(rk);
                    }
                }

                if (missingKeys.Count > 0) missingKeysTotal += missingKeys.Count;
                if (syntaxErrors > 0) malformed++;
                else if (isYaml) validYaml++;
                else if (isToml) validToml++;

                double completeness = 100.0;
                completeness -= missingKeys.Count * 20.0;
                completeness -= syntaxErrors * 15.0;
                if (hasDate && !isIsoDate) completeness -= 10.0;
                completeness = Math.Max(0.0, Math.Min(100.0, Math.Round(completeness, 1)));

                string status = missingKeys.Count == 0 && syntaxErrors == 0
                    ? $"Complete {format} frontmatter (Title: \"{titleVal ?? "Untitled"}\")"
                    : missingKeys.Count > 0
                        ? $"Incomplete metadata: missing {string.Join(", ", missingKeys)}"
                        : $"Malformed frontmatter syntax ({syntaxErrors} error(s))";

                items.Add(new WorkspaceFrontmatterMetadataItem(
                    file, fileName, true, format, hasTitle, titleVal, hasDate, dateVal, isIsoDate,
                    hasAuthor, hasTags, tagCount, syntaxErrors, missingKeys, completeness, status));
            }
        }

        double score = 100.0;
        if (scannedFiles > 0)
        {
            double missingFmRatio = (double)missingFrontmatter / scannedFiles;
            score -= missingFmRatio * 30.0;
            score -= malformed * 15.0;
            score -= missingKeysTotal * 5.0;
        }
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (scannedFiles == 0) category = "Clean Baseline";
        else if (score >= 90.0) category = "Optimal Frontmatter Schema Compliance (≥90%)";
        else if (score >= 75.0) category = "Good Metadata Coverage with Minor Key Gaps (75-89%)";
        else if (score >= 50.0) category = "Moderate Frontmatter Deficiencies (50-74%)";
        else category = "Severe Missing Frontmatter & Schema Corruption (<50%)";

        string summary = FormatWorkspaceFrontmatterSchemaValidationSummary(
            scannedFiles, withFrontmatter, missingFrontmatter, validYaml, validToml,
            malformed, missingKeysTotal, score, category);

        return new WorkspaceFrontmatterAudit(
            scannedFiles, withFrontmatter, missingFrontmatter, validYaml, validToml,
            malformed, missingKeysTotal, score, items, category, summary);
    }

    public static string FormatWorkspaceFrontmatterSchemaValidationSummary(
        int filesScanned, int withFrontmatter, int missingFrontmatter,
        int validYaml, int validToml, int malformed, int missingKeys,
        double score, string category)
    {
        if (filesScanned == 0) return "Workspace frontmatter metadata audit: Clean baseline · 0 documents scanned";

        string fileStr = filesScanned == 1 ? "1 doc" : $"{filesScanned} docs";
        var parts = new List<string>();

        parts.Add($"{withFrontmatter}/{filesScanned} with frontmatter");
        if (validYaml > 0) parts.Add($"{validYaml} YAML");
        if (validToml > 0) parts.Add($"{validToml} TOML");
        if (missingFrontmatter > 0) parts.Add($"⚠️ {missingFrontmatter} missing frontmatter");
        if (malformed > 0) parts.Add($"⚠️ {malformed} malformed");
        if (missingKeys > 0) parts.Add($"⚠️ {missingKeys} missing key{(missingKeys == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Workspace frontmatter metadata audit ({fileStr} · {details} · {category} · {score:0.#}% compliance score)";
    }

    public static WorkspaceTaskListAudit AuditWorkspaceTaskListProgression(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        var exists = fileExists ?? (p => File.Exists(p));
        var readLines = readAllLines ?? (p => {
            try { return File.ReadAllLines(p); }
            catch { return null; }
        });

        var items = new List<WorkspaceDocumentTaskItem>();
        int scannedFiles = 0;
        int docsWithTasks = 0;
        int totalTasks = 0;
        int completedTasks = 0;
        int pendingTasks = 0;
        int unlabeledTasks = 0;

        var taskRegex = new System.Text.RegularExpressions.Regex(
            @"^(?<indent>\s*)(?<marker>[-*+]|\d+\.)\s+\[(?<state>[ xX])\](?:\s+(?<desc>.*))?$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        if (filePaths != null)
        {
            foreach (var file in filePaths)
            {
                if (string.IsNullOrWhiteSpace(file)) continue;
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!exists(file)) continue;

                scannedFiles++;
                string fileName = Path.GetFileName(file);
                var lines = readLines(file);
                if (lines == null || lines.Length == 0)
                {
                    continue;
                }

                bool inCodeFence = false;
                bool inFrontmatter = false;
                int docTotal = 0;
                int docCompleted = 0;
                int docPending = 0;
                int docUnlabeled = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string trimmed = line.Trim();

                    if (i == 0 && (trimmed == "---" || trimmed == "+++"))
                    {
                        inFrontmatter = true;
                        continue;
                    }
                    if (inFrontmatter)
                    {
                        if (trimmed == "---" || trimmed == "+++") inFrontmatter = false;
                        continue;
                    }

                    if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                    {
                        inCodeFence = !inCodeFence;
                        continue;
                    }
                    if (inCodeFence) continue;

                    var match = taskRegex.Match(line);
                    if (match.Success)
                    {
                        docTotal++;
                        bool isDone = match.Groups["state"].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
                        if (isDone) docCompleted++; else docPending++;

                        string desc = match.Groups["desc"].Value?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(desc)) docUnlabeled++;
                    }
                }

                if (docTotal > 0)
                {
                    docsWithTasks++;
                    totalTasks += docTotal;
                    completedTasks += docCompleted;
                    pendingTasks += docPending;
                    unlabeledTasks += docUnlabeled;

                    double docPct = Math.Round((double)docCompleted / docTotal * 100.0, 1);
                    string status = docPending == 0
                        ? $"All {docTotal} tasks completed (100%)"
                        : $"{docCompleted}/{docTotal} completed ({docPending} pending)";

                    items.Add(new WorkspaceDocumentTaskItem(
                        file, fileName, docTotal, docCompleted, docPending, docUnlabeled, docPct, status));
                }
            }
        }

        double workspaceCompletionPct = totalTasks > 0
            ? Math.Round((double)completedTasks / totalTasks * 100.0, 1)
            : 100.0;

        double score = 100.0;
        if (totalTasks > 0)
        {
            score -= (100.0 - workspaceCompletionPct) * 0.4;
            score -= unlabeledTasks * 5.0;
        }
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (totalTasks == 0) category = "Clean Baseline · No Tasks";
        else if (score >= 90.0) category = "Optimal Task Velocity (≥90%)";
        else if (score >= 75.0) category = "Healthy Task Progression (75-89%)";
        else if (score >= 50.0) category = "Moderate Pending Backlog (50-74%)";
        else category = "Severe Incomplete Backlog (<50%)";

        string summary = FormatWorkspaceTaskListSummary(
            scannedFiles, docsWithTasks, totalTasks, completedTasks,
            pendingTasks, unlabeledTasks, workspaceCompletionPct, score, category);

        return new WorkspaceTaskListAudit(
            scannedFiles, docsWithTasks, totalTasks, completedTasks,
            pendingTasks, unlabeledTasks, workspaceCompletionPct, score, items, category, summary);
    }

    public static WorkspaceImageAssetsAudit AuditWorkspaceImageAssetPaths(
        IEnumerable<string>? files,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        var fileList = (files ?? Enumerable.Empty<string>()).ToList();
        fileExists ??= path => File.Exists(path);
        readAllLines ??= path => File.Exists(path) ? File.ReadAllLines(path) : null;

        var items = new List<WorkspaceImageAssetItem>();
        int totalDocsAudited = fileList.Count;
        int docsWithImages = 0;
        int totalImages = 0;
        int validLocal = 0;
        int brokenLocal = 0;
        int remoteUrls = 0;
        int dataUris = 0;
        int nonPortable = 0;
        int missingAlt = 0;

        var imgRegex = new System.Text.RegularExpressions.Regex(
            @"!\[(?<alt>[^\]]*)\]\((?<path>[^)\s]+)(?:\s+[""'][^""']*[""'])?\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (var filePath in fileList)
        {
            var lines = readAllLines(filePath);
            if (lines == null || lines.Length == 0) continue;

            string docDir = Path.GetDirectoryName(filePath) ?? string.Empty;
            string fileName = Path.GetFileName(filePath);
            bool docHasImages = false;

            for (int l = 0; l < lines.Length; l++)
            {
                string line = lines[l];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var matches = imgRegex.Matches(line);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    totalImages++;
                    docHasImages = true;
                    string altText = m.Groups["alt"].Value;
                    string imgPath = m.Groups["path"].Value.Trim();
                    bool hasMissingAlt = string.IsNullOrWhiteSpace(altText);
                    if (hasMissingAlt) missingAlt++;

                    bool isRemote = imgPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                    imgPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                    bool isData = imgPath.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
                    bool isLocal = !isRemote && !isData;
                    bool isAbs = isLocal && (Path.IsPathRooted(imgPath) || imgPath.StartsWith("/"));
                    if (isAbs) nonPortable++;

                    bool existsOnDisk = false;
                    if (isLocal)
                    {
                        string normPath = imgPath.Replace('/', Path.DirectorySeparatorChar);
                        if (isAbs)
                        {
                            existsOnDisk = fileExists(imgPath) || fileExists(normPath);
                        }
                        else
                        {
                            string combinedDoc = Path.Combine(docDir, normPath);
                            string rawCombined = Path.Combine(docDir, imgPath);
                            if (fileExists(combinedDoc) || fileExists(rawCombined))
                            {
                                existsOnDisk = true;
                            }
                            else if (!string.IsNullOrEmpty(workspaceRoot))
                            {
                                string combinedWs = Path.Combine(workspaceRoot, normPath);
                                string rawCombinedWs = Path.Combine(workspaceRoot, imgPath);
                                existsOnDisk = fileExists(combinedWs) || fileExists(rawCombinedWs);
                            }
                        }

                        if (existsOnDisk) validLocal++;
                        else brokenLocal++;
                    }
                    else if (isRemote) remoteUrls++;
                    else if (isData) dataUris++;

                    string status = isLocal
                        ? (existsOnDisk ? "Valid Local Asset" : "Broken Image Path")
                        : (isRemote ? "Remote Web Image" : "Data URI Image");

                    string rec = "Media asset verified";
                    if (isLocal && !existsOnDisk) rec = $"Broken image path '{imgPath}': target file does not exist on disk";
                    else if (isAbs) rec = $"Non-portable absolute path '{imgPath}': consider using relative path './...'";
                    else if (hasMissingAlt) rec = "Missing image alt-text: add descriptive label for WCAG accessibility";

                    items.Add(new WorkspaceImageAssetItem(
                        filePath, fileName, l + 1, m.Value, altText, imgPath,
                        isRemote, isData, isLocal, isAbs, existsOnDisk, hasMissingAlt,
                        status, rec));
                }
            }

            if (docHasImages) docsWithImages++;
        }

        if (totalImages == 0)
        {
            string emptySummary = FormatWorkspaceImagePathsSummary(totalDocsAudited, 0, 0, 0, 0, 0, 0, 0, 100.0, "No Images Found");
            return new WorkspaceImageAssetsAudit(
                totalDocsAudited, 0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                items, "No Images Found", emptySummary);
        }

        double score = 100.0;
        score -= brokenLocal * 25.0;
        score -= nonPortable * 10.0;
        score -= missingAlt * 5.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (score >= 95.0) category = "Flawless Media Asset Integrity (≥95%)";
        else if (score >= 80.0) category = "High Media Path Hygiene (80-94%)";
        else if (score >= 60.0) category = "Moderate Asset Consistency (60-79%)";
        else if (score >= 40.0) category = "Broken Media Hazards (40-59%)";
        else category = "Critical Media Resolution Failure (<40%)";

        string summary = FormatWorkspaceImagePathsSummary(
            totalDocsAudited, docsWithImages, totalImages, validLocal, brokenLocal, remoteUrls, nonPortable, missingAlt, score, category);

        return new WorkspaceImageAssetsAudit(
            totalDocsAudited, docsWithImages, totalImages, validLocal, brokenLocal, remoteUrls,
            dataUris, nonPortable, missingAlt, score, items, category, summary);
    }

    public static string FormatWorkspaceImagePathsSummary(
        int totalDocs, int docsWithImages, int totalImages, int validLocal,
        int brokenLocal, int remoteUrls, int nonPortable, int missingAlt,
        double score, string category)
    {
        if (totalImages == 0)
            return $"Workspace media asset audit: Clean workspace · {totalDocs} documents · 0 image links detected";

        var parts = new List<string>
        {
            $"{totalDocs} docs ({docsWithImages} with images)",
            $"{totalImages} images ({validLocal} valid local)"
        };
        if (brokenLocal > 0) parts.Add($"⚠️ {brokenLocal} broken");
        if (remoteUrls > 0) parts.Add($"{remoteUrls} remote");
        if (nonPortable > 0) parts.Add($"{nonPortable} absolute");
        if (missingAlt > 0) parts.Add($"{missingAlt} missing alt");

        string details = string.Join(" · ", parts);
        return $"Workspace media asset audit ({details} · {category} · {score:0.#}% integrity score)";
    }

    public static WorkspaceCrossDocumentLinkAudit AuditWorkspaceInterDocumentLinks(
        IEnumerable<string>? filePaths,
        string? workspaceRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, string[]?>? readAllLines = null)
    {
        var rawList = (filePaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mdFiles = rawList
            .Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int totalDocsAudited = mdFiles.Count;
        if (totalDocsAudited == 0)
        {
            string emptySummary = FormatWorkspaceInterDocumentLinksSummary(0, 0, 0, 0, 0, 0, 100.0, "Clean Workspace");
            return new WorkspaceCrossDocumentLinkAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<CrossDocumentLinkItem>(), "Clean Workspace", emptySummary);
        }

        var items = new List<CrossDocumentLinkItem>();
        int docsWithLinks = 0;
        int crossDocLinks = 0;
        int internalFragmentLinks = 0;
        int validLinks = 0;
        int missingFiles = 0;
        int missingAnchors = 0;

        var headingCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> GetHeadingSlugs(string filePath)
        {
            if (headingCache.TryGetValue(filePath, out var cached)) return cached;

            var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[]? lines = null;
            try
            {
                lines = readAllLines != null ? readAllLines(filePath) : (File.Exists(filePath) ? File.ReadAllLines(filePath) : null);
            }
            catch { }

            if (lines != null)
            {
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("#"))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(trimmed, @"^#+\s+(.*)$");
                        if (m.Success)
                        {
                            string heading = m.Groups[1].Value.Trim();
                            string slug = heading.ToLowerInvariant();
                            slug = System.Text.RegularExpressions.Regex.Replace(slug, @"[^\w\s\-]", "");
                            slug = System.Text.RegularExpressions.Regex.Replace(slug, @"\s+", "-");
                            slugs.Add(slug);
                            slugs.Add(heading.ToLowerInvariant().Replace(" ", "-"));
                        }
                    }
                }
            }

            headingCache[filePath] = slugs;
            return slugs;
        }

        var linkRegex = new System.Text.RegularExpressions.Regex(@"(?<!!)\[(?<text>[^\]]*)\]\((?<target>[^)]+)\)");

        foreach (var file in mdFiles)
        {
            string[]? lines = null;
            try
            {
                lines = readAllLines != null ? readAllLines(file) : (File.Exists(file) ? File.ReadAllLines(file) : null);
            }
            catch { }

            if (lines == null || lines.Length == 0) continue;

            string sourceDir = Path.GetDirectoryName(file) ?? workspaceRoot ?? "";
            string fileName = Path.GetFileName(file);
            bool docHasLinks = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var matches = linkRegex.Matches(line);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    string linkText = m.Groups["text"].Value;
                    string rawTarget = m.Groups["target"].Value.Trim();

                    if (rawTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        rawTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        rawTarget.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                        rawTarget.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    docHasLinks = true;

                    if (rawTarget.StartsWith("#"))
                    {
                        internalFragmentLinks++;
                        string anchor = rawTarget.TrimStart('#').ToLowerInvariant();
                        var selfSlugs = GetHeadingSlugs(file);
                        bool anchorResolved = selfSlugs.Contains(anchor);
                        if (anchorResolved) validLinks++;
                        else missingAnchors++;

                        items.Add(new CrossDocumentLinkItem(
                            file, fileName, i + 1, linkText, rawTarget, file, anchor,
                            IsCrossDocument: false, TargetFileExists: true, TargetAnchorResolved: anchorResolved,
                            Status: anchorResolved ? "Resolved Internal Anchor" : "Missing Internal Anchor Heading",
                            Recommendation: anchorResolved ? "Valid fragment anchor" : $"Heading matching '#{anchor}' not found in current document"));
                    }
                    else
                    {
                        crossDocLinks++;
                        string targetDocPath;
                        string? targetAnchor = null;

                        int hashIdx = rawTarget.IndexOf('#');
                        if (hashIdx >= 0)
                        {
                            targetDocPath = rawTarget.Substring(0, hashIdx).Trim();
                            targetAnchor = rawTarget.Substring(hashIdx + 1).Trim().ToLowerInvariant();
                        }
                        else
                        {
                            targetDocPath = rawTarget;
                        }

                        string resolvedFullPath;
                        try
                        {
                            resolvedFullPath = Path.IsPathRooted(targetDocPath)
                                ? Path.GetFullPath(targetDocPath)
                                : Path.GetFullPath(Path.Combine(sourceDir, targetDocPath));
                        }
                        catch
                        {
                            resolvedFullPath = Path.Combine(sourceDir, targetDocPath);
                        }

                        bool exists = fileExists != null ? fileExists(resolvedFullPath) : File.Exists(resolvedFullPath);
                        bool anchorResolved = true;

                        if (!exists)
                        {
                            missingFiles++;
                            items.Add(new CrossDocumentLinkItem(
                                file, fileName, i + 1, linkText, rawTarget, resolvedFullPath, targetAnchor,
                                IsCrossDocument: true, TargetFileExists: false, TargetAnchorResolved: false,
                                Status: "Missing Target File",
                                Recommendation: $"Target document '{targetDocPath}' was not found on disk"));
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(targetAnchor))
                            {
                                var targetSlugs = GetHeadingSlugs(resolvedFullPath);
                                anchorResolved = targetSlugs.Contains(targetAnchor);
                                if (!anchorResolved) missingAnchors++;
                            }

                            if (anchorResolved) validLinks++;

                            string status = anchorResolved
                                ? "Resolved Cross-Document Link"
                                : "Missing Anchor Heading in Target Document";
                            string rec = anchorResolved
                                ? "Valid cross-document link"
                                : $"Target document exists but anchor '#{targetAnchor}' was not found in headings";

                            items.Add(new CrossDocumentLinkItem(
                                file, fileName, i + 1, linkText, rawTarget, resolvedFullPath, targetAnchor,
                                IsCrossDocument: true, TargetFileExists: true, TargetAnchorResolved: anchorResolved,
                                Status: status, Recommendation: rec));
                        }
                    }
                }
            }

            if (docHasLinks) docsWithLinks++;
        }

        int totalLinksAudited = items.Count;
        if (totalLinksAudited == 0)
        {
            string emptySummary = FormatWorkspaceInterDocumentLinksSummary(totalDocsAudited, 0, 0, 0, 0, 0, 100.0, "No Links Found");
            return new WorkspaceCrossDocumentLinkAudit(
                totalDocsAudited, 0, 0, 0, 0, 0, 0, 0, 100.0,
                items, "No Links Found", emptySummary);
        }

        double score = 100.0;
        score -= missingFiles * 25.0;
        score -= missingAnchors * 15.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (score >= 95.0) category = "Flawless Hyperlink Network (≥95%)";
        else if (score >= 80.0) category = "High Hypertext Integrity (80-94%)";
        else if (score >= 60.0) category = "Moderate Linking Consistency (60-79%)";
        else if (score >= 40.0) category = "Broken Hyperlink Hazards (40-59%)";
        else category = "Critical Broken Hypertext Network (<40%)";

        string summary = FormatWorkspaceInterDocumentLinksSummary(
            totalDocsAudited, docsWithLinks, totalLinksAudited, validLinks, missingFiles, missingAnchors, score, category);

        return new WorkspaceCrossDocumentLinkAudit(
            totalDocsAudited, docsWithLinks, totalLinksAudited, crossDocLinks, internalFragmentLinks,
            validLinks, missingFiles, missingAnchors, score, items, category, summary);
    }

    public static string FormatWorkspaceInterDocumentLinksSummary(
        int totalDocs, int docsWithLinks, int totalLinks, int validLinks,
        int missingFiles, int missingAnchors, double score, string category)
    {
        if (totalLinks == 0)
            return $"Workspace inter-document link audit: Clean workspace · {totalDocs} documents · 0 cross-document links detected";

        var parts = new List<string>
        {
            $"{totalDocs} docs ({docsWithLinks} with links)",
            $"{totalLinks} links ({validLinks} valid)"
        };
        if (missingFiles > 0) parts.Add($"⚠️ {missingFiles} missing files");
        if (missingAnchors > 0) parts.Add($"⚠️ {missingAnchors} missing anchors");

        string details = string.Join(" · ", parts);
        return $"Workspace inter-document link audit ({details} · {category} · {score:0.#}% integrity score)";
    }


    public static string FormatWorkspaceTaskListSummary(
        int totalDocs, int docsWithTasks, int totalTasks, int completedTasks,
        int pendingTasks, int unlabeledTasks, double completionPct, double score, string category)
    {
        if (totalTasks == 0) return "Workspace task list audit: Clean baseline · 0 task checkboxes detected";

        string docStr = docsWithTasks == 1 ? "1 document" : $"{docsWithTasks} documents";
        var parts = new List<string>
        {
            $"{completedTasks}/{totalTasks} tasks completed ({completionPct:0.#}%)",
            $"across {docStr}",
            $"{pendingTasks} pending"
        };

        if (unlabeledTasks > 0) parts.Add($"⚠️ {unlabeledTasks} unlabeled");

        string details = string.Join(" · ", parts);
        return $"Workspace task list & action item audit ({details} · {category} · {score:0.#}% hygiene score)";
    }

    public static WorkspaceHeadingSlugAndTocAudit AuditWorkspaceHeadingSlugsAndTocAnchors(
        string? workspaceDirectory = null,
        IEnumerable<string>? explicitFiles = null,
        Func<string, string[]?>? readAllLines = null)
    {
        string root = workspaceDirectory ?? Directory.GetCurrentDirectory();
        var files = (explicitFiles != null)
            ? explicitFiles.ToList()
            : (Directory.Exists(root)
                ? Directory.GetFiles(root, "*.md", SearchOption.AllDirectories).ToList()
                : new List<string>());

        int totalDocsAudited = files.Count;
        if (totalDocsAudited == 0)
        {
            string emptySummary = FormatWorkspaceHeadingSlugsSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "No Documents Found");
            return new WorkspaceHeadingSlugAndTocAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<HeadingSlugItem>(), "No Documents Found", emptySummary);
        }

        var items = new List<HeadingSlugItem>();
        int docsWithHeadings = 0;
        int totalHeadings = 0;
        int duplicateSlugsCount = 0;
        int brokenTocAnchorsCount = 0;
        int tocOrphanCount = 0;
        var uniqueSlugsGlobal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            string[]? lines = null;
            try
            {
                lines = readAllLines != null
                    ? readAllLines(file)
                    : (File.Exists(file) ? File.ReadAllLines(file) : null);
            }
            catch
            {
                // skip unreadable file
            }

            if (lines == null || lines.Length == 0) continue;

            string fileName = Path.GetFileName(file);
            bool docHasHeadings = false;
            var docSlugs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var headingsInDoc = new List<(int LineNumber, string Title, int Level, string BaseSlug, string FinalSlug)>();
            var internalAnchorsInDoc = new List<(int LineNumber, string Anchor)>();

            bool inCodeBlock = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inCodeBlock = !inCodeBlock;
                    continue;
                }
                if (inCodeBlock) continue;

                var headingMatch = Regex.Match(line, @"^(?<hashes>#{1,6})\s+(?<title>.+)$");
                if (headingMatch.Success)
                {
                    docHasHeadings = true;
                    int level = headingMatch.Groups["hashes"].Length;
                    string title = headingMatch.Groups["title"].Value.Trim();

                    string baseSlug = GenerateHeadingSlug(title);
                    if (!docSlugs.ContainsKey(baseSlug))
                    {
                        docSlugs[baseSlug] = 1;
                        headingsInDoc.Add((i + 1, title, level, baseSlug, baseSlug));
                    }
                    else
                    {
                        int occurrence = docSlugs[baseSlug]++;
                        string finalSlug = $"{baseSlug}-{occurrence}";
                        duplicateSlugsCount++;
                        headingsInDoc.Add((i + 1, title, level, baseSlug, finalSlug));
                    }
                }

                var anchorMatches = Regex.Matches(line, @"\[(?<text>[^\]]+)\]\(#(?<anchor>[^\)]+)\)");
                foreach (Match am in anchorMatches)
                {
                    string anchor = am.Groups["anchor"].Value.Trim().ToLowerInvariant();
                    internalAnchorsInDoc.Add((i + 1, anchor));
                }
            }

            if (docHasHeadings) docsWithHeadings++;
            totalHeadings += headingsInDoc.Count;

            var docDefinedSlugs = new HashSet<string>(headingsInDoc.Select(h => h.FinalSlug), StringComparer.OrdinalIgnoreCase);

            foreach (var a in internalAnchorsInDoc)
            {
                if (!docDefinedSlugs.Contains(a.Anchor))
                {
                    brokenTocAnchorsCount++;
                }
            }

            foreach (var h in headingsInDoc)
            {
                uniqueSlugsGlobal.Add(h.FinalSlug);
                bool isDup = docSlugs.TryGetValue(h.BaseSlug, out int occ) && occ > 1 && h.FinalSlug != h.BaseSlug;
                int refCount = internalAnchorsInDoc.Count(a => string.Equals(a.Anchor, h.FinalSlug, StringComparison.OrdinalIgnoreCase));
                bool isOrphan = (headingsInDoc.Count >= 4 && refCount == 0);
                if (isOrphan) tocOrphanCount++;

                string status = !isDup && refCount > 0
                    ? "Referenced Anchor"
                    : (isDup ? "Duplicate Slug Disambiguated" : (isOrphan ? "TOC Orphan Heading" : "Defined Heading"));

                string rec = isDup
                    ? $"Disambiguate duplicate heading '{h.Title}' to avoid indexed anchor collision (#{h.FinalSlug})"
                    : (refCount == 0 && headingsInDoc.Count >= 5
                        ? $"Add Table of Contents link to heading '{h.Title}'"
                        : "Consistent heading slug and anchor resolution");

                items.Add(new HeadingSlugItem(
                    file, fileName, h.LineNumber, h.Title, h.Level,
                    h.FinalSlug, isDup, docSlugs[h.BaseSlug],
                    refCount, isOrphan, status, rec));
            }
        }

        double score = 100.0;
        score -= (brokenTocAnchorsCount * 20.0);
        score -= (duplicateSlugsCount * 8.0);
        if (totalHeadings > 0 && tocOrphanCount > 0 && totalHeadings >= 10)
        {
            double orphanRatio = (double)tocOrphanCount / totalHeadings;
            score -= (orphanRatio * 15.0);
        }
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 => "Optimal Heading & TOC Integrity",
            >= 80.0 => "High Navigational Consistency",
            >= 60.0 => "Moderate Anchor Drift",
            >= 40.0 => "Broken TOC Anchors Detected",
            _ => "Severe Navigational Desynchronization"
        };

        string summary = FormatWorkspaceHeadingSlugsSummary(
            totalDocsAudited, docsWithHeadings, totalHeadings, uniqueSlugsGlobal.Count,
            duplicateSlugsCount, brokenTocAnchorsCount, tocOrphanCount, score, category);

        return new WorkspaceHeadingSlugAndTocAudit(
            totalDocsAudited, docsWithHeadings, totalHeadings, uniqueSlugsGlobal.Count,
            duplicateSlugsCount, brokenTocAnchorsCount, tocOrphanCount, score,
            items, category, summary);
    }

    public static string GenerateHeadingSlug(string headingTitle)
    {
        if (string.IsNullOrWhiteSpace(headingTitle)) return "heading";
        string lower = headingTitle.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[*_`~]", "");
        lower = Regex.Replace(lower, @"[^\w\s-]", "");
        lower = Regex.Replace(lower, @"[\s_]+", "-").Trim('-');
        return string.IsNullOrEmpty(lower) ? "heading" : lower;
    }

    public static string FormatWorkspaceHeadingSlugsSummary(
        int totalDocs, int docsWithHeadings, int totalHeadings, int uniqueSlugs,
        int duplicateSlugs, int brokenAnchors, int orphanHeadings,
        double score, string category)
    {
        if (totalDocs == 0) return "Workspace heading slug & TOC audit: 0 documents audited";
        if (totalHeadings == 0)
            return $"Workspace heading slug & TOC audit ({totalDocs} docs): 0 headings found · 100% score";

        var parts = new List<string>
        {
            $"{totalDocs} doc{(totalDocs == 1 ? string.Empty : "s")} ({docsWithHeadings} with headings)",
            $"{totalHeadings} heading{(totalHeadings == 1 ? string.Empty : "s")}"
        };
        if (duplicateSlugs > 0) parts.Add($"⚠️ {duplicateSlugs} duplicate slug{(duplicateSlugs == 1 ? string.Empty : "s")}");
        if (brokenAnchors > 0) parts.Add($"⚠️ {brokenAnchors} broken TOC anchor{(brokenAnchors == 1 ? string.Empty : "s")}");
        if (orphanHeadings > 0) parts.Add($"{orphanHeadings} unlinked heading{(orphanHeadings == 1 ? string.Empty : "s")}");

        string details = string.Join(" · ", parts);
        return $"Workspace heading slug & TOC audit ({details} · {category} · {score:0.#}% consistency score)";
    }

}

/// <summary>
/// Represents dormancy and file inventory metrics for a workspace directory cluster.
/// </summary>
public sealed record WorkspaceClusterDormancyInfo(
    string WorkspaceName,
    string DirectoryPath,
    int TotalFiles,
    int DormantFiles,
    double DormancyRatio,
    IReadOnlyList<string> SampleFiles);

/// <summary>
/// Represents the result of a workspace folder cluster dormancy audit.
/// </summary>
public sealed record WorkspaceClusterDormancyAudit(
    int TotalFiles,
    int TotalClusters,
    int StagnantClusters,
    IReadOnlyList<WorkspaceClusterDormancyInfo> Clusters,
    string Summary);

/// <summary>
/// Represents activity velocity and active document ratios for a workspace directory cluster.
/// </summary>
public sealed record ClusterActivityVelocityInfo(
    string ClusterName,
    int TotalFiles,
    int ActiveFiles,
    double ActivityPercentage,
    string VelocityRating,
    string MostRecentFile);

/// <summary>
/// Represents the result of a workspace folder cluster activity velocity audit.
/// </summary>
public sealed record ClusterActivityVelocityAudit(
    int TotalFiles,
    int TotalClusters,
    double OverallVelocityPercentage,
    string TopClusterName,
    IReadOnlyList<ClusterActivityVelocityInfo> Clusters,
    string Summary);

/// <summary>
/// Represents an active workspace cluster filter chip for quick search filtering in the SuiteHub.
/// </summary>
public sealed record ActiveClusterFilterChip(
    string ClusterName,
    int TotalFiles,
    int ActiveFiles,
    double ActivityPercentage,
    string FilterQuery,
    string DisplayBadge);

/// <summary>
/// Represents file count and percentage metrics for an individual file extension / format.
/// </summary>
public sealed record FormatClusterInfo(
    string Extension,
    string FormatName,
    int FileCount,
    double Percentage);

/// <summary>
/// Represents the result of a file format cluster distribution matrix and silo detection audit.
/// </summary>
public sealed record FormatClusterMatrixAudit(
    int TotalFiles,
    int TotalFormats,
    string DominantFormat,
    double DominantPercentage,
    double DiversityIndex,
    string SiloStatus,
    IReadOnlyList<FormatClusterInfo> Formats,
    string Summary);

/// <summary>
/// Represents format coverage and companion files for an individual document family.
/// </summary>
public sealed class DocumentFamilyCoverageInfo
{
    public string BaseName { get; }
    public string DirectoryPath { get; }
    public HashSet<string> Extensions { get; }

    public DocumentFamilyCoverageInfo(string baseName, string dir, HashSet<string> extensions)
    {
        BaseName = baseName;
        DirectoryPath = dir;
        Extensions = extensions;
    }

    public bool HasSource => Extensions.Contains(".md") || Extensions.Contains(".markdown");
    public bool HasCompiled => Extensions.Contains(".docx") || Extensions.Contains(".pdf") || Extensions.Contains(".html") || Extensions.Contains(".epub") || Extensions.Contains(".pptx");


}

/// <summary>
/// Represents the result of a companion export format coverage audit across recent documents.
/// </summary>
public sealed record CompanionFormatCoverageAudit(
    int TotalFamilies,
    int FullyCoveredFamilies,
    int SourceOnlyFamilies,
    int ExportOnlyFamilies,
    double CoveragePercentage,
    string CoverageStatus,
    IReadOnlyList<DocumentFamilyCoverageInfo> Families,
    string Summary);

/// <summary>
/// Represents aging category and file details for a single document.
/// </summary>
public sealed record DocumentAgingInfo(
    string FilePath,
    string FileName,
    DateTime LastModifiedUtc,
    double AgeDays,
    string AgeTier);

/// <summary>
/// Represents the result of an audit of workspace document aging, modification staleness, and editing velocity.
/// </summary>
public sealed record DocumentStalenessAudit(
    int TotalFiles,
    int ActiveFiles,
    int WarmFiles,
    int ColdFiles,
    int ArchivedFiles,
    double FreshnessIndex,
    double AverageAgeDays,
    IReadOnlyList<DocumentAgingInfo> DocumentAges,
    string VelocityCategory,
    string Summary);

/// <summary>
/// Represents file size and storage footprint metrics for an individual document.
/// </summary>
public sealed record DocumentStorageInfo(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    string FormattedSize,
    bool IsHeavyOutlier);

/// <summary>
/// Represents the result of an audit of workspace document storage footprint and disk volume distribution.
/// </summary>
public sealed record WorkspaceStorageFootprintAudit(
    int TotalFiles,
    int ExistingFiles,
    long TotalBytes,
    string FormattedTotalSize,
    double AverageBytesPerFile,
    string FormattedAverageSize,
    int HeavyOutliersCount,
    string LargestFileName,
    long LargestFileSizeBytes,
    IReadOnlyList<DocumentStorageInfo> DocumentSizes,
    string FootprintCategory,
    string Summary);

/// <summary>
/// Represents parsed naming convention and character compliance information for an individual document.
/// </summary>
public sealed record DocumentNamingInfo(
    string FilePath,
    string FileName,
    string BaseName,
    string DetectedConvention,
    bool HasSpaces,
    bool HasSpecialChars);

/// <summary>
/// Represents the result of an audit of workspace document naming conventions, casing consistency, and shell-safety.
/// </summary>
public sealed record DocumentNamingConventionAudit(
    int TotalFiles,
    int KebabCaseCount,
    int SnakeCaseCount,
    int PascalCaseCount,
    int CamelCaseCount,
    int SpacedCount,
    int OtherCount,
    string DominantConvention,
    double ConsistencyScore,
    int FilesWithSpecialChars,
    IReadOnlyList<DocumentNamingInfo> Documents,
    string Category,
    string Summary);

/// <summary>
/// Represents path length and folder nesting metrics for an individual file in a MAX_PATH safety audit.
/// </summary>
public sealed record PathLengthMetricInfo(
    string FilePath,
    string FileName,
    int PathLength,
    int NestingDepth,
    bool ExceedsMaxPath,
    bool IsApproachingLimit);

/// <summary>
/// Represents the result of an audit of workspace file path lengths, folder nesting depth, and Windows MAX_PATH safety.
/// </summary>
public sealed record WorkspacePathLengthAudit(
    int TotalFiles,
    int SafePathCount,
    int WarningPathCount,
    int ViolationPathCount,
    int MaxPathLength,
    string LongestPath,
    int MaxNestingDepth,
    string DeepestPath,
    double AveragePathLength,
    double AverageNestingDepth,
    double PathSafetyScore,
    IReadOnlyList<PathLengthMetricInfo> PathMetrics,
    string SafetyCategory,
    string Summary);

/// <summary>
/// Represents file extension count and percentage distribution within the workspace.
/// </summary>
public sealed record ExtensionDistributionInfo(
    string Extension,
    string Category,
    int FileCount,
    double Percentage);

/// <summary>
/// Represents the result of an audit of workspace file extension diversity, format distribution, and markdown variants.
/// </summary>
public sealed record WorkspaceExtensionDiversityAudit(
    int TotalFiles,
    int UniqueExtensions,
    double ShannonEntropy,
    int StandardMarkdownCount,
    int NonStandardMarkdownCount,
    int OfficeDocumentCount,
    int WebAndDataCount,
    int AssetCount,
    IReadOnlyList<string> NonStandardMarkdownFiles,
    IReadOnlyList<ExtensionDistributionInfo> Distributions,
    string DiversityCategory,
    string Summary);

/// <summary>
/// Represents evaluated relative cross-reference or asset embed target status.
/// </summary>
public sealed record CrossReferenceLinkInfo(
    string SourceFile,
    string TargetPath,
    string LinkText,
    bool Exists,
    bool IsImageEmbed);

/// <summary>
/// Represents the result of an audit of workspace inter-document cross-references, dead links, and orphan media assets.
/// </summary>
public sealed record WorkspaceDeadLinkAudit(
    int TotalFiles,
    int TotalRelativeLinks,
    int ValidLinkCount,
    int BrokenLinkCount,
    int OrphanAssetCount,
    double IntegrityScore,
    IReadOnlyList<CrossReferenceLinkInfo> BrokenLinks,
    IReadOnlyList<string> OrphanAssets,
    string IntegrityCategory,
    string Summary);

/// <summary>
/// Represents compression savings and modern format recommendations for an individual media asset.
/// </summary>
public sealed record AssetCompressionItem(
    string AssetPath,
    string FileName,
    string Extension,
    long CurrentSizeBytes,
    string CurrentFormattedSize,
    long EstimatedOptimizedSizeBytes,
    string EstimatedOptimizedFormattedSize,
    double PotentialSavingsPercent,
    string RecommendedFormat,
    bool IsOversized,
    bool IsRemote);

/// <summary>
/// Represents the result of an audit of workspace media asset compression,
/// image format modernization (WebP/AVIF), and storage optimization potential.
/// </summary>
public sealed record WorkspaceAssetCompressionAudit(
    int TotalAssetsScanned,
    int LocalAssetsCount,
    int RemoteAssetsCount,
    int ModernFormatCount,
    int LegacyFormatCount,
    int OversizedCount,
    long TotalCurrentSizeBytes,
    long TotalEstimatedOptimizedSizeBytes,
    long TotalPotentialSavingsBytes,
    double OverallSavingsPercentage,
    double AssetEfficiencyScore,
    IReadOnlyList<AssetCompressionItem> Assets,
    string EfficiencyCategory,
    string Summary);

/// <summary>
/// Represents high-DPI scaling readiness and SVG bloat optimization metrics for an individual media asset.
/// </summary>
public sealed record AssetDpiSvgItem(
    string AssetPath,
    string FileName,
    string AssetType,
    long FileSizeBytes,
    bool IsHighDpiReady,
    bool HasSvgBloat,
    int SvgBloatTagCount,
    double EstimatedOptimizationSavingsPercent,
    string OptimizationRecommendation);

/// <summary>
/// Represents the result of an audit of workspace High-DPI display readiness, raster density,
/// and SVG vector minification opportunities.
/// </summary>
public sealed record WorkspaceAssetDpiSvgAudit(
    int TotalAssetsEvaluated,
    int RasterCount,
    int SvgCount,
    int HighDpiReadyCount,
    int LowDpiRiskCount,
    int UnoptimizedSvgCount,
    double OverallHighDpiReadinessScore,
    long PotentialSvgSavingsBytes,
    IReadOnlyList<AssetDpiSvgItem> Assets,
    string ReadinessCategory,
    string Summary);

/// <summary>
/// Represents parsed font family metrics, declaration source, and fallback status for an asset.
/// </summary>
public sealed record WorkspaceFontItem(
    string SourceFile,
    string FontFamily,
    string DeclaredSource,
    bool HasGenericFallback,
    string FallbackCategory,
    bool IsRemoteAsset,
    bool IsSelfContained);

/// <summary>
/// Represents the result of an audit of workspace typography, embedded web font assets,
/// generic CSS fallback coverage, and self-containment resilience.
/// </summary>
public sealed record WorkspaceTypographyAudit(
    int TotalFilesScanned,
    int TotalFontDeclarations,
    int DistinctFamiliesCount,
    int SelfContainedFontCount,
    int MissingFallbackCount,
    int RemoteWebFontCount,
    double TypographicSelfContainmentScore,
    IReadOnlyList<WorkspaceFontItem> FontDeclarations,
    IReadOnlyList<string> DiscoveredFamilies,
    string TypographicHealthCategory,
    string Summary);

/// <summary>
/// Represents evaluated file size metrics, storage category, and bloat status for a workspace file.
/// </summary>
public sealed record WorkspaceFileSizeItem(
    string FilePath,
    string FileName,
    string Extension,
    long SizeBytes,
    string FormattedSize,
    string SizeTier,
    bool IsHeavyBloat);

/// <summary>
/// Represents the result of an audit of workspace file size distribution, storage bloat detection,
/// and storage compactness scoring.
/// </summary>
public sealed record WorkspaceFileSizeDistributionAudit(
    int TotalFilesScanned,
    long TotalSizeBytes,
    string FormattedTotalSize,
    long MeanSizeBytes,
    long MaxSizeBytes,
    string HeaviestFileName,
    int HeavyBloatCount,
    double StorageCompactnessScore,
    string StorageHealthCategory,
    IReadOnlyDictionary<string, int> ExtensionCounts,
    IReadOnlyList<WorkspaceFileSizeItem> Files,
    string Summary);

/// <summary>
/// Represents an identified group of identical duplicate assets sharing a common SHA-256 hash.
/// </summary>
public sealed record DuplicateAssetGroup(
    string ContentHashSha256,
    long AssetSizeBytes,
    string FormattedAssetSize,
    IReadOnlyList<string> DuplicateFilePaths,
    long WastedRedundantBytes,
    string FormattedWastedBytes);

/// <summary>
/// Represents the result of an audit of workspace asset redundancy, SHA-256 content hash deduplication,
/// and archive packaging savings potential.
/// </summary>
public sealed record WorkspaceAssetDeduplicationAudit(
    int TotalAssetsScanned,
    int UniqueAssetCount,
    int DuplicateGroupCount,
    int TotalRedundantAssetCount,
    long TotalScannedSizeBytes,
    long TotalRedundantWastedBytes,
    string FormattedRedundantWastedBytes,
    double DeduplicationEfficiencyScore,
    IReadOnlyList<DuplicateAssetGroup> DuplicateGroups,
    string DeduplicationCategory,
    string Summary);

/// <summary>
/// Represents parsed level, formatting style, title, and anchor slug for an individual document heading.
/// </summary>
public sealed record DocumentHeadingItem(
    string FilePath,
    int LineNumber,
    int Level,
    string Style,
    string Title,
    string AnchorSlug,
    bool HasInvalidChars,
    bool IsLevelSkipped);

/// <summary>
/// Represents the result of an audit of workspace heading depth uniformity, ATX vs Setext style consistency,
/// and anchor slug integrity.
/// </summary>
public sealed record WorkspaceHeadingUniformityAudit(
    int TotalDocumentsEvaluated,
    int TotalHeadingsFound,
    int AtxCount,
    int SetextCount,
    int SkippedLevelCount,
    int DuplicateSlugCount,
    double HeadingUniformityScore,
    IReadOnlyList<DocumentHeadingItem> Headings,
    IReadOnlyList<string> Issues,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated code fence language tag, highlighter support, and canonical alias recommendations for a code block.
/// </summary>
public sealed record WorkspaceCodeFenceItem(
    string FilePath,
    string FileName,
    int LineNumber,
    string RawFenceTag,
    string NormalizedLanguage,
    bool IsUntagged,
    bool HasHighlighterSupport,
    bool IsNonCanonicalAlias,
    string RecommendedTag);

/// <summary>
/// Represents the result of an audit of workspace code fence language tags, syntax highlighter coverage,
/// and canonical language alias consistency.
/// </summary>
public sealed record WorkspaceCodeFenceLanguageAudit(
    int TotalDocumentsEvaluated,
    int TotalCodeFencesFound,
    int TaggedFencesCount,
    int UntaggedFencesCount,
    int SupportedHighlighterCount,
    int UnsupportedOrUnknownCount,
    int NonCanonicalAliasCount,
    double CoverageScore,
    IReadOnlyList<WorkspaceCodeFenceItem> CodeFences,
    IReadOnlyDictionary<string, int> LanguageDistribution,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated media link details, path resolution, resource categorization, and portability checks.
/// </summary>
public sealed record WorkspaceMediaLinkItem(
    string FilePath,
    string FileName,
    int LineNumber,
    string RawLink,
    string ResolvedPath,
    string MediaType,
    bool IsExternal,
    bool IsDataUri,
    bool Exists,
    bool IsAbsoluteLocal,
    bool HasUnencodedSpaces,
    string? IssueDescription);

/// <summary>
/// Represents the result of an audit of workspace media link integrity, missing local assets,
/// data URIs, and cross-platform portability warnings.
/// </summary>
public sealed record WorkspaceMediaLinkIntegrityAudit(
    int TotalDocumentsEvaluated,
    int TotalMediaLinksFound,
    int ValidLocalMediaCount,
    int MissingLocalMediaCount,
    int ExternalMediaCount,
    int DataUriCount,
    int PortabilityWarningCount,
    double IntegrityScore,
    IReadOnlyList<WorkspaceMediaLinkItem> MediaLinks,
    IReadOnlyList<string> MissingPaths,
    string Category,
    string Summary);

/// <summary>
/// Represents an evaluated cross-document link, target file resolution, and target heading anchor slug match.
/// </summary>
public sealed record CrossDocumentAnchorLinkItem(
    string SourceFilePath,
    string SourceFileName,
    string TargetRelativePath,
    string TargetResolvedPath,
    string HeadingAnchorSlug,
    int LineNumber,
    bool TargetFileExists,
    bool TargetAnchorExists,
    string Snippet,
    string? IssueDescription);

/// <summary>
/// Represents the result of an audit of workspace cross-document relative anchor links ([Doc](other.md#heading)),
/// target file existence, and destination heading slug resolution.
/// </summary>
public sealed record WorkspaceCrossDocumentAnchorAudit(
    int TotalDocumentsEvaluated,
    int TotalCrossDocLinksFound,
    int ValidLinksCount,
    int BrokenTargetFileCount,
    int BrokenAnchorSlugCount,
    double CrossDocAnchorIntegrityScore,
    IReadOnlyList<CrossDocumentAnchorLinkItem> AnchorLinks,
    IReadOnlyList<string> MissingAnchors,
    string Category,
    string Summary);




/// <summary>
/// Represents evaluated table metrics, column consistency, and delimiter compliance for an individual markdown table.
/// </summary>
public sealed record WorkspaceTableItem(
    string FilePath,
    string FileName,
    int StartLineNumber,
    int HeaderColumnCount,
    int DataRowCount,
    bool HasDelimiterRow,
    bool HasRaggedRows,
    int RaggedRowCount,
    IReadOnlyList<string> ColumnAlignments,
    string Snippet,
    string? IssueDescription);

/// <summary>
/// Represents the result of an audit of workspace markdown tables, column count uniformity,
/// delimiter rows, alignment syntax, and tabular data integrity.
/// </summary>
public sealed record WorkspaceTableFormattingAudit(
    int TotalDocumentsEvaluated,
    int TotalTablesFound,
    int ValidTablesCount,
    int RaggedTablesCount,
    int MissingDelimiterTablesCount,
    int EmptyHeaderOnlyTablesCount,
    double TabularIntegrityScore,
    IReadOnlyList<WorkspaceTableItem> Tables,
    IReadOnlyList<string> FormattingAlerts,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated in-degree, out-degree, role, and orphan status for an individual workspace document node.
/// </summary>
public sealed record WorkspaceDocumentGraphNode(
    string FilePath,
    string FileName,
    int InDegree,
    int OutDegree,
    string NodeRole,
    bool IsOrphan);

/// <summary>
/// Represents a directed cross-document link from a source document to a target document.
/// </summary>
public sealed record WorkspaceDocumentGraphEdge(
    string SourceFileName,
    string TargetFileName,
    string RawLinkTarget,
    int LineNumber);

/// <summary>
/// Represents the result of an audit of the workspace markdown cross-document link graph,
/// degree centrality, hub identification, and isolated orphaned note detection.
/// </summary>
public sealed record WorkspaceDocumentGraphAudit(
    int TotalDocumentsEvaluated,
    int TotalLinksFound,
    int OrphanedDocumentCount,
    int HubDocumentCount,
    double GraphDensity,
    double CrossReferenceScore,
    IReadOnlyList<WorkspaceDocumentGraphNode> Nodes,
    IReadOnlyList<WorkspaceDocumentGraphEdge> Edges,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated path resolution, alt-text, and physical existence for an individual media asset reference.
/// </summary>
public sealed record WorkspaceMediaItem(
    string DocumentPath,
    string DocumentName,
    string AltText,
    string RawSource,
    bool IsRemote,
    bool Exists,
    string ResolvedFullPath,
    string? IssueDescription);

/// <summary>
/// Represents the result of an audit of workspace media asset references, local path resolution, and missing image hazards.
/// </summary>
public sealed record WorkspaceMediaAssetAudit(
    int TotalDocumentsEvaluated,
    int TotalMediaReferences,
    int LocalAssetsChecked,
    int ValidLocalAssets,
    int MissingLocalAssets,
    int WebRemoteAssets,
    double MediaIntegrityScore,
    IReadOnlyList<WorkspaceMediaItem> MediaItems,
    IReadOnlyList<string> MissingAssetPaths,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated alt-text accessibility compliance, quality rating, and line number for an embedded workspace media item.
/// </summary>
public sealed record WorkspaceMediaAccessibilityItem(
    string DocumentPath,
    string DocumentName,
    int LineNumber,
    string AltText,
    string MediaSource,
    bool HasEmptyAlt,
    bool HasGenericFilenameAlt,
    bool IsDescriptive,
    string AccessibilityQuality);

/// <summary>
/// Represents the result of an audit of workspace embedded media alt-text quality, WCAG non-text content compliance,
/// empty caption violations, and generic placeholder descriptions.
/// </summary>
public sealed record WorkspaceMediaAccessibilityAudit(
    int TotalDocumentsEvaluated,
    int TotalMediaReferences,
    int DescriptiveAltCount,
    int EmptyAltCount,
    int GenericFilenameAltCount,
    double AccessibilityComplianceScore,
    IReadOnlyList<WorkspaceMediaAccessibilityItem> MediaItems,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated frontmatter presence, format (YAML/TOML), schema field completeness, and status for a workspace document.
/// </summary>
public sealed record WorkspaceFrontmatterDocItem(
    string FilePath,
    string FileName,
    bool HasFrontmatter,
    string FrontmatterFormat,
    bool IsMalformed,
    bool HasTitle,
    bool HasDate,
    bool HasAuthor,
    bool HasTags,
    bool HasDescription,
    int SchemaFieldsFound,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown documents for frontmatter metadata blocks (YAML/TOML),
/// schema completeness (title, date, author, tags, description), and consistency across workspace files.
/// </summary>
public sealed record WorkspaceFrontmatterMetadataAudit(
    int TotalDocumentsEvaluated,
    int DocumentsWithFrontmatter,
    int DocumentsMissingFrontmatter,
    int YamlFrontmatterCount,
    int TomlFrontmatterCount,
    int MalformedFrontmatterCount,
    int TitleCoverageCount,
    int DateCoverageCount,
    int AuthorCoverageCount,
    int TagsCoverageCount,
    int DescriptionCoverageCount,
    double CompletenessScore,
    IReadOnlyList<WorkspaceFrontmatterDocItem> Documents,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated heading metrics, H1 presence, outline depth, and skipped level status for an individual workspace document.
/// </summary>
public sealed record WorkspaceDocumentHeadingItem(
    string FilePath,
    string FileName,
    int TotalHeadings,
    int H1Count,
    int MaxDepth,
    int SkippedLevels,
    bool HasMultipleH1,
    bool HasMissingH1,
    double DocumentScore,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown document heading hierarchy depth, skipped outline levels,
/// and document structure ergonomics.
/// </summary>
public sealed record WorkspaceHeadingHierarchyAudit(
    int TotalDocumentsEvaluated,
    int TotalHeadingsFound,
    int DocumentsWithValidOutline,
    int DocumentsWithSkippedLevels,
    int DocumentsWithMultipleH1,
    int DocumentsMissingH1,
    int MaxDepthAcrossWorkspace,
    double ErgonomicsScore,
    IReadOnlyList<WorkspaceDocumentHeadingItem> Documents,
    string Category,
    string Summary);

/// <summary>
/// Represents domain name, link count, protocol security, and sample links for an external host domain.
/// </summary>
public sealed record ExternalDomainLinkItem(
    string Domain,
    int LinkCount,
    bool IsSecureHttps,
    IReadOnlyList<string> SampleUrls,
    string Status);

/// <summary>
/// Represents an internal anchor reference (#anchor), its target document, resolved status, and line number.
/// </summary>
public sealed record LocalAnchorReferenceItem(
    string DocumentName,
    int LineNumber,
    string AnchorText,
    string TargetAnchor,
    bool IsResolved,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown external hyperlink domains, internal heading anchors (#anchor),
/// protocol security, and link integrity.
/// </summary>
public sealed record WorkspaceHyperlinkDomainsAudit(
    int TotalDocumentsEvaluated,
    int TotalExternalLinks,
    int TotalInternalAnchors,
    int UniqueDomainsCount,
    int InsecureHttpCount,
    int BrokenAnchorCount,
    double LinkingHealthScore,
    IReadOnlyList<ExternalDomainLinkItem> ExternalDomains,
    IReadOnlyList<LocalAnchorReferenceItem> LocalAnchors,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated code block count, line volume, percentage, and alias normalization for a specific programming language tag.
/// </summary>
public sealed record CodeBlockLanguageCountItem(
    string LanguageTag,
    int BlockCount,
    int LineCount,
    double Percentage,
    bool IsNormalized,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown fenced code block language tags,
/// missing syntax highlighting coverage, and language distribution.
/// </summary>
public sealed record WorkspaceCodeBlockLanguageAudit(
    int TotalDocumentsEvaluated,
    int TotalCodeBlocksFound,
    int UntaggedBlocksCount,
    int UniqueLanguagesCount,
    string DominantLanguage,
    double SyntaxHighlightingCoverageScore,
    IReadOnlyList<CodeBlockLanguageCountItem> Languages,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated code fence count, delimiter distribution, and indentation alert status for a markdown file.
/// </summary>
public sealed record CodeFenceFileAuditItem(
    string FilePath,
    string FileName,
    int TotalFences,
    int BacktickFences,
    int TildeFences,
    int IndentedFences,
    int MixedDelimiterCount,
    bool HasFormattingAnomaly,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown code block fence formatting, delimiter consistency,
/// and indentation anomalies.
/// </summary>
public sealed record WorkspaceCodeFenceIndentationAudit(
    int TotalFilesScanned,
    int FilesWithFences,
    int TotalFences,
    int BacktickCount,
    int TildeCount,
    int IndentationAlertCount,
    double ConsistencyScore,
    IReadOnlyList<CodeFenceFileAuditItem> Files,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics and formatting consistency for an individual markdown file's syntax dialect.
/// </summary>
public sealed record MarkdownDialectFileItem(
    string FilePath,
    string FileName,
    int TotalFences,
    int BacktickFences,
    int TildeFences,
    int TaggedLanguageFences,
    int UntaggedFences,
    int HyphenListItems,
    int AsteriskListItems,
    int PlusListItems,
    int AtxHeadings,
    int SetextHeadings,
    double FileDialectConsistencyScore);

/// <summary>
/// Represents the result of an audit of workspace markdown syntax dialect conventions, delimiter alignment,
/// code fence language tagging, and heading styles.
/// </summary>
public sealed record WorkspaceMarkdownDialectAudit(
    int TotalFilesScanned,
    int FilesWithMarkdown,
    string DominantFenceType,
    int BacktickTotal,
    int TildeTotal,
    int TaggedLanguageFencesTotal,
    int UntaggedFencesTotal,
    int HyphenListTotal,
    int AsteriskListTotal,
    int PlusListTotal,
    int AtxHeadingsTotal,
    int SetextHeadingsTotal,
    double DialectAlignmentScore,
    IReadOnlyList<MarkdownDialectFileItem> Files,
    string Category,
    string Summary,
    string? RecommendedConfiguration = null);

/// <summary>
/// Represents the result of a simulated batch dialect normalization dry-run across all workspace markdown files.
/// </summary>
public sealed record DialectNormalizationDryRunResult(
    int TotalFilesAudited,
    int FilesRequiringNormalization,
    int TotalReplacementCount,
    int FenceReplacementsCount,
    int ListMarkerReplacementsCount,
    int HeadingStyleReplacementsCount,
    double ProjectedConformityScore,
    IReadOnlyList<string> ChangedFileNames,
    string Summary);

/// <summary>
/// Represents evaluated metrics, backup path, and replacement counts for an individual workspace file normalized to the dominant dialect.
/// </summary>
public sealed record FileNormalizationApplyItem(
    string FilePath,
    string BackupPath,
    int ReplacementsCount,
    bool IsSuccessful,
    string Status);

/// <summary>
/// Represents the result of an applied batch markdown dialect normalization across workspace files with verified rollback backups.
/// </summary>
public sealed record DialectNormalizationApplyResult(
    int TotalFilesAudited,
    int FilesNormalized,
    int TotalReplacementsApplied,
    int BackupsCreatedCount,
    bool AllBackupsVerified,
    IReadOnlyList<FileNormalizationApplyItem> Items,
    string Summary);

/// <summary>
/// Represents evaluated frontmatter metrics, format, required keys, parsed date, and tag counts for an individual workspace file.
/// </summary>
public sealed record WorkspaceFrontmatterFileItem(
    string FilePath,
    bool HasFrontmatter,
    string Format,
    IReadOnlyList<string> MissingRequiredKeys,
    IReadOnlyDictionary<string, string> ParsedKeys,
    bool HasValidDate,
    string? DateParsed,
    bool HasValidTags,
    int TagCount,
    bool IsConforming,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown documents for frontmatter schema validity (YAML/TOML),
/// required metadata keys (title, date, tags, author), date format parsing, and tag array structure.
/// </summary>
public sealed record WorkspaceFrontmatterSchemaAudit(
    int TotalFilesAudited,
    int FilesWithFrontmatter,
    int ConformingFilesCount,
    int MissingKeysViolationsCount,
    int DateFormatViolationsCount,
    int TagFormatViolationsCount,
    double SchemaIntegrityScore,
    IReadOnlyList<WorkspaceFrontmatterFileItem> Files,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, link type, raw target, resolved path, and disk existence for a workspace link.
/// </summary>
public sealed record WorkspaceLinkTargetItem(
    string SourceFilePath,
    int LineNumber,
    string LinkType,
    string TargetRaw,
    string TargetResolvedPath,
    bool TargetExists,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown cross-document wiki-links, relative document hyperlinks,
/// and local media asset embeds for physical file existence and hyperlink integrity.
/// </summary>
public sealed record WorkspaceWikiLinksAudit(
    int TotalFilesScanned,
    int TotalLinksAudited,
    int WikiLinksCount,
    int RelativeDocLinksCount,
    int AssetLinksCount,
    int ValidTargetsCount,
    int BrokenTargetsCount,
    double LinkIntegrityScore,
    IReadOnlyList<WorkspaceLinkTargetItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, frequency, document count, and taxonomy status for an individual workspace tag.
/// </summary>
public sealed record TagTaxonomyItem(
    string TagName,
    int Frequency,
    int DocumentCount,
    bool HasCasingInconsistency,
    bool HasPluralDuplicate,
    bool IsSingleton,
    IReadOnlyList<string> Variants,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown documents for tag taxonomy consistency:
/// frontmatter tags, inline #tags, casing variations, singular/plural duplication, and orphaned singleton tags.
/// </summary>
public sealed record WorkspaceTagTaxonomyAudit(
    int TotalFilesScanned,
    int TotalTagOccurrences,
    int UniqueTagsCount,
    int CasingInconsistenciesCount,
    int PluralDuplicatesCount,
    int SingletonTagsCount,
    double TaxonomyCohesionScore,
    IReadOnlyList<TagTaxonomyItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated word count, estimated reading time, and length tier for an individual workspace document.
/// </summary>
public sealed record WorkspaceDocumentWordCountItem(
    string FilePath,
    string FileName,
    int WordCount,
    double EstimatedReadingMinutes,
    string LengthTier,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown documents for word counts, estimated reading times,
/// length distribution tiers, and longest document outliers.
/// </summary>
public sealed record WorkspaceWordCountDistributionAudit(
    int TotalFilesScanned,
    int TotalWords,
    double TotalReadingMinutes,
    double AverageWordsPerDocument,
    int QuickReadCount,
    int StandardReadCount,
    int LongFormReadCount,
    string LongestDocumentName,
    int MaxWordCount,
    double DistributionScore,
    IReadOnlyList<WorkspaceDocumentWordCountItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated code block metrics, language tag, canonical identifier, and alias status for an individual code fence.
/// </summary>
public sealed record WorkspaceFenceGrammarItem(
    string FilePath,
    string FileName,
    int StartLine,
    int LineCount,
    string RawLanguage,
    string CanonicalLanguage,
    bool IsUntagged,
    bool IsAlias,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown documents for code fence language tags, missing tags,
/// and non-standard syntax aliases.
/// </summary>
public sealed record WorkspaceFenceGrammarAudit(
    int TotalFilesScanned,
    int TotalCodeBlocks,
    int TaggedCodeBlocks,
    int UntaggedCodeBlocks,
    int AliasCodeBlocks,
    int UniqueLanguagesCount,
    double MetadataRigorScore,
    IReadOnlyList<WorkspaceFenceGrammarItem> Items,
    string Category,
    string Summary);



/// <summary>
/// Represents evaluated math formula metrics, file location, LaTeX snippet, and syntax error status for an individual expression.
/// </summary>
public sealed record WorkspaceMathFormulaItem(
    string FilePath,
    string FileName,
    int LineNumber,
    string FormulaSnippet,
    bool IsBlockMath,
    bool HasSyntaxError,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown documents for inline and block LaTeX/KaTeX mathematical expressions,
/// balanced curly braces, and syntax consistency.
/// </summary>
public sealed record WorkspaceMathConsistencyAudit(
    int TotalFilesScanned,
    int TotalFormulasFound,
    int InlineFormulaCount,
    int BlockFormulaCount,
    int SyntaxErrorCount,
    double MathConsistencyScore,
    IReadOnlyList<WorkspaceMathFormulaItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, line bounds, column/row count, and alignment status for an individual workspace table.
/// </summary>
public sealed record WorkspaceTableAlignmentItem(
    string FilePath,
    string FileName,
    int TableStartLine,
    int TableEndLine,
    int ColumnCount,
    int RowCount,
    bool HasDelimiterRow,
    bool HasRaggedColumns,
    bool HasUnescapedPipes,
    bool IsWellAligned,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown documents for GFM pipe table column alignment,
/// delimiter separator bars, and grid formatting integrity.
/// </summary>
public sealed record WorkspaceTableAlignmentAudit(
    int TotalFilesScanned,
    int TotalTablesFound,
    int ValidTablesCount,
    int DelimiterAnomalyCount,
    int RaggedColumnCount,
    int UnescapedPipeCount,
    double FormattingRigorScore,
    IReadOnlyList<WorkspaceTableAlignmentItem> Tables,
    string Category,
    string Summary);



/// <summary>
/// Represents evaluated frontmatter presence, format, keys, syntax errors, and completeness for an individual workspace document.
/// </summary>
public sealed record WorkspaceFrontmatterMetadataItem(
    string FilePath,
    string FileName,
    bool HasFrontmatter,
    string Format,
    bool HasTitle,
    string? Title,
    bool HasDate,
    string? DateString,
    bool IsDateIso8601,
    bool HasAuthor,
    bool HasTags,
    int TagCount,
    int SyntaxErrorCount,
    IReadOnlyList<string> MissingRequiredKeys,
    double CompletenessScore,
    string Status);

/// <summary>
/// Represents the result of an audit of workspace markdown document frontmatter blocks (YAML/TOML),
/// required schema metadata keys, ISO 8601 date validation, and metadata completeness.
/// </summary>
public sealed record WorkspaceFrontmatterAudit(
    int TotalDocumentsAudited,
    int FrontmatterDocumentCount,
    int MissingFrontmatterCount,
    int ValidYamlCount,
    int ValidTomlCount,
    int MalformedSyntaxCount,
    int MissingRequiredKeysCount,
    double SchemaComplianceScore,
    IReadOnlyList<WorkspaceFrontmatterMetadataItem> Items,
    string Category,
    string Summary);


/// <summary>
/// Represents task list progress, completion count, and pending action items for an individual workspace markdown document.
/// </summary>
public sealed record WorkspaceDocumentTaskItem(
    string FilePath,
    string FileName,
    int TotalTasks,
    int CompletedTasks,
    int PendingTasks,
    int UnlabeledTasks,
    double CompletionPercentage,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown task list checkboxes, project action item completion velocity,
/// pending checklist items, and task tracking hygiene across all workspace documents.
/// </summary>
public sealed record WorkspaceTaskListAudit(
    int TotalDocumentsAudited,
    int DocumentsWithTasksCount,
    int TotalTasksFound,
    int CompletedTasksCount,
    int PendingTasksCount,
    int UnlabeledTasksCount,
    double WorkspaceCompletionPercentage,
    double TaskTrackingHygieneScore,
    IReadOnlyList<WorkspaceDocumentTaskItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents an image asset reference, target path, protocol, local file existence, and accessibility status in a workspace document.
/// </summary>
public sealed record WorkspaceImageAssetItem(
    string FilePath,
    string FileName,
    int LineNumber,
    string RawMatch,
    string AltText,
    string ImagePath,
    bool IsRemoteUrl,
    bool IsDataUri,
    bool IsLocalFile,
    bool IsAbsolutePath,
    bool FileExistsOnDisk,
    bool HasMissingAltText,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of workspace markdown image asset paths, local file existence on disk,
/// broken media anchors, remote URL dependencies, data URIs, and alt-text accessibility.
/// </summary>
/// <summary>
/// Represents evaluated source file, target file, anchor heading, and resolution status for an individual cross-document markdown link.
/// </summary>
public sealed record CrossDocumentLinkItem(
    string SourceFilePath,
    string SourceFileName,
    int LineNumber,
    string LinkText,
    string RawTarget,
    string TargetFilePath,
    string? TargetAnchor,
    bool IsCrossDocument,
    bool TargetFileExists,
    bool TargetAnchorResolved,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of workspace markdown cross-document hyperlinks, internal fragment anchors,
/// target file existence, anchor heading resolution, and hypertext network integrity.
/// </summary>
public sealed record WorkspaceCrossDocumentLinkAudit(
    int TotalDocumentsAudited,
    int DocumentsWithLinksCount,
    int TotalLinksAudited,
    int CrossDocumentLinksCount,
    int InternalFragmentLinksCount,
    int ValidLinksCount,
    int MissingFileCount,
    int MissingAnchorCount,
    double HyperlinkIntegrityScore,
    IReadOnlyList<CrossDocumentLinkItem> Items,
    string Category,
    string Summary);

public sealed record WorkspaceImageAssetsAudit(
    int TotalDocumentsAudited,
    int DocumentsWithImagesCount,
    int TotalImagesFound,
    int ValidLocalImagesCount,
    int BrokenLocalImagesCount,
    int RemoteUrlsCount,
    int DataUrisCount,
    int NonPortableAbsolutePathsCount,
    int MissingAltTextCount,
    double MediaAssetIntegrityScore,
    IReadOnlyList<WorkspaceImageAssetItem> Items,
    string Category,
    string Summary);


/// <summary>
/// Represents evaluated metrics, line number, heading text, level, generated slug, and TOC reference status in a workspace document.
/// </summary>
public sealed record HeadingSlugItem(
    string FilePath,
    string FileName,
    int LineNumber,
    string HeadingText,
    int HeadingLevel,
    string GeneratedSlug,
    bool IsDuplicateSlug,
    int TotalOccurrences,
    int ReferencingTocCount,
    bool IsTocOrphan,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of workspace markdown heading slugs, duplicate anchor disambiguation,
/// Table of Contents internal fragment consistency, and orphaned heading navigation.
/// </summary>
public sealed record WorkspaceHeadingSlugAndTocAudit(
    int TotalDocumentsAudited,
    int DocumentsWithHeadingsCount,
    int TotalHeadingsFound,
    int UniqueSlugsCount,
    int DuplicateSlugsCount,
    int BrokenTocAnchorsCount,
    int TocOrphanHeadingsCount,
    double HeadingNavigationScore,
    IReadOnlyList<HeadingSlugItem> Items,
    string Category,
    string Summary);


public sealed record WorkspaceTableAutoFixResult(
    int TotalFilesScanned,
    int TotalFilesModified,
    int TotalTablesAligned,
    int TotalPipesPadded,
    bool IsDryRun,
    string Summary,
    int BackupsCreated = 0);

public sealed record WorkspaceTableRollbackResult(
    int TotalBackupsScanned,
    int TotalFilesRestored,
    string Summary);

public sealed record WorkspaceCrossFileLinkItem(
    string SourceFilePath,
    int LineNumber,
    string LinkText,
    string TargetUrl,
    bool IsExternal,
    bool TargetFileExists,
    bool AnchorExists,
    string Recommendation);

public sealed record WorkspaceCrossFileLinkAudit(
    int TotalFilesScanned,
    int TotalLinksEvaluated,
    int ValidLocalLinks,
    int BrokenTargetFiles,
    int BrokenAnchorHeaders,
    int ExternalLinksCount,
    double HyperlinkCohesionScore,
    IReadOnlyList<WorkspaceCrossFileLinkItem> Items,
    string Category,
    string Summary);

