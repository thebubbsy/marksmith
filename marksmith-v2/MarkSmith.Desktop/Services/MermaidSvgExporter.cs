using System.Globalization;
using System.Text;
using MarkSmith.ViewModels.Mermaid;

namespace MarkSmith.Services;

/// <summary>
/// Renders the Studio canvas (nodes + connectors) to a standalone SVG document.
/// The connector <c>PathData</c> produced by the ViewModels is already valid SVG path syntax
/// (M / L / C / Q commands), so lines are emitted verbatim. Node shapes are re-drawn as native
/// SVG primitives that mirror the on-canvas XAML templates (rounded rect, ellipse, diamond,
/// hexagon, actor, database cylinder). Arrowheads are emitted as reusable <c>&lt;marker&gt;</c>
/// definitions so the export matches the on-screen connectors exactly.
/// </summary>
public static class MermaidSvgExporter
{
    private const string FontFamily = "Segoe UI, Helvetica, Arial, sans-serif";
    private const double NodeFontSize = 13;
    private const double LabelFontSize = 11;
    private const double LineHeight = 16;
    private const double Padding = 48;
    private const string NodeTextFill = "#EDF2F4";

    public static string GenerateSvg(
        IReadOnlyList<DiagramNodeViewModel> nodes,
        IReadOnlyList<DiagramConnectorViewModel> connectors,
        string background = "#1E1E2E")
    {
        if (nodes.Count == 0 && connectors.Count == 0)
        {
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
                   $"<rect width=\"200\" height=\"200\" fill=\"{background}\"/></svg>";
        }

        // ---- Bounding box (nodes + connector endpoints) with breathing room --------------
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var n in nodes)
        {
            minX = Math.Min(minX, n.X);
            minY = Math.Min(minY, n.Y);
            maxX = Math.Max(maxX, n.X + n.Width);
            maxY = Math.Max(maxY, n.Y + n.Height);
        }
        foreach (var c in connectors)
        {
            minX = Math.Min(minX, Math.Min(c.SourceX, c.TargetX));
            minY = Math.Min(minY, Math.Min(c.SourceY, c.TargetY));
            maxX = Math.Max(maxX, Math.Max(c.SourceX, c.TargetX));
            maxY = Math.Max(maxY, Math.Max(c.SourceY, c.TargetY));
        }
        if (minX > maxX) { minX = 0; minY = 0; maxX = 400; maxY = 300; }
        minX -= Padding; minY -= Padding; maxX += Padding; maxY += Padding;
        double w = maxX - minX, h = maxY - minY;

        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(minX)} {F(minY)} {F(w)} {F(h)}\" width=\"{F(w)}\" height=\"{F(h)}\" font-family=\"{FontFamily}\">"));

        // Background fill so the export isn't transparent (matches the dark canvas).
        sb.AppendLine(FormattableString.Invariant(
            $"  <rect x=\"{F(minX)}\" y=\"{F(minY)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"{background}\"/>"));

        // Arrowhead marker definitions.
        sb.Append(BuildMarkerDefs(connectors, background));

        // Connectors first (below nodes), then nodes, then connector labels on top for legibility.
        foreach (var c in connectors) sb.Append(BuildConnectorPath(c));
        foreach (var n in nodes.OrderBy(n => n.ZIndex)) sb.Append(BuildNode(n));
        foreach (var c in connectors) sb.Append(BuildConnectorLabel(c));

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    // ---- Arrowhead markers ---------------------------------------------------------------

    private static string BuildMarkerDefs(IReadOnlyList<DiagramConnectorViewModel> connectors, string background)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defs = new StringBuilder();
        foreach (var c in connectors)
        {
            AppendMarker(defs, seen, c.EndHead, c.StrokeColor, background);
            AppendMarker(defs, seen, c.StartHead, c.StrokeColor, background);
        }
        if (defs.Length == 0) return string.Empty;
        return "  <defs>\n" + defs.ToString() + "  </defs>\n";
    }

    private static void AppendMarker(StringBuilder defs, HashSet<string> seen, string head, string color, string background)
    {
        if (string.IsNullOrEmpty(head) || head.Equals("None", StringComparison.OrdinalIgnoreCase)) return;
        string id = MarkerId(head, color);
        if (!seen.Add(id)) return;

        string inner;
        double refX = 10, refY = 5;
        switch (head)
        {
            case "Cross":
                inner = $"<path d=\"M 1,1 L 9,9 M 9,1 L 1,9\" stroke=\"{color}\" stroke-width=\"1.6\" fill=\"none\"/>";
                refX = 5;
                break;
            case "Circle":
                inner = $"<circle cx=\"5\" cy=\"5\" r=\"4\" fill=\"{color}\"/>";
                refX = 5;
                break;
            case "Diamond":
            case "Composition":
                inner = $"<path d=\"M 5,0 L 10,5 L 5,10 L 0,5 Z\" fill=\"{color}\"/>";
                refX = 5;
                break;
            case "Aggregation":
                inner = $"<path d=\"M 5,0 L 10,5 L 5,10 L 0,5 Z\" fill=\"{background}\" stroke=\"{color}\" stroke-width=\"1.4\"/>";
                refX = 5;
                break;
            case "Inheritance":
                inner = $"<path d=\"M 0,0 L 10,5 L 0,10 Z\" fill=\"{background}\" stroke=\"{color}\" stroke-width=\"1.4\"/>";
                break;
            case "CrowsFoot":
                inner = $"<path d=\"M 10,5 L 0,0 M 10,5 L 0,5 M 10,5 L 0,10\" stroke=\"{color}\" stroke-width=\"1.4\" fill=\"none\"/>";
                break;
            case "Normal":
            default:
                inner = $"<path d=\"M 0,0 L 10,5 L 0,10 Z\" fill=\"{color}\"/>";
                break;
        }

        defs.AppendLine(FormattableString.Invariant(
            $"    <marker id=\"{id}\" viewBox=\"0 0 10 10\" refX=\"{F(refX)}\" refY=\"{F(refY)}\" markerWidth=\"10\" markerHeight=\"10\" orient=\"auto-start-reverse\" markerUnits=\"userSpaceOnUse\">{inner}</marker>"));
    }

    private static string MarkerId(string head, string color) =>
        "ah-" + SanitizeId(head + "-" + color);

    private static string MarkerRef(string head, string color, string attribute)
    {
        if (string.IsNullOrEmpty(head) || head.Equals("None", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return $" {attribute}=\"url(#{MarkerId(head, color)})\"";
    }

    // ---- Connectors ----------------------------------------------------------------------

    private static string BuildConnectorPath(DiagramConnectorViewModel c)
    {
        if (string.IsNullOrEmpty(c.PathData)) return string.Empty;
        string dash = c.LineStyle switch
        {
            "Dashed" => " stroke-dasharray=\"6,4\"",
            _ => string.Empty
        };
        double width = c.LineStyle == "Thick" ? c.StrokeWidth + 1 : c.StrokeWidth;
        string markerStart = MarkerRef(c.StartHead, c.StrokeColor, "marker-start");
        string markerEnd = MarkerRef(c.EndHead, c.StrokeColor, "marker-end");
        return FormattableString.Invariant(
            $"  <path d=\"{c.PathData}\" fill=\"none\" stroke=\"{c.StrokeColor}\" stroke-width=\"{F(width)}\"{dash}{markerStart}{markerEnd} stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n");
    }

    private static string BuildConnectorLabel(DiagramConnectorViewModel c)
    {
        if (string.IsNullOrWhiteSpace(c.Label)) return string.Empty;
        double estW = c.Label.Length * 6.5 + 12;
        double estH = 18;
        return FormattableString.Invariant(
            $"  <rect x=\"{F(c.MidpointX - estW / 2)}\" y=\"{F(c.MidpointY - estH / 2)}\" width=\"{F(estW)}\" height=\"{F(estH)}\" rx=\"4\" fill=\"#2B2D42\" stroke=\"#8D99AE\" stroke-width=\"1\"/>\n  <text x=\"{F(c.MidpointX)}\" y=\"{F(c.MidpointY)}\" text-anchor=\"middle\" dominant-baseline=\"central\" font-size=\"{F(LabelFontSize)}\" fill=\"{NodeTextFill}\">{Escape(c.Label)}</text>\n");
    }

    // ---- Nodes ---------------------------------------------------------------------------

    private static string BuildNode(DiagramNodeViewModel n)
    {
        var sb = new StringBuilder();
        double x = n.X, y = n.Y, w = n.Width, h = n.Height;
        double cx = x + w / 2, cy = y + h / 2;
        string fill = n.FillColor, stroke = n.StrokeColor;
        double sw = n.StrokeWidth;

        switch (n.Shape)
        {
            case "Circle":
                sb.AppendLine(FormattableString.Invariant(
                    $"  <ellipse cx=\"{F(cx)}\" cy=\"{F(cy)}\" rx=\"{F(w / 2)}\" ry=\"{F(h / 2)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{F(sw)}\"/>"));
                break;

            case "Rhombus":
            case "RhombusDiamond":
            case "Choice":
                sb.AppendLine(FormattableString.Invariant(
                    $"  <polygon points=\"{F(cx)},{F(y)} {F(x + w)},{F(cy)} {F(cx)},{F(y + h)} {F(x)},{F(cy)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{F(sw)}\"/>"));
                break;

            case "Hexagon":
                sb.AppendLine(FormattableString.Invariant(
                    $"  <polygon points=\"{F(x + 0.25 * w)},{F(y)} {F(x + 0.75 * w)},{F(y)} {F(x + w)},{F(cy)} {F(x + 0.75 * w)},{F(y + h)} {F(x + 0.25 * w)},{F(y + h)} {F(x)},{F(cy)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{F(sw)}\"/>"));
                break;

            case "Actor":
                sb.Append(BuildActor(n));
                return sb.ToString(); // actor places its own label

            case "CylindricalDatabase":
                sb.Append(BuildCylinder(n));
                return sb.ToString(); // cylinder places its own label

            default: // Rectangle / RoundedRectangle / any fallback shape
                sb.AppendLine(FormattableString.Invariant(
                    $"  <rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"10\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{F(sw)}\"/>"));
                break;
        }

        sb.Append(BuildNodeLabel(n, cx, cy));
        return sb.ToString();
    }

    private static string BuildActor(DiagramNodeViewModel n)
    {
        double cx = n.X + n.Width / 2;
        double headR = 9;
        double headCy = n.Y + 14;
        double shoulderY = headCy + headR;
        double hipY = shoulderY + 16;
        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant(
            $"  <circle cx=\"{F(cx)}\" cy=\"{F(headCy)}\" r=\"{F(headR)}\" fill=\"none\" stroke=\"{n.StrokeColor}\" stroke-width=\"{F(n.StrokeWidth)}\"/>"));
        sb.AppendLine(FormattableString.Invariant(
            $"  <path d=\"M {F(cx)},{F(shoulderY)} L {F(cx)},{F(hipY)} M {F(cx - 12)},{F(shoulderY + 5)} L {F(cx + 12)},{F(shoulderY + 5)} M {F(cx)},{F(hipY)} L {F(cx - 10)},{F(hipY + 13)} M {F(cx)},{F(hipY)} L {F(cx + 10)},{F(hipY + 13)}\" stroke=\"{n.StrokeColor}\" stroke-width=\"{F(n.StrokeWidth)}\" fill=\"none\" stroke-linecap=\"round\"/>"));
        sb.Append(BuildNodeLabel(n, cx, n.Y + n.Height - 8));
        return sb.ToString();
    }

    private static string BuildCylinder(DiagramNodeViewModel n)
    {
        double x = n.X, y = n.Y, w = n.Width, h = n.Height;
        double rx = w / 2, ry = Math.Min(12, h / 4);
        double cx = x + w / 2;
        var sb = new StringBuilder();
        // Side walls + bottom bulge, then the top lid ellipse drawn over it.
        sb.AppendLine(FormattableString.Invariant(
            $"  <path d=\"M {F(x)},{F(y + ry)} L {F(x)},{F(y + h - ry)} A {F(rx)},{F(ry)} 0 0 0 {F(x + w)},{F(y + h - ry)} L {F(x + w)},{F(y + ry)}\" fill=\"{n.FillColor}\" stroke=\"{n.StrokeColor}\" stroke-width=\"{F(n.StrokeWidth)}\"/>"));
        sb.AppendLine(FormattableString.Invariant(
            $"  <ellipse cx=\"{F(cx)}\" cy=\"{F(y + ry)}\" rx=\"{F(rx)}\" ry=\"{F(ry)}\" fill=\"{n.FillColor}\" stroke=\"{n.StrokeColor}\" stroke-width=\"{F(n.StrokeWidth)}\"/>"));
        sb.Append(BuildNodeLabel(n, cx, y + h / 2 + ry / 2));
        return sb.ToString();
    }

    private static string BuildNodeLabel(DiagramNodeViewModel n, double cx, double cy)
    {
        if (string.IsNullOrWhiteSpace(n.LabelText)) return string.Empty;
        var lines = n.LabelText.Split('\n');
        double totalH = lines.Length * LineHeight;
        double firstY = cy - totalH / 2 + LineHeight / 2;

        var sb = new StringBuilder();
        sb.Append(FormattableString.Invariant(
            $"  <text text-anchor=\"middle\" font-size=\"{F(NodeFontSize)}\" font-weight=\"600\" fill=\"{NodeTextFill}\">"));
        for (int i = 0; i < lines.Length; i++)
        {
            double ty = firstY + i * LineHeight;
            sb.Append(FormattableString.Invariant(
                $"<tspan x=\"{F(cx)}\" y=\"{F(ty)}\" dominant-baseline=\"central\">{Escape(lines[i])}</tspan>"));
        }
        sb.AppendLine("</text>");
        return sb.ToString();
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string SanitizeId(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
