using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class HydraulicJumpModel
{
    public string Title { get; set; } = "Hydraulic Jump Energy Dissipation";
    public double DepthY1Meters { get; set; } = 0.40;  // Upstream initial depth (m)
    public double VelocityV1Mps { get; set; } = 6.5;   // Upstream velocity (m/s)
    public double ChannelWidthMeters { get; set; } = 3.0; // B (m)
    public const double Gravity = 9.80665;

    // Upstream Froude Number Fr1 = v1 / sqrt(g * y1)
    public double FroudeNumberFr1 => VelocityV1Mps / Math.Sqrt(Gravity * DepthY1Meters);
    public bool IsSupercritical => FroudeNumberFr1 > 1.0;

    // Bélanger Subcritical Conjugate Depth y2 = (y1 / 2) * (sqrt(1 + 8*Fr1^2) - 1)
    public double SubcriticalDepthY2 => (DepthY1Meters / 2.0) * (Math.Sqrt(1.0 + 8.0 * Math.Pow(FroudeNumberFr1, 2)) - 1.0);

    // Specific Head Energy Loss Delta E = (y2 - y1)^3 / (4 * y1 * y2)
    public double EnergyLossHeadMeters => Math.Pow(SubcriticalDepthY2 - DepthY1Meters, 3) / (4.0 * DepthY1Meters * SubcriticalDepthY2);

    // Initial Specific Energy E1 = y1 + (v1^2 / 2g)
    public double InitialSpecificEnergyMeters => DepthY1Meters + (Math.Pow(VelocityV1Mps, 2) / (2.0 * Gravity));

    public double DissipationEfficiencyPercent => InitialSpecificEnergyMeters > 0.001
        ? (EnergyLossHeadMeters / InitialSpecificEnergyMeters) * 100.0
        : 0.0;
}

public static class HydraulicJumpService
{
    private static readonly Regex JumpFenceRegex = new(
        @":::(?:hydraulic-jump|hydraulicjump|stilling-basin)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Y1Regex = new(
        @"(?:y1|depth|initial_depth)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex V1Regex = new(
        @"(?:v1|velocity|speed)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m/s)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WidthRegex = new(
        @"(?:width|b|channel_width)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static HydraulicJumpModel ParseJump(string blockText, string defaultTitle = "Hydraulic Jump Energy Dissipation")
    {
        var model = new HydraulicJumpModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = JumpFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var ym = Y1Regex.Match(header);
            if (ym.Success && double.TryParse(ym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double y1))
                model.DepthY1Meters = Math.Clamp(y1, 0.05, 10.0);

            var vm = V1Regex.Match(header);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double v1))
                model.VelocityV1Mps = Math.Clamp(v1, 0.1, 50.0);

            var wm = WidthRegex.Match(header);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double w))
                model.ChannelWidthMeters = Math.Clamp(w, 0.5, 100.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var ym = Y1Regex.Match(l);
            if (ym.Success && double.TryParse(ym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double y1))
                model.DepthY1Meters = Math.Clamp(y1, 0.05, 10.0);

            var vm = V1Regex.Match(l);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double v1))
                model.VelocityV1Mps = Math.Clamp(v1, 0.1, 50.0);

            var wm = WidthRegex.Match(l);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double w))
                model.ChannelWidthMeters = Math.Clamp(w, 0.5, 100.0);
        }

        return model;
    }

    public static string RenderJumpSvg(HydraulicJumpModel model)
    {
        double width = 500;
        double height = 280;
        double bedY = 210;
        double bedStartX = 40;
        double bedEndX = 280;

        double scaleY = 30.0;
        double y1Pix = Math.Clamp(model.DepthY1Meters * scaleY, 12.0, 35.0);
        double y2Pix = Math.Clamp(model.SubcriticalDepthY2 * scaleY, y1Pix + 20.0, 120.0);

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-jump-svg\">");
        sb.AppendLine("""
            <style>
              .hj-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .hj-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .hj-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .hj-bed { fill: #334155; stroke: #64748b; stroke-width: 2; }
              .hj-super { fill: #0284c7; fill-opacity: 0.4; stroke: #38bdf8; stroke-width: 1.5; }
              .hj-roller { fill: #38bdf8; fill-opacity: 0.25; stroke: #f43f5e; stroke-width: 1.5; stroke-dasharray: 4 2; }
              .hj-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .hj-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .hj-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"hj-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"hj-title\">🌊 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"hj-meta\">y1 = {model.DepthY1Meters:F2}m • v1 = {model.VelocityV1Mps:F2} m/s • Upstream Fr1 = {model.FroudeNumberFr1:F2}</text>");

        // Channel Bed
        sb.AppendLine($"  <rect x=\"{bedStartX}\" y=\"{bedY}\" width=\"{bedEndX - bedStartX}\" height=\"14\" class=\"hj-bed\" />");

        // Water Profile (Supercritical -> Hydraulic Jump Roller -> Subcritical)
        double jumpStartX = bedStartX + 60;
        double jumpEndX = bedStartX + 150;

        string waterPath = $"M {bedStartX} {bedY} L {bedStartX} {bedY - y1Pix} L {jumpStartX} {bedY - y1Pix} Q {(jumpStartX + jumpEndX) / 2} {bedY - y2Pix - 15} {jumpEndX} {bedY - y2Pix} L {bedEndX} {bedY - y2Pix} L {bedEndX} {bedY} Z";
        sb.AppendLine($"  <path d=\"{waterPath}\" class=\"hj-super\" />");

        // Turbulent Surface Roller Circle / Swirl
        double rollerCx = (jumpStartX + jumpEndX) / 2;
        double rollerCy = bedY - (y1Pix + y2Pix) / 2;
        sb.AppendLine($"  <circle cx=\"{rollerCx:F1}\" cy=\"{rollerCy:F1}\" r=\"14\" class=\"hj-roller\" />");
        sb.AppendLine($"  <text x=\"{rollerCx:F1}\" y=\"{rollerCy + 3:F1}\" font-family=\"monospace\" font-size=\"8\" fill=\"#f43f5e\" text-anchor=\"middle\">Turbulence</text>");

        // Dimension annotations: y1 and y2
        sb.AppendLine($"  <line x1=\"{bedStartX + 20}\" y1=\"{bedY}\" x2=\"{bedStartX + 20}\" y2=\"{bedY - y1Pix}\" stroke=\"#fbbf24\" stroke-width=\"1.2\" />");
        sb.AppendLine($"  <text x=\"{bedStartX + 24}\" y=\"{bedY - y1Pix / 2 + 3}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">y₁</text>");

        sb.AppendLine($"  <line x1=\"{bedEndX - 20}\" y1=\"{bedY}\" x2=\"{bedEndX - 20}\" y2=\"{bedY - y2Pix}\" stroke=\"#fbbf24\" stroke-width=\"1.2\" />");
        sb.AppendLine($"  <text x=\"{bedEndX - 16}\" y=\"{bedY - y2Pix / 2 + 3}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">y₂</text>");

        // Results Card on Right
        double cardX = 300;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"180\" height=\"195\" rx=\"6\" class=\"hj-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"hj-lbl\">Initial Froude (Fr₁):</text>");
        string frClass = model.IsSupercritical ? "Supercritical" : "Subcritical";
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"hj-val\" fill=\"#f43f5e\">Fr₁ = {model.FroudeNumberFr1:F2} ({frClass})</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"hj-lbl\">Conjugate Depth (y₂):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"hj-val\" fill=\"#10b981\">{model.SubcriticalDepthY2:F2} m</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"hj-lbl\">Energy Head Loss (ΔE):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"hj-val\">{model.EnergyLossHeadMeters:F2} m</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"hj-lbl\">Dissipation Efficiency:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"hj-val\" fill=\"#fbbf24\">η = {model.DissipationEfficiencyPercent:F1} %</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Bélanger Hydraulic Equation</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
