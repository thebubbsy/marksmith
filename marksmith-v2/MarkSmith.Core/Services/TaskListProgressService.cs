using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Computes task-list completion metrics from a Markdown document (Task 48). Scans for GFM checkbox
/// items — <c>- [ ]</c> (open) and <c>- [x]</c>/<c>- [X]</c> (done) — across both bullet and ordered
/// lists, and returns the completed count, total count, and completion percentage. Checkbox-looking
/// text inside fenced code blocks is ignored (it's sample code, not a task), matching how the
/// renderer treats fences. Indented/nested task items count too.
/// </summary>
public static partial class TaskListProgressService
{
    // A GFM task item: up to three leading spaces, a bullet (-,*,+) or an ordered marker (1. / 1)),
    // whitespace, then a checkbox. The captured char is ' ' (open) or 'x'/'X' (done).
    [GeneratedRegex(@"^\s{0,3}(?:[-*+]|\d+[.)])\s+\[([ xX])\]")]
    private static partial Regex TaskItemRe();

    /// <summary>Returns completion metrics; <c>Total == 0</c> means the document has no task items.</summary>
    public static TaskListProgress Calculate(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return TaskListProgress.Empty;

        int completed = 0, total = 0;
        string? fenceMarker = null; // the ``` / ~~~ run that opened the current code block, or null

        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = raw.TrimStart();

            if (fenceMarker is null)
            {
                var marker = FenceMarker(trimmed);
                if (marker is not null) { fenceMarker = marker; continue; }

                var m = TaskItemRe().Match(raw);
                if (m.Success)
                {
                    total++;
                    if (m.Groups[1].Value is "x" or "X") completed++;
                }
            }
            else if (trimmed.StartsWith(fenceMarker, StringComparison.Ordinal))
            {
                fenceMarker = null; // closing fence — resume scanning on the next line
            }
        }

        return new TaskListProgress(completed, total);
    }

    // The run of >=3 backticks/tildes a line opens with, or null (mirrors DocumentStatsService).
    private static string? FenceMarker(string trimmed)
    {
        char c = trimmed.Length > 0 ? trimmed[0] : '\0';
        if (c != '`' && c != '~') return null;
        int n = 0;
        while (n < trimmed.Length && trimmed[n] == c) n++;
        return n >= 3 ? new string(c, n) : null;
    }
}

/// <summary>Immutable task-list completion snapshot.</summary>
/// <param name="Completed">Count of <c>[x]</c>/<c>[X]</c> items.</param>
/// <param name="Total">Count of all checkbox items (open + done).</param>
public readonly record struct TaskListProgress(int Completed, int Total)
{
    public static TaskListProgress Empty => new(0, 0);

    public bool HasTasks => Total > 0;

    /// <summary>Completion as a percentage 0-100 (one decimal); 0 when there are no tasks.</summary>
    public double Percentage => Total == 0 ? 0 : Math.Round(Completed * 100.0 / Total, 1);

    /// <summary>Display form, e.g. "66.7%" (or "0%" when empty).</summary>
    public string PercentageText => $"{Percentage.ToString(Percentage == Math.Floor(Percentage) ? "0" : "0.#")}%";

    /// <summary>Compact status-bar form, e.g. "2/3 tasks done (66.7%)".</summary>
    public string SummaryText => HasTasks
        ? $"{Completed}/{Total} tasks done ({PercentageText})"
        : "No tasks";
}
