using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Physics;

public class RelativisticModel
{
    public string Title { get; set; } = "Relativistic Spacetime Minkowski Diagram";
    public double BetaVelocity { get; set; } = 0.866; // v / c (0 to <1)
    public double ProperTimeUs { get; set; } = 2.2;   // Delta t0 (us) (e.g. Muon lifetime)
    public double ProperLengthM { get; set; } = 100.0;// L0 (m)

    // Lorentz Factor gamma = 1 / sqrt(1 - beta^2)
    public double Gamma => 1.0 / Math.Sqrt(Math.Max(0.001, 1.0 - Math.Pow(BetaVelocity, 2)));

    // Dilated Time Delta t = gamma * Delta t0
    public double DilatedTimeUs => Gamma * ProperTimeUs;

    // Contracted Length L = L0 / gamma
    public double ContractedLengthM => ProperLengthM / Gamma;

    // Rapidity theta = atanh(beta)
    public double Rapidity => 0.5 * Math.Log((1.0 + BetaVelocity) / Math.Max(0.0001, 1.0 - BetaVelocity));
}

public static class RelativisticMinkowskiService
{
    private static readonly Regex RelativisticFenceRegex = new(
        @":::(?:relativistic|minkowski|lorentz)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BetaRegex = new(
        @"(?:beta|v|velocity)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:c)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProperTimeRegex = new(
        @"(?:proper_time|t0|time)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:us|µs|s)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProperLengthRegex = new(
        @"(?:proper_length|l0|length)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RelativisticModel ParseRelativistic(string blockText, string defaultTitle = "Relativistic Spacetime Minkowski Diagram")
    {
        var model = new RelativisticModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = RelativisticFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var bm = BetaRegex.Match(header);
            if (bm.Success && double.TryParse(bm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double beta))
                model.BetaVelocity = Math.Clamp(beta, 0.0, 0.999);

            var tm0 = ProperTimeRegex.Match(header);
            if (tm0.Success && double.TryParse(tm0.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double t0))
                model.ProperTimeUs = Math.Clamp(t0, 0.001, 10000.0);

            var lm = ProperLengthRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double l0))
                model.ProperLengthM = Math.Clamp(l0, 0.1, 1000000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var bm = BetaRegex.Match(l);
            if (bm.Success && double.TryParse(bm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double beta))
                model.BetaVelocity = Math.Clamp(beta, 0.0, 0.999);

            var tm0 = ProperTimeRegex.Match(l);
            if (tm0.Success && double.TryParse(tm0.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double t0))
                model.ProperTimeUs = Math.Clamp(t0, 0.001, 10000.0);

            var lm = ProperLengthRegex.Match(l);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double l0))
                model.ProperLengthM = Math.Clamp(l0, 0.1, 1000000.0);
        }

        return model;
    }

    public static string RenderRelativisticSvg(RelativisticModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 150;
        double oy = 160;
        double axisLen = 95;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-relativistic-svg\">");
        sb.AppendLine("""
            <style>
              .rv-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .rv-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .rv-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .rv-axis { stroke: #475569; stroke-width: 1.2; }
              .rv-lightcone { stroke: #fbbf24; stroke-width: 1.5; stroke-dasharray: 4 3; }
              .rv-boost-ct { stroke: #38bdf8; stroke-width: 2; }
              .rv-boost-x { stroke: #f43f5e; stroke-width: 2; }
              .rv-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .rv-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .rv-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"rv-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"rv-title\">🚀 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"rv-meta\">v = {model.BetaVelocity:F3} c • Lorentz Factor γ = {model.Gamma:F3}</text>");

        // Stationary Rest Frame Axes (x, ct)
        sb.AppendLine($"  <line x1=\"{ox - axisLen}\" y1=\"{oy}\" x2=\"{ox + axisLen}\" y2=\"{oy}\" class=\"rv-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy + axisLen}\" x2=\"{ox}\" y2=\"{oy - axisLen}\" class=\"rv-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + axisLen + 4}\" y=\"{oy + 3}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">x</text>");
        sb.AppendLine($"  <text x=\"{ox}\" y=\"{oy - axisLen - 4}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\" text-anchor=\"middle\">ct</text>");

        // 45-degree Invariant Light Cone (Photons: x = ct)
        sb.AppendLine($"  <line x1=\"{ox - axisLen * 0.85}\" y1=\"{oy + axisLen * 0.85}\" x2=\"{ox + axisLen * 0.85}\" y2=\"{oy - axisLen * 0.85}\" class=\"rv-lightcone\" />");
        sb.AppendLine($"  <line x1=\"{ox - axisLen * 0.85}\" y1=\"{oy - axisLen * 0.85}\" x2=\"{ox + axisLen * 0.85}\" y2=\"{oy + axisLen * 0.85}\" class=\"rv-lightcone\" />");
        sb.AppendLine($"  <text x=\"{ox + axisLen * 0.75}\" y=\"{oy - axisLen * 0.85}\" font-family=\"monospace\" font-size=\"8\" fill=\"#fbbf24\">c (Light)</text>");

        // Boosted Moving Frame Axes (ct' at slope 1/beta, x' at slope beta)
        double beta = model.BetaVelocity;
        double ctPrimeEndX = ox + beta * axisLen;
        double ctPrimeEndY = oy - axisLen;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ctPrimeEndX:F1}\" y2=\"{ctPrimeEndY:F1}\" class=\"rv-boost-ct\" />");
        sb.AppendLine($"  <text x=\"{ctPrimeEndX + 4:F1}\" y=\"{ctPrimeEndY:F1}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#38bdf8\">ct'</text>");

        double xPrimeEndX = ox + axisLen;
        double xPrimeEndY = oy - beta * axisLen;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{xPrimeEndX:F1}\" y2=\"{xPrimeEndY:F1}\" class=\"rv-boost-x\" />");
        sb.AppendLine($"  <text x=\"{xPrimeEndX + 4:F1}\" y=\"{xPrimeEndY + 3:F1}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#f43f5e\">x'</text>");

        // Results Card on Right
        double cardX = 300;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"180\" height=\"195\" rx=\"6\" class=\"rv-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"rv-lbl\">Lorentz Factor (γ):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"rv-val\" font-size=\"14\" fill=\"#fbbf24\">γ = {model.Gamma:F3}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"rv-lbl\">Time Dilation (Δt = γ·Δt₀):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"rv-val\" fill=\"#38bdf8\">{model.DilatedTimeUs:F2} μs (was {model.ProperTimeUs:F1}μs)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"rv-lbl\">Length Contraction (L = L₀/γ):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"rv-val\" fill=\"#10b981\">{model.ContractedLengthM:F1} m (was {model.ProperLengthM:F0}m)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"rv-lbl\">Rapidity Parameter (θ):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"rv-val\">θ = {model.Rapidity:F3} rad</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Special Relativity Spacetime</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
