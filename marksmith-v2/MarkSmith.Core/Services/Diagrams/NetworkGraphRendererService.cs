using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Diagrams;

public record NetworkNode(string Id, string Label, double X, double Y);
public record NetworkEdge(string Source, string Target, double Weight, bool IsDirected, bool IsBidirectional);

public class NetworkGraph
{
    public List<NetworkNode> Nodes { get; } = new();
    public List<NetworkEdge> Edges { get; } = new();
}

/// <summary>
/// Service for parsing network graph topologies and rendering interactive SVG network diagrams with edge weights.
/// </summary>
public static class NetworkGraphRendererService
{
    private static readonly Regex EdgeRegex = new(
        @"([a-zA-Z0-9_\-]+)\s*(->|<->|--)\s*([a-zA-Z0-9_\-]+)(?:\s*\[\s*(\d+(?:\.\d+)?)\s*\])?",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses network graph definitions and computes circular node positions.
    /// </summary>
    public static NetworkGraph Parse(string graphDefinition)
    {
        var graph = new NetworkGraph();
        if (string.IsNullOrWhiteSpace(graphDefinition))
            return graph;

        var nodeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = EdgeRegex.Matches(graphDefinition);

        foreach (Match m in matches)
        {
            string src = m.Groups[1].Value.Trim();
            string op = m.Groups[2].Value.Trim();
            string dst = m.Groups[3].Value.Trim();
            double weight = m.Groups[4].Success && double.TryParse(m.Groups[4].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double w) ? w : 1.0;

            nodeSet.Add(src);
            nodeSet.Add(dst);

            bool isDir = op == "->";
            bool isBi = op == "<->";
            graph.Edges.Add(new NetworkEdge(src, dst, weight, isDir, isBi));
        }

        // Layout nodes in a circular formation
        var nodeList = nodeSet.ToList();
        double centerX = 200, centerY = 150, radius = 100;
        int count = Math.Max(1, nodeList.Count);

        for (int i = 0; i < count; i++)
        {
            double angle = (2 * Math.PI * i) / count - (Math.PI / 2);
            double x = centerX + radius * Math.Cos(angle);
            double y = centerY + radius * Math.Sin(angle);
            graph.Nodes.Add(new NetworkNode(nodeList[i], nodeList[i], Math.Round(x, 1), Math.Round(y, 1)));
        }

        return graph;
    }

    /// <summary>
    /// Renders an SVG network graph diagram.
    /// </summary>
    public static string RenderSvg(NetworkGraph graph)
    {
        if (graph.Nodes.Count == 0)
            return "<svg width=\"400\" height=\"300\"></svg>";

        var sb = new StringBuilder();
        sb.AppendLine("<svg width=\"400\" height=\"300\" viewBox=\"0 0 400 300\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-network-graph\">");
        sb.AppendLine("""
            <defs>
              <marker id="net-arrow" markerWidth="6" markerHeight="6" refX="16" refY="3" orient="auto">
                <polygon points="0 0, 6 3, 0 6" fill="#58a6ff" />
              </marker>
            </defs>
            """);

        // 1. Draw Edges
        foreach (var edge in graph.Edges)
        {
            var u = graph.Nodes.FirstOrDefault(n => n.Id.Equals(edge.Source, StringComparison.OrdinalIgnoreCase));
            var v = graph.Nodes.FirstOrDefault(n => n.Id.Equals(edge.Target, StringComparison.OrdinalIgnoreCase));
            if (u != null && v != null)
            {
                string marker = edge.IsDirected ? " marker-end=\"url(#net-arrow)\"" : "";
                double strokeW = Math.Clamp(edge.Weight / 3.0, 1.0, 4.0);

                sb.AppendLine($"  <line x1=\"{u.X}\" y1=\"{u.Y}\" x2=\"{v.X}\" y2=\"{v.Y}\" stroke=\"#30363d\" stroke-width=\"{strokeW}\"{marker} />");

                if (edge.Weight > 1.0)
                {
                    double mx = (u.X + v.X) / 2;
                    double my = (u.Y + v.Y) / 2;
                    sb.AppendLine($"  <text x=\"{mx}\" y=\"{my}\" fill=\"#8b949e\" font-size=\"10\" text-anchor=\"middle\" font-family=\"sans-serif\">{edge.Weight}</text>");
                }
            }
        }

        // 2. Draw Nodes
        foreach (var n in graph.Nodes)
        {
            sb.AppendLine($"  <g transform=\"translate({n.X}, {n.Y})\">");
            sb.AppendLine("    <circle r=\"16\" fill=\"#1f6feb\" stroke=\"#58a6ff\" stroke-width=\"2\" />");
            sb.AppendLine($"    <text y=\"4\" fill=\"#ffffff\" font-size=\"11\" font-weight=\"600\" text-anchor=\"middle\" font-family=\"sans-serif\">{n.Label}</text>");
            sb.AppendLine("  </g>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
