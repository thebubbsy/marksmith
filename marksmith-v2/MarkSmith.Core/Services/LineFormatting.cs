using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>Line-level Markdown markers the editor's toolbar and Ctrl+1–4 apply.</summary>
public enum LineMarker
{
    Bullet,
    Numbered,
    Task,
    Quote,
}

/// <summary>
/// The result of a line-formatting edit: replace <see cref="Length"/> characters at
/// <see cref="Start"/> with <see cref="Replacement"/>, then select
/// <see cref="SelectionStart"/>/<see cref="SelectionLength"/> (positions in the edited text).
/// Replacing a range rather than the whole text keeps the editor's scroll position and undo burst.
/// </summary>
public readonly record struct LineEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionLength);

/// <summary>
/// Headings and list/quote markers act on whole lines: the lines the selection touches, or the
/// caret's line. Pressing the same one again removes it, and switching (H2 to H3, bullets to
/// numbers) replaces the old marker instead of stacking a second one. The toolbar used to insert
/// "# " at the caret, so a heading clicked at the end of "Hello" gave "Hello# ".
/// Line breaks may be "\r\n", "\n" or the bare "\r" a WinUI TextBox stores.
/// </summary>
public static class LineFormatting
{
    private static readonly Regex HeadingRx = new(@"^( {0,3})(#{1,6})(?:[ \t]+|$)", RegexOptions.Compiled);
    private static readonly Regex TaskRx = new(@"^(\s*)[-*+] \[[ xX]\] ", RegexOptions.Compiled);
    private static readonly Regex BulletRx = new(@"^(\s*)[-*+] ", RegexOptions.Compiled);
    private static readonly Regex NumberedRx = new(@"^(\s*)\d{1,9}[.)] ", RegexOptions.Compiled);
    private static readonly Regex QuoteRx = new(@"^(\s*)> ?", RegexOptions.Compiled);

    /// <summary>Make the touched lines headings of <paramref name="level"/> (1–6), or plain text
    /// again when they already all are.</summary>
    public static LineEdit Heading(string text, int selectionStart, int selectionLength, int level)
    {
        level = Math.Clamp(level, 1, 6);
        var hashes = new string('#', level);
        return Apply(text, selectionStart, selectionLength, lines =>
        {
            var content = lines.Where(l => l.Trim().Length > 0).ToList();
            var allAtLevel = content.Count > 0 && content.All(l =>
            {
                var m = HeadingRx.Match(l);
                return m.Success && m.Groups[2].Length == level;
            });
            return lines.Select((l, i) =>
            {
                var body = StripHeading(l);
                if (allAtLevel) return body;
                // A blank line inside a multi-line selection stays blank; a lone blank caret line
                // gets the marker so the user can start typing the heading.
                if (body.Trim().Length == 0 && lines.Count > 1) return l;
                return $"{hashes} {body}";
            }).ToList();
        });
    }

    /// <summary>Add <paramref name="marker"/> to the touched lines, or remove it when every
    /// non-blank line already has it. Bullets, numbers and tasks replace one another.</summary>
    public static LineEdit Toggle(string text, int selectionStart, int selectionLength, LineMarker marker)
    {
        return Apply(text, selectionStart, selectionLength, lines =>
        {
            var content = lines.Where(l => l.Trim().Length > 0).ToList();
            var allHave = content.Count > 0 && content.All(l => Has(l, marker));
            var number = 0;
            return lines.Select(l =>
            {
                if (lines.Count > 1 && l.Trim().Length == 0) return l;
                if (allHave) return Remove(l, marker);
                if (marker == LineMarker.Quote) return "> " + l;
                var (indent, body) = SplitListMarker(l);
                return marker switch
                {
                    LineMarker.Bullet => $"{indent}- {body}",
                    LineMarker.Task => $"{indent}- [ ] {body}",
                    _ => $"{indent}{++number}. {body}",
                };
            }).ToList();
        });
    }

    private static string StripHeading(string line)
    {
        var m = HeadingRx.Match(line);
        return m.Success ? line[m.Length..] : line;
    }

    private static bool Has(string line, LineMarker marker) => marker switch
    {
        LineMarker.Quote => QuoteRx.IsMatch(line),
        LineMarker.Task => TaskRx.IsMatch(line),
        LineMarker.Bullet => BulletRx.IsMatch(line) && !TaskRx.IsMatch(line),
        _ => NumberedRx.IsMatch(line),
    };

    private static string Remove(string line, LineMarker marker)
    {
        if (marker == LineMarker.Quote)
        {
            var q = QuoteRx.Match(line);
            return q.Success ? q.Groups[1].Value + line[q.Length..] : line;
        }
        var (indent, body) = SplitListMarker(line);
        return indent + body;
    }

    // Indentation and text of a list line with its marker (task, bullet or number) taken off.
    private static (string Indent, string Body) SplitListMarker(string line)
    {
        foreach (var rx in new[] { TaskRx, BulletRx, NumberedRx })
        {
            var m = rx.Match(line);
            if (m.Success) return (m.Groups[1].Value, line[m.Length..]);
        }
        var indentLength = line.Length - line.TrimStart(' ', '\t').Length;
        return (line[..indentLength], line[indentLength..]);
    }

    private static bool IsBreak(char c) => c is '\r' or '\n';

    // Finds the block of whole lines the selection touches, runs the transform over them, and
    // works out where the caret or selection should land afterwards.
    private static LineEdit Apply(string text, int selectionStart, int selectionLength,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> transform)
    {
        text ??= string.Empty;
        var selStart = Math.Clamp(selectionStart, 0, text.Length);
        var selEnd = Math.Clamp(selStart + Math.Max(0, selectionLength), selStart, text.Length);

        // A selection that ends right after a line break (whole lines selected by dragging down)
        // doesn't include the next line.
        if (selEnd > selStart && IsBreak(text[selEnd - 1])) selEnd--;
        if (selEnd > selStart && selEnd < text.Length && text[selEnd] == '\n' && text[selEnd - 1] == '\r') selEnd--;

        var blockStart = selStart;
        while (blockStart > 0 && !IsBreak(text[blockStart - 1])) blockStart--;
        var blockEnd = Math.Max(selEnd, blockStart);
        while (blockEnd < text.Length && !IsBreak(text[blockEnd])) blockEnd++;

        var block = text[blockStart..blockEnd];
        var parts = Regex.Split(block, "(\r\n|\r|\n)");
        var lines = new List<string>();
        for (var i = 0; i < parts.Length; i += 2) lines.Add(parts[i]);

        var newLines = transform(lines);

        var sb = new StringBuilder();
        for (var i = 0; i < newLines.Count; i++)
        {
            sb.Append(newLines[i]);
            if (2 * i + 1 < parts.Length) sb.Append(parts[2 * i + 1]);
        }
        var replacement = sb.ToString();

        if (selectionLength > 0 || lines.Count > 1)
            return new LineEdit(blockStart, block.Length, replacement, blockStart, replacement.Length);

        // Caret on one line: keep it at the same spot in the line's text, never inside the marker.
        var oldLine = lines[0];
        var newLine = newLines[0];
        var oldBody = BodyStart(oldLine);
        var newBody = BodyStart(newLine);
        var caretInLine = selStart - blockStart;
        var caret = blockStart + Math.Max(newBody, caretInLine - oldBody + newBody);
        caret = Math.Clamp(caret, blockStart, blockStart + newLine.Length);
        return new LineEdit(blockStart, block.Length, replacement, caret, 0);
    }

    // Where a line's text begins after any heading, quote or list marker.
    private static int BodyStart(string line)
    {
        var offset = 0;
        var q = QuoteRx.Match(line);
        if (q.Success) offset = q.Length;
        var rest = line[offset..];
        var h = HeadingRx.Match(rest);
        if (h.Success) return offset + h.Length;
        foreach (var rx in new[] { TaskRx, BulletRx, NumberedRx })
        {
            var m = rx.Match(rest);
            if (m.Success) return offset + m.Length;
        }
        return offset;
    }
}
