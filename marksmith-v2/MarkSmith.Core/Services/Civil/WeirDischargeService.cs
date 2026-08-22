using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class WeirFlowModel
{
    public string Title { get; set; } = "Hydraulic Weir Flow Discharge";
    public string WeirType { get; set; } = "v-notch"; // "v-notch", "rectangular", "cipolletti"
    public double HeadMeters { get; set; } = 0.35;    // H (m)
    public double NotchAngleDeg { get; set; } = 90.0; // theta (deg) for V-notch
    public double CrestWidthMeters { get; set; } = 1.2; // L (m) for rectangular
    public double Cd { get; set; } = 0.59;            // Discharge coefficient
    public const double Gravity = 9.80665;            // g (m/s^2)

    /// <summary>
    /// Computes volumetric discharge flow rate Q in m^3/s.
    /// </summary>
    public double DischargeQ
    {
        get
        {
            if (WeirType.Contains("rect"))
            {
                // Francis formula: Q = (2/3) * Cd * L * sqrt(2g) * H^(3/2)
                return (2.0 / 3.0) * Cd * CrestWidthMeters * Math.Sqrt(2.0 * Gravity) * Math.Pow(HeadMeters, 1.5);
            }
            else if (WeirType.Contains("cipolletti"))
            {
                // Cipolletti trapezoidal weir: Q = 1.86 * L * H^(3/2)
                return 1.86 * CrestWidthMeters * Math.Pow(HeadMeters, 1.5);
            }
            else
            {
                // Kindsvater-Shen / Thomson formula for V-notch: Q = (8/15) * Cd * sqrt(2g) * tan(theta/2) * H^(5/2)
                double halfAngleRad = (NotchAngleDeg / 2.0) * (Math.PI / 180.0);
                return (8.0 / 15.0) * Cd * Math.Sqrt(2.0 * Gravity) * Math.Tan(halfAngleRad) * Math.Pow(HeadMeters, 2.5);
            }
        }
    }

    public double DischargeLps => DischargeQ * 1000.0; // Liters per second
}

public static class WeirDischargeService
{
    private static readonly Regex WeirFenceRegex = new(
        @":::(?:weir|hydraulic-weir|v-notch)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TypeRegex = new(
        @"type\s*[:=]\s*""?([a-zA-Z0-9_\-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HeadRegex = new(
        @"(?:head|H)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AngleRegex = new(
        @"(?:angle|theta)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static WeirFlowModel ParseWeir(string blockText, string defaultTitle = "Hydraulic Weir Flow Discharge")
    {
        var model = new WeirFlowModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = WeirFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var ty = TypeRegex.Match(header);
            if (ty.Success) model.WeirType = ty.Groups[1].Value.ToLowerInvariant();

            var hm = HeadRegex.Match(header);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                model.HeadMeters = Math.Clamp(h, 0.02, 3.0);

            var am = AngleRegex.Match(header);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                model.NotchAngleDeg = Math.Clamp(a, 20.0, 120.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var ty = TypeRegex.Match(l);
            if (ty.Success) model.WeirType = ty.Groups[1].Value.ToLowerInvariant();

            var hm = HeadRegex.Match(l);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                model.HeadMeters = Math.Clamp(h, 0.02, 3.0);

            var am = AngleRegex.Match(l);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                model.NotchAngleDeg = Math.Clamp(a, 20.0, 120.0);
        }

        return model;
    }

    public static string RenderWeirSvg(WeirFlowModel model)
    {
        double width = 480;
        double height = 260;
        double weirX = 50;
        double weirY = 80;
        double weirW = 200;
        double weirH = 120;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-weir-svg\">");
        sb.AppendLine("""
            <style>
              .weir-bg { fill: #0b1120; stroke: #1e293b; stroke-width: 1.5; }
              .weir-plate { fill: #334155; stroke: #64748b; stroke-width: 1.5; }
              .weir-water { fill: #0284c7; fill-opacity: 0.35; stroke: #38bdf8; stroke-width: 1.5; }
              .weir-nappe { fill: #38bdf8; fill-opacity: 0.25; stroke: #38bdf8; stroke-width: 1; stroke-dasharray: 3 2; }
              .weir-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .weir-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .weir-dim { stroke: #fbbf24; stroke-width: 1.2; stroke-dasharray: 3 2; }
              .weir-dim-text { font-family: monospace; font-size: 10px; font-weight: 700; fill: #fbbf24; }
              .weir-res-title { font-family: Segoe UI, sans-serif; font-size: 11px; font-weight: 700; fill: #f8fafc; }
              .weir-res-val { font-family: monospace; font-size: 16px; font-weight: 700; fill: #38bdf8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"weir-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"weir-title\">🌊 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"weir-meta\">Type: {model.WeirType.ToUpperInvariant()} • Head (H): {model.HeadMeters:F2} m</text>");

        // Upstream Water Pool
        double waterTopY = weirY + 20;
        double crestY = weirY + 80;
        sb.AppendLine($"  <rect x=\"{weirX}\" y=\"{waterTopY}\" width=\"{weirW}\" height=\"{crestY + 30 - waterTopY}\" class=\"weir-water\" />");

        // Triangular V-Notch or Rectangular Weir Plate
        if (model.WeirType.Contains("v-notch") || model.WeirType.Contains("triangular"))
        {
            // V-notch cutout
            double cx = weirX + weirW / 2;
            double notchTopW = 70;
            string platePath = $"M {weirX} {weirY} L {cx - notchTopW / 2} {weirY} L {cx} {crestY} L {cx + notchTopW / 2} {weirY} L {weirX + weirW} {weirY} L {weirX + weirW} {weirY + weirH} L {weirX} {weirY + weirH} Z";
            sb.AppendLine($"  <path d=\"{platePath}\" class=\"weir-plate\" />");

            // Overflow Nappe cascade
            sb.AppendLine($"  <polygon points=\"{cx - notchTopW / 2},{weirY} {cx},{crestY} {cx + notchTopW / 2},{weirY} {cx + 20},{weirY + weirH} {cx - 20},{weirY + weirH}\" class=\"weir-nappe\" />");
        }
        else
        {
            // Rectangular Weir Crest
            double cx = weirX + weirW / 2;
            double rectW = 60;
            string platePath = $"M {weirX} {weirY} L {cx - rectW / 2} {weirY} L {cx - rectW / 2} {crestY} L {cx + rectW / 2} {crestY} L {cx + rectW / 2} {weirY} L {weirX + weirW} {weirY} L {weirX + weirW} {weirY + weirH} L {weirX} {weirY + weirH} Z";
            sb.AppendLine($"  <path d=\"{platePath}\" class=\"weir-plate\" />");
        }

        // Head H Dimension Line
        sb.AppendLine($"  <line x1=\"{weirX + 15}\" y1=\"{waterTopY}\" x2=\"{weirX + 15}\" y2=\"{crestY}\" class=\"weir-dim\" />");
        sb.AppendLine($"  <line x1=\"{weirX + 10}\" y1=\"{waterTopY}\" x2=\"{weirX + 20}\" y2=\"{waterTopY}\" stroke=\"#fbbf24\" stroke-width=\"1\" />");
        sb.AppendLine($"  <line x1=\"{weirX + 10}\" y1=\"{crestY}\" x2=\"{weirX + 20}\" y2=\"{crestY}\" stroke=\"#fbbf24\" stroke-width=\"1\" />");
        sb.AppendLine($"  <text x=\"{weirX + 22}\" y=\"{(waterTopY + crestY) / 2 + 3}\" class=\"weir-dim-text\">H = {model.HeadMeters:F2}m</text>");

        // Hydrodynamic Results Card on Right
        double cardX = 275;
        double cardY = 70;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"185\" height=\"135\" rx=\"6\" fill=\"#1e293b\" stroke=\"#334155\" stroke-width=\"1\" />");
        sb.AppendLine($"  <text x=\"{cardX + 14}\" y=\"{cardY + 24}\" class=\"weir-res-title\">Discharge Flow Rate (Q):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 14}\" y=\"{cardY + 54}\" class=\"weir-res-val\">{model.DischargeLps:F1} L/s</text>");
        sb.AppendLine($"  <text x=\"{cardX + 14}\" y=\"{cardY + 76}\" font-family=\"monospace\" font-size=\"11\" fill=\"#94a3b8\">({model.DischargeQ:F4} m³/s)</text>");
        sb.AppendLine($"  <text x=\"{cardX + 14}\" y=\"{cardY + 102}\" font-family=\"monospace\" font-size=\"10\" fill=\"#64748b\">Cd = {model.Cd:F2} • θ = {model.NotchAngleDeg:F0}°</text>");
        sb.AppendLine($"  <text x=\"{cardX + 14}\" y=\"{cardY + 120}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#38bdf8\">Kindsvater-Shen Equation</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
