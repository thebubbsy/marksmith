using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Physics;

public class PrismModel
{
    public string Title { get; set; } = "Prism Dispersion";
    public double ApexAngleDeg { get; set; } = 60.0;
    public string Material { get; set; } = "Flint Glass";
}

/// <summary>
/// Service for calculating chromatic optical dispersion across triangular prisms and rendering SVG spectrum schematics.
/// </summary>
public static class PrismSpectrogramService
{
    private static readonly Regex PrismFenceRegex = new(
        @":::prism([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex AngleRegex = new(
        @"angle\s*=\s*(-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MatRegex = new(
        @"material\s*=\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static PrismModel ParsePrism(string blockText, string defaultTitle = "Prism Dispersion")
    {
        var model = new PrismModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = PrismFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var am = AngleRegex.Match(text);
        if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pa)) model.ApexAngleDeg = pa;

        var mm = MatRegex.Match(text);
        if (mm.Success) model.Material = mm.Groups[1].Value.Trim();

        return model;
    }

    public static string RenderPrismSvg(PrismModel model)
    {
        double width = 420;
        double height = 260;
        double cx = 180;
        double cy = 140;
        double size = 80;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-prism-svg\">");
        sb.AppendLine("""
            <style>
              .ps-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .ps-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .ps-meta { font-family: monospace; font-size: 10px; fill: #94a3b8; }
              .ps-prism { fill: #38bdf8; fill-opacity: 0.15; stroke: #38bdf8; stroke-width: 2; }
              .ps-white-ray { stroke: #ffffff; stroke-width: 2.5; }
              .ps-red { stroke: #ef4444; stroke-width: 1.8; }
              .ps-green { stroke: #22c55e; stroke-width: 1.8; }
              .ps-blue { stroke: #3b82f6; stroke-width: 1.8; }
              .ps-violet { stroke: #a855f7; stroke-width: 1.8; }
              .ps-label { font-family: monospace; font-size: 9px; font-weight: 700; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"ps-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"ps-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"40\" class=\"ps-meta\">Apex: {model.ApexAngleDeg}° • {System.Net.WebUtility.HtmlEncode(model.Material)}</text>");

        // Triangular Prism: Apex (cx, cy - size), Left (cx - size, cy + size), Right (cx + size, cy + size)
        double pApexX = cx; double pApexY = cy - size;
        double pLeftX = cx - size * 0.866; double pLeftY = cy + size * 0.5;
        double pRightX = cx + size * 0.866; double pRightY = cy + size * 0.5;

        sb.AppendLine($"  <polygon points=\"{pApexX},{pApexY} {pLeftX},{pLeftY} {pRightX},{pRightY}\" class=\"ps-prism\" />");

        // Incident White Ray from Left
        double inX1 = 30; double inY1 = cy + 20;
        double inX2 = cx - size * 0.433; double inY2 = cy;
        sb.AppendLine($"  <line x1=\"{inX1}\" y1=\"{inY1}\" x2=\"{inX2}\" y2=\"{inY2}\" class=\"ps-white-ray\" />");

        // Dispersed Rays inside & outside
        double exitX = cx + size * 0.433;
        (string colClass, string hex, double exitDy, double screenDy, string label)[] colors =
        {
            ("ps-red", "#ef4444", -12, 10, "700nm (Red)"),
            ("ps-green", "#22c55e", -4, 30, "530nm (Green)"),
            ("ps-blue", "#3b82f6", 4, 50, "470nm (Blue)"),
            ("ps-violet", "#a855f7", 12, 70, "400nm (Violet)")
        };

        foreach (var c in colors)
        {
            double internalEndY = cy + c.exitDy;
            double screenX = width - 40;
            double screenY = cy + c.screenDy;

            // Inside prism ray
            sb.AppendLine($"  <line x1=\"{inX2}\" y1=\"{inY2}\" x2=\"{exitX}\" y2=\"{internalEndY}\" class=\"{c.colClass}\" />");
            // Dispersed ray outside
            sb.AppendLine($"  <line x1=\"{exitX}\" y1=\"{internalEndY}\" x2=\"{screenX}\" y2=\"{screenY}\" class=\"{c.colClass}\" />");
            sb.AppendLine($"  <text x=\"{screenX + 5}\" y=\"{screenY + 3}\" fill=\"{c.hex}\" class=\"ps-label\">{c.label}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
