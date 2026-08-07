using System.Text;

namespace MarkSmith.Services;

/// <summary>Classification of one redline segment (D5).</summary>
public enum DiffChangeType { Unchanged, Added, Deleted }

/// <summary>A contiguous run of lines with the same change classification.</summary>
public sealed record DiffSegment(DiffChangeType ChangeType, string Text);

/// <summary>The redline comparison result (D5): per-line segments, counts, and a self-contained
/// HTML rendering with <c>diff-added</c> / <c>diff-deleted</c> classes.</summary>
public sealed class DiffResult
{
    public required IReadOnlyList<DiffSegment> Segments { get; init; }
    public bool HasChanges => AddedCount > 0 || DeletedCount > 0;
    public int AddedCount { get; init; }
    public int DeletedCount { get; init; }
    public int UnchangedCount { get; init; }
    public required string Html { get; init; }
}

/// <summary>
/// Visual redline document diff engine (backlog D5): compares two document versions and produces
/// classified line segments (unchanged/added/deleted) plus an HTML redline rendering, and a
/// word-level inline diff (<c>&lt;del&gt;</c>/<c>&lt;ins&gt;</c>) for changed lines.
/// </summary>
public static class VisualDocumentDiffService
{
    public static DiffResult ComputeDiff(string left, string right)
    {
        var lines = LineDiff.Diff(left ?? "", right ?? "");

        var segments = new List<DiffSegment>();
        var current = new List<LineDiff.Line>();
        var currentKind = LineDiff.Kind.Same;

        foreach (var line in lines)
        {
            if (line.Kind != currentKind)
            {
                if (current.Count > 0) segments.Add(Flush(current, currentKind));
                current.Clear();
                currentKind = line.Kind;
            }
            current.Add(line);
        }
        if (current.Count > 0) segments.Add(Flush(current, currentKind));

        var added = lines.Count(l => l.Kind == LineDiff.Kind.Added);
        var deleted = lines.Count(l => l.Kind == LineDiff.Kind.Removed);
        var unchanged = lines.Count - added - deleted;

        return new DiffResult
        {
            Segments = segments,
            AddedCount = added,
            DeletedCount = deleted,
            UnchangedCount = unchanged,
            Html = BuildHtml(segments),
        };
    }

    /// <summary>Word-level inline diff: unchanged words plain, removed words wrapped in
    /// <c>&lt;del&gt;</c>, added words in <c>&lt;ins&gt;</c>.</summary>
    public static string InlineWordDiff(string left, string right)
    {
        var a = Tokenize(left ?? "");
        var b = Tokenize(right ?? "");

        // Word-level LCS on tokens (same guard as the line diff).
        int n = a.Count, m = b.Count;
        if ((long)n * m > 4_000_000) return System.Net.WebUtility.HtmlEncode(left ?? "") + "<ins>" + System.Net.WebUtility.HtmlEncode(right ?? "") + "</ins>";

        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = string.Equals(a[i], b[j]) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var sb = new StringBuilder();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[x], b[y]))
            {
                sb.Append(Escape(a[x]));
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                sb.Append("<del>").Append(Escape(a[x])).Append("</del>");
                x++;
            }
            else
            {
                sb.Append("<ins>").Append(Escape(b[y])).Append("</ins>");
                y++;
            }
        }
        while (x < n) { sb.Append("<del>").Append(Escape(a[x])).Append("</del>"); x++; }
        while (y < m) { sb.Append("<ins>").Append(Escape(b[y])).Append("</ins>"); y++; }
        return sb.ToString();
    }

    private static DiffSegment Flush(List<LineDiff.Line> run, LineDiff.Kind kind)
    {
        var changeType = kind switch
        {
            LineDiff.Kind.Added => DiffChangeType.Added,
            LineDiff.Kind.Removed => DiffChangeType.Deleted,
            _ => DiffChangeType.Unchanged,
        };
        return new DiffSegment(changeType, string.Join("\n", run.Select(l => l.Text)));
    }

    private static string BuildHtml(IReadOnlyList<DiffSegment> segments)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><style>");
        sb.Append("body{font-family:Consolas,monospace;font-size:13px;margin:12px;background:#fff;color:#222;}");
        sb.Append(".diff-added{background:#e6ffec;color:#1a7f37;padding:1px 6px;display:block;white-space:pre-wrap;}");
        sb.Append(".diff-deleted{background:#ffebe9;color:#cf222e;padding:1px 6px;display:block;white-space:pre-wrap;text-decoration:line-through;}");
        sb.Append(".diff-unchanged{color:#555;padding:1px 6px;display:block;white-space:pre-wrap;}");
        sb.Append("</style></head><body>");
        foreach (var segment in segments)
        {
            var cls = segment.ChangeType switch
            {
                DiffChangeType.Added => "diff-added",
                DiffChangeType.Deleted => "diff-deleted",
                _ => "diff-unchanged",
            };
            sb.Append("<span class=\"").Append(cls).Append("\">")
              .Append(System.Net.WebUtility.HtmlEncode(segment.Text)).Append("</span>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static List<string> Tokenize(string text)
    {
        // Words = runs of letters/digits; everything else is a separator token kept verbatim so the
        // inline diff never merges distinct words ("brown" stays standalone).
        var tokens = new List<string>();
        var word = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) { word.Append(ch); }
            else
            {
                if (word.Length > 0) { tokens.Add(word.ToString()); word.Clear(); }
                if (!char.IsWhiteSpace(ch)) tokens.Add(ch.ToString());
            }
        }
        if (word.Length > 0) tokens.Add(word.ToString());
        return tokens;
    }

    private static string Escape(string s) => System.Net.WebUtility.HtmlEncode(s);
}
