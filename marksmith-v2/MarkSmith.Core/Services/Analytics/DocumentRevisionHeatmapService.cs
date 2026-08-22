using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MarkSmith.Services.Analytics;

public record SectionRevisionStats(
    string SectionName,
    int AddedLines,
    int DeletedLines,
    int EditCount,
    double ChurnScore,
    string StabilityRating);

/// <summary>
/// Service that analyzes document section edit churn and renders visual revision activity heatmaps.
/// </summary>
public static class DocumentRevisionHeatmapService
{
    /// <summary>
    /// Computes churn scores and stability ratings for document sections.
    /// </summary>
    public static List<SectionRevisionStats> AnalyzeSections(IEnumerable<(string Section, int Added, int Deleted, int Edits)> history)
    {
        var list = new List<SectionRevisionStats>();
        if (history == null)
            return list;

        foreach (var (sec, added, deleted, edits) in history)
        {
            // Churn score: weighted formula of line edits and revisions
            double churn = (added * 1.0) + (deleted * 1.5) + (edits * 4.0);
            churn = Math.Round(churn, 1);

            string rating = churn switch
            {
                < 15 => "Stable",
                < 50 => "Moderate Churn",
                < 120 => "Active Iteration",
                _ => "High Churn"
            };

            list.Add(new SectionRevisionStats(sec, added, deleted, edits, churn, rating));
        }

        return list;
    }

    /// <summary>
    /// Renders an SVG revision heatmap displaying section churn intensity.
    /// </summary>
    public static string RenderHeatmapSvg(List<SectionRevisionStats> sections)
    {
        if (sections == null || sections.Count == 0)
            return "<svg width=\"300\" height=\"100\"></svg>";

        double maxChurn = Math.Max(1.0, sections.Max(s => s.ChurnScore));
        double barHeight = 36;
        double width = 500;
        double height = sections.Count * (barHeight + 10) + 40;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-revision-heatmap\">");
        sb.AppendLine("""
            <style>
              .sec-name { font-family: Segoe UI, sans-serif; font-size: 12px; fill: #e6edf3; font-weight: 600; }
              .churn-text { font-family: Segoe UI, sans-serif; font-size: 11px; fill: #8b949e; text-anchor: end; }
            </style>
            """);

        double y = 20;
        foreach (var sec in sections)
        {
            double ratio = Math.Clamp(sec.ChurnScore / maxChurn, 0.05, 1.0);
            double barWidth = ratio * 260;
            string color = GetHeatmapColor(ratio);

            sb.AppendLine($"  <g transform=\"translate(20, {y})\">");
            sb.AppendLine($"    <text x=\"0\" y=\"22\" class=\"sec-name\">{System.Net.WebUtility.HtmlEncode(sec.SectionName)}</text>");
            sb.AppendLine($"    <rect x=\"160\" y=\"6\" width=\"{barWidth}\" height=\"22\" rx=\"4\" fill=\"{color}\" />");
            sb.AppendLine($"    <text x=\"460\" y=\"22\" class=\"churn-text\">{sec.ChurnScore:F0} pts ({sec.StabilityRating})</text>");
            sb.AppendLine("  </g>");

            y += barHeight + 10;
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string GetHeatmapColor(double ratio)
    {
        return ratio switch
        {
            < 0.25 => "#238636", // Stable Green
            < 0.50 => "#1f6feb", // Moderate Blue
            < 0.75 => "#d29922", // Active Amber
            _ => "#f85149"       // High Churn Red
        };
    }
}
