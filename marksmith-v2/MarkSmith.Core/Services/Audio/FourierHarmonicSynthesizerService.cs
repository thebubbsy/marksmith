using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Audio;

public class FourierModel
{
    public string Title { get; set; } = "Fourier Series Harmonic Synthesizer";
    public string WaveformType { get; set; } = "square"; // "square", "sawtooth", "triangle"
    public int HarmonicsCount { get; set; } = 7;
    public double FrequencyHz { get; set; } = 100.0;

    public double EvaluatePartialSum(double tNormalized)
    {
        double sum = 0.0;
        double w = 2.0 * Math.PI * tNormalized;

        if (WaveformType.Contains("saw"))
        {
            // Sawtooth: (2/pi) * sum_{k=1}^N ((-1)^(k+1) / k) * sin(k*w)
            for (int k = 1; k <= HarmonicsCount; k++)
            {
                double coeff = (2.0 / Math.PI) * (Math.Pow(-1, k + 1) / k);
                sum += coeff * Math.Sin(k * w);
            }
        }
        else if (WaveformType.Contains("tri"))
        {
            // Triangle: (8/pi^2) * sum_{n=0}^{N-1} ((-1)^n / (2n+1)^2) * sin((2n+1)*w)
            for (int n = 0; n < HarmonicsCount; n++)
            {
                int k = 2 * n + 1;
                double coeff = (8.0 / (Math.PI * Math.PI)) * (Math.Pow(-1, n) / (k * k));
                sum += coeff * Math.Sin(k * w);
            }
        }
        else
        {
            // Square: (4/pi) * sum_{n=0}^{N-1} (1 / (2n+1)) * sin((2n+1)*w)
            for (int n = 0; n < HarmonicsCount; n++)
            {
                int k = 2 * n + 1;
                double coeff = (4.0 / Math.PI) * (1.0 / k);
                sum += coeff * Math.Sin(k * w);
            }
        }

        return sum;
    }
}

public static class FourierHarmonicSynthesizerService
{
    private static readonly Regex FourierFenceRegex = new(
        @":::(?:fourier|fourier-series|harmonic-synth)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TypeRegex = new(
        @"type\s*[:=]\s*""?([a-zA-Z0-9_\-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HarmonicsRegex = new(
        @"(?:harmonics|order|n)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FreqRegex = new(
        @"(?:freq|frequency)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[hH]?[zZ]?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static FourierModel ParseFourier(string blockText, string defaultTitle = "Fourier Series Harmonic Synthesizer")
    {
        var model = new FourierModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = FourierFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var ty = TypeRegex.Match(header);
            if (ty.Success) model.WaveformType = ty.Groups[1].Value.ToLowerInvariant();

            var hm = HarmonicsRegex.Match(header);
            if (hm.Success && int.TryParse(hm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hc))
                model.HarmonicsCount = Math.Clamp(hc, 1, 30);

            var fm = FreqRegex.Match(header);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double f))
                model.FrequencyHz = Math.Clamp(f, 1.0, 20000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var ty = TypeRegex.Match(l);
            if (ty.Success) model.WaveformType = ty.Groups[1].Value.ToLowerInvariant();

            var hm = HarmonicsRegex.Match(l);
            if (hm.Success && int.TryParse(hm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hc))
                model.HarmonicsCount = Math.Clamp(hc, 1, 30);

            var fm = FreqRegex.Match(l);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double f))
                model.FrequencyHz = Math.Clamp(f, 1.0, 20000.0);
        }

        return model;
    }

    public static string RenderFourierSvg(FourierModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 40;
        double oy = 150;
        double waveW = 240;
        double waveAmp = 60;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-fourier-svg\">");
        sb.AppendLine("""
            <style>
              .fo-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .fo-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .fo-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .fo-axis { stroke: #475569; stroke-width: 1.2; }
              .fo-grid { stroke: #1e293b; stroke-width: 1; stroke-dasharray: 3 3; }
              .fo-wave { fill: none; stroke: #38bdf8; stroke-width: 2.2; }
              .fo-spec-bar { fill: #ec4899; }
              .fo-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .fo-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .fo-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"fo-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"fo-title\">🎹 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"fo-meta\">Target: {model.WaveformType.ToUpperInvariant()} • N = {model.HarmonicsCount} Harmonics (Gibbs Ringing)</text>");

        // Time domain wave axis
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + waveW}\" y2=\"{oy}\" class=\"fo-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy - waveAmp - 10}\" x2=\"{ox}\" y2=\"{oy + waveAmp + 10}\" class=\"fo-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy - waveAmp}\" x2=\"{ox + waveW}\" y2=\"{oy - waveAmp}\" class=\"fo-grid\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy + waveAmp}\" x2=\"{ox + waveW}\" y2=\"{oy + waveAmp}\" class=\"fo-grid\" />");

        // Synthesize Time-Domain Waveform across 2 periods
        int samples = 140;
        var path = new StringBuilder();
        for (int i = 0; i <= samples; i++)
        {
            double t = (i / (double)samples) * 2.0; // 0 to 2.0 periods
            double val = model.EvaluatePartialSum(t);

            double px = ox + (i / (double)samples) * waveW;
            double py = oy - val * waveAmp;

            if (i == 0) path.Append($"M {px:F1} {py:F1}");
            else path.Append($" L {px:F1} {py:F1}");
        }

        sb.AppendLine($"  <path d=\"{path}\" class=\"fo-wave\" />");

        // Frequency-Domain Harmonic Spectrum Bar Chart on Right
        double specX = 310;
        double specY = 65;
        sb.AppendLine($"  <rect x=\"{specX}\" y=\"{specY}\" width=\"170\" height=\"185\" rx=\"6\" class=\"fo-card-bg\" />");
        sb.AppendLine($"  <text x=\"{specX + 12}\" y=\"{specY + 20}\" class=\"fo-lbl\" font-weight=\"700\" fill=\"#f8fafc\">Harmonic Spectrum:</text>");

        int maxBars = Math.Min(model.HarmonicsCount, 7);
        double barBaseY = specY + 160;
        double barWidth = 14;
        double barGap = 6;

        for (int i = 0; i < maxBars; i++)
        {
            double k = model.WaveformType.Contains("saw") ? (i + 1) : (2 * i + 1);
            double amp = model.WaveformType.Contains("tri") ? (1.0 / (k * k)) : (1.0 / k);
            double barH = amp * 100.0;
            double bx = specX + 18 + i * (barWidth + barGap);
            double by = barBaseY - barH;

            sb.AppendLine($"  <rect x=\"{bx:F1}\" y=\"{by:F1}\" width=\"{barWidth}\" height=\"{barH:F1}\" rx=\"2\" class=\"fo-spec-bar\" />");
            sb.AppendLine($"  <text x=\"{bx + barWidth / 2:F1}\" y=\"{barBaseY + 12}\" font-family=\"monospace\" font-size=\"8\" fill=\"#94a3b8\" text-anchor=\"middle\">{k}f₀</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
