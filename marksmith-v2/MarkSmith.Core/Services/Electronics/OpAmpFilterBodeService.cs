using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class FilterBodeModel
{
    public string Title { get; set; } = "Active Op-Amp Filter Bode Response";
    public string FilterType { get; set; } = "lowpass"; // "lowpass", "highpass", "bandpass", "sallen-key"
    public double CutoffFreqHz { get; set; } = 1000.0;   // fc (Hz)
    public double QualityFactorQ { get; set; } = 0.707;  // Butterworth Q = 1/sqrt(2)
    public double PassbandGainDb { get; set; } = 0.0;    // dB

    public double GetMagnitudeDb(double freqHz)
    {
        double wRatio = freqHz / CutoffFreqHz;
        if (FilterType.Contains("high"))
        {
            // 2nd order highpass: H(s) = (s/w0)^2 / ( (s/w0)^2 + (1/Q)(s/w0) + 1 )
            double num = Math.Pow(wRatio, 2);
            double denReal = 1.0 - Math.Pow(wRatio, 2);
            double denImag = wRatio / QualityFactorQ;
            double den = Math.Sqrt(denReal * denReal + denImag * denImag);
            return PassbandGainDb + 20.0 * Math.Log10(Math.Max(0.0001, num / den));
        }
        else if (FilterType.Contains("band"))
        {
            // Bandpass: H(s) = (1/Q)(s/w0) / ( (s/w0)^2 + (1/Q)(s/w0) + 1 )
            double num = wRatio / QualityFactorQ;
            double denReal = 1.0 - Math.Pow(wRatio, 2);
            double denImag = wRatio / QualityFactorQ;
            double den = Math.Sqrt(denReal * denReal + denImag * denImag);
            return PassbandGainDb + 20.0 * Math.Log10(Math.Max(0.0001, num / den));
        }
        else
        {
            // 2nd order lowpass / Sallen-Key: H(s) = 1 / ( (s/w0)^2 + (1/Q)(s/w0) + 1 )
            double denReal = 1.0 - Math.Pow(wRatio, 2);
            double denImag = wRatio / QualityFactorQ;
            double den = Math.Sqrt(denReal * denReal + denImag * denImag);
            return PassbandGainDb - 20.0 * Math.Log10(Math.Max(0.0001, den));
        }
    }
}

public static class OpAmpFilterBodeService
{
    private static readonly Regex FilterFenceRegex = new(
        @":::(?:filter|bode-plot|active-filter|bode)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TypeRegex = new(
        @"type\s*[:=]\s*""?([a-zA-Z0-9_\-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CutoffRegex = new(
        @"(?:cutoff|fc|freq)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK]?[hH]?[zZ]?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QRegex = new(
        @"(?:q|quality)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static FilterBodeModel ParseFilter(string blockText, string defaultTitle = "Active Op-Amp Filter Bode Response")
    {
        var model = new FilterBodeModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = FilterFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var ty = TypeRegex.Match(header);
            if (ty.Success) model.FilterType = ty.Groups[1].Value.ToLowerInvariant();

            var cm = CutoffRegex.Match(header);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fc))
            {
                if (header.Contains("kHz") || header.Contains("khz")) fc *= 1000;
                model.CutoffFreqHz = Math.Clamp(fc, 10.0, 1000000.0);
            }

            var qm = QRegex.Match(header);
            if (qm.Success && double.TryParse(qm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double q))
                model.QualityFactorQ = Math.Clamp(q, 0.1, 10.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var ty = TypeRegex.Match(l);
            if (ty.Success) model.FilterType = ty.Groups[1].Value.ToLowerInvariant();

            var cm = CutoffRegex.Match(l);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fc))
            {
                if (l.Contains("kHz") || l.Contains("khz")) fc *= 1000;
                model.CutoffFreqHz = Math.Clamp(fc, 10.0, 1000000.0);
            }

            var qm = QRegex.Match(l);
            if (qm.Success && double.TryParse(qm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double q))
                model.QualityFactorQ = Math.Clamp(q, 0.1, 10.0);
        }

        return model;
    }

    public static string RenderFilterBodeSvg(FilterBodeModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 60;
        double oy = 230;
        double axisW = 400;
        double axisH = 160;

        double minFreq = model.CutoffFreqHz / 100.0;
        double maxFreq = model.CutoffFreqHz * 100.0;
        double logMin = Math.Log10(minFreq);
        double logMax = Math.Log10(maxFreq);

        double minDb = -40.0;
        double maxDb = 10.0;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-bode-svg\">");
        sb.AppendLine("""
            <style>
              .bo-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .bo-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .bo-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .bo-axis { stroke: #475569; stroke-width: 1.2; }
              .bo-grid { stroke: #1e293b; stroke-width: 1; stroke-dasharray: 2 2; }
              .bo-curve { fill: none; stroke: #38bdf8; stroke-width: 2.2; }
              .bo-cutoff { stroke: #f43f5e; stroke-width: 1.2; stroke-dasharray: 4 2; }
              .bo-label { font-family: monospace; font-size: 9px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"bo-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"bo-title\">🎚 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"bo-meta\">{model.FilterType.ToUpperInvariant()} • fc = {model.CutoffFreqHz:F0} Hz • Q = {model.QualityFactorQ:F3} (-3dB Knee)</text>");

        // Horizontal dB grid lines
        for (double db = minDb; db <= maxDb; db += 10.0)
        {
            double gy = oy - ((db - minDb) / (maxDb - minDb)) * axisH;
            sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{gy:F1}\" x2=\"{ox + axisW}\" y2=\"{gy:F1}\" class=\"bo-grid\" />");
            string dbStr = db > 0 ? $"+{db:F0}" : $"{db:F0}";
            sb.AppendLine($"  <text x=\"{ox - 8}\" y=\"{gy + 3:F1}\" class=\"bo-label\" text-anchor=\"end\">{dbStr}dB</text>");
        }

        // Vertical Logarithmic Decades Grid
        for (int dec = (int)Math.Floor(logMin); dec <= (int)Math.Ceiling(logMax); dec++)
        {
            double f = Math.Pow(10, dec);
            if (f >= minFreq && f <= maxFreq)
            {
                double gx = ox + ((dec - logMin) / (logMax - logMin)) * axisW;
                sb.AppendLine($"  <line x1=\"{gx:F1}\" y1=\"{oy}\" x2=\"{gx:F1}\" y2=\"{oy - axisH}\" class=\"bo-grid\" />");
                string fStr = f >= 1000 ? $"{f / 1000:F0}k" : $"{f:F0}";
                sb.AppendLine($"  <text x=\"{gx:F1}\" y=\"{oy + 14}\" class=\"bo-label\" text-anchor=\"middle\">{fStr}Hz</text>");
            }
        }

        // Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + axisW + 10}\" y2=\"{oy}\" class=\"bo-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - axisH - 10}\" class=\"bo-axis\" />");

        // Cutoff Frequency Line
        double fcX = ox + ((Math.Log10(model.CutoffFreqHz) - logMin) / (logMax - logMin)) * axisW;
        sb.AppendLine($"  <line x1=\"{fcX:F1}\" y1=\"{oy}\" x2=\"{fcX:F1}\" y2=\"{oy - axisH}\" class=\"bo-cutoff\" />");
        sb.AppendLine($"  <text x=\"{fcX + 4:F1}\" y=\"{oy - axisH + 12}\" font-family=\"monospace\" font-size=\"9\" fill=\"#f43f5e\">fc (-3dB)</text>");

        // Plot Bode Magnitude Curve
        int points = 120;
        var path = new StringBuilder();
        for (int i = 0; i <= points; i++)
        {
            double t = i / (double)points;
            double logF = logMin + t * (logMax - logMin);
            double freq = Math.Pow(10, logF);
            double magDb = model.GetMagnitudeDb(freq);

            double px = ox + t * axisW;
            double py = oy - ((Math.Clamp(magDb, minDb, maxDb) - minDb) / (maxDb - minDb)) * axisH;

            if (i == 0) path.Append($"M {px:F1} {py:F1}");
            else path.Append($" L {px:F1} {py:F1}");
        }

        sb.AppendLine($"  <path d=\"{path}\" class=\"bo-curve\" />");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
