using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>The lines a line-list Insert dialog (workflow, timeline, tab group, data grid, chart,
/// code block, table, SmartArt, image, video, columns, canvas) opens with when lines are selected, and the range Insert then replaces. The dialogs
/// opened on their samples and dropped the block after the selection, so turning a list into a
/// workflow meant retyping it and deleting the original.</summary>
public static class SelectionPrefill
{
    // What may sit before the selection on its first line: indentation, a list marker, a task box.
    private static readonly Regex LeadAllowed = new(@"^\s*(?:(?:[-*+]|\d{1,9}[.)])\s+(?:\[[ xX]\]\s+)?)?$", RegexOptions.CultureInvariant);
    private static readonly Regex ListMarker = new(@"^(?:[-*+]|\d{1,9}[.)])\s+(?:\[[ xX]\]\s+)?", RegexOptions.CultureInvariant);
    private static readonly Regex SeparatorRow = new(@"^\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?$", RegexOptions.CultureInvariant);

    /// <summary>The whole lines a selection covers, or null when it doesn't cover whole lines (a few
    /// words inside a paragraph), is empty or is only white space. Text before the selection on its
    /// first line may only be indentation or a list marker, and text after it on its last line only
    /// white space; a selection ending just after a line break ends on the line before it.</summary>
    public static (int Start, int Length)? WholeLines(string? doc, int selStart, int selLength)
    {
        doc ??= "";
        selStart = Math.Clamp(selStart, 0, doc.Length);
        selLength = Math.Clamp(selLength, 0, doc.Length - selStart);
        if (selLength == 0 || string.IsNullOrWhiteSpace(doc.Substring(selStart, selLength))) return null;

        int start = selStart;
        while (start > 0 && !IsBreak(doc[start - 1])) start--;
        int selEnd = selStart + selLength;
        if (IsBreak(doc[selEnd - 1]))
            selEnd -= selEnd >= 2 && doc[selEnd - 2] == '\r' && doc[selEnd - 1] == '\n' ? 2 : 1;
        int end = Math.Max(selEnd, start);
        while (end < doc.Length && !IsBreak(doc[end])) end++;

        if (!LeadAllowed.IsMatch(doc[start..selStart])) return null;
        if (selEnd < end && !string.IsNullOrWhiteSpace(doc[selEnd..end])) return null;
        return (start, end - start);
    }

    /// <summary>Non-empty lines with list markers ("- ", "1. ", "- [ ] ") taken off: the entries of a
    /// workflow, timeline or tab group.</summary>
    public static IReadOnlyList<string> ListLines(string? text) =>
        Lines(text).Select(l => ListMarker.Replace(l, "").Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>Non-empty lines for a data grid: rows kept as written (comma, tab or pipe separated,
    /// all of which the grid reads), list markers taken off, a pipe table's |---| row dropped.</summary>
    public static IReadOnlyList<string> GridRows(string? text) =>
        Lines(text).Select(l => ListMarker.Replace(l, "").Trim())
            .Where(l => l.Length > 0 && !(l.Contains('|') && SeparatorRow.IsMatch(l))).ToList();

    /// <summary>Lines for a chart, which reads only "label,value": a tab- or pipe-separated row
    /// becomes its first two cells joined by a comma ("Q1\t10" and "| Q1 | 10 |" give "Q1,10").</summary>
    public static IReadOnlyList<string> ChartRows(string? text) =>
        GridRows(text).Select(row =>
        {
            string[] cells = row.Contains('|') ? row.Trim().Trim('|').Split('|')
                : row.Contains('\t') ? row.Split('\t')
                : Array.Empty<string>();
            cells = cells.Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
            return cells.Length >= 2 ? cells[0] + "," + cells[1] : row;
        }).ToList();

    private static readonly Regex UnescapedPipe = new(@"(?<!\\)\|", RegexOptions.CultureInvariant);
    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".tif", ".tiff", ".avif" };

    /// <summary>Cells for the Table dialog: a pipe-table row split on its unescaped pipes (its |---|
    /// row dropped), a tab row on tabs, a comma row on commas, at most 51 rows of 20 cells (a header
    /// and the dialog's 50 body rows). Null when no line has a separator, so a plain list keeps the
    /// dialog's sample instead of becoming a one-column table.</summary>
    public static IReadOnlyList<IReadOnlyList<string>>? TableCells(string? text)
    {
        var rows = GridRows(text);
        if (!rows.Any(r => r.Contains('|') || r.Contains('\t') || r.Contains(','))) return null;
        return rows.Take(51).Select(row =>
        {
            string[] cells = row.Contains('|') ? UnescapedPipe.Split(row.Trim().Trim('|'))
                : row.Contains('\t') ? row.Split('\t')
                : row.Split(',');
            return (IReadOnlyList<string>)cells.Select(c => c.Trim()).Take(20).ToList();
        }).ToList();
    }

    /// <summary>Lines for the SmartArt outline: list markers taken off, each line's indentation kept
    /// relative to the least indented one (a tab counts as four spaces, as the outline reads it), so a
    /// nested list keeps its levels.</summary>
    public static IReadOnlyList<string> OutlineLines(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.TrimEnd())
            .Select(l =>
            {
                var body = l.TrimStart(' ', '\t');
                var width = l[..(l.Length - body.Length)].Sum(c => c == '\t' ? 4 : 1);
                return (Width: width, Body: ListMarker.Replace(body, "").Trim());
            })
            .Where(l => l.Body.Length > 0).ToList();
        if (lines.Count == 0) return Array.Empty<string>();
        var least = lines.Min(l => l.Width);
        return lines.Select(l => new string(' ', l.Width - least) + l.Body).ToList();
    }

    /// <summary>What the Image dialog opens with for a one-line selection: an image file path or a
    /// web address as the source, other words as the alt text (both unwrapped from &lt;…&gt; or
    /// quotes). Nothing for an empty or multi-line selection, or words too long to be alt text.</summary>
    public static (string Source, string Alt) ImagePrefill(string? selection)
    {
        var s = ListMarker.Replace((selection ?? "").Trim(), "").Trim();
        if (s.Length == 0 || s.IndexOfAny(new[] { '\r', '\n' }) >= 0) return ("", "");
        var bare = InsertSnippetBuilder.UnwrapPasted(s);
        if (IsWebAddress(bare)) return (bare, "");
        var ext = System.IO.Path.GetExtension(bare.Split('?', '#')[0]).ToLowerInvariant();
        if (ImageExtensions.Contains(ext) && !bare.Contains("](")) return (bare, "");
        return s.Length <= 250 ? ("", s) : ("", "");
    }

    /// <summary>A one-line selection that is a web address, for the Video dialog's link; otherwise "".</summary>
    public static string VideoPrefill(string? selection)
    {
        var s = ListMarker.Replace((selection ?? "").Trim(), "").Trim();
        if (s.IndexOfAny(new[] { '\r', '\n' }) >= 0) return "";
        var bare = InsertSnippetBuilder.UnwrapPasted(s);
        return IsWebAddress(bare) ? bare : "";
    }

    private static bool IsWebAddress(string s) =>
        !s.Any(char.IsWhiteSpace) && Uri.TryCreate(s, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && u.Host.Length > 0;

    /// <summary>The paragraphs (split at blank lines) the Columns dialog opens with, one per column,
    /// each kept as written. Empty when a line starts a ":::" fence, which would end the columns
    /// block early, or when nothing is left. A line that is only "===" starts the next column, as it
    /// does inside the block; under paragraph text it underlines a heading, which is kept as a
    /// "# " heading so it doesn't split the paragraph into an extra column.</summary>
    public static IReadOnlyList<string> ColumnParagraphs(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        if (lines.Any(l => l.TrimStart().StartsWith(":::", StringComparison.Ordinal))) return Array.Empty<string>();
        var paragraphs = new List<string>();
        var current = new List<string>();
        void EndParagraph()
        {
            if (current.Count > 0) paragraphs.Add(string.Join("\n", current));
            current.Clear();
        }
        foreach (var line in lines.Append(""))
        {
            if (ColumnBreak.IsMatch(line))
            {
                var heading = current.Skip(HeadingStart(current)).ToList();
                if (heading.Count == 0) { EndParagraph(); continue; }
                current.RemoveRange(current.Count - heading.Count, heading.Count);
                current.Add("# " + string.Join(" ", heading.Select(l => l.Trim())));
                continue;
            }
            if (line.Trim().Length > 0) { current.Add(line); continue; }
            EndParagraph();
        }
        return paragraphs;
    }

    // The column dialog's splitter (MarkdownHtmlService.ColumnsSegmentSplitRe): a line of 3+ '='.
    private static readonly Regex ColumnBreak = new(@"^[ \t]*===+[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex BlockStart = new(@"^\s{0,3}(?:[-*+>]\s|\d{1,9}[.)]\s|#{1,6}(?:\s|$)|```|~~~|\|)", RegexOptions.CultureInvariant);

    // Where the plain text a "===" line would underline begins: after the last list item, quote,
    // heading, fence or table line (those can't be a heading's text). Count when there is none.
    private static int HeadingStart(List<string> lines)
    {
        var start = lines.Count;
        while (start > 0 && !BlockStart.IsMatch(lines[start - 1])) start--;
        return start;
    }

    /// <summary>A selected &lt;svg&gt;…&lt;/svg&gt; drawing for the canvas dialog, or "" for anything
    /// else (or one with a line that is only ":::", which would end the canvas block early).</summary>
    public static string CanvasSvg(string? text)
    {
        var s = (text ?? "").Trim();
        if (!s.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || !s.EndsWith("</svg>", StringComparison.OrdinalIgnoreCase)
            || (s.Length > 4 && !(char.IsWhiteSpace(s[4]) || s[4] == '>' || s[4] == '/'))) return "";
        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return lines.Any(l => l.Trim() == ":::") ? "" : s;
    }

    /// <summary>The note a prefilled dialog shows, so Insert replacing the lines is no surprise.</summary>
    public static string Note(int lines) => Note(lines, "selected line");

    /// <summary>The note counted in something other than lines ("selected paragraph" for Columns,
    /// whose two paragraphs read "the 4 selected lines" with the blank line between them).</summary>
    public static string Note(int count, string selectedNoun) =>
        $"Filled from {TheSelected(count, selectedNoun)}. Insert replaces {(count == 1 ? "it" : "them")}.";

    /// <summary>"the selected line" or "the 4 selected lines": "the 1 selected line" read oddly in
    /// the note and the status bar.</summary>
    public static string TheSelectedLines(int lines) => TheSelected(lines, "selected line");

    /// <summary>"the selected paragraph" or "the 2 selected paragraphs".</summary>
    public static string TheSelected(int count, string selectedNoun) =>
        count == 1 ? $"the {selectedNoun}" : $"the {CountText.Of(count, selectedNoun)}";

    /// <summary>Lines in a selection, counting a WinUI bare '\r' as a break.</summary>
    public static int LineCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Length;

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static bool IsBreak(char c) => c == '\r' || c == '\n';
}
