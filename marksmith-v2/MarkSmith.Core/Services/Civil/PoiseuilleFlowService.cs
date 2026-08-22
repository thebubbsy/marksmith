using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class PoiseuilleFlowModel
{
    public string Title { get; set; } = "Poiseuille Laminar Pipe Flow Profile";
    public double PipeRadiusMm { get; set; } = 50.0;    // R (mm)
    public double PipeLengthMeters { get; set; } = 10.0;// L (m)
    public double ViscosityPaS { get; set; } = 0.08;    // mu (Pa.s) (e.g. Engine Oil)
    public double PressureDropPa { get; set; } = 5000.0;// Delta P (Pa)
    public double FluidDensityKgM3 { get; set; } = 880.0;// rho (kg/m3)

    public double RadiusM => PipeRadiusMm / 1000.0;

    // Max centerline velocity u_max = (Delta P * R^2) / (4 * mu * L)
    public double MaxVelocityMps => (PressureDropPa * Math.Pow(RadiusM, 2)) / (4.0 * ViscosityPaS * PipeLengthMeters);
    public double AvgVelocityMps => MaxVelocityMps / 2.0;

    // Volumetric flow rate Q = (pi * Delta P * R^4) / (8 * mu * L)
    public double DischargeQ => (Math.PI * PressureDropPa * Math.Pow(RadiusM, 4)) / (8.0 * ViscosityPaS * PipeLengthMeters);
    public double DischargeLps => DischargeQ * 1000.0;

    // Wall Shear Stress tau_w = (Delta P * R) / (2 * L)
    public double WallShearStressPa => (PressureDropPa * RadiusM) / (2.0 * PipeLengthMeters);

    // Reynolds Number Re = (rho * u_avg * 2R) / mu
    public double ReynoldsNumber => (FluidDensityKgM3 * AvgVelocityMps * 2.0 * RadiusM) / ViscosityPaS;
    public bool IsLaminar => ReynoldsNumber < 2300.0;
}

public static class PoiseuilleFlowService
{
    private static readonly Regex PoiseuilleFenceRegex = new(
        @":::(?:poiseuille|laminar-flow|pipe-flow)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RadiusRegex = new(
        @"(?:r|radius)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LengthRegex = new(
        @"(?:L|length)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ViscosityRegex = new(
        @"(?:mu|viscosity)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[pP]a\.?s)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DpRegex = new(
        @"(?:dp|pressure|delta_p)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[pP]a)?(?:\s*kPa)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PoiseuilleFlowModel ParsePoiseuille(string blockText, string defaultTitle = "Poiseuille Laminar Pipe Flow Profile")
    {
        var model = new PoiseuilleFlowModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = PoiseuilleFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var rm = RadiusRegex.Match(header);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r))
                model.PipeRadiusMm = Math.Clamp(r, 2.0, 500.0);

            var lm = LengthRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double l))
                model.PipeLengthMeters = Math.Clamp(l, 0.1, 1000.0);

            var mm = ViscosityRegex.Match(header);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double mu))
                model.ViscosityPaS = Math.Clamp(mu, 0.0001, 10.0);

            var dpm = DpRegex.Match(header);
            if (dpm.Success && double.TryParse(dpm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dp))
            {
                if (header.Contains("kPa") || header.Contains("kpa")) dp *= 1000.0;
                model.PressureDropPa = Math.Clamp(dp, 10.0, 1000000.0);
            }

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var rm = RadiusRegex.Match(l);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r))
                model.PipeRadiusMm = Math.Clamp(r, 2.0, 500.0);

            var lm = LengthRegex.Match(l);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double len))
                model.PipeLengthMeters = Math.Clamp(len, 0.1, 1000.0);

            var mm = ViscosityRegex.Match(l);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double mu))
                model.ViscosityPaS = Math.Clamp(mu, 0.0001, 10.0);

            var dpm = DpRegex.Match(l);
            if (dpm.Success && double.TryParse(dpm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dp))
            {
                if (l.Contains("kPa") || l.Contains("kpa")) dp *= 1000.0;
                model.PressureDropPa = Math.Clamp(dp, 10.0, 1000000.0);
            }
        }

        return model;
    }

    public static string RenderPoiseuilleSvg(PoiseuilleFlowModel model)
    {
        double width = 500;
        double height = 280;
        double cy = 150;
        double pipeX = 40;
        double pipeW = 240;
        double pipeR = 65;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-poiseuille-svg\">");
        sb.AppendLine("""
            <style>
              .ps-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .ps-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .ps-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .ps-pipe-wall { fill: #334155; stroke: #64748b; stroke-width: 2; }
              .ps-fluid { fill: #0284c7; fill-opacity: 0.15; }
              .ps-centerline { stroke: #475569; stroke-width: 1; stroke-dasharray: 4 3; }
              .ps-profile { fill: none; stroke: #38bdf8; stroke-width: 2.2; }
              .ps-arrow { stroke: #38bdf8; stroke-width: 1.5; }
              .ps-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .ps-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .ps-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"ps-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"ps-title\">🛢 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"ps-meta\">R = {model.PipeRadiusMm:F0}mm • μ = {model.ViscosityPaS:F3} Pa·s • ΔP = {model.PressureDropPa / 1000.0:F1} kPa</text>");

        // Pipe Upper & Lower Walls
        sb.AppendLine($"  <rect x=\"{pipeX}\" y=\"{cy - pipeR - 8}\" width=\"{pipeW}\" height=\"8\" class=\"ps-pipe-wall\" />");
        sb.AppendLine($"  <rect x=\"{pipeX}\" y=\"{cy + pipeR}\" width=\"{pipeW}\" height=\"8\" class=\"ps-pipe-wall\" />");
        sb.AppendLine($"  <rect x=\"{pipeX}\" y=\"{cy - pipeR}\" width=\"{pipeW}\" height=\"{pipeR * 2}\" class=\"ps-fluid\" />");
        sb.AppendLine($"  <line x1=\"{pipeX}\" y1=\"{cy}\" x2=\"{pipeX + pipeW}\" y2=\"{cy}\" class=\"ps-centerline\" />");

        // Parabolic Velocity Profile Vector Curve
        double profileStartX = pipeX + 50;
        double maxVectorLen = 110.0;

        var path = new StringBuilder();
        int steps = 40;
        for (int i = 0; i <= steps; i++)
        {
            double t = (i / (double)steps) * 2.0 - 1.0; // -1 to +1
            double y = cy + t * pipeR;
            double uNorm = 1.0 - (t * t); // Parabolic 1 - (r/R)^2
            double x = profileStartX + uNorm * maxVectorLen;

            if (i == 0) path.Append($"M {x:F1} {y:F1}");
            else path.Append($" L {x:F1} {y:F1}");

            // Draw flow vector arrows at discrete heights
            if (i % 6 == 0 && uNorm > 0.05)
            {
                sb.AppendLine($"  <line x1=\"{profileStartX}\" y1=\"{y:F1}\" x2=\"{x:F1}\" y2=\"{y:F1}\" class=\"ps-arrow\" />");
                sb.AppendLine($"  <polygon points=\"{x:F1},{y:F1} {x - 5:F1},{y - 2.5:F1} {x - 5:F1},{y + 2.5:F1}\" fill=\"#38bdf8\" />");
            }
        }

        sb.AppendLine($"  <path d=\"{path}\" class=\"ps-profile\" />");
        sb.AppendLine($"  <text x=\"{profileStartX + maxVectorLen + 6:F1}\" y=\"{cy + 3}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#38bdf8\">umax</text>");

        // Results Card on Right
        double cardX = 305;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"175\" height=\"195\" rx=\"6\" class=\"ps-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"ps-lbl\">Max Velocity (umax):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"ps-val\">{model.MaxVelocityMps:F2} m/s</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"ps-lbl\">Flow Discharge (Q):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"ps-val\" fill=\"#10b981\">{model.DischargeLps:F2} L/s</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"ps-lbl\">Wall Shear Stress (τw):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"ps-val\">{model.WallShearStressPa:F1} Pa</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"ps-lbl\">Reynolds Number (Re):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"ps-val\" fill=\"#fbbf24\">Re = {model.ReynoldsNumber:F0}</text>");

        string flowRegime = model.IsLaminar ? "✓ Fully Laminar (Re &lt; 2300)" : "⚠ Turbulent / Transition";
        string regimeColor = model.IsLaminar ? "#10b981" : "#f43f5e";
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" font-weight=\"700\" fill=\"{regimeColor}\">{flowRegime}</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
