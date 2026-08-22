using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class SlopeStabilityModel
{
    public string Title { get; set; } = "Soil Embankment Slope Stability (Bishop)";
    public double HeightM { get; set; } = 8.0;           // H (m)
    public double SlopeAngleDeg { get; set; } = 30.0;    // beta (deg)
    public double UnitWeightGamma { get; set; } = 19.0;  // gamma (kN/m^3)
    public double CohesionC { get; set; } = 12.0;        // c' (kPa)
    public double FrictionAnglePhiDeg { get; set; } = 26.0; // phi' (deg)
    public double SlipRadiusM { get; set; } = 14.0;      // R (m)

    // Slices approximation (6 slices)
    public int SliceCount { get; set; } = 6;

    // Bishop Simplified Factor of Safety FS = Sum(Resisting) / Sum(Driving)
    // Approximate closed formula for homogeneous slope with circular slip
    public double FactorOfSafety
    {
        get
        {
            double phiRad = FrictionAnglePhiDeg * Math.PI / 180.0;
            double betaRad = SlopeAngleDeg * Math.PI / 180.0;
            // Taylor / Bishop stability number N_s approx c / (gamma * H)
            double nStability = CohesionC / (UnitWeightGamma * HeightM);
            double fsFriction = Math.Tan(phiRad) / Math.Max(0.01, Math.Tan(betaRad));
            double fsCohesion = nStability * 5.5;
            return Math.Clamp(fsFriction + fsCohesion, 0.5, 5.0);
        }
    }

    public string StabilityStatus => FactorOfSafety >= 1.5 ? "SAFE (FS ≥ 1.5)" : (FactorOfSafety >= 1.2 ? "MARGINAL (1.2 ≤ FS < 1.5)" : "CRITICAL (FS < 1.2)");
}

public static class SlopeStabilityService
{
    private static readonly Regex SlopeFenceRegex = new(
        @":::(?:slope-stability|bishop-slope|slip-circle)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HeightRegex = new(
        @"(?:height|\bh\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SlopeRegex = new(
        @"(?:slope|beta|slope_angle)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:deg)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GammaRegex = new(
        @"(?:gamma|unit_weight|density)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][nN]/m3)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CohesionRegex = new(
        @"(?:cohesion|\bc\b|c_prime)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PhiRegex = new(
        @"(?:phi|friction|phi_prime)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:deg)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RadiusRegex = new(
        @"(?:radius|\br\b|slip_radius)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SlopeStabilityModel ParseSlope(string blockText, string defaultTitle = "Soil Embankment Slope Stability (Bishop)")
    {
        var model = new SlopeStabilityModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = SlopeFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var hm = HeightRegex.Match(header);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                model.HeightM = Math.Clamp(h, 1.0, 100.0);

            var sm = SlopeRegex.Match(header);
            if (sm.Success && double.TryParse(sm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double beta))
                model.SlopeAngleDeg = Math.Clamp(beta, 10.0, 80.0);

            var gm = GammaRegex.Match(header);
            if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double gamma))
                model.UnitWeightGamma = Math.Clamp(gamma, 10.0, 30.0);

            var cm = CohesionRegex.Match(header);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
                model.CohesionC = Math.Clamp(c, 0.0, 200.0);

            var pm = PhiRegex.Match(header);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double phi))
                model.FrictionAnglePhiDeg = Math.Clamp(phi, 0.0, 50.0);

            var rm = RadiusRegex.Match(header);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r))
                model.SlipRadiusM = Math.Clamp(r, 2.0, 200.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var hm = HeightRegex.Match(l);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                model.HeightM = Math.Clamp(h, 1.0, 100.0);

            var sm = SlopeRegex.Match(l);
            if (sm.Success && double.TryParse(sm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double beta))
                model.SlopeAngleDeg = Math.Clamp(beta, 10.0, 80.0);

            var gm = GammaRegex.Match(l);
            if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double gamma))
                model.UnitWeightGamma = Math.Clamp(gamma, 10.0, 30.0);

            var cm = CohesionRegex.Match(l);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
                model.CohesionC = Math.Clamp(c, 0.0, 200.0);

            var pm = PhiRegex.Match(l);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double phi))
                model.FrictionAnglePhiDeg = Math.Clamp(phi, 0.0, 50.0);

            var rm = RadiusRegex.Match(l);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r))
                model.SlipRadiusM = Math.Clamp(r, 2.0, 200.0);
        }

        return model;
    }

    public static string RenderSlopeSvg(SlopeStabilityModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double groundY = 220;
        double crestY = 100;
        double toeX = 130;
        double crestX = 220;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-slope-svg\">");
        sb.AppendLine("""
            <style>
              .sl-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .sl-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .sl-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .sl-soil { fill: #78716c; fill-opacity: 0.35; stroke: #a8a29e; stroke-width: 1.8; }
              .sl-slip { fill: #f43f5e; fill-opacity: 0.2; stroke: #f43f5e; stroke-width: 2.2; stroke-dasharray: 4 2; }
              .sl-slice { stroke: #fbbf24; stroke-width: 1; stroke-dasharray: 2 2; }
              .sl-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .sl-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .sl-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"sl-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"sl-title\">⛰ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"sl-meta\">H = {model.HeightM:F1}m • β = {model.SlopeAngleDeg:F0}° • c' = {model.CohesionC:F0}kPa • φ' = {model.FrictionAnglePhiDeg:F0}° • FS = {model.FactorOfSafety:F2}</text>");

        // Embankment Soil Profile
        string soilPath = $"M {ox} {groundY} L {toeX} {groundY} L {crestX} {crestY} L 280 {crestY} L 280 {groundY + 20} L {ox} {groundY + 20} Z";
        sb.AppendLine($"  <path d=\"{soilPath}\" class=\"sl-soil\" />");

        // Circular Slip Failure Surface (Arc from beyond crest to toe)
        double slipCrestX = 250;
        double arcCenterX = 200;
        string slipPath = $"M {toeX} {groundY} Q {arcCenterX} {groundY + 25} {slipCrestX} {crestY}";
        sb.AppendLine($"  <path d=\"{slipPath}\" class=\"sl-slip\" />");
        sb.AppendLine($"  <text x=\"{arcCenterX}\" y=\"{groundY + 16}\" font-family=\"monospace\" font-size=\"8\" fill=\"#f43f5e\" text-anchor=\"middle\">Bishop Slip Arc</text>");

        // Vertical Slices
        int slices = 4;
        for (int i = 1; i <= slices; i++)
        {
            double sx = toeX + i * ((slipCrestX - toeX) / (slices + 1));
            sb.AppendLine($"  <line x1=\"{sx:F1}\" y1=\"{crestY + 25}\" x2=\"{sx:F1}\" y2=\"{groundY + 5}\" class=\"sl-slice\" />");
        }

        // Results Card on Right
        double cardX = 300;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"180\" height=\"195\" rx=\"6\" class=\"sl-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"sl-lbl\">Factor of Safety (FS):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"sl-val\" font-size=\"14\" fill=\"#10b981\">FS = {model.FactorOfSafety:F2}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"sl-lbl\">Slope Stability Assessment:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"sl-val\" fill=\"#fbbf24\">{model.StabilityStatus}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"sl-lbl\">Embankment Height (H):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"sl-val\">H = {model.HeightM:F1} m</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"sl-lbl\">Effective Shear Strength:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"sl-val\">c'={model.CohesionC:F0}kPa, φ'={model.FrictionAnglePhiDeg:F0}°</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Bishop Simplified Method</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
