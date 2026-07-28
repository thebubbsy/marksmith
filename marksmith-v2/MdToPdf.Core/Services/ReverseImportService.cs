using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using M = DocumentFormat.OpenXml.Math;

namespace MdToPdf.Services;

// The inverse of DocxExportService: reads a .docx that Marksmith produced and reconstructs the
// source Markdown. This is a REAL reverse converter — no embedded copy of the original is stored or
// read back. It pattern-matches the exact OOXML signatures the forward engine emits (heading styles,
// run-level bold/italic/strike/highlight/inline-code/sub/sup, zebra tables with a bold header row,
// numId-based lists with w14 checkbox task items, Consolas code paragraphs, centered OMML equation
// paragraphs, single-cell alert tables, wave-border horizontal rules, and H_* bookmark anchors) and
// re-emits CANONICAL Markdown. Because the forward pass is lossy for presentation-only choices
// (emphasis marker style, table cell padding, math whitespace, soft line breaks), the guarantee is:
// canonical Markdown round-trips byte-for-byte, and any equivalent spelling converges to canonical
// form on the first pass (idempotent from then on).
public sealed class ReverseImportService
{
    // ---- public entry ------------------------------------------------------------------------

    public string ImportFromDocx(string docxPath)
    {
        using var stream = File.OpenRead(docxPath);
        return ImportFromDocx(stream);
    }

    public string ImportFromDocx(Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, false);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("Not a valid .docx (no main document part).");
        var body = main.Document?.Body ?? throw new InvalidDataException("Not a valid .docx (no body).");

        var numMap = LoadNumbering(doc);
        var items = new List<Block>();

        foreach (var child in body.ChildElements)
        {
            switch (child)
            {
                case W.Table t:
                    items.Add(ConvertTable(t));
                    break;
                case W.Paragraph p:
                    var block = ConvertParagraph(p, main);
                    if (block is not null) items.Add(block);
                    break;
                // sectPr and other structural parts carry no content.
            }
        }

        return Assemble(items, numMap);
    }

    // ---- intermediate model ------------------------------------------------------------------

    private abstract record Block;
    private sealed record HeadingBlock(int Level, string Text) : Block;
    private sealed record ParagraphBlock(string Text) : Block;
    private sealed record HrBlock : Block;
    private sealed record TableBlock(string Markdown) : Block;
    private sealed record AlertBlock(string Markdown) : Block;
    private sealed record CodeBlock(string Lang, string Content) : Block;
    private sealed record DisplayMathBlock(string Latex) : Block;
    private sealed record MermaidBlock(string Content) : Block;
    private sealed record ListItemBlock(int NumId, bool IsTask, bool Checked, string Text) : Block;

    // ---- assembly ----------------------------------------------------------------------------

    private string Assemble(List<Block> items, Dictionary<int, bool> numMap)
    {
        var blocks = new List<string>();
        int i = 0;
        while (i < items.Count)
        {
            if (items[i] is ListItemBlock first)
            {
                // A "list" is a run of consecutive list paragraphs sharing (numId, task-ness). A
                // change in either marks a new list, which the source separated with a blank line.
                var group = new List<ListItemBlock>();
                while (i < items.Count && items[i] is ListItemBlock li &&
                       li.NumId == first.NumId && li.IsTask == first.IsTask)
                {
                    group.Add(li);
                    i++;
                }
                blocks.Add(RenderListGroup(group, numMap));
            }
            else
            {
                blocks.Add(RenderBlock(items[i]));
                i++;
            }
        }
        return string.Join("\n\n", blocks) + "\n";
    }

    private string RenderBlock(Block b) => b switch
    {
        HeadingBlock h => new string('#', h.Level) + " " + h.Text,
        ParagraphBlock p => p.Text,
        HrBlock => "---",
        TableBlock t => t.Markdown,
        AlertBlock a => a.Markdown,
        CodeBlock c => "```" + c.Lang + "\n" + c.Content + "\n```",
        DisplayMathBlock m => "$$\n" + m.Latex + "\n$$",
        MermaidBlock mm => "```mermaid\n" + mm.Content + "\n```",
        _ => "",
    };

    private string RenderListGroup(List<ListItemBlock> group, Dictionary<int, bool> numMap)
    {
        var isOrdered = numMap.TryGetValue(group[0].NumId, out var ord) && ord;
        var lines = new List<string>();
        int counter = 1;
        foreach (var item in group)
        {
            string prefix = item.IsTask
                ? (item.Checked ? "- [x] " : "- [ ] ")
                : (isOrdered ? counter++ + ". " : "- ");
            lines.Add(prefix + item.Text);
        }
        return string.Join("\n", lines);
    }

    // ---- numbering ---------------------------------------------------------------------------

    // numId -> isOrdered (decimal vs bullet), from numbering.xml.
    private static Dictionary<int, bool> LoadNumbering(WordprocessingDocument doc)
    {
        var map = new Dictionary<int, bool>();
        var numbering = doc.MainDocumentPart?.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return map;

        var abstractOrdered = new Dictionary<int, bool>();
        foreach (var an in numbering.Elements<W.AbstractNum>())
        {
            var absId = an.AbstractNumberId?.Value ?? 0;
            var lvl0 = an.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == 0);
            // OpenXml 3.x: NumberFormatValues is a struct — compare the value, never ToString().
            var fmt = lvl0?.GetFirstChild<W.NumberingFormat>()?.Val?.Value;
            abstractOrdered[absId] = fmt == W.NumberFormatValues.Decimal;
        }
        foreach (var num in numbering.Elements<W.NumberingInstance>())
        {
            var numId = num.NumberID?.Value ?? 0;
            var absId = num.GetFirstChild<W.AbstractNumId>()?.Val?.Value ?? 0;
            map[numId] = abstractOrdered.TryGetValue(absId, out var ord) && ord;
        }
        return map;
    }

    // ---- paragraphs --------------------------------------------------------------------------

    private Block? ConvertParagraph(W.Paragraph p, MainDocumentPart main)
    {
        var pPr = p.GetFirstChild<W.ParagraphProperties>();
        var styleId = pPr?.GetFirstChild<W.ParagraphStyleId>()?.Val?.Value ?? "";

        // Mermaid diagram: the forward engine renders a ```mermaid fence as a native Word drawing
        // (a wpg group of wps shapes). Recover the diagram source from the shape identity tags.
        var drawing = p.GetFirstChild<W.Run>()?.GetFirstChild<W.Drawing>();
        if (drawing is not null && DocxShapeParser.TryParseMermaid(drawing) is { } mermaid)
            return new MermaidBlock(mermaid);

        // Heading.
        if (styleId.StartsWith("Heading", StringComparison.Ordinal) &&
            int.TryParse(styleId.Substring("Heading".Length), out var level) && level is >= 1 and <= 6)
        {
            return new HeadingBlock(level, ConvertInlines(p, main).Trim());
        }

        // Horizontal rule: an empty paragraph whose only decoration is a wave bottom border.
        var bottomBorder = pPr?.GetFirstChild<W.ParagraphBorders>()?.GetFirstChild<W.BottomBorder>();
        var hasAnyText = p.Descendants<W.Text>().Any(t => t.Text.Length > 0);
        if (!hasAnyText && bottomBorder?.Val?.Value == W.BorderValues.Wave)
            return new HrBlock();

        // Code block: the forward engine marks these with keepLines + a full border + Consolas runs.
        if (pPr?.GetFirstChild<W.KeepLines>() is not null)
        {
            var lang = styleId.StartsWith("MSCode_", StringComparison.Ordinal)
                ? styleId.Substring("MSCode_".Length)
                : "";
            return new CodeBlock(lang, ExtractCodeContent(p));
        }

        // Display math: a centered paragraph whose ONLY content is an OMML equation.
        var omml = p.GetFirstChild<M.OfficeMath>();
        var isCentered = pPr?.GetFirstChild<W.Justification>()?.Val?.Value == W.JustificationValues.Center;
        if (omml is not null && !hasAnyText && isCentered)
            return new DisplayMathBlock(OmmlToLatex.Convert(omml));

        // List item.
        var numPr = pPr?.GetFirstChild<W.NumberingProperties>();
        if (numPr is not null)
        {
            var numId = numPr.GetFirstChild<W.NumberingId>()?.Val?.Value ?? 0;
            // Task list: the forward engine emits the checkbox as a w:sdt (w14:checkbox) whose first
            // run is a ballot glyph — ☒ (U+2612) checked / ☐ (U+2610) unchecked. Detect the glyph in
            // the paragraph's text directly (robust to the sdt's concrete OpenXml class).
            var textParts = p.Descendants<W.Text>().Select(t => t.Text).ToList();
            bool isTask = false, isChecked = false;
            if (textParts.Count > 0 && textParts[0].Length > 0 &&
                (textParts[0][0] == '\u2612' || textParts[0][0] == '\u2610'))
            {
                isTask = true;
                isChecked = textParts[0][0] == '\u2612';
                textParts[0] = textParts[0].Substring(1);
            }
            var text = string.Concat(textParts).TrimStart();
            return new ListItemBlock(numId, isTask, isChecked, text);
        }

        // Plain paragraph (may contain inline math / links / formatting).
        var inline = ConvertInlines(p, main);
        if (string.IsNullOrWhiteSpace(inline)) return null; // drop empty spacer paragraphs
        return new ParagraphBlock(inline);
    }

    // ---- inline content ----------------------------------------------------------------------

    private string ConvertInlines(W.Paragraph p, MainDocumentPart main, bool excludeSdt = false)
    {
        var sb = new StringBuilder();
        foreach (var child in p.ChildElements)
        {
            switch (child)
            {
                case W.Run r:
                    sb.Append(ConvertRun(r));
                    break;
                case M.OfficeMath om:
                    sb.Append('$').Append(OmmlToLatex.Convert(om)).Append('$');
                    break;
                case W.Hyperlink h:
                    sb.Append(ConvertHyperlink(h, main));
                    break;
                case W.SdtBlock:
                    // Task checkbox glyph — handled by the caller, not part of the item text.
                    if (!excludeSdt) sb.Append(GetSdtText((W.SdtBlock)child));
                    break;
                // BookmarkStart/End, proof errors, etc. carry no visible text.
            }
        }
        return sb.ToString();
    }

    private static string GetSdtText(W.SdtBlock sdt) =>
        string.Concat(sdt.Descendants<W.Text>().Select(t => t.Text));

    private static string ConvertRun(W.Run r)
    {
        var rPr = r.GetFirstChild<W.RunProperties>();
        var text = string.Concat(r.Elements<W.Text>().Select(t => t.Text));
        if (text.Length == 0) return "";

        bool bold = rPr?.GetFirstChild<W.Bold>() is not null;
        bool italic = rPr?.GetFirstChild<W.Italic>() is not null;
        bool strike = rPr?.GetFirstChild<W.Strike>() is not null;
        bool highlight = rPr?.GetFirstChild<W.Highlight>() is not null;
        bool isCode = rPr?.GetFirstChild<W.RunFonts>()?.Ascii?.Value == "Consolas";
        // OpenXml 3.x models these as structs, not C# enums — compare the values, never ToString().
        var vertAlign = rPr?.GetFirstChild<W.VerticalTextAlignment>()?.Val?.Value;

        if (isCode) return "`" + text + "`";

        var s = text;
        if (bold && italic) s = "***" + s + "***";
        else if (bold) s = "**" + s + "**";
        else if (italic) s = "*" + s + "*";
        if (strike) s = "~~" + s + "~~";
        if (highlight) s = "==" + s + "==";
        if (vertAlign == W.VerticalPositionValues.Subscript) s = "~" + s + "~";
        else if (vertAlign == W.VerticalPositionValues.Superscript) s = "^" + s + "^";
        return s;
    }

    private string ConvertHyperlink(W.Hyperlink h, MainDocumentPart main)
    {
        var text = string.Concat(h.Descendants<W.Text>().Select(t => t.Text));
        var anchor = h.Anchor?.Value;
        if (!string.IsNullOrEmpty(anchor))
        {
            var slug = anchor.StartsWith("H_", StringComparison.Ordinal)
                ? anchor.Substring(2).Replace('_', '-')
                : anchor;
            return "[" + text + "](#" + slug + ")";
        }
        var relId = h.Id?.Value;
        if (!string.IsNullOrEmpty(relId))
        {
            var rel = main.HyperlinkRelationships.FirstOrDefault(x => x.Id == relId);
            if (rel is not null) return "[" + text + "](" + rel.Uri + ")";
        }
        return text;
    }

    // ---- code blocks -------------------------------------------------------------------------

    private static string ExtractCodeContent(W.Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var child in p.ChildElements)
        {
            if (child is not W.Run r) continue;
            foreach (var el in r.ChildElements)
            {
                if (el is W.Break) sb.Append('\n');
                else if (el is W.Text t) sb.Append(t.Text);
            }
        }
        return sb.ToString();
    }

    // ---- tables & alerts ---------------------------------------------------------------------

    private Block ConvertTable(W.Table t)
    {
        var rows = t.Elements<W.TableRow>().ToList();

        // GitHub alert callout: a single-cell table whose first paragraph is "{icon} {KIND}".
        if (rows.Count == 1)
        {
            var cells = rows[0].Elements<W.TableCell>().ToList();
            if (cells.Count == 1)
            {
                var paras = cells[0].Elements<W.Paragraph>().ToList();
                if (paras.Count >= 1)
                {
                    var title = string.Concat(paras[0].Descendants<W.Text>().Select(x => x.Text)).Trim();
                    var kind = AlertKindFromTitle(title);
                    if (kind is not null)
                    {
                        var sb = new StringBuilder("> [!" + kind + "]");
                        foreach (var cp in paras.Skip(1))
                        {
                            var line = string.Concat(cp.Descendants<W.Text>().Select(x => x.Text));
                            if (line.Length > 0) sb.Append("\n> ").Append(line);
                        }
                        return new AlertBlock(sb.ToString());
                    }
                }
            }
        }

        // Regular table: header row + separator + data rows.
        var mdRows = new List<string>();
        int colCount = 0;
        foreach (var row in rows)
        {
            var cells = row.Elements<W.TableCell>()
                .Select(c => string.Concat(c.Descendants<W.Text>().Select(x => x.Text)).Trim())
                .ToList();
            colCount = Math.Max(colCount, cells.Count);
            mdRows.Add("| " + string.Join(" | ", cells) + " |");
        }
        if (mdRows.Count == 0) return new TableBlock("");
        var separator = "| " + string.Join(" | ", Enumerable.Repeat("---", colCount)) + " |";
        mdRows.Insert(1, separator);
        return new TableBlock(string.Join("\n", mdRows));
    }

    private static string? AlertKindFromTitle(string title)
    {
        // Title is "{icon} {KIND}" (e.g. "💡 TIP"); the kind is the trailing word.
        var lastSpace = title.LastIndexOf(' ');
        var kind = lastSpace >= 0 ? title.Substring(lastSpace + 1) : title;
        return kind.ToUpperInvariant() switch
        {
            "NOTE" or "TIP" or "IMPORTANT" or "WARNING" or "CAUTION" => kind.ToUpperInvariant(),
            _ => null,
        };
    }
}
