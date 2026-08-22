using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class Timer555Model
{
    public string Title { get; set; } = "NE555 Astable Multivibrator";
    public double ResistorAOhms { get; set; } = 10000.0;  // RA (Ohms) (10k)
    public double ResistorBOhms { get; set; } = 47000.0;  // RB (Ohms) (47k)
    public double CapacitorFarads { get; set; } = 1e-7;   // C (Farads) (100nF)
    public double SupplyVcc { get; set; } = 5.0;          // Vcc (V)

    // High time t_high = ln(2) * (RA + RB) * C
    public double HighTimeSec => Math.Log(2) * (ResistorAOhms + ResistorBOhms) * CapacitorFarads;
    public double HighTimeMs => HighTimeSec * 1000.0;

    // Low time t_low = ln(2) * RB * C
    public double LowTimeSec => Math.Log(2) * ResistorBOhms * CapacitorFarads;
    public double LowTimeMs => LowTimeSec * 1000.0;

    // Period T = t_high + t_low
    public double PeriodSec => HighTimeSec + LowTimeSec;
    public double PeriodMs => PeriodSec * 1000.0;

    // Frequency f = 1 / T = 1.44 / ((RA + 2*RB)*C)
    public double FrequencyHz => 1.0 / Math.Max(1e-12, PeriodSec);

    // Duty Cycle D = t_high / T * 100%
    public double DutyCyclePercent => (HighTimeSec / Math.Max(1e-12, PeriodSec)) * 100.0;
}

public static class Timer555AstableService
{
    private static readonly Regex TimerFenceRegex = new(
        @":::(?:555-timer|astable-555|timer555)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RaRegex = new(
        @"(?:ra|r1|resistor_a)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kKmM]?[oO]hm|Ω)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RbRegex = new(
        @"(?:rb|r2|resistor_b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kKmM]?[oO]hm|Ω)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CapRegex = new(
        @"(?:c|cap|capacitor)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[uUmMnNpP]?[fF])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static Timer555Model ParseTimer(string blockText, string defaultTitle = "NE555 Astable Multivibrator")
    {
        var model = new Timer555Model { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = TimerFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var ram = RaRegex.Match(header);
            if (ram.Success && double.TryParse(ram.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ra))
            {
                if (header.Contains("k") || header.Contains("K")) ra *= 1000.0;
                model.ResistorAOhms = Math.Clamp(ra, 10.0, 10000000.0);
            }

            var rbm = RbRegex.Match(header);
            if (rbm.Success && double.TryParse(rbm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rb))
            {
                if (header.Contains("k") || header.Contains("K")) rb *= 1000.0;
                model.ResistorBOhms = Math.Clamp(rb, 10.0, 10000000.0);
            }

            var cm = CapRegex.Match(header);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
            {
                if (header.Contains("nF") || header.Contains("nf")) c *= 1e-9;
                else if (header.Contains("uF") || header.Contains("uf") || header.Contains("µF")) c *= 1e-6;
                else if (header.Contains("pF") || header.Contains("pf")) c *= 1e-12;
                model.CapacitorFarads = Math.Clamp(c, 1e-13, 0.1);
            }

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var ram = RaRegex.Match(l);
            if (ram.Success && double.TryParse(ram.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ra))
            {
                if (l.Contains("k") || l.Contains("K")) ra *= 1000.0;
                model.ResistorAOhms = Math.Clamp(ra, 10.0, 10000000.0);
            }

            var rbm = RbRegex.Match(l);
            if (rbm.Success && double.TryParse(rbm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rb))
            {
                if (l.Contains("k") || l.Contains("K")) rb *= 1000.0;
                model.ResistorBOhms = Math.Clamp(rb, 10.0, 10000000.0);
            }

            var cm = CapRegex.Match(l);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
            {
                if (l.Contains("nF") || l.Contains("nf")) c *= 1e-9;
                else if (l.Contains("uF") || l.Contains("uf") || l.Contains("µF")) c *= 1e-6;
                else if (l.Contains("pF") || l.Contains("pf")) c *= 1e-12;
                model.CapacitorFarads = Math.Clamp(c, 1e-13, 0.1);
            }
        }

        return model;
    }

    public static string RenderTimerSvg(Timer555Model model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double oy = 220;
        double waveW = 240;
        double capH = 80;
        double outH = 50;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-timer-svg\">");
        sb.AppendLine("""
            <style>
              .tm-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .tm-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .tm-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .tm-axis { stroke: #475569; stroke-width: 1.2; }
              .tm-thresh { stroke: #fbbf24; stroke-width: 1; stroke-dasharray: 4 2; }
              .tm-cap-wave { fill: none; stroke: #38bdf8; stroke-width: 2.2; }
              .tm-out-wave { fill: none; stroke: #10b981; stroke-width: 2; }
              .tm-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .tm-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .tm-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"tm-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"tm-title\">⏱ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"tm-meta\">RA = {model.ResistorAOhms / 1000.0:F0}k • RB = {model.ResistorBOhms / 1000.0:F0}k • f = {model.FrequencyHz:F1} Hz • Duty = {model.DutyCyclePercent:F1}%</text>");

        // Capacitor Waveform Axes & Thresholds (1/3 Vcc and 2/3 Vcc)
        double capBaseY = oy - outH - 25;
        double threshLowY = capBaseY - capH * (1.0 / 3.0);
        double threshHighY = capBaseY - capH * (2.0 / 3.0);

        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{capBaseY}\" x2=\"{ox + waveW + 15}\" y2=\"{capBaseY}\" class=\"tm-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{threshLowY:F1}\" x2=\"{ox + waveW}\" y2=\"{threshLowY:F1}\" class=\"tm-thresh\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{threshHighY:F1}\" x2=\"{ox + waveW}\" y2=\"{threshHighY:F1}\" class=\"tm-thresh\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{threshHighY + 3:F1}\" font-family=\"monospace\" font-size=\"8\" fill=\"#fbbf24\" text-anchor=\"end\">⅔Vcc</text>");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{threshLowY + 3:F1}\" font-family=\"monospace\" font-size=\"8\" fill=\"#fbbf24\" text-anchor=\"end\">⅓Vcc</text>");

        // Output Pin Square Wave Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + waveW + 15}\" y2=\"{oy}\" class=\"tm-axis\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{oy - outH / 2}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#10b981\" text-anchor=\"end\">OUT</text>");

        // Render 2 full cycles of Exponential Charge/Discharge + Square Wave
        var capPath = new StringBuilder();
        var outPath = new StringBuilder();

        double cycleW = waveW / 2.0;
        double dutyRatio = model.DutyCyclePercent / 100.0;
        double tHighPix = cycleW * dutyRatio;
        double tLowPix = cycleW * (1.0 - dutyRatio);

        for (int c = 0; c < 2; c++)
        {
            double startX = ox + c * cycleW;
            double highEndX = startX + tHighPix;
            double lowEndX = highEndX + tLowPix;

            // Charge phase: 1/3 Vcc -> 2/3 Vcc exponential
            int chargeSteps = 20;
            for (int i = 0; i <= chargeSteps; i++)
            {
                double t = i / (double)chargeSteps;
                double px = startX + t * tHighPix;
                // exp charge approx
                double py = threshLowY - (threshLowY - threshHighY) * (1.0 - Math.Exp(-1.2 * t)) / (1.0 - Math.Exp(-1.2));

                if (c == 0 && i == 0) capPath.Append($"M {px:F1} {py:F1}");
                else capPath.Append($" L {px:F1} {py:F1}");
            }

            // Discharge phase: 2/3 Vcc -> 1/3 Vcc exponential
            int disSteps = 20;
            for (int i = 0; i <= disSteps; i++)
            {
                double t = i / (double)disSteps;
                double px = highEndX + t * tLowPix;
                double py = threshHighY + (threshLowY - threshHighY) * (1.0 - Math.Exp(-1.2 * t)) / (1.0 - Math.Exp(-1.2));
                capPath.Append($" L {px:F1} {py:F1}");
            }

            // Square wave output
            if (c == 0) outPath.Append($"M {startX:F1} {oy - outH} L {highEndX:F1} {oy - outH} L {highEndX:F1} {oy} L {lowEndX:F1} {oy} ");
            else outPath.Append($"L {startX:F1} {oy - outH} L {highEndX:F1} {oy - outH} L {highEndX:F1} {oy} L {lowEndX:F1} {oy} ");
        }

        sb.AppendLine($"  <path d=\"{capPath}\" class=\"tm-cap-wave\" />");
        sb.AppendLine($"  <path d=\"{outPath}\" class=\"tm-out-wave\" />");

        // Results Card on Right
        double cardX = 310;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"170\" height=\"195\" rx=\"6\" class=\"tm-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"tm-lbl\">Oscillation Frequency:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"tm-val\" font-size=\"14\" fill=\"#10b981\">{model.FrequencyHz:F1} Hz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"tm-lbl\">High Time (t_high):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"tm-val\">{model.HighTimeMs:F2} ms</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"tm-lbl\">Low Time (t_low):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"tm-val\">{model.LowTimeMs:F2} ms</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"tm-lbl\">Duty Cycle (D):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"tm-val\" fill=\"#fbbf24\">D = {model.DutyCyclePercent:F1} %</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">NE555 Timer RC Oscillator</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
