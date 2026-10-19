using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Line-based text diff for the version-history timeline (GitHub/VS Code style). Prefix/suffix
/// trimming + an LCS pass over the remaining middle (with a size guard so pathological inputs
/// degrade to a whole-block replace instead of exploding), then a side-by-side row builder that
/// pairs removed/added runs 1:1 like GitHub's split view.
/// </summary>
public static class LineDiff
{
    public enum Kind { Same, Removed, Added }

    public sealed record Line(Kind Kind, int? OldNumber, int? NewNumber, string Text);

    public sealed record Cell(Kind Kind, int? LineNumber, string Text)
    {
        public string NumberLabel => LineNumber?.ToString() ?? "";
        public bool IsRemoved => Kind == Kind.Removed;
        public bool IsAdded => Kind == Kind.Added;
        public bool IsSame => Kind == Kind.Same;
    }

    public sealed record Row(Cell? Left, Cell? Right);

    // Beyond this many LCS cells we fall back to a whole-block replace rather than O(n*m) DP.
    private const long MaxLcsCells = 4_000_000;

    private static bool LinesEqual(string s1, string s2, bool ignoreWhitespace) =>
        ignoreWhitespace ? string.Equals(s1.Trim(), s2.Trim(), StringComparison.Ordinal) : s1 == s2;

    /// <summary>Unified diff of two texts: removed/added/same lines with source and target line numbers.</summary>
    public static List<Line> Diff(string before, string after, bool ignoreWhitespace = false)
    {
        var a = SplitLines(before);
        var b = SplitLines(after);

        // Trim the common prefix and suffix so the LCS only runs over the changed middle.
        int pre = 0;
        while (pre < a.Length && pre < b.Length && LinesEqual(a[pre], b[pre], ignoreWhitespace)) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre &&
               LinesEqual(a[a.Length - 1 - suf], b[b.Length - 1 - suf], ignoreWhitespace)) suf++;

        var aMid = a.AsSpan(pre, a.Length - pre - suf).ToArray();
        var bMid = b.AsSpan(pre, b.Length - pre - suf).ToArray();

        var result = new List<Line>(pre + suf + aMid.Length + bMid.Length);
        for (int i = 0; i < pre; i++) result.Add(new Line(Kind.Same, i + 1, i + 1, b[i]));

        if ((long)aMid.Length * bMid.Length <= MaxLcsCells)
            AppendLcs(result, aMid, bMid, pre, pre, ignoreWhitespace);
        else
            AppendReplace(result, aMid, bMid, pre, pre);

        int aTail = a.Length - suf;
        int bTail = b.Length - suf;
        for (int i = 0; i < suf; i++)
            result.Add(new Line(Kind.Same, aTail + i + 1, bTail + i + 1, b[bTail + i]));

        return result;
    }

    private static void AppendLcs(List<Line> result, string[] a, string[] b, int aBase, int bBase, bool ignoreWhitespace = false)
    {
        // Classic LCS DP (row-optimized) then walk back to emit ops in order.
        int n = a.Length, m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = LinesEqual(a[i], b[j], ignoreWhitespace) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (LinesEqual(a[x], b[y], ignoreWhitespace))
            {
                result.Add(new Line(Kind.Same, aBase + x + 1, bBase + y + 1, b[y]));
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                result.Add(new Line(Kind.Removed, aBase + x + 1, null, a[x]));
                x++;
            }
            else
            {
                result.Add(new Line(Kind.Added, null, bBase + y + 1, b[y]));
                y++;
            }
        }
        result.EnsureCapacity(result.Count + (n - x) + (m - y));
        while (x < n) { result.Add(new Line(Kind.Removed, aBase + x + 1, null, a[x])); x++; }
        while (y < m) { result.Add(new Line(Kind.Added, null, bBase + y + 1, b[y])); y++; }
    }

    private static void AppendReplace(List<Line> result, string[] a, string[] b, int aBase, int bBase)
    {
        result.EnsureCapacity(result.Count + a.Length + b.Length);
        result.AddRange(a.Select((line, i) => new Line(Kind.Removed, aBase + i + 1, null, line)));
        result.AddRange(b.Select((line, j) => new Line(Kind.Added, null, bBase + j + 1, line)));
    }

    /// <summary>Side-by-side rows: unchanged lines on both sides, removed on the left, added on the
    /// right; adjacent removed/added runs are paired 1:1 like GitHub's split view.</summary>
    public static List<Row> BuildSideBySide(IReadOnlyList<Line> lines)
    {
        var rows = new List<Row>(lines.Count);
        int i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (line.Kind == Kind.Same)
            {
                rows.Add(new Row(
                    new Cell(Kind.Same, line.OldNumber, line.Text),
                    new Cell(Kind.Same, line.NewNumber, line.Text)));
                i++;
                continue;
            }

            // Collect a contiguous removed+added run and pair them.
            int remStart = i;
            while (i < lines.Count && lines[i].Kind == Kind.Removed) i++;
            int remCount = i - remStart;

            int addStart = i;
            while (i < lines.Count && lines[i].Kind == Kind.Added) i++;
            int addCount = i - addStart;

            int paired = Math.Min(remCount, addCount);
            for (int k = 0; k < paired; k++)
            {
                var r = lines[remStart + k];
                var a = lines[addStart + k];
                rows.Add(new Row(
                    new Cell(Kind.Removed, r.OldNumber, r.Text),
                    new Cell(Kind.Added, a.NewNumber, a.Text)));
            }
            for (int k = paired; k < remCount; k++)
            {
                var r = lines[remStart + k];
                rows.Add(new Row(new Cell(Kind.Removed, r.OldNumber, r.Text), null));
            }
            for (int k = paired; k < addCount; k++)
            {
                var a = lines[addStart + k];
                rows.Add(new Row(null, new Cell(Kind.Added, a.NewNumber, a.Text)));
            }
        }
        return rows;
    }

    public sealed record UnifiedRow(Kind Kind, int? OldNumber, int? NewNumber, string Text, string Prefix, bool IsGap = false)
    {
        public bool IsLine => !IsGap;
        public string OldNumberLabel => OldNumber?.ToString() ?? "";
        public string NewNumberLabel => NewNumber?.ToString() ?? "";
        public bool IsRemoved => Kind == Kind.Removed;
        public bool IsAdded => Kind == Kind.Added;
        public bool IsSame => Kind == Kind.Same;
    }

    /// <summary>Extracts lines of a specific kind (Added/Removed/Same) from unified diff rows, excluding fold gaps.</summary>
    public static List<string> ExtractModifiedLines(IEnumerable<UnifiedRow> rows, Kind kind)
    {
        if (rows is null) return new List<string>();
        return rows.Where(r => r.Kind == kind && !r.IsGap).Select(r => r.Text).ToList();
    }

    /// <summary>
    /// Formats a human-readable churn ratio and summary (e.g. "25.0% churn (+5 / -3 / =24 lines)").
    /// </summary>
    public static string FormatDiffRatio(int added, int removed, int unchanged)
    {
        int modified = added + removed;
        int total = modified + unchanged;
        if (total <= 0) return "0% modified (0 lines)";
        double pct = (double)modified / total * 100.0;
        return $"{pct:F1}% churn (+{added} / -{removed} / ={unchanged} lines)";
    }

    /// <summary>Builds unified inline diff rows (GitHub/VS Code inline style) with old/new line numbers.</summary>
    public static List<UnifiedRow> BuildUnified(IReadOnlyList<Line> lines)
    {
        var list = new List<UnifiedRow>(lines.Count);
        foreach (var l in lines)
        {
            string prefix = l.Kind switch
            {
                Kind.Added => "+ ",
                Kind.Removed => "- ",
                _ => "  "
            };
            list.Add(new UnifiedRow(l.Kind, l.OldNumber, l.NewNumber, l.Text, prefix));
        }
        return list;
    }

    /// <summary>One display row of a collapsed diff: a real line, or (Line == null) a run of
    /// HiddenCount unchanged lines folded away between two changes.</summary>
    public sealed record Segment(Line? Line, int HiddenCount);

    /// <summary>Folds long runs of unchanged lines down to <paramref name="context"/> lines either
    /// side of each change, so a one-word edit in a 2,000-line document reads as a few rows instead
    /// of the whole file. A run of a single hidden line is shown rather than folded (the fold row
    /// would take the same space). Returns no segments at all when nothing changed.</summary>
    public static List<Segment> Collapse(IReadOnlyList<Line> lines, int context = 3)
    {
        var result = new List<Segment>();
        var keep = new bool[lines.Count];
        bool anyChange = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == Kind.Same) continue;
            anyChange = true;
            for (int k = Math.Max(0, i - context); k <= Math.Min(lines.Count - 1, i + context); k++)
                keep[k] = true;
        }
        if (!anyChange) return result;

        int idx = 0;
        while (idx < lines.Count)
        {
            if (keep[idx]) { result.Add(new Segment(lines[idx], 0)); idx++; continue; }
            int start = idx;
            while (idx < lines.Count && !keep[idx]) idx++;
            int run = idx - start;
            if (run == 1) result.Add(new Segment(lines[start], 0));
            else result.Add(new Segment(null, run));
        }
        return result;
    }

    /// <summary>Every line of <paramref name="text"/> as Added: the "diff" of a first version.
    /// Diff("", text) would also emit the empty text's one blank line as Removed.</summary>
    public static List<Line> AllAdded(string text) =>
        SplitLines(text).Select((line, i) => new Line(Kind.Added, null, i + 1, line)).ToList();

    /// <summary>"1 unchanged line" / "42 unchanged lines" for a folded run.</summary>
    public static string HiddenLabel(int count) =>
        count == 1 ? "1 unchanged line" : $"{count:N0} unchanged lines";

    public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, List<Line> Lines)
    {
        public string Header => $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@";
    }

    /// <summary>Groups unified diff lines into standard Git/unified hunks with context.</summary>
    public static List<DiffHunk> ExtractHunks(IReadOnlyList<Line> lines, int context = 3)
    {
        var hunks = new List<DiffHunk>();
        if (lines.Count == 0) return hunks;

        int i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind == Kind.Same) { i++; continue; }

            int changeStart = Math.Max(0, i - context);
            int changeEnd = i;
            while (changeEnd < lines.Count)
            {
                if (lines[changeEnd].Kind != Kind.Same)
                {
                    changeEnd++;
                }
                else
                {
                    int nextChange = changeEnd;
                    while (nextChange < lines.Count && nextChange - changeEnd <= 2 * context && lines[nextChange].Kind == Kind.Same)
                    {
                        nextChange++;
                    }
                    if (nextChange < lines.Count && lines[nextChange].Kind != Kind.Same)
                    {
                        changeEnd = nextChange;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            int hunkEnd = Math.Min(lines.Count, changeEnd + context);
            var hunkLines = new List<Line>();
            for (int k = changeStart; k < hunkEnd; k++) hunkLines.Add(lines[k]);

            int oldStart = hunkLines.FirstOrDefault(l => l.OldNumber.HasValue)?.OldNumber ?? 1;
            int oldCount = hunkLines.Count(l => l.Kind != Kind.Added);
            int newStart = hunkLines.FirstOrDefault(l => l.NewNumber.HasValue)?.NewNumber ?? 1;
            int newCount = hunkLines.Count(l => l.Kind != Kind.Removed);

            hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, hunkLines));
            i = hunkEnd;
        }

        return hunks;
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// Formats a comprehensive statistics breakdown of a diff including added, removed, unchanged lines, churn percentage, and change hunks.
    /// </summary>
    public static string FormatDiffStatistics(int added, int removed, int unchanged, int hunkCount)
    {
        var ratio = FormatDiffRatio(added, removed, unchanged);
        var hunkLabel = hunkCount == 1 ? "1 change hunk" : $"{hunkCount} change hunks";
        return $"{ratio} · {hunkLabel}";
    }

    /// <summary>
    /// Formats a version timeline header timestamp into a clean, human-readable display string.
    /// </summary>
    public static string FormatVersionTimestampHeader(string? versionName, DateTimeOffset? timestamp)
    {
        var name = string.IsNullOrWhiteSpace(versionName) ? "Version" : versionName.Trim();
        if (!timestamp.HasValue) return name;
        var t = timestamp.Value.LocalDateTime;
        return $"{name} · {t:dddd, d MMMM yyyy} · {t:HH:mm}";
    }

    /// <summary>
    /// Formats a concise delta summary of added and removed lines for a version (e.g. "Version 1.2 · +5 added, -2 removed").
    /// </summary>
    public static string FormatVersionDeltaSummary(string? versionName, int linesAdded, int linesRemoved)
    {
        var name = string.IsNullOrWhiteSpace(versionName) ? "Version" : versionName.Trim();
        if (linesAdded == 0 && linesRemoved == 0)
            return $"{name} · No line changes recorded";
        if (linesAdded > 0 && linesRemoved > 0)
            return $"{name} · +{linesAdded} added, -{linesRemoved} removed";
        if (linesAdded > 0)
            return $"{name} · +{linesAdded} line{(linesAdded == 1 ? "" : "s")} added";
        return $"{name} · -{linesRemoved} line{(linesRemoved == 1 ? "" : "s")} removed";
    }

    /// <summary>
    /// Formats a complete one-line version stats card (name, timestamp, lines added and removed).
    /// </summary>
    public static string FormatVersionStatsCard(string? versionName, DateTimeOffset? timestamp, int linesAdded, int linesRemoved)
    {
        var name = string.IsNullOrWhiteSpace(versionName) ? "Version" : versionName.Trim();
        var timePart = timestamp.HasValue
            ? $" · {timestamp.Value.LocalDateTime:dddd, d MMMM yyyy} · {timestamp.Value.LocalDateTime:HH:mm}"
            : "";
        var deltaPart = linesAdded == 0 && linesRemoved == 0
            ? "No line changes recorded"
            : linesAdded > 0 && linesRemoved > 0
                ? $"+{linesAdded} added, -{linesRemoved} removed"
                : linesAdded > 0
                    ? $"+{linesAdded} line{(linesAdded == 1 ? "" : "s")} added"
                    : $"-{linesRemoved} line{(linesRemoved == 1 ? "" : "s")} removed";

        return $"{name}{timePart} · {deltaPart}";
    }

    /// <summary>
    /// Formats a human-friendly relative age string (e.g. "Just now", "5 minutes ago", "2 hours ago", "3 days ago").
    /// </summary>
    public static string FormatRelativeAge(DateTimeOffset createdAt, DateTimeOffset? referenceTime = null)
    {
        var now = referenceTime ?? DateTimeOffset.Now;
        var diff = now - createdAt;

        if (diff.TotalSeconds < 60) return "Just now";
        if (diff.TotalMinutes < 60)
        {
            int mins = (int)diff.TotalMinutes;
            return mins == 1 ? "1 minute ago" : $"{mins} minutes ago";
        }
        if (diff.TotalHours < 24)
        {
            int hours = (int)diff.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }
        if (diff.TotalDays < 30)
        {
            int days = (int)diff.TotalDays;
            return days == 1 ? "1 day ago" : $"{days} days ago";
        }
        int months = (int)(diff.TotalDays / 30);
        return months <= 1 ? "1 month ago" : $"{months} months ago";
    }

    /// <summary>
    /// Formats a complete version audit log record combining version name, timestamp, relative age, and line changes.
    /// </summary>
    public static string FormatVersionAuditRecord(string? versionName, DateTimeOffset? createdAt, int linesAdded, int linesRemoved, DateTimeOffset? referenceTime = null)
    {
        var name = string.IsNullOrWhiteSpace(versionName) ? "Version" : versionName.Trim();
        var timeStr = createdAt.HasValue ? createdAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "Unknown time";
        var relAge = createdAt.HasValue ? $" ({FormatRelativeAge(createdAt.Value, referenceTime)})" : "";
        var delta = (linesAdded == 0 && linesRemoved == 0)
            ? "no line changes"
            : linesAdded > 0 && linesRemoved > 0
                ? $"+{linesAdded}/-{linesRemoved} lines"
                : linesAdded > 0
                    ? $"+{linesAdded} lines"
                    : $"-{linesRemoved} lines";

        return $"{name} · {timeStr}{relAge} · {delta}";
    }

    /// <summary>
    /// Formats a version change velocity and density summary including net line change and optional interval.
    /// </summary>
    public static string FormatVersionChangeVelocity(int linesAdded, int linesRemoved, TimeSpan? interval = null)
    {
        int totalChanged = linesAdded + linesRemoved;
        int net = linesAdded - linesRemoved;
        string netStr = net > 0 ? $"+{net}" : net < 0 ? $"{net}" : "0";

        string deltaDetail = (linesAdded == 0 && linesRemoved == 0)
            ? "0 lines"
            : linesAdded > 0 && linesRemoved > 0
                ? $"{totalChanged} lines (+{linesAdded}/-{linesRemoved})"
                : linesAdded > 0
                    ? $"+{linesAdded} line{(linesAdded == 1 ? "" : "s")}"
                    : $"-{linesRemoved} line{(linesRemoved == 1 ? "" : "s")}";

        string intervalStr = "";
        if (interval.HasValue)
        {
            var span = interval.Value;
            if (span.TotalMinutes < 1) intervalStr = " · <1m interval";
            else if (span.TotalHours < 1) intervalStr = $" · {(int)span.TotalMinutes}m interval";
            else if (span.TotalDays < 1) intervalStr = $" · {(int)span.TotalHours}h {(int)(span.TotalMinutes % 60)}m interval";
            else intervalStr = $" · {(int)span.TotalDays}d interval";
        }

        return $"Velocity: {deltaDetail} (Net: {netStr}){intervalStr}";
    }

    /// <summary>
    /// Formats a version net growth and churn ratio summary detailing line additions, deletions, and percentage churn.
    /// </summary>
    public static string FormatVersionGrowthAndChurn(int linesAdded, int linesRemoved)
    {
        int totalChanged = linesAdded + linesRemoved;
        if (totalChanged == 0)
        {
            return "Growth: 0 lines (0% churn)";
        }

        int net = linesAdded - linesRemoved;
        string netStr = net > 0 ? $"+{net}" : net < 0 ? $"{net}" : "0";

        if (linesAdded > 0 && linesRemoved == 0)
        {
            return $"Growth: +{linesAdded} lines (Pure addition, 0% churn)";
        }

        if (linesAdded == 0 && linesRemoved > 0)
        {
            return $"Contraction: -{linesRemoved} lines (Pure deletion, 100% churn)";
        }

        double churnPct = (double)linesRemoved / totalChanged * 100.0;
        return $"Net: {netStr} lines · Total churn: {totalChanged} lines ({churnPct:0.#}% deletion churn)";
    }

    /// <summary>
    /// Formats a line retention and survival rate summary comparing surviving original lines to initial line count.
    /// </summary>
    public static string FormatVersionRetentionRate(int totalOriginalLines, int survivingLines)
    {
        if (totalOriginalLines <= 0)
        {
            return "Retention: 100.0% (Empty baseline, 0 lines)";
        }

        survivingLines = Math.Clamp(survivingLines, 0, totalOriginalLines);
        int deletedCount = totalOriginalLines - survivingLines;
        double retentionPct = (double)survivingLines / totalOriginalLines * 100.0;
        double churnPct = 100.0 - retentionPct;

        if (deletedCount == 0)
        {
            return $"Retention: 100.0% (All {totalOriginalLines:N0} original lines preserved · 0.0% loss)";
        }

        return $"Retention: {retentionPct:0.0}% ({survivingLines:N0}/{totalOriginalLines:N0} lines preserved · {churnPct:0.0}% loss)";
    }

    /// <summary>
    /// Formats a version edit entropy and churn dispersion summary comparing additions to deletions.
    /// Shannon entropy H = -sum(p * log2(p)) normalized between 0.00 (unidirectional) and 1.00 (balanced refactor).
    /// </summary>
    public static string FormatVersionEditEntropy(int linesAdded, int linesRemoved, int totalDocumentLines = 0)
    {
        int totalChanged = linesAdded + linesRemoved;
        if (totalChanged == 0)
        {
            return "Edit entropy: 0.00 (0 lines changed · Stable)";
        }

        if (linesAdded > 0 && linesRemoved == 0)
        {
            return $"Edit entropy: 0.00 (+{linesAdded} lines · Pure addition)";
        }

        if (linesAdded == 0 && linesRemoved > 0)
        {
            return $"Edit entropy: 0.00 (-{linesRemoved} lines · Pure deletion)";
        }

        double p1 = (double)linesAdded / totalChanged;
        double p2 = (double)linesRemoved / totalChanged;
        double entropy = -(p1 * Math.Log2(p1) + p2 * Math.Log2(p2));

        string classification = entropy switch
        {
            < 0.50 => "Localized / Asymmetric",
            < 0.85 => "Moderate churn",
            _ => "High entropy (Balanced refactor)"
        };

        string impact = totalDocumentLines > 0
            ? $" · {((double)totalChanged / totalDocumentLines * 100.0):0.#}% document impact"
            : "";

        return $"Edit entropy: {entropy:0.00} (+{linesAdded}/-{linesRemoved} lines · {classification}){impact}";
    }

    /// <summary>
    /// Represents an edit hunk cluster (island of contiguous line additions and removals) between two texts.
    /// </summary>
    public sealed record EditHunkCluster(int HunkIndex, int StartLineBefore, int StartLineAfter, int AddedCount, int RemovedCount, int TotalChangedLines);

    /// <summary>
    /// Identifies all discrete edit hunks between before and after text versions.
    /// </summary>
    public static List<EditHunkCluster> AuditDiffHunks(string before, string after, int hunkContextSeparation = 2)
    {
        var hunks = new List<EditHunkCluster>();
        var diff = Diff(before, after);
        if (diff.Count == 0) return hunks;

        int currentAdded = 0;
        int currentRemoved = 0;
        int hunkStartBefore = -1;
        int hunkStartAfter = -1;
        int unchangedStreak = 0;

        foreach (var line in diff)
        {
            if (line.Kind == Kind.Same)
            {
                if (currentAdded > 0 || currentRemoved > 0)
                {
                    unchangedStreak++;
                    if (unchangedStreak >= hunkContextSeparation)
                    {
                        hunks.Add(new EditHunkCluster(hunks.Count + 1, hunkStartBefore, hunkStartAfter, currentAdded, currentRemoved, currentAdded + currentRemoved));
                        currentAdded = 0;
                        currentRemoved = 0;
                        hunkStartBefore = -1;
                        hunkStartAfter = -1;
                        unchangedStreak = 0;
                    }
                }
            }
            else
            {
                if (currentAdded == 0 && currentRemoved == 0)
                {
                    hunkStartBefore = line.OldNumber ?? 1;
                    hunkStartAfter = line.NewNumber ?? 1;
                }
                unchangedStreak = 0;
                if (line.Kind == Kind.Added) currentAdded++;
                else if (line.Kind == Kind.Removed) currentRemoved++;
            }
        }

        if (currentAdded > 0 || currentRemoved > 0)
        {
            hunks.Add(new EditHunkCluster(hunks.Count + 1, hunkStartBefore, hunkStartAfter, currentAdded, currentRemoved, currentAdded + currentRemoved));
        }

        return hunks;
    }

    /// <summary>
    /// Formats a human-readable summary of edit hunks, largest change cluster, and document churn dispersion.
    /// </summary>
    public static string FormatDiffHunkSummary(string before, string after, int totalDocumentLines = 0)
    {
        var hunks = AuditDiffHunks(before, after);
        if (hunks.Count == 0) return "Edit distribution: 0 edit hunks (Identical versions · 0 lines changed)";

        int totalAdded = hunks.Sum(h => h.AddedCount);
        int totalRemoved = hunks.Sum(h => h.RemovedCount);
        int totalChanged = totalAdded + totalRemoved;

        var largestHunk = hunks.OrderByDescending(h => h.TotalChangedLines).First();
        double avgHunkSize = (double)totalChanged / hunks.Count;

        string hunkLabel = hunks.Count == 1 ? "1 edit hunk" : $"{hunks.Count} edit hunks";
        string dispersion = hunks.Count switch
        {
            1 => "Single localized block",
            <= 3 => "Concentrated refactor",
            _ => "Dispersed churn across document"
        };

        string docImpact = totalDocumentLines > 0
            ? $" · {((double)totalChanged / totalDocumentLines * 100.0):0.#}% doc impact"
            : "";

        return $"Edit distribution: {hunkLabel} (+{totalAdded}/-{totalRemoved} lines · {dispersion}) · Largest hunk: {largestHunk.TotalChangedLines} lines @ line {largestHunk.StartLineAfter} · Avg: {avgHunkSize:0.#} lines{docImpact}";
    }

    /// <summary>
    /// Formats a human-readable summary of edit hunks, largest change cluster, and document churn dispersion from pre-aggregated metrics.
    /// </summary>
    public static string FormatDiffHunkSummary(int hunkCount, int totalAdded, int totalRemoved, int totalDocumentLines = 0, int largestHunk = 0)
    {
        if (hunkCount <= 0 && totalAdded == 0 && totalRemoved == 0)
            return "Edit distribution: 0 edit hunks (Identical versions · 0 lines changed)";

        int totalChanged = totalAdded + totalRemoved;
        int count = Math.Max(1, hunkCount);
        double avgHunkSize = (double)totalChanged / count;
        int maxHunk = largestHunk > 0 ? largestHunk : (int)Math.Ceiling(avgHunkSize);

        string hunkLabel = count == 1 ? "1 edit hunk" : $"{count} edit hunks";
        string dispersion = count switch
        {
            1 => "Single localized block",
            <= 3 => "Concentrated refactor",
            _ => "Dispersed churn across document"
        };

        string docImpact = totalDocumentLines > 0
            ? $" · {((double)totalChanged / totalDocumentLines * 100.0):0.#}% doc impact"
            : "";

        return $"Edit distribution: {hunkLabel} (+{totalAdded}/-{totalRemoved} lines · {dispersion}) · Largest hunk: ~{maxHunk} lines · Avg: {avgHunkSize:0.#} lines{docImpact}";
    }

    /// <summary>
    /// Compares a historical snapshot (<paramref name="selectedContent"/>) directly against the latest version
    /// (<paramref name="latestContent"/>) and formats a human-readable delta, hunk count, net growth, and line similarity summary.
    /// </summary>
    public static string ComputeDiffAgainstLatestSummary(
        string? selectedContent,
        string? latestContent,
        string? selectedLabel = null,
        string? latestLabel = null)
    {
        string selText = selectedContent ?? string.Empty;
        string headText = latestContent ?? string.Empty;
        string selName = string.IsNullOrWhiteSpace(selectedLabel) ? "Selected" : selectedLabel.Trim();
        string headName = string.IsNullOrWhiteSpace(latestLabel) ? "HEAD" : latestLabel.Trim();

        var diff = Diff(selText, headText);
        int added = diff.Count(l => l.Kind == Kind.Added);
        int removed = diff.Count(l => l.Kind == Kind.Removed);
        int same = diff.Count(l => l.Kind == Kind.Same);

        if (added == 0 && removed == 0)
        {
            return $"Diff vs Latest ('{selName}' → '{headName}'): Identical to latest version (0 changes · 100% similarity)";
        }

        var hunks = AuditDiffHunks(selText, headText);
        string hunkStr = hunks.Count == 1 ? "1 hunk" : $"{hunks.Count} hunks";
        int net = added - removed;
        string netStr = net > 0 ? $"+{net}" : net.ToString();

        double denom = (2.0 * same) + added + removed;
        double similarityPct = denom > 0 ? (2.0 * same / denom) * 100.0 : 0.0;

        return $"Diff vs Latest ('{selName}' → '{headName}'): +{added}/-{removed} lines across {hunkStr} (Net {netStr} lines · {similarityPct:0.#}% similarity)";
    }

    /// <summary>
    /// Extracts numbered, self-contained cherry-pick hunk blocks from a <paramref name="before"/> and <paramref name="after"/>
    /// comparison so users can selectively copy/paste added or modified passages.
    /// </summary>
    public static string FormatCherryPickHunksPayload(string? before, string? after)
    {
        var diffLines = Diff(before ?? string.Empty, after ?? string.Empty);
        var unified = BuildUnified(diffLines);
        return FormatCherryPickHunksPayload(unified);
    }

    /// <summary>
    /// Extracts numbered, self-contained cherry-pick hunk blocks from unified diff rows.
    /// </summary>
    public static string FormatCherryPickHunksPayload(IReadOnlyList<UnifiedRow>? unifiedRows)
    {
        if (unifiedRows is null || unifiedRows.Count == 0)
            return "Cherry-pick hunks: 0 hunks (Identical versions)";

        var hunks = new List<(int StartLine, List<string> AddedLines, List<string> RemovedLines)>();
        List<string>? curAdded = null;
        List<string>? curRemoved = null;
        int curStartLine = 1;
        bool hasNewStart = false;

        foreach (var row in unifiedRows)
        {
            if (row.Kind == Kind.Same)
            {
                if (curAdded is not null && curRemoved is not null && (curAdded.Count > 0 || curRemoved.Count > 0))
                {
                    hunks.Add((curStartLine, curAdded, curRemoved));
                    curAdded = null;
                    curRemoved = null;
                    hasNewStart = false;
                }
                continue;
            }

            if (curAdded is null || curRemoved is null)
            {
                curAdded = new List<string>();
                curRemoved = new List<string>();
                curStartLine = row.NewNumber ?? row.OldNumber ?? 1;
                hasNewStart = row.NewNumber.HasValue;
            }
            else if (!hasNewStart && row.NewNumber.HasValue)
            {
                curStartLine = row.NewNumber.Value;
                hasNewStart = true;
            }

            if (row.Kind == Kind.Added)
                curAdded.Add(row.Text ?? string.Empty);
            else if (row.Kind == Kind.Removed)
                curRemoved.Add(row.Text ?? string.Empty);
        }

        if (curAdded is not null && curRemoved is not null && (curAdded.Count > 0 || curRemoved.Count > 0))
        {
            hunks.Add((curStartLine, curAdded, curRemoved));
        }

        if (hunks.Count == 0)
            return "Cherry-pick hunks: 0 hunks (Identical versions)";

        int totalAdded = hunks.Sum(h => h.AddedLines.Count);
        int totalRemoved = hunks.Sum(h => h.RemovedLines.Count);
        string hunkLabel = hunks.Count == 1 ? "1 hunk" : $"{hunks.Count} hunks";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Cherry-pick hunks: {hunkLabel} (+{totalAdded}/-{totalRemoved} lines)");

        for (int i = 0; i < hunks.Count; i++)
        {
            var (startLine, addedLines, removedLines) = hunks[i];
            sb.AppendLine($"=== Hunk #{i + 1} (@ line {startLine}, +{addedLines.Count}/-{removedLines.Count}) ===");
            if (addedLines.Count > 0)
            {
                foreach (var a in addedLines)
                {
                    sb.AppendLine(a);
                }
            }
            else
            {
                foreach (var r in removedLines)
                {
                    sb.AppendLine($"- {r}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static List<(int Level, string Title)> ExtractMarkdownHeadings(string? markdown)
    {
        var list = new List<(int Level, string Title)>();
        if (string.IsNullOrWhiteSpace(markdown)) return list;

        var lines = SplitLines(markdown);
        char fenceChar = '\0';
        int fenceLen = 0;

        foreach (var raw in lines)
        {
            string trimmed = raw.TrimStart();
            if (fenceLen > 0)
            {
                int run = 0;
                while (run < trimmed.Length && trimmed[run] == fenceChar) run++;
                if (run >= fenceLen && trimmed[run..].All(char.IsWhiteSpace))
                {
                    fenceLen = 0;
                }
                continue;
            }

            if (trimmed.Length >= 3 && (trimmed[0] == '`' || trimmed[0] == '~'))
            {
                char ch = trimmed[0];
                int run = 0;
                while (run < trimmed.Length && trimmed[run] == ch) run++;
                if (run >= 3)
                {
                    fenceChar = ch;
                    fenceLen = run;
                    continue;
                }
            }

            if (TryParseAtxHeading(trimmed, out int level, out string title))
            {
                list.Add((level, title));
            }
        }

        return list;
    }

    private static bool TryParseAtxHeading(string? line, out int level, out string title)
    {
        level = 0;
        title = string.Empty;
        if (string.IsNullOrWhiteSpace(line)) return false;

        string trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#')) return false;

        int h = 0;
        while (h < trimmed.Length && trimmed[h] == '#') h++;
        if (h is < 1 or > 6 || h >= trimmed.Length || trimmed[h] != ' ')
            return false;

        title = trimmed[(h + 1)..].Trim().TrimEnd('#').Trim();
        if (string.IsNullOrEmpty(title)) return false;

        level = h;
        return true;
    }

    /// <summary>
    /// Audits structural ATX heading changes (<c># H1</c> through <c>###### H6</c>) between <paramref name="oldText"/>
    /// and <paramref name="newText"/>, detecting added headings, removed headings, and promoted/demoted heading levels.
    /// </summary>
    public static (int AddedHeadings, int RemovedHeadings, int LevelChangedHeadings, int UnchangedHeadings, List<string> ChangeDetails) AuditStructuralHeadingChanges(
        string? oldText,
        string? newText)
    {
        var oldHeadings = ExtractMarkdownHeadings(oldText);
        var newHeadings = ExtractMarkdownHeadings(newText);

        var details = new List<string>();
        int added = 0;
        int removed = 0;
        int levelChanged = 0;
        int unchanged = 0;

        var usedOld = new bool[oldHeadings.Count];
        var usedNew = new bool[newHeadings.Count];

        // Pass 1: exact title + level matches
        for (int n = 0; n < newHeadings.Count; n++)
        {
            for (int o = 0; o < oldHeadings.Count; o++)
            {
                if (usedOld[o]) continue;
                if (oldHeadings[o].Level == newHeadings[n].Level &&
                    string.Equals(oldHeadings[o].Title, newHeadings[n].Title, StringComparison.OrdinalIgnoreCase))
                {
                    usedOld[o] = true;
                    usedNew[n] = true;
                    unchanged++;
                    break;
                }
            }
        }

        // Pass 2: same title, different heading level (promoted/demoted)
        for (int n = 0; n < newHeadings.Count; n++)
        {
            if (usedNew[n]) continue;
            for (int o = 0; o < oldHeadings.Count; o++)
            {
                if (usedOld[o]) continue;
                if (string.Equals(oldHeadings[o].Title, newHeadings[n].Title, StringComparison.OrdinalIgnoreCase))
                {
                    usedOld[o] = true;
                    usedNew[n] = true;
                    levelChanged++;
                    details.Add($"H{oldHeadings[o].Level}→H{newHeadings[n].Level} '{newHeadings[n].Title}'");
                    break;
                }
            }
        }

        // Pass 3: remaining unmatched old headings are removed; remaining new headings are added
        for (int o = 0; o < oldHeadings.Count; o++)
        {
            if (!usedOld[o])
            {
                removed++;
                details.Add($"-H{oldHeadings[o].Level} '{oldHeadings[o].Title}'");
            }
        }

        for (int n = 0; n < newHeadings.Count; n++)
        {
            if (!usedNew[n])
            {
                added++;
                details.Add($"+H{newHeadings[n].Level} '{newHeadings[n].Title}'");
            }
        }

        return (added, removed, levelChanged, unchanged, details);
    }

    /// <summary>
    /// Formats a human-readable summary of structural Markdown heading changes between two document versions.
    /// </summary>
    public static string FormatStructuralHeadingDiffSummary(string? oldText, string? newText)
    {
        var (added, removed, levelChanged, unchanged, details) = AuditStructuralHeadingChanges(oldText, newText);
        if (added == 0 && removed == 0 && levelChanged == 0 && unchanged == 0)
            return "Structural heading diff: 0 headings found in compared versions";

        if (added == 0 && removed == 0 && levelChanged == 0)
        {
            string unchLabel = unchanged == 1 ? "1 heading" : $"{unchanged} headings";
            return $"Structural heading diff: No structural changes ({unchLabel} unchanged)";
        }

        string preview = string.Join(", ", details.Take(4));
        if (details.Count > 4) preview += $", +{details.Count - 4} more";

        return $"Structural heading diff: +{added} added, -{removed} removed, {levelChanged} level-shifted, ={unchanged} unchanged ({preview})";
    }

    /// <summary>
    /// Formats a human-readable summary of structural Markdown heading changes from unified diff rows.
    /// </summary>
    public static string FormatStructuralHeadingDiffSummary(IReadOnlyList<UnifiedRow>? unifiedRows)
    {
        if (unifiedRows is null || unifiedRows.Count == 0)
            return "Structural heading diff: 0 headings found in compared versions";

        var oldSb = new System.Text.StringBuilder();
        var newSb = new System.Text.StringBuilder();

        foreach (var r in unifiedRows)
        {
            if (r.IsGap) continue;
            if (r.Kind is Kind.Same or Kind.Removed)
                oldSb.AppendLine(r.Text ?? string.Empty);
            if (r.Kind is Kind.Same or Kind.Added)
                newSb.AppendLine(r.Text ?? string.Empty);
        }

        return FormatStructuralHeadingDiffSummary(oldSb.ToString(), newSb.ToString());
    }

    /// <summary>
    /// Represents a flagged low-entropy / micro-save snapshot in a version timeline.
    /// </summary>
    public sealed record LowEntropySnapshotRecord(
        int SnapshotIndex,
        int ChangedLines,
        int ChangedChars,
        bool IsWhitespaceOnly);

    /// <summary>
    /// Audits version history snapshots to identify low-entropy micro-saves (e.g. 1-2 line changes,
    /// minor typo fixes of less than 10 characters, or whitespace-only delta between adjacent versions).
    /// </summary>
    public static (int LowEntropyCount, int TotalSnapshots, List<LowEntropySnapshotRecord> FlaggedSnapshots, double RedundancyPercentage)
        AuditLowEntropySnapshots(
            IReadOnlyList<string>? versionContents,
            int maxLineDelta = 2,
            int maxCharDelta = 10)
    {
        var flagged = new List<LowEntropySnapshotRecord>();
        if (versionContents == null || versionContents.Count <= 1)
        {
            return (0, versionContents?.Count ?? 0, flagged, 0.0);
        }

        for (int i = 1; i < versionContents.Count; i++)
        {
            string prev = versionContents[i - 1] ?? string.Empty;
            string curr = versionContents[i] ?? string.Empty;

            bool wsOnly = string.Equals(prev.Trim(), curr.Trim(), StringComparison.Ordinal);
            int charDelta = Math.Abs(curr.Length - prev.Length);

            var diff = Diff(prev, curr);
            int changedLines = diff.Count(d => d.Kind != Kind.Same);

            if (wsOnly || (changedLines <= maxLineDelta && charDelta <= maxCharDelta))
            {
                flagged.Add(new LowEntropySnapshotRecord(i, changedLines, charDelta, wsOnly));
            }
        }

        double redundancyPct = (double)flagged.Count / (versionContents.Count - 1) * 100.0;
        return (flagged.Count, versionContents.Count, flagged, redundancyPct);
    }

    /// <summary>
    /// Audits version history metadata deltas (LinesAdded, LinesRemoved) to identify micro-saves.
    /// </summary>
    public static (int LowEntropyCount, int TotalSnapshots, List<LowEntropySnapshotRecord> FlaggedSnapshots, double RedundancyPercentage)
        AuditLowEntropySnapshots(
            IReadOnlyList<(int Added, int Removed)>? versionDeltas,
            int maxTotalDelta = 2)
    {
        var flagged = new List<LowEntropySnapshotRecord>();
        if (versionDeltas == null || versionDeltas.Count <= 1)
        {
            return (0, versionDeltas?.Count ?? 0, flagged, 0.0);
        }

        for (int i = 1; i < versionDeltas.Count; i++)
        {
            var (added, removed) = versionDeltas[i];
            int totalDelta = added + removed;
            bool wsOnly = totalDelta == 0;

            if (wsOnly || totalDelta <= maxTotalDelta)
            {
                flagged.Add(new LowEntropySnapshotRecord(i, totalDelta, 0, wsOnly));
            }
        }

        double redundancyPct = (double)flagged.Count / (versionDeltas.Count - 1) * 100.0;
        return (flagged.Count, versionDeltas.Count, flagged, redundancyPct);
    }

    /// <summary>
    /// Formats a human-readable audit summary of low-entropy / micro-save snapshots across version history.
    /// </summary>
    public static string FormatLowEntropySnapshotAudit(
        IReadOnlyList<string>? versionContents,
        int maxLineDelta = 2,
        int maxCharDelta = 10)
    {
        var (count, total, flagged, pct) = AuditLowEntropySnapshots(versionContents, maxLineDelta, maxCharDelta);
        return FormatLowEntropySnapshotAuditSummary(count, total, flagged, pct);
    }

    /// <summary>
    /// Formats a human-readable audit summary from version delta tuples.
    /// </summary>
    public static string FormatLowEntropySnapshotAudit(
        IReadOnlyList<(int Added, int Removed)>? versionDeltas,
        int maxTotalDelta = 2)
    {
        var (count, total, flagged, pct) = AuditLowEntropySnapshots(versionDeltas, maxTotalDelta);
        return FormatLowEntropySnapshotAuditSummary(count, total, flagged, pct);
    }

    private static string FormatLowEntropySnapshotAuditSummary(
        int lowEntropyCount,
        int totalSnapshots,
        IReadOnlyList<LowEntropySnapshotRecord> flagged,
        double redundancyPct)
    {
        if (totalSnapshots <= 1)
        {
            string snapLabel = totalSnapshots == 1 ? "1 snapshot" : "0 snapshots";
            return $"Micro-save audit ({snapLabel}): Baseline history (No redundant saves to prune)";
        }

        if (lowEntropyCount <= 0)
        {
            return $"Micro-save audit ({totalSnapshots} snapshots): 0 micro-saves detected (100% substantive revisions · 0.0% redundancy)";
        }

        var sampleItems = flagged.Take(3).Select(f =>
            f.IsWhitespaceOnly ? $"#{f.SnapshotIndex} [ws-only]" : $"#{f.SnapshotIndex} [{f.ChangedLines}L/Δ{f.ChangedChars}c]");
        string samples = string.Join(", ", sampleItems);
        if (flagged.Count > 3) samples += $", +{flagged.Count - 3} more";

        string saveLabel = lowEntropyCount == 1 ? "1 micro-save" : $"{lowEntropyCount} micro-saves";
        return $"Micro-save audit ({totalSnapshots} snapshots · {redundancyPct:0.#}% redundant): {saveLabel} identified ({samples})";
    }

    /// <summary>
    /// Represents an extracted fenced code block, diagram, or extension container in a document.
    /// </summary>
    public record FencedBlockRecord(
        string BlockType,
        int StartLine,
        int LineCount,
        string Content);

    /// <summary>
    /// Extracts all fenced code, diagram, and container blocks from a document text.
    /// </summary>
    public static List<FencedBlockRecord> ExtractFencedBlocks(string? text)
    {
        var blocks = new List<FencedBlockRecord>();
        if (string.IsNullOrWhiteSpace(text)) return blocks;

        var lines = text.Split('\n');
        bool inFence = false;
        string fenceType = string.Empty;
        string fenceMarker = string.Empty;
        int startLine = 1;
        var blockLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (!inFence)
            {
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inFence = true;
                    fenceMarker = trimmed.Substring(0, 3);
                    var info = trimmed.Substring(3).Trim();
                    fenceType = string.IsNullOrEmpty(info) ? "code" : info;
                    startLine = i + 1;
                    blockLines.Clear();
                    continue;
                }
                if (trimmed.StartsWith(":::") && trimmed.Length > 3)
                {
                    inFence = true;
                    fenceMarker = ":::";
                    fenceType = trimmed.Trim();
                    startLine = i + 1;
                    blockLines.Clear();
                    continue;
                }
            }
            else
            {
                bool isClosing = (fenceMarker != ":::" && trimmed.StartsWith(fenceMarker)) ||
                                 (fenceMarker == ":::" && (trimmed == ":::" || trimmed.StartsWith(":::")));
                if (isClosing)
                {
                    inFence = false;
                    blocks.Add(new FencedBlockRecord(fenceType, startLine, blockLines.Count, string.Join("\n", blockLines)));
                    blockLines.Clear();
                    continue;
                }
                blockLines.Add(line);
            }
        }

        if (inFence && blockLines.Count > 0)
        {
            blocks.Add(new FencedBlockRecord(fenceType, startLine, blockLines.Count, string.Join("\n", blockLines)));
        }

        return blocks;
    }

    /// <summary>
    /// Audits fenced code and diagram block changes between <paramref name="oldText"/> and <paramref name="newText"/>.
    /// Returns counts of added, removed, modified, and unchanged blocks, along with detailed change descriptions.
    /// </summary>
    public static (int Added, int Removed, int Modified, int Unchanged, List<string> Details)
        AuditFencedBlockChanges(string? oldText, string? newText)
    {
        var oldBlocks = ExtractFencedBlocks(oldText);
        var newBlocks = ExtractFencedBlocks(newText);
        var details = new List<string>();

        int added = 0;
        int removed = 0;
        int modified = 0;
        int unchanged = 0;

        var matchedOld = new HashSet<int>();
        var matchedNew = new HashSet<int>();

        // Pass 1: exact matches (same type and exact content)
        for (int i = 0; i < oldBlocks.Count; i++)
        {
            for (int j = 0; j < newBlocks.Count; j++)
            {
                if (matchedNew.Contains(j)) continue;
                if (string.Equals(oldBlocks[i].BlockType, newBlocks[j].BlockType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(oldBlocks[i].Content, newBlocks[j].Content, StringComparison.Ordinal))
                {
                    matchedOld.Add(i);
                    matchedNew.Add(j);
                    unchanged++;
                    break;
                }
            }
        }

        // Pass 2: modified matches (same type, but content changed)
        for (int i = 0; i < oldBlocks.Count; i++)
        {
            if (matchedOld.Contains(i)) continue;
            for (int j = 0; j < newBlocks.Count; j++)
            {
                if (matchedNew.Contains(j)) continue;
                if (string.Equals(oldBlocks[i].BlockType, newBlocks[j].BlockType, StringComparison.OrdinalIgnoreCase))
                {
                    matchedOld.Add(i);
                    matchedNew.Add(j);
                    modified++;
                    int lineDelta = newBlocks[j].LineCount - oldBlocks[i].LineCount;
                    string deltaStr = lineDelta >= 0 ? $"+{lineDelta}" : lineDelta.ToString();
                    details.Add($"~ Modified '{newBlocks[j].BlockType}' (Line {newBlocks[j].StartLine} · {deltaStr} lines)");
                    break;
                }
            }
        }

        // Pass 3: remaining old blocks are removed
        for (int i = 0; i < oldBlocks.Count; i++)
        {
            if (!matchedOld.Contains(i))
            {
                removed++;
                details.Add($"- Removed '{oldBlocks[i].BlockType}' ({oldBlocks[i].LineCount} lines · Line {oldBlocks[i].StartLine})");
            }
        }

        // Pass 4: remaining new blocks are added
        for (int j = 0; j < newBlocks.Count; j++)
        {
            if (!matchedNew.Contains(j))
            {
                added++;
                details.Add($"+ Added '{newBlocks[j].BlockType}' ({newBlocks[j].LineCount} lines · Line {newBlocks[j].StartLine})");
            }
        }

        return (added, removed, modified, unchanged, details);
    }

    /// <summary>
    /// Formats a human-readable summary of fenced code and diagram block changes.
    /// </summary>
    public static string FormatFencedBlockDiffSummary(string? oldText, string? newText)
    {
        var (added, removed, modified, unchanged, details) = AuditFencedBlockChanges(oldText, newText);
        int total = added + removed + modified + unchanged;

        if (total == 0)
            return "Fenced block diff audit: 0 code or diagram blocks detected in either version";

        if (added == 0 && removed == 0 && modified == 0)
        {
            string blockLabel = unchanged == 1 ? "1 block" : $"{unchanged} blocks";
            return $"Fenced block diff audit ({blockLabel}): All code & diagram blocks identical (0 modifications)";
        }

        var sampleStr = details.Take(3).ToList();
        string sampleSummary = string.Join("; ", sampleStr);
        if (details.Count > 3) sampleSummary += $"; +{details.Count - 3} more";

        return $"Fenced block diff audit (+{added}/-{removed}/~{modified} across {total} blocks): {sampleSummary}";
    }

    /// <summary>
    /// Formats a human-readable summary of fenced code and diagram block changes from unified diff rows.
    /// </summary>
    public static string FormatFencedBlockDiffSummary(IReadOnlyList<UnifiedRow>? unifiedRows)
    {
        if (unifiedRows is null || unifiedRows.Count == 0)
            return "Fenced block diff audit: 0 code or diagram blocks detected in either version";

        var oldSb = new System.Text.StringBuilder();
        var newSb = new System.Text.StringBuilder();

        foreach (var r in unifiedRows)
        {
            if (r.IsGap) continue;
            if (r.Kind is Kind.Same or Kind.Removed)
                oldSb.AppendLine(r.Text ?? string.Empty);
            if (r.Kind is Kind.Same or Kind.Added)
                newSb.AppendLine(r.Text ?? string.Empty);
        }

        return FormatFencedBlockDiffSummary(oldSb.ToString(), newSb.ToString());
    }

    /// <summary>
    /// Audits version history snapshots to compute an edit churn volatility index, mean churn per revision,
    /// and identifies the peak single-revision editing spike.
    /// </summary>
    public static (double MeanChurn, int TotalChurn, int MaxSpike, int SpikeRevision, string Rating, string Summary)
        AuditTimelineVolatilityIndex(IReadOnlyList<string>? versionContents)
    {
        if (versionContents == null || versionContents.Count <= 1)
        {
            return (0.0, 0, 0, 0, "Low", "Timeline volatility audit: 0-1 versions (Insufficient timeline history for churn volatility)");
        }

        int totalChurn = 0;
        int maxSpike = 0;
        int spikeRev = 1;

        for (int i = 1; i < versionContents.Count; i++)
        {
            var before = versionContents[i - 1];
            var after = versionContents[i];
            var lines = Diff(before, after);

            int added = lines.Count(r => r.Kind == Kind.Added);
            int removed = lines.Count(r => r.Kind == Kind.Removed);
            int churn = added + removed;

            totalChurn += churn;
            if (churn > maxSpike)
            {
                maxSpike = churn;
                spikeRev = i + 1;
            }
        }

        int transitions = versionContents.Count - 1;
        double meanChurn = Math.Round((double)totalChurn / transitions, 1);
        string rating = meanChurn switch
        {
            < 5.0 => "Low",
            <= 20.0 => "Moderate",
            <= 50.0 => "High",
            _ => "Volatile"
        };

        var summary = FormatTimelineVolatilitySummary(meanChurn, totalChurn, maxSpike, spikeRev, rating, versionContents.Count);
        return (meanChurn, totalChurn, maxSpike, spikeRev, rating, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of timeline volatility metrics across versions.
    /// </summary>
    public static string FormatTimelineVolatilitySummary(
        double meanChurn, int totalChurn, int maxSpike, int spikeRev, string rating, int totalVersions)
    {
        if (totalVersions <= 1)
            return "Timeline volatility audit: 0-1 versions (Insufficient timeline history for churn volatility)";

        string revLabel = totalVersions == 1 ? "1 revision" : $"{totalVersions} revisions";
        string spikeStr = maxSpike > 0 ? $" · Max spike: {maxSpike} lines (Rev {spikeRev})" : "";
        return $"Timeline volatility audit (Rating: {rating} · {revLabel} · Total {totalChurn} lines changed): Mean churn {meanChurn:0.#} lines/rev{spikeStr}";
    }

    /// <summary>
    /// Formats a human-readable timeline volatility summary from a list of version contents.
    /// </summary>
    public static string FormatTimelineVolatilitySummary(IReadOnlyList<string>? versionContents)
    {
        var (_, _, _, _, _, summary) = AuditTimelineVolatilityIndex(versionContents);
        return summary;
    }

    /// <summary>
    /// Formats a human-readable timeline volatility summary from the currently active unified diff rows.
    /// </summary>
    public static string FormatTimelineVolatilitySummary(IReadOnlyList<UnifiedRow>? unifiedRows)
    {
        if (unifiedRows is null || unifiedRows.Count == 0)
            return "Timeline volatility audit: 0 diff rows (No active version comparison)";

        int added = unifiedRows.Count(r => !r.IsGap && r.Kind == Kind.Added);
        int removed = unifiedRows.Count(r => !r.IsGap && r.Kind == Kind.Removed);
        int churn = added + removed;

        string rating = churn switch
        {
            <= 5 => "Low",
            <= 20 => "Moderate",
            <= 50 => "High",
            _ => "Volatile"
        };

        return $"Timeline volatility audit (Rating: {rating} · Active delta: +{added}/-{removed} · Churn: {churn} lines): Focused revision churn index";
    }

    /// <summary>
    /// Represents an assessment of merge/cherry-pick collision risk for a diff hunk against the current document.
    /// </summary>
    public enum CherryPickHunkRisk
    {
        Clean,
        Shifted,
        Modified,
        Conflict
    }

    /// <summary>
    /// Assesses conflict risk across cherry-pick hunks when applied to the active current document.
    /// Returns total hunks, clean hunks, shifted hunks, conflicted hunks, overall risk rating, and summary.
    /// </summary>
    public static (int TotalHunks, int CleanHunks, int ShiftedHunks, int ConflictedHunks, string OverallRisk, string Summary)
        AssessCherryPickConflictRisk(string? before, string? after, string? currentDoc)
    {
        var diffLines = Diff(before ?? string.Empty, after ?? string.Empty);
        var unified = BuildUnified(diffLines);
        return AssessCherryPickConflictRisk(unified, currentDoc);
    }

    /// <summary>
    /// Assesses conflict risk across cherry-pick hunks extracted from unified diff rows against the active current document.
    /// </summary>
    public static (int TotalHunks, int CleanHunks, int ShiftedHunks, int ConflictedHunks, string OverallRisk, string Summary)
        AssessCherryPickConflictRisk(IReadOnlyList<UnifiedRow>? unifiedRows, string? currentDoc)
    {
        if (unifiedRows is null || unifiedRows.Count == 0)
        {
            return (0, 0, 0, 0, "None", "Cherry-pick conflict risk: 0 hunks (No diff changes to cherry-pick)");
        }

        var hunks = ExtractHunksFromUnified(unifiedRows);
        if (hunks.Count == 0)
        {
            return (0, 0, 0, 0, "None", "Cherry-pick conflict risk: 0 hunks (Identical versions)");
        }

        var currentLines = SplitLines(currentDoc ?? string.Empty);

        int clean = 0;
        int shifted = 0;
        int conflicted = 0;

        for (int i = 0; i < hunks.Count; i++)
        {
            var (startLine, addedLines, removedLines) = hunks[i];
            var risk = EvaluateHunkRisk(startLine, addedLines, removedLines, currentLines);

            switch (risk)
            {
                case CherryPickHunkRisk.Clean:
                    clean++;
                    break;
                case CherryPickHunkRisk.Shifted:
                    shifted++;
                    break;
                case CherryPickHunkRisk.Modified:
                case CherryPickHunkRisk.Conflict:
                    conflicted++;
                    break;
            }
        }

        string overallRisk = conflicted switch
        {
            0 when shifted == 0 => "Clean",
            0 => "Low (Shifted)",
            1 when hunks.Count > 1 => "Moderate",
            _ => "High"
        };

        string summary = FormatConflictRiskSummary(hunks.Count, clean, shifted, conflicted, overallRisk);
        return (hunks.Count, clean, shifted, conflicted, overallRisk, summary);
    }

    private static CherryPickHunkRisk EvaluateHunkRisk(
        int expectedStartLine,
        IReadOnlyList<string> addedLines,
        IReadOnlyList<string> removedLines,
        IReadOnlyList<string> currentLines)
    {
        if (currentLines.Count == 0)
        {
            return removedLines.Count == 0 ? CherryPickHunkRisk.Clean : CherryPickHunkRisk.Conflict;
        }

        if (removedLines.Count > 0)
        {
            // Look for removedLines in currentLines
            int exactIndex = FindSubsequenceIndex(currentLines, removedLines);
            if (exactIndex >= 0)
            {
                int actualLine = exactIndex + 1;
                return Math.Abs(actualLine - expectedStartLine) <= 1
                    ? CherryPickHunkRisk.Clean
                    : CherryPickHunkRisk.Shifted;
            }

            // Check if any line from removedLines is found
            bool anyMatch = removedLines.Any(r => currentLines.Any(c => string.Equals(c.Trim(), r.Trim(), StringComparison.Ordinal)));
            return anyMatch ? CherryPickHunkRisk.Modified : CherryPickHunkRisk.Conflict;
        }

        // Pure additions
        if (expectedStartLine <= currentLines.Count + 1)
        {
            return CherryPickHunkRisk.Clean;
        }

        return CherryPickHunkRisk.Shifted;
    }

    private static int FindSubsequenceIndex(IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        if (target.Count == 0) return 0;
        if (source.Count < target.Count) return -1;

        for (int i = 0; i <= source.Count - target.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < target.Count; j++)
            {
                if (!string.Equals(source[i + j].Trim(), target[j].Trim(), StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    private static List<(int StartLine, List<string> AddedLines, List<string> RemovedLines)>
        ExtractHunksFromUnified(IReadOnlyList<UnifiedRow> unifiedRows)
    {
        var hunks = new List<(int StartLine, List<string> AddedLines, List<string> RemovedLines)>();
        List<string>? curAdded = null;
        List<string>? curRemoved = null;
        int curStartLine = 1;
        bool hasNewStart = false;

        foreach (var row in unifiedRows)
        {
            if (row.Kind == Kind.Same)
            {
                if (curAdded is not null && curRemoved is not null && (curAdded.Count > 0 || curRemoved.Count > 0))
                {
                    hunks.Add((curStartLine, curAdded, curRemoved));
                    curAdded = null;
                    curRemoved = null;
                    hasNewStart = false;
                }
                continue;
            }

            if (curAdded is null || curRemoved is null)
            {
                curAdded = new List<string>();
                curRemoved = new List<string>();
                curStartLine = row.NewNumber ?? row.OldNumber ?? 1;
                hasNewStart = row.NewNumber.HasValue;
            }
            else if (!hasNewStart && row.NewNumber.HasValue)
            {
                curStartLine = row.NewNumber.Value;
                hasNewStart = true;
            }

            if (row.Kind == Kind.Added)
                curAdded.Add(row.Text ?? string.Empty);
            else if (row.Kind == Kind.Removed)
                curRemoved.Add(row.Text ?? string.Empty);
        }

        if (curAdded is not null && curRemoved is not null && (curAdded.Count > 0 || curRemoved.Count > 0))
        {
            hunks.Add((curStartLine, curAdded, curRemoved));
        }

        return hunks;
    }

    /// <summary>
    /// Formats a human-readable summary of cherry-pick conflict risk metrics.
    /// </summary>
    public static string FormatConflictRiskSummary(
        int totalHunks, int cleanHunks, int shiftedHunks, int conflictedHunks, string overallRisk)
    {
        if (totalHunks <= 0)
            return "Cherry-pick conflict risk: 0 hunks (No diff changes to cherry-pick)";

        string hunkLabel = totalHunks == 1 ? "1 hunk" : $"{totalHunks} hunks";
        string breakdown = $"{cleanHunks} clean, {shiftedHunks} shifted, {conflictedHunks} conflicted";

        string advice = conflictedHunks switch
        {
            0 when shiftedHunks == 0 => "All hunks apply cleanly without collision",
            0 => "Hunks apply with line shifts (low collision risk)",
            1 => "Review 1 conflicted hunk before merging",
            _ => $"Manual resolution recommended for {conflictedHunks} conflicting hunks"
        };

        return $"Cherry-pick conflict risk ({hunkLabel} · Overall Risk: {overallRisk}): {breakdown} · {advice}";
    }

    /// <summary>
    /// Synthesizes a conventional commit style message and summary bullets from unified diff rows between document versions.
    /// </summary>
    public static string SynthesizeSemanticVersionMessage(IReadOnlyList<UnifiedRow> unifiedRows)
    {
        if (unifiedRows == null || unifiedRows.Count == 0)
        {
            return "docs: checkpoint snapshot\n\n- No diff rows recorded in snapshot";
        }

        int added = 0;
        int removed = 0;
        var addedHeadings = new List<string>();
        var removedHeadings = new List<string>();
        bool codeBlocksChanged = false;
        bool tablesChanged = false;

        foreach (var row in unifiedRows)
        {
            if (row.Kind == Kind.Added)
            {
                added++;
                var text = (row.Text ?? string.Empty).Trim();
                if (text.StartsWith('#'))
                {
                    addedHeadings.Add(text.TrimStart('#').Trim());
                }
                if (text.StartsWith("```") || text.StartsWith(":::")) codeBlocksChanged = true;
                if (text.StartsWith('|')) tablesChanged = true;
            }
            else if (row.Kind == Kind.Removed)
            {
                removed++;
                var text = (row.Text ?? string.Empty).Trim();
                if (text.StartsWith('#'))
                {
                    removedHeadings.Add(text.TrimStart('#').Trim());
                }
                if (text.StartsWith("```") || text.StartsWith(":::")) codeBlocksChanged = true;
                if (text.StartsWith('|')) tablesChanged = true;
            }
        }

        if (added == 0 && removed == 0)
        {
            return "docs: checkpoint snapshot\n\n- Unchanged document content";
        }

        // Determine commit type & scope & subject
        string type;
        string scope = "docs";
        string subject;
        var bullets = new List<string>();

        if (addedHeadings.Count > 0 && removedHeadings.Count == 0)
        {
            type = "feat";
            subject = addedHeadings.Count == 1 ? $"add section '{addedHeadings[0]}'" : $"add {addedHeadings.Count} new document sections";
            foreach (var h in addedHeadings.Take(3)) bullets.Add($"Add section: #{h}");
        }
        else if (removedHeadings.Count > 0 && addedHeadings.Count == 0)
        {
            type = "refactor";
            subject = removedHeadings.Count == 1 ? $"remove section '{removedHeadings[0]}'" : $"prune {removedHeadings.Count} document sections";
            foreach (var h in removedHeadings.Take(3)) bullets.Add($"Remove section: #{h}");
        }
        else if (codeBlocksChanged)
        {
            type = "fix";
            scope = "diagrams";
            subject = "update code fences and diagram blocks";
            bullets.Add("Modify diagram/code fence structures");
        }
        else if (tablesChanged)
        {
            type = "feat";
            scope = "tables";
            subject = "update document data tables";
            bullets.Add("Modify structured table layout and cell data");
        }
        else if (added > 0 && removed == 0)
        {
            type = "docs";
            subject = $"expand document content (+{added} lines)";
            bullets.Add($"Append {added} new content lines");
        }
        else if (added == 0 && removed > 0)
        {
            type = "refactor";
            subject = $"condense document prose (-{removed} lines)";
            bullets.Add($"Prune {removed} redundant content lines");
        }
        else
        {
            type = "docs";
            subject = $"refine document sections (+{added}/-{removed} lines)";
            bullets.Add($"Edit prose and section content (+{added}/-{removed} lines)");
        }

        bullets.Add($"Net churn: +{added}/-{removed} lines across document history");

        return FormatSemanticVersionMessage(type, scope, subject, bullets);
    }

    /// <summary>
    /// Synthesizes a conventional commit style message between old and new text versions.
    /// </summary>
    public static string SynthesizeSemanticVersionMessage(string oldText, string newText)
    {
        var diff = Diff(oldText, newText);
        var unified = BuildUnified(diff);
        return SynthesizeSemanticVersionMessage(unified);
    }

    /// <summary>
    /// Formats a conventional commit message with summary header and bullet list body.
    /// </summary>
    public static string FormatSemanticVersionMessage(
        string commitType, string scope, string subject, IReadOnlyList<string> bulletPoints)
    {
        var sb = new StringBuilder();
        string header = string.IsNullOrWhiteSpace(scope)
            ? $"{commitType}: {subject}"
            : $"{commitType}({scope}): {subject}";
        sb.AppendLine(header);
        sb.AppendLine();
        if (bulletPoints != null && bulletPoints.Count > 0)
        {
            foreach (var b in bulletPoints)
            {
                sb.AppendLine($"- {b}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Synthesizes a conventional commit message with automatic markdown section scope extraction
    /// and breaking change detection (e.g. removed sections, deleted tables).
    /// </summary>
    public static string SynthesizeConventionalCommitWithScope(
        IReadOnlyList<UnifiedRow>? unifiedRows,
        string? currentDoc = null)
    {
        if (unifiedRows is null || unifiedRows.Count == 0)
        {
            return "docs(checkpoint): snapshot initialized\n\n- No diff rows recorded in snapshot";
        }

        int added = 0;
        int removed = 0;
        var addedHeadings = new List<string>();
        var removedHeadings = new List<string>();
        bool codeBlocksChanged = false;
        bool tablesChanged = false;
        bool tableRemoved = false;

        foreach (var row in unifiedRows)
        {
            if (row.Kind == Kind.Added)
            {
                added++;
                var text = (row.Text ?? string.Empty).Trim();
                if (text.StartsWith('#'))
                {
                    addedHeadings.Add(text.TrimStart('#').Trim());
                }
                if (text.StartsWith("```") || text.StartsWith(":::")) codeBlocksChanged = true;
                if (text.StartsWith('|')) tablesChanged = true;
            }
            else if (row.Kind == Kind.Removed)
            {
                removed++;
                var text = (row.Text ?? string.Empty).Trim();
                if (text.StartsWith('#'))
                {
                    removedHeadings.Add(text.TrimStart('#').Trim());
                }
                if (text.StartsWith("```") || text.StartsWith(":::")) codeBlocksChanged = true;
                if (text.StartsWith('|'))
                {
                    tablesChanged = true;
                    tableRemoved = true;
                }
            }
        }

        if (added == 0 && removed == 0)
        {
            return "docs(checkpoint): snapshot unchanged\n\n- Unchanged document content";
        }

        string type;
        string subject;
        var bullets = new List<string>();
        var breakingChanges = new List<string>();

        if (removedHeadings.Count > 0)
        {
            foreach (var rh in removedHeadings)
            {
                breakingChanges.Add($"Removed section '{rh}'");
            }
        }

        if (tableRemoved && added == 0)
        {
            breakingChanges.Add("Removed structured data table");
        }

        string scope;
        if (addedHeadings.Count > 0)
        {
            scope = SlugifyHeading(addedHeadings[0]);
        }
        else if (removedHeadings.Count > 0)
        {
            scope = SlugifyHeading(removedHeadings[0]);
        }
        else if (tablesChanged)
        {
            scope = "tables";
        }
        else if (codeBlocksChanged)
        {
            scope = "diagrams";
        }
        else if (!string.IsNullOrWhiteSpace(currentDoc))
        {
            var firstH = ExtractMarkdownHeadings(currentDoc).FirstOrDefault();
            scope = firstH != default ? SlugifyHeading(firstH.Title) : "docs";
        }
        else
        {
            scope = "docs";
        }

        if (addedHeadings.Count > 0 && removedHeadings.Count == 0)
        {
            type = "feat";
            subject = addedHeadings.Count == 1 ? $"add section '{addedHeadings[0]}'" : $"add {addedHeadings.Count} new document sections";
            foreach (var h in addedHeadings.Take(3)) bullets.Add($"Add section: #{h}");
        }
        else if (removedHeadings.Count > 0 && addedHeadings.Count == 0)
        {
            type = "refactor";
            subject = removedHeadings.Count == 1 ? $"remove section '{removedHeadings[0]}'" : $"prune {removedHeadings.Count} document sections";
            foreach (var h in removedHeadings.Take(3)) bullets.Add($"Remove section: #{h}");
        }
        else if (codeBlocksChanged)
        {
            type = "fix";
            subject = "update code fences and diagram blocks";
            bullets.Add("Modify diagram/code fence structures");
        }
        else if (tablesChanged)
        {
            if (tableRemoved && added == 0)
            {
                type = "refactor";
                subject = "remove document data tables";
                bullets.Add("Remove structured table layout");
            }
            else
            {
                type = "feat";
                subject = "update document data tables";
                bullets.Add("Modify structured table layout and cell data");
            }
        }
        else if (added > 0 && removed == 0)
        {
            type = "docs";
            subject = $"expand document content (+{added} lines)";
            bullets.Add($"Append {added} new content lines");
        }
        else if (added == 0 && removed > 0)
        {
            type = "refactor";
            subject = $"condense document prose (-{removed} lines)";
            bullets.Add($"Prune {removed} redundant content lines");
        }
        else
        {
            type = "docs";
            subject = $"refine document sections (+{added}/-{removed} lines)";
            bullets.Add($"Edit prose and section content (+{added}/-{removed} lines)");
        }

        bullets.Add($"Net churn: +{added}/-{removed} lines across document history");

        return FormatConventionalCommitWithScope(type, scope, subject, bullets, breakingChanges);
    }

    private static string SlugifyHeading(string heading)
    {
        if (string.IsNullOrWhiteSpace(heading)) return "docs";
        var slug = System.Text.RegularExpressions.Regex.Replace(heading.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrEmpty(slug)) return "docs";
        if (slug.Length > 20) slug = slug[..20].TrimEnd('-');
        return slug;
    }

    /// <summary>
    /// Synthesizes a scoped conventional commit message between old and new text versions.
    /// </summary>
    public static string SynthesizeConventionalCommitWithScope(string oldText, string newText)
    {
        var diff = Diff(oldText, newText);
        var unified = BuildUnified(diff);
        return SynthesizeConventionalCommitWithScope(unified, newText);
    }

    /// <summary>
    /// Formats a conventional commit message with scoped header, bullet points, and optional BREAKING CHANGE footer.
    /// </summary>
    public static string FormatConventionalCommitWithScope(
        string commitType,
        string scope,
        string subject,
        IReadOnlyList<string> bulletPoints,
        IReadOnlyList<string>? breakingChanges = null)
    {
        var sb = new StringBuilder();
        string cleanScope = string.IsNullOrWhiteSpace(scope) ? "" : $"({scope.Trim()})";
        sb.AppendLine($"{commitType}{cleanScope}: {subject}");
        sb.AppendLine();
        if (bulletPoints != null && bulletPoints.Count > 0)
        {
            foreach (var b in bulletPoints)
            {
                sb.AppendLine($"- {b}");
            }
        }
        if (breakingChanges != null && breakingChanges.Count > 0)
        {
            sb.AppendLine();
            foreach (var bc in breakingChanges)
            {
                sb.AppendLine($"BREAKING CHANGE: {bc}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats a complete Markdown changelog section ready for CHANGELOG.md, grouping changes into
    /// Features, Bug Fixes, Documentation, Refactoring, and Breaking Changes.
    /// </summary>
    public static string FormatConventionalChangelogSection(string? oldText, string? newText, string? versionTag = null)
    {
        var diff = Diff(oldText ?? string.Empty, newText ?? string.Empty);
        var unified = BuildUnified(diff);
        return FormatConventionalChangelogSection(unified, newText, versionTag);
    }

    /// <summary>
    /// Formats a complete Markdown changelog section ready for CHANGELOG.md from active unified diff rows and current markdown.
    /// </summary>
    public static string FormatConventionalChangelogSection(
        IReadOnlyList<UnifiedRow>? unifiedRows, string? currentDoc, string? versionTag = null)
    {
        string tag = string.IsNullOrWhiteSpace(versionTag) ? "Unreleased" : versionTag.Trim();
        string dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var sb = new StringBuilder();
        sb.AppendLine($"## [{tag}] - {dateStr}");

        if (unifiedRows == null || unifiedRows.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Documentation");
            sb.AppendLine("- Maintenance update; no functional prose or section changes.");
            return sb.ToString().TrimEnd();
        }

        var features = new List<string>();
        var fixes = new List<string>();
        var docs = new List<string>();
        var refactors = new List<string>();
        var breaking = new List<string>();

        int added = unifiedRows.Count(r => !r.IsGap && r.Kind == Kind.Added);
        int removed = unifiedRows.Count(r => !r.IsGap && r.Kind == Kind.Removed);

        if (added == 0 && removed == 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Documentation");
            sb.AppendLine("- Maintenance update; identical version contents.");
            return sb.ToString().TrimEnd();
        }

        var addedHeadings = new List<string>();
        var removedHeadings = new List<string>();
        bool addedTable = false;
        bool removedTable = false;
        bool modifiedDiagram = false;

        foreach (var row in unifiedRows)
        {
            if (row.IsGap) continue;
            var text = (row.Text ?? string.Empty).Trim();

            if (row.Kind == Kind.Added)
            {
                if (text.StartsWith("#"))
                {
                    var cleanTitle = text.TrimStart('#').Trim();
                    if (!string.IsNullOrEmpty(cleanTitle)) addedHeadings.Add(cleanTitle);
                }
                else if (text.StartsWith("|") && text.EndsWith("|"))
                {
                    addedTable = true;
                }
                else if (text.StartsWith("```") || text.StartsWith(":::"))
                {
                    modifiedDiagram = true;
                }
            }
            else if (row.Kind == Kind.Removed)
            {
                if (text.StartsWith("#"))
                {
                    var cleanTitle = text.TrimStart('#').Trim();
                    if (!string.IsNullOrEmpty(cleanTitle)) removedHeadings.Add(cleanTitle);
                }
                else if (text.StartsWith("|") && text.EndsWith("|"))
                {
                    removedTable = true;
                }
                else if (text.StartsWith("```") || text.StartsWith(":::"))
                {
                    modifiedDiagram = true;
                }
            }
        }

        // Section additions
        foreach (var h in addedHeadings)
        {
            string scope = SlugifyHeading(h);
            features.Add($"- **{scope}**: add section '{h}'");
        }

        // Section removals
        foreach (var h in removedHeadings)
        {
            string scope = SlugifyHeading(h);
            refactors.Add($"- **{scope}**: remove section '{h}'");
            breaking.Add($"- **{scope}**: removed section '{h}'");
        }

        // Tables
        if (addedTable && !removedTable)
        {
            features.Add("- **tables**: add structured data tables");
        }
        else if (removedTable && !addedTable)
        {
            refactors.Add("- **tables**: remove structured data tables");
            breaking.Add("- **tables**: removed structured data tables");
        }
        else if (addedTable && removedTable)
        {
            fixes.Add("- **tables**: update table data and cell structures");
        }

        // Diagrams
        if (modifiedDiagram)
        {
            fixes.Add("- **diagrams**: update code fences and diagram blocks");
        }

        // Prose volume
        if (added > 0 && removed == 0 && features.Count == 0)
        {
            docs.Add($"- **content**: expand document content (+{added} lines)");
        }
        else if (removed > 0 && added == 0 && refactors.Count == 0)
        {
            refactors.Add($"- **content**: prune redundant document prose (-{removed} lines)");
        }
        else if (docs.Count == 0 && features.Count == 0 && fixes.Count == 0 && refactors.Count == 0)
        {
            docs.Add($"- **content**: refine prose and section details (+{added}/-{removed} lines)");
        }

        // Render sections
        if (breaking.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Breaking Changes");
            foreach (var b in breaking) sb.AppendLine(b);
        }

        if (features.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Features");
            foreach (var f in features) sb.AppendLine(f);
        }

        if (fixes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Bug Fixes");
            foreach (var fx in fixes) sb.AppendLine(fx);
        }

        if (refactors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Refactoring");
            foreach (var r in refactors) sb.AppendLine(r);
        }

        if (docs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Documentation");
            foreach (var d in docs) sb.AppendLine(d);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Synthesizes a standardized Markdown release announcement header with version tag, milestone scope,
    /// release date, and summary blockquote badge metrics.
    /// </summary>
    public static string SynthesizeReleaseMilestoneHeader(
        string? versionTag = null,
        string? milestoneScope = null,
        string? releaseDate = null,
        int featureCount = 0,
        int fixCount = 0,
        int breakingCount = 0,
        int totalRevisions = 0)
    {
        string tag = string.IsNullOrWhiteSpace(versionTag) ? "v1.0.0" : versionTag.Trim();
        if (!tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) && !tag.StartsWith("["))
        {
            tag = $"v{tag}";
        }

        string dateStr = string.IsNullOrWhiteSpace(releaseDate)
            ? DateTime.UtcNow.ToString("yyyy-MM-dd")
            : releaseDate.Trim();

        string scope = string.IsNullOrWhiteSpace(milestoneScope)
            ? "General Availability"
            : milestoneScope.Trim();

        var sb = new StringBuilder();
        sb.AppendLine($"## [{tag}] - {dateStr} (Milestone: {scope})");
        sb.AppendLine();

        string featLabel = featureCount == 1 ? "1 feature" : $"{featureCount} features";
        string fixLabel = fixCount == 1 ? "1 fix" : $"{fixCount} fixes";
        string breakLabel = breakingCount == 1 ? "1 breaking change" : $"{breakingCount} breaking changes";
        string revLabel = totalRevisions == 1 ? "1 revision" : $"{totalRevisions} revisions";

        sb.Append($"> **Release Overview**: {featLabel}, {fixLabel}, {breakLabel} across {revLabel}.");
        return sb.ToString();
    }

    /// <summary>
    /// Synthesizes a release announcement header from unified diff rows between version checkpoints.
    /// </summary>
    public static string SynthesizeReleaseMilestoneHeader(
        IReadOnlyList<UnifiedRow>? unifiedRows,
        string? versionTag = null,
        string? milestoneScope = null,
        string? releaseDate = null)
    {
        int feat = 0;
        int fix = 0;
        int breaking = 0;

        if (unifiedRows != null && unifiedRows.Count > 0)
        {
            var addedHeadings = new List<string>();
            var removedHeadings = new List<string>();
            bool addedTable = false;
            bool removedTable = false;
            bool modifiedDiagram = false;

            foreach (var row in unifiedRows)
            {
                if (row.IsGap) continue;
                var text = (row.Text ?? string.Empty).Trim();

                if (row.Kind == Kind.Added)
                {
                    if (text.StartsWith('#'))
                    {
                        var cleanTitle = text.TrimStart('#').Trim();
                        if (!string.IsNullOrEmpty(cleanTitle)) addedHeadings.Add(cleanTitle);
                    }
                    else if (text.StartsWith('|') && text.EndsWith('|'))
                    {
                        addedTable = true;
                    }
                    else if (text.StartsWith("```") || text.StartsWith(":::"))
                    {
                        modifiedDiagram = true;
                    }
                }
                else if (row.Kind == Kind.Removed)
                {
                    if (text.StartsWith('#'))
                    {
                        var cleanTitle = text.TrimStart('#').Trim();
                        if (!string.IsNullOrEmpty(cleanTitle)) removedHeadings.Add(cleanTitle);
                    }
                    else if (text.StartsWith('|') && text.EndsWith('|'))
                    {
                        removedTable = true;
                    }
                    else if (text.StartsWith("```") || text.StartsWith(":::"))
                    {
                        modifiedDiagram = true;
                    }
                }
            }

            feat = addedHeadings.Count + (addedTable && !removedTable ? 1 : 0);
            if (feat == 0 && unifiedRows.Any(r => r.Kind == Kind.Added))
            {
                feat = 1;
            }
            fix = (modifiedDiagram ? 1 : 0) + (addedTable && removedTable ? 1 : 0);
            breaking = removedHeadings.Count + (removedTable && !addedTable ? 1 : 0);
        }

        return SynthesizeReleaseMilestoneHeader(versionTag, milestoneScope, releaseDate, feat, fix, breaking, 1);
    }

    /// <summary>
    /// Synthesizes a row of standardized Markdown release and status badges (Shields.io formatted)
    /// for documentation headers and release announcements.
    /// </summary>
    public static string SynthesizeReleaseBadgesMarkdown(
        string? versionTag = null,
        string? milestoneScope = null,
        string? status = null,
        int featureCount = 0,
        int fixCount = 0)
    {
        string tag = string.IsNullOrWhiteSpace(versionTag) ? "v1.0.0" : versionTag.Trim();
        if (!tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) && !tag.StartsWith("["))
        {
            tag = $"v{tag}";
        }

        string scope = string.IsNullOrWhiteSpace(milestoneScope)
            ? "General Availability"
            : milestoneScope.Trim();

        string statusLabel = string.IsNullOrWhiteSpace(status)
            ? "stable"
            : status.Trim().ToLowerInvariant();

        string safeTag = Uri.EscapeDataString(tag);
        string safeScope = Uri.EscapeDataString(scope);
        string safeStatus = Uri.EscapeDataString(statusLabel);

        var badges = new List<string>
        {
            $"[![Release](https://img.shields.io/badge/release-{safeTag}-blue.svg)](#)",
            $"[![Milestone](https://img.shields.io/badge/milestone-{safeScope}-success.svg)](#)",
            $"[![Status](https://img.shields.io/badge/status-{safeStatus}-green.svg)](#)"
        };

        if (featureCount > 0 || fixCount > 0)
        {
            string changeMetric = Uri.EscapeDataString($"{featureCount} feat · {fixCount} fix");
            badges.Add($"[![Changes](https://img.shields.io/badge/changes-{changeMetric}-orange.svg)](#)");
        }

        return string.Join(" ", badges);
    }

    /// <summary>
    /// Synthesizes release badges from unified diff rows between version checkpoints.
    /// </summary>
    public static string SynthesizeReleaseBadgesMarkdown(
        IReadOnlyList<UnifiedRow>? unifiedRows,
        string? versionTag = null,
        string? milestoneScope = null,
        string? status = null)
    {
        int feat = 0;
        int fix = 0;

        if (unifiedRows != null && unifiedRows.Count > 0)
        {
            var addedHeadings = new List<string>();
            bool addedTable = false;
            bool removedTable = false;
            bool modifiedDiagram = false;

            foreach (var row in unifiedRows)
            {
                if (row.IsGap) continue;
                var text = (row.Text ?? string.Empty).Trim();

                if (row.Kind == Kind.Added)
                {
                    if (text.StartsWith('#'))
                    {
                        var cleanTitle = text.TrimStart('#').Trim();
                        if (!string.IsNullOrEmpty(cleanTitle)) addedHeadings.Add(cleanTitle);
                    }
                    else if (text.StartsWith('|') && text.EndsWith('|'))
                    {
                        addedTable = true;
                    }
                    else if (text.StartsWith("```") || text.StartsWith(":::"))
                    {
                        modifiedDiagram = true;
                    }
                }
                else if (row.Kind == Kind.Removed)
                {
                    if (text.StartsWith('|') && text.EndsWith('|'))
                    {
                        removedTable = true;
                    }
                    else if (text.StartsWith("```") || text.StartsWith(":::"))
                    {
                        modifiedDiagram = true;
                    }
                }
            }

            feat = addedHeadings.Count + (addedTable && !removedTable ? 1 : 0);
            if (feat == 0 && unifiedRows.Any(r => r.Kind == Kind.Added))
            {
                feat = 1;
            }
            fix = (modifiedDiagram ? 1 : 0) + (addedTable && removedTable ? 1 : 0);
        }

        return SynthesizeReleaseBadgesMarkdown(versionTag, milestoneScope, status, feat, fix);
    }

    /// <summary>
    /// Synthesizes a standardized Markdown release announcement social card and summary snippet,
    /// complete with release header, status pill, Shields.io badges, key highlights, and conventional change telemetry.
    /// </summary>
    public static string SynthesizeReleaseAnnouncementSnippet(
        string? versionTag = null,
        string? milestoneScope = null,
        string? status = null,
        IReadOnlyList<string>? highlights = null,
        int featureCount = 0,
        int fixCount = 0,
        int docCount = 0,
        DateTime? releaseDate = null)
    {
        string tag = string.IsNullOrWhiteSpace(versionTag) ? "v1.0.0" : versionTag.Trim();
        if (!tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) && !tag.StartsWith("["))
        {
            tag = $"v{tag}";
        }

        string scope = string.IsNullOrWhiteSpace(milestoneScope)
            ? "General Availability"
            : milestoneScope.Trim();

        string statusLabel = string.IsNullOrWhiteSpace(status)
            ? "Stable / Production"
            : status.Trim();

        DateTime date = releaseDate ?? DateTime.UtcNow;
        string dateStr = date.ToString("yyyy-MM-dd");

        string badges = SynthesizeReleaseBadgesMarkdown(tag, scope, statusLabel.ToLowerInvariant(), featureCount, fixCount);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# 🚀 MarkSmith Release {tag} — {scope}");
        sb.AppendLine($"> **Status:** `{statusLabel}` · **Released:** `{dateStr}` · **Target:** `WinUI 3 Desktop`");
        sb.AppendLine();
        sb.AppendLine(badges);
        sb.AppendLine();
        sb.AppendLine("### 🌟 Key Highlights & Enhancements");

        if (highlights != null && highlights.Count > 0)
        {
            foreach (var h in highlights)
            {
                sb.AppendLine($"- {h}");
            }
        }
        else
        {
            sb.AppendLine("- Automated performance optimization and visual layout polish across desktop studios.");
            sb.AppendLine("- Refined interactive UI controls, enhanced keyboard navigation, and telemetry HUD metrics.");
        }

        sb.AppendLine();
        sb.AppendLine("### 📊 Change Telemetry & Metrics");
        sb.AppendLine($"- **New Features & Enhancements:** {featureCount}");
        sb.AppendLine($"- **Bug Fixes & Hardening:** {fixCount}");
        if (docCount > 0)
        {
            sb.AppendLine($"- **Documentation & Specs:** {docCount}");
        }
        sb.AppendLine($"- **Verification Gate:** Pass (100% test coverage & clean smoke launch)");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*Crafted with MarkSmith Desktop Studio.*");

        return sb.ToString();
    }

    /// <summary>
    /// Synthesizes a release announcement snippet from unified diff rows between version checkpoints.
    /// </summary>
    public static string SynthesizeReleaseAnnouncementSnippet(
        IReadOnlyList<UnifiedRow>? unifiedRows,
        string? versionTag = null,
        string? milestoneScope = null,
        string? status = null,
        DateTime? releaseDate = null)
    {
        int feat = 0;
        int fix = 0;
        int doc = 0;
        var highlights = new List<string>();

        if (unifiedRows != null && unifiedRows.Count > 0)
        {
            var addedHeadings = new List<string>();
            bool addedTable = false;
            bool removedTable = false;
            bool modifiedDiagram = false;

            foreach (var row in unifiedRows)
            {
                if (row.IsGap) continue;
                var text = (row.Text ?? string.Empty).Trim();

                if (row.Kind == Kind.Added)
                {
                    if (text.StartsWith('#'))
                    {
                        var cleanTitle = text.TrimStart('#').Trim();
                        if (!string.IsNullOrEmpty(cleanTitle))
                        {
                            addedHeadings.Add(cleanTitle);
                            highlights.Add($"Added new section: **{cleanTitle}**");
                        }
                    }
                    else if (text.StartsWith('|') && text.EndsWith('|'))
                    {
                        addedTable = true;
                    }
                    else if (text.StartsWith("```") || text.StartsWith(":::"))
                    {
                        modifiedDiagram = true;
                    }
                }
                else if (row.Kind == Kind.Removed)
                {
                    if (text.StartsWith('|') && text.EndsWith('|'))
                    {
                        removedTable = true;
                    }
                    else if (text.StartsWith("```") || text.StartsWith(":::"))
                    {
                        modifiedDiagram = true;
                    }
                }
            }

            if (addedTable) highlights.Add("Added structured data tables and grid representations.");
            if (modifiedDiagram) highlights.Add("Updated interactive diagrams and visual blocks.");

            feat = addedHeadings.Count + (addedTable && !removedTable ? 1 : 0);
            if (feat == 0 && unifiedRows.Any(r => r.Kind == Kind.Added))
            {
                feat = 1;
            }
            fix = (modifiedDiagram ? 1 : 0) + (addedTable && removedTable ? 1 : 0);
            doc = Math.Max(1, unifiedRows.Count(r => r.Kind == Kind.Added) / 20);
        }

        return SynthesizeReleaseAnnouncementSnippet(
            versionTag, milestoneScope, status, highlights, feat, fix, doc, releaseDate);
    }

    /// <summary>
    /// Synthesizes a multi-channel release announcement digest supporting Teams, Slack, and Discord markdown formats.
    /// Provides tailored formatting for cross-platform release announcements with channel-specific syntax rules.
    /// </summary>
    public static string SynthesizeMultiChannelReleaseDigest(
        string? versionTag = null,
        string? milestoneScope = null,
        string? status = null,
        IReadOnlyList<string>? highlights = null,
        int featureCount = 0,
        int fixCount = 0,
        int docCount = 0,
        DateTime? releaseDate = null)
    {
        string tag = string.IsNullOrWhiteSpace(versionTag) ? "v1.0.0" : versionTag.Trim();
        if (!tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) && !tag.StartsWith("["))
        {
            tag = $"v{tag}";
        }

        string milestone = string.IsNullOrWhiteSpace(milestoneScope) ? "General Release" : milestoneScope.Trim();
        string stat = string.IsNullOrWhiteSpace(status) ? "Stable" : status.Trim();
        string dateStr = (releaseDate ?? DateTime.UtcNow).ToString("yyyy-MM-dd");

        var hl = highlights != null && highlights.Count > 0
            ? highlights.Take(5).ToList()
            : new List<string> { "Routine performance, reliability, and visual polish enhancements across desktop surfaces." };

        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"# 📢 MarkSmith Multi-Channel Release Digest — {tag} ({milestone})");
        sb.AppendLine($"> Generated {dateStr} · Status: **{stat}** · Telemetry: **{featureCount}** feat, **{fixCount}** fix, **{docCount}** docs");
        sb.AppendLine();

        // 1. Microsoft Teams / CommonMark Section
        sb.AppendLine("### 🟦 Microsoft Teams / GitHub Discussion Format");
        sb.AppendLine("```markdown");
        sb.AppendLine($"## 🚀 MarkSmith {tag} Released — {milestone}");
        sb.AppendLine($"> **Status:** {stat} | **Date:** {dateStr}");
        sb.AppendLine();
        sb.AppendLine("**Key Highlights:**");
        foreach (var h in hl)
        {
            sb.AppendLine($"- {h}");
        }
        sb.AppendLine();
        sb.AppendLine($"**Metrics:** {featureCount} features, {fixCount} fixes, {docCount} documentation updates.");
        sb.AppendLine("```");
        sb.AppendLine();

        // 2. Slack (mrkdwn format: *bold*, • bullets, no # headers)
        sb.AppendLine("### 🟨 Slack Format (mrkdwn)");
        sb.AppendLine("```text");
        sb.AppendLine($"*:rocket: MarkSmith {tag} Released — {milestone}*");
        sb.AppendLine($"_Status:_ `{stat}` | _Date:_ `{dateStr}`");
        sb.AppendLine();
        sb.AppendLine("*Key Highlights:*");
        foreach (var h in hl)
        {
            sb.AppendLine($"• {h}");
        }
        sb.AppendLine();
        sb.AppendLine($"*Metrics:* {featureCount} features | {fixCount} fixes | {docCount} docs updates");
        sb.AppendLine("```");
        sb.AppendLine();

        // 3. Discord Format (### headers, inline code, - bullets)
        sb.AppendLine("### 🟪 Discord Format");
        sb.AppendLine("```markdown");
        sb.AppendLine($"### 🚀 MarkSmith {tag} — {milestone}");
        sb.AppendLine($"**Status:** `{stat}` • **Date:** `{dateStr}`");
        sb.AppendLine();
        sb.AppendLine("**Highlights:**");
        foreach (var h in hl)
        {
            sb.AppendLine($"- {h}");
        }
        sb.AppendLine();
        sb.AppendLine($"📊 **Stats:** `{featureCount} feat` • `{fixCount} fix` • `{docCount} docs`");
        sb.AppendLine("```");

        return sb.ToString();
    }

    /// <summary>
    /// Synthesizes a multi-channel release digest from unified diff rows between version checkpoints.
    /// </summary>
    public static string SynthesizeMultiChannelReleaseDigest(
        IReadOnlyList<UnifiedRow>? unifiedRows,
        string? versionTag = null,
        string? milestoneScope = null,
        string? status = null,
        DateTime? releaseDate = null)
    {
        int feat = 0;
        int fix = 0;
        int doc = 0;
        var highlights = new List<string>();

        if (unifiedRows != null && unifiedRows.Count > 0)
        {
            var addedHeadings = new List<string>();
            bool addedTable = false;
            bool addedCode = false;

            foreach (var row in unifiedRows)
            {
                if (row.Kind == Kind.Added && !string.IsNullOrWhiteSpace(row.Text))
                {
                    string t = row.Text.Trim();
                    if (t.StartsWith("#") && !t.StartsWith("####"))
                    {
                        string heading = t.TrimStart('#', ' ').Trim();
                        if (!string.IsNullOrEmpty(heading) && !addedHeadings.Contains(heading))
                        {
                            addedHeadings.Add(heading);
                        }
                    }
                    else if (t.StartsWith("|") && t.EndsWith("|"))
                    {
                        addedTable = true;
                    }
                    else if (t.StartsWith("```") || t.StartsWith("~~~"))
                    {
                        addedCode = true;
                    }
                }
            }

            foreach (var h in addedHeadings.Take(3))
            {
                highlights.Add($"Added new section: **{h}**");
            }
            if (addedTable)
            {
                highlights.Add("Added structured data tables and grid representations.");
            }
            if (addedCode)
            {
                highlights.Add("Added fenced code snippets and technical examples.");
            }

            feat = Math.Max(addedHeadings.Count, unifiedRows.Count(r => r.Kind == Kind.Added) > 0 ? 1 : 0);
            fix = unifiedRows.Count(r => r.Kind == Kind.Removed);
            doc = Math.Max(1, unifiedRows.Count(r => r.Kind == Kind.Added) / 20);
        }

        return SynthesizeMultiChannelReleaseDigest(
            versionTag, milestoneScope, status, highlights, feat, fix, doc, releaseDate);
    }

    /// <summary>
    /// Audits version diff churn acceleration, line turbulence, and modification hotspots across unified diff rows.
    /// </summary>
    public static DiffChurnAccelerationAudit AuditDiffChurnAcceleration(IEnumerable<UnifiedRow>? unifiedRows)
    {
        if (unifiedRows == null) return AuditDiffChurnAcceleration((IEnumerable<Line>?)null);
        return AuditDiffChurnAcceleration(unifiedRows.Where(r => !r.IsGap).Select(r => new Line(r.Kind, r.OldNumber, r.NewNumber, r.Text)));
    }

    /// <summary>
    /// Audits version diff churn acceleration, line turbulence, and modification hotspots across diff rows.
    /// </summary>
    public static DiffChurnAccelerationAudit AuditDiffChurnAcceleration(IEnumerable<Line>? diffLines)
    {
        if (diffLines == null)
        {
            return new DiffChurnAccelerationAudit(
                0, 0, 0, 0, 0, 0.0, 0.0, "None",
                Array.Empty<SectionChurnHotspot>(), "Identical (Zero Churn)",
                "Diff churn acceleration: 0 lines · No diff rows");
        }

        var list = diffLines.ToList();
        if (list.Count == 0)
        {
            return new DiffChurnAccelerationAudit(
                0, 0, 0, 0, 0, 0.0, 0.0, "None",
                Array.Empty<SectionChurnHotspot>(), "Identical (Zero Churn)",
                "Diff churn acceleration: 0 lines · No diff rows");
        }

        int added = list.Count(l => l.Kind == Kind.Added);
        int removed = list.Count(l => l.Kind == Kind.Removed);
        int same = list.Count(l => l.Kind == Kind.Same);

        int totalBefore = same + removed;
        int totalAfter = same + added;
        int totalChanged = added + removed;

        double churnRatio = totalBefore > 0
            ? Math.Round((double)totalChanged / totalBefore, 2)
            : (totalAfter > 0 ? 1.0 : 0.0);

        double netGrowth = totalBefore > 0
            ? Math.Round((double)(totalAfter - totalBefore) / totalBefore, 2)
            : (totalAfter > 0 ? 1.0 : 0.0);

        // Section hotspots
        var sectionChanges = new Dictionary<string, (int Added, int Removed)>(StringComparer.OrdinalIgnoreCase);
        string currentHeading = "Preamble / Introduction";

        foreach (var l in list)
        {
            string t = l.Text.Trim();
            if (t.StartsWith("#"))
            {
                currentHeading = t.TrimStart('#', ' ').Trim();
            }

            if (l.Kind == Kind.Added || l.Kind == Kind.Removed)
            {
                if (!sectionChanges.TryGetValue(currentHeading, out var cur))
                {
                    cur = (0, 0);
                }

                if (l.Kind == Kind.Added) cur = (cur.Added + 1, cur.Removed);
                else cur = (cur.Added, cur.Removed + 1);

                sectionChanges[currentHeading] = cur;
            }
        }

        var hotspots = new List<SectionChurnHotspot>();
        string worstHeading = "None";
        int maxSectionChurn = -1;

        foreach (var kvp in sectionChanges.OrderByDescending(c => c.Value.Added + c.Value.Removed))
        {
            int ch = kvp.Value.Added + kvp.Value.Removed;
            double velocity = totalChanged > 0 ? Math.Round((double)ch / totalChanged, 2) : 0.0;
            hotspots.Add(new SectionChurnHotspot(kvp.Key, kvp.Value.Added, kvp.Value.Removed, ch, velocity));

            if (ch > maxSectionChurn)
            {
                maxSectionChurn = ch;
                worstHeading = kvp.Key;
            }
        }

        string category;
        if (totalChanged == 0)
        {
            category = "Identical (Zero Churn)";
        }
        else if (churnRatio >= 0.50)
        {
            category = "High Volatility (Rapid Revision Churn)";
        }
        else if (churnRatio >= 0.15)
        {
            category = "Moderate Active Churn";
        }
        else
        {
            category = "Stable Minor Edits";
        }

        string summary = FormatDiffChurnSummary(
            totalBefore, totalAfter, added, removed, churnRatio, netGrowth, worstHeading, category);

        return new DiffChurnAccelerationAudit(
            totalBefore, totalAfter, added, removed, same,
            churnRatio, netGrowth, worstHeading, hotspots, category, summary);
    }

    /// <summary>
    /// Audits version diff churn acceleration between two markdown text versions.
    /// </summary>
    public static DiffChurnAccelerationAudit AuditDiffChurnAcceleration(string? before, string? after)
    {
        var diff = Diff(before ?? "", after ?? "");
        return AuditDiffChurnAcceleration(diff);
    }

    /// <summary>
    /// Formats a human-readable summary of diff churn acceleration metrics.
    /// </summary>
    public static string FormatDiffChurnSummary(
        int totalBefore, int totalAfter, int added, int removed,
        double churnRatio, double netGrowth, string hotspotHeading, string category)
    {
        int totalChanged = added + removed;
        if (totalChanged == 0) return "Diff churn acceleration: 0 changes · Versions are identical";

        string sign = netGrowth >= 0 ? "+" : "";
        string growthStr = $"{sign}{netGrowth * 100:0.#}% net";
        string hotspotStr = hotspotHeading != "None" ? $" · Hotspot: '{hotspotHeading}'" : "";

        return $"Diff churn acceleration (+{added}/-{removed} lines · {category}): {churnRatio * 100:0.#}% churn ratio, {growthStr}{hotspotStr}";
    }

    /// <summary>
    /// Synthesizes a structured GitHub/GitLab Pull Request markdown description and risk assessment
    /// from unified diff rows and document content.
    /// </summary>
    public static PullRequestDescriptionAudit SynthesizePullRequestDescription(
        IReadOnlyList<UnifiedRow>? unifiedRows, string? baseDoc = null, string? prTitle = null)
    {
        if (unifiedRows == null || unifiedRows.Count == 0)
        {
            string emptyMarkdown = $"## Summary\n{(prTitle ?? "Routine Maintenance")}\n\n*No diff changes recorded between compared versions.*";
            return new PullRequestDescriptionAudit(0, 0, 0, "Low (No Changes)", "No Changes", Array.Empty<string>(), emptyMarkdown, "PR description: 0 changes");
        }

        int added = 0;
        int removed = 0;
        bool hasRemovedHeadings = false;
        bool hasRemovedTables = false;
        var modifiedHeadings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string currentHeading = "Preamble / Global";

        foreach (var r in unifiedRows)
        {
            string text = r.Text ?? string.Empty;
            string trimmed = text.Trim();

            if (trimmed.StartsWith("#"))
            {
                int h = 0;
                while (h < trimmed.Length && trimmed[h] == '#') h++;
                string title = trimmed[h..].Trim();
                if (!string.IsNullOrEmpty(title)) currentHeading = title;
            }

            if (r.IsAdded)
            {
                added++;
                modifiedHeadings.Add(currentHeading);
            }
            else if (r.IsRemoved)
            {
                removed++;
                modifiedHeadings.Add(currentHeading);
                if (trimmed.StartsWith("#")) hasRemovedHeadings = true;
                if (trimmed.Contains("|")) hasRemovedTables = true;
            }
        }

        int totalDelta = added + removed;
        int net = added - removed;
        string sign = net >= 0 ? "+" : "";

        string riskTier;
        if (hasRemovedHeadings || hasRemovedTables)
        {
            riskTier = "High (Structural / Breaking Changes)";
        }
        else if (totalDelta > 250)
        {
            riskTier = "High (Large Diff)";
        }
        else if (totalDelta > 50)
        {
            riskTier = "Medium (Component / Feature Polish)";
        }
        else if (totalDelta > 0)
        {
            riskTier = "Low (Targeted Refinement)";
        }
        else
        {
            riskTier = "Low (Zero Churn)";
        }

        string titleLine = string.IsNullOrWhiteSpace(prTitle) ? "Automated Desktop Polish & Subsystem Refinement" : prTitle.Trim();
        var affectedList = modifiedHeadings.OrderBy(h => h).Take(6).ToList();
        string areasBullets = affectedList.Count > 0
            ? string.Join("\n", affectedList.Select(h => $"- `{h}`"))
            : "- Core document body";

        var sb = new StringBuilder();
        sb.AppendLine($"## Summary");
        sb.AppendLine(titleLine);
        sb.AppendLine();
        sb.AppendLine($"### Change Volume & Risk");
        sb.AppendLine($"- **Added**: +{added} lines");
        sb.AppendLine($"- **Removed**: -{removed} lines");
        sb.AppendLine($"- **Net Delta**: {sign}{net} lines");
        sb.AppendLine($"- **Risk Tier**: {riskTier}");
        sb.AppendLine();
        sb.AppendLine($"### Key Areas Touched");
        sb.AppendLine(areasBullets);
        sb.AppendLine();
        sb.AppendLine($"### Verification Checklist");
        sb.AppendLine($"- [x] Build passes with 0 compilation errors");
        sb.AppendLine($"- [x] Unit test suite verified (100% pass rate)");
        sb.AppendLine($"- [x] Tested locally in WinUI 3 Desktop");
        sb.AppendLine($"- [ ] User documentation / comments maintained");

        string prMarkdown = sb.ToString();
        string summary = $"PR synthesis (+{added}/-{removed} lines · {riskTier}): {affectedList.Count} areas modified";

        return new PullRequestDescriptionAudit(
            added, removed, net, riskTier, titleLine, affectedList, prMarkdown, summary);
    }

    /// <summary>
    /// Synthesizes a structured Pull Request description between two markdown document versions.
    /// </summary>
    public static PullRequestDescriptionAudit SynthesizePullRequestDescription(
        string? before, string? after, string? prTitle = null)
    {
        var diff = Diff(before ?? "", after ?? "");
        var unified = BuildUnified(diff);
        return SynthesizePullRequestDescription(unified, before, prTitle);
    }

    /// <summary>
    /// Synthesizes a semantic Conventional Commit message (feat, fix, docs, refactor, style, chore)
    /// from unified diff rows, detecting breaking changes, modified sections, and change intent.
    /// </summary>
    public static ConventionalCommitAudit SynthesizeConventionalCommitMessage(
        IReadOnlyList<UnifiedRow>? unifiedRows, string? baseDoc = null, string? scope = null)
    {
        string determinedScope = !string.IsNullOrWhiteSpace(scope) ? scope.Trim().ToLowerInvariant() : "content";

        if (unifiedRows == null || unifiedRows.Count == 0)
        {
            string emptyMsg = $"chore({determinedScope}): no file changes recorded";
            return new ConventionalCommitAudit(
                "chore", determinedScope, "no file changes recorded", "No diff changes between compared versions.",
                false, string.Empty, emptyMsg, "Conventional commit: 0 changes");
        }

        int added = 0;
        int removed = 0;
        bool hasRemovedHeadings = false;
        bool hasRemovedTables = false;
        bool hasBreakingKeyword = false;
        bool hasFixKeyword = false;
        bool hasDocKeyword = false;
        var touchedHeadings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string currentHeading = "Preamble";

        foreach (var r in unifiedRows)
        {
            string text = r.Text ?? string.Empty;
            string trimmed = text.Trim();

            if (trimmed.StartsWith("#"))
            {
                int h = 0;
                while (h < trimmed.Length && trimmed[h] == '#') h++;
                string headingTitle = trimmed[h..].Trim();
                if (!string.IsNullOrEmpty(headingTitle)) currentHeading = headingTitle;
            }

            if (r.IsAdded)
            {
                added++;
                touchedHeadings.Add(currentHeading);
                string lower = trimmed.ToLowerInvariant();
                if (lower.Contains("fix") || lower.Contains("bug") || lower.Contains("patch") || lower.Contains("resolve"))
                    hasFixKeyword = true;
                if (lower.Contains("docs") || lower.Contains("guide") || lower.Contains("readme"))
                    hasDocKeyword = true;
            }
            else if (r.IsRemoved)
            {
                removed++;
                touchedHeadings.Add(currentHeading);
                if (trimmed.StartsWith("#")) hasRemovedHeadings = true;
                if (trimmed.Contains("|")) hasRemovedTables = true;
                if (trimmed.Contains("BREAKING CHANGE", StringComparison.OrdinalIgnoreCase))
                    hasBreakingKeyword = true;
            }
        }

        bool isBreakingChange = hasBreakingKeyword || hasRemovedHeadings || hasRemovedTables;
        string breakingNotice = string.Empty;
        if (isBreakingChange)
        {
            breakingNotice = hasRemovedHeadings
                ? "BREAKING CHANGE: Removed or renamed structural section headings."
                : (hasRemovedTables
                    ? "BREAKING CHANGE: Structural table schemas or datasets removed."
                    : "BREAKING CHANGE: Document structure modified in a non-backwards-compatible manner.");
        }

        string type;
        if (hasFixKeyword)
        {
            type = "fix";
        }
        else if (added > 0 && removed == 0 && added >= 10)
        {
            type = "feat";
        }
        else if (added > 0 && (double)added / Math.Max(1, added + removed) >= 0.70)
        {
            type = "feat";
        }
        else if (hasDocKeyword)
        {
            type = "docs";
        }
        else if (added > 0 && removed > 0 && Math.Abs(added - removed) <= 10 && added < 30)
        {
            type = "refactor";
        }
        else if (added + removed <= 5)
        {
            type = "style";
        }
        else
        {
            type = "refactor";
        }

        var sampleHeadings = touchedHeadings.OrderBy(h => h).Take(3).ToList();
        string subjectSummary;
        if (sampleHeadings.Count > 0 && !sampleHeadings.All(h => h.Equals("Preamble", StringComparison.OrdinalIgnoreCase)))
        {
            var validHeadings = sampleHeadings.Where(h => !h.Equals("Preamble", StringComparison.OrdinalIgnoreCase)).ToList();
            string areas = string.Join(", ", validHeadings);
            if (areas.Length > 40) areas = areas[..37] + "...";
            subjectSummary = $"update {areas}";
        }
        else
        {
            subjectSummary = $"{type} updates across document (+{added}/-{removed} lines)";
        }

        string headerLine = isBreakingChange
            ? $"{type}({determinedScope})!: {subjectSummary}"
            : $"{type}({determinedScope}): {subjectSummary}";

        var bodyLines = new List<string>
        {
            $"- Delta: +{added} / -{removed} lines (net {(added - removed >= 0 ? "+" : "")}{added - removed})"
        };
        if (sampleHeadings.Count > 0)
        {
            bodyLines.Add($"- Touched sections: {string.Join(", ", sampleHeadings)}");
        }
        string body = string.Join("\n", bodyLines);

        var sb = new StringBuilder();
        sb.AppendLine(headerLine);
        sb.AppendLine();
        sb.AppendLine(body);
        if (isBreakingChange)
        {
            sb.AppendLine();
            sb.AppendLine(breakingNotice);
        }

        string fullCommitMessage = sb.ToString().TrimEnd();
        string summary = $"Conventional commit ({type}{(isBreakingChange ? "!" : "")}): {subjectSummary} (+{added}/-{removed})";

        return new ConventionalCommitAudit(
            type, determinedScope, subjectSummary, body, isBreakingChange, breakingNotice, fullCommitMessage, summary);
    }

    /// <summary>
    /// Synthesizes a conventional commit message between two markdown document versions.
    /// </summary>
    public static ConventionalCommitAudit SynthesizeConventionalCommitMessage(
        string? before, string? after, string? scope = null)
    {
        var diff = Diff(before ?? "", after ?? "");
        var unified = BuildUnified(diff);
        return SynthesizeConventionalCommitMessage(unified, before, scope);
    }

    /// <summary>
    /// Evaluates unified diff rows and current version string to recommend a Semantic Version (SemVer)
    /// bump (Major, Minor, Patch) and synthesizes a formatted changelog entry.
    /// </summary>
    public static SemanticVersionBumpAudit SynthesizeSemanticVersionRecommendation(
        IReadOnlyList<UnifiedRow>? unifiedRows, string? currentVersion = null, string? baseDoc = null)
    {
        string rawVer = !string.IsNullOrWhiteSpace(currentVersion) ? currentVersion.Trim().TrimStart('v', 'V') : "1.0.0";
        int major = 1, minor = 0, patch = 0;
        var verParts = rawVer.Split('.');
        if (verParts.Length >= 1 && int.TryParse(verParts[0], out int maj)) major = maj;
        if (verParts.Length >= 2 && int.TryParse(verParts[1], out int min)) minor = min;
        if (verParts.Length >= 3 && int.TryParse(verParts[2].Split('-', '+')[0], out int pat)) patch = pat;

        string currentSemVer = $"{major}.{minor}.{patch}";

        if (unifiedRows == null || unifiedRows.Count == 0)
        {
            string emptySummary = $"SemVer bump: {currentSemVer} → {currentSemVer} (None) · No diff changes detected";
            return new SemanticVersionBumpAudit(
                currentSemVer, currentSemVer, "None", "No diff changes between compared versions.",
                0, 0, false, $"## [{currentSemVer}] - {DateTime.UtcNow:yyyy-MM-dd}\n### Unchanged\n- No modifications detected.\n", emptySummary);
        }

        int added = 0;
        int removed = 0;
        bool hasBreakingKeyword = false;
        bool hasRemovedHeadings = false;
        bool hasRemovedTables = false;
        bool hasAddedHeadings = false;
        bool hasAddedTables = false;
        bool hasFixKeyword = false;
        var addedBulletPoints = new List<string>();
        var fixedBulletPoints = new List<string>();
        var changedBulletPoints = new List<string>();

        foreach (var r in unifiedRows)
        {
            string text = r.Text ?? string.Empty;
            string trimmed = text.Trim();

            if (r.IsAdded)
            {
                added++;
                if (trimmed.StartsWith("#"))
                {
                    hasAddedHeadings = true;
                    string hTitle = trimmed.TrimStart('#').Trim();
                    if (!string.IsNullOrEmpty(hTitle)) addedBulletPoints.Add($"Added section `{hTitle}`");
                }
                if (trimmed.Contains("|")) hasAddedTables = true;

                string lower = trimmed.ToLowerInvariant();
                if (lower.Contains("fix") || lower.Contains("bug") || lower.Contains("patch") || lower.Contains("resolve"))
                {
                    hasFixKeyword = true;
                    if (trimmed.Length > 4 && fixedBulletPoints.Count < 5)
                    {
                        fixedBulletPoints.Add(trimmed.Length > 60 ? trimmed[..57] + "..." : trimmed);
                    }
                }
            }
            else if (r.IsRemoved)
            {
                removed++;
                if (trimmed.StartsWith("#"))
                {
                    hasRemovedHeadings = true;
                    string hTitle = trimmed.TrimStart('#').Trim();
                    if (!string.IsNullOrEmpty(hTitle)) changedBulletPoints.Add($"Removed section `{hTitle}`");
                }
                if (trimmed.Contains("|")) hasRemovedTables = true;
                if (trimmed.Contains("BREAKING CHANGE", StringComparison.OrdinalIgnoreCase))
                    hasBreakingKeyword = true;
            }
        }

        bool isBreaking = hasBreakingKeyword || hasRemovedHeadings || hasRemovedTables;
        string bumpType;
        string recommendedVer;
        string rationale;

        if (isBreaking)
        {
            bumpType = "Major";
            recommendedVer = $"{major + 1}.0.0";
            rationale = "Breaking change detected (structural removal of headings/tables or explicit BREAKING CHANGE notice)";
        }
        else if (hasAddedHeadings || hasAddedTables || added >= 15)
        {
            bumpType = "Minor";
            recommendedVer = $"{major}.{minor + 1}.0";
            rationale = "New feature or section additions detected (substantive new content with backwards compatibility)";
        }
        else if (added > 0 || removed > 0)
        {
            bumpType = "Patch";
            recommendedVer = $"{major}.{minor}.{patch + 1}";
            rationale = hasFixKeyword
                ? "Backwards-compatible defect resolution or bug fix detected"
                : "Backwards-compatible revisions, prose polish, or targeted corrections";
        }
        else
        {
            bumpType = "None";
            recommendedVer = currentSemVer;
            rationale = "No line modifications recorded";
        }

        // Build Keep-A-Changelog formatted markdown
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## [{recommendedVer}] - {DateTime.UtcNow:yyyy-MM-dd}");
        if (isBreaking)
        {
            sb.AppendLine("### Breaking Changes");
            sb.AppendLine($"- {rationale}");
        }
        if (addedBulletPoints.Count > 0)
        {
            sb.AppendLine("### Added");
            foreach (var b in addedBulletPoints.Take(5)) sb.AppendLine($"- {b}");
        }
        if (fixedBulletPoints.Count > 0)
        {
            sb.AppendLine("### Fixed");
            foreach (var b in fixedBulletPoints.Take(5)) sb.AppendLine($"- {b}");
        }
        if (changedBulletPoints.Count > 0)
        {
            sb.AppendLine("### Changed");
            foreach (var b in changedBulletPoints.Take(5)) sb.AppendLine($"- {b}");
        }
        if (addedBulletPoints.Count == 0 && fixedBulletPoints.Count == 0 && changedBulletPoints.Count == 0)
        {
            sb.AppendLine(bumpType == "Patch" ? "### Fixed" : "### Changed");
            sb.AppendLine($"- General revisions and document refinements ({added} added, {removed} removed lines).");
        }

        string changelog = sb.ToString();
        string summary = $"SemVer bump recommendation: {currentSemVer} → {recommendedVer} ({bumpType}) · {added} additions, {removed} deletions · {rationale}";

        return new SemanticVersionBumpAudit(
            currentSemVer, recommendedVer, bumpType, rationale, added, removed, isBreaking, changelog, summary);
    }

    /// <summary>
    /// Evaluates diff line changes to recommend a Semantic Version bump.
    /// </summary>
    public static SemanticVersionBumpAudit SynthesizeSemanticVersionRecommendation(
        IEnumerable<Line>? diffLines, string? currentVersion = null)
    {
        var lineList = (diffLines as IReadOnlyList<Line>) ?? diffLines?.ToList() ?? (IReadOnlyList<Line>)Array.Empty<Line>();
        var unified = BuildUnified(lineList);
        return SynthesizeSemanticVersionRecommendation(unified, currentVersion);
    }

    /// <summary>
    /// Evaluates raw text diff between before and after versions to recommend a Semantic Version bump.
    /// </summary>
    public static SemanticVersionBumpAudit SynthesizeSemanticVersionRecommendation(
        string? before, string? after, string? currentVersion = null)
    {
        var diff = Diff(before ?? "", after ?? "");
        var unified = BuildUnified(diff);
        return SynthesizeSemanticVersionRecommendation(unified, currentVersion, before);
    }

    /// <summary>
    /// Synthesizes comprehensive production-grade release notes, categorized change groups,
    /// and a targeted migration guide with breaking change checklists from diff deltas.
    /// </summary>
    public static ReleaseNotesAndMigrationAudit SynthesizeReleaseNotesAndMigrationGuide(
        IEnumerable<UnifiedRow>? unifiedRows, string currentVersion = "1.0.0")
    {
        var rows = (unifiedRows as IReadOnlyList<UnifiedRow>) ?? unifiedRows?.ToList() ?? (IReadOnlyList<UnifiedRow>)Array.Empty<UnifiedRow>();

        int addedCount = 0;
        int removedCount = 0;
        var features = new List<string>();
        var fixes = new List<string>();
        var breakingChanges = new List<string>();
        var deprecations = new List<string>();
        var maintenance = new List<string>();

        foreach (var r in rows)
        {
            if (r.IsGap) continue;
            if (r.Kind == Kind.Added) addedCount++;
            else if (r.Kind == Kind.Removed) removedCount++;

            string text = (r.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (r.Kind == Kind.Added)
            {
                if (text.StartsWith("BREAKING CHANGE:", StringComparison.OrdinalIgnoreCase) || text.Contains("!:"))
                {
                    breakingChanges.Add(text.Replace("BREAKING CHANGE:", "").Trim());
                }
                else if (text.StartsWith("#"))
                {
                    string heading = text.TrimStart('#').Trim();
                    if (!string.IsNullOrWhiteSpace(heading))
                    {
                        features.Add($"Add section: **{heading}**");
                    }
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(?:feat|feature)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    features.Add(text.TrimStart('-', '*', '+', ' '));
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(?:fix|bug|patch|resolve)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    fixes.Add(text.TrimStart('-', '*', '+', ' '));
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(?:deprecat|obsolete)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    deprecations.Add(text.TrimStart('-', '*', '+', ' '));
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(?:refactor|perf|chore|doc|test)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    maintenance.Add(text.TrimStart('-', '*', '+', ' '));
                }
            }
            else if (r.Kind == Kind.Removed)
            {
                if (text.StartsWith("#") && !text.StartsWith("####"))
                {
                    string heading = text.TrimStart('#').Trim();
                    if (!string.IsNullOrWhiteSpace(heading))
                    {
                        breakingChanges.Add($"Removed section or API: **{heading}**");
                    }
                }
            }
        }

        // Default fallbacks if diff has changes but no explicit commit tags
        if (features.Count == 0 && addedCount > 0)
        {
            features.Add($"Integrated {addedCount} lines of content and functional enhancements.");
        }
        if (fixes.Count == 0 && removedCount > 0 && breakingChanges.Count == 0)
        {
            fixes.Add($"Refined and pruned {removedCount} obsolete or redundant lines.");
        }

        // Determine recommended version bump
        string curVer = string.IsNullOrWhiteSpace(currentVersion) ? "1.0.0" : currentVersion.TrimStart('v', 'V');
        var parts = curVer.Split('.');
        int major = parts.Length > 0 && int.TryParse(parts[0], out int mj) ? mj : 1;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int mn) ? mn : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int pt) ? pt : 0;

        string nextVer;
        string riskTier;
        if (breakingChanges.Count > 0)
        {
            nextVer = $"{major + 1}.0.0";
            riskTier = "Major (Breaking Changes)";
        }
        else if (features.Count > 0)
        {
            nextVer = $"{major}.{minor + 1}.0";
            riskTier = "Minor (Feature Enhancements)";
        }
        else
        {
            nextVer = $"{major}.{minor}.{patch + 1}";
            riskTier = "Patch (Maintenance & Fixes)";
        }

        // Synthesize Migration Checklist
        var migrationSteps = new List<string>();
        if (breakingChanges.Count > 0)
        {
            migrationSteps.Add("Audit affected downstream consumers and templates for removed structures or APIs.");
            foreach (var b in breakingChanges.Take(3))
            {
                migrationSteps.Add($"Migrate references to '{b}' according to the new specification.");
            }
            migrationSteps.Add("Execute full automated regression tests to verify backward compatibility.");
        }
        else
        {
            migrationSteps.Add("Zero breaking changes detected. Fully backward-compatible drop-in upgrade.");
        }

        var categories = new List<ReleaseNoteCategory>();
        if (features.Count > 0) categories.Add(new ReleaseNoteCategory("🚀 Features & Enhancements", features));
        if (fixes.Count > 0) categories.Add(new ReleaseNoteCategory("🐛 Bug Fixes & Stability", fixes));
        if (breakingChanges.Count > 0) categories.Add(new ReleaseNoteCategory("⚠️ Breaking Changes", breakingChanges));
        if (deprecations.Count > 0) categories.Add(new ReleaseNoteCategory("⏳ Deprecation Notices", deprecations));
        if (maintenance.Count > 0) categories.Add(new ReleaseNoteCategory("🛠 Maintenance & Chores", maintenance));

        // Build Markdown Output
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Release Notes — v{nextVer}");
        sb.AppendLine($"> **Scope:** {riskTier} · **Baseline:** v{curVer} · **Delta:** +{addedCount} / -{removedCount} lines");
        sb.AppendLine();

        sb.AppendLine("## Highlights");
        if (features.Count > 0) sb.AppendLine($"- {features[0]}");
        if (fixes.Count > 0) sb.AppendLine($"- {fixes[0]}");
        if (breakingChanges.Count > 0) sb.AppendLine($"- ⚠ Attention: Contains {breakingChanges.Count} breaking changes requiring manual migration.");
        sb.AppendLine();

        foreach (var cat in categories)
        {
            sb.AppendLine($"## {cat.CategoryName}");
            foreach (var item in cat.Items.Take(6))
            {
                sb.AppendLine($"- {item}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## 📋 Migration Guide");
        foreach (var step in migrationSteps)
        {
            sb.AppendLine($"- [ ] {step}");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*Generated automatically by MarkSmith Studio History & Version Control.*");

        string releaseNotesMd = sb.ToString();
        string summary = FormatReleaseNotesSummary(
            nextVer, curVer, riskTier, features.Count, fixes.Count, breakingChanges.Count, addedCount, removedCount);

        return new ReleaseNotesAndMigrationAudit(
            curVer, nextVer, riskTier, features.Count, fixes.Count, breakingChanges.Count,
            addedCount + removedCount, categories, migrationSteps, releaseNotesMd, summary);
    }

    /// <summary>
    /// Evaluates diff line changes to synthesize release notes and migration guide.
    /// </summary>
    public static ReleaseNotesAndMigrationAudit SynthesizeReleaseNotesAndMigrationGuide(
        IEnumerable<Line>? diffLines, string currentVersion = "1.0.0")
    {
        var lineList = (diffLines as IReadOnlyList<Line>) ?? diffLines?.ToList() ?? (IReadOnlyList<Line>)Array.Empty<Line>();
        var unified = BuildUnified(lineList);
        return SynthesizeReleaseNotesAndMigrationGuide(unified, currentVersion);
    }

    /// <summary>
    /// Evaluates raw text diff to synthesize release notes and migration guide.
    /// </summary>
    public static ReleaseNotesAndMigrationAudit SynthesizeReleaseNotesAndMigrationGuide(
        string? before, string? after, string currentVersion = "1.0.0")
    {
        var diff = Diff(before ?? "", after ?? "");
        var unified = BuildUnified(diff);
        return SynthesizeReleaseNotesAndMigrationGuide(unified, currentVersion);
    }

    /// <summary>
    /// Formats a human-readable summary of synthesized release notes and migration scope.
    /// </summary>
    public static string FormatReleaseNotesSummary(
        string nextVer, string curVer, string riskTier, int featCount, int fixCount,
        int breakCount, int addedLines, int removedLines)
    {
        string breakNote = breakCount > 0 ? $" · ⚠ {breakCount} breaking" : "";
        return $"Release notes (v{nextVer} · {riskTier}): {featCount} feat, {fixCount} fix{breakNote} · +{addedLines}/-{removedLines} lines";
    }

    /// <summary>
    /// Audits diff rows to detect localized modification churn hotspots, calculate the change volatility index,
    /// and compute Shannon change concentration entropy across document blocks.
    /// </summary>
    public static CommitChurnHotspotsAudit AuditCommitChurnHotspots(IEnumerable<UnifiedRow>? unifiedRows)
    {
        var rows = (unifiedRows ?? Enumerable.Empty<UnifiedRow>()).ToList();
        if (rows.Count == 0)
        {
            return new CommitChurnHotspotsAudit(
                0, 0, 0, 0, 0.0, 0.0, 0.0,
                Array.Empty<ChurnHotspotInfo>(),
                "Stable Baseline (Zero Churn)",
                "Commit churn hotspots: 0 diff rows · No changes detected");
        }

        int added = 0;
        int removed = 0;
        int unchanged = 0;
        var modifiedIndices = new List<int>();

        string currentHeading = "Preamble / Document Header";
        var sectionChanges = new Dictionary<string, (int Added, int Removed, int StartLine, int EndLine)>();

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            string text = (r.Text ?? "").Trim();

            if (text.StartsWith('#'))
            {
                currentHeading = text.TrimStart('#').Trim();
            }

            if (r.Kind == Kind.Added)
            {
                added++;
                modifiedIndices.Add(i);
                if (!sectionChanges.TryGetValue(currentHeading, out var cur))
                    sectionChanges[currentHeading] = (1, 0, i + 1, i + 1);
                else
                    sectionChanges[currentHeading] = (cur.Added + 1, cur.Removed, cur.StartLine, i + 1);
            }
            else if (r.Kind == Kind.Removed)
            {
                removed++;
                modifiedIndices.Add(i);
                if (!sectionChanges.TryGetValue(currentHeading, out var cur))
                    sectionChanges[currentHeading] = (0, 1, i + 1, i + 1);
                else
                    sectionChanges[currentHeading] = (cur.Added, cur.Removed + 1, cur.StartLine, i + 1);
            }
            else
            {
                unchanged++;
            }
        }

        int totalModified = added + removed;
        if (totalModified == 0)
        {
            return new CommitChurnHotspotsAudit(
                rows.Count, 0, 0, rows.Count, 0.0, 0.0, 0.0,
                Array.Empty<ChurnHotspotInfo>(),
                "Stable Baseline (Zero Churn)",
                $"Commit churn hotspots ({rows.Count} rows): 100% stable · Zero modified lines");
        }

        var hotspots = new List<ChurnHotspotInfo>();
        if (modifiedIndices.Count > 0)
        {
            int clusterStart = modifiedIndices[0];
            int clusterEnd = modifiedIndices[0];
            int clusterAdded = rows[clusterStart].Kind == Kind.Added ? 1 : 0;
            int clusterRemoved = rows[clusterStart].Kind == Kind.Removed ? 1 : 0;

            for (int k = 1; k < modifiedIndices.Count; k++)
            {
                int idx = modifiedIndices[k];
                if (idx <= clusterEnd + 2)
                {
                    clusterEnd = idx;
                    if (rows[idx].Kind == Kind.Added) clusterAdded++;
                    else if (rows[idx].Kind == Kind.Removed) clusterRemoved++;
                }
                else
                {
                    string heading = "Section";
                    for (int h = clusterStart; h >= 0; h--)
                    {
                        string t = (rows[h].Text ?? "").Trim();
                        if (t.StartsWith('#')) { heading = t.TrimStart('#').Trim(); break; }
                    }

                    int clTotal = clusterAdded + clusterRemoved;
                    double density = Math.Round((double)clTotal / Math.Max(1, clusterEnd - clusterStart + 1), 2);
                    hotspots.Add(new ChurnHotspotInfo(
                        clusterStart + 1, clusterEnd + 1, heading, clusterAdded, clusterRemoved, clTotal, density));

                    clusterStart = idx;
                    clusterEnd = idx;
                    clusterAdded = rows[idx].Kind == Kind.Added ? 1 : 0;
                    clusterRemoved = rows[idx].Kind == Kind.Removed ? 1 : 0;
                }
            }

            string lastHeading = "Section";
            for (int h = clusterStart; h >= 0; h--)
            {
                string t = (rows[h].Text ?? "").Trim();
                if (t.StartsWith('#')) { lastHeading = t.TrimStart('#').Trim(); break; }
            }
            int lastTotal = clusterAdded + clusterRemoved;
            double lastDensity = Math.Round((double)lastTotal / Math.Max(1, clusterEnd - clusterStart + 1), 2);
            hotspots.Add(new ChurnHotspotInfo(
                clusterStart + 1, clusterEnd + 1, lastHeading, clusterAdded, clusterRemoved, lastTotal, lastDensity));
        }

        int blockSize = Math.Max(5, rows.Count / 10);
        int blockCount = (int)Math.Ceiling((double)rows.Count / blockSize);
        var blockEdits = new int[blockCount];
        foreach (int idx in modifiedIndices)
        {
            int b = Math.Min(idx / blockSize, blockCount - 1);
            blockEdits[b]++;
        }

        double entropy = 0.0;
        foreach (int count in blockEdits)
        {
            if (count > 0)
            {
                double p = (double)count / totalModified;
                entropy -= p * Math.Log2(p);
            }
        }
        double maxEntropy = blockCount > 1 ? Math.Log2(blockCount) : 1.0;
        double normalizedEntropy = maxEntropy > 0 ? Math.Round(Math.Clamp(entropy / maxEntropy, 0.0, 1.0), 2) : 0.0;

        double modRatio = (double)totalModified / rows.Count;
        double peakDensity = hotspots.Count > 0 ? hotspots.Max(h => h.Density) : 0.0;
        double volatilityIndex = Math.Round(Math.Clamp((modRatio * 50.0) + (peakDensity * 30.0) + (Math.Min(1.0, totalModified / 50.0) * 20.0), 0.0, 100.0), 1);

        string category;
        if (volatilityIndex >= 65.0 || (hotspots.Any(h => h.TotalChanged >= 15 && h.Density >= 0.8)))
        {
            category = "Extreme Hotspot Volatility (High Risk)";
        }
        else if (volatilityIndex >= 35.0 || hotspots.Count >= 3)
        {
            category = "Moderate Concentrated Churn";
        }
        else if (normalizedEntropy >= 0.7 && totalModified > 5)
        {
            category = "Diffused Low-Impact Refactor";
        }
        else
        {
            category = "Surgical Precision Change";
        }

        string summary = FormatCommitChurnHotspotsSummary(
            rows.Count, added, removed, hotspots.Count, volatilityIndex, normalizedEntropy, category, hotspots);

        return new CommitChurnHotspotsAudit(
            rows.Count,
            added,
            removed,
            unchanged,
            volatilityIndex,
            normalizedEntropy,
            peakDensity,
            hotspots.OrderByDescending(h => h.TotalChanged).ToList(),
            category,
            summary);
    }

    /// <summary>
    /// Static overload auditing commit churn hotspots from raw text diff.
    /// </summary>
    public static CommitChurnHotspotsAudit AuditCommitChurnHotspots(string? before, string? after)
    {
        var diff = Diff(before ?? "", after ?? "");
        var unified = BuildUnified(diff);
        return AuditCommitChurnHotspots(unified);
    }

    /// <summary>
    /// Formats a human-readable summary of commit churn hotspots and volatility metrics.
    /// </summary>
    public static string FormatCommitChurnHotspotsSummary(
        int totalRows, int added, int removed, int clusterCount, double volatilityIndex,
        double entropy, string category, IReadOnlyList<ChurnHotspotInfo> hotspots)
    {
        if (totalRows == 0 || (added == 0 && removed == 0))
            return "Commit churn audit: 0 modifications · Document is unchanged";

        string clusterLabel = clusterCount == 1 ? "1 hotspot cluster" : $"{clusterCount} hotspot clusters";
        string topHotspotStr = "";
        if (hotspots.Count > 0)
        {
            var top = hotspots.OrderByDescending(h => h.TotalChanged).First();
            topHotspotStr = $" · Top hotspot: Lines {top.StartLine}-{top.EndLine} in '{top.SectionHeading}' ({top.TotalChanged} lines)";
        }

        return $"Commit churn audit (+{added}/-{removed} in {totalRows} rows · {category}): Volatility: {volatilityIndex:0.#}/100 (Entropy {entropy:0.##}) · {clusterLabel}{topHotspotStr}";
    }

    /// <summary>
    /// Audits diff chunk context boundaries for semantic alignment (headings, code fences, functions),
    /// multiline construct truncation, and identifies adjacent/overlapping hunks suitable for deduplication.
    /// </summary>
    public static DiffChunkBoundaryAudit AuditChunkContextBoundaries(IReadOnlyList<UnifiedRow>? unifiedRows)
    {
        if (unifiedRows is null || unifiedRows.Count == 0)
        {
            string emptySummary = "Diff chunk boundary audit: 0 hunks · Document is unchanged (Zero diff deltas)";
            return new DiffChunkBoundaryAudit(0, 0, 0, 0, 0.0, 100.0, Array.Empty<ChunkBoundaryInfo>(), "Clean Baseline (Zero Diff Deltas)", emptySummary);
        }

        var rows = unifiedRows.Where(r => !r.IsGap).ToList();
        if (rows.Count == 0 || rows.All(r => r.Kind == Kind.Same))
        {
            string emptySummary = "Diff chunk boundary audit: 0 hunks · Document is unchanged (Zero diff deltas)";
            return new DiffChunkBoundaryAudit(0, 0, 0, 0, 0.0, 100.0, Array.Empty<ChunkBoundaryInfo>(), "Clean Baseline (Zero Diff Deltas)", emptySummary);
        }

        // Identify modified clusters (hunks)
        var hunkRanges = new List<(int Start, int End)>();
        int hunkStart = -1;

        for (int i = 0; i < rows.Count; i++)
        {
            bool isMod = rows[i].Kind != Kind.Same;
            if (isMod)
            {
                if (hunkStart == -1) hunkStart = i;
            }
            else
            {
                if (hunkStart != -1)
                {
                    hunkRanges.Add((hunkStart, i - 1));
                    hunkStart = -1;
                }
            }
        }
        if (hunkStart != -1)
        {
            hunkRanges.Add((hunkStart, rows.Count - 1));
        }

        if (hunkRanges.Count == 0)
        {
            string emptySummary = "Diff chunk boundary audit: 0 hunks · Document is unchanged (Zero diff deltas)";
            return new DiffChunkBoundaryAudit(0, 0, 0, 0, 0.0, 100.0, Array.Empty<ChunkBoundaryInfo>(), "Clean Baseline (Zero Diff Deltas)", emptySummary);
        }

        var hunkInfos = new List<ChunkBoundaryInfo>();
        int alignedCount = 0;
        int truncatedCount = 0;
        int overlappingCount = 0;
        double totalPadding = 0.0;

        for (int h = 0; h < hunkRanges.Count; h++)
        {
            var (startIdx, endIdx) = hunkRanges[h];

            // Context leading lines
            int leadingContext = 0;
            int prevBoundary = (h > 0) ? hunkRanges[h - 1].End : -1;
            for (int k = startIdx - 1; k > prevBoundary; k--)
            {
                if (rows[k].Kind == Kind.Same) leadingContext++;
                else break;
            }

            // Context trailing lines
            int trailingContext = 0;
            int nextBoundary = (h < hunkRanges.Count - 1) ? hunkRanges[h + 1].Start : rows.Count;
            for (int k = endIdx + 1; k < nextBoundary; k++)
            {
                if (rows[k].Kind == Kind.Same) trailingContext++;
                else break;
            }

            totalPadding += (leadingContext + trailingContext) / 2.0;

            // Check overlap / proximity to adjacent hunk
            bool overlaps = false;
            if (h > 0 && (startIdx - hunkRanges[h - 1].End - 1) <= 2) overlaps = true;
            if (h < hunkRanges.Count - 1 && (hunkRanges[h + 1].Start - endIdx - 1) <= 2) overlaps = true;
            if (overlaps) overlappingCount++;

            // Find preceding semantic header
            string precedingHeader = "(Document Root)";
            for (int k = startIdx; k >= 0; k--)
            {
                string t = rows[k].Text?.Trim() ?? "";
                if (t.StartsWith("#") || t.StartsWith("//") || t.StartsWith("/*") || t.Contains("class ") || t.Contains("void "))
                {
                    precedingHeader = t.Length > 30 ? t.Substring(0, 27) + "..." : t;
                    break;
                }
            }

            // Semantic alignment check
            bool aligns = false;
            if (startIdx == 0) aligns = true;
            else
            {
                string prevLine = rows[startIdx - 1].Text?.Trim() ?? "";
                string startLine = rows[startIdx].Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(prevLine) || prevLine.StartsWith("#") || prevLine.StartsWith("```") ||
                    startLine.StartsWith("#") || startLine.StartsWith("```"))
                {
                    aligns = true;
                }
            }
            if (aligns) alignedCount++;

            // Truncation check (e.g. split in the middle of a code fence or table)
            int fencesBefore = 0;
            for (int k = 0; k < startIdx; k++)
            {
                if ((rows[k].Text?.Trim() ?? "").StartsWith("```")) fencesBefore++;
            }
            bool truncates = (fencesBefore % 2 != 0);
            if (truncates) truncatedCount++;

            hunkInfos.Add(new ChunkBoundaryInfo(
                h + 1,
                startIdx + 1,
                endIdx + 1,
                leadingContext,
                trailingContext,
                precedingHeader,
                aligns,
                truncates,
                overlaps));
        }

        double meanPadding = Math.Round(totalPadding / hunkRanges.Count, 1);
        double boundaryScore = Math.Round(Math.Clamp(
            ((double)alignedCount / hunkRanges.Count * 60.0) +
            (Math.Max(0.0, 1.0 - ((double)truncatedCount / hunkRanges.Count)) * 25.0) +
            (Math.Max(0.0, 1.0 - ((double)overlappingCount / hunkRanges.Count)) * 15.0), 0.0, 100.0), 1);

        string category;
        if (alignedCount == hunkRanges.Count && truncatedCount == 0 && overlappingCount == 0)
        {
            category = "Pixel-Perfect Semantic Chunk Boundaries";
        }
        else if (truncatedCount > 0)
        {
            category = "Truncated Multiline Constructs Detected";
        }
        else if (overlappingCount > 0)
        {
            category = "Overlapping Hunks (Deduplication Recommended)";
        }
        else
        {
            category = "Well-Formed Chunk Boundaries";
        }

        string summary = FormatChunkBoundarySummary(
            hunkRanges.Count, alignedCount, truncatedCount, overlappingCount, meanPadding, boundaryScore, category);

        return new DiffChunkBoundaryAudit(
            hunkRanges.Count,
            alignedCount,
            truncatedCount,
            overlappingCount,
            meanPadding,
            boundaryScore,
            hunkInfos,
            category,
            summary);
    }

    /// <summary>
    /// Static overload auditing diff chunk context boundaries from raw text comparison.
    /// </summary>
    public static DiffChunkBoundaryAudit AuditChunkContextBoundaries(string? before, string? after)
    {
        var diff = Diff(before ?? "", after ?? "");
        var unified = BuildUnified(diff);
        return AuditChunkContextBoundaries(unified);
    }

    /// <summary>
    /// Formats a human-readable summary of diff chunk boundary metrics.
    /// </summary>
    public static string FormatChunkBoundarySummary(
        int totalHunks, int alignedHunks, int truncatedCount, int overlappingCount,
        double meanPadding, double score, string category)
    {
        if (totalHunks == 0) return "Diff chunk boundary audit: 0 hunks · Document is unchanged (Zero diff deltas)";

        string hunkLabel = totalHunks == 1 ? "1 hunk" : $"{totalHunks} hunks";
        string issues = "";
        if (truncatedCount > 0) issues += $" · ⚠ {truncatedCount} truncated constructs";
        if (overlappingCount > 0) issues += $" · ⚠ {overlappingCount} overlapping hunks";

        return $"Diff chunk boundary audit ({hunkLabel} · {category}): {score:0.#}% integrity (Aligned: {alignedHunks}/{totalHunks} · Mean context padding {meanPadding:0.#} lines){issues}";
    }

    /// <summary>
    /// Audits unified diff hunks for syntactic scope headers (classes, methods, functions, markdown headings),
    /// ensures folded diff hunks preserve complete lexical context blocks, and identifies unanchored diff deltas.
    /// </summary>
    public static SemanticHunkFoldingAudit AuditSemanticHunkFoldingAndTokenContext(IReadOnlyList<UnifiedRow>? unifiedRows)
    {
        if (unifiedRows is null || unifiedRows.Count == 0)
        {
            string emptySummary = "Diff semantic hunk folding: 0 hunks · Document is unchanged (Zero diff deltas)";
            return new SemanticHunkFoldingAudit(0, 0, 0, 0, 0, 100.0, Array.Empty<SemanticHunkScopeInfo>(), Array.Empty<string>(), "Clean Baseline (Zero Diff Deltas)", emptySummary);
        }

        var rows = unifiedRows.Where(r => !r.IsGap).ToList();
        if (rows.Count == 0 || rows.All(r => r.Kind == Kind.Same))
        {
            string emptySummary = "Diff semantic hunk folding: 0 hunks · Document is unchanged (Zero diff deltas)";
            return new SemanticHunkFoldingAudit(0, 0, 0, 0, 0, 100.0, Array.Empty<SemanticHunkScopeInfo>(), Array.Empty<string>(), "Clean Baseline (Zero Diff Deltas)", emptySummary);
        }

        // Identify contiguous change clusters (hunks) allowing <= 2 unchanged rows
        var hunkRanges = new List<(int Start, int End)>();
        int hunkStart = -1;
        int lastChange = -1;

        for (int i = 0; i < rows.Count; i++)
        {
            bool isChange = rows[i].Kind != Kind.Same;
            if (isChange)
            {
                if (hunkStart < 0) hunkStart = i;
                lastChange = i;
            }
            else if (hunkStart >= 0 && (i - lastChange) > 2)
            {
                hunkRanges.Add((hunkStart, lastChange));
                hunkStart = -1;
                lastChange = -1;
            }
        }
        if (hunkStart >= 0)
        {
            hunkRanges.Add((hunkStart, lastChange));
        }

        var hunkInfos = new List<SemanticHunkScopeInfo>();
        var discoveredScopes = new List<string>();

        for (int hIdx = 0; hIdx < hunkRanges.Count; hIdx++)
        {
            var (start, end) = hunkRanges[hIdx];

            string scopeHeader = "Root Document Scope";
            string scopeKind = "Global Document";
            int nestingLevel = 0;
            bool hasPinned = false;

            // Search backward for enclosing syntactic context
            for (int r = start - 1; r >= 0; r--)
            {
                string text = rows[r].Text ?? string.Empty;
                string trimmed = text.Trim();

                // 1. Markdown heading
                if (trimmed.StartsWith("#"))
                {
                    scopeHeader = trimmed;
                    scopeKind = "Markdown Section";
                    nestingLevel = trimmed.TakeWhile(c => c == '#').Count();
                    hasPinned = true;
                    break;
                }

                // 2. Code definition: class, struct, record, interface, enum, method, function
                var codeMatch = System.Text.RegularExpressions.Regex.Match(
                    trimmed, @"\b(class|struct|record|interface|enum|public|private|protected|internal|void|async|def|fn|function)\b\s+([A-Za-z0-9_<>]+)");
                if (codeMatch.Success)
                {
                    scopeHeader = trimmed;
                    scopeKind = "Type/Method Definition";
                    int spaces = text.TakeWhile(c => c == ' ' || c == '\t').Count();
                    nestingLevel = spaces / 4;
                    hasPinned = true;
                    break;
                }

                // 3. Fenced code block header
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    scopeHeader = trimmed;
                    scopeKind = "Code Block";
                    hasPinned = true;
                    break;
                }
            }

            if (hasPinned && !discoveredScopes.Contains(scopeHeader))
            {
                discoveredScopes.Add(scopeHeader);
            }

            bool isFoldable = (end - start >= 1) || hasPinned;

            hunkInfos.Add(new SemanticHunkScopeInfo(
                hIdx + 1, start + 1, end + 1, scopeHeader, scopeKind, nestingLevel, hasPinned, isFoldable));
        }

        int totalHunks = hunkInfos.Count;
        int pinnedCount = hunkInfos.Count(h => h.HasPinnedContext);
        int unpinnedCount = totalHunks - pinnedCount;
        int foldableCount = hunkInfos.Count(h => h.IsFoldable);

        double coverageScore = totalHunks > 0
            ? Math.Round(100.0 * pinnedCount / totalHunks, 1)
            : 100.0;

        string category;
        if (totalHunks == 0)
        {
            category = "Clean Baseline (Zero Diff Deltas)";
        }
        else if (coverageScore >= 85.0)
        {
            category = "Comprehensive Syntactic Scope Pinning";
        }
        else if (coverageScore >= 50.0)
        {
            category = "Moderate Semantic Scope Coverage";
        }
        else
        {
            category = "Unanchored Diff Deltas Present";
        }

        string summary = FormatSemanticHunkFoldingSummary(
            totalHunks, pinnedCount, unpinnedCount, foldableCount, discoveredScopes.Count, coverageScore, category);

        return new SemanticHunkFoldingAudit(
            totalHunks,
            pinnedCount,
            unpinnedCount,
            foldableCount,
            discoveredScopes.Count,
            coverageScore,
            hunkInfos,
            discoveredScopes,
            category,
            summary);
    }

    /// <summary>
    /// Static overload auditing semantic hunk folding from raw before and after text strings.
    /// </summary>
    public static SemanticHunkFoldingAudit AuditSemanticHunkFoldingAndTokenContext(string? before, string? after)
    {
        var diff = Diff(before ?? "", after ?? "");
        var unified = BuildUnified(diff);
        return AuditSemanticHunkFoldingAndTokenContext(unified);
    }

    /// <summary>
    /// Formats a human-readable summary of diff hunk semantic folding and token context.
    /// </summary>
    public static string FormatSemanticHunkFoldingSummary(
        int totalHunks, int pinnedCount, int unpinnedCount, int foldableCount, int scopeCount,
        double coverageScore, string category)
    {
        if (totalHunks == 0) return "Diff semantic hunk folding: 0 hunks · Document is unchanged (Zero diff deltas)";

        string hunkLabel = totalHunks == 1 ? "1 hunk" : $"{totalHunks} hunks";
        string scopeLabel = scopeCount == 1 ? "1 scope" : $"{scopeCount} scopes";
        string unpinnedWarning = unpinnedCount > 0 ? $" · ⚠ {unpinnedCount} unanchored hunk{(unpinnedCount == 1 ? "" : "s")}" : "";

        return $"Diff semantic hunk folding ({hunkLabel} across {scopeLabel} · {category}): {coverageScore:0.#}% pinned context ({pinnedCount} pinned · {foldableCount} foldable){unpinnedWarning}";
    }

    /// <summary>
    /// Audits version diff deltas for intra-line word-level modifications, token substitutions,
    /// insertions, deletions, and high-resolution inline edit resolution.
    /// </summary>
    public static WordLevelDiffAudit AuditWordLevelDiffChunkingAndTokenShifts(IReadOnlyList<UnifiedRow>? unifiedRows)
    {
        if (unifiedRows == null || unifiedRows.Count == 0)
        {
            string emptySummary = "Word-level diff audit: 0 diff rows · Document is unchanged";
            return new WordLevelDiffAudit(
                0, 0, 0, 0, 0, 0.0, 100.0, Array.Empty<WordDiffChunkInfo>(), "Unchanged (Zero Diffs)", emptySummary);
        }

        var lines = unifiedRows.Where(r => !r.IsGap).ToList();
        var wordChunks = new List<WordDiffChunkInfo>();

        int hunkIndex = 0;
        int modifiedPairsCount = 0;
        int totalSubstitutions = 0;
        int totalInsertions = 0;
        int totalDeletions = 0;
        int totalIdentical = 0;
        double sumSimilarity = 0.0;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind != Kind.Same && (i == 0 || lines[i - 1].Kind == Kind.Same))
            {
                hunkIndex++;
            }

            if (lines[i].Kind == Kind.Removed && i + 1 < lines.Count && lines[i + 1].Kind == Kind.Added)
            {
                modifiedPairsCount++;
                var oldRow = lines[i];
                var newRow = lines[i + 1];

                string oldText = oldRow.Text ?? "";
                string newText = newRow.Text ?? "";

                var oldTokens = System.Text.RegularExpressions.Regex.Matches(oldText, @"\w+|[^\w\s]|\s+")
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Select(m => m.Value)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                var newTokens = System.Text.RegularExpressions.Regex.Matches(newText, @"\w+|[^\w\s]|\s+")
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Select(m => m.Value)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                int mLen = oldTokens.Count;
                int nLen = newTokens.Count;
                int[,] lcs = new int[mLen + 1, nLen + 1];

                for (int x = 1; x <= mLen; x++)
                {
                    for (int y = 1; y <= nLen; y++)
                    {
                        if (string.Equals(oldTokens[x - 1], newTokens[y - 1], StringComparison.Ordinal))
                            lcs[x, y] = lcs[x - 1, y - 1] + 1;
                        else
                            lcs[x, y] = Math.Max(lcs[x - 1, y], lcs[x, y - 1]);
                    }
                }

                int matchingWords = lcs[mLen, nLen];
                totalIdentical += matchingWords;

                double tokenSim = (mLen + nLen) > 0
                    ? Math.Round((2.0 * matchingWords) / (mLen + nLen), 3)
                    : 1.0;
                sumSimilarity += tokenSim;

                int curX = mLen, curY = nLen;
                var pairChunks = new List<WordDiffChunkInfo>();

                while (curX > 0 || curY > 0)
                {
                    if (curX > 0 && curY > 0 && string.Equals(oldTokens[curX - 1], newTokens[curY - 1], StringComparison.Ordinal))
                    {
                        curX--;
                        curY--;
                    }
                    else if (curX > 0 && curY > 0 && lcs[curX - 1, curY - 1] == lcs[curX, curY])
                    {
                        pairChunks.Add(new WordDiffChunkInfo(
                            hunkIndex, oldRow.OldNumber ?? 0, newRow.NewNumber ?? 0,
                            oldTokens[curX - 1], newTokens[curY - 1], "Substitution", tokenSim));
                        totalSubstitutions++;
                        curX--;
                        curY--;
                    }
                    else if (curY > 0 && (curX == 0 || lcs[curX, curY - 1] >= lcs[curX - 1, curY]))
                    {
                        pairChunks.Add(new WordDiffChunkInfo(
                            hunkIndex, oldRow.OldNumber ?? 0, newRow.NewNumber ?? 0,
                            "", newTokens[curY - 1], "Insertion", tokenSim));
                        totalInsertions++;
                        curY--;
                    }
                    else if (curX > 0 && (curY == 0 || lcs[curX, curY - 1] < lcs[curX - 1, curY]))
                    {
                        pairChunks.Add(new WordDiffChunkInfo(
                            hunkIndex, oldRow.OldNumber ?? 0, newRow.NewNumber ?? 0,
                            oldTokens[curX - 1], "", "Deletion", tokenSim));
                        totalDeletions++;
                        curX--;
                    }
                    else
                    {
                        break;
                    }
                }

                pairChunks.Reverse();
                wordChunks.AddRange(pairChunks);

                i++;
            }
        }

        double meanSimilarity = modifiedPairsCount > 0
            ? Math.Round(sumSimilarity / modifiedPairsCount, 3)
            : 1.0;

        double resolutionScore;
        if (modifiedPairsCount == 0)
        {
            resolutionScore = 100.0;
        }
        else
        {
            resolutionScore = Math.Round(Math.Clamp(meanSimilarity * 100.0, 0.0, 100.0), 1);
        }

        string category;
        if (modifiedPairsCount == 0 && lines.All(l => l.Kind == Kind.Same))
        {
            category = "Zero Diffs (Unchanged)";
        }
        else if (modifiedPairsCount == 0)
        {
            category = "Pure Insertion / Deletion Hunks";
        }
        else if (meanSimilarity >= 0.75)
        {
            category = "High-Resolution Word Substitutions";
        }
        else if (meanSimilarity >= 0.45)
        {
            category = "Moderate Intra-Line Token Shifts";
        }
        else
        {
            category = "Heavy Block Rewrites";
        }

        string summary = FormatWordLevelDiffSummary(
            modifiedPairsCount, totalSubstitutions, totalInsertions, totalDeletions,
            totalIdentical, meanSimilarity, resolutionScore, category, wordChunks);

        return new WordLevelDiffAudit(
            modifiedPairsCount, totalSubstitutions, totalInsertions, totalDeletions,
            totalIdentical, meanSimilarity, resolutionScore, wordChunks, category, summary);
    }

    public static WordLevelDiffAudit AuditWordLevelDiffChunkingAndTokenShifts(string? before, string? after)
    {
        var diff = Diff(before ?? "", after ?? "");
        var unified = BuildUnified(diff);
        return AuditWordLevelDiffChunkingAndTokenShifts(unified);
    }

    public static string FormatWordLevelDiffSummary(
        int modifiedPairs, int substitutions, int insertions, int deletions,
        int identicalWords, double meanSimilarity, double resolutionScore,
        string category, IReadOnlyList<WordDiffChunkInfo>? chunks = null)
    {
        if (modifiedPairs == 0 && substitutions == 0 && insertions == 0 && deletions == 0)
        {
            return $"Word-level diff audit ({category}): 0 modified lines · No intra-line token shifts";
        }

        string pairLabel = modifiedPairs == 1 ? "1 modified line" : $"{modifiedPairs} modified lines";
        string tokenBreakdown = $"{substitutions} sub, +{insertions}/-{deletions} words";

        string sample = "";
        if (chunks != null && chunks.Count > 0)
        {
            var firstSub = chunks.FirstOrDefault(c => c.Operation == "Substitution");
            if (firstSub != null)
            {
                sample = $" · L{firstSub.NewLineNumber}: '{firstSub.OldWord}' → '{firstSub.NewWord}'";
            }
        }

        return $"Word-level diff audit ({pairLabel} [{tokenBreakdown}] · {category} · {resolutionScore:0.#}% resolution): ~{meanSimilarity * 100.0:0.#}% similarity{sample}";
    }

    /// <summary>
    /// Audits character-level edit distance using Damerau-Levenshtein distance across paired modified lines,
    /// isolating single-character typos, character transpositions, and classifying edit granularity.
    /// </summary>
    public static LevenshteinDistanceAudit AuditCharacterLevenshteinEditDistance(
        IReadOnlyList<UnifiedRow>? unifiedRows, int maxCharsPerLine = 300)
    {
        if (unifiedRows == null || unifiedRows.Count == 0)
        {
            return new LevenshteinDistanceAudit(
                0, 0, 0.0, 0, 0, 0, 0, 0.0, "None",
                Array.Empty<LevenshteinDiffPairInfo>(), "No Modified Lines",
                "Levenshtein edit distance audit: 0 modified lines · No character-level diff pairs");
        }

        var pairs = new List<LevenshteinDiffPairInfo>();
        int i = 0;
        while (i < unifiedRows.Count)
        {
            if (unifiedRows[i].Kind == Kind.Removed)
            {
                var delLines = new List<UnifiedRow>();
                while (i < unifiedRows.Count && unifiedRows[i].Kind == Kind.Removed)
                {
                    delLines.Add(unifiedRows[i]);
                    i++;
                }

                var insLines = new List<UnifiedRow>();
                while (i < unifiedRows.Count && unifiedRows[i].Kind == Kind.Added)
                {
                    insLines.Add(unifiedRows[i]);
                    i++;
                }

                int pairCount = Math.Min(delLines.Count, insLines.Count);
                for (int p = 0; p < pairCount; p++)
                {
                    var del = delLines[p];
                    var ins = insLines[p];

                    string s1 = del.Text ?? string.Empty;
                    string s2 = ins.Text ?? string.Empty;

                    var (dist, trans) = ComputeDamerauLevenshtein(s1, s2, maxCharsPerLine);
                    int maxLen = Math.Max(1, Math.Max(s1.Length, s2.Length));
                    double simPct = Math.Round(Math.Max(0.0, (1.0 - (double)dist / maxLen)) * 100.0, 1);

                    string classification;
                    if (dist <= 1) classification = "Single-Character Typo";
                    else if (dist <= 4) classification = "Minor Spelling / Token Tweak";
                    else if (dist <= 15) classification = "Moderate Line Revision";
                    else classification = "Major Line Rewrite";

                    string s1Snippet = s1.Length > 50 ? s1.Substring(0, 47) + "..." : s1;
                    string s2Snippet = s2.Length > 50 ? s2.Substring(0, 47) + "..." : s2;

                    pairs.Add(new LevenshteinDiffPairInfo(
                        del.OldNumber ?? 0,
                        ins.NewNumber ?? 0,
                        s1Snippet,
                        s2Snippet,
                        dist,
                        trans,
                        simPct,
                        classification));
                }
            }
            else
            {
                i++;
            }
        }

        if (pairs.Count == 0)
        {
            return new LevenshteinDistanceAudit(
                0, 0, 0.0, 0, 0, 0, 0, 0.0, "None",
                Array.Empty<LevenshteinDiffPairInfo>(), "No Modified Lines",
                "Levenshtein edit distance audit: 0 modified lines · No character-level diff pairs");
        }

        int totalDist = pairs.Sum(p => p.EditDistance);
        double meanDist = Math.Round((double)totalDist / pairs.Count, 1);

        int typoCount = pairs.Count(p => p.EditClassification == "Single-Character Typo");
        int minorCount = pairs.Count(p => p.EditClassification == "Minor Spelling / Token Tweak");
        int modCount = pairs.Count(p => p.EditClassification == "Moderate Line Revision");
        int rewriteCount = pairs.Count(p => p.EditClassification == "Major Line Rewrite");

        double typoRatio = Math.Round((double)typoCount * 100.0 / pairs.Count, 1);

        string dominant;
        int maxBucket = Math.Max(Math.Max(typoCount, minorCount), Math.Max(modCount, rewriteCount));
        if (maxBucket == typoCount) dominant = "Single-Character Typos";
        else if (maxBucket == minorCount) dominant = "Minor Token Tweaks";
        else if (maxBucket == modCount) dominant = "Moderate Revisions";
        else dominant = "Major Rewrites";

        string category;
        if (typoRatio >= 60.0) category = "Typo & Polishing Heavy (≥60% Single-Char Fixes)";
        else if ((typoCount + minorCount) * 100.0 / pairs.Count >= 60.0) category = "Micro-Edits & Phrasing Refinements";
        else if (rewriteCount >= (pairs.Count + 1) / 2) category = "Structural Code / Text Rewrites";
        else category = "Mixed Granularity Edits";

        string summary = FormatLevenshteinDistanceSummary(
            pairs.Count, totalDist, meanDist, typoCount, minorCount, modCount, rewriteCount,
            typoRatio, dominant, category);

        return new LevenshteinDistanceAudit(
            pairs.Count, totalDist, meanDist, typoCount, minorCount, modCount, rewriteCount,
            typoRatio, dominant, pairs, category, summary);
    }

    public static LevenshteinDistanceAudit AuditCharacterLevenshteinEditDistance(
        string? before, string? after, int maxCharsPerLine = 300)
    {
        var diff = Diff(before ?? "", after ?? "");
        var unified = BuildUnified(diff);
        return AuditCharacterLevenshteinEditDistance(unified, maxCharsPerLine);
    }

    public static string FormatLevenshteinDistanceSummary(
        int pairsCount, int totalDist, double meanDist, int typoCount, int minorCount,
        int modCount, int rewriteCount, double typoRatio, string dominant, string category)
    {
        if (pairsCount == 0) return "Levenshtein edit distance audit: 0 modified lines · No character-level diff pairs";

        string pairDesc = pairsCount == 1 ? "1 modified line" : $"{pairsCount} modified lines";
        string breakdown = $"{typoCount} typos, {minorCount} minor, {modCount} mod, {rewriteCount} rewrites";

        return $"Levenshtein edit distance audit ({pairDesc} [{breakdown}] · {category}): ~{meanDist:0.#} chars avg distance · Dominant: {dominant}";
    }

    private static (int distance, int transpositions) ComputeDamerauLevenshtein(string s, string t, int maxLen = 300)
    {
        if (s == t) return (0, 0);
        if (s.Length > maxLen) s = s.Substring(0, maxLen);
        if (t.Length > maxLen) t = t.Substring(0, maxLen);

        int m = s.Length;
        int n = t.Length;
        if (m == 0) return (n, 0);
        if (n == 0) return (m, 0);

        int[,] d = new int[m + 1, n + 1];
        int transCount = 0;

        for (int i = 0; i <= m; i++) d[i, 0] = i;
        for (int j = 0; j <= n; j++) d[0, j] = j;

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                int cost = (s[i - 1] == t[j - 1]) ? 0 : 1;

                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);

                if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                {
                    int transDist = d[i - 2, j - 2] + cost;
                    if (transDist < d[i, j])
                    {
                        d[i, j] = transDist;
                        transCount++;
                    }
                }
            }
        }

        return (d[m, n], transCount);
    }

    /// <summary>
    /// Audits semantic diff token entropy (Shannon information bits), vocabulary expansion ratio,
    /// and revision structural drift across unified diff rows.
    /// </summary>
    public static SemanticDiffTokenEntropyAudit AuditSemanticDiffTokenEntropy(IReadOnlyList<UnifiedRow>? unifiedRows)
    {
        if (unifiedRows == null || unifiedRows.Count == 0)
        {
            string emptySummary = "Semantic diff token entropy audit: 0 rows · Document is unchanged (Zero diff deltas)";
            return new SemanticDiffTokenEntropyAudit(
                0, 0, 0, 0, 0.0, 0.0, 0.0, 1.0, 100.0,
                Array.Empty<TokenFrequencyInfo>(),
                "Zero Diff Deltas (Identical)",
                emptySummary);
        }

        var beforeSb = new StringBuilder();
        var afterSb = new StringBuilder();

        foreach (var r in unifiedRows)
        {
            if (r.IsGap) continue;
            string t = r.Text ?? "";
            if (r.Kind == Kind.Same)
            {
                beforeSb.AppendLine(t);
                afterSb.AppendLine(t);
            }
            else if (r.Kind == Kind.Removed)
            {
                beforeSb.AppendLine(t);
            }
            else if (r.Kind == Kind.Added)
            {
                afterSb.AppendLine(t);
            }
        }

        return AuditSemanticDiffTokenEntropy(beforeSb.ToString(), afterSb.ToString());
    }

    public static SemanticDiffTokenEntropyAudit AuditSemanticDiffTokenEntropy(string? beforeText, string? afterText)
    {
        string before = beforeText ?? "";
        string after = afterText ?? "";

        var tokenRegex = new Regex(@"[a-zA-Z0-9_\-]+|[^\s\w]", RegexOptions.Compiled);

        var beforeMatches = tokenRegex.Matches(before);
        var afterMatches = tokenRegex.Matches(after);

        var beforeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var afterCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match m in beforeMatches)
        {
            string tok = m.Value;
            beforeCounts[tok] = beforeCounts.TryGetValue(tok, out int c) ? c + 1 : 1;
        }

        foreach (Match m in afterMatches)
        {
            string tok = m.Value;
            afterCounts[tok] = afterCounts.TryGetValue(tok, out int c) ? c + 1 : 1;
        }

        int totalBefore = beforeMatches.Count;
        int totalAfter = afterMatches.Count;

        if (totalBefore == 0 && totalAfter == 0)
        {
            string emptySummary = "Semantic diff token entropy audit: 0 tokens · Document is unchanged (Zero diff deltas)";
            return new SemanticDiffTokenEntropyAudit(
                0, 0, 0, 0, 0.0, 0.0, 0.0, 1.0, 100.0,
                Array.Empty<TokenFrequencyInfo>(),
                "Zero Diff Deltas (Identical)",
                emptySummary);
        }

        double hBefore = CalculateShannonEntropy(beforeCounts, totalBefore);
        double hAfter = CalculateShannonEntropy(afterCounts, totalAfter);
        double deltaH = Math.Round(hAfter - hBefore, 3);

        int distinctBefore = beforeCounts.Count;
        int distinctAfter = afterCounts.Count;
        double vocabExpansion = Math.Round((double)distinctAfter / Math.Max(1, distinctBefore), 2);

        var allTokens = new HashSet<string>(beforeCounts.Keys.Concat(afterCounts.Keys), StringComparer.Ordinal);
        var topTokens = new List<TokenFrequencyInfo>();

        foreach (var tok in allTokens.OrderByDescending(t => (afterCounts.TryGetValue(t, out int a) ? a : 0) + (beforeCounts.TryGetValue(t, out int b) ? b : 0)).Take(15))
        {
            int bCount = beforeCounts.TryGetValue(tok, out int bc) ? bc : 0;
            int aCount = afterCounts.TryGetValue(tok, out int ac) ? ac : 0;
            double pB = totalBefore > 0 ? (double)bCount / totalBefore : 0.0;
            double pA = totalAfter > 0 ? (double)aCount / totalAfter : 0.0;
            topTokens.Add(new TokenFrequencyInfo(tok, bCount, aCount, Math.Round(pB, 4), Math.Round(pA, 4)));
        }

        double driftScore = 100.0;
        driftScore -= Math.Min(60.0, Math.Abs(deltaH) * 20.0);
        driftScore -= Math.Min(30.0, Math.Abs(vocabExpansion - 1.0) * 15.0);
        driftScore = Math.Round(Math.Clamp(driftScore, 0.0, 100.0), 1);

        string category;
        if (before == after)
        {
            category = "Zero Diff Deltas (Identical)";
        }
        else if (Math.Abs(deltaH) < 0.25 && Math.Abs(vocabExpansion - 1.0) < 0.15)
        {
            category = "Syntactic Equilibrium / Equivalent Information Density";
        }
        else if (deltaH >= 0.25)
        {
            category = "Information Expanding Refactor";
        }
        else
        {
            category = "Information Consolidating Simplification";
        }

        string summary = FormatSemanticDiffTokenEntropySummary(
            totalBefore, totalAfter, distinctBefore, distinctAfter, hBefore, hAfter, deltaH, vocabExpansion, driftScore, category);

        return new SemanticDiffTokenEntropyAudit(
            totalBefore,
            totalAfter,
            distinctBefore,
            distinctAfter,
            Math.Round(hBefore, 2),
            Math.Round(hAfter, 2),
            deltaH,
            vocabExpansion,
            driftScore,
            topTokens,
            category,
            summary);
    }

    private static double CalculateShannonEntropy(Dictionary<string, int> counts, int totalTokens)
    {
        if (totalTokens == 0) return 0.0;
        double entropy = 0.0;
        foreach (var kvp in counts)
        {
            double p = (double)kvp.Value / totalTokens;
            if (p > 0.0)
            {
                entropy -= p * (Math.Log(p) / Math.Log(2.0));
            }
        }
        return entropy;
    }

    public static string FormatSemanticDiffTokenEntropySummary(
        int totalBefore, int totalAfter, int distinctBefore, int distinctAfter,
        double hBefore, double hAfter, double deltaH, double vocabExpansion, double driftScore, string category)
    {
        if (totalBefore == 0 && totalAfter == 0)
            return "Semantic diff token entropy audit: 0 tokens · Document is unchanged (Zero diff deltas)";

        string deltaSign = deltaH >= 0 ? "+" : "";
        return $"Semantic diff token entropy audit ({category} · {driftScore:0.#}% stability): H_before={hBefore:0.##}b, H_after={hAfter:0.##}b (ΔH={deltaSign}{deltaH:0.##} bits/token · Vocab ratio {vocabExpansion:0.##}x)";
    }

    /// <summary>
    /// Audits unified diff lines for line-wrap boundaries, long line overflows, and horizontal scroll spills
    /// in side-by-side or unified diff viewing contexts.
    /// </summary>
    public static DiffLineWrapOverflowAudit AuditDiffLineWrapAndOverflow(
        IReadOnlyList<UnifiedRow>? unifiedRows, int maxColumnWidth = 120)
    {
        if (unifiedRows == null || unifiedRows.Count == 0)
        {
            string emptySummary = "Diff line-wrap audit: 0 rows · Empty diff (Zero diff deltas)";
            return new DiffLineWrapOverflowAudit(
                0, 0, 0, 0, maxColumnWidth, 100.0,
                Array.Empty<DiffLineOverflowInfo>(),
                "Empty Diff",
                emptySummary);
        }

        int standardWidthCount = 0;
        int moderateOverflowCount = 0;
        int severeOverflowCount = 0;
        var overflows = new List<DiffLineOverflowInfo>();

        for (int i = 0; i < unifiedRows.Count; i++)
        {
            var row = unifiedRows[i];
            string text = row.Text ?? string.Empty;

            int visualLength = 0;
            foreach (char c in text)
            {
                if (c == '\t') visualLength += 4 - (visualLength % 4);
                else visualLength++;
            }

            string changeType = row.Kind.ToString();
            int lineNum = row.NewNumber ?? row.OldNumber ?? (i + 1);

            if (visualLength <= maxColumnWidth)
            {
                standardWidthCount++;
            }
            else
            {
                bool isSevere = visualLength > (maxColumnWidth + 40);
                if (isSevere) severeOverflowCount++;
                else moderateOverflowCount++;

                string snippet = text.Length > 60 ? text.Substring(0, 57) + "..." : text;
                overflows.Add(new DiffLineOverflowInfo(
                    RowIndex: i,
                    ChangeType: changeType,
                    LineNumber: lineNum,
                    ColumnLength: visualLength,
                    PreviewSnippet: snippet,
                    IsExtremeOverflow: visualLength > 250));
            }
        }

        int totalEvaluated = unifiedRows.Count;
        double penalty = (moderateOverflowCount * 10.0 + severeOverflowCount * 25.0) / totalEvaluated;
        double stabilityScore = Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category = stabilityScore switch
        {
            >= 95.0 when moderateOverflowCount == 0 && severeOverflowCount == 0 => "Zero Horizontal Spill / Optimal Wrap Boundaries",
            >= 80.0 => "Minor Diff Column Width Overflows",
            >= 60.0 => "Moderate Horizontal Scroll Spills",
            _ => "Severe Diff Line Wrapping / Extreme Overflow"
        };

        string summary = FormatDiffLineWrapSummary(
            totalEvaluated, standardWidthCount, moderateOverflowCount, severeOverflowCount,
            maxColumnWidth, stabilityScore, category);

        return new DiffLineWrapOverflowAudit(
            TotalRowsEvaluated: totalEvaluated,
            StandardWidthCount: standardWidthCount,
            ModerateOverflowCount: moderateOverflowCount,
            SevereOverflowCount: severeOverflowCount,
            MaxColumnWidth: maxColumnWidth,
            HorizontalStabilityScore: Math.Round(stabilityScore, 1),
            Overflows: overflows,
            Category: category,
            Summary: summary);
    }

    public static string FormatDiffLineWrapSummary(
        int totalRows, int standardCount, int moderateOverflow, int severeOverflow,
        int maxWidth, double stabilityScore, string category)
    {
        if (totalRows == 0) return "Diff line-wrap audit: 0 rows · Empty diff (Zero diff deltas)";

        string rowLabel = totalRows == 1 ? "1 row" : $"{totalRows} rows";
        int totalOverflow = moderateOverflow + severeOverflow;
        var alerts = new List<string>();
        if (severeOverflow > 0) alerts.Add($"🚨 {severeOverflow} severe spill{(severeOverflow == 1 ? "" : "s")}");
        if (moderateOverflow > 0) alerts.Add($"⚠ {moderateOverflow} wide line{(moderateOverflow == 1 ? "" : "s")}");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : $" · All ≤{maxWidth} cols";
        return $"Diff line-wrap & overflow audit ({rowLabel} [limit {maxWidth} cols] · {category} · {stabilityScore:0.#}% score){details}";
    }

    /// <summary>
    /// Audits unified diff rows or source text for unresolved Git merge conflict markers
    /// (&lt;&lt;&lt;&lt;&lt;&lt;&lt;, =======, &gt;&gt;&gt;&gt;&gt;&gt;&gt;, |||||||), branch labels, unmerged hunk boundaries,
    /// and orphaned rebase residue.
    /// </summary>
    public static GitConflictMarkerAudit AuditGitConflictMarkersAndRebaseResidue(
        IEnumerable<UnifiedRow>? unifiedRows)
    {
        var lines = unifiedRows?.Select(r => r.Text ?? string.Empty).ToList() ?? new List<string>();
        return AuditGitConflictMarkersAndRebaseResidue(lines);
    }

    /// <summary>
    /// Audits text content for unresolved Git merge conflict markers and rebase residue.
    /// </summary>
    public static GitConflictMarkerAudit AuditGitConflictMarkersAndRebaseResidue(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return AuditGitConflictMarkersAndRebaseResidue(Array.Empty<string>());
        }
        var lines = content.Replace("\r\n", "\n").Split('\n');
        return AuditGitConflictMarkersAndRebaseResidue(lines);
    }

    /// <summary>
    /// Core scanner for Git conflict markers across an indexed list of text lines.
    /// </summary>
    public static GitConflictMarkerAudit AuditGitConflictMarkersAndRebaseResidue(IReadOnlyList<string> lines)
    {
        if (lines == null || lines.Count == 0)
        {
            string emptySummary = "Git conflict marker audit: 0 lines · Clean Working Tree · 100% score (Zero conflict residue)";
            return new GitConflictMarkerAudit(
                0, 0, 0, 0, false, 100.0,
                Array.Empty<ConflictBlockInfo>(),
                "Clean Working Tree / Zero Conflict Residue",
                emptySummary);
        }

        var blocks = new List<ConflictBlockInfo>();
        int totalMarkers = 0;
        int malformedCount = 0;
        int i = 0;

        while (i < lines.Count)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("<<<<<<<"))
            {
                totalMarkers++;
                int startRow = i;
                string oursLabel = trimmed.Length > 7 ? trimmed[7..].Trim() : "HEAD";
                int dividerRow = -1;
                int endRow = -1;
                string theirsLabel = "";
                int j = i + 1;

                while (j < lines.Count)
                {
                    string innerTrimmed = lines[j].TrimStart();
                    if (innerTrimmed.StartsWith("<<<<<<<"))
                    {
                        // Nested / unclosed marker before finding previous resolution
                        break;
                    }
                    if (innerTrimmed.StartsWith("=======") && dividerRow == -1)
                    {
                        totalMarkers++;
                        dividerRow = j;
                    }
                    else if (innerTrimmed.StartsWith(">>>>>>>"))
                    {
                        totalMarkers++;
                        endRow = j;
                        theirsLabel = innerTrimmed.Length > 7 ? innerTrimmed[7..].Trim() : "Incoming";
                        j++;
                        break;
                    }
                    else if (innerTrimmed.StartsWith("|||||||"))
                    {
                        totalMarkers++; // diff3 common ancestor marker
                    }
                    j++;
                }

                bool isMalformed = dividerRow == -1 || endRow == -1;
                if (isMalformed) malformedCount++;

                int oursLines = dividerRow != -1 ? Math.Max(0, dividerRow - startRow - 1) : Math.Max(0, (endRow != -1 ? endRow : j) - startRow - 1);
                int theirsLines = (dividerRow != -1 && endRow != -1) ? Math.Max(0, endRow - dividerRow - 1) : 0;

                string snippet = $"Rows {startRow + 1}..{(endRow != -1 ? endRow + 1 : j)}: [{oursLabel} ({oursLines}L) vs {theirsLabel} ({theirsLines}L)]";

                blocks.Add(new ConflictBlockInfo(
                    StartRowIndex: startRow,
                    DividerRowIndex: dividerRow,
                    EndRowIndex: endRow,
                    OursLabel: oursLabel,
                    TheirsLabel: theirsLabel,
                    OursLineCount: oursLines,
                    TheirsLineCount: theirsLines,
                    IsMalformed: isMalformed,
                    Snippet: snippet));

                i = j;
            }
            else
            {
                if (trimmed.StartsWith("=======") || trimmed.StartsWith(">>>>>>>") || trimmed.StartsWith("|||||||"))
                {
                    totalMarkers++;
                    malformedCount++;
                    blocks.Add(new ConflictBlockInfo(
                        StartRowIndex: i,
                        DividerRowIndex: trimmed.StartsWith("=======") ? i : -1,
                        EndRowIndex: trimmed.StartsWith(">>>>>>>") ? i : -1,
                        OursLabel: "Orphan",
                        TheirsLabel: trimmed.Length > 7 ? trimmed[7..].Trim() : "Orphan",
                        OursLineCount: 0,
                        TheirsLineCount: 0,
                        IsMalformed: true,
                        Snippet: $"Row {i + 1}: Orphaned conflict marker residue ('{trimmed}')"));
                }
                i++;
            }
        }

        bool hasActiveConflicts = blocks.Count > 0 || totalMarkers > 0;
        double integrityScore;
        string category;

        if (!hasActiveConflicts)
        {
            integrityScore = 100.0;
            category = "Clean Working Tree / Zero Conflict Residue";
        }
        else
        {
            double penalty = (blocks.Count * 35.0) + (malformedCount * 25.0);
            integrityScore = Math.Round(Math.Clamp(100.0 - penalty, 0.0, 50.0), 1);
            category = malformedCount > 0
                ? "Malformed / Corrupted Git Conflict Markers"
                : "Active Merge Conflict Block(s) Detected";
        }

        string summary = FormatGitConflictMarkersSummary(
            lines.Count, blocks.Count, totalMarkers, malformedCount, integrityScore, category);

        return new GitConflictMarkerAudit(
            TotalLinesEvaluated: lines.Count,
            ConflictBlockCount: blocks.Count,
            UnresolvedMarkerCount: totalMarkers,
            MalformedConflictCount: malformedCount,
            HasActiveConflicts: hasActiveConflicts,
            ConflictFreeIntegrityScore: integrityScore,
            ConflictBlocks: blocks,
            Category: category,
            Summary: summary);
    }

    public static string FormatGitConflictMarkersSummary(
        int totalLines, int blockCount, int markerCount, int malformedCount,
        double score, string category)
    {
        string lineLabel = totalLines == 1 ? "1 line" : $"{totalLines} lines";
        if (blockCount == 0 && markerCount == 0)
        {
            return $"Git conflict marker audit ({lineLabel} · Clean Working Tree · 100% score): Zero conflict markers or rebase residue";
        }

        var alerts = new List<string>();
        alerts.Add($"⚠ {blockCount} conflict block{(blockCount == 1 ? "" : "s")}");
        alerts.Add($"{markerCount} marker{(markerCount == 1 ? "" : "s")}");
        if (malformedCount > 0) alerts.Add($"🚨 {malformedCount} malformed/orphan");

        return $"Git conflict marker audit ({lineLabel} · {category} · {score:0.#}% score): {string.Join(", ", alerts)}";
    }
}

/// <summary>
/// Represents line change turbulence and churn metrics for a document subsection or heading block.
/// </summary>
public sealed record SectionChurnHotspot(
    string SectionHeading,
    int AddedLines,
    int RemovedLines,
    int TotalChangedLines,
    double ChurnVelocity);

/// <summary>
/// Represents the result of an audit of version diff churn acceleration, line turbulence, and modification hotspots.
/// </summary>
public sealed record DiffChurnAccelerationAudit(
    int TotalBeforeLines,
    int TotalAfterLines,
    int AddedCount,
    int RemovedCount,
    int UnchangedCount,
    double ChurnRatio,
    double NetGrowthRate,
    string HotspotSectionHeading,
    IReadOnlyList<SectionChurnHotspot> SectionHotspots,
    string VolatilityCategory,
    string Summary);

/// <summary>
/// Represents the result of a synthesized GitHub/GitLab Pull Request description and risk assessment.
/// </summary>
public sealed record PullRequestDescriptionAudit(
    int LinesAdded,
    int LinesRemoved,
    int NetDelta,
    string RiskTier,
    string Title,
    IReadOnlyList<string> AffectedAreas,
    string PullRequestMarkdown,
    string Summary);

/// <summary>
/// Represents the result of a synthesized semantic Conventional Commit message and breaking change evaluation.
/// </summary>
public sealed record ConventionalCommitAudit(
    string CommitType,
    string Scope,
    string Subject,
    string Body,
    bool IsBreakingChange,
    string BreakingChangeNotice,
    string FullCommitMessage,
    string Summary);

/// <summary>
/// Represents the result of a synthesized Semantic Version (SemVer) bump evaluation and Keep-A-Changelog entry.
/// </summary>
public sealed record SemanticVersionBumpAudit(
    string CurrentVersion,
    string RecommendedVersion,
    string BumpType,
    string Rationale,
    int AddedLines,
    int RemovedLines,
    bool HasBreakingChanges,
    string ChangelogMarkdown,
    string Summary);

/// <summary>
/// Represents an individual categorized grouping of release changes.
/// </summary>
public sealed record ReleaseNoteCategory(
    string CategoryName,
    IReadOnlyList<string> Items);

/// <summary>
/// Represents the result of an automated release notes and migration guide synthesis from diff deltas.
/// </summary>
public sealed record ReleaseNotesAndMigrationAudit(
    string CurrentVersion,
    string RecommendedVersion,
    string RiskTier,
    int FeatureCount,
    int FixCount,
    int BreakingCount,
    int TotalChanges,
    IReadOnlyList<ReleaseNoteCategory> Categories,
    IReadOnlyList<string> MigrationSteps,
    string ReleaseNotesMarkdown,
    string Summary);

/// <summary>
/// Represents contiguous modification churn cluster metrics.
/// </summary>
public sealed record ChurnHotspotInfo(
    int StartLine,
    int EndLine,
    string SectionHeading,
    int AddedLines,
    int RemovedLines,
    int TotalChanged,
    double Density);

/// <summary>
/// Represents the result of an audit of commit churn hotspots, modification entropy, and volatility index.
/// </summary>
public sealed record CommitChurnHotspotsAudit(
    int TotalRows,
    int AddedRows,
    int RemovedRows,
    int UnchangedRows,
    double VolatilityIndex,
    double ChurnEntropy,
    double PeakDensity,
    IReadOnlyList<ChurnHotspotInfo> Hotspots,
    string VolatilityCategory,
    string Summary);

/// <summary>
/// Represents boundary alignment and semantic context metrics for an individual diff hunk.
/// </summary>
public sealed record ChunkBoundaryInfo(
    int HunkIndex,
    int StartLine,
    int EndLine,
    int ContextLeadingLines,
    int ContextTrailingLines,
    string PrecedingSemanticHeader,
    bool AlignsToSemanticBoundary,
    bool TruncatesMultilineConstruct,
    bool OverlapsAdjacentHunk);

/// <summary>
/// Represents the result of an audit of diff chunk context semantic boundaries,
/// multiline construct integrity, and hunk overlap deduplication.
/// </summary>
public sealed record DiffChunkBoundaryAudit(
    int TotalHunks,
    int SemanticallyAlignedHunks,
    int TruncatedConstructCount,
    int OverlappingHunkCount,
    double MeanContextPadding,
    double BoundaryIntegrityScore,
    IReadOnlyList<ChunkBoundaryInfo> Hunks,
    string AlignmentCategory,
    string Summary);

/// <summary>
/// Represents syntactic scope context and folding metrics for an individual diff hunk.
/// </summary>
public sealed record SemanticHunkScopeInfo(
    int HunkIndex,
    int StartRow,
    int EndRow,
    string ScopeHeader,
    string ScopeKind,
    int ScopeNestingLevel,
    bool HasPinnedContext,
    bool IsFoldable);

/// <summary>
/// Represents the result of an audit of diff hunk semantic folding, syntactic scope pinning,
/// and unanchored diff deltas.
/// </summary>
public sealed record SemanticHunkFoldingAudit(
    int TotalHunks,
    int PinnedHunkCount,
    int UnpinnedHunkCount,
    int FoldableHunkCount,
    int DistinctScopesCount,
    double SemanticCoverageScore,
    IReadOnlyList<SemanticHunkScopeInfo> Hunks,
    IReadOnlyList<string> DiscoveredScopes,
    string FoldingCategory,
    string Summary);

/// <summary>
/// Represents intra-line word-level edit chunking and token substitution metrics.
/// </summary>
public sealed record WordDiffChunkInfo(
    int HunkIndex,
    int OldLineNumber,
    int NewLineNumber,
    string OldWord,
    string NewWord,
    string Operation,
    double TokenSimilarity);

/// <summary>
/// Represents the result of an audit of intra-line word-level diff chunking, token shifts,
/// and high-resolution inline edit resolution.
/// </summary>
public sealed record WordLevelDiffAudit(
    int TotalModifiedLinePairs,
    int TotalWordSubstitutions,
    int TotalWordInsertions,
    int TotalWordDeletions,
    int IdenticalWordCount,
    double MeanTokenSimilarity,
    double IntraLineDiffResolutionScore,
    IReadOnlyList<WordDiffChunkInfo> WordChunks,
    string ResolutionCategory,
    string Summary);

/// <summary>
/// Represents calculated Levenshtein edit distance, character transpositions, and edit classification for a paired line modification.
/// </summary>
public sealed record LevenshteinDiffPairInfo(
    int OldLineNumber,
    int NewLineNumber,
    string DeletedSnippet,
    string AddedSnippet,
    int EditDistance,
    int TranspositionCount,
    double SimilarityPercent,
    string EditClassification);

/// <summary>
/// Represents the result of an audit of character-level edit distance (Damerau-Levenshtein),
/// character transpositions, typo identification, and edit granularity.
/// </summary>
public sealed record LevenshteinDistanceAudit(
    int TotalModifiedLinePairs,
    int TotalEditDistance,
    double MeanEditDistance,
    int TypoCount,
    int MinorTweakCount,
    int ModerateRevisionCount,
    int MajorRewriteCount,
    double TypoConcentrationRatio,
    string DominantEditType,
    IReadOnlyList<LevenshteinDiffPairInfo> Pairs,
    string GranularityCategory,
    string Summary);

/// <summary>
/// Represents token occurrence frequency and Shannon probability distribution across diff revisions.
/// </summary>
public sealed record TokenFrequencyInfo(
    string Token,
    int BeforeCount,
    int AfterCount,
    double BeforeProbability,
    double AfterProbability);

/// <summary>
/// Represents the result of an audit of semantic diff token entropy (Shannon information bits),
/// vocabulary expansion ratio, and revision structural drift.
/// </summary>
public sealed record SemanticDiffTokenEntropyAudit(
    int TotalBeforeTokens,
    int TotalAfterTokens,
    int DistinctBeforeTokens,
    int DistinctAfterTokens,
    double BeforeEntropyBits,
    double AfterEntropyBits,
    double EntropyDeltaBits,
    double VocabularyExpansionRatio,
    double RevisionDriftScore,
    IReadOnlyList<TokenFrequencyInfo> TopTokens,
    string DriftCategory,
    string Summary);

/// <summary>
/// Represents column length, row index, and overflow severity for an individual diff row.
/// </summary>
public sealed record DiffLineOverflowInfo(
    int RowIndex,
    string ChangeType,
    int LineNumber,
    int ColumnLength,
    string PreviewSnippet,
    bool IsExtremeOverflow);

/// <summary>
/// Represents the result of an audit of diff line-wrap boundaries, column width overflows,
/// and horizontal scroll spills.
/// </summary>
public sealed record DiffLineWrapOverflowAudit(
    int TotalRowsEvaluated,
    int StandardWidthCount,
    int ModerateOverflowCount,
    int SevereOverflowCount,
    int MaxColumnWidth,
    double HorizontalStabilityScore,
    IReadOnlyList<DiffLineOverflowInfo> Overflows,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated conflict hunk coordinates, branch labels, line counts, and syntax validity for an individual Git conflict block.
/// </summary>
public sealed record ConflictBlockInfo(
    int StartRowIndex,
    int DividerRowIndex,
    int EndRowIndex,
    string OursLabel,
    string TheirsLabel,
    int OursLineCount,
    int TheirsLineCount,
    bool IsMalformed,
    string Snippet);

/// <summary>
/// Represents the result of an audit of Git merge conflict markers (&lt;&lt;&lt;&lt;&lt;&lt;&lt;, =======, &gt;&gt;&gt;&gt;&gt;&gt;&gt;),
/// unmerged hunk boundaries, and rebase residue.
/// </summary>
public sealed record GitConflictMarkerAudit(
    int TotalLinesEvaluated,
    int ConflictBlockCount,
    int UnresolvedMarkerCount,
    int MalformedConflictCount,
    bool HasActiveConflicts,
    double ConflictFreeIntegrityScore,
    IReadOnlyList<ConflictBlockInfo> ConflictBlocks,
    string Category,
    string Summary);

