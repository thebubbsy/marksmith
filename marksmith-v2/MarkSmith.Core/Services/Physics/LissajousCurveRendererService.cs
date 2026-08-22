using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Physics;

public class LissajousModel
{
    public string Title { get; set; } = "Lissajous Figure";
    public double FreqX { get; set; } = 3.0;
    public double FreqY { get; set; } = 2.0;
    public double PhaseDeltaPi { get; set; } = 0.5;
}

/// <summary>
/// Service for calculating harmonic oscillator phase shifts and rendering vector SVG Lissajous figures.
/// </summary>
public static class LissajousCurveRendererService
{
    private static readonly Regex LissajousFenceRegex = new(
        @":::lissajous([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex FxRegex = new(
        @"fx\s*=\s*(-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FyRegex = new(
        @"fy\s*=\s*(-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DeltaRegex = new(
        @"delta\s*=\s*(-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static LissajousModel ParseLissajous(string blockText, string defaultTitle = "Lissajous Curve")
    {
        var model = new LissajousModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = LissajousFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var fxM = FxRegex.Match(text);
        if (fxM.Success && double.TryParse(fxM.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pfx)) model.FreqX = pfx;

        var fyM = FyRegex.Match(text);
        if (fyM.Success && double.TryParse(fyM.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pfy)) model.FreqY = pfy;

        var dM = DeltaRegex.Match(text);
        if (dM.Success && double.TryParse(dM.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pd)) model.PhaseDeltaPi = pd;

        return model;
    }

    public static string RenderLissajousSvg(LissajousModel model)
    {
        double width = 320;
        double height = 300;
        double cx = width / 2;
        double cy = height / 2 + 10;
        double ampX = 110;
        double ampY = 100;
        int samples = 360;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-lissajous-svg\">");
        sb.AppendLine("""
            <style>
              .lj-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .lj-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .lj-meta { font-family: monospace; font-size: 10px; fill: #94a3b8; }
              .lj-grid { stroke: #334155; stroke-width: 1; stroke-dasharray: 4 4; }
              .lj-curve { fill: none; stroke: #38bdf8; stroke-width: 2.5; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"lj-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"lj-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"40\" class=\"lj-meta\">Ratio: {model.FreqX}:{model.FreqY} • δ = {model.PhaseDeltaPi}π</text>");

        // Grid lines
        sb.AppendLine($"  <line x1=\"{cx - ampX}\" y1=\"{cy}\" x2=\"{cx + ampX}\" y2=\"{cy}\" class=\"lj-grid\" />");
        sb.AppendLine($"  <line x1=\"{cx}\" y1=\"{cy - ampY}\" x2=\"{cx}\" y2=\"{cy + ampY}\" class=\"lj-grid\" />");

        // Parametric path
        var pathSb = new StringBuilder();
        double deltaRad = model.PhaseDeltaPi * Math.PI;

        for (int i = 0; i <= samples; i++)
        {
            double t = (i / (double)samples) * (2 * Math.PI);
            double x = cx + ampX * Math.Sin(model.FreqX * t + deltaRad);
            double y = cy - ampY * Math.Sin(model.FreqY * t);

            if (i == 0)
                pathSb.Append($"M {x.ToString("F1", CultureInfo.InvariantCulture)} {y.ToString("F1", CultureInfo.InvariantCulture)}");
            else
                pathSb.Append($" L {x.ToString("F1", CultureInfo.InvariantCulture)} {y.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        sb.AppendLine($"  <path d=\"{pathSb}\" class=\"lj-curve\" />");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
