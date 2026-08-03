namespace MarkSmith.Mermaid.Ast;

[Flags]
public enum GanttTaskStatus
{
    Normal = 0,
    Active = 1 << 0,
    Done = 1 << 1,
    Crit = 1 << 2
}

public sealed class GanttTask
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public GanttTaskStatus Status { get; set; } = GanttTaskStatus.Normal;
    public bool IsMilestone { get; set; }
    public string? StartDate { get; set; }       // e.g. "2024-01-01" or "after des1"
    public string DurationOrEndDate { get; set; } = string.Empty; // e.g. "30d" or "2024-01-31"
    public string? AfterTaskId { get; set; }
}

public sealed class GanttSection
{
    public string Name { get; set; } = string.Empty;
    public List<GanttTask> Tasks { get; } = new();
}

public sealed class GanttChartAst : MermaidDiagramAst
{
    public override MermaidDiagramType DiagramType => MermaidDiagramType.Gantt;
    public string DateFormat { get; set; } = "YYYY-MM-DD";
    public string AxisFormat { get; set; } = "%Y-%m-%d";
    public List<GanttSection> Sections { get; } = new();
}
