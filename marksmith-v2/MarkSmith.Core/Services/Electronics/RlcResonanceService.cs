using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class RlcResonanceModel
{
    public string Title { get; set; } = "Series RLC Resonant Tank Circuit";
    public double ResistanceOhms { get; set; } = 10.0;     // R (Ohms)
    public double InductanceHenry { get; set; } = 0.001;   // L (H) (e.g. 1 mH)
    public double CapacitanceFarads { get; set; } = 1e-7;  // C (F) (e.g. 100 nF)
    public string Topology { get; set; } = "series";

    // Resonant Frequency f0 = 1 / (2 * pi * sqrt(L * C))
    public double ResonantFreqHz => 1.0 / (2.0 * Math.PI * Math.Sqrt(InductanceHenry * CapacitanceFarads));

    // Characteristic Impedance Z0 = sqrt(L / C)
    public double CharacteristicZ0 => Math.Sqrt(InductanceHenry / CapacitanceFarads);

    // Quality Factor Q = (omega0 * L) / R for series, or R / (omega0 * L) for parallel
    public double QualityFactor => Topology.Contains("par")
        ? ResistanceOhms / (2.0 * Math.PI * ResonantFreqHz * InductanceHenry)
        : (2.0 * Math.PI * ResonantFreqHz * InductanceHenry) / ResistanceOhms;

    // Bandwidth BW = f0 / Q
    public double BandwidthHz => ResonantFreqHz / Math.Max(0.01, QualityFactor);
}

public static class RlcResonanceService
{
    private static readonly Regex RlcFenceRegex = new(
        @":::(?:rlc|rlc-resonance|resonant-circuit)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RRegex = new(
        @"(?:r|resistance)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[oO]hm|Ω)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LRegex = new(
        @"(?:l|inductance)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[uUmM]?[hH])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CRegex = new(
        @"(?:c|capacitance)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[pPnNuU]?[fF])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RlcResonanceModel ParseRlc(string blockText, string defaultTitle = "Series RLC Resonant Tank Circuit")
    {
        var model = new RlcResonanceModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = RlcFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var rm = RRegex.Match(header);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r))
                model.ResistanceOhms = Math.Clamp(r, 0.1, 100000.0);

            var lm = LRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double l))
            {
                if (header.Contains("mH") || header.Contains("mh")) l *= 1e-3;
                else if (header.Contains("uH") || header.Contains("uh") || header.Contains("µH")) l *= 1e-6;
                model.InductanceHenry = Math.Clamp(l, 1e-9, 10.0);
            }

            var cm = CRegex.Match(header);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
            {
                if (header.Contains("nF") || header.Contains("nf")) c *= 1e-9;
                else if (header.Contains("uF") || header.Contains("uf") || header.Contains("µF")) c *= 1e-6;
                else if (header.Contains("pF") || header.Contains("pf")) c *= 1e-12;
                model.CapacitanceFarads = Math.Clamp(c, 1e-13, 1.0);
            }

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var rm = RRegex.Match(l);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r))
                model.ResistanceOhms = Math.Clamp(r, 0.1, 100000.0);

            var lm = LRegex.Match(l);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ind))
            {
                if (l.Contains("mH") || l.Contains("mh")) ind *= 1e-3;
                else if (l.Contains("uH") || l.Contains("uh") || l.Contains("µH")) ind *= 1e-6;
                model.InductanceHenry = Math.Clamp(ind, 1e-9, 10.0);
            }

            var cm = CRegex.Match(l);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cap))
            {
                if (l.Contains("nF") || l.Contains("nf")) cap *= 1e-9;
                else if (l.Contains("uF") || l.Contains("uf") || l.Contains("µF")) cap *= 1e-6;
                else if (l.Contains("pF") || l.Contains("pf")) cap *= 1e-12;
                model.CapacitanceFarads = Math.Clamp(cap, 1e-13, 1.0);
            }
        }

        return model;
    }

    public static string RenderRlcSvg(RlcResonanceModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double oy = 230;
        double plotW = 240;
        double plotH = 150;

        double f0 = model.ResonantFreqHz;
        double minF = f0 * 0.5;
        double maxF = f0 * 1.5;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-rlc-svg\">");
        sb.AppendLine("""
            <style>
              .rc-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .rc-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .rc-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .rc-axis { stroke: #475569; stroke-width: 1.2; }
              .rc-grid { stroke: #1e293b; stroke-width: 1; stroke-dasharray: 2 2; }
              .rc-curve { fill: none; stroke: #38bdf8; stroke-width: 2.2; }
              .rc-f0-line { stroke: #fbbf24; stroke-width: 1.2; stroke-dasharray: 3 3; }
              .rc-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .rc-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .rc-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"rc-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"rc-title\">⚡ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"rc-meta\">R = {model.ResistanceOhms:F0} Ω • f0 = {f0 / 1000.0:F2} kHz • Q = {model.QualityFactor:F1}</text>");

        // Coordinate Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + plotW + 15}\" y2=\"{oy}\" class=\"rc-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - plotH - 10}\" class=\"rc-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + plotW + 10}\" y=\"{oy + 14}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">f</text>");
        sb.AppendLine($"  <text x=\"{ox - 8}\" y=\"{oy - plotH - 4}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\" text-anchor=\"end\">|I(f)|</text>");

        // Draw Resonance Bell Curve (Normalized Current vs Frequency)
        var path = new StringBuilder();
        int steps = 60;
        double Q = Math.Max(0.5, model.QualityFactor);

        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            double f = minF + t * (maxF - minF);
            double delta = (f / f0) - (f0 / f);
            double mag = 1.0 / Math.Sqrt(1.0 + Math.Pow(Q * delta, 2)); // Normalized resonant peak

            double px = ox + t * plotW;
            double py = oy - mag * plotH;

            if (i == 0) path.Append($"M {px:F1} {py:F1}");
            else path.Append($" L {px:F1} {py:F1}");
        }

        sb.AppendLine($"  <path d=\"{path}\" class=\"rc-curve\" />");

        // Resonant f0 Center Marker Line
        double f0X = ox + 0.5 * plotW;
        sb.AppendLine($"  <line x1=\"{f0X:F1}\" y1=\"{oy}\" x2=\"{f0X:F1}\" y2=\"{oy - plotH}\" class=\"rc-f0-line\" />");
        sb.AppendLine($"  <circle cx=\"{f0X:F1}\" cy=\"{oy - plotH}\" r=\"4\" fill=\"#fbbf24\" stroke=\"#ffffff\" stroke-width=\"1.5\" />");
        sb.AppendLine($"  <text x=\"{f0X:F1}\" y=\"{oy + 14}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#fbbf24\" text-anchor=\"middle\">f₀</text>");

        // Results Card on Right
        double cardX = 310;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"170\" height=\"195\" rx=\"6\" class=\"rc-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"rc-lbl\">Resonant Frequency (f₀):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"rc-val\">{f0:F1} Hz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"rc-lbl\">Quality Factor (Q):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"rc-val\" fill=\"#10b981\">Q = {model.QualityFactor:F2}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"rc-lbl\">-3dB Bandwidth (BW):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"rc-val\">{model.BandwidthHz:F1} Hz</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"rc-lbl\">Characteristic (Z₀):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"rc-val\" fill=\"#fbbf24\">{model.CharacteristicZ0:F1} Ω</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">LC Tank Resonance Mode</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
