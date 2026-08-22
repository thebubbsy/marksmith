using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Project;

public record GanttScheduleTask(string Name, DateTime StartDate, DateTime EndDate, int ProgressPercent, string? Predecessor);
public record GanttScheduleMilestone(string Name, DateTime Date);

public class GanttModel
{
    public string Title { get; set; } = "Project Schedule";
    public List<GanttScheduleTask> Tasks { get; } = new();
    public List<GanttScheduleMilestone> Milestones { get; } = new();
}

/// <summary>
/// Service for parsing Gantt project schedules and rendering SVG timeline charts with progress fills and milestones.
/// </summary>
public static class GanttScheduleRendererService
{
    private static readonly Regex GanttFenceRegex = new(
        @":::gantt(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex TaskRegex = new(
        @"task\s*""([^""]+)""\s*(\d{4}-\d{2}-\d{2})\s*->\s*(\d{4}-\d{2}-\d{2})(?:\s*\[progress:\s*(\d+)\%?\])?(?:\s*after\s*""([^""]+)"")?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MilestoneRegex = new(
        @"milestone\s*""([^""]+)""\s*(\d{4}-\d{2}-\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a Gantt markdown block into tasks and milestones.
    /// </summary>
    public static GanttModel ParseGantt(string blockText, string defaultTitle = "Sprint Schedule")
    {
        var model = new GanttModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = GanttFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var tm = TaskRegex.Match(l);
            if (tm.Success)
            {
                string name = tm.Groups[1].Value.Trim();
                DateTime start = DateTime.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture);
                DateTime end = DateTime.Parse(tm.Groups[3].Value, CultureInfo.InvariantCulture);
                int prog = tm.Groups[4].Success ? int.Parse(tm.Groups[4].Value) : 0;
                string? pred = tm.Groups[5].Success ? tm.Groups[5].Value.Trim() : null;
                model.Tasks.Add(new GanttScheduleTask(name, start, end, prog, pred));
                continue;
            }

            var mm = MilestoneRegex.Match(l);
            if (mm.Success)
            {
                string name = mm.Groups[1].Value.Trim();
                DateTime date = DateTime.Parse(mm.Groups[2].Value, CultureInfo.InvariantCulture);
                model.Milestones.Add(new GanttScheduleMilestone(name, date));
                continue;
            }
        }

        return model;
    }

    /// <summary>
    /// Renders an SVG Gantt chart.
    /// </summary>
    public static string RenderGanttSvg(GanttModel model)
    {
        int rowCount = model.Tasks.Count + model.Milestones.Count;
        if (rowCount == 0)
            return "<svg width=\"400\" height=\"100\"></svg>";

        DateTime minDate = DateTime.MaxValue;
        DateTime maxDate = DateTime.MinValue;

        foreach (var t in model.Tasks)
        {
            if (t.StartDate < minDate) minDate = t.StartDate;
            if (t.EndDate > maxDate) maxDate = t.EndDate;
        }
        foreach (var m in model.Milestones)
        {
            if (m.Date < minDate) minDate = m.Date;
            if (m.Date > maxDate) maxDate = m.Date;
        }

        double totalDays = Math.Max(1, (maxDate - minDate).TotalDays);
        double width = 520;
        double height = 70 + rowCount * 36;
        double labelW = 140;
        double chartW = width - labelW - 30;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-gantt-svg\">");
        sb.AppendLine("""
            <style>
              .gt-bg { fill: #0d1117; stroke: #30363d; stroke-width: 1.5; }
              .gt-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #e6edf3; }
              .gt-grid { stroke: #21262d; stroke-width: 1; }
              .gt-label { font-family: Segoe UI, sans-serif; font-size: 11px; font-weight: 600; fill: #c9d1d9; }
              .gt-bar-bg { fill: #21262d; rx: 4; }
              .gt-bar-fill { fill: #238636; rx: 4; }
              .gt-ms-diamond { fill: #d29922; stroke: #f0883e; stroke-width: 1.5; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"gt-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"gt-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        double curY = 55;

        // Tasks
        foreach (var t in model.Tasks)
        {
            double sOffset = ((t.StartDate - minDate).TotalDays / totalDays) * chartW;
            double dur = (((t.EndDate - t.StartDate).TotalDays) / totalDays) * chartW;
            dur = Math.Max(12, dur);

            sb.AppendLine($"  <text x=\"20\" y=\"{curY + 14}\" class=\"gt-label\">{System.Net.WebUtility.HtmlEncode(t.Name)}</text>");
            sb.AppendLine($"  <rect x=\"{labelW + sOffset}\" y=\"{curY}\" width=\"{dur}\" height=\"18\" class=\"gt-bar-bg\" />");
            if (t.ProgressPercent > 0)
            {
                double progW = dur * (Math.Min(100, t.ProgressPercent) / 100.0);
                sb.AppendLine($"  <rect x=\"{labelW + sOffset}\" y=\"{curY}\" width=\"{progW}\" height=\"18\" class=\"gt-bar-fill\" />");
            }
            curY += 36;
        }

        // Milestones
        foreach (var m in model.Milestones)
        {
            double mOffset = ((m.Date - minDate).TotalDays / totalDays) * chartW;
            double mx = labelW + mOffset;
            double my = curY + 8;

            sb.AppendLine($"  <text x=\"20\" y=\"{curY + 14}\" class=\"gt-label\">{System.Net.WebUtility.HtmlEncode(m.Name)}</text>");
            sb.AppendLine($"  <polygon points=\"{mx},{my - 7} {mx + 7},{my} {mx},{my + 7} {mx - 7},{my}\" class=\"gt-ms-diamond\" />");
            curY += 36;
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
