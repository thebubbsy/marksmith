using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Biology;

public record PathwayStep(string Reactants, string? Enzyme, string Products, bool IsReversible);

public class PathwayModel
{
    public string Title { get; set; } = "Metabolic Pathway";
    public List<PathwayStep> Steps { get; } = new();
}

/// <summary>
/// Service for parsing biochemical metabolic pathway reactions and rendering clean reaction cycle diagrams in SVG.
/// </summary>
public static class MetabolicPathwayRendererService
{
    private static readonly Regex PathwayFenceRegex = new(
        @":::pathway(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex IrrevStepRegex = new(
        @"^(.+?)\s*-\(([^)]+)\)->\s*(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex RevStepRegex = new(
        @"^(.+?)\s*<->\s*(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex DirectStepRegex = new(
        @"^(.+?)\s*->\s*(.+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a pathway block into a structured biochemical reaction model.
    /// </summary>
    public static PathwayModel ParsePathway(string blockText, string defaultTitle = "Metabolic Pathway")
    {
        var model = new PathwayModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = PathwayFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var irrevMatch = IrrevStepRegex.Match(l);
            if (irrevMatch.Success)
            {
                model.Steps.Add(new PathwayStep(
                    irrevMatch.Groups[1].Value.Trim(),
                    irrevMatch.Groups[2].Value.Trim(),
                    irrevMatch.Groups[3].Value.Trim(),
                    false));
                continue;
            }

            var revMatch = RevStepRegex.Match(l);
            if (revMatch.Success)
            {
                model.Steps.Add(new PathwayStep(
                    revMatch.Groups[1].Value.Trim(),
                    null,
                    revMatch.Groups[2].Value.Trim(),
                    true));
                continue;
            }

            var directMatch = DirectStepRegex.Match(l);
            if (directMatch.Success)
            {
                model.Steps.Add(new PathwayStep(
                    directMatch.Groups[1].Value.Trim(),
                    null,
                    directMatch.Groups[2].Value.Trim(),
                    false));
                continue;
            }
        }

        return model;
    }

    /// <summary>
    /// Renders an SVG vector diagram of the metabolic reaction cascade.
    /// </summary>
    public static string RenderPathwaySvg(PathwayModel model)
    {
        if (model.Steps.Count == 0)
            return "<svg width=\"350\" height=\"100\"></svg>";

        double width = 480;
        double height = Math.Max(150, model.Steps.Count * 65 + 60);

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-pathway-svg\">");
        sb.AppendLine("""
            <defs>
              <marker id="pathArrow" markerWidth="6" markerHeight="6" refX="5" refY="3" orient="auto">
                <path d="M 0 0 L 6 3 L 0 6 z" fill="#3fb950" />
              </marker>
            </defs>
            <style>
              .pw-bg { fill: #0d1117; stroke: #30363d; stroke-width: 1.5; }
              .pw-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #e6edf3; }
              .pw-chem { font-family: Segoe UI, sans-serif; font-size: 11px; font-weight: 600; fill: #ffffff; }
              .pw-enzyme { font-family: monospace; font-size: 10px; font-style: italic; fill: #58a6ff; }
              .pw-arrow { stroke: #3fb950; stroke-width: 2; marker-end: url(#pathArrow); fill: none; }
              .pw-arrow-rev { stroke: #d29922; stroke-width: 1.8; stroke-dasharray: 4 2; fill: none; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"pw-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"pw-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        double curY = 55;
        foreach (var s in model.Steps)
        {
            sb.AppendLine($"  <g transform=\"translate(30, {curY})\">");
            sb.AppendLine($"    <text x=\"0\" y=\"16\" class=\"pw-chem\">{System.Net.WebUtility.HtmlEncode(s.Reactants)}</text>");

            if (s.IsReversible)
            {
                sb.AppendLine("    <line x1=\"160\" y1=\"12\" x2=\"240\" y2=\"12\" class=\"pw-arrow-rev\" />");
                sb.AppendLine("    <text x=\"190\" y=\"6\" class=\"pw-enzyme\">&#8644;</text>");
            }
            else
            {
                sb.AppendLine("    <line x1=\"160\" y1=\"12\" x2=\"240\" y2=\"12\" class=\"pw-arrow\" />");
                if (!string.IsNullOrEmpty(s.Enzyme))
                {
                    sb.AppendLine($"    <text x=\"165\" y=\"6\" class=\"pw-enzyme\">{System.Net.WebUtility.HtmlEncode(s.Enzyme)}</text>");
                }
            }

            sb.AppendLine($"    <text x=\"260\" y=\"16\" class=\"pw-chem\">{System.Net.WebUtility.HtmlEncode(s.Products)}</text>");
            sb.AppendLine("  </g>");

            curY += 55;
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
