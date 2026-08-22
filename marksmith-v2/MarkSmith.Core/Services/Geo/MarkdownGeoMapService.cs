using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Geo;

public record GeoPin(double Lat, double Lon, string Label);
public record GeoRoute(string FromLabel, string ToLabel, string? DistanceLabel);

public class GeoMapModel
{
    public string Title { get; set; } = "Geographic Map";
    public List<GeoPin> Pins { get; } = new();
    public List<GeoRoute> Routes { get; } = new();
}

/// <summary>
/// Service for parsing geographic coordinate waypoints and rendering responsive SVG 2D projected route maps.
/// </summary>
public static class MarkdownGeoMapService
{
    private static readonly Regex MapFenceRegex = new(
        @":::map(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex PinRegex = new(
        @"pin\s*\[\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\]\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RouteRegex = new(
        @"route\s*""([^""]+)""\s*->\s*""([^""]+)""(?:\s*\[([^\]]+)\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a map block into a geographic map model.
    /// </summary>
    public static GeoMapModel ParseMap(string blockText, string defaultTitle = "World Map")
    {
        var model = new GeoMapModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = MapFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var pm = PinRegex.Match(l);
            if (pm.Success)
            {
                double lat = double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double plat) ? plat : 0.0;
                double lon = double.TryParse(pm.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double plon) ? plon : 0.0;
                string label = pm.Groups[3].Value.Trim();
                model.Pins.Add(new GeoPin(lat, lon, label));
                continue;
            }

            var rm = RouteRegex.Match(l);
            if (rm.Success)
            {
                string from = rm.Groups[1].Value.Trim();
                string to = rm.Groups[2].Value.Trim();
                string? dist = rm.Groups[3].Success ? rm.Groups[3].Value.Trim() : null;
                model.Routes.Add(new GeoRoute(from, to, dist));
                continue;
            }
        }

        return model;
    }

    /// <summary>
    /// Renders an SVG geographic vector map with equirectangular projected coordinates and flight path arcs.
    /// </summary>
    public static string RenderMapSvg(GeoMapModel model)
    {
        double width = 500;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-geo-map\">");
        sb.AppendLine("""
            <style>
              .map-bg { fill: #0f141c; stroke: #30363d; stroke-width: 1.5; }
              .map-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #e6edf3; }
              .map-grid { stroke: #21262d; stroke-width: 0.8; stroke-dasharray: 4 4; fill: none; }
              .route-arc { stroke: #58a6ff; stroke-width: 2; stroke-dasharray: 5 3; fill: none; }
              .pin-dot { fill: #f85149; stroke: #ffffff; stroke-width: 1.5; }
              .pin-label { font-family: Segoe UI, sans-serif; font-size: 10px; font-weight: 600; fill: #e6edf3; }
              .dist-badge { font-family: monospace; font-size: 9px; fill: #58a6ff; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"map-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"map-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Lat/Lon Grid lines
        for (int lon = -120; lon <= 120; lon += 60)
        {
            double x = LonToX(lon, width);
            sb.AppendLine($"  <line x1=\"{x}\" y1=\"40\" x2=\"{x}\" y2=\"{height - 20}\" class=\"map-grid\" />");
        }
        for (int lat = -60; lat <= 60; lat += 30)
        {
            double y = LatToY(lat, height);
            sb.AppendLine($"  <line x1=\"20\" y1=\"{y}\" x2=\"{width - 20}\" y2=\"{y}\" class=\"map-grid\" />");
        }

        var pinCoords = new Dictionary<string, (double X, double Y)>();
        foreach (var pin in model.Pins)
        {
            double px = LonToX(pin.Lon, width);
            double py = LatToY(pin.Lat, height);
            pinCoords[pin.Label] = (px, py);
        }

        // Draw Routes
        foreach (var r in model.Routes)
        {
            if (pinCoords.TryGetValue(r.FromLabel, out var p1) && pinCoords.TryGetValue(r.ToLabel, out var p2))
            {
                double mx = (p1.X + p2.X) / 2;
                double my = Math.Min(p1.Y, p2.Y) - 30; // curved arc
                sb.AppendLine($"  <path d=\"M {p1.X} {p1.Y} Q {mx} {my} {p2.X} {p2.Y}\" class=\"route-arc\" />");
                if (!string.IsNullOrEmpty(r.DistanceLabel))
                {
                    sb.AppendLine($"  <text x=\"{mx}\" y=\"{my - 4}\" class=\"dist-badge\" text-anchor=\"middle\">{System.Net.WebUtility.HtmlEncode(r.DistanceLabel)}</text>");
                }
            }
        }

        // Draw Pins
        foreach (var pin in model.Pins)
        {
            var (px, py) = pinCoords[pin.Label];
            sb.AppendLine($"  <circle cx=\"{px}\" cy=\"{py}\" r=\"4\" class=\"pin-dot\" />");
            sb.AppendLine($"  <text x=\"{px + 8}\" y=\"{py + 3}\" class=\"pin-label\">{System.Net.WebUtility.HtmlEncode(pin.Label)}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static double LonToX(double lon, double w) => 20 + ((lon + 180) / 360.0) * (w - 40);
    private static double LatToY(double lat, double h) => (h - 20) - ((lat + 90) / 180.0) * (h - 60);
}
