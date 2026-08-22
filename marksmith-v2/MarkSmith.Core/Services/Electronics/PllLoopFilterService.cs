using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class PllLoopFilterModel
{
    public string Title { get; set; } = "PLL Charge Pump & 2nd-Order Passive Loop Filter";
    public double RefFreqMhz { get; set; } = 20.0;       // f_ref (MHz)
    public double ChargePumpCurrentMa { get; set; } = 2.5;// I_cp (mA)
    public double VcoGainMhzPerV { get; set; } = 120.0;   // K_vco (MHz/V)
    public int FeedbackDividerN { get; set; } = 120;     // N divider
    public double ResistorR1Kohm { get; set; } = 1.8;    // R1 (kohm)
    public double CapacitorC1Nf { get; set; } = 2.2;     // C1 (nF)
    public double CapacitorC2Pf { get; set; } = 150.0;   // C2 (pF)

    // Component standard units
    public double R1Ohms => ResistorR1Kohm * 1000.0;
    public double C1Farads => CapacitorC1Nf * 1e-9;
    public double C2Farads => CapacitorC2Pf * 1e-12;
    public double IcpAmps => ChargePumpCurrentMa * 1e-3;
    public double KvcoRadPerV => VcoGainMhzPerV * 1e6 * 2.0 * Math.PI;

    // Zero frequency omega_z (rad/s) = 1 / (R1 * C1)
    public double OmegaZ => 1.0 / Math.Max(1e-12, R1Ohms * C1Farads);
    public double FreqZKhz => (OmegaZ / (2.0 * Math.PI)) / 1000.0;

    // Pole frequency omega_p (rad/s) = (C1 + C2) / (R1 * C1 * C2)
    public double OmegaP => (C1Farads + C2Farads) / Math.Max(1e-18, R1Ohms * C1Farads * C2Farads);
    public double FreqPKhz => (OmegaP / (2.0 * Math.PI)) / 1000.0;

    // Loop Bandwidth Crossover omega_c (rad/s) = sqrt(omega_z * omega_p)
    public double OmegaC => Math.Sqrt(OmegaZ * OmegaP);
    public double LoopBandwidthKhz => (OmegaC / (2.0 * Math.PI)) / 1000.0;

    // Phase Margin phi_m (deg) = arctan(omega_c / omega_z) - arctan(omega_c / omega_p)
    public double PhaseMarginDeg
    {
        get
        {
            double rad = Math.Atan(OmegaC / Math.Max(1.0, OmegaZ)) - Math.Atan(OmegaC / Math.Max(1.0, OmegaP));
            return rad * (180.0 / Math.PI);
        }
    }

    // Natural frequency omega_n (rad/s)
    public double OmegaN => Math.Sqrt((IcpAmps * KvcoRadPerV) / (2.0 * Math.PI * FeedbackDividerN * Math.Max(1e-12, C1Farads + C2Farads)));

    // Damping factor zeta
    public double DampingFactor => (R1Ohms * C1Farads / 2.0) * OmegaN;

    // Lock Time T_lock (us) ~ 4 / (zeta * omega_n)
    public double LockTimeUs => (4.0 / Math.Max(1.0, DampingFactor * OmegaN)) * 1e6;
}

public static class PllLoopFilterService
{
    private static readonly Regex FilterFenceRegex = new(
        @":::(?:pll-filter|pll-loop-filter|pll-bode)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FrefRegex = new(
        @"(?:\bf_ref\b|\bref_freq\b|\bfref\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IcpRegex = new(
        @"(?:\bicp\b|\bi_cp\b|\bcharge_pump\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][aA])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KvcoRegex = new(
        @"(?:\bkvco\b|\bk_vco\b|\bvco_gain\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][hH][zZ]/[vV])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NRegex = new(
        @"(?:\bn_div\b|\bdivider\b|\bn\b)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex R1Regex = new(
        @"(?:\br1\b|\br\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][oO]hm|kΩ|Ω)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex C1Regex = new(
        @"(?:\bc1\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[nN][fF]|µ[fF])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex C2Regex = new(
        @"(?:\bc2\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[pP][fF]|nF)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PllLoopFilterModel ParsePllFilter(string blockText, string defaultTitle = "PLL Charge Pump & 2nd-Order Passive Loop Filter")
    {
        var model = new PllLoopFilterModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = FilterFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var frm = FrefRegex.Match(header);
            if (frm.Success && double.TryParse(frm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fr))
                model.RefFreqMhz = Math.Clamp(fr, 0.01, 1000.0);

            var icpm = IcpRegex.Match(header);
            if (icpm.Success && double.TryParse(icpm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double icp))
                model.ChargePumpCurrentMa = Math.Clamp(icp, 0.01, 50.0);

            var kvm = KvcoRegex.Match(header);
            if (kvm.Success && double.TryParse(kvm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double kv))
                model.VcoGainMhzPerV = Math.Clamp(kv, 0.1, 5000.0);

            var nm = NRegex.Match(header);
            if (nm.Success && int.TryParse(nm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out int nVal))
                model.FeedbackDividerN = Math.Clamp(nVal, 1, 10000);

            var r1m = R1Regex.Match(header);
            if (r1m.Success && double.TryParse(r1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r1))
                model.ResistorR1Kohm = Math.Clamp(r1, 0.01, 100.0);

            var c1m = C1Regex.Match(header);
            if (c1m.Success && double.TryParse(c1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c1))
                model.CapacitorC1Nf = Math.Clamp(c1, 0.01, 500.0);

            var c2m = C2Regex.Match(header);
            if (c2m.Success && double.TryParse(c2m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c2))
                model.CapacitorC2Pf = Math.Clamp(c2, 1.0, 10000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var frm = FrefRegex.Match(l);
            if (frm.Success && double.TryParse(frm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fr))
                model.RefFreqMhz = Math.Clamp(fr, 0.01, 1000.0);

            var icpm = IcpRegex.Match(l);
            if (icpm.Success && double.TryParse(icpm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double icp))
                model.ChargePumpCurrentMa = Math.Clamp(icp, 0.01, 50.0);

            var kvm = KvcoRegex.Match(l);
            if (kvm.Success && double.TryParse(kvm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double kv))
                model.VcoGainMhzPerV = Math.Clamp(kv, 0.1, 5000.0);

            var nm = NRegex.Match(l);
            if (nm.Success && int.TryParse(nm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out int nVal))
                model.FeedbackDividerN = Math.Clamp(nVal, 1, 10000);

            var r1m = R1Regex.Match(l);
            if (r1m.Success && double.TryParse(r1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r1))
                model.ResistorR1Kohm = Math.Clamp(r1, 0.01, 100.0);

            var c1m = C1Regex.Match(l);
            if (c1m.Success && double.TryParse(c1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c1))
                model.CapacitorC1Nf = Math.Clamp(c1, 0.01, 500.0);

            var c2m = C2Regex.Match(l);
            if (c2m.Success && double.TryParse(c2m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c2))
                model.CapacitorC2Pf = Math.Clamp(c2, 1.0, 10000.0);
        }

        return model;
    }

    public static string RenderPllFilterSvg(PllLoopFilterModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-pllfilter-svg\">");
        sb.AppendLine("""
            <style>
              .pf-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .pf-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .pf-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .pf-axis { stroke: #64748b; stroke-width: 1.5; }
              .pf-gain { fill: none; stroke: #38bdf8; stroke-width: 2.5; }
              .pf-phase { fill: none; stroke: #fbbf24; stroke-width: 2; stroke-dasharray: 4 2; }
              .pf-crossover { stroke: #f43f5e; stroke-width: 1.2; stroke-dasharray: 2 2; }
              .pf-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .pf-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .pf-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"pf-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"pf-title\">🔄 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"pf-meta\">Icp={model.ChargePumpCurrentMa:F1}mA • Kvco={model.VcoGainMhzPerV:F0}MHz/V • N={model.FeedbackDividerN} • BW={model.LoopBandwidthKhz:F1}kHz • PM={model.PhaseMarginDeg:F1}°</text>");

        // Dual Bode Plot Axes on Left
        double x1 = 35;
        double x2 = 270;
        double yBase = 220;
        double yTop = 75;
        double yMid = (yTop + yBase) / 2.0;

        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yBase}\" x2=\"{x2}\" y2=\"{yBase}\" class=\"pf-axis\" />");
        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yTop}\" x2=\"{x1}\" y2=\"{yBase}\" class=\"pf-axis\" />");
        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yMid}\" x2=\"{x2}\" y2=\"{yMid}\" stroke=\"#334155\" stroke-width=\"1\" stroke-dasharray=\"2 2\" />");

        sb.AppendLine($"  <text x=\"{x2 - 35}\" y=\"{yBase + 16}\" font-family=\"monospace\" font-size=\"9\" fill=\"#64748b\">Freq (log)</text>");
        sb.AppendLine($"  <text x=\"{x1 - 10}\" y=\"{yTop - 6}\" font-family=\"monospace\" font-size=\"9\" fill=\"#38bdf8\">Gain (dB)</text>");

        // Crossover vertical line
        double xCross = x1 + (x2 - x1) * 0.55;
        sb.AppendLine($"  <line x1=\"{xCross}\" y1=\"{yTop}\" x2=\"{xCross}\" y2=\"{yBase}\" class=\"pf-crossover\" />");
        sb.AppendLine($"  <text x=\"{xCross - 20:F1}\" y=\"{yTop + 14}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f43f5e\">fc={model.LoopBandwidthKhz:F0}k</text>");

        // Open-Loop Gain Curve (starts high, crosses 0dB at xCross, rolls off at -20dB/dec then -40dB/dec)
        var gainCurve = new StringBuilder();
        gainCurve.Append($"M {x1 + 10},{yTop + 15} ");
        gainCurve.Append($"Q {xCross - 30},{yMid - 10} {xCross},{yMid} ");
        gainCurve.Append($"Q {xCross + 40},{yMid + 25} {x2 - 10},{yBase - 15}");
        sb.AppendLine($"  <path d=\"{gainCurve}\" class=\"pf-gain\" />");

        // Phase Curve (starts at -90, peaks at PM around xCross, rolls off to -180)
        double phasePeakY = yMid - Math.Clamp(model.PhaseMarginDeg * 0.6, 15.0, 45.0);
        var phaseCurve = new StringBuilder();
        phaseCurve.Append($"M {x1 + 10},{yBase - 30} ");
        phaseCurve.Append($"Q {xCross - 35},{phasePeakY} {xCross},{phasePeakY} ");
        phaseCurve.Append($"Q {xCross + 45},{phasePeakY + 10} {x2 - 10},{yBase - 25}");
        sb.AppendLine($"  <path d=\"{phaseCurve}\" class=\"pf-phase\" />");
        sb.AppendLine($"  <text x=\"{xCross - 15:F1}\" y=\"{phasePeakY - 6:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#fbbf24\">PM={model.PhaseMarginDeg:F0}°</text>");

        // Results Card on Right
        double cardX = 285;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"225\" height=\"205\" rx=\"6\" class=\"pf-card-bg\" />");

        string pmColor = model.PhaseMarginDeg >= 45.0 ? "#10b981" : "#f43f5e";

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"pf-lbl\">Phase Margin (Stability):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"pf-val\" font-size=\"14\" fill=\"{pmColor}\">PM = {model.PhaseMarginDeg:F1}° ({(model.PhaseMarginDeg >= 45 ? "STABLE" : "MARGINAL")})</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"pf-lbl\">Loop Bandwidth (f_c):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"pf-val\" fill=\"#38bdf8\">f_c = {model.LoopBandwidthKhz:F1} kHz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"pf-lbl\">Filter Zero & Pole Frequencies:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"pf-val\" font-size=\"11\">fz = {model.FreqZKhz:F1} kHz  |  fp = {model.FreqPKhz:F1} kHz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"pf-lbl\">Estimated Lock Time (T_lock):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"pf-val\" fill=\"#fbbf24\">T_lock ≈ {model.LockTimeUs:F1} µs (ζ = {model.DampingFactor:F2})</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"pf-lbl\">Filter RC Values:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" font-family=\"monospace\" font-size=\"10.5\" fill=\"#94a3b8\">R1={model.ResistorR1Kohm:F1}kΩ, C1={model.CapacitorC1Nf:F1}nF, C2={model.CapacitorC2Pf:F0}pF</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
