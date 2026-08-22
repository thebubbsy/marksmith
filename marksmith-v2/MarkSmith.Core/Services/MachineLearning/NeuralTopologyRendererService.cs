using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.MachineLearning;

public record NeuralLayer(string Name, int NodeCount, string? Activation);

public class NeuralTopologyModel
{
    public string Title { get; set; } = "Neural Network Topology";
    public List<NeuralLayer> Layers { get; } = new();
}

/// <summary>
/// Service for parsing neural network architectures and rendering interactive multi-layer topology schematics in SVG.
/// </summary>
public static class NeuralTopologyRendererService
{
    private static readonly Regex NnFenceRegex = new(
        @":::nn(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex LayerRegex = new(
        @"layer\s+""([^""]+)""\s+nodes=(\d+)(?:\s+act=([A-Za-z0-9_-]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static NeuralTopologyModel ParseTopology(string blockText, string defaultTitle = "Neural Topology")
    {
        var model = new NeuralTopologyModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = NnFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var lm = LayerRegex.Match(l);
            if (lm.Success)
            {
                string name = lm.Groups[1].Value.Trim();
                int nodes = int.TryParse(lm.Groups[2].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int pn) ? pn : 1;
                string? act = lm.Groups[3].Success ? lm.Groups[3].Value.ToLowerInvariant() : null;
                model.Layers.Add(new NeuralLayer(name, Math.Max(1, Math.Min(12, nodes)), act));
                continue;
            }
        }

        return model;
    }

    public static string RenderTopologySvg(NeuralTopologyModel model)
    {
        if (model.Layers.Count == 0)
            return "<svg width=\"400\" height=\"100\"></svg>";

        double width = Math.Max(420, model.Layers.Count * 130 + 40);
        double height = 280;
        double cy = height / 2 + 10;
        double layerSpacing = (width - 80) / Math.Max(1, model.Layers.Count - 1);

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-nn-svg\">");
        sb.AppendLine("""
            <style>
              .nn-bg { fill: #0d1117; stroke: #30363d; stroke-width: 1.5; }
              .nn-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #e6edf3; }
              .nn-synapse { stroke: #58a6ff; stroke-width: 1; opacity: 0.35; }
              .nn-node { fill: #1f6feb; stroke: #58a6ff; stroke-width: 2; }
              .nn-label { font-family: Segoe UI, sans-serif; font-size: 10px; font-weight: 600; fill: #8b949e; text-anchor: middle; }
              .nn-act { font-family: monospace; font-size: 9px; fill: #f0883e; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"nn-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"nn-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        var layerNodePositions = new List<List<(double X, double Y)>>();

        for (int l = 0; l < model.Layers.Count; l++)
        {
            var layer = model.Layers[l];
            double lx = 40 + l * layerSpacing;
            double nodeSpacing = 28;
            double topY = cy - ((layer.NodeCount - 1) * nodeSpacing) / 2.0;

            var currentLayerPos = new List<(double X, double Y)>();
            for (int n = 0; n < layer.NodeCount; n++)
            {
                double ny = topY + n * nodeSpacing;
                currentLayerPos.Add((lx, ny));
            }
            layerNodePositions.Add(currentLayerPos);
        }

        // Draw Synapses
        for (int l = 0; l < layerNodePositions.Count - 1; l++)
        {
            var fromNodes = layerNodePositions[l];
            var toNodes = layerNodePositions[l + 1];

            foreach (var fn in fromNodes)
            {
                foreach (var tn in toNodes)
                {
                    sb.AppendLine($"  <line x1=\"{fn.X}\" y1=\"{fn.Y}\" x2=\"{tn.X}\" y2=\"{tn.Y}\" class=\"nn-synapse\" />");
                }
            }
        }

        // Draw Nodes and Labels
        for (int l = 0; l < model.Layers.Count; l++)
        {
            var layer = model.Layers[l];
            var nodes = layerNodePositions[l];
            double lx = nodes[0].X;

            sb.AppendLine($"  <text x=\"{lx}\" y=\"{height - 25}\" class=\"nn-label\">{System.Net.WebUtility.HtmlEncode(layer.Name)}</text>");
            if (!string.IsNullOrEmpty(layer.Activation))
            {
                sb.AppendLine($"  <text x=\"{lx}\" y=\"{height - 12}\" class=\"nn-act\">{System.Net.WebUtility.HtmlEncode(layer.Activation)}</text>");
            }

            foreach (var n in nodes)
            {
                sb.AppendLine($"  <circle cx=\"{n.X}\" cy=\"{n.Y}\" r=\"8\" class=\"nn-node\" />");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
