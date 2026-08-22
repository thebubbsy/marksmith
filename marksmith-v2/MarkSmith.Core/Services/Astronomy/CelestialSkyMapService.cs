using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Astronomy;

public record CelestialStar(string Name, double RaHours, double DecDeg, double Magnitude);
public record ConstellationLine(string FromStar, string ToStar);

public class CelestialSkyMapModel
{
    public string Title { get; set; } = "Celestial Sky Map";
    public List<CelestialStar> Stars { get; } = new();
    public List<ConstellationLine> Lines { get; } = new();
}

/// <summary>
/// Service for parsing astronomical star coordinates and rendering polar stereographic SVG night sky charts.
/// </summary>
public static class CelestialSkyMapService
{
    private static readonly Regex SkyMapFenceRegex = new(
        @":::skymap(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex StarRegex = new(
        @"star\s*""([^""]+)""\s*\[\s*RA:\s*(-?\d+(?:\.\d+)?)\s*h\s*,\s*Dec:\s*(-?\d+(?:\.\d+)?)\s*°?\s*\](?:\s*mag\s*=\s*(-?\d+(?:\.\d+)?))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LineRegex = new(
        @"line\s*""([^""]+)""\s*->\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a skymap block into celestial star and constellation models.
    /// </summary>
    public static CelestialSkyMapModel ParseSkyMap(string blockText, string defaultTitle = "Night Sky Map")
    {
        var model = new CelestialSkyMapModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = SkyMapFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var sm = StarRegex.Match(l);
            if (sm.Success)
            {
                string name = sm.Groups[1].Value.Trim();
                double ra = double.TryParse(sm.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r) ? r : 0.0;
                double dec = double.TryParse(sm.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
                double mag = sm.Groups[4].Success && double.TryParse(sm.Groups[4].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double m) ? m : 1.0;
                model.Stars.Add(new CelestialStar(name, ra, dec, mag));
                continue;
            }

            var lm = LineRegex.Match(l);
            if (lm.Success)
            {
                model.Lines.Add(new ConstellationLine(lm.Groups[1].Value.Trim(), lm.Groups[2].Value.Trim()));
                continue;
            }
        }

        return model;
    }

    /// <summary>
    /// Renders a circular polar stereographic SVG chart of the celestial sphere.
    /// </summary>
    public static string RenderSkyMapSvg(CelestialSkyMapModel model)
    {
        double size = 360;
        double cx = size / 2;
        double cy = size / 2;
        double rSky = 140;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{size}\" height=\"{size + 30}\" viewBox=\"0 0 {size} {size + 30}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-skymap-svg\">");
        sb.AppendLine("""
            <style>
              .sky-bg { fill: #040814; stroke: #1e293b; stroke-width: 2; }
              .sky-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #e2e8f0; text-anchor: middle; }
              .sky-grid { stroke: #1e293b; stroke-width: 0.8; stroke-dasharray: 2 4; fill: none; }
              .const-line { stroke: #38bdf8; stroke-width: 1.2; stroke-dasharray: 4 2; opacity: 0.7; }
              .star-disc { fill: #ffffff; }
              .star-glow { fill: #93c5fd; opacity: 0.35; }
              .star-label { font-family: Segoe UI, sans-serif; font-size: 9px; font-weight: 600; fill: #bae6fd; }
            </style>
            """);

        sb.AppendLine($"  <text x=\"{cx}\" y=\"20\" class=\"sky-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <g transform=\"translate(0, 25)\">");
        sb.AppendLine($"    <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{rSky}\" class=\"sky-bg\" />");

        // Polar Hour Circles
        for (int r = 40; r <= 120; r += 40)
        {
            sb.AppendLine($"    <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\" class=\"sky-grid\" />");
        }
        for (int h = 0; h < 24; h += 6)
        {
            double rad = (h / 24.0) * 2 * Math.PI - (Math.PI / 2);
            double x2 = cx + rSky * Math.Cos(rad);
            double y2 = cy + rSky * Math.Sin(rad);
            sb.AppendLine($"    <line x1=\"{cx}\" y1=\"{cy}\" x2=\"{x2}\" y2=\"{y2}\" class=\"sky-grid\" />");
        }

        var starPos = new Dictionary<string, (double X, double Y)>();
        foreach (var star in model.Stars)
        {
            double theta = (star.RaHours / 24.0) * 2 * Math.PI - (Math.PI / 2);
            double dist = ((90 - star.DecDeg) / 180.0) * rSky;
            double sx = cx + dist * Math.Cos(theta);
            double sy = cy + dist * Math.Sin(theta);
            starPos[star.Name] = (sx, sy);
        }

        // Draw constellation lines
        foreach (var l in model.Lines)
        {
            if (starPos.TryGetValue(l.FromStar, out var p1) && starPos.TryGetValue(l.ToStar, out var p2))
            {
                sb.AppendLine($"    <line x1=\"{p1.X}\" y1=\"{p1.Y}\" x2=\"{p2.X}\" y2=\"{p2.Y}\" class=\"const-line\" />");
            }
        }

        // Draw stars
        foreach (var star in model.Stars)
        {
            var (sx, sy) = starPos[star.Name];
            double starR = Math.Max(2.0, 5.0 - star.Magnitude);
            sb.AppendLine($"    <circle cx=\"{sx}\" cy=\"{sy}\" r=\"{starR * 2.2}\" class=\"star-glow\" />");
            sb.AppendLine($"    <circle cx=\"{sx}\" cy=\"{sy}\" r=\"{starR}\" class=\"star-disc\" />");
            sb.AppendLine($"    <text x=\"{sx + starR + 4}\" y=\"{sy + 3}\" class=\"star-label\">{System.Net.WebUtility.HtmlEncode(star.Name)}</text>");
        }

        sb.AppendLine("  </g>");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
