namespace MarkSmith.Mermaid.Ast;

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
    /// <summary>Notes kept verbatim: <c>note right of X : text</c>, or a multi-line note as its
    /// lines joined with '\n' (opening line, body lines, <c>end note</c>).</summary>
    public List<string> Notes { get; } = new();

    /// <summary>The state a note is attached to ("note left of X…"), or null.</summary>
    public static string? NoteTarget(string note)
    {
        var m = System.Text.RegularExpressions.Regex.Match(note, @"^note\s+(?:left|right)\s+of\s+([^\s:]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
