using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Project;

public record GanttTask(
    string Name,
    DateTime StartDate,
    DateTime EndDate,
    int ProgressPercent,
    int LineNumber);

public class GanttTimelineModel
{
    public string Title { get; set; } = "Project Schedule";
    public List<GanttTask> Tasks { get; } = new();
    public DateTime MinDate => Tasks.Count > 0 ? Tasks.Min(t => t.StartDate) : DateTime.Today;
    public DateTime MaxDate => Tasks.Count > 0 ? Tasks.Max(t => t.EndDate) : DateTime.Today.AddDays(30);
    public int TotalDays => Math.Max(1, (int)(MaxDate - MinDate).TotalDays);
}

/// <summary>
/// Service for parsing Markdown Gantt schedule entries and rendering responsive SVG Gantt diagrams with progress tracking.
/// </summary>
public static class MarkdownGanttTimelineService
{
    private static readonly Regex TaskRegex = new(
        @"\[(\d{4}-\d{2}-\d{2})\s*->\s*(\d{4}-\d{2}-\d{2})\]\s*([^%\r\n]+?)(?:\s*%(\d{1,3}))?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Parses Gantt timeline entries from Markdown text.
    /// </summary>
    public static GanttTimelineModel ParseTimeline(string markdown, string defaultTitle = "Project Schedule")
    {
        var model = new GanttTimelineModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(markdown))
            return model;

        var matches = TaskRegex.Matches(markdown);
        foreach (Match m in matches)
        {
            if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) &&
                DateTime.TryParseExact(m.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                string name = m.Groups[3].Value.Trim();
                int progress = m.Groups[4].Success && int.TryParse(m.Groups[4].Value, out int p) ? Math.Clamp(p, 0, 100) : 0;

                int lineNum = 1 + (m.Index > 0 ? markdown.Substring(0, m.Index).Split('\n').Length - 1 : 0);
                model.Tasks.Add(new GanttTask(name, start, end, progress, lineNum));
            }
        }

        return model;
    }

    /// <summary>
    /// Renders an SVG Gantt diagram with time scales and progress bars.
    /// </summary>
    public static string RenderGanttSvg(GanttTimelineModel model)
    {
        if (model.Tasks.Count == 0)
            return "<svg width=\"500\" height=\"100\"></svg>";

        double labelWidth = 140;
        double chartWidth = 400;
        double totalWidth = labelWidth + chartWidth + 40;
        double rowHeight = 32;
        double totalHeight = model.Tasks.Count * rowHeight + 60;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{totalWidth}\" height=\"{totalHeight}\" viewBox=\"0 0 {totalWidth} {totalHeight}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-gantt-svg\">");
        sb.AppendLine("""
            <style>
              .g-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #e6edf3; }
              .g-label { font-family: Segoe UI, sans-serif; font-size: 11px; fill: #8b949e; font-weight: 600; }
              .g-pct { font-family: Segoe UI, sans-serif; font-size: 10px; fill: #ffffff; font-weight: 600; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"g-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        DateTime min = model.MinDate;
        int totalDays = model.TotalDays;

        double y = 45;
        foreach (var t in model.Tasks)
        {
            double startOffsetDays = (t.StartDate - min).TotalDays;
            double durDays = Math.Max(1, (t.EndDate - t.StartDate).TotalDays);

            double barX = labelWidth + 20 + (startOffsetDays / totalDays) * chartWidth;
            double barW = Math.Max(12, (durDays / totalDays) * chartWidth);
            double progW = barW * (t.ProgressPercent / 100.0);

            // Label
            sb.AppendLine($"  <text x=\"20\" y=\"{y + 16}\" class=\"g-label\">{System.Net.WebUtility.HtmlEncode(t.Name)}</text>");

            // Track background
            sb.AppendLine($"  <rect x=\"{barX}\" y=\"{y + 2}\" width=\"{barW}\" height=\"18\" rx=\"4\" fill=\"#21262d\" stroke=\"#30363d\" />");

            // Progress bar
            if (progW > 0)
            {
                sb.AppendLine($"  <rect x=\"{barX}\" y=\"{y + 2}\" width=\"{progW}\" height=\"18\" rx=\"4\" fill=\"#1f6feb\" />");
            }

            // Percentage
            sb.AppendLine($"  <text x=\"{barX + barW / 2}\" y=\"{y + 15}\" class=\"g-pct\">{t.ProgressPercent}%</text>");

            y += rowHeight;
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
