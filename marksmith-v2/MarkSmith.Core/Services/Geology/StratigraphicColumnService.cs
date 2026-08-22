using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Geology;

public record StratigraphicLayer(double FromDepth, double ToDepth, string Name, string Lithology, string ColorHex);

public class StratigraphicModel
{
    public string Title { get; set; } = "Stratigraphic Profile";
    public List<StratigraphicLayer> Layers { get; } = new();
}

/// <summary>
/// Service for parsing geological borehole stratigraphic data and rendering SVG core sample columns.
/// </summary>
public static class StratigraphicColumnService
{
    private static readonly Regex StratFenceRegex = new(
        @":::stratigraphy(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex LayerRegex = new(
        @"layer\s+(-?\d+(?:\.\d+)?)\s*-\s*(-?\d+(?:\.\d+)?)\s*m?\s*""([^""]+)""(?:\s*\[(?:lithology:\s*([A-Za-z0-9_-]+))?(?:,\s*color:\s*(#[0-9A-Fa-f]{6}))?\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static StratigraphicModel ParseStratigraphy(string blockText, string defaultTitle = "Borehole Log")
    {
        var model = new StratigraphicModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = StratFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var lm = LayerRegex.Match(l);
            if (lm.Success)
            {
                double from = double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pf) ? pf : 0.0;
                double to = double.TryParse(lm.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pt) ? pt : 0.0;
                string name = lm.Groups[3].Value.Trim();
                string lith = lm.Groups[4].Success ? lm.Groups[4].Value.ToLowerInvariant() : "sedimentary";
                string color = lm.Groups[5].Success ? lm.Groups[5].Value : "#e2e8f0";
                model.Layers.Add(new StratigraphicLayer(from, to, name, lith, color));
                continue;
            }
        }

        return model;
    }

    public static string RenderStratigraphySvg(StratigraphicModel model)
    {
        if (model.Layers.Count == 0)
            return "<svg width=\"350\" height=\"100\"></svg>";

        double maxDepth = 0;
        foreach (var ly in model.Layers)
        {
            if (ly.ToDepth > maxDepth) maxDepth = ly.ToDepth;
        }
        maxDepth = Math.Max(10, maxDepth);

        double width = 420;
        double colTop = 55;
        double colHeight = 220;
        double height = colTop + colHeight + 35;
        double colX = 70;
        double colW = 120;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-strat-svg\">");
        sb.AppendLine("""
            <defs>
              <pattern id="pat-sandstone" width="8" height="8" patternUnits="userSpaceOnUse">
                <circle cx="2" cy="2" r="1" fill="#78350f" />
                <circle cx="6" cy="6" r="1" fill="#78350f" />
              </pattern>
              <pattern id="pat-shale" width="10" height="6" patternUnits="userSpaceOnUse">
                <line x1="0" y1="3" x2="10" y2="3" stroke="#334155" stroke-width="1" />
              </pattern>
              <pattern id="pat-limestone" width="12" height="8" patternUnits="userSpaceOnUse">
                <rect x="0" y="0" width="12" height="8" fill="none" stroke="#475569" stroke-width="0.8" />
                <line x1="6" y1="0" x2="6" y2="8" stroke="#475569" stroke-width="0.8" />
              </pattern>
            </defs>
            <style>
              .st-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .st-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .st-tick { font-family: monospace; font-size: 9px; fill: #94a3b8; text-anchor: end; }
              .st-axis { stroke: #475569; stroke-width: 1; }
              .st-label { font-family: Segoe UI, sans-serif; font-size: 11px; font-weight: 600; fill: #f8fafc; }
              .st-depth { font-family: monospace; font-size: 9px; fill: #38bdf8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"st-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"st-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Depth Axis
        sb.AppendLine($"  <line x1=\"{colX - 10}\" y1=\"{colTop}\" x2=\"{colX - 10}\" y2=\"{colTop + colHeight}\" class=\"st-axis\" />");

        foreach (var ly in model.Layers)
        {
            double y1 = colTop + (ly.FromDepth / maxDepth) * colHeight;
            double y2 = colTop + (ly.ToDepth / maxDepth) * colHeight;
            double h = y2 - y1;

            // Tick
            sb.AppendLine($"  <text x=\"{colX - 15}\" y=\"{y1 + 4}\" class=\"st-tick\">{ly.FromDepth}m</text>");

            // Base Fill
            sb.AppendLine($"  <rect x=\"{colX}\" y=\"{y1}\" width=\"{colW}\" height=\"{h}\" fill=\"{ly.ColorHex}\" stroke=\"#1e293b\" stroke-width=\"1\" />");

            // Lithologic Pattern
            string pat = ly.Lithology switch
            {
                "sandstone" => "url(#pat-sandstone)",
                "shale" => "url(#pat-shale)",
                "limestone" => "url(#pat-limestone)",
                _ => "none"
            };
            if (pat != "none")
            {
                sb.AppendLine($"  <rect x=\"{colX}\" y=\"{y1}\" width=\"{colW}\" height=\"{h}\" fill=\"{pat}\" />");
            }

            // Label & Depths
            sb.AppendLine($"  <text x=\"{colX + colW + 15}\" y=\"{y1 + h / 2 + 3}\" class=\"st-label\">{System.Net.WebUtility.HtmlEncode(ly.Name)}</text>");
            sb.AppendLine($"  <text x=\"{colX + colW + 15}\" y=\"{y1 + h / 2 + 15}\" class=\"st-depth\">({ly.FromDepth} - {ly.ToDepth}m)</text>");
        }

        sb.AppendLine($"  <text x=\"{colX - 15}\" y=\"{colTop + colHeight + 4}\" class=\"st-tick\">{maxDepth}m</text>");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
