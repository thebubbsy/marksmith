using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Physics;

public record OpticalLens(string Type, double PositionX, double FocalLength);
public record LightRay(double StartX, double StartY, double EndX, double EndY);

public class OpticalSystemModel
{
    public string Title { get; set; } = "Optical System";
    public List<OpticalLens> Lenses { get; } = new();
    public List<LightRay> Rays { get; } = new();
}

/// <summary>
/// Service for parsing optical lens setups and rendering Snell refraction SVG ray tracing diagrams.
/// </summary>
public static class OpticalLensRayTracingService
{
    private static readonly Regex OpticsFenceRegex = new(
        @":::optics([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex LensRegex = new(
        @"lens\s+(convex|concave|biconvex|plano-convex)\s+pos=(-?\d+(?:\.\d+)?)\s+f=(-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RayRegex = new(
        @"ray\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)\s*->\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static OpticalSystemModel ParseOptics(string blockText, string defaultTitle = "Lens Ray Tracing")
    {
        var model = new OpticalSystemModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = OpticsFenceRegex.Match(blockText);
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
            var lm = LensRegex.Match(l);
            if (lm.Success)
            {
                string type = lm.Groups[1].Value.ToLowerInvariant();
                double pos = double.TryParse(lm.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pp) ? pp : 0.0;
                double f = double.TryParse(lm.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pf) ? pf : 0.0;
                model.Lenses.Add(new OpticalLens(type, pos, f));
                continue;
            }

            var rm = RayRegex.Match(l);
            if (rm.Success)
            {
                double x1 = double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double px1) ? px1 : 0.0;
                double y1 = double.TryParse(rm.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double py1) ? py1 : 0.0;
                double x2 = double.TryParse(rm.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double px2) ? px2 : 0.0;
                double y2 = double.TryParse(rm.Groups[4].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double py2) ? py2 : 0.0;
                model.Rays.Add(new LightRay(x1, y1, x2, y2));
            }
        }

        return model;
    }

    public static string RenderOpticsSvg(OpticalSystemModel model)
    {
        double width = 450;
        double height = 220;
        double cy = height / 2 + 10;
        double ox = 30;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-optics-svg\">");
        sb.AppendLine("""
            <style>
              .op-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .op-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .op-axis { stroke: #475569; stroke-width: 1; stroke-dasharray: 4 4; }
              .op-lens { fill: #38bdf8; fill-opacity: 0.25; stroke: #38bdf8; stroke-width: 2; }
              .op-ray { stroke: #fbbf24; stroke-width: 1.5; }
              .op-label { font-family: monospace; font-size: 9px; fill: #94a3b8; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"op-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"op-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Optical Axis
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{cy}\" x2=\"{width - ox}\" y2=\"{cy}\" class=\"op-axis\" />");

        // Lenses
        foreach (var lens in model.Lenses)
        {
            double lx = ox + lens.PositionX * 1.5;
            double lh = 120;
            sb.AppendLine($"  <ellipse cx=\"{lx}\" cy=\"{cy}\" rx=\"12\" ry=\"{lh / 2}\" class=\"op-lens\" />");
            sb.AppendLine($"  <text x=\"{lx}\" y=\"{cy + lh / 2 + 15}\" class=\"op-label\">f = {lens.FocalLength}mm</text>");
        }

        // Rays
        foreach (var ray in model.Rays)
        {
            double x1 = ox + ray.StartX * 1.5;
            double y1 = cy - ray.StartY;
            double x2 = ox + ray.EndX * 1.5;
            double y2 = cy - ray.EndY;
            sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" class=\"op-ray\" />");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
