using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class MosfetCurveModel
{
    public string Title { get; set; } = "NMOS Output Characteristic (ID vs VDS)";
    public string ChannelType { get; set; } = "NMOS";
    public double ThresholdVoltageVth { get; set; } = 2.0;    // Vth (V)
    public double TransconductanceKn { get; set; } = 50.0;     // kn' * (W/L) in mA/V^2
    public double LambdaModulation { get; set; } = 0.015;      // lambda (1/V)
    public List<double> GateVoltagesVgs { get; } = new() { 3.0, 4.0, 5.0, 6.0 };
}

public static class MosfetCharacteristicService
{
    private static readonly Regex MosfetFenceRegex = new(
        @":::(?:mosfet|mosfet-curve|nmos-curve)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VthRegex = new(
        @"(?:vth|threshold)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:V)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KnRegex = new(
        @"(?:kn|transconductance|k)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LambdaRegex = new(
        @"(?:lambda|channel_length)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static MosfetCurveModel ParseMosfet(string blockText, string defaultTitle = "NMOS Output Characteristic (ID vs VDS)")
    {
        var model = new MosfetCurveModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = MosfetFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var vtm = VthRegex.Match(header);
            if (vtm.Success && double.TryParse(vtm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double vth))
                model.ThresholdVoltageVth = Math.Clamp(vth, 0.2, 10.0);

            var knm = KnRegex.Match(header);
            if (knm.Success && double.TryParse(knm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double kn))
                model.TransconductanceKn = Math.Clamp(kn, 1.0, 1000.0);

            var lm = LambdaRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double lambda))
                model.LambdaModulation = Math.Clamp(lambda, 0.0, 0.2);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var vtm = VthRegex.Match(l);
            if (vtm.Success && double.TryParse(vtm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double vth))
                model.ThresholdVoltageVth = Math.Clamp(vth, 0.2, 10.0);

            var knm = KnRegex.Match(l);
            if (knm.Success && double.TryParse(knm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double kn))
                model.TransconductanceKn = Math.Clamp(kn, 1.0, 1000.0);

            var lm = LambdaRegex.Match(l);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double lambda))
                model.LambdaModulation = Math.Clamp(lambda, 0.0, 0.2);
        }

        return model;
    }

    public static string RenderMosfetSvg(MosfetCurveModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 60;
        double oy = 230;
        double axisW = 380;
        double axisH = 160;

        double maxVds = 10.0; // Volts
        double maxVgs = model.GateVoltagesVgs.Max();
        double maxVov = Math.Max(0.1, maxVgs - model.ThresholdVoltageVth);
        double maxId = 0.5 * model.TransconductanceKn * Math.Pow(maxVov, 2) * (1.0 + model.LambdaModulation * maxVds) * 1.15; // mA

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-mosfet-svg\">");
        sb.AppendLine("""
            <style>
              .mf-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .mf-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .mf-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .mf-axis { stroke: #475569; stroke-width: 1.2; }
              .mf-grid { stroke: #1e293b; stroke-width: 1; stroke-dasharray: 2 2; }
              .mf-curve { fill: none; stroke: #38bdf8; stroke-width: 2; }
              .mf-sat-bnd { fill: none; stroke: #f43f5e; stroke-width: 1.5; stroke-dasharray: 4 2; }
              .mf-label { font-family: monospace; font-size: 9.5px; fill: #94a3b8; }
              .mf-vgs-tag { font-family: monospace; font-size: 9px; font-weight: 700; fill: #38bdf8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"mf-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"mf-title\">⚡ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"mf-meta\">{model.ChannelType} • Vth = {model.ThresholdVoltageVth:F1}V • kn = {model.TransconductanceKn:F0} mA/V² • λ = {model.LambdaModulation:F3}</text>");

        // Grid lines & Axis ticks
        for (int v = 2; v <= 10; v += 2)
        {
            double gx = ox + (v / maxVds) * axisW;
            sb.AppendLine($"  <line x1=\"{gx:F1}\" y1=\"{oy}\" x2=\"{gx:F1}\" y2=\"{oy - axisH}\" class=\"mf-grid\" />");
            sb.AppendLine($"  <text x=\"{gx:F1}\" y=\"{oy + 14}\" class=\"mf-label\" text-anchor=\"middle\">{v}V</text>");
        }

        // Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + axisW + 15}\" y2=\"{oy}\" class=\"mf-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - axisH - 10}\" class=\"mf-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + axisW + 10}\" y=\"{oy + 14}\" class=\"mf-label\" font-weight=\"700\">VDS (V)</text>");
        sb.AppendLine($"  <text x=\"{ox - 10}\" y=\"{oy - axisH - 4}\" class=\"mf-label\" font-weight=\"700\" text-anchor=\"end\">ID (mA)</text>");

        // Pinch-off Saturation Boundary Parabola: Vds,sat = Vgs - Vth -> ID,sat = 0.5 * kn * Vds^2
        var satPath = new StringBuilder();
        int satSteps = 40;
        for (int i = 0; i <= satSteps; i++)
        {
            double vdsSat = (i / (double)satSteps) * maxVov;
            double idSat = 0.5 * model.TransconductanceKn * Math.Pow(vdsSat, 2);

            double px = ox + (vdsSat / maxVds) * axisW;
            double py = oy - (idSat / maxId) * axisH;

            if (i == 0) satPath.Append($"M {px:F1} {py:F1}");
            else satPath.Append($" L {px:F1} {py:F1}");
        }
        sb.AppendLine($"  <path d=\"{satPath}\" class=\"mf-sat-bnd\" />");
        sb.AppendLine($"  <text x=\"{ox + (maxVov / maxVds) * axisW - 20:F1}\" y=\"{oy - 0.5 * axisH:F1}\" font-family=\"Segoe UI, sans-serif\" font-size=\"8.5\" fill=\"#f43f5e\">Pinch-off Boundary</text>");

        // Family of curves for each VGS step
        int samples = 80;
        foreach (var vgs in model.GateVoltagesVgs)
        {
            double vov = vgs - model.ThresholdVoltageVth;
            if (vov <= 0) continue;

            var path = new StringBuilder();
            for (int i = 0; i <= samples; i++)
            {
                double t = i / (double)samples;
                double vds = t * maxVds;

                double id;
                if (vds < vov)
                {
                    // Triode / Ohmic: ID = kn * [ (Vgs - Vth)*Vds - 0.5*Vds^2 ] * (1 + lambda*Vds)
                    id = model.TransconductanceKn * (vov * vds - 0.5 * Math.Pow(vds, 2)) * (1.0 + model.LambdaModulation * vds);
                }
                else
                {
                    // Saturation: ID = 0.5 * kn * (Vgs - Vth)^2 * (1 + lambda*Vds)
                    id = 0.5 * model.TransconductanceKn * Math.Pow(vov, 2) * (1.0 + model.LambdaModulation * vds);
                }

                double px = ox + (vds / maxVds) * axisW;
                double py = oy - (id / maxId) * axisH;

                if (i == 0) path.Append($"M {px:F1} {py:F1}");
                else path.Append($" L {px:F1} {py:F1}");

                if (i == samples)
                {
                    sb.AppendLine($"  <text x=\"{px + 4:F1}\" y=\"{py + 3:F1}\" class=\"mf-vgs-tag\">VGS={vgs:F0}V</text>");
                }
            }

            sb.AppendLine($"  <path d=\"{path}\" class=\"mf-curve\" />");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
