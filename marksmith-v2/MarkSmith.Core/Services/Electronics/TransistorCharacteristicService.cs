using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class BjtCurveModel
{
    public string Title { get; set; } = "BJT Output Characteristics (IC vs VCE)";
    public string TransistorType { get; set; } = "NPN"; // "NPN" or "PNP"
    public double Beta { get; set; } = 100.0;           // DC current gain hFE
    public double EarlyVoltageVa { get; set; } = 100.0; // VA (V)
    public double ThermalVoltageVt { get; set; } = 0.026; // VT = kT/q = 26mV
    public List<double> BaseCurrentsMicroAmps { get; } = new() { 10, 20, 30, 40, 50 };
}

public static class TransistorCharacteristicService
{
    private static readonly Regex BjtFenceRegex = new(
        @":::(?:bjt|transistor|bjt-curve)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TypeRegex = new(
        @"type\s*[:=]\s*""?([a-zA-Z0-9_\-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BetaRegex = new(
        @"(?:beta|hfe)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VaRegex = new(
        @"(?:va|early_voltage)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:V)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static BjtCurveModel ParseBjt(string blockText, string defaultTitle = "BJT Output Characteristics (IC vs VCE)")
    {
        var model = new BjtCurveModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = BjtFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var ty = TypeRegex.Match(header);
            if (ty.Success) model.TransistorType = ty.Groups[1].Value.ToUpperInvariant();

            var bm = BetaRegex.Match(header);
            if (bm.Success && double.TryParse(bm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double b))
                model.Beta = Math.Clamp(b, 10.0, 1000.0);

            var vm = VaRegex.Match(header);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double va))
                model.EarlyVoltageVa = Math.Clamp(va, 10.0, 500.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var ty = TypeRegex.Match(l);
            if (ty.Success) model.TransistorType = ty.Groups[1].Value.ToUpperInvariant();

            var bm = BetaRegex.Match(l);
            if (bm.Success && double.TryParse(bm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double b))
                model.Beta = Math.Clamp(b, 10.0, 1000.0);

            var vm = VaRegex.Match(l);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double va))
                model.EarlyVoltageVa = Math.Clamp(va, 10.0, 500.0);
        }

        return model;
    }

    public static string RenderBjtSvg(BjtCurveModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 60;
        double oy = 230;
        double axisW = 380;
        double axisH = 160;

        double maxVce = 10.0; // Volts
        double maxIc = (model.Beta * model.BaseCurrentsMicroAmps.Max() * 1e-6 * (1.0 + maxVce / model.EarlyVoltageVa)) * 1000.0 * 1.15; // mA

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-bjt-svg\">");
        sb.AppendLine("""
            <style>
              .bjt-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .bjt-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .bjt-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .bjt-axis { stroke: #475569; stroke-width: 1.5; }
              .bjt-grid { stroke: #1e293b; stroke-width: 1; stroke-dasharray: 2 2; }
              .bjt-curve { fill: none; stroke: #38bdf8; stroke-width: 2; }
              .bjt-sat-zone { fill: #f43f5e; fill-opacity: 0.08; }
              .bjt-label { font-family: monospace; font-size: 9.5px; fill: #94a3b8; }
              .bjt-ib-tag { font-family: monospace; font-size: 9px; font-weight: 700; fill: #38bdf8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"bjt-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"bjt-title\">⚡ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"bjt-meta\">{model.TransistorType} • β (hFE) = {model.Beta:F0} • Early Voltage VA = {model.EarlyVoltageVa:F0}V</text>");

        // Saturation region background shading (Vce < 0.6V)
        double satW = (0.7 / maxVce) * axisW;
        sb.AppendLine($"  <rect x=\"{ox}\" y=\"{oy - axisH}\" width=\"{satW:F1}\" height=\"{axisH}\" class=\"bjt-sat-zone\" />");
        sb.AppendLine($"  <text x=\"{ox + 4}\" y=\"{oy - axisH + 14}\" font-family=\"Segoe UI, sans-serif\" font-size=\"8.5\" fill=\"#f43f5e\">Saturation</text>");
        sb.AppendLine($"  <text x=\"{ox + satW + 15}\" y=\"{oy - axisH + 14}\" font-family=\"Segoe UI, sans-serif\" font-size=\"8.5\" fill=\"#38bdf8\">Active Region</text>");

        // Grid lines & Axis ticks
        for (int v = 2; v <= 10; v += 2)
        {
            double gx = ox + (v / maxVce) * axisW;
            sb.AppendLine($"  <line x1=\"{gx:F1}\" y1=\"{oy}\" x2=\"{gx:F1}\" y2=\"{oy - axisH}\" class=\"bjt-grid\" />");
            sb.AppendLine($"  <text x=\"{gx:F1}\" y=\"{oy + 14}\" class=\"bjt-label\" text-anchor=\"middle\">{v}V</text>");
        }

        // Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + axisW + 15}\" y2=\"{oy}\" class=\"bjt-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - axisH - 10}\" class=\"bjt-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + axisW + 10}\" y=\"{oy + 14}\" class=\"bjt-label\" font-weight=\"700\">VCE (V)</text>");
        sb.AppendLine($"  <text x=\"{ox - 10}\" y=\"{oy - axisH - 4}\" class=\"bjt-label\" font-weight=\"700\" text-anchor=\"end\">IC (mA)</text>");

        // Family of curves for each IB step
        int samples = 80;
        foreach (var ibMicro in model.BaseCurrentsMicroAmps)
        {
            double ib = ibMicro * 1e-6;
            var path = new StringBuilder();

            for (int i = 0; i <= samples; i++)
            {
                double t = i / (double)samples;
                double vce = t * maxVce;

                // Ebers-Moll with Early effect: IC = beta * IB * (1 + Vce/VA) * (1 - exp(-Vce / (2*VT)))
                double kneeFactor = 1.0 - Math.Exp(-vce / 0.15);
                double earlyFactor = 1.0 + (vce / model.EarlyVoltageVa);
                double ic = model.Beta * ib * kneeFactor * earlyFactor;
                double icMa = ic * 1000.0;

                double px = ox + (vce / maxVce) * axisW;
                double py = oy - (icMa / maxIc) * axisH;

                if (i == 0) path.Append($"M {px:F1} {py:F1}");
                else path.Append($" L {px:F1} {py:F1}");

                if (i == samples)
                {
                    sb.AppendLine($"  <text x=\"{px + 4:F1}\" y=\"{py + 3:F1}\" class=\"bjt-ib-tag\">IB={ibMicro:F0}µA</text>");
                }
            }

            sb.AppendLine($"  <path d=\"{path}\" class=\"bjt-curve\" />");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
