using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Diagrams;

public record DiagramNode(string Id, string Label, double X = 0, double Y = 0, double Width = 140, double Height = 50);
public record DiagramEdge(string From, string To, string? Label = null);

public class ParsedDiagram
{
    public List<DiagramNode> Nodes { get; } = new();
    public List<DiagramEdge> Edges { get; } = new();
    public string DiagramType { get; set; } = "digraph";
}

/// <summary>
/// Service that parses Graphviz DOT and PlantUML component syntax and renders native SVG vector graphics without external dependencies.
/// </summary>
public static class PlantUmlDotConverterService
{
    private static readonly Regex DotEdgeRegex = new(
        @"([a-zA-Z0-9_\-]+)\s*->\s*([a-zA-Z0-9_\-]+)(?:\s*\[\s*label\s*=\s*""([^""]*)""\s*\])?",
        RegexOptions.Compiled);

    private static readonly Regex PlantUmlEdgeRegex = new(
        @"\[([^\]]+)\]\s*->\s*\[([^\]]+)\](?:\s*:\s*(.*))?",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses DOT or PlantUML component diagram code into structured node and edge models.
    /// </summary>
    public static ParsedDiagram Parse(string code)
    {
        var diagram = new ParsedDiagram();
        if (string.IsNullOrWhiteSpace(code))
            return diagram;

        var nodeDict = new Dictionary<string, DiagramNode>(StringComparer.OrdinalIgnoreCase);

        // Check for PlantUML component syntax: [A] -> [B] : label
        var plantMatches = PlantUmlEdgeRegex.Matches(code);
        if (plantMatches.Count > 0)
        {
            diagram.DiagramType = "plantuml";
            foreach (Match m in plantMatches)
            {
                string from = m.Groups[1].Value.Trim();
                string to = m.Groups[2].Value.Trim();
                string? label = m.Groups[3].Success ? m.Groups[3].Value.Trim() : null;

                if (!nodeDict.ContainsKey(from)) nodeDict[from] = new DiagramNode(from, from);
                if (!nodeDict.ContainsKey(to)) nodeDict[to] = new DiagramNode(to, to);

                diagram.Edges.Add(new DiagramEdge(from, to, label));
            }
        }
        else
        {
            // Default: Graphviz DOT: A -> B [label="call"]
            var dotMatches = DotEdgeRegex.Matches(code);
            foreach (Match m in dotMatches)
            {
                string from = m.Groups[1].Value.Trim();
                string to = m.Groups[2].Value.Trim();
                string? label = m.Groups[3].Success ? m.Groups[3].Value.Trim() : null;

                if (!nodeDict.ContainsKey(from)) nodeDict[from] = new DiagramNode(from, from);
                if (!nodeDict.ContainsKey(to)) nodeDict[to] = new DiagramNode(to, to);

                diagram.Edges.Add(new DiagramEdge(from, to, label));
            }
        }

        // Layout nodes in a simple horizontal / grid pipeline
        double curX = 40, curY = 40;
        foreach (var node in nodeDict.Values)
        {
            diagram.Nodes.Add(node with { X = curX, Y = curY });
            curX += 180;
        }

        return diagram;
    }

    /// <summary>
    /// Renders the parsed diagram as standalone SVG vector markup.
    /// </summary>
    public static string RenderSvg(ParsedDiagram diagram)
    {
        if (diagram.Nodes.Count == 0)
            return "<svg width=\"200\" height=\"100\"></svg>";

        double width = Math.Max(300, diagram.Nodes.Max(n => n.X + n.Width) + 60);
        double height = 140;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-native-diagram\">");
        sb.AppendLine("""
            <defs>
                <marker id="dot-arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto">
                    <polygon points="0 0, 8 4, 0 8" fill="#58a6ff" />
                </marker>
                <filter id="box-shadow" x="-10%" y="-10%" width="120%" height="120%">
                    <feDropShadow dx="0" dy="4" stdDeviation="6" flood-color="#000" flood-opacity="0.25" />
                </filter>
            </defs>
            """);

        // 1. Draw Edges
        foreach (var edge in diagram.Edges)
        {
            var source = diagram.Nodes.FirstOrDefault(n => n.Id.Equals(edge.From, StringComparison.OrdinalIgnoreCase));
            var target = diagram.Nodes.FirstOrDefault(n => n.Id.Equals(edge.To, StringComparison.OrdinalIgnoreCase));
            if (source != null && target != null)
            {
                double x1 = source.X + source.Width;
                double y1 = source.Y + source.Height / 2;
                double x2 = target.X;
                double y2 = target.Y + target.Height / 2;

                sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"#58a6ff\" stroke-width=\"2\" marker-end=\"url(#dot-arrow)\" />");
                if (!string.IsNullOrEmpty(edge.Label))
                {
                    double mx = (x1 + x2) / 2;
                    double my = (y1 + y2) / 2 - 8;
                    sb.AppendLine($"  <text x=\"{mx}\" y=\"{my}\" fill=\"#8b949e\" font-size=\"11\" text-anchor=\"middle\" font-family=\"sans-serif\">{edge.Label}</text>");
                }
            }
        }

        // 2. Draw Nodes
        foreach (var n in diagram.Nodes)
        {
            sb.AppendLine($"  <g transform=\"translate({n.X}, {n.Y})\">");
            sb.AppendLine($"    <rect width=\"{n.Width}\" height=\"{n.Height}\" rx=\"8\" fill=\"#161b22\" stroke=\"#30363d\" stroke-width=\"1.5\" filter=\"url(#box-shadow)\" />");
            sb.AppendLine($"    <text x=\"{n.Width / 2}\" y=\"{n.Height / 2 + 5}\" fill=\"#e6edf3\" font-size=\"13\" font-weight=\"600\" text-anchor=\"middle\" font-family=\"Segoe UI, sans-serif\">{n.Label}</text>");
            sb.AppendLine("  </g>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
