namespace MdToPdf.Mermaid.Ast;

public enum FlowDirection { TD, TB, LR, BT, RL }

public enum FlowNodeShape
{
    Rectangle,           // [label]
    RoundedRectangle,    // (label)
    Stadium,             // ([label])
    Subroutine,          // [[label]]
    CylindricalDatabase, // [(label)]
    Circle,              // ((label))
    Asymmetric,          // >label]
    RhombusDiamond,      // {label}
    Hexagon,             // {{label}}
    Parallelogram,       // [/label/] or [\label\]
    Trapezoid            // [/label\] or [\label/]
}

public enum FlowLineStyle { Solid, Dashed, Thick }
public enum FlowArrowHead { Normal, Cross, Circle, None }

public sealed class FlowNode
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public FlowNodeShape Shape { get; set; } = FlowNodeShape.Rectangle;
    public string? SubgraphId { get; set; }
}

public sealed class FlowEdge
{
    public string FromId { get; set; } = string.Empty;
    public string ToId { get; set; } = string.Empty;
    public FlowLineStyle LineStyle { get; set; } = FlowLineStyle.Solid;
    public FlowArrowHead StartHead { get; set; } = FlowArrowHead.None;
    public FlowArrowHead EndHead { get; set; } = FlowArrowHead.Normal;
    public string? Label { get; set; }
}

public sealed class FlowSubgraph
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> NodeIds { get; } = new();
    public List<FlowSubgraph> NestedSubgraphs { get; } = new();
}

public sealed class FlowchartDiagramAst : MermaidDiagramAst
{
    public override MermaidDiagramType DiagramType => MermaidDiagramType.Flowchart;
    public FlowDirection Direction { get; set; } = FlowDirection.TD;
    public Dictionary<string, FlowNode> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FlowEdge> Edges { get; } = new();
    public List<FlowSubgraph> Subgraphs { get; } = new();
}
