using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Engineering;

public record LogicGate(string GateType, List<string> Inputs, string Output);

public class LogicCircuit
{
    public List<LogicGate> Gates { get; } = new();
}

/// <summary>
/// Service for parsing digital logic gate expressions and rendering IEEE standard logic schematics in SVG.
/// </summary>
public static class LogicCircuitRendererService
{
    private static readonly Regex GateExprRegex = new(
        @"(AND|OR|NOT|XOR|NAND|NOR)\s*\(\s*([^)]+)\s*\)\s*->\s*([a-zA-Z0-9_\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses logic gate statements into a circuit model.
    /// </summary>
    public static LogicCircuit ParseCircuit(string circuitText)
    {
        var circuit = new LogicCircuit();
        if (string.IsNullOrWhiteSpace(circuitText))
            return circuit;

        foreach (Match m in GateExprRegex.Matches(circuitText))
        {
            string type = m.Groups[1].Value.ToUpperInvariant();
            var inputs = new List<string>();
            foreach (var inTok in m.Groups[2].Value.Split(','))
            {
                inputs.Add(inTok.Trim());
            }
            string output = m.Groups[3].Value.Trim();

            circuit.Gates.Add(new LogicGate(type, inputs, output));
        }

        return circuit;
    }

    /// <summary>
    /// Renders an SVG schematic of the logic circuit.
    /// </summary>
    public static string RenderCircuitSvg(LogicCircuit circuit)
    {
        if (circuit.Gates.Count == 0)
            return "<svg width=\"300\" height=\"100\"></svg>";

        double width = Math.Max(350, circuit.Gates.Count * 120 + 80);
        double height = 180;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-circuit-svg\">");
        sb.AppendLine("""
            <style>
              .gate-body { fill: #161b22; stroke: #58a6ff; stroke-width: 2; }
              .gate-type { font-family: Segoe UI, sans-serif; font-size: 11px; font-weight: 700; fill: #ffffff; text-anchor: middle; }
              .sig-label { font-family: monospace; font-size: 10px; fill: #8b949e; }
              .sig-line { stroke: #58a6ff; stroke-width: 1.5; }
            </style>
            """);

        double startX = 60;
        for (int i = 0; i < circuit.Gates.Count; i++)
        {
            var g = circuit.Gates[i];
            double gx = startX + i * 130;
            double gy = 60;

            // Gate Box / Symbol
            sb.AppendLine($"  <rect x=\"{gx}\" y=\"{gy}\" width=\"50\" height=\"44\" rx=\"6\" class=\"gate-body\" />");
            sb.AppendLine($"  <text x=\"{gx + 25}\" y=\"{gy + 26}\" class=\"gate-type\">{System.Net.WebUtility.HtmlEncode(g.GateType)}</text>");

            // Inputs
            if (g.Inputs.Count > 0)
            {
                double inY = gy + 12;
                foreach (var inp in g.Inputs)
                {
                    sb.AppendLine($"  <line x1=\"{gx - 20}\" y1=\"{inY}\" x2=\"{gx}\" y2=\"{inY}\" class=\"sig-line\" />");
                    sb.AppendLine($"  <text x=\"{gx - 32}\" y=\"{inY + 3}\" class=\"sig-label\">{System.Net.WebUtility.HtmlEncode(inp)}</text>");
                    inY += 20;
                }
            }

            // Output
            double outY = gy + 22;
            sb.AppendLine($"  <line x1=\"{gx + 50}\" y1=\"{outY}\" x2=\"{gx + 75}\" y2=\"{outY}\" class=\"sig-line\" />");
            sb.AppendLine($"  <text x=\"{gx + 80}\" y=\"{outY + 3}\" class=\"sig-label\">{System.Net.WebUtility.HtmlEncode(g.Output)}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
