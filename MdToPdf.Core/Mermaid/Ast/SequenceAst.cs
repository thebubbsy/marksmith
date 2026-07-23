namespace MdToPdf.Mermaid.Ast;

public enum SequenceParticipantType { Participant, Actor }

public sealed class SequenceParticipant
{
    public string Id { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public SequenceParticipantType Type { get; set; } = SequenceParticipantType.Participant;
}

public enum SequenceMessageType { SolidArrow, DashedArrow, SolidOpen, DashedOpen, CrossArrow, PointArrow }

public sealed class SequenceMessage
{
    public string FromId { get; set; } = string.Empty;
    public string ToId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public SequenceMessageType MessageType { get; set; } = SequenceMessageType.SolidArrow;
    public bool ActivateTarget { get; set; }
    public bool DeactivateTarget { get; set; }
}

public enum SequenceBlockType { Loop, Alt, Opt, Par, Critical }

public sealed class SequenceBlock
{
    public SequenceBlockType BlockType { get; set; }
    public string HeaderText { get; set; } = string.Empty;
    public List<SequenceMessage> Messages { get; } = new();
    public List<(string Condition, List<SequenceMessage> Messages)> ElseBranches { get; } = new();
}

public enum NotePlacement { LeftOf, RightOf, Over }

public sealed class SequenceNote
{
    public NotePlacement Placement { get; set; }
    public List<string> TargetParticipantIds { get; } = new();
    public string Text { get; set; } = string.Empty;
}

public sealed class SequenceDiagramAst : MermaidDiagramAst
{
    public override MermaidDiagramType DiagramType => MermaidDiagramType.Sequence;
    public List<SequenceParticipant> Participants { get; } = new();
    public List<SequenceMessage> Messages { get; } = new();
    public List<SequenceBlock> Blocks { get; } = new();
    public List<SequenceNote> Notes { get; } = new();
    public bool AutoNumber { get; set; }
}
