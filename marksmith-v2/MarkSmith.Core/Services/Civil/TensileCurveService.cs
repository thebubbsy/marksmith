using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class TensileCurveModel
{
    public string Title { get; set; } = "Engineering Stress-Strain Curve";
    public string MaterialName { get; set; } = "Structural Steel A36";
    public double ModulusGpa { get; set; } = 200.0;    // E (GPa)
    public double YieldMpa { get; set; } = 250.0;      // sigma_y (MPa)
    public double UtsMpa { get; set; } = 400.0;        // sigma_uts (MPa)
    public double FractureStrain { get; set; } = 0.25; // epsilon_f (mm/mm)

    public double YieldStrain => (YieldMpa / (ModulusGpa * 1000.0)); // Hookean limit
    public double Offset02Strain => YieldStrain + 0.002;             // 0.2% proof strain
    public double UniformStrainUts => FractureStrain * 0.65;         // Peak necking strain

    // Modulus of Resilience Ur = (sigma_y^2) / (2 * E) in kJ/m3
    public double ResilienceModulusKjM3 => (Math.Pow(YieldMpa, 2)) / (2.0 * ModulusGpa);

    // Modulus of Toughness Ut approx ((sigma_y + 2*sigma_uts) / 3) * epsilon_f in MJ/m3
    public double ToughnessModulusMjM3 => ((YieldMpa + 2.0 * UtsMpa) / 3.0) * FractureStrain;
}

public static class TensileCurveService
{
    private static readonly Regex TensileFenceRegex = new(
        @":::(?:tensile-test|stress-strain|tensile-curve)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ModulusRegex = new(
        @"(?:e|modulus|youngs)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[gG][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex YieldRegex = new(
        @"(?:yield|sigma_y|sy)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UtsRegex = new(
        @"(?:uts|ultimate|sigma_uts)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FracRegex = new(
        @"(?:frac|fracture|elongation|ef)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:%)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static TensileCurveModel ParseTensile(string blockText, string defaultTitle = "Engineering Stress-Strain Curve")
    {
        var model = new TensileCurveModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = TensileFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var em = ModulusRegex.Match(header);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e))
                model.ModulusGpa = Math.Clamp(e, 1.0, 1000.0);

            var ym = YieldRegex.Match(header);
            if (ym.Success && double.TryParse(ym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double y))
                model.YieldMpa = Math.Clamp(y, 10.0, 3000.0);

            var um = UtsRegex.Match(header);
            if (um.Success && double.TryParse(um.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double uts))
                model.UtsMpa = Math.Clamp(uts, model.YieldMpa * 1.05, 4000.0);

            var fm = FracRegex.Match(header);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double frac))
            {
                if (frac > 1.0) frac /= 100.0;
                model.FractureStrain = Math.Clamp(frac, 0.01, 1.0);
            }

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var em = ModulusRegex.Match(l);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e))
                model.ModulusGpa = Math.Clamp(e, 1.0, 1000.0);

            var ym = YieldRegex.Match(l);
            if (ym.Success && double.TryParse(ym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double y))
                model.YieldMpa = Math.Clamp(y, 10.0, 3000.0);

            var um = UtsRegex.Match(l);
            if (um.Success && double.TryParse(um.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double uts))
                model.UtsMpa = Math.Clamp(uts, model.YieldMpa * 1.05, 4000.0);

            var fm = FracRegex.Match(l);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double frac))
            {
                if (frac > 1.0) frac /= 100.0;
                model.FractureStrain = Math.Clamp(frac, 0.01, 1.0);
            }
        }

        return model;
    }

    public static string RenderTensileSvg(TensileCurveModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 55;
        double oy = 230;
        double axisW = 230;
        double axisH = 160;

        double maxStrain = model.FractureStrain * 1.15;
        double maxStress = model.UtsMpa * 1.20;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-tensile-svg\">");
        sb.AppendLine("""
            <style>
              .ts-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .ts-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .ts-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .ts-axis { stroke: #475569; stroke-width: 1.2; }
              .ts-grid { stroke: #1e293b; stroke-width: 1; stroke-dasharray: 2 2; }
              .ts-tough-fill { fill: #38bdf8; fill-opacity: 0.12; }
              .ts-curve { fill: none; stroke: #38bdf8; stroke-width: 2.2; }
              .ts-offset-line { stroke: #f43f5e; stroke-width: 1.2; stroke-dasharray: 4 2; }
              .ts-pt { fill: #fbbf24; stroke: #ffffff; stroke-width: 1.5; }
              .ts-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .ts-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .ts-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"ts-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"ts-title\">🏗 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"ts-meta\">E = {model.ModulusGpa:F0} GPa • Yield = {model.YieldMpa:F0} MPa • UTS = {model.UtsMpa:F0} MPa</text>");

        // Coordinate Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + axisW + 15}\" y2=\"{oy}\" class=\"ts-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - axisH - 10}\" class=\"ts-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + axisW + 10}\" y=\"{oy + 14}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">ε</text>");
        sb.AppendLine($"  <text x=\"{ox - 10}\" y=\"{oy - axisH - 4}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\" text-anchor=\"end\">σ (MPa)</text>");

        // Key Points:
        // 1. Origin (0, 0)
        // 2. Yield Point (ey, YieldMpa)
        // 3. UTS Peak (e_uts, UtsMpa)
        // 4. Fracture Point (ef, FractureMpa)
        double ey = Math.Max(0.005, model.YieldStrain);
        double eUts = model.UniformStrainUts;
        double ef = model.FractureStrain;
        double fractureStress = model.UtsMpa * 0.85;

        double pYieldX = ox + (ey / maxStrain) * axisW;
        double pYieldY = oy - (model.YieldMpa / maxStress) * axisH;

        double pUtsX = ox + (eUts / maxStrain) * axisW;
        double pUtsY = oy - (model.UtsMpa / maxStress) * axisH;

        double pFracX = ox + (ef / maxStrain) * axisW;
        double pFracY = oy - (fractureStress / maxStress) * axisH;

        // Stress-Strain Curve and Toughness Shaded Polygon
        string curvePath = $"M {ox} {oy} L {pYieldX:F1} {pYieldY:F1} Q {(pYieldX + pUtsX) / 2} {pUtsY:F1} {pUtsX:F1} {pUtsY:F1} Q {(pUtsX + pFracX) / 2} {pUtsY:F1} {pFracX:F1} {pFracY:F1}";
        string fillPoly = $"{curvePath} L {pFracX:F1} {oy} Z";

        sb.AppendLine($"  <path d=\"{fillPoly}\" class=\"ts-tough-fill\" />");
        sb.AppendLine($"  <path d=\"{curvePath}\" class=\"ts-curve\" />");

        // 0.2% Offset Yield Line
        double offsetStartStrain = 0.002;
        double pOffStartX = ox + (offsetStartStrain / maxStrain) * axisW;
        double pOffEndX = ox + ((ey + 0.002) / maxStrain) * axisW;
        sb.AppendLine($"  <line x1=\"{pOffStartX:F1}\" y1=\"{oy}\" x2=\"{pOffEndX:F1}\" y2=\"{pYieldY:F1}\" class=\"ts-offset-line\" />");

        // Key Points Markers
        sb.AppendLine($"  <circle cx=\"{pYieldX:F1}\" cy=\"{pYieldY:F1}\" r=\"4\" class=\"ts-pt\" />");
        sb.AppendLine($"  <circle cx=\"{pUtsX:F1}\" cy=\"{pUtsY:F1}\" r=\"4\" class=\"ts-pt\" />");
        sb.AppendLine($"  <circle cx=\"{pFracX:F1}\" cy=\"{pFracY:F1}\" r=\"4\" fill=\"#f43f5e\" stroke=\"#ffffff\" stroke-width=\"1.5\" />");

        sb.AppendLine($"  <text x=\"{pYieldX - 4:F1}\" y=\"{pYieldY - 6:F1}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">σy</text>");
        sb.AppendLine($"  <text x=\"{pUtsX - 10:F1}\" y=\"{pUtsY - 6:F1}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">UTS</text>");
        sb.AppendLine($"  <text x=\"{pFracX + 4:F1}\" y=\"{pFracY + 3:F1}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f43f5e\">Fracture</text>");

        // Results Card on Right
        double cardX = 305;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"175\" height=\"195\" rx=\"6\" class=\"ts-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"ts-lbl\">Yield Strength (σy):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"ts-val\">{model.YieldMpa:F0} MPa (0.2% offset)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"ts-lbl\">Ultimate Strength (UTS):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"ts-val\" fill=\"#10b981\">{model.UtsMpa:F0} MPa</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"ts-lbl\">Fracture Elongation (εf):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"ts-val\">{model.FractureStrain * 100.0:F1} %</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"ts-lbl\">Modulus of Toughness (Ut):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"ts-val\" fill=\"#fbbf24\">{model.ToughnessModulusMjM3:F1} MJ/m³</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Ductile Engineering Metal</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
