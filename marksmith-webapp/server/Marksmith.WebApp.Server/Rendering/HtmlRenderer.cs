using System.Text;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkSmith.WebApp.Server.Documents;

namespace MarkSmith.WebApp.Server.Rendering;

/// <summary>
/// Full-document DOCX -> HTML renderer. v1 deliberately renders the whole document after each
/// sequenced batch (no delta rendering): the spec's "~200ms for a medium doc" budget is met by
/// batching client edits server-side, not by incremental DOM diffing. The output is a single
/// self-contained HTML fragment the client drops into its contenteditable surface.
///
/// Round-tripping rule: the fragment is a *view* of the server's authoritative OOXML, not a
/// second source of truth. Comments, track changes, images and hyperlinks are emitted with
/// stable data attributes the client uses to map DOM ranges back to OOXML anchors.
/// </summary>
public sealed class HtmlRenderer
{
    /// <summary>Renders the whole document body as an HTML fragment (no &lt;html&gt; wrapper).</summary>
    public string Render(DocxDocument doc)
    {
        var sb = new StringBuilder();
        var main = doc.MainPart;
        foreach (var child in doc.DocumentBody.ChildElements)
        {
            switch (child)
            {
                case Paragraph p: sb.Append(RenderParagraph(p, main)); break;
                case Table t: sb.Append(RenderTable(t, main)); break;
            }
        }
        return sb.ToString();
    }

    private static string RenderParagraph(Paragraph p, DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart)
    {
        var sb = new StringBuilder();
        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
        var tag = style switch
        {
            "Heading1" => "h1",
            "Heading2" => "h2",
            "Heading3" => "h3",
            "Heading4" => "h4",
            "Heading5" => "h5",
            "Heading6" => "h6",
            _ => "p",
        };

        sb.Append($"<{tag} data-ms-style=\"{Escape(style)}\"");

        var jc = p.ParagraphProperties?.Justification?.Val?.Value;
        var indent = p.ParagraphProperties?.Indentation?.Left?.Value;
        if (jc is not null || indent is not null)
        {
            var styles = new List<string>();
            if (jc is not null) styles.Add($"text-align:{MapAlign(jc.Value)}");
            if (indent is not null) styles.Add($"margin-left:{Escape(indent)}");
            sb.Append($" style=\"{string.Join(';', styles)}\"");
        }

        var numPr = p.ParagraphProperties?.NumberingProperties;
        if (numPr is not null)
        {
            sb.Append($" data-ms-list=\"{numPr.NumberingId?.Val?.Value ?? 0}\" data-ms-level=\"{numPr.NumberingLevelReference?.Val?.Value ?? 0}\"");
        }

        sb.Append('>');

        // Emit comment anchors in document order: <mark data-ms-comment="id"> around the anchored
        // text, or a standalone marker when the range is empty.
        var commentRanges = p.Descendants<CommentRangeStart>().ToList();
        if (commentRanges.Count > 0)
        {
            foreach (var start in commentRanges)
            {
                var id = start.Id?.Value ?? "0";
                sb.Append($"<mark data-ms-comment=\"{Escape(id)}\">");
            }
            // The range end markers close in the same nesting order; we approximate by closing
            // after the anchored content (v1: one mark per comment range).
        }

        foreach (var child in p.ChildElements)
        {
            switch (child)
            {
                case Run r when r.Elements<CommentReference>().Any(): break; // comment ref run renders nothing
                case Run r: sb.Append(RenderRun(r)); break;
                case Hyperlink h: sb.Append(RenderHyperlink(h, mainPart)); break;
                case InsertedRun ins: sb.Append(RenderTrackChange(ins)); break;
                case DeletedRun del: sb.Append(RenderTrackChange(del)); break;
                case CommentRangeStart: break;
                case CommentRangeEnd: sb.Append("</mark>"); break;
            }
        }

        if (commentRanges.Count > 0) sb.Append("</mark>");
        sb.Append($"</{tag}>");
        return sb.ToString();
    }

    private static string RenderRun(Run run)
    {
        var sb = new StringBuilder();
        var rpr = run.RunProperties;
        var bold = rpr?.Bold?.Val?.Value == true;
        var italic = rpr?.Italic?.Val?.Value == true;
        var underline = rpr?.Underline?.Val?.Value != null && rpr.Underline.Val.Value != UnderlineValues.None;
        var strike = rpr?.Strike?.Val?.Value == true;
        var color = rpr?.Color?.Val?.Value;

        var styles = new List<string>();
        if (bold) styles.Add("font-weight:700");
        if (italic) styles.Add("font-style:italic");
        if (underline) styles.Add("text-decoration:underline");
        if (strike) styles.Add("text-decoration:line-through");
        if (color is not null && color.Length > 0 && color != "auto") styles.Add($"color:#{color}");

        var span = styles.Count > 0 ? $"<span style=\"{string.Join(';', styles)}\">" : "<span>";
        sb.Append(span);

        foreach (var el in run.ChildElements)
        {
            switch (el)
            {
                case Text t:
                    sb.Append(Escape(t.Text ?? ""));
                    break;
                case Break:
                    sb.Append("<br/>");
                    break;
                case Drawing d:
                    sb.Append(RenderDrawing(d));
                    break;
            }
        }

        sb.Append("</span>");
        return sb.ToString();
    }

    private static string RenderHyperlink(Hyperlink h, DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart)
    {
        // h.Id is the relationship id; find target URI from main part relationships.
        var relId = h.Id?.Value;
        var rel = (relId is not null ? mainPart.HyperlinkRelationships.FirstOrDefault(r => r.Id == relId)?.Uri.OriginalString : null) ?? relId ?? "#";
        var sb = new StringBuilder();
        sb.Append($"<a href=\"{Escape(rel)}\" target=\"_blank\" rel=\"noopener\" data-ms-hyperlink=\"1\">");
        foreach (var run in h.Elements<Run>()) sb.Append(RenderRun(run));
        sb.Append("</a>");
        return sb.ToString();
    }

    private static string RenderTrackChange(InsertedRun ins)
    {
        var id = ins.Id?.Value ?? "0";
        var sb = new StringBuilder();
        sb.Append($"<ins data-ms-change=\"{Escape(id)}\" data-ms-change-type=\"insert\" data-ms-author=\"{Escape(ins.Author?.Value ?? "")}\">");
        foreach (var run in ins.Elements<Run>()) sb.Append(RenderRun(run));
        sb.Append("</ins>");
        return sb.ToString();
    }

    private static string RenderTrackChange(DeletedRun del)
    {
        var id = del.Id?.Value ?? "0";
        var sb = new StringBuilder();
        sb.Append($"<del data-ms-change=\"{Escape(id)}\" data-ms-change-type=\"delete\" data-ms-author=\"{Escape(del.Author?.Value ?? "")}\">");
        foreach (var run in del.Elements<Run>()) sb.Append(RenderRun(run));
        sb.Append("</del>");
        return sb.ToString();
    }

    private static string RenderDrawing(Drawing drawing)
    {
        var docPr = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>().FirstOrDefault();
        var id = docPr?.Name?.Value ?? "image";
        var alt = docPr?.Description?.Value ?? "";
        return $"<img data-ms-image=\"{Escape(id)}\" alt=\"{Escape(alt)}\" data-ms-image-placeholder=\"1\"/>";
    }

    private static string RenderTable(Table t, DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart)
    {
        var sb = new StringBuilder();
        sb.Append("<table data-ms-table=\"1\"><tbody>");
        foreach (var row in t.Elements<TableRow>())
        {
            sb.Append("<tr>");
            foreach (var cell in row.Elements<TableCell>())
            {
                sb.Append("<td>");
                foreach (var child in cell.ChildElements)
                {
                    if (child is Paragraph p) sb.Append(RenderParagraph(p, mainPart));
                }
                sb.Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private static string MapAlign(JustificationValues jc)
    {
        if (jc == JustificationValues.Center) return "center";
        if (jc == JustificationValues.Right) return "right";
        if (jc == JustificationValues.Both) return "justify";
        return "left";
    }

    private static string Escape(string s) =>
        System.Net.WebUtility.HtmlEncode(s);
}
