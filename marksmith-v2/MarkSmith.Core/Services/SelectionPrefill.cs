using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>The lines a line-list Insert dialog (workflow, timeline, tab group, data grid, chart,
/// code block) opens with when lines are selected, and the range Insert then replaces. The dialogs
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

    /// <summary>The note a prefilled dialog shows, so Insert replacing the lines is no surprise.</summary>
    public static string Note(int lines) =>
        $"Filled from the {CountText.Of(lines, "selected line")}. Insert replaces {(lines == 1 ? "it" : "them")}.";

    /// <summary>Lines in a selection, counting a WinUI bare '\r' as a break.</summary>
    public static int LineCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Length;

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static bool IsBreak(char c) => c == '\r' || c == '\n';
}
