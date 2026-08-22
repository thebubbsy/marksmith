using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class RfSmithMatchingModel
{
    public string Title { get; set; } = "RF Smith Chart Impedance Matching";
    public double CharacteristicZ0 { get; set; } = 50.0; // Z0 (Ohms)
    public double LoadResistanceRl { get; set; } = 25.0; // RL (Ohms)
    public double LoadReactanceXl { get; set; } = 40.0;  // XL (Ohms)
    public double FrequencyGhz { get; set; } = 2.4;      // f (GHz)

    // Normalized impedance z = r + jx
    public double NormalizedR => LoadResistanceRl / Math.Max(1.0, CharacteristicZ0);
    public double NormalizedX => LoadReactanceXl / Math.Max(1.0, CharacteristicZ0);

    // Complex Reflection Coefficient Gamma = (z - 1) / (z + 1)
    public double GammaReal
    {
        get
        {
            double r = NormalizedR;
            double x = NormalizedX;
            double denom = Math.Pow(r + 1.0, 2) + Math.Pow(x, 2);
            return (Math.Pow(r, 2) - 1.0 + Math.Pow(x, 2)) / Math.Max(1e-6, denom);
        }
    }

    public double GammaImag
    {
        get
        {
            double r = NormalizedR;
            double x = NormalizedX;
            double denom = Math.Pow(r + 1.0, 2) + Math.Pow(x, 2);
            return (2.0 * x) / Math.Max(1e-6, denom);
        }
    }

    // Magnitude |Gamma|
    public double GammaMag => Math.Sqrt(Math.Pow(GammaReal, 2) + Math.Pow(GammaImag, 2));

    // Phase angle in degrees
    public double GammaPhaseDeg => Math.Atan2(GammaImag, GammaReal) * (180.0 / Math.PI);

    // VSWR = (1 + |Gamma|) / (1 - |Gamma|)
    public double Vswr => (1.0 + GammaMag) / Math.Max(1e-4, 1.0 - GammaMag);

    // Return Loss RL = -20 * log10(|Gamma|) in dB
    public double ReturnLossDb => -20.0 * Math.Log10(Math.Clamp(GammaMag, 1e-4, 1.0));
}

public static class RfSmithMatchingService
{
    private static readonly Regex SmithFenceRegex = new(
        @":::(?:rf-matching|smith-matching|antenna-matching)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Z0Regex = new(
        @"(?:z0|char_imp|z_ref)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[oO]hm|Ω)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LoadRegex = new(
        @"(?:load|zl|z_load)\s*[:=]\s*""?(\d+(?:\.\d+)?)\s*([+-]\s*\d+(?:\.\d+)?j|[+-]\s*j\d+(?:\.\d+)?)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RlRegex = new(
        @"(?:rl|r_load|r)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[oO]hm|Ω)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex XlRegex = new(
        @"(?:xl|x_load|x)\s*[:=]\s*""?(-?\d+(?:\.\d+)?)(?:[oO]hm|Ω)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FreqRegex = new(
        @"(?:freq|f|frequency)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[gG][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RfSmithMatchingModel ParseSmith(string blockText, string defaultTitle = "RF Smith Chart Impedance Matching")
    {
        var model = new RfSmithMatchingModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = SmithFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var z0m = Z0Regex.Match(header);
            if (z0m.Success && double.TryParse(z0m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double z0))
                model.CharacteristicZ0 = Math.Clamp(z0, 1.0, 1000.0);

            var lm = LoadRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rl))
            {
                model.LoadResistanceRl = Math.Clamp(rl, 0.0, 10000.0);
                string jPart = lm.Groups[2].Value.Replace(" ", "").Replace("j", "");
                if (double.TryParse(jPart, NumberStyles.Any, CultureInfo.InvariantCulture, out double xl))
                    model.LoadReactanceXl = Math.Clamp(xl, -10000.0, 10000.0);
            }

            var rlm = RlRegex.Match(header);
            if (rlm.Success && double.TryParse(rlm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rlv))
                model.LoadResistanceRl = Math.Clamp(rlv, 0.0, 10000.0);

            var xlm = XlRegex.Match(header);
            if (xlm.Success && double.TryParse(xlm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double xlv))
                model.LoadReactanceXl = Math.Clamp(xlv, -10000.0, 10000.0);

            var fm = FreqRegex.Match(header);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double f))
                model.FrequencyGhz = Math.Clamp(f, 0.001, 100.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var z0m = Z0Regex.Match(l);
            if (z0m.Success && double.TryParse(z0m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double z0))
                model.CharacteristicZ0 = Math.Clamp(z0, 1.0, 1000.0);

            var rlm = RlRegex.Match(l);
            if (rlm.Success && double.TryParse(rlm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rlv))
                model.LoadResistanceRl = Math.Clamp(rlv, 0.0, 10000.0);

            var xlm = XlRegex.Match(l);
            if (xlm.Success && double.TryParse(xlm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double xlv))
                model.LoadReactanceXl = Math.Clamp(xlv, -10000.0, 10000.0);

            var fm = FreqRegex.Match(l);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double f))
                model.FrequencyGhz = Math.Clamp(f, 0.001, 100.0);
        }

        return model;
    }

    public static string RenderSmithSvg(RfSmithMatchingModel model)
    {
        double width = 500;
        double height = 280;
        double cx = 150;
        double cy = 150;
        double rSmith = 85;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-smith-svg\">");
        sb.AppendLine("""
            <style>
              .sm-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .sm-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .sm-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .sm-outer { fill: #0f172a; stroke: #64748b; stroke-width: 1.8; }
              .sm-circle { fill: none; stroke: #334155; stroke-width: 1; }
              .sm-axis { stroke: #64748b; stroke-width: 1.2; }
              .sm-load-pt { fill: #f43f5e; stroke: #ffffff; stroke-width: 1.5; }
              .sm-vswr-circle { fill: none; stroke: #fbbf24; stroke-width: 1.2; stroke-dasharray: 3 3; }
              .sm-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .sm-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .sm-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"sm-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"sm-title\">🎯 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"sm-meta\">Z0 = {model.CharacteristicZ0:F0}Ω • ZL = {model.LoadResistanceRl:F0} + j{model.LoadReactanceXl:F0}Ω • |Γ| = {model.GammaMag:F2} (VSWR = {model.Vswr:F2})</text>");

        // Outer Unit Circle |Gamma| = 1
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{rSmith}\" class=\"sm-outer\" />");

        // Horizontal Real Axis
        sb.AppendLine($"  <line x1=\"{cx - rSmith}\" y1=\"{cy}\" x2=\"{cx + rSmith}\" y2=\"{cy}\" class=\"sm-axis\" />");

        // Constant Resistance Circles: r = 0.5, 1.0, 2.0
        double[] rVals = { 0.5, 1.0, 2.0 };
        foreach (double r in rVals)
        {
            double cShift = (r / (r + 1.0)) * rSmith;
            double cr = (1.0 / (r + 1.0)) * rSmith;
            sb.AppendLine($"  <circle cx=\"{cx + cShift:F1}\" cy=\"{cy}\" r=\"{cr:F1}\" class=\"sm-circle\" />");
        }

        // VSWR Circle with radius |Gamma| * rSmith
        double vswrRadius = Math.Clamp(model.GammaMag * rSmith, 2.0, rSmith);
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{vswrRadius:F1}\" class=\"sm-vswr-circle\" />");

        // Load Point Marker (GammaReal, -GammaImag)
        double pLoadX = cx + model.GammaReal * rSmith;
        double pLoadY = cy - model.GammaImag * rSmith; // SVG y inverted

        sb.AppendLine($"  <circle cx=\"{pLoadX:F1}\" cy=\"{pLoadY:F1}\" r=\"4.5\" class=\"sm-load-pt\" />");
        sb.AppendLine($"  <text x=\"{pLoadX + 6:F1}\" y=\"{pLoadY - 4:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#f43f5e\">ZL</text>");

        // Results Card on Right
        double cardX = 300;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"180\" height=\"195\" rx=\"6\" class=\"sm-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"bk-lbl\">Reflection Coeff (|Γ|):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"bk-val\" font-size=\"14\" fill=\"#fbbf24\">|Γ| = {model.GammaMag:F3} ∠{model.GammaPhaseDeg:F1}°</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"bk-lbl\">VSWR Voltage Ratio:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"bk-val\" fill=\"#f43f5e\">{model.Vswr:F2} : 1</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"bk-lbl\">Return Loss (RL):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"bk-val\" fill=\"#10b981\">{model.ReturnLossDb:F2} dB</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"bk-lbl\">Normalized Impedance (z):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"bk-val\">z = {model.NormalizedR:F2} + j{model.NormalizedX:F2}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Complex Reflection Plane</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
