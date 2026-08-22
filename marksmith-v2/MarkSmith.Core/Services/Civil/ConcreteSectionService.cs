using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class ConcreteSectionModel
{
    public string Title { get; set; } = "Reinforced Concrete Ultimate Moment & Whitney Block";
    public double WidthMm { get; set; } = 300.0;       // Beam width b (mm)
    public double DepthMm { get; set; } = 600.0;       // Total depth h (mm)
    public double EffectiveDepthMm { get; set; } = 540.0; // Effective depth d (mm)
    public double ConcreteFcMpa { get; set; } = 32.0;  // f'c (MPa)
    public double SteelFyMpa { get; set; } = 500.0;    // fy (MPa)
    public double SteelAreaMm2 { get; set; } = 1800.0; // As (mm2)

    // Whitney stress block factor beta1
    public double Beta1 => Math.Clamp(0.85 - 0.05 * Math.Max(0.0, (ConcreteFcMpa - 28.0) / 7.0), 0.65, 0.85);

    // Whitney Equivalent Stress Block Depth a (mm) = (As * fy) / (0.85 * f'c * b)
    public double BlockDepthA => (SteelAreaMm2 * SteelFyMpa) / Math.Max(1.0, 0.85 * ConcreteFcMpa * WidthMm);

    // Neutral Axis Depth c (mm) = a / beta1
    public double NeutralAxisC => BlockDepthA / Math.Max(1e-4, Beta1);

    // Tensile Steel Strain eps_s = 0.003 * (d - c) / c
    public double SteelStrain => 0.003 * (EffectiveDepthMm - NeutralAxisC) / Math.Max(1e-4, NeutralAxisC);

    // Strength Reduction Factor phi
    public double PhiFactor => SteelStrain >= 0.005 ? 0.90 : Math.Clamp(0.65 + (SteelStrain - 0.002) * (250.0 / 3.0), 0.65, 0.90);

    // Nominal Moment Capacity Mn (kNm) = As * fy * (d - a / 2) * 1e-6
    public double NominalMomentKnM => SteelAreaMm2 * SteelFyMpa * (EffectiveDepthMm - BlockDepthA / 2.0) * 1e-6;

    // Design Ultimate Moment Capacity phi * Mn (kNm)
    public double UltimateMomentKnM => PhiFactor * NominalMomentKnM;

    // Reinforcement ratio rho = As / (b * d)
    public double ReinforcementRatio => SteelAreaMm2 / Math.Max(1.0, WidthMm * EffectiveDepthMm);
}

public static class ConcreteSectionService
{
    private static readonly Regex SectionFenceRegex = new(
        @":::(?:concrete-section|rc-beam|whitney-block)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WidthRegex = new(
        @"(?:\bwidth\b|\bb\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DepthRegex = new(
        @"(?:\bdepth\b|\bh\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DeffRegex = new(
        @"(?:\bd_eff\b|\bd\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FcRegex = new(
        @"(?:\bfc\b|\bf_c\b|\bconcrete\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][pP][aA])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FyRegex = new(
        @"(?:\bfy\b|\bf_y\b|\bsteel\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][pP][aA])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RebarRegex = new(
        @"(?:\brebar_area\b|\bas\b|\barea_s\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm2)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ConcreteSectionModel ParseConcreteSection(string blockText, string defaultTitle = "Reinforced Concrete Ultimate Moment & Whitney Block")
    {
        var model = new ConcreteSectionModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = SectionFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var wm = WidthRegex.Match(header);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double w))
                model.WidthMm = Math.Clamp(w, 50.0, 5000.0);

            var dm = DepthRegex.Match(header);
            if (dm.Success && double.TryParse(dm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                model.DepthMm = Math.Clamp(d, 50.0, 10000.0);

            var dem = DeffRegex.Match(header);
            if (dem.Success && double.TryParse(dem.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double de))
                model.EffectiveDepthMm = Math.Clamp(de, 40.0, model.DepthMm);

            var fcm = FcRegex.Match(header);
            if (fcm.Success && double.TryParse(fcm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fc))
                model.ConcreteFcMpa = Math.Clamp(fc, 10.0, 150.0);

            var fym = FyRegex.Match(header);
            if (fym.Success && double.TryParse(fym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fy))
                model.SteelFyMpa = Math.Clamp(fy, 100.0, 1500.0);

            var asm = RebarRegex.Match(header);
            if (asm.Success && double.TryParse(asm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double asv))
                model.SteelAreaMm2 = Math.Clamp(asv, 10.0, 50000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var wm = WidthRegex.Match(l);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double w))
                model.WidthMm = Math.Clamp(w, 50.0, 5000.0);

            var dm = DepthRegex.Match(l);
            if (dm.Success && double.TryParse(dm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                model.DepthMm = Math.Clamp(d, 50.0, 10000.0);

            var dem = DeffRegex.Match(l);
            if (dem.Success && double.TryParse(dem.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double de))
                model.EffectiveDepthMm = Math.Clamp(de, 40.0, model.DepthMm);

            var fcm = FcRegex.Match(l);
            if (fcm.Success && double.TryParse(fcm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fc))
                model.ConcreteFcMpa = Math.Clamp(fc, 10.0, 150.0);

            var fym = FyRegex.Match(l);
            if (fym.Success && double.TryParse(fym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fy))
                model.SteelFyMpa = Math.Clamp(fy, 100.0, 1500.0);

            var asm = RebarRegex.Match(l);
            if (asm.Success && double.TryParse(asm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double asv))
                model.SteelAreaMm2 = Math.Clamp(asv, 10.0, 50000.0);
        }

        return model;
    }

    public static string RenderConcreteSectionSvg(ConcreteSectionModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-concrete-svg\">");
        sb.AppendLine("""
            <style>
              .rc-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .rc-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .rc-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .rc-concrete { fill: #334155; stroke: #64748b; stroke-width: 1.5; fill-opacity: 0.6; }
              .rc-stirrup { fill: none; stroke: #94a3b8; stroke-width: 1.2; }
              .rc-rebar { fill: #38bdf8; stroke: #ffffff; stroke-width: 1; }
              .rc-whitney { fill: #f43f5e; fill-opacity: 0.35; stroke: #f43f5e; stroke-width: 1.5; }
              .rc-axis { stroke: #64748b; stroke-width: 1; stroke-dasharray: 3 3; }
              .rc-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .rc-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .rc-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"rc-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"rc-title\">🏛️ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"rc-meta\">{model.WidthMm:F0}x{model.DepthMm:F0}mm (d={model.EffectiveDepthMm:F0}mm) • f'c={model.ConcreteFcMpa:F0}MPa • fy={model.SteelFyMpa:F0}MPa • As={model.SteelAreaMm2:F0}mm²</text>");

        // RC Cross Section on Left
        double secX = 40;
        double secY = 70;
        double secW = 75;
        double secH = 150;

        sb.AppendLine($"  <rect x=\"{secX}\" y=\"{secY}\" width=\"{secW}\" height=\"{secH}\" rx=\"4\" class=\"rc-concrete\" />");
        sb.AppendLine($"  <rect x=\"{secX + 6}\" y=\"{secY + 6}\" width=\"{secW - 12}\" height=\"{secH - 12}\" rx=\"2\" class=\"rc-stirrup\" />");

        // Bottom Rebar Dots (4 bars)
        double rebarY = secY + secH - 14;
        for (int i = 0; i < 4; i++)
        {
            double rx = secX + 12 + i * ((secW - 24) / 3.0);
            sb.AppendLine($"  <circle cx=\"{rx:F1}\" cy=\"{rebarY}\" r=\"4.5\" class=\"rc-rebar\" />");
        }

        // Stress Block Diagram (Whitney rectangular block) in center
        double wbX = 145;
        double wbY = secY;
        double wbH = secH;
        double scaleA = Math.Clamp((model.BlockDepthA / model.DepthMm) * wbH, 8.0, wbH * 0.9);
        double scaleC = Math.Clamp((model.NeutralAxisC / model.DepthMm) * wbH, scaleA, wbH * 0.95);

        // Neutral axis line
        sb.AppendLine($"  <line x1=\"{wbX - 10}\" y1=\"{wbY + scaleC:F1}\" x2=\"{wbX + 90}\" y2=\"{wbY + scaleC:F1}\" class=\"rc-axis\" />");
        sb.AppendLine($"  <text x=\"{wbX + 95}\" y=\"{wbY + scaleC + 3:F1}\" font-family=\"monospace\" font-size=\"8\" fill=\"#64748b\">N.A. (c={model.NeutralAxisC:F0}mm)</text>");

        // Whitney 0.85*f'c block
        sb.AppendLine($"  <rect x=\"{wbX}\" y=\"{wbY}\" width=\"60\" height=\"{scaleA:F1}\" class=\"rc-whitney\" />");
        sb.AppendLine($"  <text x=\"{wbX + 8}\" y=\"{wbY + scaleA / 2.0 + 3:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#f43f5e\">0.85f'c</text>");
        sb.AppendLine($"  <text x=\"{wbX + 66}\" y=\"{wbY + scaleA / 2.0 + 3:F1}\" font-family=\"monospace\" font-size=\"8\" fill=\"#f43f5e\">a={model.BlockDepthA:F0}mm</text>");

        // Force arrows (Cc compression and Ts tension)
        sb.AppendLine($"  <line x1=\"{wbX + 30}\" y1=\"{wbY + scaleA / 2.0:F1}\" x2=\"{wbX - 15}\" y2=\"{wbY + scaleA / 2.0:F1}\" stroke=\"#f43f5e\" stroke-width=\"2\" />");
        sb.AppendLine($"  <text x=\"{wbX - 35}\" y=\"{wbY + scaleA / 2.0 + 3:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#f43f5e\">Cc</text>");

        sb.AppendLine($"  <line x1=\"{wbX - 15}\" y1=\"{rebarY}\" x2=\"{wbX + 30}\" y2=\"{rebarY}\" stroke=\"#38bdf8\" stroke-width=\"2\" />");
        sb.AppendLine($"  <text x=\"{wbX - 35}\" y=\"{rebarY + 3}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#38bdf8\">Ts</text>");

        // Results Card on Right
        double cardX = 275;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"235\" height=\"205\" rx=\"6\" class=\"rc-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"rc-lbl\">Design Moment Capacity (φMn):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"rc-val\" font-size=\"14\" fill=\"#10b981\">φMn = {model.UltimateMomentKnM:F1} kNm</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"rc-lbl\">Nominal Flexural Strength (Mn):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"rc-val\" fill=\"#fbbf24\">Mn = {model.NominalMomentKnM:F1} kNm (φ = {model.PhiFactor:F2})</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"rc-lbl\">Whitney Block Depth (a) & Neutral Axis (c):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"rc-val\" font-size=\"11\">a = {model.BlockDepthA:F1} mm  |  c = {model.NeutralAxisC:F1} mm (β1={model.Beta1:F2})</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"rc-lbl\">Tensile Steel Strain (ε_s):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"rc-val\" fill=\"#38bdf8\">ε_s = {model.SteelStrain:F4} ({(model.SteelStrain >= 0.005 ? "Tension Controlled" : "Transition")})</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"rc-lbl\">Reinforcement Ratio (ρ):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" class=\"rc-val\">ρ = {model.ReinforcementRatio*100:F2}% (As = {model.SteelAreaMm2:F0} mm²)</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
