using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Astronomy;

public record OrbitBody(string Name, double SemiMajorAxisAU, double Eccentricity, string ColorHex);

public class OrbitSystemModel
{
    public string Title { get; set; } = "Planetary Orbits";
    public List<OrbitBody> Bodies { get; } = new();
}

/// <summary>
/// Service for parsing Keplerian orbital parameters and rendering elliptical planetary orbit diagrams in SVG.
/// </summary>
public static class PlanetaryOrbitRendererService
{
    private static readonly Regex OrbitFenceRegex = new(
        @":::orbit([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex BodyRegex = new(
        @"body\s+""([^""]+)""\s+a=(-?\d+(?:\.\d+)?)\s+e=(-?\d+(?:\.\d+)?)(?:\s*\[color:\s*(#[0-9A-Fa-f]{6})\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static OrbitSystemModel ParseOrbit(string blockText, string defaultTitle = "Planetary Orbits")
    {
        var model = new OrbitSystemModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = OrbitFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var bm = BodyRegex.Match(l);
            if (bm.Success)
            {
                string name = bm.Groups[1].Value.Trim();
                double a = double.TryParse(bm.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sa) ? sa : 1.0;
                double e = double.TryParse(bm.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ecc) ? ecc : 0.0;
                string col = bm.Groups[4].Success ? bm.Groups[4].Value : "#38bdf8";
                model.Bodies.Add(new OrbitBody(name, a, e, col));
            }
        }

        return model;
    }

    public static string RenderOrbitSvg(OrbitSystemModel model)
    {
        double width = 420;
        double height = 340;
        double cx = width / 2;
        double cy = height / 2 + 10;
        double scale = 120; // pixels per AU

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-orbit-svg\">");
        sb.AppendLine("""
            <style>
              .ob-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .ob-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .ob-sun { fill: #fbbf24; stroke: #f59e0b; stroke-width: 3; }
              .ob-ellipse { fill: none; stroke-width: 1.2; stroke-dasharray: 4 2; opacity: 0.7; }
              .ob-label { font-family: Segoe UI, sans-serif; font-size: 9px; font-weight: 600; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"ob-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"ob-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Central Star (Sun) at center
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"8\" class=\"ob-sun\" />");

        foreach (var body in model.Bodies)
        {
            double aPx = body.SemiMajorAxisAU * scale;
            double bPx = aPx * Math.Sqrt(Math.Max(0.01, 1 - body.Eccentricity * body.Eccentricity));
            double cPx = aPx * body.Eccentricity; // focal offset

            // Ellipse centered at (cx - cPx, cy) so the Sun at (cx, cy) is at one focus
            double ecx = cx - cPx;
            double ecy = cy;

            sb.AppendLine($"  <ellipse cx=\"{ecx}\" cy=\"{ecy}\" rx=\"{aPx}\" ry=\"{bPx}\" stroke=\"{body.ColorHex}\" class=\"ob-ellipse\" />");

            // Planet disc at perihelion (cx + aPx - cPx, cy)
            double px = cx + (aPx - cPx);
            double py = cy;
            sb.AppendLine($"  <circle cx=\"{px}\" cy=\"{py}\" r=\"4.5\" fill=\"{body.ColorHex}\" />");
            sb.AppendLine($"  <text x=\"{px + 7}\" y=\"{py + 3}\" fill=\"{body.ColorHex}\" class=\"ob-label\">{System.Net.WebUtility.HtmlEncode(body.Name)}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
