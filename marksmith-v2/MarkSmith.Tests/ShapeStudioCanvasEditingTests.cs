using System.Linq;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;
using Edges = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.ResizeEdges;

namespace MarkSmith.Tests;

/// <summary>
/// Run #21: Shape Studio's canvas editing — resize handles, rubber-band selection, and a group
/// drag that stops as one at the canvas edge.
/// </summary>
public class ShapeStudioCanvasEditingTests
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
    public void Dragging_the_bottom_right_corner_grows_from_the_top_left()
    {
        var r = ShapeDesignStudioViewModel.ResizeRect(100, 100, 200, 100, Edges.Right | Edges.Bottom, 40, 20, keepAspect: false);
        Assert.Equal((100.0, 100.0, 240.0, 120.0), r);
    }

    [Fact]
    public void Dragging_the_left_edge_keeps_the_right_edge_still()
    {
        var r = ShapeDesignStudioViewModel.ResizeRect(100, 100, 200, 100, Edges.Left, -30, 999, keepAspect: false);
        Assert.Equal((70.0, 100.0, 230.0, 100.0), r);   // the vertical travel is ignored on a side handle
    }

    [Fact]
    public void A_shape_never_inverts_or_shrinks_below_the_minimum()
    {
        var r = ShapeDesignStudioViewModel.ResizeRect(100, 100, 200, 100, Edges.Left | Edges.Top, 500, 500, keepAspect: false);
        Assert.Equal(ShapeDesignStudioViewModel.MinShapeSize, r.W);
        Assert.Equal(ShapeDesignStudioViewModel.MinShapeSize, r.H);
        Assert.Equal(300 - ShapeDesignStudioViewModel.MinShapeSize, r.X);   // right edge unchanged
        Assert.Equal(200 - ShapeDesignStudioViewModel.MinShapeSize, r.Y);
    }

    [Fact]
    public void A_shape_cannot_be_dragged_past_the_canvas_origin()
    {
        var r = ShapeDesignStudioViewModel.ResizeRect(20, 30, 100, 50, Edges.Left | Edges.Top, -200, -200, keepAspect: false);
        Assert.Equal((0.0, 0.0, 120.0, 80.0), r);
    }

    [Fact]
    public void Shift_on_a_corner_keeps_the_proportions()
    {
        var r = ShapeDesignStudioViewModel.ResizeRect(0, 0, 200, 100, Edges.Right | Edges.Bottom, 100, 0, keepAspect: true);
        Assert.Equal(300, r.W);
        Assert.Equal(150, r.H);

        var fromTopLeft = ShapeDesignStudioViewModel.ResizeRect(100, 100, 200, 100, Edges.Left | Edges.Top, -100, 0, keepAspect: true);
        Assert.Equal((0.0, 50.0, 300.0, 150.0), fromTopLeft);   // bottom-right corner stays put
    }

    [Fact]
    public void Resizing_a_shape_refits_its_label()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.AddShapeAt("roundrect", 0, 0, 240, 100);
        var shape = vm.Shapes.Single();
        shape.Text = "Internationalisation";
        var before = shape.LabelFontSize;
        var changed = new System.Collections.Generic.List<string?>();
        shape.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ResizeShape(shape, (0, 0, 240, 100), Edges.Right, -170, 0, keepAspect: false);

        Assert.Equal(70, shape.Width);
        Assert.Contains(nameof(ShapeCanvasItemViewModel.LabelFontSize), changed);
        Assert.True(shape.LabelFontSize < before);
    }

    [Fact]
    public void A_rubber_band_selects_every_shape_it_touches()
    {
        var vm = ThreeRects();
        int hit = vm.SelectInRect(0, 0, 210, 160, additive: false);   // touches the first two
        Assert.Equal(2, hit);
        Assert.Equal(2, vm.SelectionCount);
        Assert.True(vm.Shapes[0].IsSelected);
        Assert.True(vm.Shapes[1].IsSelected);
        Assert.False(vm.Shapes[2].IsSelected);
        Assert.NotNull(vm.SelectedShape);
        Assert.True(vm.CanAlign);
    }

    [Fact]
    public void A_rubber_band_with_Ctrl_adds_and_without_it_replaces()
    {
        var vm = ThreeRects();
        vm.SelectedShape = vm.Shapes[2];
        vm.SelectInRect(0, 0, 50, 60, additive: true);
        Assert.Equal(2, vm.SelectionCount);
        Assert.True(vm.Shapes[2].IsSelected);

        vm.SelectInRect(190, 140, 20, 20, additive: false);
        Assert.Equal(1, vm.SelectionCount);
        Assert.Same(vm.Shapes[1], vm.SelectedShape);
    }

    [Fact]
    public void A_rubber_band_over_empty_canvas_selects_nothing()
    {
        var vm = ThreeRects();
        Assert.Equal(0, vm.SelectInRect(1000, 1000, 50, 50, additive: false));
        Assert.Equal(0, vm.SelectionCount);
        Assert.Null(vm.SelectedShape);
    }

    [Fact]
    public void A_group_held_against_the_edge_keeps_its_layout()
    {
        var vm = ThreeRects();
        vm.SelectAll();
        // Drag far left: the group stops when its leftmost shape (x=10) reaches the edge.
        var moved = vm.NudgeSelection(-300, 0);
        Assert.Equal(-10, moved.Dx);
        Assert.Equal(new double[] { 0, 190, 70 }, vm.Shapes.Select(s => s.X).ToArray());

        // Nothing more to give: a further push reports no movement.
        Assert.Equal((0.0, 0.0), vm.NudgeSelection(-5, 0));
    }

    [Fact]
    public async System.Threading.Tasks.Task Layer_ordering_reorders_shapes_preserves_selection_and_undoes()
    {
        var vm = ThreeRects();
        var a = vm.Shapes[0];
        var b = vm.Shapes[1];
        var c = vm.Shapes[2];
        a.Text = "A";
        b.Text = "B";
        c.Text = "C";

        vm.SelectedShape = a;
        vm.BringForward();
        Assert.Equal(new[] { "B", "A", "C" }, vm.Shapes.Select(s => s.Text).ToArray());
        Assert.Same(a, vm.SelectedShape);

        vm.BringToFront();
        Assert.Equal(new[] { "B", "C", "A" }, vm.Shapes.Select(s => s.Text).ToArray());
        Assert.Same(a, vm.SelectedShape);

        // Already at the front: a second BringToFront is a no-op and does not push another undo step.
        vm.BringToFront();
        Assert.Equal("Already at the front.", vm.StatusMessage);

        vm.SendBackward();
        Assert.Equal(new[] { "B", "A", "C" }, vm.Shapes.Select(s => s.Text).ToArray());

        vm.SendToBack();
        Assert.Equal(new[] { "A", "B", "C" }, vm.Shapes.Select(s => s.Text).ToArray());
        Assert.Same(a, vm.SelectedShape);

        await vm.UndoAsync();
        Assert.Equal(new[] { "B", "A", "C" }, vm.Shapes.Select(s => s.Text).ToArray());
    }

    [Fact]
    public void Rotate_90_degrees_wraps_within_inspector_bounds_and_skips_connectors()
    {
        var vm = new ShapeDesignStudioViewModel();
        var box = vm.AddShapeAt("roundrect", 40, 40, 120, 60, text: "Step");
        var line = vm.AddConnectorLine(40, 120, 160, 120);

        vm.SelectedShape = box;
        vm.RotateClockwise();
        Assert.Equal(90, box.Rotation);
        vm.RotateClockwise();
        Assert.Equal(180, box.Rotation);
        vm.RotateClockwise();
        Assert.Equal(-90, box.Rotation);
        vm.RotateCounterClockwise();
        Assert.Equal(180, box.Rotation);

        vm.SelectedShape = line;
        vm.RotateClockwise();
        Assert.Equal(0, line.Rotation);
        Assert.Contains("endpoints", vm.StatusMessage);

        // Dense read-only mode (>= 50 shapes) also blocks rotation mutations.
        while (vm.Shapes.Count < ShapeDesignStudioViewModel.DenseThreshold)
        {
            vm.Shapes.Add(new ShapeItemViewModel { Id = $"s{vm.Shapes.Count + 1}", Prst = "rect" });
        }
        Assert.True(vm.IsDense);
        vm.SelectedShape = box;
        vm.RotateClockwise();
        Assert.Equal(180, box.Rotation);
    }
}
