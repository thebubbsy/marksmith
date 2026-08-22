using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class BuckBoostModel
{
    public string Title { get; set; } = "Inverting Buck-Boost DC-DC Converter";
    public double InputVoltageVin { get; set; } = 12.0;    // Vin (V)
    public double OutputVoltageVout { get; set; } = -15.0; // Vout (V) (inverting)
    public double OutputCurrentIout { get; set; } = 2.0;   // Iout (A)
    public double SwitchFreqKhz { get; set; } = 250.0;     // f_sw (kHz)
    public double InductorUh { get; set; } = 47.0;         // L (uH)
    public double OutputCapUf { get; set; } = 220.0;       // C (uF)

    // Absolute output voltage magnitude
    public double MagVout => Math.Abs(OutputVoltageVout);

    // Duty Cycle D = |Vout| / (Vin + |Vout|)
    public double DutyCycle => MagVout / Math.Max(0.1, InputVoltageVin + MagVout);

    // Equivalent load resistance R (Ohms)
    public double LoadResistance => MagVout / Math.Max(0.01, OutputCurrentIout);

    // Average Inductor Current I_L = Iout / (1 - D)
    public double AvgInductorCurrent => OutputCurrentIout / Math.Max(1e-4, 1.0 - DutyCycle);

    // Inductor Current Ripple Delta I_L (A) = (Vin * D) / (f_sw * L)
    public double InductorCurrentRipple
    {
        get
        {
            double fHz = SwitchFreqKhz * 1000.0;
            double lH = InductorUh * 1e-6;
            return (InputVoltageVin * DutyCycle) / Math.Max(1e-9, fHz * lH);
        }
    }

    // Critical Inductance for CCM Boundary L_crit (uH) = ((1 - D)^2 * R) / (2 * f_sw)
    public double CriticalInductanceUh
    {
        get
        {
            double fHz = SwitchFreqKhz * 1000.0;
            double r = LoadResistance;
            double lCritH = (Math.Pow(1.0 - DutyCycle, 2) * r) / (2.0 * fHz);
            return lCritH * 1e6;
        }
    }

    // Operating Mode: CCM vs DCM
    public bool IsCcm => InductorUh >= CriticalInductanceUh;

    // Peak Inductor Current (A)
    public double PeakInductorCurrent => AvgInductorCurrent + (InductorCurrentRipple / 2.0);

    // Output Voltage Ripple (mV) = (Iout * D) / (f_sw * C)
    public double OutputVoltageRippleMv
    {
        get
        {
            double fHz = SwitchFreqKhz * 1000.0;
            double cF = OutputCapUf * 1e-6;
            double rippleV = (OutputCurrentIout * DutyCycle) / Math.Max(1e-9, fHz * cF);
            return rippleV * 1000.0;
        }
    }
}

public static class BuckBoostConverterService
{
    private static readonly Regex BuckBoostFenceRegex = new(
        @":::(?:buck-boost|inverting-buck-boost|dcdc-buck-boost)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VinRegex = new(
        @"(?:\bvin\b|\bv_in\b|\binput_v\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[vV])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VoutRegex = new(
        @"(?:\bvout\b|\bv_out\b|\boutput_v\b)\s*[:=]\s*""?(-?\d+(?:\.\d+)?)(?:[vV])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IoutRegex = new(
        @"(?:\biout\b|\bi_out\b|\bload_i\b|\bcurrent\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[aA])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FswRegex = new(
        @"(?:\bfsw\b|\bf_sw\b|\bfreq\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LRegex = new(
        @"(?:\binductor\b|\bl\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[uU][hH]|µ[hH])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CRegex = new(
        @"(?:\bcap\b|\bc\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[uU][fF]|µ[fF])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static BuckBoostModel ParseBuckBoost(string blockText, string defaultTitle = "Inverting Buck-Boost DC-DC Converter")
    {
        var model = new BuckBoostModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = BuckBoostFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var vm = VinRegex.Match(header);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double vin))
                model.InputVoltageVin = Math.Clamp(vin, 1.0, 1000.0);

            var vom = VoutRegex.Match(header);
            if (vom.Success && double.TryParse(vom.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double vout))
                model.OutputVoltageVout = Math.Clamp(vout, -1000.0, 1000.0);

            var im = IoutRegex.Match(header);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double iout))
                model.OutputCurrentIout = Math.Clamp(iout, 0.01, 200.0);

            var fm = FswRegex.Match(header);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fsw))
                model.SwitchFreqKhz = Math.Clamp(fsw, 1.0, 10000.0);

            var lm = LRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double l))
                model.InductorUh = Math.Clamp(l, 0.1, 10000.0);

            var cm = CRegex.Match(header);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
                model.OutputCapUf = Math.Clamp(c, 0.1, 50000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var vm = VinRegex.Match(l);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double vin))
                model.InputVoltageVin = Math.Clamp(vin, 1.0, 1000.0);

            var vom = VoutRegex.Match(l);
            if (vom.Success && double.TryParse(vom.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double vout))
                model.OutputVoltageVout = Math.Clamp(vout, -1000.0, 1000.0);

            var im = IoutRegex.Match(l);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double iout))
                model.OutputCurrentIout = Math.Clamp(iout, 0.01, 200.0);

            var fm = FswRegex.Match(l);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fsw))
                model.SwitchFreqKhz = Math.Clamp(fsw, 1.0, 10000.0);

            var lm = LRegex.Match(l);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double lv))
                model.InductorUh = Math.Clamp(lv, 0.1, 10000.0);

            var cm = CRegex.Match(l);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cv))
                model.OutputCapUf = Math.Clamp(cv, 0.1, 50000.0);
        }

        return model;
    }

    public static string RenderBuckBoostSvg(BuckBoostModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-buckboost-svg\">");
        sb.AppendLine("""
            <style>
              .bb-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .bb-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .bb-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .bb-axis { stroke: #64748b; stroke-width: 1.5; }
              .bb-il-wave { fill: none; stroke: #fbbf24; stroke-width: 2.5; }
              .bb-avg-line { stroke: #38bdf8; stroke-width: 1.5; stroke-dasharray: 4 2; }
              .bb-band { fill: #38bdf8; fill-opacity: 0.15; }
              .bb-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .bb-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .bb-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"bb-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"bb-title\">⚡ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"bb-meta\">Vin = {model.InputVoltageVin:F1}V • Vout = {model.OutputVoltageVout:F1}V • Iout = {model.OutputCurrentIout:F1}A • D = {model.DutyCycle*100:F1}% ({(model.IsCcm ? "CCM" : "DCM")})</text>");

        // Inductor Current Triangular Waveform on Left
        double x1 = 35;
        double x2 = 270;
        double yBase = 220;
        double yTop = 75;

        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yBase}\" x2=\"{x2}\" y2=\"{yBase}\" class=\"bb-axis\" />");
        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yTop}\" x2=\"{x1}\" y2=\"{yBase}\" class=\"bb-axis\" />");
        sb.AppendLine($"  <text x=\"{x2 - 35}\" y=\"{yBase + 16}\" font-family=\"monospace\" font-size=\"9\" fill=\"#64748b\">Time (t)</text>");
        sb.AppendLine($"  <text x=\"{x1 - 10}\" y=\"{yTop - 6}\" font-family=\"monospace\" font-size=\"9\" fill=\"#64748b\">i_L(t)</text>");

        // 2 Cycles of Triangular Inductor Current
        double periodW = (x2 - x1 - 20.0) / 2.0;
        double dW = periodW * model.DutyCycle;
        double yAvg = yBase - 65;
        double rippleH = Math.Clamp(30.0 * (model.InductorCurrentRipple / Math.Max(0.5, model.AvgInductorCurrent)), 8.0, 45.0);
        double yPeak = yAvg - rippleH;
        double yValley = yAvg + rippleH;

        // Average current line
        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yAvg}\" x2=\"{x2 - 10}\" y2=\"{yAvg}\" class=\"bb-avg-line\" />");
        sb.AppendLine($"  <text x=\"{x2 - 75}\" y=\"{yAvg - 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#38bdf8\">I_avg={model.AvgInductorCurrent:F1}A</text>");

        var wavePath = new StringBuilder();
        // Cycle 1
        double t0 = x1 + 10;
        wavePath.Append($"M {t0:F1},{yValley:F1} L {t0 + dW:F1},{yPeak:F1} L {t0 + periodW:F1},{yValley:F1} ");
        // Cycle 2
        double t1 = t0 + periodW;
        wavePath.Append($"L {t1 + dW:F1},{yPeak:F1} L {t1 + periodW:F1},{yValley:F1}");

        sb.AppendLine($"  <path d=\"{wavePath}\" class=\"bb-il-wave\" />");

        // Duty interval marker for Cycle 1
        sb.AppendLine($"  <rect x=\"{t0}\" y=\"{yBase - 12}\" width=\"{dW}\" height=\"12\" class=\"bb-band\" />");
        sb.AppendLine($"  <text x=\"{t0 + dW / 2.0 - 10:F1}\" y=\"{yBase - 3}\" font-family=\"monospace\" font-size=\"8\" fill=\"#38bdf8\">D•T</text>");
        sb.AppendLine($"  <text x=\"{t0 + dW + (periodW - dW)/2.0 - 15:F1}\" y=\"{yBase - 3}\" font-family=\"monospace\" font-size=\"8\" fill=\"#94a3b8\">(1-D)•T</text>");

        // Results Card on Right
        double cardX = 285;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"225\" height=\"205\" rx=\"6\" class=\"bb-card-bg\" />");

        string modeColor = model.IsCcm ? "#10b981" : "#f59e0b";
        string modeText = model.IsCcm ? "Continuous (CCM)" : "Discontinuous (DCM)";

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"bb-lbl\">Operating Mode & Critical L:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"bb-val\" font-size=\"13\" fill=\"{modeColor}\">{modeText} (L_crit={model.CriticalInductanceUh:F1}µH)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"bb-lbl\">Duty Cycle (D):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"bb-val\" font-size=\"14\" fill=\"#38bdf8\">D = {model.DutyCycle:F3} ({model.DutyCycle*100:F1}%)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"bb-lbl\">Inductor Current Ripple (ΔI_L):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"bb-val\" fill=\"#fbbf24\">ΔI_L = {model.InductorCurrentRipple:F2} A ({model.InductorCurrentRipple/model.AvgInductorCurrent*100:F0}% I_avg)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"bb-lbl\">Peak Switch Current (I_pk):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"bb-val\" fill=\"#f43f5e\">I_pk = {model.PeakInductorCurrent:F2} A</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"bb-lbl\">Output Voltage Ripple (ΔV_out):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" class=\"bb-val\" fill=\"#10b981\">ΔV_out = {model.OutputVoltageRippleMv:F1} mV ({model.OutputVoltageRippleMv/(model.MagVout*1000)*100:F2}%)</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
