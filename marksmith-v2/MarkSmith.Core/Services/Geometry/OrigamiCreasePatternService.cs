using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Geometry;

public enum CreaseKind { Valley, Mountain, Border, FoldArrow }
public record OrigamiFoldElement(CreaseKind Kind, double X1, double Y1, double X2, double Y2, string? Label);

public class OrigamiModel
{
    public string Title { get; set; } = "Origami Fold";
    public List<OrigamiFoldElement> Elements { get; } = new();
}

/// <summary>
/// Service for parsing origami crease patterns and rendering 2D/2.5D paper fold diagrams in SVG.
/// </summary>
public static class OrigamiCreasePatternService
{
    private static readonly Regex OrigamiFenceRegex = new(
        @":::origami([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex LineRegex = new(
        @"(valley|mountain|border|fold-arrow)\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)\s*->\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)(?:\s*\[([^\]]+)\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static OrigamiModel ParseOrigami(string blockText, string defaultTitle = "Origami Step")
    {
        var model = new OrigamiModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = OrigamiFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var m = LineRegex.Match(l);
            if (m.Success)
            {
                string kindStr = m.Groups[1].Value.ToLowerInvariant();
                var kind = kindStr switch
                {
                    "mountain" => CreaseKind.Mountain,
                    "border" => CreaseKind.Border,
                    "fold-arrow" => CreaseKind.FoldArrow,
                    _ => CreaseKind.Valley
                };

                double x1 = double.TryParse(m.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double px1) ? px1 : 0.0;
                double y1 = double.TryParse(m.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double py1) ? py1 : 0.0;
                double x2 = double.TryParse(m.Groups[4].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double px2) ? px2 : 0.0;
                double y2 = double.TryParse(m.Groups[5].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double py2) ? py2 : 0.0;
                string? label = m.Groups[6].Success ? m.Groups[6].Value.Trim() : null;

                model.Elements.Add(new OrigamiFoldElement(kind, x1, y1, x2, y2, label));
            }
        }

        return model;
    }

    public static string RenderOrigamiSvg(OrigamiModel model)
    {
        double width = 380;
        double height = 280;
        double ox = 90;
        double oy = 55;
        double paperSize = 200;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-origami-svg\">");
        sb.AppendLine("""
            <defs>
              <marker id="foldArrow" markerWidth="8" markerHeight="8" refX="6" refY="4" orient="auto">
                <path d="M 1 1 L 7 4 L 1 7 z" fill="#38bdf8" />
              </marker>
            </defs>
            <style>
              .og-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .og-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .og-paper { fill: #f8fafc; stroke: #334155; stroke-width: 2; }
              .og-valley { stroke: #3b82f6; stroke-width: 2; stroke-dasharray: 6 3; fill: none; }
              .og-mountain { stroke: #ef4444; stroke-width: 2; stroke-dasharray: 6 2 2 2; fill: none; }
              .og-border { stroke: #1e293b; stroke-width: 2; fill: none; }
              .og-arrow { stroke: #38bdf8; stroke-width: 2.5; marker-end: url(#foldArrow); fill: none; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"og-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"og-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Base square paper
        sb.AppendLine($"  <rect x=\"{ox}\" y=\"{oy}\" width=\"{paperSize}\" height=\"{paperSize}\" class=\"og-paper\" />");

        foreach (var elem in model.Elements)
        {
            double x1 = ox + (elem.X1 / 100.0) * paperSize;
            double y1 = oy + (elem.Y1 / 100.0) * paperSize;
            double x2 = ox + (elem.X2 / 100.0) * paperSize;
            double y2 = oy + (elem.Y2 / 100.0) * paperSize;

            string cls = elem.Kind switch
            {
                CreaseKind.Mountain => "og-mountain",
                CreaseKind.Border => "og-border",
                CreaseKind.FoldArrow => "og-arrow",
                _ => "og-valley"
            };

            sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" class=\"{cls}\" />");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
