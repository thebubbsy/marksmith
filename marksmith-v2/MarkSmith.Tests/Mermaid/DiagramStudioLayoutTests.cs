using System;
using System.Linq;
using MarkSmith.Core.Mermaid.Routing;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>
/// Diagram Studio drew sequence diagrams as boxes in a row joined box to box (so A→B and B→A
/// overlapped on one line and message order was lost), drew a state diagram's start and end as
/// one shared [*] node, and let any cycle stretch the layered layout into a tangle. These pin the
/// lifeline/row layout, the start/end split (and its lossless round trip) and the cycle-safe ranks.
/// </summary>
public class DiagramStudioLayoutTests
{
    // ---------------- Sequence ----------------

    private const string Conversation =
        "sequenceDiagram\n    participant A\n    participant B\n    A->>B: request\n    B-->>A: reply\n    A->>A: think\n    A->>B: done\n";

    [Fact]
    public void Every_message_gets_its_own_row_in_order()
    {
        var vm = Load(Conversation);
        var ys = vm.Connectors.Select(c => c.SourceY).ToList();
        for (int i = 1; i < ys.Count; i++) Assert.True(ys[i] > ys[i - 1], $"row {i} at {ys[i]} not below {ys[i - 1]}");
        // A→B and B→A no longer share a line.
        Assert.NotEqual(vm.Connectors[0].SourceY, vm.Connectors[1].SourceY);
    }

    [Fact]
    public void Messages_run_lifeline_to_lifeline_and_flat()
    {
        var vm = Load(Conversation);
        var a = vm.Nodes.Single(n => n.Id == "A");
        var b = vm.Nodes.Single(n => n.Id == "B");
        var request = vm.Connectors[0];
        Assert.Equal(a.LifelineX, request.SourceX, 3);
        Assert.Equal(b.LifelineX, request.TargetX, 3);
        Assert.Equal(request.SourceY, request.TargetY, 3);
        Assert.True(request.SourceY > Math.Max(a.Y + a.Height, b.Y + b.Height), "messages start below the headers");

        var reply = vm.Connectors[1];
        Assert.Equal(b.LifelineX, reply.SourceX, 3);
        Assert.Equal(a.LifelineX, reply.TargetX, 3);
    }

    [Fact]
    public void Labels_sit_above_their_line_not_on_it()
    {
        var vm = Load(Conversation);
        var request = vm.Connectors[0];
        Assert.True(request.MidpointY < request.SourceY - 8, $"label centre {request.MidpointY} vs line {request.SourceY}");
    }

    [Fact]
    public void Self_calls_loop_out_to_the_right_and_back()
    {
        var vm = Load(Conversation);
        var think = vm.Connectors[2];
        var a = vm.Nodes.Single(n => n.Id == "A");
        Assert.Equal(a.LifelineX, think.SourceX, 3);
        Assert.Equal(a.LifelineX, think.TargetX, 3);
        Assert.True(think.TargetY > think.SourceY);
        Assert.True(think.MidpointX > a.LifelineX + SequenceLayout.SelfLoopWidth, "label clear of the loop");
    }

    [Fact]
    public void Every_participant_has_a_lifeline_past_the_last_message()
    {
        var vm = Load(Conversation);
        double last = vm.Connectors.Max(c => Math.Max(c.SourceY, c.TargetY));
        Assert.All(vm.Nodes, n =>
        {
            Assert.True(n.HasLifeline);
            Assert.True(n.LifelineBottom > last, $"{n.Id} lifeline ends at {n.LifelineBottom}, last message at {last}");
        });
        Assert.True(vm.GetContentBounds()!.Value.Bottom >= vm.Nodes.Max(n => n.LifelineBottom));
    }

    [Fact]
    public void Adding_moving_and_deleting_re_lays_the_conversation()
    {
        var vm = Load("sequenceDiagram\n    A->>B: one\n");
        double first = vm.Connectors[0].SourceY;

        vm.AddConnector("B", "Left", "A", "Right");
        var added = vm.Connectors[1];
        Assert.True(added.SourceY > first, "a new message goes on a new row below");
        Assert.False(string.IsNullOrEmpty(added.PathData));

        var b = vm.Nodes.Single(n => n.Id == "B");
        vm.SelectNode(b);
        vm.MoveSelectedNodes(80, 0);
        Assert.Equal(b.LifelineX, vm.Connectors[0].TargetX, 3);

        vm.ClearSelection();
        vm.SelectedConnector = vm.Connectors[0];
        vm.DeleteSelected();
        Assert.Single(vm.Connectors);
        Assert.Equal(first, vm.Connectors[0].SourceY, 3);
    }

    [Fact]
    public void Long_labels_widen_the_gap_between_participants()
    {
        var shortVm = Load("sequenceDiagram\n    A->>B: hi\n");
        var longVm = Load("sequenceDiagram\n    A->>B: please send me the full quarterly report as a PDF\n");
        double Gap(MermaidStudioViewModel vm) => vm.Nodes[1].LifelineX - vm.Nodes[0].LifelineX;
        Assert.True(Gap(longVm) > Gap(shortVm));
        Assert.True(Gap(longVm) >= SequenceLayout.LabelWidth("please send me the full quarterly report as a PDF"));
    }

    [Fact]
    public void Lifelines_are_only_for_sequence_diagrams()
    {
        var vm = Load(Conversation);
        vm.LoadFromMermaidCode("flowchart TD\n    A --> B\n");
        Assert.All(vm.Nodes, n => Assert.False(n.HasLifeline));
    }

    [Fact]
    public void Sequence_round_trip_keeps_message_order()
    {
        var vm = Load(Conversation);
        var code = vm.GenerateMermaidCode();
        int req = code.IndexOf("request", StringComparison.Ordinal);
        int rep = code.IndexOf("reply", StringComparison.Ordinal);
        int done = code.IndexOf("done", StringComparison.Ordinal);
        Assert.True(req > 0 && req < rep && rep < done, code);
    }

    // ---------------- State: start and end ----------------

    private const string Machine =
        "stateDiagram-v2\n    [*] --> Created\n    Created --> Paid : pay\n    Paid --> Created : refund\n    Paid --> Delivered\n    Delivered --> [*]\n    Created --> [*] : cancel\n";

    [Fact]
    public void Start_and_end_are_separate_nodes()
    {
        var vm = Load(Machine);
        var start = vm.Nodes.Single(n => n.Id == MermaidStudioViewModel.StatePseudoId);
        var end = vm.Nodes.Single(n => n.Id == MermaidStudioViewModel.StateEndNodeId);
        Assert.Equal("Start", start.Shape);
        Assert.Equal("End", end.Shape);
        Assert.All(vm.Connectors.Where(c => c.TargetNodeId.StartsWith("[*]", StringComparison.Ordinal)),
            c => Assert.Equal(MermaidStudioViewModel.StateEndNodeId, c.TargetNodeId));
        Assert.Equal(MermaidStudioViewModel.StatePseudoId, vm.Connectors.First().SourceNodeId);
    }

    [Fact]
    public void Start_heads_the_diagram_and_end_closes_it()
    {
        var vm = Load(Machine);
        var start = vm.Nodes.Single(n => n.Id == MermaidStudioViewModel.StatePseudoId);
        var end = vm.Nodes.Single(n => n.Id == MermaidStudioViewModel.StateEndNodeId);
        Assert.All(vm.Nodes.Where(n => n != start), n => Assert.True(n.X > start.X, $"{n.Id} left of start"));
        Assert.All(vm.Nodes.Where(n => n != end), n => Assert.True(n.X < end.X, $"{n.Id} right of end"));
    }

    [Fact]
    public void State_round_trip_writes_both_points_back_as_star()
    {
        var vm = Load(Machine);
        var code = vm.GenerateMermaidCode();
        Assert.Contains("[*] --> Created", code);
        Assert.Contains("Delivered --> [*]", code);
        Assert.Contains("Created --> [*] : cancel", code);
        Assert.DoesNotContain("[*]end", code.Split('\n').Where(l => !l.TrimStart().StartsWith("%%", StringComparison.Ordinal)).Aggregate("", (a, l) => a + l));

        // And the reload splits them the same way again.
        var again = Load(code);
        Assert.Single(again.Nodes, n => n.Id == MermaidStudioViewModel.StateEndNodeId);
        Assert.Equal(vm.Connectors.Count, again.Connectors.Count);
    }

    [Fact]
    public void A_diagram_that_only_ends_gets_just_an_end_point()
    {
        var vm = Load("stateDiagram-v2\n    Idle --> Done\n    Done --> [*]\n");
        Assert.DoesNotContain(vm.Nodes, n => n.Shape == "Start");
        Assert.Single(vm.Nodes, n => n.Shape == "End");
    }

    [Fact]
    public void Start_and_end_points_from_the_palette_write_star()
    {
        var vm = Load("stateDiagram-v2\n    Idle --> Busy\n");
        var startItem = vm.PaletteItems.Single(p => p.ShapeType == "Start");
        var endItem = vm.PaletteItems.Single(p => p.ShapeType == "End");
        var s = vm.AddNodeFromPalette(startItem, 10, 10);
        var e = vm.AddNodeFromPalette(endItem, 400, 10);
        Assert.Equal(28, s.Width);
        vm.AddConnector(s.Id, "Right", "Idle", "Left");
        vm.AddConnector("Busy", "Right", e.Id, "Left");
        var code = vm.GenerateMermaidCode();
        Assert.Contains("[*] --> Idle", code);
        Assert.Contains("Busy --> [*]", code);
        Assert.DoesNotContain(s.Id + " -->", code);
    }

    // ---------------- Layered layout ----------------

    [Fact]
    public void Cycles_do_not_stretch_the_ranks()
    {
        var r = LayeredLayout.Compute(
            new[] { "S", "A", "B", "C", "E" },
            new[] { ("S", "A"), ("A", "B"), ("B", "C"), ("C", "A"), ("B", "A"), ("C", "E") });
        Assert.Equal(0, r.Ranks["S"]);
        Assert.Equal(1, r.Ranks["A"]);
        Assert.Equal(2, r.Ranks["B"]);
        Assert.Equal(3, r.Ranks["C"]);
        Assert.Equal(4, r.Ranks["E"]);
        Assert.Contains(("C", "A"), r.BackEdges);
        Assert.Contains(("B", "A"), r.BackEdges);
    }

    [Fact]
    public void A_pure_cycle_still_lays_out()
    {
        var r = LayeredLayout.Compute(new[] { "A", "B", "C" }, new[] { ("A", "B"), ("B", "C"), ("C", "A") });
        Assert.Equal(new[] { 0, 1, 2 }, new[] { r.Ranks["A"], r.Ranks["B"], r.Ranks["C"] });
    }

    [Fact]
    public void Self_loops_and_unknown_ends_are_ignored()
    {
        var r = LayeredLayout.Compute(new[] { "A", "B" }, new[] { ("A", "A"), ("A", "B"), ("A", "Ghost") });
        Assert.Equal(0, r.Ranks["A"]);
        Assert.Equal(1, r.Ranks["B"]);
    }

    [Fact]
    public void Ordering_untangles_crossed_edges()
    {
        // Declared so that the naive order crosses: top row A,B; bottom row declared Y then X but
        // A→X and B→Y.
        var r = LayeredLayout.Compute(
            new[] { "R", "A", "B", "Y", "X" },
            new[] { ("R", "A"), ("R", "B"), ("B", "Y"), ("A", "X") });
        var top = r.Layers[1].ToList();
        var bottom = r.Layers[2].ToList();
        Assert.Equal(top.IndexOf("A") < top.IndexOf("B"), bottom.IndexOf("X") < bottom.IndexOf("Y"));
    }

    [Fact]
    public void Layout_is_deterministic()
    {
        var a = Load(Machine);
        var b = Load(Machine);
        Assert.Equal(a.Nodes.Select(n => (n.Id, n.X, n.Y)), b.Nodes.Select(n => (n.Id, n.X, n.Y)));
    }

    private static MermaidStudioViewModel Load(string code)
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(code);
        return vm;
    }
}
