using System;
using System.Linq;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>
/// A sequence message's place is its order, and there was no way to change it on the canvas.
/// ↑/↓ on a selected message now moves it a row, across block boundaries, with undo. Separately,
/// "autonumber" was kept in the code but never drawn.
/// </summary>
public class SequenceReorderTests
{
    private const string Flow =
        "sequenceDiagram\n" +
        "    participant A\n" +
        "    participant B\n" +
        "    A->>B: first\n" +
        "    loop retry\n" +
        "        B->>A: second\n" +
        "    end\n" +
        "    A->>B: third\n";

    private static MermaidStudioViewModel Load(string code)
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(code);
        return vm;
    }

    private static DiagramConnectorViewModel Select(MermaidStudioViewModel vm, string label)
    {
        vm.ClearSelection();
        var c = vm.Connectors.Single(x => x.Label == label);
        vm.SelectedConnector = c;
        return c;
    }

    private static void AssertInOrder(string code, params string[] needles)
    {
        for (int i = 1; i < needles.Length; i++)
        {
            int a = code.IndexOf(needles[i - 1], StringComparison.Ordinal), b = code.IndexOf(needles[i], StringComparison.Ordinal);
            Assert.True(a >= 0 && b >= 0 && a < b, $"'{needles[i - 1]}' should come before '{needles[i]}' in:\n{code}");
        }
    }

    [Fact]
    public void Down_moves_a_message_into_the_next_slot_inside_a_block()
    {
        var vm = Load(Flow);
        var first = Select(vm, "first");
        Assert.True(vm.MoveSelectedMessage(+1));
        var code = vm.GenerateMermaidCode();
        // "first" takes the slot inside the loop; "second" takes the one before it.
        AssertInOrder(code, "B->>A: second", "loop retry", "A->>B: first", "end", "A->>B: third");
        Assert.True(first.SourceY > vm.Connectors.Single(c => c.Label == "second").SourceY, "the canvas row moved too");
    }

    [Fact]
    public void Up_and_down_are_inverse_and_stop_at_the_ends()
    {
        var vm = Load(Flow);
        string before = vm.GenerateMermaidCode();
        Select(vm, "third");
        Assert.False(vm.MoveSelectedMessage(+1));
        Assert.True(vm.MoveSelectedMessage(-1));
        Assert.True(vm.MoveSelectedMessage(+1));
        Assert.Equal(before, vm.GenerateMermaidCode());
        Select(vm, "first");
        Assert.False(vm.MoveSelectedMessage(-1));
    }

    [Fact]
    public void Arrow_keys_on_a_selected_message_reorder_instead_of_nudging()
    {
        var vm = Load(Flow);
        Select(vm, "third");
        vm.NudgeSelected(0, -1, coarse: false);
        AssertInOrder(vm.GenerateMermaidCode(), "A->>B: first", "loop retry", "A->>B: third", "end", "B->>A: second");
    }

    [Fact]
    public void A_move_can_be_undone()
    {
        var vm = Load(Flow);
        string before = vm.GenerateMermaidCode();
        Select(vm, "first");
        vm.MoveSelectedMessage(+1);
        vm.Undo();
        Assert.Equal(before, vm.GenerateMermaidCode());
    }

    [Fact]
    public void A_newly_drawn_message_can_be_moved_up_into_the_conversation()
    {
        var vm = Load(Flow);
        vm.AddConnector("B", "Right", "A", "Left");
        var added = vm.SelectedConnector!;
        added.Label = "new";
        vm.ClearSelection();
        vm.SelectedConnector = added;
        Assert.True(vm.MoveSelectedMessage(-1));
        AssertInOrder(vm.GenerateMermaidCode(), "B->>A: second", "end", "B->>A: new", "A->>B: third");
    }

    // ---------------- autonumber ----------------

    [Fact]
    public void Autonumber_numbers_messages_in_order()
    {
        var vm = Load("sequenceDiagram\n    autonumber\n" + Flow.Substring("sequenceDiagram\n".Length));
        Assert.Equal(new[] { "1", "2", "3" }, vm.Connectors.Select(c => c.SequenceNumber).ToArray());
        Assert.Contains("autonumber", vm.GenerateMermaidCode());
    }

    [Fact]
    public void Autonumber_start_step_and_off_are_honoured()
    {
        var vm = Load("sequenceDiagram\n    autonumber 10 5\n    A->>B: a\n    A->>B: b\n    autonumber off\n    A->>B: c\n    autonumber\n    A->>B: d\n");
        Assert.Equal(new string?[] { "10", "15", null, "20" }, vm.Connectors.Select(c => c.SequenceNumber).ToArray());
    }

    [Fact]
    public void No_numbers_without_autonumber_and_numbers_follow_a_move()
    {
        var plain = Load(Flow);
        Assert.All(plain.Connectors, c => Assert.False(c.HasSequenceNumber));

        var vm = Load("sequenceDiagram\n    autonumber\n    A->>B: x\n    A->>B: y\n");
        Select(vm, "y");
        vm.MoveSelectedMessage(-1);
        Assert.Equal("1", vm.Connectors.Single(c => c.Label == "y").SequenceNumber);
        Assert.Equal("2", vm.Connectors.Single(c => c.Label == "x").SequenceNumber);
    }
}
