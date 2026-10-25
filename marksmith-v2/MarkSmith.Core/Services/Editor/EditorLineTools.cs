using System;
using System.Collections.Generic;
using System.Linq;

namespace MarkSmith.Services.Editor;

/// <summary>
/// The editor's Transform ▸ Sort lines and Remove duplicate lines, over a selection with '\n'
/// breaks (the editor's <c>EditorText()</c> copy). A selection of whole lines usually ends with the
/// last line's break; that break used to count as an empty line, so Sort A to Z moved a blank line
/// to the top of the selection and glued its last line to the line below. Trailing breaks now stay
/// at the end, untouched.
/// </summary>
public static class EditorLineTools
{
    /// <summary>The selection's lines sorted A to Z (or Z to A), ignoring case; trailing breaks kept.</summary>
    public static string SortLines(string selection, bool descending = false)
    {
        var (body, tail) = SplitTrailingBreaks(selection ?? string.Empty);
        if (body.Length == 0) return selection ?? string.Empty;
        var lines = body.Split('\n');
        var sorted = descending
            ? lines.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
            : lines.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", sorted) + tail;
    }

    /// <summary>
    /// The selection with repeated lines dropped (the first of each kept, in order), and how many
    /// were dropped. Lines compare exactly, apart from a trailing '\r'.
    /// </summary>
    public static (string Text, int Removed) DedupeLines(string selection)
    {
        var (body, tail) = SplitTrailingBreaks(selection ?? string.Empty);
        if (body.Length == 0) return (selection ?? string.Empty, 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        var lines = body.Split('\n');
        foreach (var line in lines)
            if (seen.Add(line.TrimEnd('\r'))) kept.Add(line);
        return (string.Join("\n", kept) + tail, lines.Length - kept.Count);
    }

    private static (string Body, string Tail) SplitTrailingBreaks(string s)
    {
        var end = s.Length;
        while (end > 0 && (s[end - 1] == '\n' || s[end - 1] == '\r')) end--;
        return (s[..end], s[end..]);
    }
}
