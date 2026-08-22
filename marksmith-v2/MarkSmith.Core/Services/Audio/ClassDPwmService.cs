using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Audio;

public class ClassDPwmModel
{
    public string Title { get; set; } = "Class-D Audio PWM & LC Filter";
    public double AudioFreqKhz { get; set; } = 1.0;      // f_in (kHz)
    public double CarrierFreqKhz { get; set; } = 400.0;  // f_pwm (kHz)
    public double ModulationIndex { get; set; } = 0.85;  // M (0 to 1.0)
    public double InductorUh { get; set; } = 15.0;       // L (uH)
    public double CapacitorNf { get; set; } = 470.0;     // C (nF)
    public double SpeakerLoadOhms { get; set; } = 8.0;   // R_L (Ohms)

    // LC Low-Pass Filter Cutoff Frequency f_c = 1 / (2 * pi * sqrt(L * C)) in kHz
    public double FilterCutoffKhz
    {
        get
        {
            double lH = InductorUh * 1e-6;
            double cF = CapacitorNf * 1e-9;
            double fc = 1.0 / (2.0 * Math.PI * Math.Sqrt(lH * cF));
            return fc / 1000.0;
        }
    }

    // Filter Quality Factor Q = R_L * sqrt(C / L)
    public double FilterQ
    {
        get
        {
            double lH = InductorUh * 1e-6;
            double cF = CapacitorNf * 1e-9;
            return SpeakerLoadOhms * Math.Sqrt(cF / lH);
        }
    }

    // Theoretical Efficiency approx 92%
    public double EfficiencyPercent => 92.5;
}

public static class ClassDPwmService
{
    private static readonly Regex ClassDFenceRegex = new(
        @":::(?:class-d|class-d-amp|pwm-audio)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AudioFreqRegex = new(
        @"(?:f_in|audio_freq|f_audio)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CarrierFreqRegex = new(
        @"(?:f_pwm|carrier|f_carrier)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ModRegex = new(
        @"(?:mod|m|mod_index)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InductorRegex = new(
        @"(?:l|inductor|inductance)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[uU][hH]|µH)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CapRegex = new(
        @"(?:c|capacitor|capacitance)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[nN][fF])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LoadRegex = new(
        @"(?:load|speaker|r_load)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[oO]hm|Ω)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ClassDPwmModel ParseClassD(string blockText, string defaultTitle = "Class-D Audio PWM & LC Filter")
    {
        var model = new ClassDPwmModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = ClassDFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var afm = AudioFreqRegex.Match(header);
            if (afm.Success && double.TryParse(afm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fin))
                model.AudioFreqKhz = Math.Clamp(fin, 0.02, 50.0);

            var cfm = CarrierFreqRegex.Match(header);
            if (cfm.Success && double.TryParse(cfm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fpwm))
                model.CarrierFreqKhz = Math.Clamp(fpwm, 50.0, 2000.0);

            var mm = ModRegex.Match(header);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double mod))
                model.ModulationIndex = Math.Clamp(mod, 0.05, 1.0);

            var lm = InductorRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double l))
                model.InductorUh = Math.Clamp(l, 1.0, 500.0);

            var cm = CapRegex.Match(header);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
                model.CapacitorNf = Math.Clamp(c, 10.0, 5000.0);

            var rlm = LoadRegex.Match(header);
            if (rlm.Success && double.TryParse(rlm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r))
                model.SpeakerLoadOhms = Math.Clamp(r, 1.0, 64.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var afm = AudioFreqRegex.Match(l);
            if (afm.Success && double.TryParse(afm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fin))
                model.AudioFreqKhz = Math.Clamp(fin, 0.02, 50.0);

            var cfm = CarrierFreqRegex.Match(l);
            if (cfm.Success && double.TryParse(cfm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fpwm))
                model.CarrierFreqKhz = Math.Clamp(fpwm, 50.0, 2000.0);

            var mm = ModRegex.Match(l);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double mod))
                model.ModulationIndex = Math.Clamp(mod, 0.05, 1.0);

            var lm = InductorRegex.Match(l);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double lVal))
                model.InductorUh = Math.Clamp(lVal, 1.0, 500.0);

            var cm = CapRegex.Match(l);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cVal))
                model.CapacitorNf = Math.Clamp(cVal, 10.0, 5000.0);

            var rlm = LoadRegex.Match(l);
            if (rlm.Success && double.TryParse(rlm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rVal))
                model.SpeakerLoadOhms = Math.Clamp(rVal, 1.0, 64.0);
        }

        return model;
    }

    public static string RenderClassDSvg(ClassDPwmModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double waveW = 240;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-classd-svg\">");
        sb.AppendLine("""
            <style>
              .cd-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .cd-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .cd-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .cd-axis { stroke: #334155; stroke-width: 1; }
              .cd-tri { fill: none; stroke: #64748b; stroke-width: 1; stroke-dasharray: 2 2; }
              .cd-audio { fill: none; stroke: #fbbf24; stroke-width: 1.8; }
              .cd-pwm { fill: none; stroke: #38bdf8; stroke-width: 1.5; }
              .cd-filtered { fill: none; stroke: #10b981; stroke-width: 2.2; }
              .cd-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .cd-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .cd-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"cd-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"cd-title\">🔊 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"cd-meta\">f_audio = {model.AudioFreqKhz:F1}kHz • f_pwm = {model.CarrierFreqKhz:F0}kHz • fc = {model.FilterCutoffKhz:F1}kHz • η = {model.EfficiencyPercent:F1}%</text>");

        // Top Track: Audio Sine Wave vs Triangle Carrier
        double topCenterY = 100;
        double topH = 25;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{topCenterY}\" x2=\"{ox + waveW}\" y2=\"{topCenterY}\" class=\"cd-axis\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{topCenterY + 3}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\" text-anchor=\"end\">Audio/Tri</text>");

        // Carrier Triangle Wave (8 cycles)
        var triPath = new StringBuilder();
        int triCycles = 8;
        double triW = waveW / (double)triCycles;
        for (int c = 0; c < triCycles; c++)
        {
            double tx = ox + c * triW;
            if (c == 0) triPath.Append($"M {tx:F1} {topCenterY + topH} L {tx + triW / 2:F1} {topCenterY - topH} L {tx + triW:F1} {topCenterY + topH}");
            else triPath.Append($" L {tx + triW / 2:F1} {topCenterY - topH} L {tx + triW:F1} {topCenterY + topH}");
        }
        sb.AppendLine($"  <path d=\"{triPath}\" class=\"cd-tri\" />");

        // Modulating Audio Sine Wave
        var audioPath = new StringBuilder();
        for (int i = 0; i <= 40; i++)
        {
            double t = i / 40.0;
            double px = ox + t * waveW;
            double py = topCenterY - Math.Sin(t * 2.0 * Math.PI) * topH * model.ModulationIndex;
            if (i == 0) audioPath.Append($"M {px:F1} {py:F1}");
            else audioPath.Append($" L {px:F1} {py:F1}");
        }
        sb.AppendLine($"  <path d=\"{audioPath}\" class=\"cd-audio\" />");

        // Middle Track: PWM Pulse Train
        double pwmBaseY = 160;
        double pwmH = 20;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{pwmBaseY}\" x2=\"{ox + waveW}\" y2=\"{pwmBaseY}\" class=\"cd-axis\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{pwmBaseY - pwmH / 2}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#38bdf8\" text-anchor=\"end\">PWM</text>");

        var pwmPath = new StringBuilder();
        for (int c = 0; c < triCycles; c++)
        {
            double tx = ox + c * triW;
            double centerSine = Math.Sin((c / (double)triCycles) * 2.0 * Math.PI) * model.ModulationIndex;
            double duty = Math.Clamp(0.5 + 0.45 * centerSine, 0.05, 0.95);
            double highW = triW * duty;

            if (c == 0) pwmPath.Append($"M {tx:F1} {pwmBaseY - pwmH} L {tx + highW:F1} {pwmBaseY - pwmH} L {tx + highW:F1} {pwmBaseY} L {tx + triW:F1} {pwmBaseY} ");
            else pwmPath.Append($"L {tx:F1} {pwmBaseY - pwmH} L {tx + highW:F1} {pwmBaseY - pwmH} L {tx + highW:F1} {pwmBaseY} L {tx + triW:F1} {pwmBaseY} ");
        }
        sb.AppendLine($"  <path d=\"{pwmPath}\" class=\"cd-pwm\" />");

        // Bottom Track: Filtered Reconstructed Speaker Audio
        double filtCenterY = 215;
        double filtH = 22;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{filtCenterY}\" x2=\"{ox + waveW}\" y2=\"{filtCenterY}\" class=\"cd-axis\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{filtCenterY + 3}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#10b981\" text-anchor=\"end\">V_out</text>");

        var filtPath = new StringBuilder();
        for (int i = 0; i <= 40; i++)
        {
            double t = i / 40.0;
            double px = ox + t * waveW;
            double py = filtCenterY - Math.Sin(t * 2.0 * Math.PI) * filtH * model.ModulationIndex;
            if (i == 0) filtPath.Append($"M {px:F1} {py:F1}");
            else filtPath.Append($" L {px:F1} {py:F1}");
        }
        sb.AppendLine($"  <path d=\"{filtPath}\" class=\"cd-filtered\" />");

        // Results Card on Right
        double cardX = 300;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"180\" height=\"195\" rx=\"6\" class=\"cd-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"cd-lbl\">LC Cutoff Frequency (fc):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"cd-val\" font-size=\"14\" fill=\"#10b981\">{model.FilterCutoffKhz:F1} kHz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"cd-lbl\">Filter Damping Factor (Q):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"cd-val\" fill=\"#fbbf24\">Q = {model.FilterQ:F2} (Load {model.SpeakerLoadOhms:F0}Ω)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"cd-lbl\">Switching Efficiency (η):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"cd-val\">η ≈ {model.EfficiencyPercent:F1} %</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"cd-lbl\">LC Filter Values:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"cd-val\" fill=\"#38bdf8\">L={model.InductorUh:F0}µH, C={model.CapacitorNf:F0}nF</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Class-D Pulse Modulator</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
