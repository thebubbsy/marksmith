using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Physics;

public class DoubleSlitModel
{
    public string Title { get; set; } = "Young's Double Slit";
    public double WavelengthNm { get; set; } = 650.0;
    public double SlitSeparationMm { get; set; } = 0.25;
    public double ScreenDistanceM { get; set; } = 1.5;
}

/// <summary>
/// Service for calculating wave interference patterns and rendering double-slit intensity waveform curves in SVG.
/// </summary>
public static class DoubleSlitInterferenceService
{
    private static readonly Regex DiffractionFenceRegex = new(
        @":::diffraction([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex LambdaRegex = new(
        @"wavelength\s*=\s*(-?\d+(?:\.\d+)?)\s*(?:nm)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SlitDRegex = new(
        @"d\s*=\s*(-?\d+(?:\.\d+)?)\s*(?:mm)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DistLRegex = new(
        @"L\s*=\s*(-?\d+(?:\.\d+)?)\s*(?:m)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static DoubleSlitModel ParseDiffraction(string blockText, string defaultTitle = "Double-Slit Interference")
    {
        var model = new DoubleSlitModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = DiffractionFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var lm = LambdaRegex.Match(text);
        if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pw)) model.WavelengthNm = pw;

        var dm = SlitDRegex.Match(text);
        if (dm.Success && double.TryParse(dm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pd)) model.SlitSeparationMm = pd;

        var distM = DistLRegex.Match(text);
        if (distM.Success && double.TryParse(distM.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pl)) model.ScreenDistanceM = pl;

        return model;
    }

    public static string RenderDiffractionSvg(DoubleSlitModel model)
    {
        double width = 420;
        double height = 240;
        double cx = width / 2;
        double baseCurveY = 160;
        double maxAmp = 80;
        int samples = 200;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-diffraction-svg\">");
        sb.AppendLine("""
            <style>
              .df-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .df-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .df-meta { font-family: monospace; font-size: 10px; fill: #94a3b8; }
              .df-curve { fill: none; stroke: #ef4444; stroke-width: 2; }
              .df-axis { stroke: #334155; stroke-width: 1; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"df-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"df-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"40\" class=\"df-meta\">λ={model.WavelengthNm}nm • d={model.SlitSeparationMm}mm • L={model.ScreenDistanceM}m</text>");

        // Optical screen fringe bar
        for (int i = 0; i < samples; i++)
        {
            double x = 40 + (i / (double)samples) * (width - 80);
            double yOffset = (x - cx) / 12.0;
            double intensity = Math.Cos(yOffset) * Math.Cos(yOffset); // Cos^2 interference
            string opacity = intensity.ToString("F2", CultureInfo.InvariantCulture);
            sb.AppendLine($"  <rect x=\"{x}\" y=\"52\" width=\"2\" height=\"20\" fill=\"#ef4444\" fill-opacity=\"{opacity}\" />");
        }

        // Intensity axis
        sb.AppendLine($"  <line x1=\"40\" y1=\"{baseCurveY}\" x2=\"{width - 40}\" y2=\"{baseCurveY}\" class=\"df-axis\" />");

        // Intensity waveform curve
        var pathSb = new StringBuilder();
        for (int i = 0; i <= samples; i++)
        {
            double x = 40 + (i / (double)samples) * (width - 80);
            double yOffset = (x - cx) / 12.0;
            double intensity = Math.Cos(yOffset) * Math.Cos(yOffset);
            double y = baseCurveY - intensity * maxAmp;

            if (i == 0)
                pathSb.Append($"M {x.ToString("F1", CultureInfo.InvariantCulture)} {y.ToString("F1", CultureInfo.InvariantCulture)}");
            else
                pathSb.Append($" L {x.ToString("F1", CultureInfo.InvariantCulture)} {y.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        sb.AppendLine($"  <path d=\"{pathSb}\" class=\"df-curve\" />");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
