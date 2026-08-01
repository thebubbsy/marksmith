namespace MdToPdf.Mermaid.Ast;

public enum MindmapNodeShape
{
    Default,
    Square,     // [text]
    Rounded,    // (text)
    Circle,     // ((text))
    Cloud,      // )text(
    Bang        // ))text((
}

public sealed class MindmapNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = string.Empty;
    public MindmapNodeShape Shape { get; set; } = MindmapNodeShape.Default;
    public string? Icon { get; set; } // e.g. ::icon(fa fa-book)
    public int IndentLevel { get; set; }
    public List<MindmapNode> Children { get; } = new();
}

public sealed class MindmapAst : MermaidDiagramAst
{
    public override MermaidDiagramType DiagramType => MermaidDiagramType.Mindmap;
    public MindmapNode? Root { get; set; }
}
