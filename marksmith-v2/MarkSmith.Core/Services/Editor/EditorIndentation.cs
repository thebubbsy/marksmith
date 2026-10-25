using System;
using System.Collections.Generic;
using System.Text;

namespace MarkSmith.Services.Editor;

/// <summary>The indentation style a document mostly uses, as the status bar shows it.</summary>
public readonly record struct IndentationStyle(bool UseTabs, int Size, string Summary);

/// <summary>The result of a whole-document line edit and how many lines it changed.</summary>
public readonly record struct LineCleanup(string Text, int ChangedLines);

/// <summary>
/// The status bar's indentation tools: detect the document's indent style, convert every line's
/// leading whitespace, and trim trailing whitespace. A WinUI TextBox stores each line break as a
/// bare '\r', and these used to split on '\n' only, so the whole document read as one line: the
/// pill judged the style from line 1, "Indent using Spaces: 2" re-indented line 1 only, and Trim
/// trailing whitespace only trimmed the end of the document. Line breaks may be "\r\n", "\n" or
/// "\r" and are kept exactly as found.
/// </summary>
public static class EditorIndentation
{
    /// <summary>Tabs, 2 spaces or 4 spaces, by which most indented lines use.</summary>
    public static IndentationStyle Detect(string? text)
    {
        int spaces2 = 0, spaces4 = 0, tabs = 0;
        foreach (var (line, _) in Lines(text ?? string.Empty))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int s = 0, t = 0;
            foreach (var c in line)
            {
                if (c == ' ') s++;
                else if (c == '\t') t++;
                else break;
            }
            if (t > 0) tabs++;
            else if (s > 0)
            {
                if (s % 4 == 0) spaces4++;
                else if (s % 2 == 0) spaces2++;
            }
        }

        if (tabs > spaces4 + spaces2) return new IndentationStyle(true, 4, "Tabs");
        // A 2-space document also has lines at 4 and 8 spaces, so "more 2s than 4s" called most of
        // them 4-space. Depths of 2, 6, 10… on a fair share of lines mean 2-space levels.
        if (spaces2 > 0 && spaces2 * 4 >= spaces4) return new IndentationStyle(false, 2, "Spaces: 2");
        return new IndentationStyle(false, 4, "Spaces: 4");
    }

    /// <summary>Re-indents every line with tabs, or with <paramref name="targetSpaces"/> spaces per
    /// level. Levels are counted in the document's own unit (<see cref="Detect"/>; a tab is one
    /// level of 4 columns): dividing by the target size instead turned a 4-space document's 4
    /// spaces into two 2-space levels, so "Spaces: 2" changed nothing. Leftover columns are kept,
    /// and a line indented less than one level gets one level.</summary>
    public static LineCleanup Convert(string? text, bool toTabs, int targetSpaces)
    {
        text ??= string.Empty;
        var unit = Math.Max(1, Detect(text).Size);
        var sb = new StringBuilder(text.Length);
        var changed = 0;
        foreach (var (line, newline) in Lines(text))
        {
            int ws = 0, columns = 0;
            while (ws < line.Length && (line[ws] == ' ' || line[ws] == '\t'))
            {
                columns += line[ws] == '\t' ? 4 : 1;
                ws++;
            }

            var converted = line;
            if (ws > 0)
            {
                int levels = columns / unit, rest = columns % unit;
                if (levels == 0) { levels = 1; rest = 0; }
                var indent = toTabs
                    ? new string('\t', levels) + new string(' ', rest)
                    : new string(' ', levels * targetSpaces + rest);
                converted = indent + line.Substring(ws);
            }
            if (!string.Equals(converted, line, StringComparison.Ordinal)) changed++;
            sb.Append(converted).Append(newline);
        }
        return new LineCleanup(sb.ToString(), changed);
    }

    /// <summary>Strips spaces and tabs from the end of every line.</summary>
    public static LineCleanup TrimTrailingWhitespace(string? text)
    {
        text ??= string.Empty;
        var sb = new StringBuilder(text.Length);
        var changed = 0;
        foreach (var (line, newline) in Lines(text))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length != line.Length) changed++;
            sb.Append(trimmed).Append(newline);
        }
        return new LineCleanup(sb.ToString(), changed);
    }

    /// <summary>Each line with the break that ends it ("" for the last line).</summary>
    private static IEnumerable<(string Line, string Newline)> Lines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;
            var nl = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : c.ToString();
            yield return (text.Substring(start, i - start), nl);
            i += nl.Length - 1;
            start = i + 1;
        }
        yield return (text.Substring(start), string.Empty);
    }
}
