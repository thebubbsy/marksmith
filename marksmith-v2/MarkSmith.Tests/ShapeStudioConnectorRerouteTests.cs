using System;
using System.Linq;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;
using VM = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Shape Studio connectors could be moved as a whole but never re-routed: to point a line at a
/// different box you deleted it and drew another. Each point of a selected line now has a
/// handle; dragging an end moves it, snapping onto a shape's side or centre.
/// </summary>
public class ShapeStudioConnectorRerouteTests
{
    private static void Near(double expected, double actual, double tol = 0.01) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

    private static (VM Vm, ShapeCanvasItemViewModel Line, ShapeCanvasItemViewModel Box) Setup()
    {
        var vm = new VM();
        vm.Shapes.Clear();
        var box = vm.AddShapeAt("rect", 300, 100, 120, 60, "4472C4");
        var line = vm.AddConnectorLine(100, 130, 200, 130);
        return (vm, line, box);
    }

    [Fact]
    public void A_lines_points_read_back_in_canvas_coordinates()
    {
        var (_, line, _) = Setup();
        var pts = VM.ConnectorPoints(line);
        Assert.Equal(2, pts.Count);
        Near(100, pts[0].X); Near(200, pts[1].X);
        // A horizontal connector has a 2 px box; its points sit on the centre line.
        Near(131, pts[0].Y, 1.01);
    }

    [Fact]
    public void Dragging_an_end_near_a_shape_snaps_it_to_that_sides_middle()
    {
        var (vm, line, box) = Setup();
        var snapped = vm.MoveConnectorPoint(line, 1, 296, 127);
        Assert.Same(box, snapped);
        var pts = VM.ConnectorPoints(line);
        Near(300, pts[1].X);  // the box's left side
        Near(130, pts[1].Y);  // half way down it
        Near(100, pts[0].X);  // the other end stays put
    }

    [Fact]
    public void Far_from_any_shape_or_with_snapping_off_the_end_goes_where_it_is_dropped()
    {
        var (vm, line, _) = Setup();
        Assert.Null(vm.MoveConnectorPoint(line, 1, 250, 220));
        var pts = VM.ConnectorPoints(line);
        Near(250, pts[1].X); Near(220, pts[1].Y);

        Assert.Null(vm.MoveConnectorPoint(line, 1, 298, 129, snap: false));
        pts = VM.ConnectorPoints(line);
        Near(298, pts[1].X); Near(129, pts[1].Y);
    }

    [Fact]
    public void A_line_turned_diagonal_gets_a_box_round_both_ends()
    {
        var (vm, line, _) = Setup();
        vm.MoveConnectorPoint(line, 0, 50, 300, snap: false);
        Near(50, line.X); Near(200, line.X + line.Width);
        var pts = VM.ConnectorPoints(line);
        Near(50, pts[0].X); Near(300, pts[0].Y);
        Assert.All(line.PathPoints!, p => Assert.InRange(p.X, 0, 100));
        Assert.All(line.PathPoints!, p => Assert.InRange(p.Y, 0, 100));
    }

    [Fact]
    public void A_bend_of_an_elbow_line_moves_freely_and_doesnt_snap()
    {
        var vm = new VM();
        vm.Shapes.Clear();
        var box = vm.AddShapeAt("rect", 300, 100, 120, 60, "4472C4");
        var elbow = vm.AddPolylinePath(new[] { (100.0, 100.0), (200.0, 100.0), (200.0, 200.0) });
        Assert.Null(vm.MoveConnectorPoint(elbow, 1, 301, 130)); // right on the box's side: still no snap
        var pts = VM.ConnectorPoints(elbow);
        Near(301, pts[1].X); Near(130, pts[1].Y);
        Near(100, pts[0].X); Near(200, pts[2].Y);
    }

    [Fact]
    public void Snap_targets_follow_a_turned_shape()
    {
        var (vm, line, box) = Setup();
        box.Rotation = 90;
        // Turned a quarter, the box's top side faces right: its middle is at (360 + 30, 130).
        var hit = vm.SnapTarget(392, 131, line);
        Assert.NotNull(hit);
        Near(390, hit!.Value.X); Near(130, hit.Value.Y);
    }

    [Fact]
    public void Re_routing_is_one_undo_step()
    {
        var (vm, line, _) = Setup();
        vm.RecordUndo();
        vm.MoveConnectorPoint(line, 1, 250, 220);
        vm.MoveConnectorPoint(line, 1, 260, 230);
        vm.UndoCommand.Execute(null);
        var back = vm.Shapes.Single(s => s.PathPoints is { Count: 2 });
        var pts = VM.ConnectorPoints(back);
        Near(200, pts[1].X, 1.01);
        Near(131, pts[1].Y, 1.01);
    }
}
