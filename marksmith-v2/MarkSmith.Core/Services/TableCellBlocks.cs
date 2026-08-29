using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Recovers block-level Markdown from inside a pipe-table cell.
///
/// GFM pipe-table cells are inline-only: Markdig parses <c>&gt; [!WARNING]</c> in a cell as literal
/// text, a <c>-</c> line as a hyphen, and a fence as an inline code span. Authors still reach for
/// those constructs in cells (and write the line breaks as <c>&lt;br&gt;</c>, the only line break a
/// pipe row allows), so this turns a qualifying cell back into real Markdown that both pipelines can
/// render as blocks — a native Word callout, a real list, a real code block.
///
/// The test is deliberately conservative. A cell only qualifies when it is unambiguously block
/// content, so ordinary one-line cells keep their existing inline rendering:
/// <list type="bullet">
///   <item>it contains a <c>&lt;br&gt;</c> and at least one line opens with a block marker, or</item>
///   <item>it is a single-line GitHub alert (<c>&gt; [!NOTE] …</c>), which has no other meaning.</item>
/// </list>
/// </summary>
public static partial class TableCellBlocks
{
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRe();

    // A blockquote, a bullet or ordered list item, or a fence opener.
    [GeneratedRegex(@"^\s{0,3}(?:>|[-*+][ \t]|\d{1,9}[.)][ \t]|```|~~~)")]
    private static partial Regex BlockMarkerRe();

    // "> [!NOTE]" / "> [!warning] trailing text on the same line".
    [GeneratedRegex(@"^\s{0,3}>\s*\[!(\w+)\]\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex AlertOpenerRe();

    /// <summary>
    /// True when <paramref name="cellText"/> should be rendered as Markdown blocks rather than
    /// inline content.
    /// </summary>
    public static bool IsBlockCell(string? cellText)
    {
        if (string.IsNullOrWhiteSpace(cellText)) return false;

        var lines = SplitCell(cellText);
        if (lines.Length > 1)
            return lines.Any(l => BlockMarkerRe().IsMatch(l));

        // A one-line cell is only promoted for an alert, whose marker is unambiguous. Promoting a
        // bare "- foo" or "1. foo" would silently reformat table cells that render fine today.
        return AlertOpenerRe().IsMatch(lines[0]);
    }

    /// <summary>
    /// Converts a qualifying cell into standalone Markdown, or returns <c>null</c> when the cell is
    /// ordinary inline content.
    /// </summary>
    public static string? TryGetBlockMarkdown(string? cellText)
    {
        if (!IsBlockCell(cellText)) return null;

        var lines = SplitCell(cellText!);
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            // GitHub requires the alert marker to sit alone on its line. Authors routinely write
            // "> [!WARNING] Mind the gap!" on one line in a cell, because a cell has no newline —
            // split it so Markdig's alert extension recognises it.
            var alert = AlertOpenerRe().Match(line);
            if (alert.Success && alert.Groups[2].Value.Trim().Length > 0)
            {
                sb.Append("> [!").Append(alert.Groups[1].Value.ToUpperInvariant()).Append("]\n");
                sb.Append("> ").Append(alert.Groups[2].Value.Trim()).Append('\n');
                continue;
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Splits a cell on its <c>&lt;br&gt;</c> line breaks, trimming each line.
    ///
    /// Code spans are masked before the split is located, so a cell that merely *documents* the
    /// syntax — <c>`- one&lt;br&gt;- two`</c> in a "Raw syntax" column — is left as the inline code
    /// the author wrote instead of being promoted into a real list. Masking preserves length, so
    /// the split offsets found in the masked copy index the original text unchanged.
    /// </summary>
    private static string[] SplitCell(string cellText)
    {
        var masked = MaskCodeSpans(cellText);
        var parts = new List<string>();
        int cursor = 0;

        foreach (Match br in BrRe().Matches(masked))
        {
            parts.Add(cellText[cursor..br.Index]);
            cursor = br.Index + br.Length;
        }
        parts.Add(cellText[cursor..]);

        return parts.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
    }

    /// <summary>Blanks out backtick code spans, keeping the string's length and other offsets.</summary>
    private static string MaskCodeSpans(string text)
    {
        var chars = text.ToCharArray();
        int i = 0;
        while (i < chars.Length)
        {
            if (chars[i] != '`') { i++; continue; }

            int openStart = i;
            while (i < chars.Length && chars[i] == '`') i++;
            int fence = i - openStart;

            int scan = i;
            while (scan < chars.Length)
            {
                if (chars[scan] != '`') { scan++; continue; }
                int closeStart = scan;
                while (scan < chars.Length && chars[scan] == '`') scan++;
                if (scan - closeStart == fence)
                {
                    for (int k = openStart; k < scan; k++) chars[k] = ' ';
                    i = scan;
                    break;
                }
            }
            if (scan >= chars.Length) break; // unterminated span — leave the rest as-is
        }
        return new string(chars);
    }
}
