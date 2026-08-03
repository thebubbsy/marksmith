using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Core.Mermaid.Routing;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

using Point = MarkSmith.Core.Mermaid.Routing.Point;
using Rect = MarkSmith.Core.Mermaid.Routing.Rect;

namespace MarkSmith.Core.Tests.Mermaid;

public class RequirementR2EmpiricalChallengerTests
{
    // ==========================================
    // REQUIREMENT R2.1: Multi-Select & Bulk Move
    // ==========================================

    [Fact]
    public void SelectionState_ToggleMultiSelect_DeselectsAlreadySelectedNode()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();

        var n1 = new DiagramNodeViewModel { Id = "N1", X = 100, Y = 100, Width = 100, Height = 50 };
        var n2 = new DiagramNodeViewModel { Id = "N2", X = 300, Y = 100, Width = 100, Height = 50 };
        vm.Nodes.Add(n1);
        vm.Nodes.Add(n2);

        // Select N1
        vm.SelectNode(n1, false);
        Assert.Single(vm.SelectedNodes);
        Assert.True(n1.IsSelected);

        // Multi-select N2
        vm.SelectNode(n2, true);
        Assert.Equal(2, vm.SelectedNodes.Count);
        Assert.True(n1.IsSelected);
        Assert.True(n2.IsSelected);

        // Multi-select N2 again -> deselects N2
        vm.SelectNode(n2, true);
        Assert.Single(vm.SelectedNodes);
        Assert.True(n1.IsSelected);
        Assert.False(n2.IsSelected);
        Assert.Equal(n1, vm.SelectedNode);
    }

    [Fact]
    public void SelectionState_SelectNodesInRect_IntersectsCorrectNodes()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();

        var n1 = new DiagramNodeViewModel { Id = "N1", X = 50, Y = 50, Width = 100, Height = 50 };
        var n2 = new DiagramNodeViewModel { Id = "N2", X = 200, Y = 50, Width = 100, Height = 50 };
        var n3 = new DiagramNodeViewModel { Id = "N3", X = 400, Y = 400, Width = 100, Height = 50 };

        vm.Nodes.Add(n1);
        vm.Nodes.Add(n2);
        vm.Nodes.Add(n3);

        // Marquee rectangle covering N1 and N2 (Rect: 0, 0, 320, 120)
        var selectionBounds = new Rect(0, 0, 320, 120);
        vm.SelectNodesInRect(selectionBounds, isAdditive: false);

        Assert.Equal(2, vm.SelectedNodes.Count);
        Assert.True(n1.IsSelected);
        Assert.True(n2.IsSelected);
        Assert.False(n3.IsSelected);
    }

    [Fact]
    public void BulkMove_ClampsIndividualNodes_AtCanvasMinimumBounds()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();

        var n1 = new DiagramNodeViewModel { Id = "N1", X = 20, Y = 30, Width = 100, Height = 50 };
        var n2 = new DiagramNodeViewModel { Id = "N2", X = 150, Y = 200, Width = 100, Height = 50 };

        vm.Nodes.Add(n1);
        vm.Nodes.Add(n2);

        vm.SelectNode(n1, false);
        vm.SelectNode(n2, true);

        // Move by dx = -100, dy = -100
        vm.MoveSelectedNodes(-100, -100);

        // N1 clamped to (10, 10)
        Assert.Equal(10, n1.X);
        Assert.Equal(10, n1.Y);

        // N2 moved to 150 - 100 = 50, 200 - 100 = 100
        Assert.Equal(50, n2.X);
        Assert.Equal(100, n2.Y);
    }

    [Fact]
    public void BulkMove_BothConnectedNodesMoved_TranslatesConnectorPathDirectly()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        vm.Connectors.Clear();

        var n1 = new DiagramNodeViewModel { Id = "N1", X = 100, Y = 100, Width = 100, Height = 50 };
        var n2 = new DiagramNodeViewModel { Id = "N2", X = 300, Y = 100, Width = 100, Height = 50 };
        vm.Nodes.Add(n1);
        vm.Nodes.Add(n2);

        vm.AddConnector(n1.Id, "Right", n2.Id, "Left");
        var connector = vm.Connectors.First();
        connector.PathData = "M 200.0,125.0 L 300.0,125.0";

        vm.SelectNode(n1, false);
        vm.SelectNode(n2, true);

        // Bulk move both nodes
        vm.MoveSelectedNodes(40, 20);

        Assert.Equal(240, connector.SourceX);
        Assert.Equal(145, connector.SourceY);
        Assert.Equal(340, connector.TargetX);
        Assert.Equal(145, connector.TargetY);
        Assert.Contains("M 240.0,145.0 L 340.0,145.0", connector.PathData);
    }

    [Fact]
    public void BulkMove_SingleConnectedNodeMoved_ReRoutesConnectorGeometry()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        vm.Connectors.Clear();

        var n1 = new DiagramNodeViewModel { Id = "N1", X = 100, Y = 100, Width = 100, Height = 50 };
        var n2 = new DiagramNodeViewModel { Id = "N2", X = 400, Y = 100, Width = 100, Height = 50 };
        vm.Nodes.Add(n1);
        vm.Nodes.Add(n2);

        vm.AddConnector(n1.Id, "Right", n2.Id, "Left");
        var connector = vm.Connectors.First();

        // Select ONLY N1
        vm.SelectNode(n1, false);

        double initialTargetX = connector.TargetX;
        double initialTargetY = connector.TargetY;

        vm.MoveSelectedNodes(50, 0);

        // SourceX updated, TargetX remained at N2
        Assert.Equal(n1.X + n1.Width, connector.SourceX);
        Assert.Equal(initialTargetX, connector.TargetX);
        Assert.Equal(initialTargetY, connector.TargetY);
    }


    // ==========================================
    // REQUIREMENT R2.2: Smart Orthogonal Routing
    // ==========================================

    [Fact]
    public void GetStubPoint_CalculatesOutwardStubForAnchors()
    {
        var bounds = new Rect(100, 100, 200, 100);

        var topAnchor = new Point(200, 100);
        var bottomAnchor = new Point(200, 200);
        var leftAnchor = new Point(100, 150);
        var rightAnchor = new Point(300, 150);

        var stubTop = OrthogonalRouter.GetStubPoint(topAnchor, bounds, 20.0);
        var stubBottom = OrthogonalRouter.GetStubPoint(bottomAnchor, bounds, 20.0);
        var stubLeft = OrthogonalRouter.GetStubPoint(leftAnchor, bounds, 20.0);
        var stubRight = OrthogonalRouter.GetStubPoint(rightAnchor, bounds, 20.0);

        Assert.Equal(new Point(200, 80), stubTop);
        Assert.Equal(new Point(200, 220), stubBottom);
        Assert.Equal(new Point(80, 150), stubLeft);
        Assert.Equal(new Point(320, 150), stubRight);
    }

    [Fact]
    public void GenerateRoundedPathData_Fillets90DegreeCorners_WithQuadBezier()
    {
        var points = new List<Point>
        {
            new Point(0, 0),
            new Point(100, 0),
            new Point(100, 100)
        };

        string pathData = OrthogonalRouter.GenerateRoundedPathData(points, cornerRadius: 10.0);

        Assert.StartsWith("M 0.0,0.0", pathData);
        Assert.Contains("L 90.0,0.0", pathData);
        Assert.Contains("Q 100.0,0.0 100.0,10.0", pathData);
        Assert.EndsWith("L 100.0,100.0", pathData);
    }

    [Fact]
    public void GenerateRoundedPathData_ShortSegments_ScalesRadiusGracefully()
    {
        var points = new List<Point>
        {
            new Point(0, 0),
            new Point(10, 0), // Segment length 10
            new Point(10, 10)
        };

        // cornerRadius requested = 20, but max allowed radius for length 10 is 5.0
        string pathData = OrthogonalRouter.GenerateRoundedPathData(points, cornerRadius: 20.0);

        Assert.Contains("L 5.0,0.0", pathData);
        Assert.Contains("Q 10.0,0.0 10.0,5.0", pathData);
    }

    [Fact]
    public void OrthogonalRouter_MultipleObstacles_FindsValidAvoidancePath()
    {
        var srcBounds = new Rect(0, 100, 100, 60);
        var tgtBounds = new Rect(600, 100, 100, 60);
        var srcAnchor = new Point(100, 130);
        var tgtAnchor = new Point(600, 130);

        // Two obstacle nodes blocking direct paths
        var obs1 = new Rect(200, 100, 100, 100);
        var obs2 = new Rect(400, 80, 100, 100);
        var obstacles = new List<Rect> { obs1, obs2 };

        var path = OrthogonalRouter.Route(srcBounds, tgtBounds, obstacles, srcAnchor, tgtAnchor);

        Assert.NotNull(path);
        Assert.True(path.Count >= 2);

        // Check that path segments avoid all obstacles (inflated)
        foreach (var obs in obstacles)
        {
            var inflated = obs.Inflate(10.0);
            for (int i = 0; i < path.Count - 1; i++)
            {
                bool intersects = OrthogonalRouter.SegmentIntersectsRect(path[i], path[i + 1], inflated);
                Assert.False(intersects, $"Segment [{path[i].X},{path[i].Y}]->[{path[i+1].X},{path[i+1].Y}] intersects obstacle at [{obs.X},{obs.Y}]");
            }
        }
    }


    // ==========================================
    // REQUIREMENT R2.3: Quick-Add Hover Menus
    // ==========================================

    [Fact]
    public void QuickAddNode_AllDirections_SpawnsNodeWithCorrectAnchorsAndOffsets()
    {
        var directions = new[] { "top", "up", "right", "bottom", "down", "left" };

        foreach (var dir in directions)
        {
            var vm = new MermaidStudioViewModel();
            vm.Nodes.Clear();
            vm.Connectors.Clear();

            var baseNode = new DiagramNodeViewModel { Id = "Base", X = 300, Y = 300, Width = 100, Height = 60 };
            vm.Nodes.Add(baseNode);

            var spawned = vm.QuickAddNode(baseNode, dir);

            Assert.NotNull(spawned);
            Assert.Equal(2, vm.Nodes.Count);
            Assert.Single(vm.Connectors);

            var conn = vm.Connectors.First();
            Assert.Equal(baseNode.Id, conn.SourceNodeId);
            Assert.Equal(spawned.Id, conn.TargetNodeId);

            switch (dir)
            {
                case "top":
                case "up":
                    Assert.True(spawned.Y < baseNode.Y);
                    Assert.Equal("Top", conn.SourceAnchor);
                    Assert.Equal("Bottom", conn.TargetAnchor);
                    break;
                case "right":
                    Assert.True(spawned.X > baseNode.X);
                    Assert.Equal("Right", conn.SourceAnchor);
                    Assert.Equal("Left", conn.TargetAnchor);
                    break;
                case "bottom":
                case "down":
                    Assert.True(spawned.Y > baseNode.Y);
                    Assert.Equal("Bottom", conn.SourceAnchor);
                    Assert.Equal("Top", conn.TargetAnchor);
                    break;
                case "left":
                    Assert.True(spawned.X < baseNode.X);
                    Assert.Equal("Left", conn.SourceAnchor);
                    Assert.Equal("Right", conn.TargetAnchor);
                    break;
            }
        }
    }

    [Fact]
    public void QuickAddNode_CollisionAvoidance_StepsOverExistingNodes()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();

        var n1 = new DiagramNodeViewModel { Id = "N1", X = 100, Y = 100, Width = 100, Height = 60 };
        // Place an existing node N2 right where QuickAdd "Right" would default (around 100 + 100 + 80 = 280, Y=100)
        var n2 = new DiagramNodeViewModel { Id = "N2", X = 280, Y = 100, Width = 100, Height = 60 };

        vm.Nodes.Add(n1);
        vm.Nodes.Add(n2);

        var spawned = vm.QuickAddNode(n1, "Right");

        Assert.NotNull(spawned);
        // Spawned node should avoid N2 by stepping further right
        var spawnedRect = new Rect(spawned.X, spawned.Y, spawned.Width, spawned.Height);
        var n2Rect = new Rect(n2.X, n2.Y, n2.Width, n2.Height);

        Assert.False(spawnedRect.IntersectsWith(n2Rect), "Spawned node collided with existing node N2");
        Assert.True(spawned.X > n2.X, "Spawned node did not step past N2");
    }
}
