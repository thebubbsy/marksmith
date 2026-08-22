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

    /// <summary>Unified diff of two texts: removed/added/same lines with source and target line numbers.</summary>
    public static List<Line> Diff(string before, string after)
    {
        var a = SplitLines(before);
        var b = SplitLines(after);

        // Trim the common prefix and suffix so the LCS only runs over the changed middle.
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre &&
               a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;

        var aMid = a.AsSpan(pre, a.Length - pre - suf).ToArray();
        var bMid = b.AsSpan(pre, b.Length - pre - suf).ToArray();

        var result = new List<Line>(pre + suf + aMid.Length + bMid.Length);
        for (int i = 0; i < pre; i++) result.Add(new Line(Kind.Same, i + 1, i + 1, a[i]));

        if ((long)aMid.Length * bMid.Length <= MaxLcsCells)
            AppendLcs(result, aMid, bMid, pre, pre);
        else
            AppendReplace(result, aMid, bMid, pre, pre);

        int aTail = a.Length - suf;
        int bTail = b.Length - suf;
        for (int i = 0; i < suf; i++)
            result.Add(new Line(Kind.Same, aTail + i + 1, bTail + i + 1, a[aTail + i]));

        return result;
    }

    private static void AppendLcs(List<Line> result, string[] a, string[] b, int aBase, int bBase)
    {
        // Classic LCS DP (row-optimized) then walk back to emit ops in order.
        int n = a.Length, m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                result.Add(new Line(Kind.Same, aBase + x + 1, bBase + y + 1, a[x]));
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

    public sealed record UnifiedRow(Kind Kind, int? OldNumber, int? NewNumber, string Text, string Prefix)
    {
        public string OldNumberLabel => OldNumber?.ToString() ?? "";
        public string NewNumberLabel => NewNumber?.ToString() ?? "";
        public bool IsRemoved => Kind == Kind.Removed;
        public bool IsAdded => Kind == Kind.Added;
        public bool IsSame => Kind == Kind.Same;
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
}
