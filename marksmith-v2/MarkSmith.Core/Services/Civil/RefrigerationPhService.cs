using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class RefrigerationPhModel
{
    public string Title { get; set; } = "Vapor-Compression Refrigeration Cycle";
    public string Refrigerant { get; set; } = "R134a";
    public double EvaporatorTempC { get; set; } = 4.0;    // Tevap (deg C)
    public double CondenserTempC { get; set; } = 38.0;    // Tcond (deg C)
    public double SuperheatK { get; set; } = 5.0;         // Delta T_sh (K)
    public double SubcoolingK { get; set; } = 4.0;        // Delta T_sc (K)

    // Enthalpies (kJ/kg) - R134a approximations
    // h1: Evaporator outlet / Compressor suction (vapor) approx 400 + 0.8 * Tevap + 0.9 * Superheat
    public double EnthalpyH1 => 400.0 + 0.8 * EvaporatorTempC + 0.9 * SuperheatK;

    // h2: Compressor discharge (superheated vapor) approx h1 + 1.25 * (Tcond - Tevap)
    public double EnthalpyH2 => EnthalpyH1 + 1.25 * (CondenserTempC - EvaporatorTempC) * 0.95;

    // h3: Condenser outlet (liquid) approx 250.0 + 1.35 * (CondenserTempC - 30.0) - 1.4 * Subcooling
    public double EnthalpyH3 => 250.0 + 1.35 * (CondenserTempC - 30.0) - 1.4 * SubcoolingK;

    // h4: Expansion valve outlet (flash liquid/vapor mixture, isenthalpic h4 = h3)
    public double EnthalpyH4 => EnthalpyH3;

    // Pressures (bar) approx
    public double EvaporatorPressureBar => Math.Max(0.5, 3.0 + 0.12 * EvaporatorTempC);
    public double CondenserPressureBar => Math.Max(EvaporatorPressureBar + 1.0, 8.5 + 0.25 * (CondenserTempC - 30.0));

    // Cooling capacity q_e = h1 - h4 (kJ/kg)
    public double CoolingCapacityKjKg => EnthalpyH1 - EnthalpyH4;

    // Compressor work w_c = h2 - h1 (kJ/kg)
    public double CompressorWorkKjKg => EnthalpyH2 - EnthalpyH1;

    // Coefficient of Performance COP = q_e / w_c
    public double Cop => CoolingCapacityKjKg / Math.Max(1.0, CompressorWorkKjKg);

    // Carnot Theoretical COP Limit = (Tevap + 273.15) / (Tcond - Tevap)
    public double CarnotCop => (EvaporatorTempC + 273.15) / Math.Max(1.0, CondenserTempC - EvaporatorTempC);
}

public static class RefrigerationPhService
{
    private static readonly Regex RefFenceRegex = new(
        @":::(?:refrigeration|refrigeration-cycle|ph-diagram)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EvapRegex = new(
        @"(?:evap|tevap|evaporator)\s*[:=]\s*""?(-?\d+(?:\.\d+)?)(?:[cC])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CondRegex = new(
        @"(?:cond|tcond|condenser)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[cC])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShRegex = new(
        @"(?:superheat|sh)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ScRegex = new(
        @"(?:subcool|sc|subcooling)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FluidRegex = new(
        @"(?:refrigerant|fluid|gas)\s*[:=]\s*""?([a-zA-Z0-9_\-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RefrigerationPhModel ParseRefrigeration(string blockText, string defaultTitle = "Vapor-Compression Refrigeration Cycle")
    {
        var model = new RefrigerationPhModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = RefFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var em = EvapRegex.Match(header);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double evap))
                model.EvaporatorTempC = Math.Clamp(evap, -40.0, 20.0);

            var cm = CondRegex.Match(header);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cond))
                model.CondenserTempC = Math.Clamp(cond, 25.0, 75.0);

            var shm = ShRegex.Match(header);
            if (shm.Success && double.TryParse(shm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sh))
                model.SuperheatK = Math.Clamp(sh, 0.0, 25.0);

            var scm = ScRegex.Match(header);
            if (scm.Success && double.TryParse(scm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sc))
                model.SubcoolingK = Math.Clamp(sc, 0.0, 20.0);

            var fm = FluidRegex.Match(header);
            if (fm.Success) model.Refrigerant = fm.Groups[1].Value;

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var em = EvapRegex.Match(l);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double evap))
                model.EvaporatorTempC = Math.Clamp(evap, -40.0, 20.0);

            var cm = CondRegex.Match(l);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cond))
                model.CondenserTempC = Math.Clamp(cond, 25.0, 75.0);

            var shm = ShRegex.Match(l);
            if (shm.Success && double.TryParse(shm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sh))
                model.SuperheatK = Math.Clamp(sh, 0.0, 25.0);

            var scm = ScRegex.Match(l);
            if (scm.Success && double.TryParse(scm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sc))
                model.SubcoolingK = Math.Clamp(sc, 0.0, 20.0);

            var fm = FluidRegex.Match(l);
            if (fm.Success) model.Refrigerant = fm.Groups[1].Value;
        }

        return model;
    }

    public static string RenderRefrigerationSvg(RefrigerationPhModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double oy = 230;
        double axisW = 235;
        double axisH = 150;

        double minH = 200.0;
        double maxH = 460.0;
        double minP = 1.0;
        double maxP = 25.0;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-ref-svg\">");
        sb.AppendLine("""
            <style>
              .rf-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .rf-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .rf-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .rf-axis { stroke: #475569; stroke-width: 1.2; }
              .rf-dome { fill: #1e293b; fill-opacity: 0.4; stroke: #64748b; stroke-width: 1.5; stroke-dasharray: 4 2; }
              .rf-cycle { fill: #0284c7; fill-opacity: 0.15; stroke: #38bdf8; stroke-width: 2.2; }
              .rf-pt { fill: #fbbf24; stroke: #ffffff; stroke-width: 1.2; }
              .rf-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .rf-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .rf-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"rf-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"rf-title\">❄️ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"rf-meta\">{model.Refrigerant} • Tevap = {model.EvaporatorTempC:F0}°C • Tcond = {model.CondenserTempC:F0}°C • COP = {model.Cop:F2}</text>");

        // Coordinate Axes (Log P vs h)
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + axisW + 15}\" y2=\"{oy}\" class=\"rf-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - axisH - 10}\" class=\"rf-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + axisW + 10}\" y=\"{oy + 14}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">h (kJ/kg)</text>");
        sb.AppendLine($"  <text x=\"{ox - 8}\" y=\"{oy - axisH - 4}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\" text-anchor=\"end\">log P (bar)</text>");

        // Saturation Vapor Bell Dome Path
        // Dome apex at h approx 350 kJ/kg, P = 20 bar
        double apexX = ox + ((350.0 - minH) / (maxH - minH)) * axisW;
        double apexY = oy - (Math.Log10(20.0 / minP) / Math.Log10(maxP / minP)) * axisH;
        double leftDomeX = ox + ((220.0 - minH) / (maxH - minH)) * axisW;
        double rightDomeX = ox + ((415.0 - minH) / (maxH - minH)) * axisW;

        string domePath = $"M {leftDomeX:F1} {oy} Q {leftDomeX + 20} {apexY} {apexX:F1} {apexY:F1} Q {rightDomeX - 10} {apexY} {rightDomeX:F1} {oy}";
        sb.AppendLine($"  <path d=\"{domePath}\" class=\"rf-dome\" />");
        sb.AppendLine($"  <text x=\"{apexX:F1}\" y=\"{apexY - 6:F1}\" font-family=\"monospace\" font-size=\"8\" fill=\"#64748b\" text-anchor=\"middle\">Vapor Dome</text>");

        // Map 4 State Points:
        // Point 1: (h1, P_evap) - Compressor In
        // Point 2: (h2, P_cond) - Compressor Out
        // Point 3: (h3, P_cond) - Condenser Out (Subcooled Liquid)
        // Point 4: (h4, P_evap) - Expansion Out (Flash Liquid)
        double p1Y = oy - (Math.Log10(Math.Max(1.0, model.EvaporatorPressureBar) / minP) / Math.Log10(maxP / minP)) * axisH;
        double p2Y = oy - (Math.Log10(Math.Max(1.0, model.CondenserPressureBar) / minP) / Math.Log10(maxP / minP)) * axisH;

        double p1X = ox + ((model.EnthalpyH1 - minH) / (maxH - minH)) * axisW;
        double p2X = ox + ((model.EnthalpyH2 - minH) / (maxH - minH)) * axisW;
        double p3X = ox + ((model.EnthalpyH3 - minH) / (maxH - minH)) * axisW;
        double p4X = ox + ((model.EnthalpyH4 - minH) / (maxH - minH)) * axisW;

        string cyclePath = $"M {p1X:F1} {p1Y:F1} L {p2X:F1} {p2Y:F1} L {p3X:F1} {p2Y:F1} L {p4X:F1} {p1Y:F1} Z";
        sb.AppendLine($"  <path d=\"{cyclePath}\" class=\"rf-cycle\" />");

        // State Markers
        sb.AppendLine($"  <circle cx=\"{p1X:F1}\" cy=\"{p1Y:F1}\" r=\"3.5\" class=\"rf-pt\" />");
        sb.AppendLine($"  <text x=\"{p1X + 4:F1}\" y=\"{p1Y + 3:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#fbbf24\">1</text>");

        sb.AppendLine($"  <circle cx=\"{p2X:F1}\" cy=\"{p2Y:F1}\" r=\"3.5\" class=\"rf-pt\" />");
        sb.AppendLine($"  <text x=\"{p2X + 4:F1}\" y=\"{p2Y - 2:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#fbbf24\">2</text>");

        sb.AppendLine($"  <circle cx=\"{p3X:F1}\" cy=\"{p2Y:F1}\" r=\"3.5\" class=\"rf-pt\" />");
        sb.AppendLine($"  <text x=\"{p3X - 10:F1}\" y=\"{p2Y - 2:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#fbbf24\">3</text>");

        sb.AppendLine($"  <circle cx=\"{p4X:F1}\" cy=\"{p1Y:F1}\" r=\"3.5\" class=\"rf-pt\" />");
        sb.AppendLine($"  <text x=\"{p4X - 10:F1}\" y=\"{p1Y + 3:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#fbbf24\">4</text>");

        // Results Card on Right
        double cardX = 310;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"170\" height=\"195\" rx=\"6\" class=\"rf-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"rf-lbl\">Coefficient of Performance:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"rf-val\" font-size=\"14\" fill=\"#10b981\">COP = {model.Cop:F2}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"rf-lbl\">Cooling Capacity (qe):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"rf-val\">{model.CoolingCapacityKjKg:F1} kJ/kg</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"rf-lbl\">Compressor Work (wc):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"rf-val\">{model.CompressorWorkKjKg:F1} kJ/kg</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"rf-lbl\">Carnot Limit (COP_carnot):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"rf-val\" fill=\"#fbbf24\">COP_rev = {model.CarnotCop:F2}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Thermodynamic P-h Cycle</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
