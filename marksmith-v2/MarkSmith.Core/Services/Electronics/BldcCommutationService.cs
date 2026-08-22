using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class BldcModel
{
    public string Title { get; set; } = "BLDC 3-Phase Inverter 6-Step Commutation";
    public int PolePairsP { get; set; } = 4;           // P pole pairs (8 poles)
    public double BusVoltageVdc { get; set; } = 24.0;  // Vdc (V)
    public double PhaseCurrentAmps { get; set; } = 8.0;// Iph (A)
    public double AdvanceDeg { get; set; } = 0.0;      // Lead advance angle (deg electrical)

    // 6 Sector States
    // Sector 1: H123 = 101, A+ B- C (float)
    // Sector 2: H123 = 100, A+ C- B (float)
    // Sector 3: H123 = 110, B+ C- A (float)
    // Sector 4: H123 = 010, B+ A- C (float)
    // Sector 5: H123 = 011, C+ A- B (float)
    // Sector 6: H123 = 001, C+ B- A (float)
    public string ActiveSector => "Sector 1 (0°–60°): A+ B- (Phase C Floating)";

    // Mechanical vs Electrical Angle Factor
    public double ElectricalToMechanicalRatio => 1.0 / PolePairsP;
}

public static class BldcCommutationService
{
    private static readonly Regex BldcFenceRegex = new(
        @":::(?:bldc|bldc-commutation|bldc-motor)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PoleRegex = new(
        @"(?:poles|pole_pairs|p)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VdcRegex = new(
        @"(?:vdc|v_bus|bus_voltage)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[vV])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CurrentRegex = new(
        @"(?:current|i_ph|amps)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[aA])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AdvanceRegex = new(
        @"(?:advance|lead_angle|deg)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:deg)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static BldcModel ParseBldc(string blockText, string defaultTitle = "BLDC 3-Phase Inverter 6-Step Commutation")
    {
        var model = new BldcModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = BldcFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var pm = PoleRegex.Match(header);
            if (pm.Success && int.TryParse(pm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                model.PolePairsP = Math.Clamp(p, 1, 32);

            var vm = VdcRegex.Match(header);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double vdc))
                model.BusVoltageVdc = Math.Clamp(vdc, 1.0, 1000.0);

            var im = CurrentRegex.Match(header);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cur))
                model.PhaseCurrentAmps = Math.Clamp(cur, 0.1, 500.0);

            var am = AdvanceRegex.Match(header);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double adv))
                model.AdvanceDeg = Math.Clamp(adv, 0.0, 60.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var pm = PoleRegex.Match(l);
            if (pm.Success && int.TryParse(pm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                model.PolePairsP = Math.Clamp(p, 1, 32);

            var vm = VdcRegex.Match(l);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double vdc))
                model.BusVoltageVdc = Math.Clamp(vdc, 1.0, 1000.0);

            var im = CurrentRegex.Match(l);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cur))
                model.PhaseCurrentAmps = Math.Clamp(cur, 0.1, 500.0);

            var am = AdvanceRegex.Match(l);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double adv))
                model.AdvanceDeg = Math.Clamp(adv, 0.0, 60.0);
        }

        return model;
    }

    public static string RenderBldcSvg(BldcModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double waveW = 240;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-bldc-svg\">");
        sb.AppendLine("""
            <style>
              .bd-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .bd-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .bd-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .bd-axis { stroke: #334155; stroke-width: 1; }
              .bd-hall-h1 { fill: none; stroke: #38bdf8; stroke-width: 1.8; }
              .bd-hall-h2 { fill: none; stroke: #10b981; stroke-width: 1.8; }
              .bd-hall-h3 { fill: none; stroke: #fbbf24; stroke-width: 1.8; }
              .bd-emf-a { fill: none; stroke: #f43f5e; stroke-width: 2; }
              .bd-sector-grid { stroke: #1e293b; stroke-width: 1; stroke-dasharray: 2 2; }
              .bd-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .bd-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .bd-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"bd-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"bd-title\">⚡ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"bd-meta\">Vdc = {model.BusVoltageVdc:F0}V • P = {model.PolePairsP} pole-pairs • I_ph = {model.PhaseCurrentAmps:F1}A (120° Commutation)</text>");

        // 6 Sector Vertical Grid Lines (0 to 360 deg)
        double sectorW = waveW / 6.0;
        for (int s = 0; s <= 6; s++)
        {
            double sx = ox + s * sectorW;
            sb.AppendLine($"  <line x1=\"{sx:F1}\" y1=\"65\" x2=\"{sx:F1}\" y2=\"235\" class=\"bd-sector-grid\" />");
            if (s < 6)
            {
                sb.AppendLine($"  <text x=\"{sx + sectorW / 2:F1}\" y=\"76\" font-family=\"monospace\" font-size=\"7.5\" fill=\"#64748b\" text-anchor=\"middle\">S{s + 1}</text>");
            }
        }

        // Channel 1: Hall H1 (180 deg high, 180 deg low, offset 0)
        double h1Y = 100;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{h1Y}\" x2=\"{ox + waveW}\" y2=\"{h1Y}\" class=\"bd-axis\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{h1Y - 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#38bdf8\" text-anchor=\"end\">H1</text>");
        string h1Path = $"M {ox} {h1Y - 14} L {ox + 3 * sectorW} {h1Y - 14} L {ox + 3 * sectorW} {h1Y} L {ox + 6 * sectorW} {h1Y}";
        sb.AppendLine($"  <path d=\"{h1Path}\" class=\"bd-hall-h1\" />");

        // Channel 2: Hall H2 (shifted 120 deg -> 2 sectors)
        double h2Y = 135;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{h2Y}\" x2=\"{ox + waveW}\" y2=\"{h2Y}\" class=\"bd-axis\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{h2Y - 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#10b981\" text-anchor=\"end\">H2</text>");
        string h2Path = $"M {ox} {h2Y} L {ox + 2 * sectorW} {h2Y} L {ox + 2 * sectorW} {h2Y - 14} L {ox + 5 * sectorW} {h2Y - 14} L {ox + 5 * sectorW} {h2Y} L {ox + 6 * sectorW} {h2Y}";
        sb.AppendLine($"  <path d=\"{h2Path}\" class=\"bd-hall-h2\" />");

        // Channel 3: Hall H3 (shifted 240 deg -> 4 sectors)
        double h3Y = 170;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{h3Y}\" x2=\"{ox + waveW}\" y2=\"{h3Y}\" class=\"bd-axis\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{h3Y - 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\" text-anchor=\"end\">H3</text>");
        string h3Path = $"M {ox} {h3Y - 14} L {ox + sectorW} {h3Y - 14} L {ox + sectorW} {h3Y} L {ox + 4 * sectorW} {h3Y} L {ox + 4 * sectorW} {h3Y - 14} L {ox + 6 * sectorW} {h3Y - 14}";
        sb.AppendLine($"  <path d=\"{h3Path}\" class=\"bd-hall-h3\" />");

        // Channel 4: Phase A Trapezoidal Back-EMF
        double emfAy = 215;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{emfAy}\" x2=\"{ox + waveW}\" y2=\"{emfAy}\" class=\"bd-axis\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{emfAy - 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f43f5e\" text-anchor=\"end\">EMF_A</text>");
        // Trapezoid: Rise (0 to 1), Flat high (1 to 3), Fall (3 to 4), Flat low (4 to 6)
        string emfAPath = $"M {ox} {emfAy} L {ox + sectorW} {emfAy - 16} L {ox + 3 * sectorW} {emfAy - 16} L {ox + 4 * sectorW} {emfAy + 16} L {ox + 6 * sectorW} {emfAy + 16}";
        sb.AppendLine($"  <path d=\"{emfAPath}\" class=\"bd-emf-a\" />");

        // Results Card on Right
        double cardX = 310;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"170\" height=\"195\" rx=\"6\" class=\"bd-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"bd-lbl\">Inverter Conduction:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"bd-val\" font-size=\"13\" fill=\"#10b981\">120° 6-Step Inverter</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"bd-lbl\">Active Bridges (Sector 1):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"bd-val\">A+ (High) / B- (Low)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"bd-lbl\">Floating Phase (Sector 1):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"bd-val\" fill=\"#fbbf24\">Phase C (Z-State)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"bd-lbl\">Electrical / Mech Ratio:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"bd-val\">{model.PolePairsP} : 1 Electrical Freq</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Sensored BLDC Drive</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
