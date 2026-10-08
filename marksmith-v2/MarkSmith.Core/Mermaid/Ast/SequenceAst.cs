namespace MarkSmith.Mermaid.Ast;

public enum SequenceParticipantType { Participant, Actor }

public sealed class SequenceParticipant
{
    public string Id { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public SequenceParticipantType Type { get; set; } = SequenceParticipantType.Participant;
    /// <summary>Introduced mid-conversation by a <c>create participant/actor</c> line (kept as a
    /// statement), so the generator must not declare it again up front: Mermaid rejects a
    /// participant declared twice.</summary>
    public bool CreatedInline { get; set; }
}

/// <summary>Mermaid's message arrows: ->> -->> -> --> -x --x -) --). PointArrow is the async -).</summary>
public enum SequenceMessageType { SolidArrow, DashedArrow, SolidOpen, DashedOpen, CrossArrow, PointArrow, DashedCross, DashedPoint }

public sealed class SequenceMessage
{
    public string FromId { get; set; } = string.Empty;
    public string ToId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public SequenceMessageType MessageType { get; set; } = SequenceMessageType.SolidArrow;
    public bool ActivateTarget { get; set; }
    public bool DeactivateTarget { get; set; }
}

public enum SequenceBlockType { Loop, Alt, Opt, Par, Critical, Break, Rect }

public sealed class SequenceBlock
{
    public SequenceBlockType BlockType { get; set; }
    public string HeaderText { get; set; } = string.Empty;
    public List<SequenceMessage> Messages { get; } = new();
    public List<(string Condition, List<SequenceMessage> Messages)> ElseBranches { get; } = new();
}

/// <summary>A participant group: <c>box [color] [label]</c> … <c>end</c> around participant
/// declarations. <see cref="Header"/> is the text after "box", kept verbatim.</summary>
public sealed class SequenceBox
{
    public string Header { get; set; } = string.Empty;
    public List<string> ParticipantIds { get; } = new();
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
    public List<SequenceBox> Boxes { get; } = new();
    public bool AutoNumber { get; set; }

    /// <summary>
    /// The conversation exactly as written, in order: messages, notes, activations, block
    /// openers/dividers/ends and any line the parser doesn't model (kept verbatim). The generator
    /// writes this back when it is present. <see cref="Messages"/>, <see cref="Blocks"/> and
    /// <see cref="Notes"/> are views of it (see <see cref="RebuildIndexes"/>): on their own they
    /// can't say where a note or a loop sits, so a round trip used to move every note to the top,
    /// every block after it and every plain message to the bottom.
    /// </summary>
    public List<SequenceStatement> Statements { get; } = new();

    /// <summary>Re-derives <see cref="Messages"/> (top-level ones), <see cref="Blocks"/> (every
    /// block, nested ones too, in opening order) and <see cref="Notes"/> from <see cref="Statements"/>.</summary>
    public void RebuildIndexes()
    {
        Messages.Clear();
        Blocks.Clear();
        Notes.Clear();
        var open = new Stack<SequenceBlock?>();
        foreach (var st in Statements)
        {
            switch (st.Kind)
            {
                case SequenceStatementKind.Message when st.Message is not null:
                    if (open.Count == 0) Messages.Add(st.Message);
                    else if (open.Peek() is { } b)
                    {
                        if (b.ElseBranches.Count > 0) b.ElseBranches[^1].Messages.Add(st.Message);
                        else b.Messages.Add(st.Message);
                    }
                    break;
                case SequenceStatementKind.Note when st.Note is not null:
                    Notes.Add(st.Note);
                    break;
                case SequenceStatementKind.BlockStart:
                    var block = new SequenceBlock { BlockType = st.BlockType, HeaderText = st.Text };
                    Blocks.Add(block);
                    open.Push(block);
                    break;
                case SequenceStatementKind.BlockDivider:
                    if (open.Count > 0 && open.Peek() is { } owner) owner.ElseBranches.Add((st.Text, new List<SequenceMessage>()));
                    break;
                case SequenceStatementKind.BlockEnd:
                    if (open.Count > 0) open.Pop();
                    break;
            }
        }
    }
}

public static class SequenceCreateLine
{
    private static readonly System.Text.RegularExpressions.Regex Rx =
        new(@"^(?:create|destroy)\s+(?:(?:participant|actor)\s+)?(?:""([^""]+)""|([^\s]+))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The participant a <c>create …</c> / <c>destroy …</c> line names, or null.</summary>
    public static string? Target(string? line)
    {
        var m = Rx.Match((line ?? string.Empty).Trim());
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    public static bool IsCreate(string? line) =>
        (line ?? string.Empty).TrimStart().StartsWith("create ", StringComparison.OrdinalIgnoreCase) && Target(line) is not null;
}

public enum SequenceStatementKind { Message, Note, Activate, Deactivate, BlockStart, BlockDivider, BlockEnd, Raw }

/// <summary>One line of a sequence diagram's body, in the order it was written.</summary>
public sealed class SequenceStatement
{
    public SequenceStatementKind Kind { get; init; }
    /// <summary>Kind Message.</summary>
    public SequenceMessage? Message { get; init; }
    /// <summary>Kind Note.</summary>
    public SequenceNote? Note { get; init; }
    /// <summary>Kind Activate / Deactivate: whose lifeline.</summary>
    public string ParticipantId { get; init; } = string.Empty;
    /// <summary>Kind BlockStart.</summary>
    public SequenceBlockType BlockType { get; init; }
    /// <summary>The keyword as Mermaid spells it: loop/alt/opt/par/critical/break/rect for a
    /// block start, else/and/option for a divider.</summary>
    public string Keyword { get; init; } = string.Empty;
    /// <summary>Block header or divider condition; for Raw, the whole line.</summary>
    public string Text { get; init; } = string.Empty;

    public static SequenceStatement ForMessage(SequenceMessage m) => new() { Kind = SequenceStatementKind.Message, Message = m };
    public static SequenceStatement ForNote(SequenceNote n) => new() { Kind = SequenceStatementKind.Note, Note = n };
}
