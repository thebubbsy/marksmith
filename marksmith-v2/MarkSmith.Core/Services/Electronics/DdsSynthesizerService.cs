using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class DdsModel
{
    public string Title { get; set; } = "DDS NCO Phase Accumulator Generator";
    public double ClkFreqMhz { get; set; } = 100.0;       // f_clk (MHz)
    public int PhaseBitsN { get; set; } = 32;             // N bits (e.g. 32-bit accumulator)
    public long TuningWordM { get; set; } = 1073741824;   // M tuning word (e.g. 2^30 -> 25 MHz)
    public int DacBitsB { get; set; } = 12;               // B bits (12-bit DAC)

    // Output Frequency f_out = (M * f_clk) / 2^N in MHz
    public double OutputFreqMhz => (TuningWordM * ClkFreqMhz) / Math.Pow(2, PhaseBitsN);

    // Frequency Resolution Delta f = f_clk / 2^N in Hz
    public double FreqResolutionHz => (ClkFreqMhz * 1e6) / Math.Pow(2, PhaseBitsN);

    // Theoretical SFDR approx 6.02 * B dB
    public double SfdrDb => 6.02 * DacBitsB + 1.76;
}

public static class DdsSynthesizerService
{
    private static readonly Regex DdsFenceRegex = new(
        @":::(?:dds|dds-synth|nco)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ClkRegex = new(
        @"(?:f_clk|clk|clock)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NBitsRegex = new(
        @"(?:n_bits|\bn\b|acc_bits)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MWordRegex = new(
        @"(?:m_word|\bm\b|tuning_word)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DacBitsRegex = new(
        @"(?:dac_bits|dac|\bb\b)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static DdsModel ParseDds(string blockText, string defaultTitle = "DDS NCO Phase Accumulator Generator")
    {
        var model = new DdsModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = DdsFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var clkm = ClkRegex.Match(header);
            if (clkm.Success && double.TryParse(clkm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double clk))
                model.ClkFreqMhz = Math.Clamp(clk, 0.1, 10000.0);

            var nbm = NBitsRegex.Match(header);
            if (nbm.Success && int.TryParse(nbm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nb))
                model.PhaseBitsN = Math.Clamp(nb, 8, 64);

            var mwm = MWordRegex.Match(header);
            if (mwm.Success && long.TryParse(mwm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long mw))
                model.TuningWordM = Math.Clamp(mw, 1, (long)Math.Pow(2, model.PhaseBitsN) - 1);

            var dbm = DacBitsRegex.Match(header);
            if (dbm.Success && int.TryParse(dbm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int db))
                model.DacBitsB = Math.Clamp(db, 4, 24);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var clkm = ClkRegex.Match(l);
            if (clkm.Success && double.TryParse(clkm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double clk))
                model.ClkFreqMhz = Math.Clamp(clk, 0.1, 10000.0);

            var nbm = NBitsRegex.Match(l);
            if (nbm.Success && int.TryParse(nbm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nb))
                model.PhaseBitsN = Math.Clamp(nb, 8, 64);

            var mwm = MWordRegex.Match(l);
            if (mwm.Success && long.TryParse(mwm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long mw))
                model.TuningWordM = Math.Clamp(mw, 1, (long)Math.Pow(2, model.PhaseBitsN) - 1);

            var dbm = DacBitsRegex.Match(l);
            if (dbm.Success && int.TryParse(dbm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int db))
                model.DacBitsB = Math.Clamp(db, 4, 24);
        }

        return model;
    }

    public static string RenderDdsSvg(DdsModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double oy = 220;
        double waveW = 240;
        double rampH = 70;
        double sineH = 60;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-dds-svg\">");
        sb.AppendLine("""
            <style>
              .dd-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .dd-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .dd-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .dd-axis { stroke: #475569; stroke-width: 1.2; }
              .dd-ramp { fill: none; stroke: #fbbf24; stroke-width: 2; }
              .dd-sine { fill: none; stroke: #38bdf8; stroke-width: 2.2; }
              .dd-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .dd-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .dd-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"dd-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"dd-title\">🎛 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"dd-meta\">f_clk = {model.ClkFreqMhz:F0}MHz • N = {model.PhaseBitsN}-bit • M = {model.TuningWordM} • f_out = {model.OutputFreqMhz:F2} MHz</text>");

        // Phase Accumulator Sawtooth Ramp Axes (Top track)
        double rampBaseY = oy - sineH - 30;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{rampBaseY}\" x2=\"{ox + waveW + 15}\" y2=\"{rampBaseY}\" class=\"dd-axis\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{rampBaseY - rampH / 2}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\" text-anchor=\"end\">θ[n]</text>");

        // Reconstructed DAC Sine Wave Axes (Bottom track)
        double sineCenterY = oy - sineH / 2.0;
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{sineCenterY}\" x2=\"{ox + waveW + 15}\" y2=\"{sineCenterY}\" class=\"dd-axis\" stroke-dasharray=\"2 2\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + waveW + 15}\" y2=\"{oy}\" class=\"dd-axis\" />");
        sb.AppendLine($"  <text x=\"{ox - 6}\" y=\"{sineCenterY + 3}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#38bdf8\" text-anchor=\"end\">DAC</text>");

        // Render 3 cycles of Phase Ramp and Quantized Sine Wave
        var rampPath = new StringBuilder();
        var sinePath = new StringBuilder();
        int cycles = 3;
        double cycleW = waveW / (double)cycles;

        for (int c = 0; c < cycles; c++)
        {
            double startX = ox + c * cycleW;
            double endX = startX + cycleW;

            // Sawtooth phase ramp (0 to 2pi)
            if (c == 0) rampPath.Append($"M {startX:F1} {rampBaseY} L {endX:F1} {rampBaseY - rampH} L {endX:F1} {rampBaseY} ");
            else rampPath.Append($"M {startX:F1} {rampBaseY} L {endX:F1} {rampBaseY - rampH} L {endX:F1} {rampBaseY} ");

            // Reconstructed Quantized Sine Wave
            int sineSteps = 24;
            for (int s = 0; s <= sineSteps; s++)
            {
                double t = s / (double)sineSteps;
                double px = startX + t * cycleW;
                double rad = t * 2.0 * Math.PI;
                double py = sineCenterY - Math.Sin(rad) * (sineH / 2.0 * 0.85);

                if (c == 0 && s == 0) sinePath.Append($"M {px:F1} {py:F1}");
                else sinePath.Append($" L {px:F1} {py:F1}");
            }
        }

        sb.AppendLine($"  <path d=\"{rampPath}\" class=\"dd-ramp\" />");
        sb.AppendLine($"  <path d=\"{sinePath}\" class=\"dd-sine\" />");

        // Results Card on Right
        double cardX = 310;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"170\" height=\"195\" rx=\"6\" class=\"dd-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"dd-lbl\">Synthesized Output:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"dd-val\" font-size=\"14\" fill=\"#10b981\">{model.OutputFreqMhz:F3} MHz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"dd-lbl\">Frequency Resolution (Δf):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"dd-val\">{model.FreqResolutionHz:F4} Hz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"dd-lbl\">DAC Resolution (B):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"dd-val\">{model.DacBitsB}-bit (2^{model.DacBitsB} codes)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"dd-lbl\">Theoretical SFDR:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"dd-val\" fill=\"#fbbf24\">SFDR ≈ {model.SfdrDb:F1} dBc</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">NCO Phase Accumulator</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
