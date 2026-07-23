namespace MdToPdf.Mermaid.Ast;

public enum StateNodeType { Normal, Start, End, Choice, Fork, Join, Composite }

public sealed class StateNode
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public StateNodeType Type { get; set; } = StateNodeType.Normal;
    public List<StateNode> SubStates { get; } = new();
    public List<StateTransition> SubTransitions { get; } = new();
}

public sealed class StateTransition
{
    public string FromId { get; set; } = string.Empty;
    public string ToId { get; set; } = string.Empty;
    public string? EventLabel { get; set; }
}

public sealed class StateDiagramAst : MermaidDiagramAst
{
    public override MermaidDiagramType DiagramType => MermaidDiagramType.State;
    public bool IsV2 { get; set; } = true;
    public Dictionary<string, StateNode> States { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StateTransition> Transitions { get; } = new();
}
