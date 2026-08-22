using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Physics;

public class CarnotCycleModel
{
    public string Title { get; set; } = "Carnot Thermodynamic Heat Engine";
    public double TempHotKelvin { get; set; } = 600.0;  // Th (K)
    public double TempColdKelvin { get; set; } = 300.0; // Tc (K)
    public double CompressionRatio { get; set; } = 4.0; // r_v = V1 / V2
    public double GammaRatio { get; set; } = 1.4;       // Diatomic ideal gas

    // Carnot Efficiency eta = 1 - (Tc / Th)
    public double Efficiency => 1.0 - (TempColdKelvin / TempHotKelvin);
    public double EfficiencyPercent => Efficiency * 100.0;
}

public static class CarnotCycleService
{
    private static readonly Regex CarnotFenceRegex = new(
        @":::(?:carnot|carnot-cycle|heat-engine)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ThRegex = new(
        @"(?:th|hot|t_hot)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TcRegex = new(
        @"(?:tc|cold|t_cold)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CrRegex = new(
        @"(?:cr|ratio|compression)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static CarnotCycleModel ParseCarnot(string blockText, string defaultTitle = "Carnot Thermodynamic Heat Engine")
    {
        var model = new CarnotCycleModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = CarnotFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var thm = ThRegex.Match(header);
            if (thm.Success && double.TryParse(thm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double th))
                model.TempHotKelvin = Math.Clamp(th, 200.0, 3000.0);

            var tcm = TcRegex.Match(header);
            if (tcm.Success && double.TryParse(tcm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double tc))
                model.TempColdKelvin = Math.Clamp(tc, 50.0, model.TempHotKelvin * 0.95);

            var crm = CrRegex.Match(header);
            if (crm.Success && double.TryParse(crm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cr))
                model.CompressionRatio = Math.Clamp(cr, 1.5, 20.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var thm = ThRegex.Match(l);
            if (thm.Success && double.TryParse(thm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double th))
                model.TempHotKelvin = Math.Clamp(th, 200.0, 3000.0);

            var tcm = TcRegex.Match(l);
            if (tcm.Success && double.TryParse(tcm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double tc))
                model.TempColdKelvin = Math.Clamp(tc, 50.0, model.TempHotKelvin * 0.95);

            var crm = CrRegex.Match(l);
            if (crm.Success && double.TryParse(crm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cr))
                model.CompressionRatio = Math.Clamp(cr, 1.5, 20.0);
        }

        return model;
    }

    public static string RenderCarnotSvg(CarnotCycleModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double oy = 230;
        double pvW = 230;
        double pvH = 150;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-carnot-svg\">");
        sb.AppendLine("""
            <style>
              .cn-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .cn-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .cn-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .cn-axis { stroke: #475569; stroke-width: 1.2; }
              .cn-work { fill: #38bdf8; fill-opacity: 0.15; stroke: #38bdf8; stroke-width: 2; }
              .cn-pt { fill: #fbbf24; stroke: #ffffff; stroke-width: 1.5; }
              .cn-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .cn-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .cn-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"cn-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"cn-title\">🔥 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"cn-meta\">Hot Reservoir TH = {model.TempHotKelvin:F0} K • Cold TC = {model.TempColdKelvin:F0} K</text>");

        // P-V Diagram Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + pvW + 15}\" y2=\"{oy}\" class=\"cn-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - pvH - 10}\" class=\"cn-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + pvW + 10}\" y=\"{oy + 14}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">V</text>");
        sb.AppendLine($"  <text x=\"{ox - 10}\" y=\"{oy - pvH - 4}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\" text-anchor=\"end\">P</text>");

        // 4 Thermodynamic Points on PV diagram
        double p1x = ox + 30, p1y = oy - 140; // State 1: High P, Low V, TH
        double p2x = ox + 100, p2y = oy - 95;  // State 2: Isothermal expansion at TH
        double p3x = ox + 210, p3y = oy - 25;  // State 3: Adiabatic expansion to TC
        double p4x = ox + 110, p4y = oy - 45;  // State 4: Isothermal compression at TC

        // Enclosed Work Loop (1 -> 2 -> 3 -> 4 -> 1)
        string cyclePath = $"M {p1x} {p1y} Q {(p1x + p2x) / 2 + 5} {(p1y + p2y) / 2 + 10} {p2x} {p2y} Q {(p2x + p3x) / 2 + 10} {(p2y + p3y) / 2 + 15} {p3x} {p3y} Q {(p3x + p4x) / 2 - 5} {(p3y + p4y) / 2 - 8} {p4x} {p4y} Q {(p4x + p1x) / 2 - 10} {(p4y + p1y) / 2 - 15} {p1x} {p1y} Z";
        sb.AppendLine($"  <path d=\"{cyclePath}\" class=\"cn-work\" />");

        // State Marker Nodes
        sb.AppendLine($"  <circle cx=\"{p1x}\" cy=\"{p1y}\" r=\"4\" class=\"cn-pt\" />");
        sb.AppendLine($"  <text x=\"{p1x - 12}\" y=\"{p1y - 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">1</text>");
        sb.AppendLine($"  <circle cx=\"{p2x}\" cy=\"{p2y}\" r=\"4\" class=\"cn-pt\" />");
        sb.AppendLine($"  <text x=\"{p2x + 6}\" y=\"{p2y - 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">2</text>");
        sb.AppendLine($"  <circle cx=\"{p3x}\" cy=\"{p3y}\" r=\"4\" class=\"cn-pt\" />");
        sb.AppendLine($"  <text x=\"{p3x + 6}\" y=\"{p3y + 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">3</text>");
        sb.AppendLine($"  <circle cx=\"{p4x}\" cy=\"{p4y}\" r=\"4\" class=\"cn-pt\" />");
        sb.AppendLine($"  <text x=\"{p4x - 12}\" y=\"{p4y + 8}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">4</text>");

        // Net Work W Label in center
        sb.AppendLine($"  <text x=\"{(p1x + p3x) / 2}\" y=\"{(p1y + p3y) / 2 + 5}\" font-family=\"Segoe UI, sans-serif\" font-size=\"10\" font-weight=\"700\" fill=\"#38bdf8\" text-anchor=\"middle\">W_net</text>");

        // Results Card on Right
        double cardX = 305;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"175\" height=\"195\" rx=\"6\" class=\"cn-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 22}\" class=\"cn-lbl\">Carnot Efficiency (η):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 42}\" class=\"cn-val\" font-size=\"18\" fill=\"#10b981\">{model.EfficiencyPercent:F1} %</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 70}\" class=\"cn-lbl\">1→2: Isothermal Expansion (TH)</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 88}\" class=\"cn-lbl\">2→3: Adiabatic Expansion</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 106}\" class=\"cn-lbl\">3→4: Isothermal Compression (TC)</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 124}\" class=\"cn-lbl\">4→1: Adiabatic Compression</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 155}\" class=\"cn-lbl\">Theoretical Max Limit:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 172}\" font-family=\"monospace\" font-size=\"10\" fill=\"#fbbf24\">η = 1 - (TC / TH)</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
