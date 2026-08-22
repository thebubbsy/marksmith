using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public record AqueductSegment(double FromM, double ToM, double ElevationM, double Slope, string Type, int Arches);

public class AqueductModel
{
    public string Title { get; set; } = "Aqueduct Gradient";
    public List<AqueductSegment> Segments { get; } = new();
}

/// <summary>
/// Service for parsing civil hydraulic channel segments and rendering longitudinal SVG aqueduct arcade profiles.
/// </summary>
public static class HydraulicGradientService
{
    private static readonly Regex AqueductFenceRegex = new(
        @":::aqueduct([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex SegmentRegex = new(
        @"segment\s+(-?\d+(?:\.\d+)?)\s*-\s*(-?\d+(?:\.\d+)?)\s*m?\s+elev=(-?\d+(?:\.\d+)?)\s+slope=(-?\d+(?:\.\d+)?)(?:\s*\[type:\s*""([^""]+)""(?:,\s*arches:\s*(\d+))?\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static AqueductModel ParseAqueduct(string blockText, string defaultTitle = "Aqueduct Channel")
    {
        var model = new AqueductModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = AqueductFenceRegex.Match(blockText);
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
            var sm = SegmentRegex.Match(l);
            if (sm.Success)
            {
                double from = double.TryParse(sm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fv) ? fv : 0.0;
                double to = double.TryParse(sm.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double tv) ? tv : 100.0;
                double elev = double.TryParse(sm.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ev) ? ev : 0.0;
                double slope = double.TryParse(sm.Groups[4].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sv) ? sv : 0.001;
                string type = sm.Groups[5].Success ? sm.Groups[5].Value.ToLowerInvariant() : "channel";
                int arches = sm.Groups[6].Success && int.TryParse(sm.Groups[6].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int av) ? av : 3;

                model.Segments.Add(new AqueductSegment(from, to, elev, slope, type, arches));
            }
        }

        return model;
    }

    public static string RenderAqueductSvg(AqueductModel model)
    {
        double width = 450;
        double height = 220;
        double groundY = 175;
        double ox = 50;
        double scaleX = 2.8;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-aqueduct-svg\">");
        sb.AppendLine("""
            <style>
              .aq-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .aq-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .aq-ground { stroke: #475569; stroke-width: 2; fill: none; }
              .aq-stone { fill: #78716c; stroke: #44403c; stroke-width: 1; }
              .aq-water { stroke: #38bdf8; stroke-width: 3; fill: none; }
              .aq-label { font-family: monospace; font-size: 9px; fill: #94a3b8; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"aq-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"aq-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Ground Line
        sb.AppendLine($"  <line x1=\"{ox - 20}\" y1=\"{groundY}\" x2=\"{width - 20}\" y2=\"{groundY}\" class=\"aq-ground\" />");

        foreach (var seg in model.Segments)
        {
            double x1 = ox + seg.FromM * scaleX;
            double x2 = ox + seg.ToM * scaleX;
            double segW = x2 - x1;
            double channelY = groundY - (seg.ElevationM * 0.7);

            if (seg.Type == "arcade")
            {
                // Masonry Arcade with Arches
                sb.AppendLine($"  <rect x=\"{x1}\" y=\"{channelY}\" width=\"{segW}\" height=\"{groundY - channelY}\" class=\"aq-stone\" />");

                int arches = Math.Max(1, seg.Arches);
                double archW = segW / arches;
                for (int a = 0; a < arches; a++)
                {
                    double ax = x1 + a * archW;
                    double archR = archW * 0.38;
                    double archH = (groundY - channelY) * 0.65;
                    // Cutout arch opening (fill background color)
                    sb.AppendLine($"  <rect x=\"{ax + archW / 2 - archR}\" y=\"{groundY - archH}\" width=\"{archR * 2}\" height=\"{archH}\" rx=\"{archR}\" fill=\"#0f172a\" />");
                }
            }

            // Water surface flow line (HGL)
            sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{channelY}\" x2=\"{x2}\" y2=\"{channelY + segW * seg.Slope * 5}\" class=\"aq-water\" />");
            sb.AppendLine($"  <text x=\"{(x1 + x2) / 2}\" y=\"{channelY - 8}\" class=\"aq-label\">{seg.FromM}-{seg.ToM}m ({seg.Type})</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
