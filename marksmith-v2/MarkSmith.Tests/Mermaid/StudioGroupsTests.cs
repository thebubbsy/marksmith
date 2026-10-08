using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Core.Mermaid.Routing;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Parser;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>
/// Diagram Studio drew a composite state as one empty box and a flowchart subgraph not at all,
/// and never drew state or class notes: you edited a diagram you couldn't fully see. A
/// composite's states and transitions are now nodes inside its frame, subgraphs are frames
/// round their members, and notes sit beside what they're for.
/// </summary>
public class StudioGroupsTests
{
    private const string State =
        "stateDiagram-v2\n    [*] --> Idle\n    Idle --> Running : start\n    state Running {\n        [*] --> Loading\n        Loading --> Ready\n        Ready --> [*]\n    }\n" +
        "    Running --> Done\n    Done --> [*]\n    note right of Idle : waiting\n    note left of Loading\n        fetching\n    end note\n";

    private static MermaidStudioViewModel Load(string code)
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(code);
        return vm;
    }

    private static DiagramNodeViewModel Node(MermaidStudioViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id);

    private static bool Inside(SequenceGroupBox f, DiagramNodeViewModel n) =>
        n.X >= f.X && n.Y >= f.Y && n.X + n.Width <= f.X + f.Width && n.Y + n.Height <= f.Y + f.Height;

    private static bool Overlaps(SequenceGroupBox f, DiagramNodeViewModel n) =>
        n.X < f.X + f.Width && n.X + n.Width > f.X && n.Y < f.Y + f.Height && n.Y + n.Height > f.Y;

    [Fact]
    public void A_composites_states_are_on_the_canvas_inside_it()
    {
        var vm = Load(State);
        Assert.Equal("Running", Node(vm, "Loading").ParentId);
        Assert.Equal("Running", Node(vm, "Ready").ParentId);
        // Its own start and end points, apart from the top level's.
        Assert.Contains(vm.Nodes, n => n.Id == "Running/[*]" && n.Shape == "Start" && n.ParentId == "Running");
        Assert.Contains(vm.Nodes, n => n.Id == "Running/[*]end" && n.Shape == "End");
        Assert.Contains(vm.Connectors, c => c.SourceNodeId == "Loading" && c.TargetNodeId == "Ready");
        Assert.Contains(vm.Connectors, c => c.SourceNodeId == "Running/[*]" && c.TargetNodeId == "Loading");
    }

    [Fact]
    public void The_composite_is_a_frame_round_its_box_and_its_states_and_nothing_else()
    {
        var vm = Load(State);
        var frame = Assert.Single(vm.SequenceBoxes);
        foreach (var id in new[] { "Running", "Loading", "Ready", "Running/[*]", "Running/[*]end" })
            Assert.True(Inside(frame, Node(vm, id)), id);
        foreach (var id in new[] { "Idle", "Done", "[*]", "[*]end" })
            Assert.False(Overlaps(frame, Node(vm, id)), id);
        // The composite's own box heads the frame.
        var header = Node(vm, "Running");
        Assert.True(vm.Nodes.Where(n => n.ParentId == "Running").All(n => n.Y > header.Y + header.Height), "states sit below the composite's box");
    }

    [Fact]
    public void Saving_puts_the_states_back_inside_the_composite()
    {
        var vm = Load(State);
        var code = vm.GenerateMermaidCode();
        var ast = Assert.IsType<StateDiagramAst>(MermaidParser.Parse(code).Ast);
        var running = ast.States["Running"];
        Assert.Equal(StateNodeType.Composite, running.Type);
        Assert.Contains(running.SubStates, s => s.Id == "Loading");
        Assert.Contains(running.SubStates, s => s.Id == "Ready");
        Assert.Contains(running.SubTransitions, t => t.FromId == "[*]" && t.ToId == "Loading");
        Assert.Contains(running.SubTransitions, t => t.FromId == "Ready" && t.ToId == "[*]");
        Assert.DoesNotContain(ast.States.Keys, k => k == "Loading" || k.Contains('/'));
        Assert.Contains(ast.Transitions, t => t.FromId == "Running" && t.ToId == "Done");
        Assert.Contains(ast.Transitions, t => t.FromId == "Done" && t.ToId == "[*]");
        // And it loads again the same way.
        var again = Load(code);
        Assert.Equal(vm.Nodes.Select(n => (n.Id, n.ParentId)).OrderBy(x => x.Id), again.Nodes.Select(n => (n.Id, n.ParentId)).OrderBy(x => x.Id));
    }

    [Fact]
    public void A_state_added_to_a_composite_on_the_canvas_is_saved_inside_it()
    {
        var vm = Load(State);
        vm.Nodes.Add(new DiagramNodeViewModel { Id = "Retry", LabelText = "Retry", Shape = "Normal", Category = "State", ParentId = "Running" });
        vm.Connectors.Add(new DiagramConnectorViewModel { SourceNodeId = "Ready", TargetNodeId = "Retry" });
        var ast = Assert.IsType<StateDiagramAst>(MermaidParser.Parse(vm.GenerateMermaidCode()).Ast);
        Assert.Contains(ast.States["Running"].SubStates, s => s.Id == "Retry");
        Assert.Contains(ast.States["Running"].SubTransitions, t => t.FromId == "Ready" && t.ToId == "Retry");
    }

    [Fact]
    public void Dragging_the_composite_carries_its_states()
    {
        var vm = Load(State);
        var header = Node(vm, "Running");
        var loading = Node(vm, "Loading");
        var (lx, ly) = (loading.X, loading.Y);
        header.X += 120;
        header.Y += 40;
        vm.UpdateConnectedConnectors(header);
        Assert.Equal(lx + 120, loading.X, 3);
        Assert.Equal(ly + 40, loading.Y, 3);
        Assert.True(Inside(Assert.Single(vm.SequenceBoxes), loading));
        // A state moved on its own stays put and the frame follows it.
        loading.X += 300;
        vm.UpdateConnectedConnectors(loading);
        Assert.True(Inside(Assert.Single(vm.SequenceBoxes), loading));
        Assert.Equal(header.X - 0, Node(vm, "Running").X, 3);
    }

    [Fact]
    public void Nudging_a_selected_composite_carries_its_states_too()
    {
        var vm = Load(State);
        var loading = Node(vm, "Loading");
        double lx = loading.X;
        vm.SelectNode(Node(vm, "Running"));
        vm.MoveSelectedNodes(50, 0);
        Assert.Equal(lx + 50, loading.X, 3);
    }

    [Fact]
    public void Deleting_a_composite_keeps_its_states_at_the_level_above()
    {
        var vm = Load(State);
        vm.Nodes.Remove(Node(vm, "Running"));
        var ast = Assert.IsType<StateDiagramAst>(MermaidParser.Parse(vm.GenerateMermaidCode()).Ast);
        Assert.Contains("Loading", ast.States.Keys);
        Assert.Empty(vm.CanvasGroups());
    }

    [Fact]
    public void Nested_composites_nest_their_frames()
    {
        var code = "stateDiagram-v2\n    [*] --> Outer\n    state Outer {\n        [*] --> Inner\n        state Inner {\n            [*] --> Deep\n        }\n    }\n";
        var vm = Load(code);
        Assert.Equal("Inner", Node(vm, "Deep").ParentId);
        Assert.Equal("Outer", Node(vm, "Inner").ParentId);
        Assert.Equal(2, vm.SequenceBoxes.Count);
        var outer = vm.SequenceBoxes.OrderBy(b => b.Width * b.Height).Last();
        var inner = vm.SequenceBoxes.OrderBy(b => b.Width * b.Height).First();
        Assert.True(inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height);
        Assert.True(Inside(inner, Node(vm, "Deep")));
        var ast = Assert.IsType<StateDiagramAst>(MermaidParser.Parse(vm.GenerateMermaidCode()).Ast);
        Assert.Contains(ast.States["Outer"].SubStates.Single(s => s.Id == "Inner").SubStates, s => s.Id == "Deep");
    }

    [Fact]
    public void State_notes_are_drawn_beside_their_state()
    {
        var vm = Load(State);
        Assert.Equal(2, vm.SequenceNotes.Count);
        var idle = Node(vm, "Idle");
        var waiting = vm.SequenceNotes.Single(n => n.Text == "waiting");
        Assert.True(waiting.X >= idle.X + idle.Width, "right of Idle");
        var loading = Node(vm, "Loading");
        var fetching = vm.SequenceNotes.Single(n => n.Text == "fetching");
        Assert.True(fetching.X + fetching.Width <= loading.X, "left of Loading");
        // Deleting the state drops its note from the canvas, as the save drops it.
        vm.SelectNode(idle);
        vm.DeleteSelected();
        vm.UpdateAllConnectors();
        Assert.DoesNotContain(vm.SequenceNotes, n => n.Text == "waiting");
    }

    [Fact]
    public void Class_notes_are_drawn_too()
    {
        var vm = Load("classDiagram\n    Animal <|-- Dog\n    note for Dog \"good boy\"\n    note \"all animals\"\n");
        var dog = Node(vm, "Dog");
        var forDog = vm.SequenceNotes.Single(n => n.Text == "good boy");
        Assert.True(forDog.X >= dog.X + dog.Width);
        var free = vm.SequenceNotes.Single(n => n.Text == "all animals");
        Assert.True(free.Y + free.Height <= vm.Nodes.Min(n => n.Y), "a free note sits above the diagram");
        var bounds = vm.GetContentBounds()!.Value;
        Assert.True(bounds.Y <= free.Y, "Fit includes the notes");
    }

    [Fact]
    public void Flowchart_subgraphs_are_titled_frames_round_their_members()
    {
        var code = "flowchart TD\n    A --> B\n    subgraph Backend [Back end]\n        B --> C\n        subgraph Db\n            D\n        end\n        C --> D\n    end\n    D --> E\n";
        var vm = Load(code);
        Assert.Equal("Backend", Node(vm, "B").ParentId);
        Assert.Equal("Db", Node(vm, "D").ParentId);
        Assert.Equal(2, vm.SequenceBoxes.Count);
        var backend = vm.SequenceBoxes.Single(b => b.Label == "Back end");
        var db = vm.SequenceBoxes.Single(b => b.Label == "Db");
        foreach (var id in new[] { "B", "C", "D" }) Assert.True(Inside(backend, Node(vm, id)), id);
        Assert.True(Inside(db, Node(vm, "D")));
        foreach (var id in new[] { "A", "E" }) Assert.False(Overlaps(backend, Node(vm, id)), id);
        // The save still writes the subgraphs (run #39).
        var saved = vm.GenerateMermaidCode();
        Assert.Contains("subgraph Backend", saved);
        Assert.Contains("subgraph Db", saved);
    }

    [Fact]
    public void Diagrams_without_groups_lay_out_as_before()
    {
        var vm = Load("flowchart TD\n    A --> B\n    B --> C\n");
        Assert.Empty(vm.SequenceBoxes);
        Assert.Empty(vm.SequenceNotes);
    }

    [Fact]
    public void Grouped_layout_keeps_blocks_apart()
    {
        var nodes = new List<GroupedLayoutNode>
        {
            new("a", 100, 40, null), new("b", 100, 40, "g"), new("c", 100, 40, "g"), new("d", 100, 40, null),
        };
        var groups = new List<GroupedLayoutGroup> { new("g", null, null, "G") };
        var edges = new List<(string, string)> { ("a", "b"), ("b", "c"), ("c", "d") };
        var r = GroupedLayout.Compute(nodes, groups, edges, new GroupedLayout.Options(true, false, 100, 60, 100, 500));
        var frame = Assert.Single(r.Frames);
        // Vertical: a above the group, d below it, b above c inside it.
        Assert.True(r.Positions["a"].Y + 40 <= frame.Y);
        Assert.True(r.Positions["d"].Y >= frame.Y + frame.Height);
        Assert.True(r.Positions["b"].Y < r.Positions["c"].Y);
        foreach (var id in new[] { "b", "c" })
        {
            var (x, y) = r.Positions[id];
            Assert.True(x >= frame.X && y >= frame.Y + GroupedLayout.TitleBand - 0.01 && x + 100 <= frame.X + frame.Width && y + 40 <= frame.Y + frame.Height, id);
        }
    }

    [Fact]
    public void Note_text_is_read_as_written()
    {
        Assert.Equal(("X", false, "hi"), MermaidStudioViewModel.ParseStateNote("note right of X : hi"));
        Assert.Equal(("X", true, "a\nb"), MermaidStudioViewModel.ParseStateNote("note left of X\n    a\n    b\nend note"));
        Assert.Null(MermaidStudioViewModel.ParseStateNote("nothing"));
        Assert.Equal(("Dog", false, "good"), MermaidStudioViewModel.ParseClassNote("note for Dog \"good\""));
        Assert.Equal(((string?)null, false, "free"), MermaidStudioViewModel.ParseClassNote("note \"free\""));
    }
    [Fact]
    public void Syncing_from_code_after_moving_a_composite_doesnt_shift_its_states()
    {
        var vm = Load(State);
        var header = Node(vm, "Running");
        header.X += 200;
        vm.UpdateConnectedConnectors(header);
        var at = vm.Nodes.ToDictionary(n => n.Id, n => (n.X, n.Y));
        vm.SyncCanvasFromCode(vm.GenerateMermaidCode());
        foreach (var n in vm.Nodes)
        {
            Assert.Equal(at[n.Id].X, n.X, 3);
            Assert.Equal(at[n.Id].Y, n.Y, 3);
        }
    }

    [Fact]
    public void Deleting_every_state_inside_a_composite_doesnt_bring_them_back()
    {
        var vm = Load(State);
        foreach (var id in new[] { "Loading", "Ready", "Running/[*]", "Running/[*]end" })
        {
            vm.SelectNode(Node(vm, id));
            vm.DeleteSelected();
        }
        var code = vm.GenerateMermaidCode();
        Assert.DoesNotContain("Loading", code);
        Assert.DoesNotContain("Ready", code);
    }

    [Fact]
    public void An_edge_into_a_composite_from_outside_round_trips_without_a_second_state()
    {
        var vm = Load(State);
        vm.Connectors.Add(new DiagramConnectorViewModel { SourceNodeId = "Idle", TargetNodeId = "Ready" });
        var code = vm.GenerateMermaidCode();
        var again = Load(code);
        Assert.Single(again.Nodes, n => n.Id == "Ready");
        Assert.Equal("Running", Node(again, "Ready").ParentId);
        Assert.Contains(again.Connectors, c => c.SourceNodeId == "Idle" && c.TargetNodeId == "Ready");
    }
}
