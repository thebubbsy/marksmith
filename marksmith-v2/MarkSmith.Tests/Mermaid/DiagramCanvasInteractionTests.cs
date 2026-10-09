using System;
using System.Linq;
using MarkSmith.Core.Mermaid.Routing;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>
/// Diagram Studio canvas, by hand (routine run #48): the pieces of the pointer work that live in
/// Core. Zoom buttons step through editor stops, a connector lands on the node it highlighted
/// (topmost, with a little slack for the anchor dots), Esc mid-drag leaves no undo step behind,
/// drops land centred under the pointer, and status messages name shapes by label.
/// </summary>
public class DiagramCanvasInteractionTests
{
    [Theory]
    [InlineData(1.0, 1.1)]
    [InlineData(0.2, 0.25)]
    [InlineData(0.42, 0.5)]   // between stops (after Fit): the next stop up
    [InlineData(1.248, 1.5)]  // within tolerance of a stop counts as on it
    [InlineData(3.0, 4.0)]
    [InlineData(4.0, 4.0)]    // capped
    public void Zoom_in_goes_to_the_next_stop(double current, double expected) =>
        Assert.Equal(expected, ZoomSteps.Next(current, 4.0), 3);

    [Theory]
    [InlineData(1.0, 0.9)]
    [InlineData(0.42, 0.33)]
    [InlineData(0.25, 0.2)]
    [InlineData(0.2, 0.2)]    // floored
    [InlineData(4.0, 3.0)]
    public void Zoom_out_goes_to_the_previous_stop(double current, double expected) =>
        Assert.Equal(expected, ZoomSteps.Previous(current, 0.2), 3);

    [Fact]
    public void Zoom_in_then_out_returns_to_100_percent()
    {
        double z = 1.0;
        z = ZoomSteps.Next(z, 4.0);
        z = ZoomSteps.Previous(z, 0.2);
        Assert.Equal(1.0, z, 3);
    }

    private static MermaidStudioViewModel Empty()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        vm.Connectors.Clear();
        return vm;
    }

    private static DiagramNodeViewModel Node(MermaidStudioViewModel vm, string id, double x, double y, double w = 140, double h = 60, int z = 10)
    {
        var n = new DiagramNodeViewModel { Id = id, LabelText = id, X = x, Y = y, Width = w, Height = h, ZIndex = z };
        vm.Nodes.Add(n);
        return n;
    }

    [Fact]
    public void NodeAt_finds_the_node_under_a_point()
    {
        var vm = Empty();
        var a = Node(vm, "A", 100, 100);
        Node(vm, "B", 400, 100);
        Assert.Same(a, vm.NodeAt(150, 130));
        Assert.Null(vm.NodeAt(300, 130));
    }

    [Fact]
    public void NodeAt_tolerance_covers_the_anchor_dot_outside_the_box()
    {
        var vm = Empty();
        var a = Node(vm, "A", 100, 100);
        // The top anchor dot's outer half: 10 px above the box.
        Assert.Null(vm.NodeAt(170, 90));
        Assert.Same(a, vm.NodeAt(170, 90, tolerance: 14));
    }

    [Fact]
    public void NodeAt_prefers_the_topmost_node_where_two_overlap()
    {
        var vm = Empty();
        var under = Node(vm, "Under", 100, 100);
        var over = Node(vm, "Over", 150, 120);
        Assert.Same(over, vm.NodeAt(200, 140));      // same ZIndex: the later one is drawn on top
        under.ZIndex = 20;
        Assert.Same(under, vm.NodeAt(200, 140));     // brought to front
    }

    [Fact]
    public void NodeAt_skips_the_excluded_node()
    {
        var vm = Empty();
        var a = Node(vm, "A", 100, 100);
        Assert.Null(vm.NodeAt(150, 130, except: a));
    }

    [Fact]
    public void DiscardLastSnapshot_leaves_no_undo_step_behind()
    {
        var vm = Empty();
        Node(vm, "A", 100, 100);
        Assert.False(vm.CanUndo);
        vm.SnapshotForUndo();          // a drag starts...
        Assert.True(vm.CanUndo);
        vm.DiscardLastSnapshot();      // ...and Esc cancels it
        Assert.False(vm.CanUndo);
        vm.DiscardLastSnapshot();      // nothing left: harmless
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public void A_drop_lands_centred_under_the_pointer()
    {
        var vm = Empty();
        vm.IsGridSnapEnabled = false;
        var item = new MermaidPaletteItem { Category = "Flowchart", ShapeType = "Rectangle", DefaultText = "New Node" };
        var n = vm.AddNodeFromPalette(item, 500, 300, centreOnPoint: true);
        Assert.Equal(500, n.X + n.Width / 2, 3);
        Assert.Equal(300, n.Y + n.Height / 2, 3);

        var corner = vm.AddNodeFromPalette(item, 500, 300);
        Assert.Equal(500, corner.X, 3);
        Assert.Equal(300, corner.Y, 3);
    }

    [Fact]
    public void A_centred_start_dot_uses_its_own_size()
    {
        var vm = Empty();
        vm.IsGridSnapEnabled = false;
        var item = new MermaidPaletteItem { Category = "State", ShapeType = "Start", DefaultText = "" };
        var n = vm.AddNodeFromPalette(item, 300, 300, centreOnPoint: true);
        Assert.Equal(28, n.Width);
        Assert.Equal(300, n.X + 14, 3);
        Assert.False(n.CanQuickAdd);   // too small for four arrows around it
    }

    [Fact]
    public void Quick_add_arrows_follow_hover_and_selection()
    {
        var n = new DiagramNodeViewModel { Id = "A", Shape = "Rectangle" };
        Assert.True(n.CanQuickAdd);
        Assert.Equal(0, n.QuickAddOpacity);
        n.IsHovered = true;
        Assert.Equal(1, n.QuickAddOpacity);
        n.IsHovered = false;
        n.IsSelected = true;
        Assert.Equal(1, n.QuickAddOpacity);
    }

    [Fact]
    public void Status_messages_name_shapes_by_label_not_id()
    {
        var vm = Empty();
        var a = Node(vm, "node_1", 100, 100);
        a.LabelText = "Start Process";
        var b = Node(vm, "node_2", 400, 100);
        b.LabelText = "Check\nConditions";
        vm.AddConnector("node_1", "Right", "node_2", "Left");
        Assert.Contains("Start Process", vm.StatusText);
        Assert.Contains("'Check'", vm.StatusText);   // first line of a two-line label
        Assert.DoesNotContain("node_", vm.StatusText);

        var added = vm.QuickAddNode(a, "Bottom");
        Assert.Contains("Start Process", vm.StatusText);
        Assert.DoesNotContain(added.Id, vm.StatusText);
    }

    [Fact]
    public void A_node_with_no_label_falls_back_to_its_id()
    {
        var n = new DiagramNodeViewModel { Id = "n7", LabelText = "  " };
        Assert.Equal("n7", MermaidStudioViewModel.DisplayName(n));
    }

    [Fact]
    public void Paste_is_only_offered_once_something_was_copied()
    {
        var vm = Empty();
        var a = Node(vm, "A", 100, 100);
        Assert.False(vm.CanPaste);
        vm.SelectNode(a, false);
        vm.CopySelected();
        Assert.True(vm.CanPaste);
        Assert.Equal("Copied 1 shape.", vm.StatusText);
    }
    [Fact]
    public void Quick_add_branches_beside_a_shape_in_the_way_instead_of_jumping_past_it()
    {
        var vm = Empty();
        vm.IsGridSnapEnabled = false;
        var a = Node(vm, "A", 300, 100, 150, 60);
        var b = Node(vm, "B", 300, 220, 150, 60);   // right where "add below A" would go
        var added = vm.QuickAddNode(a, "Bottom");
        Assert.Equal(b.Y, added.Y, 3);               // same row as the blocker, not beyond it
        Assert.NotEqual(b.X, added.X);
        Assert.True(added.X + added.Width < b.X || added.X > b.X + b.Width);
    }

    [Fact]
    public void Quick_add_takes_the_free_slot_straight_out_first()
    {
        var vm = Empty();
        var a = Node(vm, "A", 300, 300, 150, 60);
        Assert.Equal(300 + 150 + 80, vm.QuickAddNode(a, "Right").X, 3);
        Assert.Equal(300 - 150 - 80, vm.QuickAddNode(a, "Left").X, 3);
        Assert.Equal(300 - 60 - 60, vm.QuickAddNode(a, "Top").Y, 3);
    }

    [Fact]
    public void Quick_add_never_places_a_shape_off_the_canvas()
    {
        var vm = Empty();
        var a = Node(vm, "A", 20, 20, 150, 60);
        var up = vm.QuickAddNode(a, "Top");
        Assert.True(up.X >= 20 && up.Y >= 20);
    }
    [Fact]
    public void The_code_tab_hides_position_lines_and_editing_it_keeps_positions()
    {
        var vm = Empty();
        Node(vm, "A", 120, 80);
        Node(vm, "C", 420, 260);
        vm.AddConnector("A", "Right", "C", "Left");
        string full = vm.GenerateMermaidCode();
        Assert.Contains("%% {", full);

        string shown = MarkSmith.Mermaid.Sync.MermaidSpatialMetadataService.StripFromCode(full);
        Assert.DoesNotContain("\"id\"", shown);
        Assert.Contains("flowchart", shown);

        var before = vm.Nodes.ToDictionary(n => n.Id, n => (n.X, n.Y));
        Assert.True(vm.SyncCanvasFromCode(shown + Environment.NewLine + "    C --> D[\"Another\"]"));
        foreach (var (id, pos) in before)
        {
            var n = vm.Nodes.Single(x => x.Id == id);
            Assert.Equal(pos.X, n.X, 3);
            Assert.Equal(pos.Y, n.Y, 3);
        }
        Assert.Contains(vm.Nodes, n => n.Id == "D");
        Assert.Contains("%% {", vm.GenerateMermaidCode());   // Sync to Markdown still carries them
    }
    [Fact]
    public void A_properties_panel_rename_is_one_undo_step()
    {
        var vm = Empty();
        var a = Node(vm, "A", 100, 100);
        a.LabelText = "Before";
        vm.SelectNode(a, false);
        Assert.False(vm.CanUndo);

        foreach (var text in new[] { "A", "Af", "Aft", "After" })   // typed into the Label box
            vm.SelectedNode!.LabelText = text;
        Assert.True(vm.CanUndo);

        vm.Undo();
        Assert.Equal("Before", vm.Nodes.Single(n => n.Id == "A").LabelText);
        Assert.False(vm.CanUndo);                                   // one step, not four
    }

    [Fact]
    public void Each_field_edited_in_the_panel_is_its_own_step()
    {
        var vm = Empty();
        var a = Node(vm, "A", 100, 100);
        a.LabelText = "Name";
        vm.SelectNode(a, false);
        a.LabelText = "Renamed";
        a.Shape = "Hexagon";

        vm.Undo();
        var n = vm.Nodes.Single(x => x.Id == "A");
        Assert.Equal("Renamed", n.LabelText);
        Assert.NotEqual("Hexagon", n.Shape);
        vm.Undo();
        Assert.Equal("Name", vm.Nodes.Single(x => x.Id == "A").LabelText);
    }

    [Fact]
    public void A_connector_style_change_in_the_panel_can_be_undone()
    {
        var vm = Empty();
        Node(vm, "A", 100, 100);
        Node(vm, "B", 400, 100);
        vm.AddConnector("A", "Right", "B", "Left");   // one step of its own
        var c = vm.SelectedConnector!;
        c.LineStyle = "Dashed";

        vm.Undo();
        Assert.Equal("Solid", vm.Connectors.Single().LineStyle);
        Assert.True(vm.CanUndo);                      // adding the connector is still there to undo
    }

    [Fact]
    public void A_change_made_with_its_own_snapshot_is_not_counted_twice()
    {
        var vm = Empty();
        var a = Node(vm, "A", 100, 100);
        a.LabelText = "Old";
        vm.SelectNode(a, false);

        vm.SnapshotForUndo();                         // the canvas's inline rename
        using (vm.SuspendEditTracking())
            a.LabelText = "New";

        vm.Undo();
        Assert.Equal("Old", vm.Nodes.Single(n => n.Id == "A").LabelText);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public void Edits_to_a_shape_that_is_not_selected_are_not_tracked()
    {
        var vm = Empty();
        var a = Node(vm, "A", 100, 100);
        a.LabelText = "Quiet";                        // e.g. a template being built
        Assert.False(vm.CanUndo);
    }
}
