using System.Linq;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>Selection, arrange, undo and command-enablement rules of the Shape Studio canvas.</summary>
public class ShapeStudioSelectionTests
{
    private static ShapeDesignStudioViewModel ThreeRects()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.AddShapeAt("rect", 10, 50, 100, 50);
        vm.AddShapeAt("rect", 200, 150, 100, 50);
        vm.AddShapeAt("rect", 80, 250, 100, 50);
        return vm;
    }

    [Fact]
    public void Align_only_moves_the_selection_not_the_whole_canvas()
    {
        var vm = ThreeRects();
        vm.SelectedShape = vm.Shapes[0];
        vm.ToggleSelection(vm.Shapes[1]);

        Assert.Equal(2, vm.SelectionCount);
        Assert.True(vm.AlignLeftCommand.CanExecute(null));
        vm.AlignLeftCommand.Execute(null);

        Assert.Equal(10, vm.Shapes[0].X);
        Assert.Equal(10, vm.Shapes[1].X);
        Assert.Equal(80, vm.Shapes[2].X); // not selected — untouched
    }

    [Fact]
    public void Align_and_distribute_are_disabled_until_enough_shapes_are_selected()
    {
        var vm = ThreeRects();
        vm.ClearSelection();
        Assert.False(vm.AlignLeftCommand.CanExecute(null));
        Assert.False(vm.DistributeVerticalCommand.CanExecute(null));

        vm.SelectedShape = vm.Shapes[0];
        Assert.False(vm.AlignLeftCommand.CanExecute(null));

        vm.ToggleSelection(vm.Shapes[1]);
        Assert.True(vm.AlignLeftCommand.CanExecute(null));
        Assert.False(vm.DistributeVerticalCommand.CanExecute(null));

        vm.ToggleSelection(vm.Shapes[2]);
        Assert.True(vm.DistributeVerticalCommand.CanExecute(null));
    }

    [Fact]
    public void Ctrl_click_toggles_and_keeps_a_primary()
    {
        var vm = ThreeRects();
        vm.SelectedShape = vm.Shapes[0];
        vm.ToggleSelection(vm.Shapes[2]);
        Assert.Same(vm.Shapes[2], vm.SelectedShape);
        Assert.True(vm.IsMultiSelect);

        vm.ToggleSelection(vm.Shapes[2]);
        Assert.Same(vm.Shapes[0], vm.SelectedShape);
        Assert.Equal(1, vm.SelectionCount);
    }

    [Fact]
    public void Click_inside_a_multi_selection_keeps_the_group()
    {
        var vm = ThreeRects();
        vm.SelectAll();
        vm.ClickSelect(vm.Shapes[1]);
        Assert.Equal(3, vm.SelectionCount);
        Assert.Same(vm.Shapes[1], vm.SelectedShape);

        vm.ClearSelection();
        vm.ClickSelect(vm.Shapes[1]);
        Assert.Equal(1, vm.SelectionCount);
    }

    [Fact]
    public void Plain_selection_replaces_a_multi_selection()
    {
        var vm = ThreeRects();
        vm.SelectAll();
        vm.SelectedShape = vm.Shapes[0];
        Assert.Equal(1, vm.SelectionCount);
        Assert.True(vm.Shapes[0].IsSelected);
    }

    [Fact]
    public void Delete_and_duplicate_act_on_every_selected_shape()
    {
        var vm = ThreeRects();
        vm.SelectedShape = vm.Shapes[0];
        vm.ToggleSelection(vm.Shapes[1]);

        vm.DuplicateSelectedCommand.Execute(null);
        Assert.Equal(5, vm.Shapes.Count);
        Assert.Equal(2, vm.SelectionCount);
        Assert.True(vm.Shapes[3].IsSelected && vm.Shapes[4].IsSelected); // the copies are selected

        vm.RemoveSelectedCommand.Execute(null);
        Assert.Equal(3, vm.Shapes.Count);
        Assert.Equal(0, vm.SelectionCount);
    }

    [Fact]
    public void Clear_can_be_undone_and_redone()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.GeneratePyramidTemplateCommand.Execute(null);
        var labels = vm.Shapes.Select(s => s.Text).ToList();

        vm.ClearAllCommand.Execute(null);
        Assert.Empty(vm.Shapes);
        Assert.True(vm.UndoCommand.CanExecute(null));

        vm.UndoCommand.Execute(null);
        Assert.Equal(labels, vm.Shapes.Select(s => s.Text).ToList());
        Assert.True(vm.IsEditable);

        vm.RedoCommand.Execute(null);
        Assert.Empty(vm.Shapes);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void Undo_restores_positions_after_align()
    {
        var vm = ThreeRects();
        vm.SelectAll();
        vm.AlignRightCommand.Execute(null);
        Assert.All(vm.Shapes, s => Assert.Equal(200, s.X));

        vm.UndoCommand.Execute(null);
        Assert.Equal(new double[] { 10, 200, 80 }, vm.Shapes.Select(s => s.X).ToArray());
    }

    [Fact]
    public void A_preset_can_be_undone_back_to_the_previous_diagram()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.GeneratePyramidTemplateCommand.Execute(null);
        var pyramid = vm.Shapes.Select(s => s.Prst).ToList();

        vm.ApplyPreset(vm.AllPresets.First(p => p.Name == "2x2 SWOT Matrix"));
        Assert.NotEqual(pyramid, vm.Shapes.Select(s => s.Prst).ToList());
        Assert.Null(vm.SelectedShape); // a fresh diagram opens with nothing selected

        vm.UndoCommand.Execute(null);
        Assert.Equal(pyramid, vm.Shapes.Select(s => s.Prst).ToList());
    }

    [Fact]
    public void Undo_skips_entries_that_match_the_canvas()
    {
        var vm = ThreeRects();
        vm.RecordUndo();           // e.g. inspector focused …
        vm.Shapes[0].X = 300;      // … and an edit made
        vm.RecordUndo();           // focused again, nothing changed after

        vm.UndoCommand.Execute(null);
        Assert.Equal(10, vm.Shapes[0].X);
    }

    [Fact]
    public void Document_commands_need_shapes()
    {
        var vm = new ShapeDesignStudioViewModel();
        Assert.False(vm.InsertIntoDocumentCommand.CanExecute(null));
        Assert.False(vm.ExportDocxCommand.CanExecute(null));
        Assert.False(vm.ApplyPaletteThemeCommand.CanExecute(null));
        Assert.False(vm.HasShapes);

        vm.AddShapeAt("ellipse", 0, 0);
        Assert.True(vm.HasShapes);
        Assert.True(vm.InsertIntoDocumentCommand.CanExecute(null));
        Assert.True(vm.ExportDocxCommand.CanExecute(null));
        Assert.True(vm.ApplyPaletteThemeCommand.CanExecute(null));
    }

    [Fact]
    public void Armed_tool_drives_the_placement_hint()
    {
        var vm = new ShapeDesignStudioViewModel();
        Assert.False(vm.IsPlacing);
        Assert.Equal("", vm.PlacementHint);

        vm.ArmedTool = "hexagon";
        Assert.True(vm.IsPlacing);
        Assert.Contains("hexagon", vm.PlacementHint);
    }

    [Fact]
    public void Nudge_moves_the_selection_and_stops_at_the_origin()
    {
        var vm = ThreeRects();
        vm.SelectedShape = vm.Shapes[0];
        vm.ToggleSelection(vm.Shapes[1]);
        var moved = vm.NudgeSelection(-50, 10);
        // The group stops as one when its leftmost shape reaches the edge (run #21); each shape used
        // to stop on its own, which squashed the group's layout.
        Assert.Equal((-10.0, 10.0), moved);
        Assert.Equal(0, vm.Shapes[0].X);
        Assert.Equal(190, vm.Shapes[1].X);
        Assert.Equal(60, vm.Shapes[0].Y);
        Assert.Equal(80, vm.Shapes[2].X);
    }

    [Theory]
    [InlineData("", "Rounded rectangle", "")]
    [InlineData("Executive Board", "Executive Board", "Rounded rectangle")]
    [InlineData("PROFESSIONAL\n$19 / month", "PROFESSIONAL", "Rounded rectangle")]
    public void Shapes_list_leads_with_the_label(string text, string title, string subtitle)
    {
        var s = new ShapeCanvasItemViewModel { Prst = "roundrect", Text = text };
        Assert.Equal(title, s.ListTitle);
        Assert.Equal(subtitle, s.ListSubtitle);
    }
}
