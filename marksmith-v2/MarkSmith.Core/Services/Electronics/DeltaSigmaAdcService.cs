using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class DeltaSigmaAdcModel
{
    public string Title { get; set; } = "Delta-Sigma (ΔΣ) ADC Noise Shaping";
    public double InputFreqKhz { get; set; } = 1.0;       // Audio test tone (kHz)
    public double NyquistRateKhz { get; set; } = 44.1;   // Nyquist base rate fs (kHz)
    public int OversamplingRatio { get; set; } = 64;     // OSR
    public int QuantizerBits { get; set; } = 1;          // N bits

    // Modulator Sampling Clock
    public double SamplingClockKhz => NyquistRateKhz * OversamplingRatio;

    // Base Quantizer SNR (dB) = 6.02 * N + 1.76
    public double BaseSnrDb => 6.02 * QuantizerBits + 1.76;

    // 1st-Order Delta-Sigma Noise Shaping SNR boost (dB) = 10 * log10((3 / pi^2) * OSR^3)
    public double NoiseShapingSnrGainDb
    {
        get
        {
            double osr = Math.Max(2.0, OversamplingRatio);
            double gain = (3.0 / Math.Pow(Math.PI, 2)) * Math.Pow(osr, 3);
            return 10.0 * Math.Log10(gain);
        }
    }

    // Total In-Band SNR (dB)
    public double TotalInBandSnrDb => BaseSnrDb + NoiseShapingSnrGainDb;

    // Effective Number of Bits (ENOB)
    public double Enob => (TotalInBandSnrDb - 1.76) / 6.02;
}

public static class DeltaSigmaAdcService
{
    private static readonly Regex DeltaSigmaFenceRegex = new(
        @":::(?:delta-sigma|sigma-delta|noise-shaping)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FinRegex = new(
        @"(?:f_in|fin|signal_freq)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FsRegex = new(
        @"(?:f_s|fs|nyquist)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OsrRegex = new(
        @"(?:osr|oversampling)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BitsRegex = new(
        @"(?:bits|n|quantizer)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static DeltaSigmaAdcModel ParseDeltaSigma(string blockText, string defaultTitle = "Delta-Sigma (ΔΣ) ADC Noise Shaping")
    {
        var model = new DeltaSigmaAdcModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = DeltaSigmaFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var fm = FinRegex.Match(header);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fin))
                model.InputFreqKhz = Math.Clamp(fin, 0.01, 100.0);

            var fsm = FsRegex.Match(header);
            if (fsm.Success && double.TryParse(fsm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fs))
                model.NyquistRateKhz = Math.Clamp(fs, 1.0, 1000.0);

            var om = OsrRegex.Match(header);
            if (om.Success && int.TryParse(om.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int osr))
                model.OversamplingRatio = Math.Clamp(osr, 2, 1024);

            var bm = BitsRegex.Match(header);
            if (bm.Success && int.TryParse(bm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bits))
                model.QuantizerBits = Math.Clamp(bits, 1, 16);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var fm = FinRegex.Match(l);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fin))
                model.InputFreqKhz = Math.Clamp(fin, 0.01, 100.0);

            var fsm = FsRegex.Match(l);
            if (fsm.Success && double.TryParse(fsm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fs))
                model.NyquistRateKhz = Math.Clamp(fs, 1.0, 1000.0);

            var om = OsrRegex.Match(l);
            if (om.Success && int.TryParse(om.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int osr))
                model.OversamplingRatio = Math.Clamp(osr, 2, 1024);

            var bm = BitsRegex.Match(l);
            if (bm.Success && int.TryParse(bm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bits))
                model.QuantizerBits = Math.Clamp(bits, 1, 16);
        }

        return model;
    }

    public static string RenderDeltaSigmaSvg(DeltaSigmaAdcModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-deltasigma-svg\">");
        sb.AppendLine("""
            <style>
              .ds-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .ds-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .ds-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .ds-axis { stroke: #64748b; stroke-width: 1.5; }
              .ds-grid { stroke: #1e293b; stroke-width: 1; stroke-dasharray: 2 2; }
              .ds-nyquist-noise { fill: none; stroke: #64748b; stroke-width: 1.5; stroke-dasharray: 3 3; }
              .ds-shaped-noise { fill: none; stroke: #f59e0b; stroke-width: 2.5; }
              .ds-signal { stroke: #38bdf8; stroke-width: 3.5; }
              .ds-filter-band { fill: #10b981; fill-opacity: 0.15; stroke: #10b981; stroke-width: 1; stroke-dasharray: 2 2; }
              .ds-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .ds-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .ds-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"ds-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"ds-title\">📈 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"ds-meta\">OSR = {model.OversamplingRatio}x • N = {model.QuantizerBits}-bit • F_clk = {model.SamplingClockKhz/1000:F2}MHz • ENOB = {model.Enob:F1} bits (SNR = {model.TotalInBandSnrDb:F1}dB)</text>");

        // PSD Spectrum Axes on Left
        double x1 = 40;
        double x2 = 280;
        double y1 = 70;
        double y2 = 220;

        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{y2}\" x2=\"{x2}\" y2=\"{y2}\" class=\"ds-axis\" />");
        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x1}\" y2=\"{y2}\" class=\"ds-axis\" />");

        sb.AppendLine($"  <text x=\"{x2 - 35}\" y=\"{y2 + 16}\" font-family=\"monospace\" font-size=\"9\" fill=\"#64748b\">Freq (log)</text>");
        sb.AppendLine($"  <text x=\"{x1 - 10}\" y=\"{y1 - 6}\" font-family=\"monospace\" font-size=\"9\" fill=\"#64748b\">PSD (dB/Hz)</text>");

        // In-band audio cutoff band [0, fs/2]
        double xBw = x1 + 65; // ~22.05 kHz boundary
        sb.AppendLine($"  <rect x=\"{x1}\" y=\"{y1}\" width=\"{xBw - x1}\" height=\"{y2 - y1}\" class=\"ds-filter-band\" />");
        sb.AppendLine($"  <text x=\"{x1 + 6}\" y=\"{y1 + 14}\" font-family=\"monospace\" font-size=\"8\" fill=\"#10b981\">Audio Band (fs/2)</text>");

        // Unshaped Nyquist Quantization Noise (flat line)
        double yFlat = y2 - 60;
        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yFlat}\" x2=\"{x2}\" y2=\"{yFlat}\" class=\"ds-nyquist-noise\" />");
        sb.AppendLine($"  <text x=\"{x2 - 80}\" y=\"{yFlat - 4}\" font-family=\"Segoe UI, sans-serif\" font-size=\"8\" fill=\"#94a3b8\">Unshaped Noise</text>");

        // 1st-Order High-Pass Shaped Noise Curve: starts very low in audio band, rises at +20dB/dec
        var noiseCurve = new StringBuilder();
        noiseCurve.Append($"M {x1},{y2 - 10} ");
        noiseCurve.Append($"Q {x1 + 40},{y2 - 25} {xBw},{y2 - 65} ");
        noiseCurve.Append($"Q {xBw + 60},{y1 + 30} {x2},{y1 + 10}");
        sb.AppendLine($"  <path d=\"{noiseCurve}\" class=\"ds-shaped-noise\" />");
        sb.AppendLine($"  <text x=\"{x2 - 80}\" y=\"{y1 + 25}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f59e0b\">+20dB/dec NTF</text>");

        // Test Signal Spike at fin
        double xSig = x1 + 25;
        sb.AppendLine($"  <line x1=\"{xSig}\" y1=\"{y2}\" x2=\"{xSig}\" y2=\"{y1 + 20}\" class=\"ds-signal\" />");
        sb.AppendLine($"  <text x=\"{xSig - 10}\" y=\"{y1 + 14}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#38bdf8\">f_in</text>");

        // Results Card on Right
        double cardX = 295;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"220\" height=\"205\" rx=\"6\" class=\"ds-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"ds-lbl\">Effective Resolution (ENOB):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"ds-val\" font-size=\"14\" fill=\"#38bdf8\">ENOB = {model.Enob:F1} Bits</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"ds-lbl\">Total In-Band Dynamic Range (SNR):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"ds-val\" fill=\"#10b981\">SNR = {model.TotalInBandSnrDb:F1} dB</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"ds-lbl\">Noise Shaping Gain (ΔSNR):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"ds-val\" fill=\"#fbbf24\">+{model.NoiseShapingSnrGainDb:F1} dB (9 dB/oct)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"ds-lbl\">Modulator Clock Rate (F_clk):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"ds-val\">{model.SamplingClockKhz/1000:F3} MHz ({model.OversamplingRatio}x fs)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"ds-lbl\">Quantizer Configuration:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" font-family=\"monospace\" font-size=\"11\" fill=\"#94a3b8\">{model.QuantizerBits}-Bit Comparator (1st-Order Mod-1)</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
