using System;
using System.Collections.Generic;
using System.Linq;
using MdToPdf.Core.Mermaid.Routing;
using MdToPdf.ViewModels.Mermaid;
using Xunit;

using Point = MdToPdf.Core.Mermaid.Routing.Point;
using Rect = MdToPdf.Core.Mermaid.Routing.Rect;

namespace MdToPdf.Core.Tests.Mermaid;

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
}
