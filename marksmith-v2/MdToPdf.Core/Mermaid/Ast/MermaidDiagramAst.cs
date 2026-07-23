namespace MdToPdf.Mermaid.Ast;

public enum MermaidDiagramType
{
    Flowchart,
    Sequence,
    Class,
    State,
    Gantt,
    Er,
    Mindmap
}

public abstract class MermaidDiagramAst
{
    public abstract MermaidDiagramType DiagramType { get; }
    public string Title { get; set; } = string.Empty;
    public List<string> Directives { get; } = new();
    public List<string> Comments { get; } = new();
}
