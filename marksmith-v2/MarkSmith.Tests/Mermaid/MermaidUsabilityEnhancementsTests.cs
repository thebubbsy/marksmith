using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Core.Mermaid.Routing;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

using Point = MarkSmith.Core.Mermaid.Routing.Point;
using Rect = MarkSmith.Core.Mermaid.Routing.Rect;

namespace MarkSmith.Core.Tests.Mermaid;

public class MermaidUsabilityEnhancementsTests
{
    [Fact]
    public void MultiSelect_BulkMove_Updates_Coordinates_Synchronously()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        vm.Connectors.Clear();

        var node1 = new DiagramNodeViewModel { Id = "Node1", X = 100, Y = 100, Width = 100, Height = 50 };
        var node2 = new DiagramNodeViewModel { Id = "Node2", X = 300, Y = 150, Width = 100, Height = 50 };
        var node3 = new DiagramNodeViewModel { Id = "Node3", X = 500, Y = 200, Width = 100, Height = 50 };

        vm.Nodes.Add(node1);
        vm.Nodes.Add(node2);
        vm.Nodes.Add(node3);

        // Add a connector connected to Node1
        vm.AddConnector(node1.Id, "Right", node3.Id, "Left");
        var connector = vm.Connectors.First();
        double initialConnSourceX = connector.SourceX;
        double initialConnSourceY = connector.SourceY;

        // Select Node1 and Node2
        vm.SelectNode(node1, false);
        vm.SelectNode(node2, true);

        Assert.Equal(2, vm.SelectedNodes.Count);
        Assert.True(node1.IsSelected);
        Assert.True(node2.IsSelected);
        Assert.False(node3.IsSelected);

        // Bulk move delta (dx = 50, dy = 30)
        double deltaX = 50;
        double deltaY = 30;

        double initial1X = node1.X;
        double initial1Y = node1.Y;
        double initial2X = node2.X;
        double initial2Y = node2.Y;

        // Call vm.MoveSelectedNodes directly per Reviewer 2 requirement
        vm.MoveSelectedNodes(deltaX, deltaY);

        Assert.Equal(initial1X + deltaX, node1.X);
        Assert.Equal(initial1Y + deltaY, node1.Y);
        Assert.Equal(initial2X + deltaX, node2.X);
        Assert.Equal(initial2Y + deltaY, node2.Y);

        // Unselected node3 remains unchanged
        Assert.Equal(500, node3.X);
        Assert.Equal(200, node3.Y);

        // Verify connected connector updated synchronously during bulk move
        Assert.Equal(initialConnSourceX + deltaX, connector.SourceX);
        Assert.Equal(initialConnSourceY + deltaY, connector.SourceY);
    }

    [Fact]
    public void MoveSelectedNodes_Clamps_To_Minimum_Canvas_Bounds()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();

        var node1 = new DiagramNodeViewModel { Id = "Node1", X = 20, Y = 20, Width = 100, Height = 50 };
        vm.Nodes.Add(node1);
        vm.SelectNode(node1, false);

        // Move by large negative delta
        vm.MoveSelectedNodes(-100, -100);

        Assert.Equal(10, node1.X);
        Assert.Equal(10, node1.Y);
    }

    [Fact]
    public void QuickAdd_UnrecognizedDirection_FallsBackToRightOffset()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();

        var node1 = new DiagramNodeViewModel { Id = "Node1", X = 100, Y = 100, Width = 100, Height = 50 };
        vm.Nodes.Add(node1);

        var spawnedNode = vm.QuickAddNode(node1, "InvalidDirectionString");

        Assert.NotNull(spawnedNode);
        Assert.True(spawnedNode.X > node1.X);
        Assert.Equal(node1.Y, spawnedNode.Y);
    }

    [Fact]
    public void Default_Connector_RoutingMode_Is_Orthogonal()
    {
        var connector = new DiagramConnectorViewModel();
        Assert.Equal(ConnectorRoutingMode.Orthogonal, connector.RoutingMode);
    }

    [Fact]
    public void OrthogonalRouter_Generates_90Degree_Path_Avoiding_Obstacles()
    {
        var sourceBounds = new Rect(0, 0, 100, 50);
        var targetBounds = new Rect(400, 0, 100, 50);

        var sourceAnchor = new Point(100, 25);
        var targetAnchor = new Point(400, 25);

        // Intermediate obstacle node directly between source and target
        var obstacleNode = new Rect(180, -30, 100, 110);
        var obstacles = new List<Rect> { obstacleNode };

        var path = OrthogonalRouter.Route(sourceBounds, targetBounds, obstacles, sourceAnchor, targetAnchor, padding: 10.0);

        Assert.NotNull(path);
        Assert.True(path.Count >= 2);

        // Verify start and end points
        Assert.Equal(sourceAnchor.X, path.First().X);
        Assert.Equal(sourceAnchor.Y, path.First().Y);
        Assert.Equal(targetAnchor.X, path.Last().X);
        Assert.Equal(targetAnchor.Y, path.Last().Y);

        // Verify all segments are orthogonal (90-degree bends, i.e. dx==0 or dy==0)
        for (int i = 0; i < path.Count - 1; i++)
        {
            var p1 = path[i];
            var p2 = path[i + 1];

            bool isHorizontal = Math.Abs(p1.Y - p2.Y) < 0.1;
            bool isVertical = Math.Abs(p1.X - p2.X) < 0.1;

            Assert.True(isHorizontal || isVertical, $"Segment [{p1.X},{p1.Y}] -> [{p2.X},{p2.Y}] is not orthogonal.");
        }

        // Verify no segment intersects the inflated obstacle node bounding box
        var inflatedObstacle = obstacleNode.Inflate(10.0);
        for (int i = 0; i < path.Count - 1; i++)
        {
            var p1 = path[i];
            var p2 = path[i + 1];

            bool intersects = OrthogonalRouter.SegmentIntersectsRect(p1, p2, inflatedObstacle);
            Assert.False(intersects, $"Segment [{p1.X},{p1.Y}] -> [{p2.X},{p2.Y}] intersects obstacle bounding box.");
        }
    }

    [Fact]
    public void QuickAdd_DirectionalArrow_SpawnsNode_And_Connector()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        vm.Connectors.Clear();

        var initialNode = new DiagramNodeViewModel
        {
            Id = "A",
            LabelText = "Source Node",
            X = 200,
            Y = 200,
            Width = 140,
            Height = 60
        };
        vm.Nodes.Add(initialNode);

        Assert.Single(vm.Nodes);
        Assert.Empty(vm.Connectors);

        // Quick add to the Right
        var spawnedNode = vm.QuickAddNode(initialNode, "Right");

        Assert.NotNull(spawnedNode);
        Assert.Equal(2, vm.Nodes.Count);
        Assert.Single(vm.Connectors);

        // Check new node coordinate offset to the right
        Assert.True(spawnedNode.X > initialNode.X + initialNode.Width);
        Assert.Equal(initialNode.Y, spawnedNode.Y);

        // Check connector links initialNode -> spawnedNode
        var connector = vm.Connectors.First();
        Assert.Equal(initialNode.Id, connector.SourceNodeId);
        Assert.Equal(spawnedNode.Id, connector.TargetNodeId);
        Assert.Equal("Right", connector.SourceAnchor);
        Assert.Equal("Left", connector.TargetAnchor);
    }

    [Fact]
    public void ZOrder_Commands_Reorder_Nodes_And_Update_CanBringForward_CanSendBackward()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();

        var n1 = new DiagramNodeViewModel { Id = "N1", LabelText = "One" };
        var n2 = new DiagramNodeViewModel { Id = "N2", LabelText = "Two" };
        var n3 = new DiagramNodeViewModel { Id = "N3", LabelText = "Three" };
        vm.Nodes.Add(n1);
        vm.Nodes.Add(n2);
        vm.Nodes.Add(n3);

        vm.SelectNode(n1, false);
        Assert.True(vm.CanBringForward());
        Assert.False(vm.CanSendBackward());

        vm.BringForwardCommand.Execute(null);
        Assert.Equal(new[] { "N2", "N1", "N3" }, vm.Nodes.OrderBy(n => n.ZIndex).Select(n => n.Id).ToArray());
        Assert.True(vm.CanBringForward());
        Assert.True(vm.CanSendBackward());

        vm.BringToFrontCommand.Execute(null);
        Assert.Equal(new[] { "N2", "N3", "N1" }, vm.Nodes.OrderBy(n => n.ZIndex).Select(n => n.Id).ToArray());
        Assert.False(vm.CanBringForward());
        Assert.True(vm.CanSendBackward());

        vm.SendBackwardCommand.Execute(null);
        Assert.Equal(new[] { "N2", "N1", "N3" }, vm.Nodes.OrderBy(n => n.ZIndex).Select(n => n.Id).ToArray());

        vm.SendToBackCommand.Execute(null);
        Assert.Equal(new[] { "N1", "N2", "N3" }, vm.Nodes.OrderBy(n => n.ZIndex).Select(n => n.Id).ToArray());
        Assert.True(vm.CanBringForward());
        Assert.False(vm.CanSendBackward());
    }

    [Fact]
    public void HoverGlowOpacity_Reflects_Hovered_Only_When_Unselected()
    {
        var node = new DiagramNodeViewModel { Id = "N1" };
        var changedProps = new List<string>();
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        Assert.Equal(0.0, node.HoverGlowOpacity);

        node.IsHovered = true;
        Assert.Equal(1.0, node.HoverGlowOpacity);
        Assert.Contains(nameof(DiagramNodeViewModel.HoverGlowOpacity), changedProps);

        node.IsSelected = true;
        Assert.Equal(0.0, node.HoverGlowOpacity);

        node.IsSelected = false;
        Assert.Equal(1.0, node.HoverGlowOpacity);

        node.IsHovered = false;
        Assert.Equal(0.0, node.HoverGlowOpacity);
    }

    [Fact]
    public void ReadableStrokeOnCanvas_Lifts_Dark_MonochromePrint_Strokes_On_Dark_Canvas_Without_Mutating_Export_Colors()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode("flowchart TD\n A --> B");
        Assert.NotEmpty(vm.Nodes);
        Assert.NotEmpty(vm.Connectors);

        vm.ActivePalette = "Monochrome Print";

        var node = vm.Nodes.First();
        var conn = vm.Connectors.First();

        // Export / Mermaid init directive colors stay true to Monochrome Print
        Assert.Equal("#1F2937", node.StrokeColor);
        Assert.Equal("#111827", conn.StrokeColor);

        // Dark-canvas display strokes are automatically lifted to high-contrast light slate
        Assert.Equal("#FFFFFF", node.CanvasStrokeColor);
        Assert.Equal("#FFFFFF", conn.DisplayStroke);

        // Selecting the connector highlights it in accent blue and restores readable stroke on deselect
        conn.IsSelected = true;
        Assert.Equal("#4CC9F0", conn.DisplayStroke);
        conn.IsSelected = false;
        Assert.Equal("#FFFFFF", conn.DisplayStroke);
    }

    [Fact]
    public void CycleSelection_Walks_Forward_And_Backward_With_WrapAround_And_Handles_Empty_Canvas()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        Assert.Null(vm.CycleSelection());

        var n1 = new DiagramNodeViewModel { Id = "A", LabelText = "Alpha", ZIndex = 0 };
        var n2 = new DiagramNodeViewModel { Id = "B", LabelText = "Beta", ZIndex = 1 };
        var n3 = new DiagramNodeViewModel { Id = "C", LabelText = "Gamma", ZIndex = 2 };
        vm.Nodes.Add(n1);
        vm.Nodes.Add(n2);
        vm.Nodes.Add(n3);

        Assert.Same(n1, vm.CycleSelection(reverse: false));
        Assert.Same(n1, vm.SelectedNode);
        Assert.Contains("Alpha", vm.StatusText);

        Assert.Same(n2, vm.CycleSelection(reverse: false));
        Assert.Same(n2, vm.SelectedNode);

        Assert.Same(n3, vm.CycleSelection(reverse: false));
        Assert.Same(n3, vm.SelectedNode);

        // Wrap around forward to first node
        Assert.Same(n1, vm.CycleSelection(reverse: false));
        Assert.Same(n1, vm.SelectedNode);

        // Wrap around backward to last node
        Assert.Same(n3, vm.CycleSelection(reverse: true));
        Assert.Same(n3, vm.SelectedNode);
    }

    [Fact]
    public void ReverseConnector_Swaps_Endpoints_And_Anchors_As_Single_Undo_Step()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        vm.Connectors.Clear();

        var a = new DiagramNodeViewModel { Id = "A", LabelText = "Start", X = 100, Y = 100 };
        var b = new DiagramNodeViewModel { Id = "B", LabelText = "End", X = 360, Y = 100 };
        vm.Nodes.Add(a);
        vm.Nodes.Add(b);
        vm.AddConnector("A", "Right", "B", "Left");

        var conn = vm.Connectors.Single();
        vm.SelectedConnector = conn;

        vm.ReverseConnectorCommand.Execute(null);

        conn = vm.Connectors.Single();
        Assert.Equal("B", conn.SourceNodeId);
        Assert.Equal("A", conn.TargetNodeId);
        Assert.Equal("Left", conn.SourceAnchor);
        Assert.Equal("Right", conn.TargetAnchor);
        Assert.Contains("Reversed connector", vm.StatusText);

        // Single Undo restores original direction
        vm.Undo();
        conn = vm.Connectors.Single();
        Assert.Equal("A", conn.SourceNodeId);
        Assert.Equal("B", conn.TargetNodeId);
        Assert.Equal("Right", conn.SourceAnchor);
        Assert.Equal("Left", conn.TargetAnchor);
    }

    [Fact]
    public void ChangeSelectedNodesShape_Updates_MultiSelection_In_Single_Undo_Step()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        vm.Connectors.Clear();

        var n1 = new DiagramNodeViewModel { Id = "N1", LabelText = "Check 1", Shape = "RoundedRectangle" };
        var n2 = new DiagramNodeViewModel { Id = "N2", LabelText = "Check 2", Shape = "Rectangle" };
        vm.Nodes.Add(n1);
        vm.Nodes.Add(n2);

        vm.SelectNode(n1, false);
        vm.SelectNode(n2, true);

        vm.ChangeSelectedNodesShape("Rhombus");

        Assert.All(vm.Nodes, n =>
        {
            Assert.Equal("Rhombus", n.Shape);
        });
        var code = vm.GenerateMermaidCode();
        Assert.Contains("{\"Check 1\"}", code);
        Assert.Contains("{\"Check 2\"}", code);

        // A single Undo must revert both nodes back to their original shapes
        vm.Undo();
        Assert.Equal("RoundedRectangle", vm.Nodes.First(n => n.Id == "N1").Shape);
        Assert.Equal("Rectangle", vm.Nodes.First(n => n.Id == "N2").Shape);
    }

    [Fact]
    public void FitSelectedNodesToLabel_Resizes_Long_Labels_And_Preserves_PseudoState_Size()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        vm.Connectors.Clear();

        var regular = new DiagramNodeViewModel
        {
            Id = "N1",
            LabelText = "Internationalisation & Regional Compliance Verification",
            Shape = "RoundedRectangle",
            Width = 100,
            Height = 44
        };
        var startDot = new DiagramNodeViewModel
        {
            Id = "S1",
            LabelText = "[*]",
            Shape = "Start",
            Width = 28,
            Height = 28
        };
        vm.Nodes.Add(regular);
        vm.Nodes.Add(startDot);

        vm.SelectNode(regular, false);
        vm.SelectNode(startDot, true);

        vm.FitSelectedNodesToLabelCommand.Execute(null);

        Assert.True(regular.Width > 100);
        Assert.Equal(28, startDot.Width);
        Assert.Equal(28, startDot.Height);

        vm.Undo();
        Assert.Equal(100, vm.Nodes.First(n => n.Id == "N1").Width);
    }
}
