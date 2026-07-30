using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MdToPdf.Services;

/// <summary>
/// The type of change for a diff segment.
/// </summary>
public enum DiffChangeType { Unchanged, Added, Deleted, Modified }

/// <summary>
/// A single segment in a redline diff — one line with its change classification.
/// </summary>
public sealed record DiffSegment(string Text, DiffChangeType ChangeType, int? LeftLine, int? RightLine);

/// <summary>
/// The result of a visual document diff operation.
/// </summary>
public sealed record VisualDiffResult(
    IReadOnlyList<DiffSegment> Segments,
    int AddedCount,
    int DeletedCount,
    int UnchangedCount,
    string Html)
{
    public int TotalChanges => AddedCount + DeletedCount;
    public bool HasChanges => TotalChanges > 0;
}

/// <summary>
/// Visual Redline Document Diff Engine (D5): produces a side-by-side or inline redline comparison
/// between two Markdown documents (or rendered text). Highlights additions (green), deletions (red),
/// and modifications (yellow) with line-level granularity.
///
/// Uses an LCS (Longest Common Subsequence) algorithm for optimal alignment, then classifies
/// each line as added, deleted, or unchanged. Output is available as structured segments
/// (for UI binding) and as self-contained HTML (for WebView2 preview).
/// </summary>
public static class VisualDocumentDiffService
{
    /// <summary>
    /// Computes a visual redline diff between two Markdown documents.
    /// Returns structured segments + rendered HTML with inline redline markup.
    /// </summary>
    public static VisualDiffResult ComputeDiff(string left, string right)
    {
        var leftLines = SplitLines(left);
        var rightLines = SplitLines(right);

        var lcs = ComputeLcs(leftLines, rightLines);
        var segments = BuildSegments(leftLines, rightLines, lcs);

        int added = segments.Count(s => s.ChangeType == DiffChangeType.Added);
        int deleted = segments.Count(s => s.ChangeType == DiffChangeType.Deleted);
        int unchanged = segments.Count(s => s.ChangeType == DiffChangeType.Unchanged);

        var html = RenderHtml(segments);

        return new VisualDiffResult(segments, added, deleted, unchanged, html);
    }

    /// <summary>
    /// Computes a word-level inline diff for two short texts (useful for showing
    /// exactly what changed within a modified line).
    /// </summary>
    public static string InlineWordDiff(string left, string right)
    {
        var leftWords = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightWords = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var lcs = ComputeLcs(leftWords, rightWords);
        var sb = new StringBuilder();

        int li = 0, ri = 0, lcsIdx = 0;
        while (li < leftWords.Length || ri < rightWords.Length)
        {
            if (lcsIdx < lcs.Count && li < leftWords.Length && ri < rightWords.Length
                && leftWords[li] == lcs[lcsIdx] && rightWords[ri] == lcs[lcsIdx])
            {
                sb.Append(leftWords[li]).Append(' ');
                li++; ri++; lcsIdx++;
            }
            else if (li < leftWords.Length && (lcsIdx >= lcs.Count || leftWords[li] != lcs[lcsIdx]))
            {
                sb.Append("<del>").Append(leftWords[li]).Append("</del> ");
                li++;
            }
            else if (ri < rightWords.Length)
            {
                sb.Append("<ins>").Append(rightWords[ri]).Append("</ins> ");
                ri++;
            }
        }

        return sb.ToString().Trim();
    }

    // ---- LCS algorithm --------------------------------------------------------------------------

    private static List<string> ComputeLcs(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        // Backtrack to find the LCS.
        var result = new List<string>();
        int x = m, y = n;
        while (x > 0 && y > 0)
        {
            if (a[x - 1] == b[y - 1])
            {
                result.Add(a[x - 1]);
                x--; y--;
            }
            else if (dp[x - 1, y] > dp[x, y - 1])
                x--;
            else
                y--;
        }

        result.Reverse();
        return result;
    }

    // ---- Segment building -----------------------------------------------------------------------

    private static List<DiffSegment> BuildSegments(string[] left, string[] right, List<string> lcs)
    {
        var segments = new List<DiffSegment>();
        int li = 0, ri = 0, lcsIdx = 0;

        while (li < left.Length || ri < right.Length)
        {
            if (lcsIdx < lcs.Count)
            {
                // Emit deletions from left until we hit the next LCS element.
                while (li < left.Length && left[li] != lcs[lcsIdx])
                {
                    segments.Add(new DiffSegment(left[li], DiffChangeType.Deleted, li, null));
                    li++;
                }
                // Emit additions from right until we hit the next LCS element.
                while (ri < right.Length && right[ri] != lcs[lcsIdx])
                {
                    segments.Add(new DiffSegment(right[ri], DiffChangeType.Added, null, ri));
                    ri++;
                }
                // Emit the common line.
                if (li < left.Length && ri < right.Length)
                {
                    segments.Add(new DiffSegment(left[li], DiffChangeType.Unchanged, li, ri));
                    li++; ri++; lcsIdx++;
                }
            }
            else
            {
                // No more LCS elements — remaining lines are all changes.
                while (li < left.Length)
                {
                    segments.Add(new DiffSegment(left[li], DiffChangeType.Deleted, li, null));
                    li++;
                }
                while (ri < right.Length)
                {
                    segments.Add(new DiffSegment(right[ri], DiffChangeType.Added, null, ri));
                    ri++;
                }
            }
        }

        return segments;
    }

    // ---- HTML rendering -------------------------------------------------------------------------

    /// <summary>
    /// Renders diff segments as a self-contained HTML page with redline styling
    /// (green = added, red = deleted, neutral = unchanged).
    /// </summary>
    private static string RenderHtml(IReadOnlyList<DiffSegment> segments)
    {
        var sb = new StringBuilder();
        sb.Append(@"<!DOCTYPE html><html><head><meta charset='utf-8'><style>
body { font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 13px; padding: 16px; background: #1e1e1e; color: #d4d4d4; }
.diff-line { padding: 2px 8px; white-space: pre-wrap; border-left: 3px solid transparent; margin: 1px 0; }
.diff-added { background: rgba(35,134,54,0.2); border-left-color: #3fb950; color: #aff5b4; }
.diff-deleted { background: rgba(218,54,51,0.15); border-left-color: #f85149; color: #ffa198; text-decoration: line-through; }
.diff-unchanged { color: #8b949e; }
.diff-header { font-weight: bold; color: #58a6ff; margin: 16px 0 8px; font-size: 14px; }
ins { background: rgba(35,134,54,0.4); text-decoration: none; }
del { background: rgba(218,54,51,0.3); }
</style></head><body>");

        sb.Append("<div class='diff-header'>Redline Diff — ");
        int adds = segments.Count(s => s.ChangeType == DiffChangeType.Added);
        int dels = segments.Count(s => s.ChangeType == DiffChangeType.Deleted);
        sb.Append($"+{adds} additions, -{dels} deletions</div>");

        foreach (var seg in segments)
        {
            var cssClass = seg.ChangeType switch
            {
                DiffChangeType.Added => "diff-line diff-added",
                DiffChangeType.Deleted => "diff-line diff-deleted",
                _ => "diff-line diff-unchanged",
            };
            var prefix = seg.ChangeType switch
            {
                DiffChangeType.Added => "+ ",
                DiffChangeType.Deleted => "- ",
                _ => "  ",
            };
            sb.Append($"<div class='{cssClass}'>{prefix}{Escape(seg.Text)}</div>\n");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string[] SplitLines(string text) =>
        (text ?? "").Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
}
