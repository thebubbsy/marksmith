using System.Globalization;
using System.Text;
using System.Xml.Linq;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Services;

// Tier 2 last-resort for diagrams. When a <w:drawing> holds shapes that DocxShapeParser cannot
// recover as Mermaid (SmartArt, freeform paths, hand-drawn grouped objects from another tool), this
// rasterizes the DrawingML to a PNG so the diagram still survives the import as a visible image
// rather than vanishing. The mapping is deliberately simple — preset geometry (rect / rounded rect /
// ellipse / diamond) plus connectors and text become SVG primitives, then SvgRasterizer paints them.
// Anything the mapper can't express degrades gracefully; the caller emits a placeholder comment when
// even rasterization fails, so content is never silently dropped.
public static class DocxDrawingRasterizer
{
    private static readonly XNamespace Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const double EmuPerPixel = 9525.0; // 914400 EMU/inch at 96 dpi

    /// <summary>
    /// Attempts to rasterize a shape drawing to <c>media/diagramN.png</c>. Returns the
    /// Markdown-relative path on success, or null when the drawing has no rasterizable geometry.
    /// </summary>
    public static string? TryRasterize(W.Drawing drawing, string mediaDir, ref int diagramCounter)
    {
        try
        {
            var svg = BuildSvg(drawing);
            if (svg is null) return null;

            var png = SvgRasterizer.ToPng(svg);
            if (png is null || png.Length == 0) return null;

            var relative = $"media/diagram{++diagramCounter}.png";
            var full = Path.Combine(mediaDir, relative);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(full, png);
            return relative;
        }
        catch
        {
            return null; // rasterization is best-effort; the caller falls back to a comment
        }
    }

    // Serializes the drawing's shapes to a standalone SVG document, or null when there is nothing to
    // draw. Coordinates are converted from EMU to pixels; the viewBox is sized to the shapes' extent.
    private static string? BuildSvg(W.Drawing drawing)
    {
        var doc = XDocument.Parse(drawing.InnerXml);
        var shapes = new List<ShapeGeom>();
        double maxX = 100, maxY = 100;

        foreach (var wsp in doc.Descendants(Wps + "wsp"))
        {
            var xfrm = wsp.Elements(Wps + "spPr").Elements(A + "xfrm").FirstOrDefault();
            if (xfrm is null) continue;
            var off = xfrm.Element(A + "off");
            var ext = xfrm.Element(A + "ext");
            double x = Px(off?.Attribute("x")?.Value);
            double y = Px(off?.Attribute("y")?.Value);
            double w = Px(ext?.Attribute("cx")?.Value);
            double h = Px(ext?.Attribute("cy")?.Value);
            if (w <= 0 && h <= 0) continue;

            var preset = wsp.Elements(Wps + "spPr").Elements(A + "prstGeom").Attributes("prst").FirstOrDefault()?.Value ?? "rect";
            bool isConnector = preset is "straightConnector1" or "bentConnector3" or "bentConnector5" or "curvedConnector3";
            var text = ReadText(wsp);

            shapes.Add(new ShapeGeom { X = x, Y = y, W = w, H = h, Preset = preset, IsConnector = isConnector, Text = text });
            maxX = Math.Max(maxX, x + w + 20);
            maxY = Math.Max(maxY, y + h + 20);
        }

        if (shapes.Count == 0) return null;

        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(maxX)}\" height=\"{F(maxY)}\" viewBox=\"0 0 {F(maxX)} {F(maxY)}\">");
        sb.Append("<rect x=\"0\" y=\"0\" width=\"100%\" height=\"100%\" fill=\"white\"/>");

        foreach (var s in shapes)
        {
            if (s.IsConnector)
            {
                sb.Append($"<line x1=\"{F(s.X)}\" y1=\"{F(s.Y)}\" x2=\"{F(s.X + s.W)}\" y2=\"{F(s.Y + s.H)}\" stroke=\"#444\" stroke-width=\"1.5\"/>");
                continue;
            }

            switch (s.Preset)
            {
                case "ellipse":
                    sb.Append($"<ellipse cx=\"{F(s.X + s.W / 2)}\" cy=\"{F(s.Y + s.H / 2)}\" rx=\"{F(s.W / 2)}\" ry=\"{F(s.H / 2)}\" fill=\"#eef\" stroke=\"#333\" stroke-width=\"1.5\"/>");
                    break;
                case "diamond":
                    var pts = $"{F(s.X + s.W / 2)},{F(s.Y)} {F(s.X + s.W)},{F(s.Y + s.H / 2)} {F(s.X + s.W / 2)},{F(s.Y + s.H)} {F(s.X)},{F(s.Y + s.H / 2)}";
                    sb.Append($"<polygon points=\"{pts}\" fill=\"#eef\" stroke=\"#333\" stroke-width=\"1.5\"/>");
                    break;
                case "roundRect":
                    sb.Append($"<rect x=\"{F(s.X)}\" y=\"{F(s.Y)}\" width=\"{F(s.W)}\" height=\"{F(s.H)}\" rx=\"8\" fill=\"#eef\" stroke=\"#333\" stroke-width=\"1.5\"/>");
                    break;
                default: // rect and anything unrecognized → plain rectangle
                    sb.Append($"<rect x=\"{F(s.X)}\" y=\"{F(s.Y)}\" width=\"{F(s.W)}\" height=\"{F(s.H)}\" fill=\"#eef\" stroke=\"#333\" stroke-width=\"1.5\"/>");
                    break;
            }

            if (!string.IsNullOrWhiteSpace(s.Text))
            {
                var escaped = System.Security.SecurityElement.Escape(s.Text.Replace("\n", " "));
                sb.Append($"<text x=\"{F(s.X + s.W / 2)}\" y=\"{F(s.Y + s.H / 2)}\" font-family=\"sans-serif\" font-size=\"12\" fill=\"#111\" text-anchor=\"middle\" dominant-baseline=\"middle\">{escaped}</text>");
            }
        }

        sb.Append("</svg>");
        return sb.ToString();

        string F(double v) => v.ToString("0.##", ci);
    }

    private static string ReadText(XElement wsp)
    {
        var content = wsp.Elements(Wps + "txbx").Elements(W + "txbxContent").FirstOrDefault();
        if (content is null) return "";
        var paras = content.Elements(W + "p")
            .Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)))
            .ToList();
        return string.Join("\n", paras).Trim();
    }

    private static double Px(string? emu) => double.TryParse(emu, out var v) ? v / EmuPerPixel : 0;

    private sealed class ShapeGeom
    {
        public double X, Y, W, H;
        public string Preset = "rect";
        public bool IsConnector;
        public string Text = "";
    }
}
