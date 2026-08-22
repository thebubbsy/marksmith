using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public record LogicGate(string Type, string In1, string In2, string Out);

public class LogicCircuitModel
{
    public string Title { get; set; } = "Logic Circuit";
    public List<LogicGate> Gates { get; } = new();
}

/// <summary>
/// Service for parsing digital logic gate netlists and rendering IEEE Std 91 circuit schematics in SVG.
/// </summary>
public static class LogicGateSchematicService
{
    private static readonly Regex CircuitFenceRegex = new(
        @":::circuit([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex GateRegex = new(
        @"gate\s+(AND|OR|XOR|NOT|NAND|NOR)\s*\(\s*([^,\)]+)(?:,\s*([^,\)]+))?\s*\)\s*->\s*([A-Za-z0-9_]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static LogicCircuitModel ParseCircuit(string blockText, string defaultTitle = "Circuit Schematic")
    {
        var model = new LogicCircuitModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = CircuitFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var gm = GateRegex.Match(l);
            if (gm.Success)
            {
                string type = gm.Groups[1].Value.ToUpperInvariant();
                string in1 = gm.Groups[2].Value.Trim();
                string in2 = gm.Groups[3].Success ? gm.Groups[3].Value.Trim() : "";
                string outNet = gm.Groups[4].Value.Trim();
                model.Gates.Add(new LogicGate(type, in1, in2, outNet));
            }
        }

        return model;
    }

    public static string RenderCircuitSvg(LogicCircuitModel model)
    {
        double width = 420;
        double gateH = 65;
        double height = Math.Max(160, model.Gates.Count * gateH + 80);
        double ox = 120;
        double oy = 55;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-circuit-svg\">");
        sb.AppendLine("""
            <style>
              .ct-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .ct-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .ct-wire { stroke: #94a3b8; stroke-width: 2; fill: none; }
              .ct-gate { fill: #1e293b; stroke: #38bdf8; stroke-width: 2; }
              .ct-label { font-family: monospace; font-size: 11px; font-weight: 700; fill: #38bdf8; text-anchor: middle; }
              .ct-port { font-family: monospace; font-size: 10px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"ct-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"ct-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        for (int i = 0; i < model.Gates.Count; i++)
        {
            var gate = model.Gates[i];
            double gy = oy + i * gateH;

            // Inputs
            sb.AppendLine($"  <line x1=\"{ox - 50}\" y1=\"{gy + 12}\" x2=\"{ox}\" y2=\"{gy + 12}\" class=\"ct-wire\" />");
            sb.AppendLine($"  <text x=\"{ox - 65}\" y=\"{gy + 16}\" class=\"ct-port\">{System.Net.WebUtility.HtmlEncode(gate.In1)}</text>");

            if (!string.IsNullOrEmpty(gate.In2))
            {
                sb.AppendLine($"  <line x1=\"{ox - 50}\" y1=\"{gy + 32}\" x2=\"{ox}\" y2=\"{gy + 32}\" class=\"ct-wire\" />");
                sb.AppendLine($"  <text x=\"{ox - 65}\" y=\"{gy + 36}\" class=\"ct-port\">{System.Net.WebUtility.HtmlEncode(gate.In2)}</text>");
            }

            // Gate Symbol Body (D-shape / Rect representation)
            sb.AppendLine($"  <path d=\"M {ox} {gy} L {ox + 35} {gy} A 22 22 0 0 1 {ox + 35} {gy + 44} L {ox} {gy + 44} Z\" class=\"ct-gate\" />");
            sb.AppendLine($"  <text x=\"{ox + 20}\" y=\"{gy + 26}\" class=\"ct-label\">{gate.Type}</text>");

            // Output
            sb.AppendLine($"  <line x1=\"{ox + 57}\" y1=\"{gy + 22}\" x2=\"{ox + 110}\" y2=\"{gy + 22}\" class=\"ct-wire\" />");
            sb.AppendLine($"  <text x=\"{ox + 120}\" y=\"{gy + 26}\" class=\"ct-port\">{System.Net.WebUtility.HtmlEncode(gate.Out)}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
