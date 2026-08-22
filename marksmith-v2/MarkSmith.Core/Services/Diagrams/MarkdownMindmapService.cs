using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Diagrams;

public class MindmapNode
{
    public string Text { get; set; } = string.Empty;
    public int Level { get; set; }
    public List<MindmapNode> Children { get; } = new();
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
/// Service that parses hierarchical Markdown outlines and renders interactive SVG concept mindmaps.
/// </summary>
public static class MarkdownMindmapService
{
    private static readonly Regex BulletRegex = new(@"^(\s*)[-*+]\s+(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Parses an indented Markdown list into a hierarchical Mindmap tree.
    /// </summary>
    public static MindmapNode? ParseTree(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return null;

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        MindmapNode? root = null;
        var stack = new List<MindmapNode>();

        foreach (var line in lines)
        {
            var match = BulletRegex.Match(line);
            if (!match.Success) continue;

            int indent = match.Groups[1].Value.Length;
            int level = indent / 2; // 2 spaces per level
            string text = match.Groups[2].Value.Trim();

            var node = new MindmapNode { Text = text, Level = level };

            if (root == null || level == 0)
            {
                root = node;
                stack.Clear();
                stack.Add(node);
            }
            else
            {
                while (stack.Count > level && stack.Count > 1)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                stack[^1].Children.Add(node);
                stack.Add(node);
            }
        }

        if (root != null)
        {
            LayoutTree(root, 60, 150, 0);
        }

        return root;
    }

    private static double _currentY = 40;

    private static void LayoutTree(MindmapNode root, double startX, double startY, int depth)
    {
        _currentY = 40;
        AssignCoordinates(root, startX, 0);
    }

    private static void AssignCoordinates(MindmapNode node, double x, int depth)
    {
        node.X = x;
        if (node.Children.Count == 0)
        {
            node.Y = _currentY;
            _currentY += 36;
        }
        else
        {
            foreach (var child in node.Children)
            {
                AssignCoordinates(child, x + 150, depth + 1);
            }
            node.Y = (node.Children.First().Y + node.Children.Last().Y) / 2;
        }
    }

    /// <summary>
    /// Renders an SVG mindmap diagram with curved branch connectors.
    /// </summary>
    public static string RenderSvg(MindmapNode root)
    {
        if (root == null)
            return "<svg width=\"300\" height=\"100\"></svg>";

        double width = 650;
        double height = Math.Max(200, _currentY + 40);

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-mindmap-svg\">");
        sb.AppendLine("""
            <style>
              .mm-node { font-family: Segoe UI, sans-serif; font-size: 11px; font-weight: 600; fill: #ffffff; text-anchor: middle; }
              .mm-branch { fill: none; stroke: #388bfd; stroke-width: 2; }
            </style>
            """);

        RenderNodeAndBranches(sb, root);

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void RenderNodeAndBranches(StringBuilder sb, MindmapNode node)
    {
        // 1. Draw branches to children
        foreach (var child in node.Children)
        {
            double cp1X = node.X + 60;
            double cp1Y = node.Y;
            double cp2X = child.X - 60;
            double cp2Y = child.Y;

            sb.AppendLine($"  <path d=\"M {node.X + 40} {node.Y} C {cp1X} {cp1Y}, {cp2X} {cp2Y}, {child.X - 40} {child.Y}\" class=\"mm-branch\" />");
            RenderNodeAndBranches(sb, child);
        }

        // 2. Draw node pill
        string fill = node.Level == 0 ? "#1f6feb" : (node.Children.Count > 0 ? "#238636" : "#21262d");
        string stroke = node.Level == 0 ? "#58a6ff" : (node.Children.Count > 0 ? "#3fb950" : "#30363d");

        sb.AppendLine($"  <g transform=\"translate({node.X}, {node.Y})\">");
        sb.AppendLine($"    <rect x=\"-40\" y=\"-12\" width=\"80\" height=\"24\" rx=\"12\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.5\" />");
        string label = node.Text.Length > 12 ? node.Text.Substring(0, 10) + ".." : node.Text;
        sb.AppendLine($"    <text y=\"4\" class=\"mm-node\">{System.Net.WebUtility.HtmlEncode(label)}</text>");
        sb.AppendLine("  </g>");
    }
}
