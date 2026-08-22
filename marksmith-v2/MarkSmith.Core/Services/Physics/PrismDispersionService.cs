using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Physics;

public class SpectralRay
{
    public string Name { get; set; } = "Green";
    public double WavelengthNm { get; set; } = 530.0;
    public string ColorHex { get; set; } = "#22c55e";
    public double RefractiveIndex { get; set; } = 1.520;
    public double DeviationDeg { get; set; } = 38.0;
}

public class PrismDispersionModel
{
    public string Title { get; set; } = "Optical Prism Refraction & Cauchy Dispersion";
    public double ApexAngleDeg { get; set; } = 60.0;     // alpha (deg)
    public double IncidentAngleDeg { get; set; } = 48.0; // theta_i (deg)
    public double CauchyA { get; set; } = 1.5046;        // BK7 crown glass
    public double CauchyB { get; set; } = 4200.0;        // nm^2
    public List<SpectralRay> Rays { get; } = new();

    public double GetRefractiveIndex(double wavelengthNm)
    {
        return CauchyA + (CauchyB / (wavelengthNm * wavelengthNm));
    }
}

public static class PrismDispersionService
{
    private static readonly Regex PrismFenceRegex = new(
        @":::(?:prism|dispersion|optical-prism)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ApexRegex = new(
        @"apex\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:deg)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IncidentRegex = new(
        @"(?:incident|angle|theta_i)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:deg)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PrismDispersionModel ParsePrism(string blockText, string defaultTitle = "Optical Prism Refraction & Cauchy Dispersion")
    {
        var model = new PrismDispersionModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
        {
            BuildSpectrumRays(model);
            return model;
        }

        var fence = PrismFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var am = ApexRegex.Match(header);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ap))
                model.ApexAngleDeg = Math.Clamp(ap, 30.0, 90.0);

            var im = IncidentRegex.Match(header);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double inc))
                model.IncidentAngleDeg = Math.Clamp(inc, 20.0, 75.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var am = ApexRegex.Match(l);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ap))
                model.ApexAngleDeg = Math.Clamp(ap, 30.0, 90.0);

            var im = IncidentRegex.Match(l);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double inc))
                model.IncidentAngleDeg = Math.Clamp(inc, 20.0, 75.0);
        }

        BuildSpectrumRays(model);
        return model;
    }

    private static void BuildSpectrumRays(PrismDispersionModel model)
    {
        model.Rays.Clear();
        var spectrum = new (string name, double wl, string hex)[]
        {
            ("Red", 680.0, "#ef4444"),
            ("Yellow", 580.0, "#eab308"),
            ("Green", 530.0, "#22c55e"),
            ("Cyan", 490.0, "#06b6d4"),
            ("Blue", 450.0, "#3b82f6"),
            ("Violet", 400.0, "#a855f7")
        };

        double theta1 = model.IncidentAngleDeg * Math.PI / 180.0;
        double alpha = model.ApexAngleDeg * Math.PI / 180.0;

        foreach (var (name, wl, hex) in spectrum)
        {
            double n = model.GetRefractiveIndex(wl);
            // Snell's Law at 1st face: sin(theta1) = n * sin(r1)
            double sinR1 = Math.Sin(theta1) / n;
            double r1 = Math.Asin(Math.Clamp(sinR1, -1.0, 1.0));
            // 2nd face: r2 = alpha - r1
            double r2 = alpha - r1;
            // Snell's Law at 2nd face: n * sin(r2) = sin(theta2)
            double sinTheta2 = n * Math.Sin(r2);
            double theta2 = Math.Asin(Math.Clamp(sinTheta2, -1.0, 1.0));
            // Total deviation delta = theta1 + theta2 - alpha
            double delta = (theta1 + theta2 - alpha) * 180.0 / Math.PI;

            model.Rays.Add(new SpectralRay
            {
                Name = name,
                WavelengthNm = wl,
                ColorHex = hex,
                RefractiveIndex = n,
                DeviationDeg = delta
            });
        }
    }

    public static string RenderPrismSvg(PrismDispersionModel model)
    {
        double width = 500;
        double height = 280;
        double apexX = 180;
        double apexY = 80;
        double prismBaseW = 140;
        double prismH = 130;

        double leftX = apexX - prismBaseW / 2;
        double rightX = apexX + prismBaseW / 2;
        double baseY = apexY + prismH;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-prism-svg\">");
        sb.AppendLine("""
            <style>
              .pr-bg { fill: #070b14; stroke: #1e293b; stroke-width: 1.5; }
              .pr-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .pr-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .pr-glass { fill: #38bdf8; fill-opacity: 0.12; stroke: #38bdf8; stroke-width: 1.8; }
              .pr-beam { stroke: #ffffff; stroke-width: 2.5; stroke-opacity: 0.9; }
              .pr-card-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1; }
              .pr-val { font-family: monospace; font-size: 11px; font-weight: 700; }
              .pr-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"pr-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"pr-title\">🌈 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"pr-meta\">Apex Angle α = {model.ApexAngleDeg:F0}° • Incident θi = {model.IncidentAngleDeg:F0}° (BK7 Glass)</text>");

        // Prism Body Triangle
        sb.AppendLine($"  <polygon points=\"{apexX:F1},{apexY:F1} {leftX:F1},{baseY:F1} {rightX:F1},{baseY:F1}\" class=\"pr-glass\" />");

        // Incident White Light Beam
        double inStartX = 40;
        double inStartY = 135;
        double hitFace1X = (apexX + leftX) / 2 + 10;
        double hitFace1Y = (apexY + baseY) / 2 - 5;
        sb.AppendLine($"  <line x1=\"{inStartX}\" y1=\"{inStartY}\" x2=\"{hitFace1X:F1}\" y2=\"{hitFace1Y:F1}\" class=\"pr-beam\" />");
        sb.AppendLine($"  <text x=\"{inStartX + 10}\" y=\"{inStartY - 10}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" font-weight=\"700\" fill=\"#ffffff\">White Light</text>");

        // Refracted Dispersed Spectrum Rays inside and exiting prism
        for (int i = 0; i < model.Rays.Count; i++)
        {
            var ray = model.Rays[i];
            double devFrac = (ray.DeviationDeg - 30.0) / 20.0;
            double hitFace2X = (apexX + rightX) / 2 - 10 + i * 1.5;
            double hitFace2Y = (apexY + baseY) / 2 + 10 + i * 4.0;

            // Ray inside prism
            sb.AppendLine($"  <line x1=\"{hitFace1X:F1}\" y1=\"{hitFace1Y:F1}\" x2=\"{hitFace2X:F1}\" y2=\"{hitFace2Y:F1}\" stroke=\"{ray.ColorHex}\" stroke-width=\"1.5\" stroke-opacity=\"0.7\" />");

            // Exiting ray fan
            double exitLen = 85.0;
            double exitAngleRad = (15.0 + devFrac * 25.0) * Math.PI / 180.0;
            double exitEndX = hitFace2X + exitLen * Math.Cos(exitAngleRad);
            double exitEndY = hitFace2Y + exitLen * Math.Sin(exitAngleRad);

            sb.AppendLine($"  <line x1=\"{hitFace2X:F1}\" y1=\"{hitFace2Y:F1}\" x2=\"{exitEndX:F1}\" y2=\"{exitEndY:F1}\" stroke=\"{ray.ColorHex}\" stroke-width=\"2\" />");
        }

        // Spectral Index & Deviation Table on Right
        double cardX = 330;
        double cardY = 65;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"150\" height=\"185\" rx=\"6\" class=\"pr-card-bg\" />");
        sb.AppendLine($"  <text x=\"{cardX + 10}\" y=\"{cardY + 20}\" class=\"pr-lbl\" font-weight=\"700\" fill=\"#f8fafc\">Spectral Dispersion</text>");

        for (int i = 0; i < model.Rays.Count; i++)
        {
            var ray = model.Rays[i];
            double ry = cardY + 42 + i * 23;
            sb.AppendLine($"  <circle cx=\"{cardX + 14}\" cy=\"{ry - 3}\" r=\"4\" fill=\"{ray.ColorHex}\" />");
            sb.AppendLine($"  <text x=\"{cardX + 26}\" y=\"{ry}\" class=\"pr-lbl\">{ray.Name} ({ray.WavelengthNm:F0}nm):</text>");
            sb.AppendLine($"  <text x=\"{cardX + 138}\" y=\"{ry}\" class=\"pr-val\" fill=\"{ray.ColorHex}\" text-anchor=\"end\">{ray.DeviationDeg:F1}°</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
