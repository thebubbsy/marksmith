using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class EulerBucklingModel
{
    public string Title { get; set; } = "Column Euler Elastic Buckling";
    public double LengthM { get; set; } = 4.5;           // L (m)
    public double YoungsModulusGpa { get; set; } = 200.0;// E (GPa) (Steel)
    public double MomentInertiaCm4 { get; set; } = 4500.0;// I (cm^4)
    public double AreaCm2 { get; set; } = 80.0;          // A (cm^2)
    public double YieldStressMpa { get; set; } = 355.0;  // sigma_y (MPa)
    public string EndCondition { get; set; } = "fixed-pinned"; // pinned-pinned, fixed-free, fixed-fixed, fixed-pinned

    // Effective length factor K
    public double EffectiveLengthFactorK
    {
        get
        {
            string ec = EndCondition.ToLowerInvariant();
            if (ec.Contains("fixed-free") || ec.Contains("cantilever")) return 2.0;
            if (ec.Contains("fixed-fixed")) return 0.5;
            if (ec.Contains("fixed-pinned") || ec.Contains("pinned-fixed")) return 0.7;
            return 1.0; // Pinned-Pinned default
        }
    }

    // Effective Length Le = K * L (m)
    public double EffectiveLengthM => EffectiveLengthFactorK * LengthM;

    // Radius of Gyration r = sqrt(I / A) (cm)
    public double RadiusGyrationCm => Math.Sqrt(MomentInertiaCm4 / Math.Max(0.1, AreaCm2));

    // Slenderness Ratio lambda = Le / r
    public double SlendernessRatio => (EffectiveLengthM * 100.0) / Math.Max(0.01, RadiusGyrationCm);

    // Critical Buckling Load P_cr = (pi^2 * E * I) / (Le^2) in kN
    // E in GPa = 1e9 N/m2, I in cm4 = 1e-8 m4
    public double CriticalBucklingLoadKn
    {
        get
        {
            double eN = YoungsModulusGpa * 1e9;
            double iM4 = MomentInertiaCm4 * 1e-8;
            double pN = (Math.PI * Math.PI * eN * iM4) / Math.Pow(EffectiveLengthM, 2);
            return pN / 1000.0;
        }
    }

    // Critical Buckling Stress sigma_cr = P_cr / A in MPa
    public double CriticalStressMpa => (CriticalBucklingLoadKn * 1000.0) / (AreaCm2 * 100.0);
}

public static class EulerBucklingService
{
    private static readonly Regex BucklingFenceRegex = new(
        @":::(?:buckling|euler-buckling|column-buckling)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LengthRegex = new(
        @"(?:length|\bl\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ERegex = new(
        @"(?:e_gpa|\be\b|modulus)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[gG][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IRegex = new(
        @"(?:i_cm4|\bi\b|inertia)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:cm4)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AreaRegex = new(
        @"(?:area_cm2|\ba\b|area)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:cm2)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex YieldRegex = new(
        @"(?:sigma_y|yield|fy)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EndsRegex = new(
        @"(?:ends|supports|end_condition)\s*[:=]\s*""?([a-zA-Z0-9_\-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static EulerBucklingModel ParseBuckling(string blockText, string defaultTitle = "Column Euler Elastic Buckling")
    {
        var model = new EulerBucklingModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = BucklingFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var lm = LengthRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double l))
                model.LengthM = Math.Clamp(l, 0.5, 100.0);

            var em = ERegex.Match(header);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e))
                model.YoungsModulusGpa = Math.Clamp(e, 10.0, 1000.0);

            var im = IRegex.Match(header);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double i))
                model.MomentInertiaCm4 = Math.Clamp(i, 1.0, 1000000.0);

            var am = AreaRegex.Match(header);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                model.AreaCm2 = Math.Clamp(a, 1.0, 100000.0);

            var ym = YieldRegex.Match(header);
            if (ym.Success && double.TryParse(ym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fy))
                model.YieldStressMpa = Math.Clamp(fy, 50.0, 2000.0);

            var endm = EndsRegex.Match(header);
            if (endm.Success) model.EndCondition = endm.Groups[1].Value.ToLowerInvariant();

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var lm = LengthRegex.Match(l);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double len))
                model.LengthM = Math.Clamp(len, 0.5, 100.0);

            var em = ERegex.Match(l);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e))
                model.YoungsModulusGpa = Math.Clamp(e, 10.0, 1000.0);

            var im = IRegex.Match(l);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double i))
                model.MomentInertiaCm4 = Math.Clamp(i, 1.0, 1000000.0);

            var am = AreaRegex.Match(l);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                model.AreaCm2 = Math.Clamp(a, 1.0, 100000.0);

            var ym = YieldRegex.Match(l);
            if (ym.Success && double.TryParse(ym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fy))
                model.YieldStressMpa = Math.Clamp(fy, 50.0, 2000.0);

            var endm = EndsRegex.Match(l);
            if (endm.Success) model.EndCondition = endm.Groups[1].Value.ToLowerInvariant();
        }

        return model;
    }

    public static string RenderBucklingSvg(EulerBucklingModel model)
    {
        double width = 500;
        double height = 280;
        double cx = 150;
        double topY = 70;
        double botY = 230;
        double colH = botY - topY;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-buckling-svg\">");
        sb.AppendLine("""
            <style>
              .bk-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .bk-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .bk-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .bk-col-axis { stroke: #475569; stroke-width: 1.2; stroke-dasharray: 4 2; }
              .bk-buckled { fill: none; stroke: #f43f5e; stroke-width: 2.5; }
              .bk-support { fill: #334155; stroke: #94a3b8; stroke-width: 1.5; }
              .bk-force { stroke: #fbbf24; stroke-width: 2.2; }
              .bk-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .bk-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .bk-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"bk-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"bk-title\">🏛 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"bk-meta\">L = {model.LengthM:F1}m • K = {model.EffectiveLengthFactorK:F1} ({model.EndCondition}) • Pcr = {model.CriticalBucklingLoadKn:F0} kN</text>");

        // Straight Initial Column Axis
        sb.AppendLine($"  <line x1=\"{cx}\" y1=\"{topY}\" x2=\"{cx}\" y2=\"{botY}\" class=\"bk-col-axis\" />");

        // Sinusoidal Buckled Shape (Lateral Deflection)
        var bucklePath = new StringBuilder();
        int steps = 40;
        double maxDeflectionPix = 32.0;

        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            double py = topY + t * colH;
            double px = cx + maxDeflectionPix * Math.Sin(Math.PI * t);

            if (i == 0) bucklePath.Append($"M {px:F1} {py:F1}");
            else bucklePath.Append($" L {px:F1} {py:F1}");
        }

        sb.AppendLine($"  <path d=\"{bucklePath}\" class=\"bk-buckled\" />");

        // Top Support / Applied Axial Force Arrow P_cr
        sb.AppendLine($"  <line x1=\"{cx}\" y1=\"{topY - 20}\" x2=\"{cx}\" y2=\"{topY}\" class=\"bk-force\" />");
        sb.AppendLine($"  <polygon points=\"{cx - 4},{topY - 6} {cx + 4},{topY - 6} {cx},{topY}\" fill=\"#fbbf24\" />");
        sb.AppendLine($"  <text x=\"{cx + 8}\" y=\"{topY - 10}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#fbbf24\">P_cr</text>");

        // Bottom Support Constraint Box / Pin
        sb.AppendLine($"  <rect x=\"{cx - 16}\" y=\"{botY}\" width=\"32\" height=\"8\" class=\"bk-support\" />");
        sb.AppendLine($"  <line x1=\"{cx - 20}\" y1=\"{botY + 8}\" x2=\"{cx + 20}\" y2=\"{botY + 8}\" stroke=\"#94a3b8\" stroke-width=\"1.5\" />");

        // Results Card on Right
        double cardX = 300;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"180\" height=\"195\" rx=\"6\" class=\"bk-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"bk-lbl\">Critical Buckling Load (Pcr):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"bk-val\" font-size=\"14\" fill=\"#f43f5e\">{model.CriticalBucklingLoadKn:F0} kN</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"bk-lbl\">Critical Stress (σ_cr):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"bk-val\" fill=\"#10b981\">{model.CriticalStressMpa:F1} MPa</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"bk-lbl\">Slenderness Ratio (λ = Le/r):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"bk-val\">λ = {model.SlendernessRatio:F1}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"bk-lbl\">Radius of Gyration (r):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"bk-val\" fill=\"#fbbf24\">r = {model.RadiusGyrationCm:F2} cm</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Euler Elastic Stability</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
