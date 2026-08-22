using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Audio;

public class SallenKeyFilterModel
{
    public string Title { get; set; } = "Sallen-Key 2nd-Order Active Filter";
    public string FilterType { get; set; } = "lowpass"; // "lowpass" or "highpass"
    public double ResistorR1Kohm { get; set; } = 10.0;  // R1 (kohm)
    public double ResistorR2Kohm { get; set; } = 10.0;  // R2 (kohm)
    public double CapacitorC1Nf { get; set; } = 22.0;   // C1 (nF)
    public double CapacitorC2Nf { get; set; } = 10.0;   // C2 (nF)
    public double OpAmpGainK { get; set; } = 1.0;       // K (gain)

    // Base units
    public double R1Ohms => ResistorR1Kohm * 1000.0;
    public double R2Ohms => ResistorR2Kohm * 1000.0;
    public double C1Farads => CapacitorC1Nf * 1e-9;
    public double C2Farads => CapacitorC2Nf * 1e-9;

    // Cutoff frequency f0 (Hz) = 1 / (2 * pi * sqrt(R1 * R2 * C1 * C2))
    public double CutoffFreqHz
    {
        get
        {
            double denom = 2.0 * Math.PI * Math.Sqrt(Math.Max(1e-24, R1Ohms * R2Ohms * C1Farads * C2Farads));
            return 1.0 / Math.Max(1e-12, denom);
        }
    }

    public double CutoffFreqKhz => CutoffFreqHz / 1000.0;

    // Quality factor Q
    public double QualityFactorQ
    {
        get
        {
            double num = Math.Sqrt(Math.Max(1e-24, R1Ohms * R2Ohms * C1Farads * C2Farads));
            double denom;
            if (FilterType.Equals("highpass", StringComparison.OrdinalIgnoreCase))
            {
                denom = R1Ohms * C1Farads + R1Ohms * C2Farads + R2Ohms * C2Farads * (1.0 - OpAmpGainK);
            }
            else
            {
                denom = R1Ohms * C2Farads + R2Ohms * C2Farads + R1Ohms * C1Farads * (1.0 - OpAmpGainK);
            }
            return num / Math.Max(1e-12, denom);
        }
    }

    // Damping ratio zeta = 1 / (2 * Q)
    public double DampingRatio => 1.0 / Math.Max(1e-4, 2.0 * QualityFactorQ);

    // Passband Alignment
    public string Alignment => QualityFactorQ switch
    {
        > 0.85 => "Chebyshev (Peaked)",
        >= 0.68 and <= 0.73 => "Butterworth (Maximally Flat)",
        >= 0.55 and <= 0.60 => "Bessel (Linear Phase)",
        _ => "Custom Active 2nd-Order"
    };
}

public static class SallenKeyFilterService
{
    private static readonly Regex FilterFenceRegex = new(
        @":::(?:sallen-key|sallenkey-filter|sallen-key-filter)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TypeRegex = new(
        @"(?:\btype\b|\bfilter_type\b|\btopology\b)\s*[:=]\s*""?([a-zA-Z\-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex R1Regex = new(
        @"(?:\br1\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][oO]hm|kΩ|Ω)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex R2Regex = new(
        @"(?:\br2\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][oO]hm|kΩ|Ω)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex C1Regex = new(
        @"(?:\bc1\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[nN][fF]|µ[fF]|pF)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex C2Regex = new(
        @"(?:\bc2\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[nN][fF]|µ[fF]|pF)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GainRegex = new(
        @"(?:\bgain\b|\bk\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SallenKeyFilterModel ParseFilter(string blockText, string defaultTitle = "Sallen-Key 2nd-Order Active Filter")
    {
        var model = new SallenKeyFilterModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = FilterFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var typem = TypeRegex.Match(header);
            if (typem.Success) model.FilterType = typem.Groups[1].Value.ToLowerInvariant();

            var r1m = R1Regex.Match(header);
            if (r1m.Success && double.TryParse(r1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r1))
                model.ResistorR1Kohm = Math.Clamp(r1, 0.01, 1000.0);

            var r2m = R2Regex.Match(header);
            if (r2m.Success && double.TryParse(r2m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r2))
                model.ResistorR2Kohm = Math.Clamp(r2, 0.01, 1000.0);

            var c1m = C1Regex.Match(header);
            if (c1m.Success && double.TryParse(c1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c1))
                model.CapacitorC1Nf = Math.Clamp(c1, 0.001, 10000.0);

            var c2m = C2Regex.Match(header);
            if (c2m.Success && double.TryParse(c2m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c2))
                model.CapacitorC2Nf = Math.Clamp(c2, 0.001, 10000.0);

            var gm = GainRegex.Match(header);
            if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double g))
                model.OpAmpGainK = Math.Clamp(g, 1.0, 10.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var typem = TypeRegex.Match(l);
            if (typem.Success) model.FilterType = typem.Groups[1].Value.ToLowerInvariant();

            var r1m = R1Regex.Match(l);
            if (r1m.Success && double.TryParse(r1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r1))
                model.ResistorR1Kohm = Math.Clamp(r1, 0.01, 1000.0);

            var r2m = R2Regex.Match(l);
            if (r2m.Success && double.TryParse(r2m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r2))
                model.ResistorR2Kohm = Math.Clamp(r2, 0.01, 1000.0);

            var c1m = C1Regex.Match(l);
            if (c1m.Success && double.TryParse(c1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c1))
                model.CapacitorC1Nf = Math.Clamp(c1, 0.001, 10000.0);

            var c2m = C2Regex.Match(l);
            if (c2m.Success && double.TryParse(c2m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c2))
                model.CapacitorC2Nf = Math.Clamp(c2, 0.001, 10000.0);

            var gm = GainRegex.Match(l);
            if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double g))
                model.OpAmpGainK = Math.Clamp(g, 1.0, 10.0);
        }

        return model;
    }

    public static string RenderFilterSvg(SallenKeyFilterModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-sallenkey-svg\">");
        sb.AppendLine("""
            <style>
              .sk-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .sk-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .sk-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .sk-axis { stroke: #64748b; stroke-width: 1.5; }
              .sk-bode { fill: none; stroke: #38bdf8; stroke-width: 2.5; }
              .sk-cutoff-line { stroke: #f43f5e; stroke-width: 1.2; stroke-dasharray: 3 3; }
              .sk-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .sk-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .sk-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"sk-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"sk-title\">🎛️ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"sk-meta\">{model.FilterType.ToUpperInvariant()} • f0 = {model.CutoffFreqKhz:F2}kHz • Q = {model.QualityFactorQ:F2} • Alignment: {model.Alignment}</text>");

        // Frequency Response Axis on Left
        double x1 = 35;
        double x2 = 270;
        double yBase = 220;
        double yTop = 75;

        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yBase}\" x2=\"{x2}\" y2=\"{yBase}\" class=\"sk-axis\" />");
        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yTop}\" x2=\"{x1}\" y2=\"{yBase}\" class=\"sk-axis\" />");
        sb.AppendLine($"  <text x=\"{x2 - 35}\" y=\"{yBase + 16}\" font-family=\"monospace\" font-size=\"9\" fill=\"#64748b\">Freq (log)</text>");
        sb.AppendLine($"  <text x=\"{x1 - 10}\" y=\"{yTop - 6}\" font-family=\"monospace\" font-size=\"9\" fill=\"#64748b\">|H(f)| dB</text>");

        // Cutoff Marker
        double xCutoff = x1 + (x2 - x1) * 0.55;
        sb.AppendLine($"  <line x1=\"{xCutoff}\" y1=\"{yTop}\" x2=\"{xCutoff}\" y2=\"{yBase}\" class=\"sk-cutoff-line\" />");
        sb.AppendLine($"  <text x=\"{xCutoff - 20:F1}\" y=\"{yTop + 14}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f43f5e\">f0={model.CutoffFreqKhz:F1}k</text>");

        // 2nd-Order Frequency Curve (-40 dB/dec)
        var respPath = new StringBuilder();
        if (model.FilterType.Equals("highpass", StringComparison.OrdinalIgnoreCase))
        {
            // High-pass: starts low, rises at +40dB/dec, levels off after cutoff
            respPath.Append($"M {x1 + 10},{yBase - 15} ");
            respPath.Append($"Q {xCutoff - 30},{yBase - 30} {xCutoff},{yTop + 25} ");
            respPath.Append($"Q {xCutoff + 30},{yTop + 20} {x2 - 10},{yTop + 20}");
        }
        else
        {
            // Low-pass: flat in passband, rolls off at -40dB/dec after cutoff
            double peakOffset = Math.Clamp((model.QualityFactorQ - 0.707) * 20.0, -5.0, 20.0);
            respPath.Append($"M {x1 + 10},{yTop + 20} ");
            respPath.Append($"L {xCutoff - 25},{yTop + 20} ");
            respPath.Append($"Q {xCutoff - 5},{yTop + 20 - peakOffset} {xCutoff},{yTop + 35} ");
            respPath.Append($"Q {xCutoff + 40},{yBase - 30} {x2 - 10},{yBase - 15}");
        }

        sb.AppendLine($"  <path d=\"{respPath}\" class=\"sk-bode\" />");
        sb.AppendLine($"  <text x=\"{x2 - 80}\" y=\"{yBase - 25}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#94a3b8\">-40 dB/dec</text>");

        // Results Card on Right
        double cardX = 285;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"225\" height=\"205\" rx=\"6\" class=\"sk-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"sk-lbl\">Cutoff Frequency (f0):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"sk-val\" font-size=\"14\" fill=\"#38bdf8\">f0 = {model.CutoffFreqKhz:F2} kHz ({model.CutoffFreqHz:F0} Hz)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"sk-lbl\">Filter Quality Factor (Q) & Damping (ζ):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"sk-val\" fill=\"#10b981\">Q = {model.QualityFactorQ:F2}  |  ζ = {model.DampingRatio:F2}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"sk-lbl\">Filter Alignment & Response:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"sk-val\" font-size=\"11.5\" fill=\"#fbbf24\">{model.Alignment}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"sk-lbl\">DC Passband Voltage Gain (K):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"sk-val\">K = {model.OpAmpGainK:F2} ({20*Math.Log10(model.OpAmpGainK):F1} dB)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"sk-lbl\">RC Passive Components:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" font-family=\"monospace\" font-size=\"10.5\" fill=\"#94a3b8\">R1={model.ResistorR1Kohm:F0}k, R2={model.ResistorR2Kohm:F0}k, C1={model.CapacitorC1Nf:F0}nF, C2={model.CapacitorC2Nf:F0}nF</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
