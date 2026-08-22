using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class SuperhetReceiverModel
{
    public string Title { get; set; } = "Superheterodyne RF Receiver Mixer & IF";
    public double RfFrequencyMhz { get; set; } = 100.0; // Desired RF carrier (MHz)
    public double IfFrequencyMhz { get; set; } = 10.7;  // Intermediate Frequency (MHz)
    public string LoSide { get; set; } = "high";       // "high" or "low" side LO
    public double PreselectorQ { get; set; } = 45.0;   // Front-end RF filter Q

    // Local Oscillator Frequency
    public double LoFrequencyMhz => LoSide.Equals("low", StringComparison.OrdinalIgnoreCase)
        ? Math.Max(0.1, RfFrequencyMhz - IfFrequencyMhz)
        : RfFrequencyMhz + IfFrequencyMhz;

    // Unwanted Image Frequency
    public double ImageFrequencyMhz => LoSide.Equals("low", StringComparison.OrdinalIgnoreCase)
        ? Math.Max(0.1, RfFrequencyMhz - 2.0 * IfFrequencyMhz)
        : RfFrequencyMhz + 2.0 * IfFrequencyMhz;

    // Image Rejection Ratio (IRR in dB)
    public double ImageRejectionRatioDb
    {
        get
        {
            double fRf = RfFrequencyMhz;
            double fImg = ImageFrequencyMhz;
            if (fRf <= 0 || fImg <= 0) return 0.0;
            double rho = (fImg / fRf) - (fRf / fImg);
            double val = 1.0 + Math.Pow(PreselectorQ, 2) * Math.Pow(rho, 2);
            return 10.0 * Math.Log10(Math.Max(1.0, val));
        }
    }
}

public static class SuperhetReceiverService
{
    private static readonly Regex SuperhetFenceRegex = new(
        @":::(?:superhet-receiver|superhet|rf-mixer)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RfRegex = new(
        @"(?:f_rf|rf_freq|rf)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IfRegex = new(
        @"(?:f_if|if_freq|if)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][hH][zZ])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LoSideRegex = new(
        @"(?:lo_side|injection|lo)\s*[:=]\s*""?([a-zA-Z]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QRegex = new(
        @"(?:q_filter|q|q_rf)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SuperhetReceiverModel ParseSuperhet(string blockText, string defaultTitle = "Superheterodyne RF Receiver Mixer & IF")
    {
        var model = new SuperhetReceiverModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = SuperhetFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var rfm = RfRegex.Match(header);
            if (rfm.Success && double.TryParse(rfm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rf))
                model.RfFrequencyMhz = Math.Clamp(rf, 0.1, 10000.0);

            var ifm = IfRegex.Match(header);
            if (ifm.Success && double.TryParse(ifm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ifreq))
                model.IfFrequencyMhz = Math.Clamp(ifreq, 0.01, 1000.0);

            var lom = LoSideRegex.Match(header);
            if (lom.Success) model.LoSide = lom.Groups[1].Value.ToLowerInvariant();

            var qm = QRegex.Match(header);
            if (qm.Success && double.TryParse(qm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double q))
                model.PreselectorQ = Math.Clamp(q, 1.0, 500.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var rfm = RfRegex.Match(l);
            if (rfm.Success && double.TryParse(rfm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rf))
                model.RfFrequencyMhz = Math.Clamp(rf, 0.1, 10000.0);

            var ifm = IfRegex.Match(l);
            if (ifm.Success && double.TryParse(ifm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ifreq))
                model.IfFrequencyMhz = Math.Clamp(ifreq, 0.01, 1000.0);

            var lom = LoSideRegex.Match(l);
            if (lom.Success) model.LoSide = lom.Groups[1].Value.ToLowerInvariant();

            var qm = QRegex.Match(l);
            if (qm.Success && double.TryParse(qm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double q))
                model.PreselectorQ = Math.Clamp(q, 1.0, 500.0);
        }

        return model;
    }

    public static string RenderSuperhetSvg(SuperhetReceiverModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-superhet-svg\">");
        sb.AppendLine("""
            <style>
              .sh-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .sh-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .sh-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .sh-axis { stroke: #64748b; stroke-width: 1.5; }
              .sh-filter { fill: none; stroke: #0284c7; stroke-width: 2; stroke-dasharray: 4 2; }
              .sh-rf-bar { stroke: #38bdf8; stroke-width: 4; }
              .sh-lo-bar { stroke: #fbbf24; stroke-width: 4; }
              .sh-img-bar { stroke: #f43f5e; stroke-width: 3; stroke-dasharray: 2 2; }
              .sh-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .sh-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .sh-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"sh-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"sh-title\">📻 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"sh-meta\">f_RF = {model.RfFrequencyMhz:F1}MHz • f_IF = {model.IfFrequencyMhz:F1}MHz • LO: {model.LoSide.ToUpperInvariant()} • IRR = {model.ImageRejectionRatioDb:F1}dB</text>");

        // Spectrum Axis on Left
        double axisX1 = 30;
        double axisX2 = 280;
        double axisY = 220;
        sb.AppendLine($"  <line x1=\"{axisX1}\" y1=\"{axisY}\" x2=\"{axisX2}\" y2=\"{axisY}\" class=\"sh-axis\" />");
        sb.AppendLine($"  <text x=\"{axisX2 - 20}\" y=\"{axisY + 16}\" font-family=\"monospace\" font-size=\"9\" fill=\"#64748b\">Freq (f)</text>");

        // Frequency mapping
        double minF = Math.Min(model.RfFrequencyMhz, Math.Min(model.LoFrequencyMhz, model.ImageFrequencyMhz)) - model.IfFrequencyMhz * 1.5;
        double maxF = Math.Max(model.RfFrequencyMhz, Math.Max(model.LoFrequencyMhz, model.ImageFrequencyMhz)) + model.IfFrequencyMhz * 1.5;
        minF = Math.Max(0.0, minF);
        double rangeF = Math.Max(1.0, maxF - minF);

        double MapFreq(double f) => axisX1 + 20.0 + ((f - minF) / rangeF) * (axisX2 - axisX1 - 40.0);

        double xRf = MapFreq(model.RfFrequencyMhz);
        double xLo = MapFreq(model.LoFrequencyMhz);
        double xImg = MapFreq(model.ImageFrequencyMhz);

        // Pre-selector Filter Curve (Bandpass around f_rf)
        double bwPx = Math.Max(15.0, (axisX2 - axisX1) / (model.PreselectorQ / 5.0));
        var filterPath = new StringBuilder();
        filterPath.Append($"M {xRf - bwPx * 2:F1},{axisY} ");
        filterPath.Append($"Q {xRf - bwPx:F1},{axisY - 20} {xRf - bwPx * 0.4:F1},{axisY - 95} ");
        filterPath.Append($"Q {xRf:F1},{axisY - 110} {xRf + bwPx * 0.4:F1},{axisY - 95} ");
        filterPath.Append($"Q {xRf + bwPx:F1},{axisY - 20} {xRf + bwPx * 2:F1},{axisY}");
        sb.AppendLine($"  <path d=\"{filterPath}\" class=\"sh-filter\" />");
        sb.AppendLine($"  <text x=\"{xRf - 24:F1}\" y=\"{axisY - 116}\" font-family=\"Segoe UI, sans-serif\" font-size=\"8.5\" fill=\"#0284c7\">RF Filter (Q={model.PreselectorQ:F0})</text>");

        // Desired RF Carrier Tone (Height 100px)
        sb.AppendLine($"  <line x1=\"{xRf:F1}\" y1=\"{axisY}\" x2=\"{xRf:F1}\" y2=\"{axisY - 100}\" class=\"sh-rf-bar\" />");
        sb.AppendLine($"  <text x=\"{xRf - 14:F1}\" y=\"{axisY + 14}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#38bdf8\">f_RF</text>");
        sb.AppendLine($"  <text x=\"{xRf - 16:F1}\" y=\"{axisY + 25}\" font-family=\"monospace\" font-size=\"7.5\" fill=\"#94a3b8\">{model.RfFrequencyMhz:F1}</text>");

        // Local Oscillator Injection Tone (Height 120px)
        sb.AppendLine($"  <line x1=\"{xLo:F1}\" y1=\"{axisY}\" x2=\"{xLo:F1}\" y2=\"{axisY - 120}\" class=\"sh-lo-bar\" />");
        sb.AppendLine($"  <text x=\"{xLo - 12:F1}\" y=\"{axisY + 14}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#fbbf24\">f_LO</text>");
        sb.AppendLine($"  <text x=\"{xLo - 14:F1}\" y=\"{axisY + 25}\" font-family=\"monospace\" font-size=\"7.5\" fill=\"#94a3b8\">{model.LoFrequencyMhz:F1}</text>");

        // Image Frequency Tone (Height 40px - attenuated by filter)
        sb.AppendLine($"  <line x1=\"{xImg:F1}\" y1=\"{axisY}\" x2=\"{xImg:F1}\" y2=\"{axisY - 45}\" class=\"sh-img-bar\" />");
        sb.AppendLine($"  <text x=\"{xImg - 16:F1}\" y=\"{axisY + 14}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#f43f5e\">f_IMG</text>");
        sb.AppendLine($"  <text x=\"{xImg - 16:F1}\" y=\"{axisY + 25}\" font-family=\"monospace\" font-size=\"7.5\" fill=\"#94a3b8\">{model.ImageFrequencyMhz:F1}</text>");

        // Results Card on Right
        double cardX = 300;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"215\" height=\"205\" rx=\"6\" class=\"sh-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"sh-lbl\">Local Oscillator (LO) Frequency:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"sh-val\" font-size=\"14\" fill=\"#fbbf24\">f_LO = {model.LoFrequencyMhz:F2} MHz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"sh-lbl\">Intermediate Frequency (IF):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"sh-val\" fill=\"#10b981\">f_IF = {model.IfFrequencyMhz:F2} MHz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"sh-lbl\">Unwanted Image Frequency:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"sh-val\" fill=\"#f43f5e\">f_IMG = {model.ImageFrequencyMhz:F2} MHz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"sh-lbl\">Image Rejection Ratio (IRR):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"sh-val\" font-size=\"13\" fill=\"#38bdf8\">IRR = {model.ImageRejectionRatioDb:F1} dB</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"sh-lbl\">Mixer Downconversion:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" font-family=\"monospace\" font-size=\"10.5\" fill=\"#94a3b8\">|{model.RfFrequencyMhz:F1} - {model.LoFrequencyMhz:F1}| = {model.IfFrequencyMhz:F1} MHz</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
