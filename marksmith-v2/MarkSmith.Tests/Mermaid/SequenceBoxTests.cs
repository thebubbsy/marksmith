using System;
using System.Linq;
using MarkSmith.Core.Mermaid.Routing;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>
/// Participant boxes (<c>box Aqua Team … end</c>) were parsed and thrown away, so every save
/// from Diagram Studio ungrouped them. They round-trip now and are drawn behind their participants.
/// </summary>
public class SequenceBoxTests
{
    private const string Grouped =
        "sequenceDiagram\n" +
        "    box Aqua Front end\n" +
        "        actor U as User\n" +
        "        participant W as Web\n" +
        "    end\n" +
        "    box rgb(200, 100, 50) Back end\n" +
        "        participant API\n" +
        "    end\n" +
        "    participant DB\n" +
        "    U->>W: click\n" +
        "    W->>API: call\n" +
        "    API->>DB: query\n";

    [Fact]
    public void Boxes_parse_with_their_participants_and_do_not_swallow_messages()
    {
        var ast = Assert.IsType<SequenceDiagramAst>(MermaidParser.Parse(Grouped).Ast);
        Assert.Equal(2, ast.Boxes.Count);
        Assert.Equal("Aqua Front end", ast.Boxes[0].Header);
        Assert.Equal(new[] { "U", "W" }, ast.Boxes[0].ParticipantIds);
        Assert.Equal(new[] { "API" }, ast.Boxes[1].ParticipantIds);
        Assert.Equal(3, ast.Messages.Count);
        Assert.Empty(ast.Blocks);
    }

    [Fact]
    public void Boxes_round_trip()
    {
        string once = MermaidCodeGenerator.Generate(MermaidParser.Parse(Grouped).Ast!);
        Assert.Contains("box Aqua Front end\n        actor U as User\n        participant W as Web\n    end", once.Replace("\r\n", "\n"));
        Assert.Contains("box rgb(200, 100, 50) Back end", once);
        Assert.Equal(once, MermaidCodeGenerator.Generate(MermaidParser.Parse(once).Ast!));
    }

    [Fact]
    public void Studio_keeps_boxes_and_drops_deleted_members()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(Grouped);
        Assert.Contains("box Aqua Front end", vm.GenerateMermaidCode());

        vm.SelectNode(vm.Nodes.Single(n => n.Id == "API"));
        vm.DeleteSelected();
        var code = vm.GenerateMermaidCode();
        Assert.DoesNotContain("Back end", code); // its only member went, so the box goes too
        Assert.Contains("box Aqua Front end", code);
    }

    [Fact]
    public void Box_panels_sit_behind_their_participants_with_room_for_the_label()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(Grouped);
        Assert.Equal(2, vm.SequenceBoxes.Count);
        var front = vm.SequenceBoxes[0];
        Assert.Equal("Front end", front.Label);
        Assert.Equal("#3300FFFF", front.Fill);
        foreach (var id in new[] { "U", "W" })
        {
            var n = vm.Nodes.Single(x => x.Id == id);
            Assert.True(front.X < n.X && front.X + front.Width > n.X + n.Width);
            Assert.True(front.Y + SequenceLayout.BoxLabelBand - 1 <= n.Y);
            Assert.True(front.Y + front.Height >= n.LifelineBottom);
        }
        var db = vm.Nodes.Single(x => x.Id == "DB");
        Assert.All(vm.SequenceBoxes, b => Assert.False(b.X < db.LifelineX && db.LifelineX < b.X + b.Width));
        Assert.True(vm.GetContentBounds()!.Value.Y <= front.Y, "Fit includes the box label band");
    }

    [Theory]
    [InlineData("Aqua Team", "#3300FFFF", "Team")]
    [InlineData("rgb(33,66,99) Team", "#33214263", "Team")]
    [InlineData("rgba(33, 66, 99, 0.5)", "#33214263", "")]
    [InlineData("#f0a Team", "#33FF00AA", "Team")]
    [InlineData("transparent Team", SequenceLayout.DefaultBoxFill, "Team")]
    [InlineData("Just a label", SequenceLayout.DefaultBoxFill, "Just a label")]
    [InlineData("", SequenceLayout.DefaultBoxFill, "")]
    public void Box_headers_split_into_fill_and_label(string header, string fill, string label)
    {
        var (f, l) = SequenceLayout.ReadBoxHeader(header);
        Assert.Equal(fill, f);
        Assert.Equal(label, l);
    }
}
