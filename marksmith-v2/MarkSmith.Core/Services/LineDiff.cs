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
}
