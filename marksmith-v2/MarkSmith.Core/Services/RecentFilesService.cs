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
