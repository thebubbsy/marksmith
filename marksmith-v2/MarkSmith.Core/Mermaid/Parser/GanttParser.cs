namespace MarkSmith.Mermaid.Parser;

using System.Text.RegularExpressions;
using MarkSmith.Mermaid.Ast;

public static class GanttParser
{
    public static GanttChartAst Parse(string code)
    {
        var ast = new GanttChartAst();
        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList();

        GanttSection? currentSection = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("%%"))
            {
                if (line.StartsWith("%%{"))
                    ast.Directives.Add(line);
                else
                    ast.Comments.Add(line.Substring(2).Trim());
                continue;
            }

            string lower = line.ToLowerInvariant();
            if (lower == "gantt")
                continue;

            if (lower.StartsWith("title "))
            {
                ast.Title = line.Substring(6).Trim();
                continue;
            }

            if (lower.StartsWith("dateformat "))
            {
                ast.DateFormat = line.Substring(11).Trim();
                continue;
            }

            if (lower.StartsWith("axisformat "))
            {
                ast.AxisFormat = line.Substring(11).Trim();
                continue;
            }

            if (lower.StartsWith("section "))
            {
                string secName = line.Substring(8).Trim();
                currentSection = new GanttSection { Name = secName };
                ast.Sections.Add(currentSection);
                continue;
            }

            // Task line: `Task Name : metadata...`
            int colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                string taskName = line.Substring(0, colonIdx).Trim();
                string meta = line.Substring(colonIdx + 1).Trim();

                if (currentSection == null)
                {
                    currentSection = new GanttSection { Name = "Default" };
                    ast.Sections.Add(currentSection);
                }

                var task = ParseTask(taskName, meta);
                currentSection.Tasks.Add(task);
            }
        }

        return ast;
    }

    private static GanttTask ParseTask(string name, string meta)
    {
        var parts = meta.Split(',').Select(p => p.Trim()).ToList();
        var task = new GanttTask { Name = name };

        GanttTaskStatus status = GanttTaskStatus.Normal;
        bool isMilestone = false;

        int index = 0;
        // Parse status keywords in initial parts
        while (index < parts.Count)
        {
            string pLower = parts[index].ToLowerInvariant();
            if (pLower == "active") { status |= GanttTaskStatus.Active; index++; }
            else if (pLower == "done") { status |= GanttTaskStatus.Done; index++; }
            else if (pLower == "crit") { status |= GanttTaskStatus.Crit; index++; }
            else if (pLower == "milestone") { isMilestone = true; index++; }
            else break;
        }

        task.Status = status;
        task.IsMilestone = isMilestone;

        List<string> remaining = parts.Skip(index).ToList();

        if (remaining.Count == 1)
        {
            task.DurationOrEndDate = remaining[0];
        }
        else if (remaining.Count == 2)
        {
            if (IsStartDate(remaining[0]))
            {
                task.StartDate = remaining[0];
                task.DurationOrEndDate = remaining[1];
                if (remaining[0].StartsWith("after ", StringComparison.OrdinalIgnoreCase))
                {
                    task.AfterTaskId = remaining[0].Substring(6).Trim();
                }
            }
            else
            {
                task.Id = remaining[0];
                task.DurationOrEndDate = remaining[1];
            }
        }
        else if (remaining.Count >= 3)
        {
            task.Id = remaining[0];
            task.StartDate = remaining[1];
            task.DurationOrEndDate = remaining[2];

            if (remaining[1].StartsWith("after ", StringComparison.OrdinalIgnoreCase))
            {
                task.AfterTaskId = remaining[1].Substring(6).Trim();
            }
        }

        if (string.IsNullOrEmpty(task.Id))
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(task.Name));
            task.Id = "t_" + Convert.ToHexString(hashBytes)[..6].ToLowerInvariant();
        }

        return task;
    }

    private static bool IsStartDate(string s)
    {
        if (s.StartsWith("after ", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Regex.IsMatch(s, @"^\d{4}[-/.]\d{2}[-/.]\d{2}"))
            return true;
        return false;
    }
}
