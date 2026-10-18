using System.Text;

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
            type = "feat";
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
}


