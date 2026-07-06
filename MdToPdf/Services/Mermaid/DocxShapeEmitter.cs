using System.Text;
using MdToPdf.Models;

namespace MdToPdf.Services.Mermaid;

// ShapeForge's final stage: turns an MDiagram (pure geometry, points) into a single inline Word
// drawing — a wpg (WordprocessingGroup) containing native wps shapes. The result is a REAL Word
// object: select the group, drag it, ungroup it, restyle any node, edit any label. No images.
//
// Emitted as an XML string for a <w:p> paragraph; callers materialize it with
// `new W.Paragraph { InnerXml = ... }` so the OpenXml SDK parses it into typed elements
// (Office 2010 wps/wpg classes) and the document stays schema-validatable.
public static class DocxShapeEmitter
{
    private const long EmuPerPt = 12700;

    private static void ScaleToFit(MDiagram d)
    {
        double s = Math.Min(1, Math.Min(
            MaxCanvasW / Math.Max(1, d.Width),
            MaxCanvasH / Math.Max(1, d.Height)));
        if (s >= 1) return;

        foreach (var sh in d.Shapes)
        {
            sh.X *= s; sh.Y *= s; sh.W *= s; sh.H *= s;
            sh.FontSize = Math.Max(6, sh.FontSize * s); // keep labels legible
        }
        foreach (var c in d.Connectors)
        {
            c.X1 *= s; c.Y1 *= s; c.X2 *= s; c.Y2 *= s;
            c.LabelX *= s; c.LabelY *= s; c.LabelW *= s; c.LabelH *= s;
        }
        d.Width *= s; d.Height *= s;
    }

    // The printable window (A4/Letter minus margins). Diagrams wider or taller are scaled
    // uniformly so no renderer ever runs a bar or box past the page edge.
    private const double MaxCanvasW = 460, MaxCanvasH = 640;

    public static string ToParagraphXml(MDiagram d, ThemeDefinition theme, uint docPrId)
    {
        ScaleToFit(d);

        var ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                 "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
                 "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                 "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
                 "xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\"";

        long cx = Emu(Math.Max(1, d.Width)), cy = Emu(Math.Max(1, d.Height));
        var sb = new StringBuilder();
        uint id = 1;

        sb.Append($"<w:r {ns}><w:drawing>");
        sb.Append($"<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">");
        sb.Append($"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>");
        sb.Append($"<wp:docPr id=\"{docPrId}\" name=\"ShapeForge diagram {docPrId}\" descr=\"Mermaid diagram rebuilt as editable Word shapes by Marksmith\"/>");
        sb.Append("<wp:cNvGraphicFramePr/>");
        sb.Append("<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\">");
        sb.Append("<wpg:wgp>");
        sb.Append($"<wpg:cNvGrpSpPr/>");
        sb.Append($"<wpg:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/>" +
                  $"<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:noFill/></wpg:grpSpPr>");

        // Connectors first so nodes draw on top of lines.
        foreach (var c in d.Connectors)
        {
            sb.Append(ConnectorXml(c, theme, ref id));
            if (!string.IsNullOrWhiteSpace(c.Label))
            {
                id++;
                sb.Append(ShapeXml(new MShape
                {
                    Kind = ShapeKind.Text,
                    X = c.LabelX, Y = c.LabelY, W = c.LabelW, H = c.LabelH,
                    Text = c.Label, FontSize = 8.5, TextColor = null,
                }, theme, id));
            }
        }
        foreach (var s in d.Shapes)
        {
            id++;
            sb.Append(ShapeXml(s, theme, id));
        }

        sb.Append("</wpg:wgp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>");
        return sb.ToString();
    }

    // ---- shapes -------------------------------------------------------------------------------

    private static string ShapeXml(MShape s, ThemeDefinition t, uint id)
    {
        var (prst, avLst) = Preset(s);
        string fill = FillXml(s, t);
        string line = LineXml(s, t);
        long x = Emu(s.X), y = Emu(s.Y), w = Emu(Math.Max(0.5, s.W)), h = Emu(Math.Max(0.5, s.H));

        var sb = new StringBuilder();
        sb.Append("<wps:wsp>");
        sb.Append($"<wps:cNvPr id=\"{id}\" name=\"{Esc(ShapeName(s))} {id}\"/>");
        sb.Append("<wps:cNvSpPr/>");
        sb.Append("<wps:spPr>");
        sb.Append($"<a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm>");
        sb.Append($"<a:prstGeom prst=\"{prst}\">{avLst}</a:prstGeom>");
        sb.Append(fill);
        sb.Append(line);
        sb.Append("</wps:spPr>");

        if (!string.IsNullOrEmpty(s.Text))
        {
            sb.Append("<wps:txbx><w:txbxContent>");
            var lines = s.Text.Replace("\r", "").Split('\n');
            foreach (var lineText in lines)
            {
                sb.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"216\" w:lineRule=\"auto\"/><w:jc w:val=\"center\"/><w:rPr>");
                sb.Append(RunProps(s, t));
                sb.Append("</w:rPr></w:pPr>");
                sb.Append($"<w:r><w:rPr>{RunProps(s, t)}</w:rPr><w:t xml:space=\"preserve\">{Esc(lineText)}</w:t></w:r>");
                sb.Append("</w:p>");
            }
            sb.Append("</w:txbxContent></wps:txbx>");
        }
        // bodyPr is required (last child). Tight insets; vertical centering.
        sb.Append("<wps:bodyPr rot=\"0\" wrap=\"square\" lIns=\"12700\" tIns=\"6350\" rIns=\"12700\" bIns=\"6350\" anchor=\"ctr\" anchorCtr=\"0\"><a:noAutofit/></wps:bodyPr>");
        sb.Append("</wps:wsp>");
        return sb.ToString();
    }

    private static string RunProps(MShape s, ThemeDefinition t)
    {
        var color = Hex(s.TextColor ?? t.Text);
        var sz = (int)Math.Round(s.FontSize * 2); // half-points
        return (s.Bold ? "<w:b/>" : "") +
               $"<w:color w:val=\"{color}\"/><w:sz w:val=\"{sz}\"/><w:szCs w:val=\"{sz}\"/>";
    }

    private static string ShapeName(MShape s) => s.Kind switch
    {
        ShapeKind.Text => "Label",
        ShapeKind.Frame => "Frame",
        ShapeKind.Pie => "Pie wedge",
        _ => s.Kind.ToString(),
    };

    private static (string prst, string avLst) Preset(MShape s)
    {
        switch (s.Kind)
        {
            case ShapeKind.Rect or ShapeKind.Frame or ShapeKind.Text: return ("rect", "<a:avLst/>");
            case ShapeKind.RoundRect: return ("roundRect", "<a:avLst><a:gd name=\"adj\" fmla=\"val 16667\"/></a:avLst>");
            case ShapeKind.Diamond: return ("diamond", "<a:avLst/>");
            case ShapeKind.Ellipse or ShapeKind.Circle: return ("ellipse", "<a:avLst/>");
            case ShapeKind.Parallelogram: return ("parallelogram", "<a:avLst/>");
            case ShapeKind.Trapezoid: return ("trapezoid", "<a:avLst/>");
            case ShapeKind.Cylinder: return ("can", "<a:avLst/>");
            case ShapeKind.Hexagon: return ("hexagon", "<a:avLst/>");
            case ShapeKind.Pie:
            {
                // DrawingML pie: adj1/adj2 in 60000ths of a degree, 0 = 3 o'clock, clockwise.
                long a1 = (long)Math.Round(Norm(s.AdjStartDeg) * 60000);
                long a2 = (long)Math.Round(Norm(s.AdjEndDeg) * 60000);
                return ("pie", $"<a:avLst><a:gd name=\"adj1\" fmla=\"val {a1}\"/><a:gd name=\"adj2\" fmla=\"val {a2}\"/></a:avLst>");
            }
            default: return ("rect", "<a:avLst/>");
        }
    }

    private static double Norm(double deg) { deg %= 360; if (deg < 0) deg += 360; return deg; }

    private static string FillXml(MShape s, ThemeDefinition t)
    {
        if (s.Kind is ShapeKind.Frame or ShapeKind.Text) return "<a:noFill/>";
        var fill = s.Fill ?? t.Secondary;
        return $"<a:solidFill><a:srgbClr val=\"{Hex(fill)}\"/></a:solidFill>";
    }

    private static string LineXml(MShape s, ThemeDefinition t)
    {
        if (s.Kind == ShapeKind.Text) return "<a:ln><a:noFill/></a:ln>";
        var stroke = Hex(s.Stroke ?? t.Border);
        long w = (long)Math.Round(s.StrokeWidth * EmuPerPt);
        var dash = s.Dashed ? "<a:prstDash val=\"dash\"/>" : "";
        return $"<a:ln w=\"{w}\"><a:solidFill><a:srgbClr val=\"{stroke}\"/></a:solidFill>{dash}</a:ln>";
    }

    // ---- connectors ---------------------------------------------------------------------------

    private static string ConnectorXml(MConnector c, ThemeDefinition t, ref uint id)
    {
        // Degenerate elbows (both endpoints on one axis) can't be drawn by bentConnector3 — a
        // zero-width/height box renders as a straight line. These are loops (a sequence
        // self-message, a same-row relationship): decompose into a 3-segment U shape.
        if (c.Elbow && Math.Abs(c.X2 - c.X1) < 2)   // vertical → U out to the right
        {
            double bulge = 30;
            var s1 = Seg(c, c.X1, c.Y1, c.X1 + bulge, c.Y1, c.StartHead, ArrowHead.None);
            var s2 = Seg(c, c.X1 + bulge, c.Y1, c.X1 + bulge, c.Y2, ArrowHead.None, ArrowHead.None);
            var s3 = Seg(c, c.X1 + bulge, c.Y2, c.X2, c.Y2, ArrowHead.None, c.EndHead);
            return StraightXml(s1, t, ++id) + StraightXml(s2, t, ++id) + StraightXml(s3, t, ++id);
        }
        if (c.Elbow && Math.Abs(c.Y2 - c.Y1) < 2)   // horizontal → U bowing down
        {
            double bulge = 40;
            var s1 = Seg(c, c.X1, c.Y1, c.X1, c.Y1 + bulge, c.StartHead, ArrowHead.None);
            var s2 = Seg(c, c.X1, c.Y1 + bulge, c.X2, c.Y1 + bulge, ArrowHead.None, ArrowHead.None);
            var s3 = Seg(c, c.X2, c.Y1 + bulge, c.X2, c.Y2, ArrowHead.None, c.EndHead);
            return StraightXml(s1, t, ++id) + StraightXml(s2, t, ++id) + StraightXml(s3, t, ++id);
        }

        id++;

        // A straight/bent connector draws from the top-left to the bottom-right of its box; encode
        // direction with flipH/flipV so any orientation works.
        double x = Math.Min(c.X1, c.X2), y = Math.Min(c.Y1, c.Y2);
        double w = Math.Abs(c.X2 - c.X1), h = Math.Abs(c.Y2 - c.Y1);
        bool flipH = c.X2 < c.X1, flipV = c.Y2 < c.Y1;
        var prst = c.Elbow ? "bentConnector3" : "straightConnector1";

        var stroke = Hex(c.Stroke ?? t.Line);
        long lw = (long)Math.Round(c.StrokeWidth * EmuPerPt);
        var dash = c.Dashed ? "<a:prstDash val=\"dash\"/>" : "";
        var head = HeadXml("headEnd", c.StartHead);
        var tail = HeadXml("tailEnd", c.EndHead);
        var flips = (flipH ? " flipH=\"1\"" : "") + (flipV ? " flipV=\"1\"" : "");

        return
            "<wps:wsp>" +
            $"<wps:cNvPr id=\"{id}\" name=\"Connector {id}\"/>" +
            "<wps:cNvCnPr/>" +
            "<wps:spPr>" +
            $"<a:xfrm{flips}><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>" +
            $"<a:prstGeom prst=\"{prst}\"><a:avLst/></a:prstGeom>" +
            "<a:noFill/>" +
            $"<a:ln w=\"{lw}\"><a:solidFill><a:srgbClr val=\"{stroke}\"/></a:solidFill>{dash}{head}{tail}</a:ln>" +
            "</wps:spPr>" +
            "<wps:bodyPr/>" +
            "</wps:wsp>";
    }

    private static MConnector Seg(MConnector src, double x1, double y1, double x2, double y2, ArrowHead start, ArrowHead end) =>
        new()
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Elbow = false,
            StartHead = start, EndHead = end,
            Dashed = src.Dashed, Stroke = src.Stroke, StrokeWidth = src.StrokeWidth,
        };

    // A single straight segment (used by the U-loop decomposition). Same emission as the main path
    // but never recurses.
    private static string StraightXml(MConnector c, ThemeDefinition t, uint id)
    {
        double x = Math.Min(c.X1, c.X2), y = Math.Min(c.Y1, c.Y2);
        double w = Math.Abs(c.X2 - c.X1), h = Math.Abs(c.Y2 - c.Y1);
        bool flipH = c.X2 < c.X1, flipV = c.Y2 < c.Y1;
        var stroke = Hex(c.Stroke ?? t.Line);
        long lw = (long)Math.Round(c.StrokeWidth * EmuPerPt);
        var dash = c.Dashed ? "<a:prstDash val=\"dash\"/>" : "";
        var flips = (flipH ? " flipH=\"1\"" : "") + (flipV ? " flipV=\"1\"" : "");
        return
            "<wps:wsp>" +
            $"<wps:cNvPr id=\"{id}\" name=\"Connector {id}\"/>" +
            "<wps:cNvCnPr/>" +
            "<wps:spPr>" +
            $"<a:xfrm{flips}><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>" +
            "<a:prstGeom prst=\"straightConnector1\"><a:avLst/></a:prstGeom>" +
            "<a:noFill/>" +
            $"<a:ln w=\"{lw}\"><a:solidFill><a:srgbClr val=\"{stroke}\"/></a:solidFill>{dash}{HeadXml("headEnd", c.StartHead)}{HeadXml("tailEnd", c.EndHead)}</a:ln>" +
            "</wps:spPr>" +
            "<wps:bodyPr/>" +
            "</wps:wsp>";
    }

    private static string HeadXml(string el, ArrowHead h) => h switch
    {
        ArrowHead.None => $"<a:{el} type=\"none\"/>",
        ArrowHead.Triangle => $"<a:{el} type=\"triangle\" w=\"med\" len=\"med\"/>",
        ArrowHead.Open => $"<a:{el} type=\"arrow\" w=\"med\" len=\"med\"/>",
        ArrowHead.Diamond => $"<a:{el} type=\"diamond\" w=\"med\" len=\"med\"/>",
        ArrowHead.Oval => $"<a:{el} type=\"oval\" w=\"med\" len=\"med\"/>",
        ArrowHead.Stealth => $"<a:{el} type=\"stealth\" w=\"med\" len=\"med\"/>",
        _ => $"<a:{el} type=\"none\"/>",
    };

    // ---- utils --------------------------------------------------------------------------------

    private static long Emu(double pt) => (long)Math.Round(pt * EmuPerPt);

    private static string Hex(string css)
    {
        var s = css.TrimStart('#');
        return (s.Length >= 6 ? s[..6] : s.PadLeft(6, '0')).ToUpperInvariant();
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
