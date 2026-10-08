using System;
using System.Linq;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using MarkSmith.Services;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>Bugs a review of the Diagram Studio save work found.</summary>
public class Pr128ReviewFixTests
{
    private static string Save(string code)
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(code);
        return vm.GenerateMermaidCode().Replace("\r\n", "\n");
    }

    [Fact]
    public void A_class_member_with_no_visibility_keeps_its_type_and_name_apart()
    {
        Assert.Equal("String name", MermaidCodeGenerator.FormatClassMember(new ClassMember { Name = "name", Type = "String", Visibility = ClassVisibility.None }));
        Assert.Equal("+String name", MermaidCodeGenerator.FormatClassMember(new ClassMember { Name = "name", Type = "String", Visibility = ClassVisibility.Public }));
        Assert.Equal("-count", MermaidCodeGenerator.FormatClassMember(new ClassMember { Name = "count", Visibility = ClassVisibility.Private }));
    }

    [Fact]
    public void A_participant_created_inside_a_box_is_declared_once()
    {
        var code = Save("sequenceDiagram\n    box Team\n    participant A\n    participant B\n    end\n    A->>A: start\n    create participant B\n    A->>B: hello\n");
        Assert.Single(code.Split('\n'), l => System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), @"^(create )?participant B\b"));
        Assert.Contains("create participant B", code);
    }

    [Theory]
    [InlineData("-)", SequenceMessageType.PointArrow)]
    [InlineData("--)", SequenceMessageType.DashedPoint)]
    [InlineData("-x", SequenceMessageType.CrossArrow)]
    [InlineData("--x", SequenceMessageType.DashedCross)]
    [InlineData("->>", SequenceMessageType.SolidArrow)]
    [InlineData("-->>", SequenceMessageType.DashedArrow)]
    public void Every_mermaid_arrow_is_a_message_and_is_written_back_the_same(string arrow, SequenceMessageType type)
    {
        var ast = Assert.IsType<SequenceDiagramAst>(MermaidParser.Parse($"sequenceDiagram\n    Alice{arrow}Bob: hi\n").Ast);
        var msg = Assert.Single(ast.Messages);
        Assert.Equal(type, msg.MessageType);
        Assert.Contains($"Alice{arrow}Bob: hi", MermaidCodeGenerator.Generate(ast));
    }

    [Fact]
    public void Participants_with_an_x_in_their_name_have_their_messages_read()
    {
        var ast = Assert.IsType<SequenceDiagramAst>(MermaidParser.Parse("sequenceDiagram\n    Alex->>Max: ping\n    Max-->>Alex: pong\n").Ast);
        Assert.Equal(2, ast.Messages.Count);
        Assert.Equal("Alex", ast.Messages[0].FromId);
        Assert.Equal("Max", ast.Messages[0].ToId);
    }

    [Fact]
    public void An_older_save_written_with_backslash_reads_as_async_and_saves_as_mermaid()
    {
        var code = Save("sequenceDiagram\n    A-\\B: later\n");
        Assert.Contains("A-)B: later", code);
    }

    [Fact]
    public void A_note_on_a_state_inside_a_composite_is_written_inside_it()
    {
        var code = Save("stateDiagram-v2\n    state Outer {\n        Inner --> Done\n        note right of Inner : hi\n    }\n");
        int open = code.IndexOf("state Outer {", StringComparison.Ordinal), close = code.IndexOf('}', open);
        int note = code.IndexOf("note right of Inner", StringComparison.Ordinal);
        Assert.True(open >= 0 && note > open && note < close, code);
        var ast = Assert.IsType<StateDiagramAst>(MermaidParser.Parse(code).Ast);
        Assert.DoesNotContain(ast.States.Keys, k => k == "Inner");
    }
}
