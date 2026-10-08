using System;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;
using E = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.ResizeEdges;
using VM = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Shape Studio only showed resize handles on unrotated shapes, so a turned shape (the Funnel,
/// a rotated arrow) could only be resized through the inspector. Handles now follow the turn:
/// the drag is read in the shape's own frame and the opposite handle stays put on screen.
/// </summary>
public class ShapeStudioRotatedResizeTests
{
    private static void Near(double expected, double actual, double tol = 0.15) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

    [Fact]
    public void Unrotated_resizes_exactly_as_before()
    {
        Assert.Equal(VM.ResizeRect(10, 20, 100, 50, E.Right | E.Bottom, 30, 10, false),
                     VM.ResizeRotatedRect(10, 20, 100, 50, 0, E.Right | E.Bottom, 30, 10, false));
        Assert.Equal(VM.ResizeRect(10, 20, 100, 50, E.Left, -5, 0, false),
                     VM.ResizeRotatedRect(10, 20, 100, 50, 360, E.Left, -5, 0, false));
    }

    [Fact]
    public void A_quarter_turned_shape_widens_when_its_right_handle_is_dragged_down()
    {
        // Turned 90° clockwise, the shape's "right" points down the screen.
        var r = VM.ResizeRotatedRect(100, 100, 100, 40, 90, E.Right, 0, 30, false);
        Near(130, r.W);
        Near(40, r.H);
        // Dragging sideways along the screen does nothing to that handle.
        var side = VM.ResizeRotatedRect(100, 100, 100, 40, 90, E.Right, 30, 0, false);
        Near(100, side.W);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(180)]
    [InlineData(-45)]
    public void The_opposite_handle_stays_where_it_is_on_screen(double rotation)
    {
        const double x = 200, y = 150, w = 120, h = 60;
        var anchorBefore = VM.HandlePosition(x, y, w, h, rotation, E.Left | E.Top);
        var r = VM.ResizeRotatedRect(x, y, w, h, rotation, E.Right | E.Bottom, 25, -12, false);
        var anchorAfter = VM.HandlePosition(r.X, r.Y, r.W, r.H, rotation, E.Left | E.Top);
        Near(anchorBefore.X, anchorAfter.X, 0.2);
        Near(anchorBefore.Y, anchorAfter.Y, 0.2);
    }

    [Fact]
    public void The_dragged_handle_follows_the_pointer()
    {
        const double x = 200, y = 150, w = 120, h = 60, rot = 30;
        var before = VM.HandlePosition(x, y, w, h, rot, E.Right | E.Bottom);
        var r = VM.ResizeRotatedRect(x, y, w, h, rot, E.Right | E.Bottom, 20, 15, false);
        var after = VM.HandlePosition(r.X, r.Y, r.W, r.H, rot, E.Right | E.Bottom);
        Near(before.X + 20, after.X, 0.2);
        Near(before.Y + 15, after.Y, 0.2);
    }

    [Fact]
    public void Minimum_size_and_proportions_hold_when_turned()
    {
        var tiny = VM.ResizeRotatedRect(0, 0, 100, 50, 45, E.Right, -500, -500, false);
        Assert.Equal(VM.MinShapeSize, tiny.W);
        var kept = VM.ResizeRotatedRect(0, 0, 100, 50, 45, E.Right | E.Bottom, 40, 40, keepAspect: true);
        Near(2.0, kept.W / kept.H, 0.01);
    }

    [Theory]
    [InlineData(E.Right, 0, 0)]       // west-east
    [InlineData(E.Bottom, 0, 2)]      // north-south
    [InlineData(E.Right, 90, 2)]      // turned a quarter: the side handle points down
    [InlineData(E.Right | E.Bottom, 0, 1)]
    [InlineData(E.Right | E.Bottom, 90, 3)]
    [InlineData(E.Right, 45, 1)]
    [InlineData(E.Left, 180, 0)]
    public void Cursors_follow_the_handle_direction(E edges, double rotation, int axis) =>
        Assert.Equal(axis, VM.HandleCursorAxis(edges, rotation));

    [Fact]
    public void ResizeShape_uses_the_shapes_rotation()
    {
        var vm = new VM();
        var shape = new ShapeCanvasItemViewModel { X = 100, Y = 100, Width = 100, Height = 40, Rotation = 90 };
        vm.ResizeShape(shape, (100, 100, 100, 40), E.Right, 0, 30, keepAspect: false);
        Near(130, shape.Width);
    }
}
