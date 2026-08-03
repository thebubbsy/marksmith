namespace MarkSmith.Core.Tests.Mermaid;

using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using Xunit;

public class SequenceRoundtripTests
{
    [Fact]
    public void Sequence_ParticipantsAndMessages_ParsesCorrectly()
    {
        string code = @"sequenceDiagram
    autonumber
    actor U as User
    participant A as API
    U->>+A: Request Data
    A-->>-U: Response Data";

        var result = MermaidParser.Parse(code);
        Assert.True(result.IsSuccess);
        var ast = Assert.IsType<SequenceDiagramAst>(result.Ast);

        Assert.True(ast.AutoNumber);
        Assert.Equal(2, ast.Participants.Count);
        Assert.Equal(SequenceParticipantType.Actor, ast.Participants[0].Type);
        Assert.Equal("User", ast.Participants[0].Alias);
        Assert.Equal(2, ast.Messages.Count);
        Assert.True(ast.Messages[0].ActivateTarget);
        Assert.True(ast.Messages[1].DeactivateTarget);
    }

    [Fact]
    public void Sequence_LoopBlockAndNotes_ParsesCorrectly()
    {
        string code = @"sequenceDiagram
    participant A
    participant B
    Note over A,B: Sync Process
    loop Every 5s
        A->>B: Ping
    end";

        var result = MermaidParser.Parse(code);
        Assert.True(result.IsSuccess);
        var ast = Assert.IsType<SequenceDiagramAst>(result.Ast);

        Assert.Single(ast.Notes);
        Assert.Equal(NotePlacement.Over, ast.Notes[0].Placement);
        Assert.Single(ast.Blocks);
        Assert.Equal(SequenceBlockType.Loop, ast.Blocks[0].BlockType);
        Assert.Equal("Every 5s", ast.Blocks[0].HeaderText);
    }

    [Theory]
    [InlineData("sequenceDiagram\n    participant Alice\n    participant Bob\n    Alice->>Bob: Hello\n")]
    public void Sequence_ParseAndGenerate_IsIdempotent(string inputCode)
    {
        var result1 = MermaidParser.Parse(inputCode);
        Assert.True(result1.IsSuccess);

        string gen1 = MermaidCodeGenerator.Generate(result1.Ast!);
        var result2 = MermaidParser.Parse(gen1);
        Assert.True(result2.IsSuccess);

        string gen2 = MermaidCodeGenerator.Generate(result2.Ast!);
        Assert.Equal(gen1, gen2);
    }
}
