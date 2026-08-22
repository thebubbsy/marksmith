using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Astronomy;

public class KeplerOrbitModel
{
    public string Title { get; set; } = "Keplerian Planetary Orbit & Ellipse";
    public double SemiMajorAxisAu { get; set; } = 1.524; // a in AU (e.g. Mars)
    public double Eccentricity { get; set; } = 0.25;    // e (0 to 0.95)
    public double TrueAnomalyDeg { get; set; } = 60.0;  // nu (deg)

    public double SemiMinorAxisAu => SemiMajorAxisAu * Math.Sqrt(1.0 - Eccentricity * Eccentricity);
    public double PeriapsisAu => SemiMajorAxisAu * (1.0 - Eccentricity);
    public double ApoapsisAu => SemiMajorAxisAu * (1.0 + Eccentricity);

    public double CurrentRadiusAu
    {
        get
        {
            double rad = TrueAnomalyDeg * (Math.PI / 180.0);
            return (SemiMajorAxisAu * (1.0 - Eccentricity * Eccentricity)) / (1.0 + Eccentricity * Math.Cos(rad));
        }
    }

    public double OrbitalPeriodYears => Math.Pow(SemiMajorAxisAu, 1.5); // Kepler's 3rd Law around Sun
}

public static class KeplerOrbitVisualizerService
{
    private static readonly Regex OrbitFenceRegex = new(
        @":::(?:orbit|kepler-orbit|planetary-orbit)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AxisRegex = new(
        @"(?:a|axis|semi_major)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:AU)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EccRegex = new(
        @"(?:e|eccentricity)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AnomalyRegex = new(
        @"(?:nu|anomaly|angle)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:deg)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static KeplerOrbitModel ParseOrbit(string blockText, string defaultTitle = "Keplerian Planetary Orbit & Ellipse")
    {
        var model = new KeplerOrbitModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = OrbitFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var am = AxisRegex.Match(header);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                model.SemiMajorAxisAu = Math.Clamp(a, 0.1, 50.0);

            var em = EccRegex.Match(header);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e))
                model.Eccentricity = Math.Clamp(e, 0.0, 0.999);

            var num = AnomalyRegex.Match(header);
            if (num.Success && double.TryParse(num.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double nu))
                model.TrueAnomalyDeg = nu % 360;

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var am = AxisRegex.Match(l);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                model.SemiMajorAxisAu = Math.Clamp(a, 0.1, 50.0);

            var em = EccRegex.Match(l);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e))
                model.Eccentricity = Math.Clamp(e, 0.0, 0.999);

            var num = AnomalyRegex.Match(l);
            if (num.Success && double.TryParse(num.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double nu))
                model.TrueAnomalyDeg = nu % 360;
        }

        return model;
    }

    public static string RenderOrbitSvg(KeplerOrbitModel model)
    {
        double width = 500;
        double height = 280;
        double cx = 170; // Geometric center of ellipse
        double cy = 150;
        double aPixels = 120.0;
        double bPixels = aPixels * Math.Sqrt(1.0 - model.Eccentricity * model.Eccentricity);
        double cPixels = aPixels * model.Eccentricity; // Focus distance from center

        double starX = cx - cPixels; // Central primary star at primary focus
        double starY = cy;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-orbit-svg\">");
        sb.AppendLine("""
            <style>
              .orb-bg { fill: #070b14; stroke: #1e293b; stroke-width: 1.5; }
              .orb-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .orb-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .orb-ellipse { fill: none; stroke: #38bdf8; stroke-width: 1.8; }
              .orb-axis { stroke: #334155; stroke-width: 1; stroke-dasharray: 3 3; }
              .orb-star { fill: #fbbf24; stroke: #ffffff; stroke-width: 2; }
              .orb-planet { fill: #38bdf8; stroke: #ffffff; stroke-width: 1.5; }
              .orb-sector { fill: #38bdf8; fill-opacity: 0.12; }
              .orb-card-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1; }
              .orb-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .orb-lbl { font-family: Segoe UI, sans-serif; font-size: 10px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"orb-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"orb-title\">🪐 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"orb-meta\">a = {model.SemiMajorAxisAu:F3} AU • e = {model.Eccentricity:F3} • Period = {model.OrbitalPeriodYears:F2} yrs</text>");

        // Semi-Major & Minor Axis lines
        sb.AppendLine($"  <line x1=\"{cx - aPixels - 15}\" y1=\"{cy}\" x2=\"{cx + aPixels + 15}\" y2=\"{cy}\" class=\"orb-axis\" />");
        sb.AppendLine($"  <line x1=\"{cx}\" y1=\"{cy - bPixels - 10}\" x2=\"{cx}\" y2=\"{cy + bPixels + 10}\" class=\"orb-axis\" />");

        // Orbit Ellipse
        sb.AppendLine($"  <ellipse cx=\"{cx}\" cy=\"{cy}\" rx=\"{aPixels}\" ry=\"{bPixels}\" class=\"orb-ellipse\" />");

        // Planet Position from Focus (True Anomaly nu)
        double rad = model.TrueAnomalyDeg * (Math.PI / 180.0);
        double rPixels = (aPixels * (1.0 - model.Eccentricity * model.Eccentricity)) / (1.0 + model.Eccentricity * Math.Cos(rad));
        double planetX = starX + rPixels * Math.Cos(rad);
        double planetY = starY - rPixels * Math.Sin(rad);

        // Swept Area Sector from Star to Planet (Kepler's 2nd Law demonstration)
        string sectorPath = $"M {starX:F1} {starY:F1} L {starX + aPixels * (1.0 - model.Eccentricity):F1} {starY:F1} A {aPixels} {bPixels} 0 0 0 {planetX:F1} {planetY:F1} Z";
        sb.AppendLine($"  <path d=\"{sectorPath}\" class=\"orb-sector\" />");

        // Radial Vector from Sun to Planet
        sb.AppendLine($"  <line x1=\"{starX:F1}\" y1=\"{starY:F1}\" x2=\"{planetX:F1}\" y2=\"{planetY:F1}\" stroke=\"#fbbf24\" stroke-width=\"1.2\" stroke-dasharray=\"2 2\" />");

        // Primary Focus Star (Sun)
        sb.AppendLine($"  <circle cx=\"{starX:F1}\" cy=\"{starY:F1}\" r=\"8\" class=\"orb-star\" />");
        sb.AppendLine($"  <text x=\"{starX - 4:F1}\" y=\"{starY + 18:F1}\" font-family=\"Segoe UI, sans-serif\" font-size=\"8.5\" fill=\"#fbbf24\">Sun (Focus)</text>");

        // Planet Body
        sb.AppendLine($"  <circle cx=\"{planetX:F1}\" cy=\"{planetY:F1}\" r=\"5.5\" class=\"orb-planet\" />");
        sb.AppendLine($"  <text x=\"{planetX + 8:F1}\" y=\"{planetY + 3:F1}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#f8fafc\">Planet (ν={model.TrueAnomalyDeg:F0}°)</text>");

        // Results Card on Right
        double cardX = 315;
        double cardY = 65;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"165\" height=\"185\" rx=\"6\" class=\"orb-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 22}\" class=\"orb-lbl\">Periapsis (Closest):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 38}\" class=\"orb-val\">{model.PeriapsisAu:F3} AU</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 62}\" class=\"orb-lbl\">Apoapsis (Furthest):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 78}\" class=\"orb-val\">{model.ApoapsisAu:F3} AU</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 102}\" class=\"orb-lbl\">Current Distance (r):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 118}\" class=\"orb-val\" fill=\"#fbbf24\">{model.CurrentRadiusAu:F3} AU</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 142}\" class=\"orb-lbl\">Orbital Period (T):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 158}\" class=\"orb-val\" fill=\"#10b981\">{model.OrbitalPeriodYears:F2} Years</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
