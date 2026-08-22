using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class MohrCoulombSoilModel
{
    public string Title { get; set; } = "Mohr-Coulomb Soil Shear Failure Envelope";
    public double CohesionKPa { get; set; } = 25.0;       // c' (kPa)
    public double FrictionAngleDeg { get; set; } = 32.0;  // phi' (deg)
    public double NormalStressKPa { get; set; } = 120.0;  // sigma_n (kPa)
    public double PoreWaterPressureKPa { get; set; } = 15.0; // u (kPa)

    // Effective Normal Stress sigma' = sigma_n - u
    public double EffectiveStressKPa => Math.Max(0.0, NormalStressKPa - PoreWaterPressureKPa);

    // Coulomb Shear Strength tau_f = c' + sigma' * tan(phi')
    public double FrictionAngleRad => FrictionAngleDeg * Math.PI / 180.0;
    public double ShearStrengthTauF => CohesionKPa + EffectiveStressKPa * Math.Tan(FrictionAngleRad);

    // Rankine Earth Pressure Coefficients
    public double Ka => (1.0 - Math.Sin(FrictionAngleRad)) / (1.0 + Math.Sin(FrictionAngleRad));
    public double Kp => (1.0 + Math.Sin(FrictionAngleRad)) / (1.0 - Math.Sin(FrictionAngleRad));
}

public static class MohrCoulombSoilService
{
    private static readonly Regex MohrCoulombFenceRegex = new(
        @":::(?:mohr-coulomb|soil-shear|coulomb-failure)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CohesionRegex = new(
        @"(?:c|cohesion)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PhiRegex = new(
        @"(?:phi|friction|friction_angle)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:deg)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SigmaRegex = new(
        @"(?:sigma|normal_stress|sn)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PoreRegex = new(
        @"(?:u|pore|water_pressure)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static MohrCoulombSoilModel ParseSoil(string blockText, string defaultTitle = "Mohr-Coulomb Soil Shear Failure Envelope")
    {
        var model = new MohrCoulombSoilModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = MohrCoulombFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var cm = CohesionRegex.Match(header);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
                model.CohesionKPa = Math.Clamp(c, 0.0, 500.0);

            var pm = PhiRegex.Match(header);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double phi))
                model.FrictionAngleDeg = Math.Clamp(phi, 0.0, 55.0);

            var sm = SigmaRegex.Match(header);
            if (sm.Success && double.TryParse(sm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                model.NormalStressKPa = Math.Clamp(s, 1.0, 2000.0);

            var um = PoreRegex.Match(header);
            if (um.Success && double.TryParse(um.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double u))
                model.PoreWaterPressureKPa = Math.Clamp(u, 0.0, model.NormalStressKPa * 0.9);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var cm = CohesionRegex.Match(l);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
                model.CohesionKPa = Math.Clamp(c, 0.0, 500.0);

            var pm = PhiRegex.Match(l);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double phi))
                model.FrictionAngleDeg = Math.Clamp(phi, 0.0, 55.0);

            var sm = SigmaRegex.Match(l);
            if (sm.Success && double.TryParse(sm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                model.NormalStressKPa = Math.Clamp(s, 1.0, 2000.0);

            var um = PoreRegex.Match(l);
            if (um.Success && double.TryParse(um.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double u))
                model.PoreWaterPressureKPa = Math.Clamp(u, 0.0, model.NormalStressKPa * 0.9);
        }

        return model;
    }

    public static string RenderSoilSvg(MohrCoulombSoilModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double oy = 230;
        double axisW = 235;
        double axisH = 150;

        double maxSigma = Math.Max(200.0, model.NormalStressKPa * 1.6);
        double maxTau = Math.Max(120.0, model.ShearStrengthTauF * 1.5);

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-soil-svg\">");
        sb.AppendLine("""
            <style>
              .mc-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .mc-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .mc-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .mc-axis { stroke: #475569; stroke-width: 1.2; }
              .mc-envelope { stroke: #f43f5e; stroke-width: 2.2; }
              .mc-circle { fill: #38bdf8; fill-opacity: 0.15; stroke: #38bdf8; stroke-width: 1.8; }
              .mc-pt { fill: #fbbf24; stroke: #ffffff; stroke-width: 1.5; }
              .mc-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .mc-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .mc-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"mc-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"mc-title\">🏔 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"mc-meta\">c' = {model.CohesionKPa:F0} kPa • φ' = {model.FrictionAngleDeg:F0}° • u = {model.PoreWaterPressureKPa:F0} kPa</text>");

        // Coordinate Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + axisW + 15}\" y2=\"{oy}\" class=\"mc-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - axisH - 10}\" class=\"mc-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + axisW + 10}\" y=\"{oy + 14}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">σ' (kPa)</text>");
        sb.AppendLine($"  <text x=\"{ox - 8}\" y=\"{oy - axisH - 4}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\" text-anchor=\"end\">τ (kPa)</text>");

        // Coulomb Envelope Line: tau = c' + sigma' * tan(phi')
        double tauAtZero = model.CohesionKPa;
        double tauAtMax = model.CohesionKPa + maxSigma * Math.Tan(model.FrictionAngleRad);

        double envStartY = oy - (tauAtZero / maxTau) * axisH;
        double envEndX = ox + axisW;
        double envEndY = oy - (tauAtMax / maxTau) * axisH;

        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{envStartY:F1}\" x2=\"{envEndX:F1}\" y2=\"{envEndY:F1}\" class=\"mc-envelope\" />");
        sb.AppendLine($"  <text x=\"{envEndX - 20:F1}\" y=\"{envEndY - 6:F1}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f43f5e\">τ = c' + σ'tan(φ')</text>");

        // Mohr Triaxial Stress Circle Tangent to Envelope
        double effSig = model.EffectiveStressKPa;
        double tauF = model.ShearStrengthTauF;
        double pX = ox + (effSig / maxSigma) * axisW;
        double pY = oy - (tauF / maxTau) * axisH;

        // Radius R = tauF * cos(phi)
        double circleRadStress = tauF * Math.Cos(model.FrictionAngleRad);
        double circleCenterStress = effSig + tauF * Math.Sin(model.FrictionAngleRad);

        double ccX = ox + (circleCenterStress / maxSigma) * axisW;
        double ccRadPix = (circleRadStress / maxSigma) * axisW;

        if (ccRadPix > 5.0 && ccRadPix < 100.0)
        {
            sb.AppendLine($"  <circle cx=\"{ccX:F1}\" cy=\"{oy}\" r=\"{ccRadPix:F1}\" class=\"mc-circle\" />");
        }

        // Tangent Point Marker
        sb.AppendLine($"  <circle cx=\"{pX:F1}\" cy=\"{pY:F1}\" r=\"4\" class=\"mc-pt\" />");
        sb.AppendLine($"  <text x=\"{pX - 4:F1}\" y=\"{pY - 6:F1}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">τf</text>");

        // Results Card on Right
        double cardX = 305;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"175\" height=\"195\" rx=\"6\" class=\"mc-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"mc-lbl\">Effective Stress (σ'):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"mc-val\">{model.EffectiveStressKPa:F1} kPa</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"mc-lbl\">Shear Strength (τf):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"mc-val\" fill=\"#10b981\">{model.ShearStrengthTauF:F1} kPa</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"mc-lbl\">Rankine Ka (Active):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"mc-val\">Ka = {model.Ka:F3}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"mc-lbl\">Rankine Kp (Passive):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"mc-val\" fill=\"#fbbf24\">Kp = {model.Kp:F3}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Terzaghi Geotechnical Model</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
