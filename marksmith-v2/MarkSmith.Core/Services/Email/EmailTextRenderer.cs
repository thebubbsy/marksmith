using System.Text;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkSmith.Services.Email;

/// <summary>The plain-text part of an email (multipart/alternative): what text-only clients,
/// notification previews and screen-reader-first setups show. Reads like a tidy plain email, not
/// like Markdown: no asterisks, links written as "text (url)", tables as aligned columns.</summary>
internal static class EmailTextRenderer
{
    public static string Render(MarkdownDocument doc, Block? omit, int figureCount)
    {
        var sb = new StringBuilder();
        foreach (var b in doc) Block(sb, b, omit, "");
        var text = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n");
        return text.Trim() + "\n";
    }

    private static void Block(StringBuilder sb, Block block, Block? omit, string indent)
    {
        if (ReferenceEquals(block, omit)) return;
        switch (block)
        {
            case YamlFrontMatterBlock:
            case LinkReferenceDefinitionGroup:
                return;
            case HeadingBlock h:
            {
                var t = InlineText(h.Inline).Trim();
                sb.Append(indent).Append(t).Append('\n');
                if (h.Level <= 2) sb.Append(indent).Append(new string(h.Level == 1 ? '=' : '-', Math.Min(t.Length, 60))).Append('\n');
                sb.Append('\n');
                return;
            }
            case ParagraphBlock p:
                sb.Append(indent).Append(InlineText(p.Inline).Replace("\n", "\n" + indent)).Append("\n\n");
                return;
            case MathBlock m:
                sb.Append(indent).Append("    ").Append(LatexText.ToReadable(m.Lines.ToString())).Append("\n\n");
                return;
            case FencedCodeBlock f when f.Info?.Trim().StartsWith("mermaid", StringComparison.OrdinalIgnoreCase) == true:
                sb.Append(indent).Append("[Diagram]\n\n");
                return;
            case CodeBlock code:
                foreach (var line in code.Lines.ToString().TrimEnd().Split('\n'))
                    sb.Append(indent).Append("    ").Append(line.TrimEnd('\r')).Append('\n');
                sb.Append('\n');
                return;
            case AlertBlock alert:
                sb.Append(indent).Append(char.ToUpperInvariant(alert.Kind.ToString().FirstOrDefault('n')))
                  .Append(alert.Kind.ToString().ToLowerInvariant().Skip(1).ToArray()).Append(":\n");
                foreach (var c in alert) Block(sb, c, omit, indent + "  ");
                return;
            case QuoteBlock q:
                foreach (var c in q) Block(sb, c, omit, indent + "> ");
                return;
            case ListBlock list:
            {
                int n = int.TryParse(list.OrderedStart, out var s) ? s : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var marker = list.IsOrdered ? $"{n++}. " : "• ";
                    if (item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: { } first } && EmailHtmlRenderer.CheckboxState(first) is { } ticked)
                        marker = ticked ? "[x] " : "[ ] ";
                    var inner = new StringBuilder();
                    foreach (var c in item) Block(inner, c, omit, "");
                    var lines = inner.ToString().TrimEnd().Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i == 0) sb.Append(indent).Append(marker).Append(lines[i].TrimStart()).Append('\n');
                        else if (lines[i].Length > 0) sb.Append(indent).Append(new string(' ', marker.Length)).Append(lines[i]).Append('\n');
                    }
                }
                sb.Append('\n');
                return;
            }
            case ThematicBreakBlock:
                sb.Append(indent).Append("----------\n\n");
                return;
            case Table table:
                TableText(sb, table, indent);
                return;
            case FootnoteGroup group:
                // A "---" right before the notes already drew the rule.
                if (!sb.ToString().TrimEnd().EndsWith("----------", StringComparison.Ordinal))
                    sb.Append(indent).Append("----------\n");
                foreach (var fn in group.OfType<Footnote>().OrderBy(f => f.Order))
                {
                    var inner = new StringBuilder();
                    foreach (var c in fn) Block(inner, c, omit, "");
                    sb.Append(indent).Append('[').Append(fn.Order).Append("] ").Append(inner.ToString().Trim()).Append('\n');
                }
                sb.Append('\n');
                return;
            case HtmlBlock html:
            {
                var raw = html.Lines.ToString().Trim();
                if (raw.StartsWith("<!--MSFIG:", StringComparison.Ordinal)) { sb.Append(indent).Append("[Diagram]\n\n"); return; }
                var stripped = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(raw, "<[^>]+>", " ")).Trim();
                if (stripped.Length > 0) sb.Append(indent).Append(System.Text.RegularExpressions.Regex.Replace(stripped, @"\s{2,}", " ")).Append("\n\n");
                return;
            }
            case LeafBlock leaf when leaf.Inline is not null:
                sb.Append(indent).Append(InlineText(leaf.Inline)).Append("\n\n");
                return;
            case ContainerBlock container:
                foreach (var c in container) Block(sb, c, omit, indent);
                return;
        }
    }

    private static void TableText(StringBuilder sb, Table table, string indent)
    {
        var rows = table.OfType<TableRow>()
            .Select(r => (r.IsHeader, Cells: r.OfType<TableCell>().Select(c =>
                string.Join(" ", c.OfType<LeafBlock>().Select(l => InlineText(l.Inline).Replace('\n', ' ').Trim()))).ToList()))
            .ToList();
        if (rows.Count == 0) return;
        int cols = rows.Max(r => r.Cells.Count);
        var widths = Enumerable.Range(0, cols).Select(c => Math.Min(40, rows.Max(r => c < r.Cells.Count ? r.Cells[c].Length : 0))).ToArray();
        foreach (var (isHeader, cells) in rows)
        {
            sb.Append(indent);
            for (int c = 0; c < cols; c++)
            {
                var v = c < cells.Count ? cells[c] : "";
                sb.Append(c == cols - 1 ? v : v.PadRight(widths[c] + 2));
            }
            sb.Append('\n');
            if (isHeader)
                sb.Append(indent).Append(string.Join("  ", widths.Select(w => new string('-', Math.Max(3, w))))).Append('\n');
        }
        sb.Append('\n');
    }

    /// <summary>Plain text of an inline run: emphasis dropped, links as "text (url)", maths made
    /// readable, task boxes omitted (the list marker carries them).</summary>
    public static string InlineText(ContainerInline? container)
    {
        if (container is null) return "";
        var sb = new StringBuilder();
        foreach (var inline in container) Inline(sb, inline);
        return sb.ToString();
    }

    private static void Inline(StringBuilder sb, Inline inline)
    {
        switch (inline)
        {
            case var box when EmailHtmlRenderer.CheckboxState(box) is not null:
                return;
            case LiteralInline lit:
                sb.Append(lit.Content.ToString());
                return;
            case HtmlEntityInline e:
                sb.Append(e.Transcoded.ToString());
                return;
            case CodeInline code:
                sb.Append(code.Content);
                return;
            case MathInline math:
                sb.Append(LatexText.ToReadable(math.Content.ToString()));
                return;
            case LineBreakInline:
                sb.Append('\n');
                return;
            case AutolinkInline auto:
                sb.Append(auto.Url);
                return;
            case FootnoteLink fl:
                if (!fl.IsBackLink) sb.Append('[').Append(fl.Footnote.Order).Append(']');
                return;
            case LinkInline link when link.IsImage:
            {
                var alt = InlineText(link).Trim();
                sb.Append('[').Append(alt.Length > 0 ? alt : "image").Append(']');
                return;
            }
            case LinkInline link:
            {
                var label = InlineText(link);
                var url = link.Url ?? "";
                sb.Append(label);
                if (url.Length > 0 && !url.StartsWith('#') && !string.Equals(label.Trim(), url, StringComparison.OrdinalIgnoreCase))
                    sb.Append(" (").Append(url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? url[7..] : url).Append(')');
                return;
            }
            case HtmlInline html:
                if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) sb.Append('\n');
                return;
            case ContainerInline c:
                foreach (var child in c) Inline(sb, child);
                return;
            default:
                sb.Append(inline.ToString());
                return;
        }
    }
}
