using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Cad;

public abstract record CadElement;
public record CadLine(double X1, double Y1, double X2, double Y2, string? Dimension) : CadElement;
public record CadRect(double X, double Y, double Width, double Height, string? Dimension) : CadElement;
public record CadCircle(double Cx, double Cy, double Radius, string? Dimension) : CadElement;

public class BlueprintModel
{
    public string Title { get; set; } = "CAD Blueprint";
    public List<CadElement> Elements { get; } = new();
}

/// <summary>
/// Service for parsing 2D CAD engineering shapes and rendering ISO technical blueprint vector schematics in SVG.
/// </summary>
public static class GeometricBlueprintService
{
    private static readonly Regex BlueprintFenceRegex = new(
        @":::blueprint(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex LineRegex = new(
        @"line\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)\s*->\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)(?:\s*\[([^\]]+)\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RectRegex = new(
        @"rect\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)\s*(-?\d+(?:\.\d+)?)\s*x\s*(-?\d+(?:\.\d+)?)(?:\s*\[([^\]]+)\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CircleRegex = new(
        @"circle\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)\s*r\s*=\s*(-?\d+(?:\.\d+)?)(?:\s*\[([^\]]+)\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a blueprint block into a geometric CAD model.
    /// </summary>
    public static BlueprintModel ParseBlueprint(string blockText, string defaultTitle = "Blueprint")
    {
        var model = new BlueprintModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = BlueprintFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var lineMatch = LineRegex.Match(l);
            if (lineMatch.Success)
            {
                double x1 = double.TryParse(lineMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double lx1) ? lx1 : 0.0;
                double y1 = double.TryParse(lineMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ly1) ? ly1 : 0.0;
                double x2 = double.TryParse(lineMatch.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double lx2) ? lx2 : 0.0;
                double y2 = double.TryParse(lineMatch.Groups[4].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ly2) ? ly2 : 0.0;
                string? dim = lineMatch.Groups[5].Success ? lineMatch.Groups[5].Value.Trim() : null;
                model.Elements.Add(new CadLine(x1, y1, x2, y2, dim));
                continue;
            }

            var rectMatch = RectRegex.Match(l);
            if (rectMatch.Success)
            {
                double x = double.TryParse(rectMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rx) ? rx : 0.0;
                double y = double.TryParse(rectMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ry) ? ry : 0.0;
                double w = double.TryParse(rectMatch.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rw) ? rw : 0.0;
                double h = double.TryParse(rectMatch.Groups[4].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rh) ? rh : 0.0;
                string? dim = rectMatch.Groups[5].Success ? rectMatch.Groups[5].Value.Trim() : null;
                model.Elements.Add(new CadRect(x, y, w, h, dim));
                continue;
            }

            var circleMatch = CircleRegex.Match(l);
            if (circleMatch.Success)
            {
                double cx = double.TryParse(circleMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ccx) ? ccx : 0.0;
                double cy = double.TryParse(circleMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ccy) ? ccy : 0.0;
                double r = double.TryParse(circleMatch.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cr) ? cr : 0.0;
                string? dim = circleMatch.Groups[4].Success ? circleMatch.Groups[4].Value.Trim() : null;
                model.Elements.Add(new CadCircle(cx, cy, r, dim));
                continue;
            }
        }

        return model;
    }

    /// <summary>
    /// Renders an SVG technical engineering blueprint drawing with dimensions and grid.
    /// </summary>
    public static string RenderBlueprintSvg(BlueprintModel model)
    {
        double width = 450;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-cad-blueprint\">");
        sb.AppendLine("""
            <defs>
              <pattern id="cadGrid" width="20" height="20" patternUnits="userSpaceOnUse">
                <path d="M 20 0 L 0 0 0 20" fill="none" stroke="#1b3a5b" stroke-width="0.8" />
              </pattern>
              <marker id="cadArrow" markerWidth="6" markerHeight="6" refX="5" refY="3" orient="auto">
                <path d="M 0 0 L 6 3 L 0 6 z" fill="#70c0ff" />
              </marker>
            </defs>
            <style>
              .bp-bg { fill: #0b1d30; stroke: #2b5680; stroke-width: 2; }
              .bp-title { font-family: Segoe UI, monospace; font-size: 12px; font-weight: 700; fill: #70c0ff; letter-spacing: 0.05em; }
              .bp-geom { stroke: #ffffff; stroke-width: 2; fill: none; }
              .bp-dim-line { stroke: #70c0ff; stroke-width: 1; stroke-dasharray: 2 2; }
              .bp-dim-text { font-family: monospace; font-size: 10px; fill: #70c0ff; text-anchor: middle; }
            </style>
            """);

        // Blueprint Backdrop & Grid
        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"6\" class=\"bp-bg\" />");
        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"6\" fill=\"url(#cadGrid)\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"bp-title\">{System.Net.WebUtility.HtmlEncode(model.Title.ToUpperInvariant())}</text>");

        double ox = 50, oy = 50;

        foreach (var elem in model.Elements)
        {
            switch (elem)
            {
                case CadLine line:
                    sb.AppendLine($"  <line x1=\"{ox + line.X1}\" y1=\"{oy + line.Y1}\" x2=\"{ox + line.X2}\" y2=\"{oy + line.Y2}\" class=\"bp-geom\" />");
                    if (!string.IsNullOrEmpty(line.Dimension))
                    {
                        double mx = ox + (line.X1 + line.X2) / 2;
                        double my = oy + (line.Y1 + line.Y2) / 2 - 6;
                        sb.AppendLine($"  <text x=\"{mx}\" y=\"{my}\" class=\"bp-dim-text\">{System.Net.WebUtility.HtmlEncode(line.Dimension)}</text>");
                    }
                    break;

                case CadRect rect:
                    sb.AppendLine($"  <rect x=\"{ox + rect.X}\" y=\"{oy + rect.Y}\" width=\"{rect.Width}\" height=\"{rect.Height}\" class=\"bp-geom\" />");
                    if (!string.IsNullOrEmpty(rect.Dimension))
                    {
                        sb.AppendLine($"  <text x=\"{ox + rect.X + rect.Width / 2}\" y=\"{oy + rect.Y - 6}\" class=\"bp-dim-text\">{System.Net.WebUtility.HtmlEncode(rect.Dimension)}</text>");
                    }
                    break;

                case CadCircle circle:
                    sb.AppendLine($"  <circle cx=\"{ox + circle.Cx}\" cy=\"{oy + circle.Cy}\" r=\"{circle.Radius}\" class=\"bp-geom\" />");
                    if (!string.IsNullOrEmpty(circle.Dimension))
                    {
                        sb.AppendLine($"  <text x=\"{ox + circle.Cx}\" y=\"{oy + circle.Cy + circle.Radius + 14}\" class=\"bp-dim-text\">{System.Net.WebUtility.HtmlEncode(circle.Dimension)}</text>");
                    }
                    break;
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
