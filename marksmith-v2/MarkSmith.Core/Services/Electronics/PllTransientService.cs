using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class PllTransientModel
{
    public string Title { get; set; } = "PLL Phase Lock Transient Response";
    public double RefFreqMhz { get; set; } = 10.0;     // f_ref (MHz)
    public int DividerN { get; set; } = 240;           // N (e.g. 2.4 GHz output)
    public double DampingZeta { get; set; } = 0.707;   // loop damping ratio
    public double NaturalFreqKhz { get; set; } = 250.0;// fn (kHz)

    public double TargetVcoFreqGhz => (RefFreqMhz * DividerN) / 1000.0;

    // Lock Time T_lock approx 4 / (zeta * omega_n) in microseconds
    public double LockTimeUs => (4.0 / (DampingZeta * (2.0 * Math.PI * NaturalFreqKhz * 1000.0))) * 1e6;

    // Evaluate step response V_ctrl(t) normalized to 1.0
    public double EvaluateStepResponse(double tNormalized)
    {
        // Underdamped 2nd-order step: 1 - (e^(-zeta*wn*t) / sqrt(1-zeta^2)) * sin(wd*t + phi)
        double wn_t = tNormalized * 6.0; // Scaled to show full transient lock
        double z = Math.Clamp(DampingZeta, 0.1, 0.99);
        double wd_t = wn_t * Math.Sqrt(1.0 - z * z);
        double phi = Math.Acos(z);

        double env = Math.Exp(-z * wn_t) / Math.Sqrt(1.0 - z * z);
        return 1.0 - env * Math.Sin(wd_t + phi);
    }
}

public static class PllTransientService
{
    private static readonly Regex PllFenceRegex = new(
        @":::(?:pll-lock|phase-locked-loop|pll(?![-\w]))([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RefRegex = new(
        @"(?:f_ref|ref|reference)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DivRegex = new(
        @"(?:\bn\b|divider|n_div)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ZetaRegex = new(
        @"(?:\bzeta\b|damping)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WnRegex = new(
        @"(?:\bfn\b|\bwn\b|natural_freq)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PllTransientModel ParsePll(string blockText, string defaultTitle = "PLL Phase Lock Transient Response")
    {
        var model = new PllTransientModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = PllFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var rm = RefRegex.Match(header);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rf))
                model.RefFreqMhz = Math.Clamp(rf, 0.1, 1000.0);

            var dm = DivRegex.Match(header);
            if (dm.Success && int.TryParse(dm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int div))
                model.DividerN = Math.Clamp(div, 1, 10000);

            var zm = ZetaRegex.Match(header);
            if (zm.Success && double.TryParse(zm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double z))
                model.DampingZeta = Math.Clamp(z, 0.1, 2.0);

            var wm = WnRegex.Match(header);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fn))
                model.NaturalFreqKhz = Math.Clamp(fn, 1.0, 10000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var rm = RefRegex.Match(l);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rf))
                model.RefFreqMhz = Math.Clamp(rf, 0.1, 1000.0);

            var dm = DivRegex.Match(l);
            if (dm.Success && int.TryParse(dm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int div))
                model.DividerN = Math.Clamp(div, 1, 10000);

            var zm = ZetaRegex.Match(l);
            if (zm.Success && double.TryParse(zm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double z))
                model.DampingZeta = Math.Clamp(z, 0.1, 2.0);

            var wm = WnRegex.Match(l);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fn))
                model.NaturalFreqKhz = Math.Clamp(fn, 1.0, 10000.0);
        }

        return model;
    }

    public static string RenderPllSvg(PllTransientModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double oy = 210;
        double plotW = 240;
        double plotH = 130;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-pll-svg\">");
        sb.AppendLine("""
            <style>
              .pl-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .pl-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .pl-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .pl-axis { stroke: #475569; stroke-width: 1.2; }
              .pl-lock-target { stroke: #fbbf24; stroke-width: 1.2; stroke-dasharray: 4 3; }
              .pl-curve { fill: none; stroke: #38bdf8; stroke-width: 2.2; }
              .pl-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .pl-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .pl-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"pl-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"pl-title\">📻 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"pl-meta\">f_ref = {model.RefFreqMhz:F1}MHz • N = {model.DividerN} • f_vco = {model.TargetVcoFreqGhz:F3} GHz</text>");

        // Coordinate Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + plotW + 15}\" y2=\"{oy}\" class=\"pl-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - plotH - 20}\" class=\"pl-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + plotW + 10}\" y=\"{oy + 14}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">Time (μs)</text>");
        sb.AppendLine($"  <text x=\"{ox - 8}\" y=\"{oy - plotH - 12}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\" text-anchor=\"end\">Vctrl / Freq</text>");

        // Target Lock Line (1.0 normalized)
        double targetY = oy - plotH * 0.70;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{targetY:F1}\" x2=\"{ox + plotW}\" y2=\"{targetY:F1}\" class=\"pl-lock-target\" />");
        sb.AppendLine($"  <text x=\"{ox + plotW + 4:F1}\" y=\"{targetY + 3:F1}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">Lock</text>");

        // Step Transient Response Curve
        var path = new StringBuilder();
        int steps = 70;
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            double resp = model.EvaluateStepResponse(t);

            double px = ox + t * plotW;
            double py = oy - resp * (plotH * 0.70);

            if (i == 0) path.Append($"M {px:F1} {py:F1}");
            else path.Append($" L {px:F1} {py:F1}");
        }

        sb.AppendLine($"  <path d=\"{path}\" class=\"pl-curve\" />");

        // Results Card on Right
        double cardX = 310;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"170\" height=\"195\" rx=\"6\" class=\"pl-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"pl-lbl\">VCO Target Frequency:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"pl-val\">{model.TargetVcoFreqGhz:F3} GHz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"pl-lbl\">Loop Damping (ζ):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"pl-val\" fill=\"#10b981\">ζ = {model.DampingZeta:F3}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"pl-lbl\">Natural Freq (fn):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"pl-val\">{model.NaturalFreqKhz:F1} kHz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"pl-lbl\">Settling Lock Time:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"pl-val\" fill=\"#fbbf24\">Tlock ≈ {model.LockTimeUs:F2} μs</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">2nd-Order Charge Pump PLL</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
