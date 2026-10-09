using System;

namespace MarkSmith.Services;

/// <summary>Where a block lands in the editor, what replaces what, and where the caret goes
/// afterwards. Apply it by selecting <see cref="Start"/>..<see cref="Start"/>+<see cref="Length"/>,
/// replacing that with <see cref="Text"/>, then selecting the caret range.</summary>
public readonly record struct BlockInsertPlan(int Start, int Length, string Text, int CaretStart, int CaretLength);

/// <summary>
/// Places a block (a table, a fenced component, a studio diagram) in a Markdown document as its
/// own paragraph. Pasting the snippet raw at the caret split the line the caret was in, glued the
/// block onto selected text (and left that text selected), and on a freshly opened document, where
/// the caret was never placed and sits at 0, put the diagram above the title.
/// </summary>
public static class BlockInsertion
{
    /// <param name="prefix">The block, or its opening half when <paramref name="suffix"/> is set
    /// (a code fence whose body the user types next). Leading and trailing line breaks are
    /// ignored: separation is decided here.</param>
    /// <param name="suffix">The closing half of a block that wraps something: the selected lines
    /// when there is a selection, else the caret lands between the halves.</param>
    /// <param name="caretPlaced">False when the user has not put the caret anywhere in this
    /// document yet; the block then goes at the end instead of at position 0.</param>
    public static BlockInsertPlan Plan(string? doc, int selStart, int selLength, string prefix, string suffix = "", bool caretPlaced = true)
    {
        doc ??= "";
        selStart = Math.Clamp(selStart, 0, doc.Length);
        selLength = Math.Clamp(selLength, 0, doc.Length - selStart);
        string nl = NewLineOf(doc);
        bool wraps = suffix.Length > 0;

        string open = Normalize(TrimBreaks(prefix), nl);
        string close = Normalize(TrimBreaks(suffix), nl);

        int start, length;
        string body = "";
        bool caretAfterOpen = false;

        if (wraps && selLength > 0)
        {
            // Wrap the selected lines, whole: a fence must start and end on its own lines.
            start = LineStart(doc, selStart);
            int selEnd = selStart + selLength;
            // A selection ending just after a line break ends on the line before it.
            if (selEnd > selStart && IsBreak(doc[selEnd - 1])) selEnd = BackOverBreak(doc, selEnd);
            int end = LineEnd(doc, Math.Max(selEnd, start));
            length = end - start;
            body = doc.Substring(start, length);
        }
        else
        {
            int anchor = !caretPlaced ? doc.Length : selStart + selLength;
            // A selection ending just after a line break ends on the line before it.
            if (caretPlaced && selLength > 0 && IsBreak(doc[anchor - 1])) anchor = BackOverBreak(doc, anchor);
            bool atLineStart = anchor == LineStart(doc, anchor);
            bool lineBlank = IsBlank(doc, LineStart(doc, anchor), LineEnd(doc, anchor));
            // At the start of a line with text on it, the block goes above that line; anywhere
            // else in a line it goes below it, so the line is never split.
            start = selLength == 0 && atLineStart && !lineBlank ? anchor : LineEnd(doc, anchor);
            if (lineBlank)
            {
                // On an empty line, fill it rather than adding one more.
                start = LineStart(doc, anchor);
                length = LineEnd(doc, anchor) - start;
            }
            else length = 0;
            caretAfterOpen = wraps;
        }

        string before = doc.Substring(0, start);
        string after = doc.Substring(start + length);
        string lead = before.Length == 0 ? "" : Repeat(nl, Math.Max(0, 2 - TrailingBreaks(before)));
        // At the end of the document one break closes the block; a blank line there is litter.
        string trail = string.IsNullOrWhiteSpace(after)
            ? (LeadingBreaks(after) > 0 ? "" : nl)
            : Repeat(nl, Math.Max(0, 2 - LeadingBreaks(after)));

        string block = wraps
            ? open + nl + (caretAfterOpen ? "" : body) + nl + close
            : open;
        string text = lead + block + trail;

        int caretStart, caretLength = 0;
        if (wraps && caretAfterOpen) caretStart = start + lead.Length + open.Length + nl.Length;
        else if (wraps) { caretStart = start + lead.Length + open.Length + nl.Length; caretLength = body.Length; }
        else caretStart = start + lead.Length + block.Length;
        return new BlockInsertPlan(start, length, text, caretStart, caretLength);
    }

    /// <summary>The document's line break. The WinUI editor stores a bare '\r'.</summary>
    public static string NewLineOf(string doc) =>
        doc.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
        : doc.Contains('\r') ? "\r"
        : doc.Contains('\n') ? "\n"
        : "\r";

    private static bool IsBreak(char c) => c == '\r' || c == '\n';

    private static int BackOverBreak(string s, int end)
    {
        if (end >= 2 && s[end - 2] == '\r' && s[end - 1] == '\n') return end - 2;
        return end - 1;
    }

    private static int LineStart(string s, int i)
    {
        while (i > 0 && !IsBreak(s[i - 1])) i--;
        return i;
    }

    private static int LineEnd(string s, int i)
    {
        while (i < s.Length && !IsBreak(s[i])) i++;
        return i;
    }

    private static bool IsBlank(string s, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (!char.IsWhiteSpace(s[i])) return false;
        return true;
    }

    /// <summary>Line breaks at the end of <paramref name="s"/>, counting "\r\n" as one and
    /// stepping over spaces on otherwise empty lines.</summary>
    private static int TrailingBreaks(string s)
    {
        int count = 0, i = s.Length;
        while (i > 0)
        {
            int j = i;
            while (j > 0 && (s[j - 1] == ' ' || s[j - 1] == '\t')) j--;
            if (j == 0 || !IsBreak(s[j - 1])) break;
            i = BackOverBreak(s, j);
            count++;
        }
        return count;
    }

    private static int LeadingBreaks(string s)
    {
        int count = 0, i = 0;
        while (i < s.Length)
        {
            int j = i;
            while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;
            if (j == s.Length || !IsBreak(s[j])) break;
            i = j + (s[j] == '\r' && j + 1 < s.Length && s[j + 1] == '\n' ? 2 : 1);
            count++;
        }
        return count;
    }

    private static string TrimBreaks(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsBreak(s[a])) a++;
        while (b > a && IsBreak(s[b - 1])) b--;
        return s.Substring(a, b - a);
    }

    private static string Normalize(string s, string nl) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", nl);

    private static string Repeat(string s, int n) => n <= 0 ? "" : string.Concat(System.Linq.Enumerable.Repeat(s, n));
}
