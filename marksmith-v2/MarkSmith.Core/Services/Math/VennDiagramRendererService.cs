using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.MathDiagrams;

public class Venn2SetModel
{
    public string Title { get; set; } = "Venn Diagram";
    public string LabelA { get; set; } = "Set A";
    public int CountA { get; set; } = 30;
    public string LabelB { get; set; } = "Set B";
    public int CountB { get; set; } = 30;
    public string LabelIntersection { get; set; } = "Intersection";
    public int CountIntersection { get; set; } = 10;
}

/// <summary>
/// Service for parsing set theory Venn diagram specifications and rendering multi-set SVG diagrams.
/// </summary>
public static class VennDiagramRendererService
{
    private static readonly Regex VennFenceRegex = new(
        @":::venn(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex SetARegex = new(
        @"set\s+A:\s*""([^""]+)""(?:\s*\((\d+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SetBRegex = new(
        @"set\s+B:\s*""([^""]+)""(?:\s*\((\d+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IntersectRegex = new(
        @"intersection(?:\s+A&B)?:\s*""([^""]+)""(?:\s*\((\d+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static Venn2SetModel ParseVenn(string blockText, string defaultTitle = "Venn Diagram")
    {
        var model = new Venn2SetModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = VennFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var am = SetARegex.Match(l);
            if (am.Success)
            {
                model.LabelA = am.Groups[1].Value.Trim();
                if (am.Groups[2].Success && int.TryParse(am.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ca)) model.CountA = ca;
                continue;
            }

            var bm = SetBRegex.Match(l);
            if (bm.Success)
            {
                model.LabelB = bm.Groups[1].Value.Trim();
                if (bm.Groups[2].Success && int.TryParse(bm.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cb)) model.CountB = cb;
                continue;
            }

            var im = IntersectRegex.Match(l);
            if (im.Success)
            {
                model.LabelIntersection = im.Groups[1].Value.Trim();
                if (im.Groups[2].Success && int.TryParse(im.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ci)) model.CountIntersection = ci;
                continue;
            }
        }

        return model;
    }

    public static string RenderVennSvg(Venn2SetModel model)
    {
        double width = 450;
        double height = 260;
        double cy = 145;
        double r = 75;
        double cxA = 180;
        double cxB = 270;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-venn-svg\">");
        sb.AppendLine("""
            <style>
              .vn-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .vn-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .circle-a { fill: #38bdf8; fill-opacity: 0.35; stroke: #38bdf8; stroke-width: 2; }
              .circle-b { fill: #f43f5e; fill-opacity: 0.35; stroke: #f43f5e; stroke-width: 2; }
              .vn-label { font-family: Segoe UI, sans-serif; font-size: 11px; font-weight: 700; fill: #ffffff; text-anchor: middle; }
              .vn-count { font-family: monospace; font-size: 10px; fill: #cbd5e1; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"vn-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"vn-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Circle A & B
        sb.AppendLine($"  <circle cx=\"{cxA}\" cy=\"{cy}\" r=\"{r}\" class=\"circle-a\" />");
        sb.AppendLine($"  <circle cx=\"{cxB}\" cy=\"{cy}\" r=\"{r}\" class=\"circle-b\" />");

        // Labels
        sb.AppendLine($"  <text x=\"{cxA - 35}\" y=\"{cy - 10}\" class=\"vn-label\">{System.Net.WebUtility.HtmlEncode(model.LabelA)}</text>");
        sb.AppendLine($"  <text x=\"{cxA - 35}\" y=\"{cy + 10}\" class=\"vn-count\">({model.CountA})</text>");

        sb.AppendLine($"  <text x=\"{cxB + 35}\" y=\"{cy - 10}\" class=\"vn-label\">{System.Net.WebUtility.HtmlEncode(model.LabelB)}</text>");
        sb.AppendLine($"  <text x=\"{cxB + 35}\" y=\"{cy + 10}\" class=\"vn-count\">({model.CountB})</text>");

        // Intersection
        double cxInter = (cxA + cxB) / 2;
        sb.AppendLine($"  <text x=\"{cxInter}\" y=\"{cy - 5}\" class=\"vn-label\">{System.Net.WebUtility.HtmlEncode(model.LabelIntersection)}</text>");
        sb.AppendLine($"  <text x=\"{cxInter}\" y=\"{cy + 12}\" class=\"vn-count\">({model.CountIntersection})</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
