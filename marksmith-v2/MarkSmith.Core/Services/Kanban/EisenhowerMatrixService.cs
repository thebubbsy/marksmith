using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Kanban;

public enum EisenhowerQuadrant
{
    Q1_DoFirst,      // Urgent & Important
    Q2_Schedule,     // Not Urgent & Important
    Q3_Delegate,     // Urgent & Not Important
    Q4_Eliminate     // Not Urgent & Not Important
}

public record MatrixTaskItem(string TaskText, bool IsCompleted, EisenhowerQuadrant Quadrant, int LineNumber);

public class EisenhowerMatrixResult
{
    public List<MatrixTaskItem> Tasks { get; } = new();
    public int Q1Count => Tasks.Count(t => t.Quadrant == EisenhowerQuadrant.Q1_DoFirst);
    public int Q2Count => Tasks.Count(t => t.Quadrant == EisenhowerQuadrant.Q2_Schedule);
    public int Q3Count => Tasks.Count(t => t.Quadrant == EisenhowerQuadrant.Q3_Delegate);
    public int Q4Count => Tasks.Count(t => t.Quadrant == EisenhowerQuadrant.Q4_Eliminate);
}

/// <summary>
/// Service that scans Markdown task lists for urgency/importance tags and constructs an Eisenhower Priority Matrix.
/// </summary>
public static class EisenhowerMatrixService
{
    private static readonly Regex TaskRegex = new(@"^\s*-\s*\[([ xX])\]\s*(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Scans Markdown task list items and categorizes them into 4 priority quadrants.
    /// </summary>
    public static EisenhowerMatrixResult ParseMatrix(string markdown)
    {
        var result = new EisenhowerMatrixResult();
        if (string.IsNullOrWhiteSpace(markdown))
            return result;

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            var match = TaskRegex.Match(line);
            if (!match.Success) continue;

            bool isDone = match.Groups[1].Value.Trim().Equals("x", StringComparison.OrdinalIgnoreCase);
            string taskText = match.Groups[2].Value.Trim();

            var quad = ClassifyQuadrant(taskText);
            string cleanText = StripTags(taskText);

            result.Tasks.Add(new MatrixTaskItem(cleanText, isDone, quad, lineNum));
        }

        return result;
    }

    /// <summary>
    /// Renders an interactive 4-quadrant Eisenhower Matrix SVG diagram.
    /// </summary>
    public static string RenderMatrixSvg(EisenhowerMatrixResult matrix)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            <svg width="600" height="400" viewBox="0 0 600 400" xmlns="http://www.w3.org/2000/svg" class="ms-eisenhower-matrix">
              <style>
                .q-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; }
                .q-task { font-family: Segoe UI, sans-serif; font-size: 11px; fill: #e6edf3; }
                .axis-label { font-family: Segoe UI, sans-serif; font-size: 11px; fill: #8b949e; font-weight: 600; text-anchor: middle; }
              </style>
              <!-- Quadrant 1: Urgent & Important -->
              <rect x="50" y="40" width="240" height="150" rx="8" fill="#2d1517" stroke="#f85149" stroke-width="1.5" />
              <text x="60" y="65" fill="#ff7b72" class="q-title">Q1: DO FIRST (Urgent &amp; Important)</text>
              
              <!-- Quadrant 2: Not Urgent & Important -->
              <rect x="310" y="40" width="240" height="150" rx="8" fill="#15281e" stroke="#3fb950" stroke-width="1.5" />
              <text x="320" y="65" fill="#56d364" class="q-title">Q2: SCHEDULE (Important)</text>
              
              <!-- Quadrant 3: Urgent & Not Important -->
              <rect x="50" y="210" width="240" height="150" rx="8" fill="#292014" stroke="#d29922" stroke-width="1.5" />
              <text x="60" y="235" fill="#e3b341" class="q-title">Q3: DELEGATE (Urgent)</text>
              
              <!-- Quadrant 4: Not Urgent & Not Important -->
              <rect x="310" y="210" width="240" height="150" rx="8" fill="#161b22" stroke="#30363d" stroke-width="1.5" />
              <text x="320" y="235" fill="#8b949e" class="q-title">Q4: ELIMINATE (Low Priority)</text>
            """);

        // Populate task items
        RenderQuadrantTasks(sb, matrix.Tasks.Where(t => t.Quadrant == EisenhowerQuadrant.Q1_DoFirst), 60, 90);
        RenderQuadrantTasks(sb, matrix.Tasks.Where(t => t.Quadrant == EisenhowerQuadrant.Q2_Schedule), 320, 90);
        RenderQuadrantTasks(sb, matrix.Tasks.Where(t => t.Quadrant == EisenhowerQuadrant.Q3_Delegate), 60, 260);
        RenderQuadrantTasks(sb, matrix.Tasks.Where(t => t.Quadrant == EisenhowerQuadrant.Q4_Eliminate), 320, 260);

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void RenderQuadrantTasks(StringBuilder sb, IEnumerable<MatrixTaskItem> tasks, double x, double startY)
    {
        double y = startY;
        foreach (var t in tasks.Take(4))
        {
            string bullet = t.IsCompleted ? "[x]" : "•";
            string text = t.TaskText.Length > 28 ? t.TaskText.Substring(0, 25) + "..." : t.TaskText;
            sb.AppendLine($"  <text x=\"{x}\" y=\"{y}\" class=\"q-task\">{bullet} {System.Net.WebUtility.HtmlEncode(text)}</text>");
            y += 20;
        }
    }

    private static EisenhowerQuadrant ClassifyQuadrant(string text)
    {
        bool urgent = text.Contains("#urgent", StringComparison.OrdinalIgnoreCase) || text.Contains("#p1", StringComparison.OrdinalIgnoreCase);
        bool important = text.Contains("#important", StringComparison.OrdinalIgnoreCase) || text.Contains("#p1", StringComparison.OrdinalIgnoreCase) || text.Contains("#p2", StringComparison.OrdinalIgnoreCase);
        bool notUrgent = text.Contains("#not-urgent", StringComparison.OrdinalIgnoreCase) || text.Contains("#p4", StringComparison.OrdinalIgnoreCase);
        bool notImportant = text.Contains("#not-important", StringComparison.OrdinalIgnoreCase) || text.Contains("#p4", StringComparison.OrdinalIgnoreCase);

        if (notUrgent) urgent = false;
        if (notImportant) important = false;

        if (urgent && important) return EisenhowerQuadrant.Q1_DoFirst;
        if (!urgent && important) return EisenhowerQuadrant.Q2_Schedule;
        if (urgent && !important) return EisenhowerQuadrant.Q3_Delegate;
        return EisenhowerQuadrant.Q4_Eliminate;
    }

    private static string StripTags(string text)
    {
        return Regex.Replace(text, @"#(?:urgent|important|not-urgent|not-important|p[1-4])\b", "", RegexOptions.IgnoreCase).Trim();
    }
}
