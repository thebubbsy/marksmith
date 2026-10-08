using System;
using System.Globalization;
using System.Linq;
using MarkSmith.Core.Mermaid.Routing;
using MarkSmith.Mermaid.Ast;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

using Point = MarkSmith.Core.Mermaid.Routing.Point;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>
/// Diagram Studio connectors used to draw a bare line (no arrowheads, no dashes, no selection
/// state) with an empty label box on every edge. These pin what each Mermaid grammar's stored
/// style now draws, where the marker sits and which way it faces.
/// </summary>
public class DiagramConnectorAppearanceTests
{
    [Theory]
    // flowchart: LineStyle + EndHead
    [InlineData("Solid", "Normal", false, 1, ConnectorMarker.None, ConnectorMarker.Arrow)]
    [InlineData("Dashed", "Normal", true, 1, ConnectorMarker.None, ConnectorMarker.Arrow)]
    [InlineData("Thick", "None", false, 2, ConnectorMarker.None, ConnectorMarker.None)]
    [InlineData("Solid", "Circle", false, 1, ConnectorMarker.None, ConnectorMarker.Circle)]
    [InlineData("Solid", "Cross", false, 1, ConnectorMarker.None, ConnectorMarker.Cross)]
    // sequence: the message type lives in LineStyle
    [InlineData("SolidArrow", "Normal", false, 1, ConnectorMarker.None, ConnectorMarker.Arrow)]
    [InlineData("DashedArrow", "Normal", true, 1, ConnectorMarker.None, ConnectorMarker.Arrow)]
    [InlineData("DashedOpen", "Normal", true, 1, ConnectorMarker.None, ConnectorMarker.None)]
    [InlineData("PointArrow", "Normal", false, 1, ConnectorMarker.None, ConnectorMarker.OpenArrow)]
    [InlineData("CrossArrow", "Normal", false, 1, ConnectorMarker.None, ConnectorMarker.Cross)]
    // class: the relationship lives in EndHead; UML markers sit on the source (canonical <|-- form)
    [InlineData("Solid", "Inheritance", false, 1, ConnectorMarker.HollowTriangle, ConnectorMarker.None)]
    [InlineData("Solid", "Realization", true, 1, ConnectorMarker.HollowTriangle, ConnectorMarker.None)]
    [InlineData("Solid", "Aggregation", false, 1, ConnectorMarker.HollowDiamond, ConnectorMarker.None)]
    [InlineData("Solid", "Composition", false, 1, ConnectorMarker.FilledDiamond, ConnectorMarker.None)]
    [InlineData("Solid", "Dependency", true, 1, ConnectorMarker.None, ConnectorMarker.Arrow)]
    [InlineData("Solid", "Association", false, 1, ConnectorMarker.None, ConnectorMarker.Arrow)]
    public void Resolve_maps_every_grammar(string line, string end, bool dashed, double width, ConnectorMarker start, ConnectorMarker endMarker)
    {
        var look = ConnectorAppearance.Resolve(line, "None", end);
        Assert.Equal(dashed, look.Dashed);
        Assert.Equal(width, look.WidthScale);
        Assert.Equal(start, look.Start);
        Assert.Equal(endMarker, look.End);
    }

    [Fact]
    public void Arrow_points_at_the_tip_along_the_travel_direction()
    {
        // Arriving downwards at (100, 200): the base must be above the tip, centred on x = 100.
        var shape = ConnectorAppearance.Shape(ConnectorMarker.Arrow, new Point(100, 200), 0, 1, 2)!.Value;
        var pts = Points(shape.PathData);
        Assert.Equal(new Point(100, 200), pts[0]);
        Assert.All(pts.Skip(1), p => Assert.True(p.Y < 200));
        Assert.Equal(100, (pts[1].X + pts[2].X) / 2, 1);
        Assert.True(shape.FilledWithStroke);
    }

    [Fact]
    public void Hollow_and_stroke_only_markers_are_flagged()
    {
        var tri = ConnectorAppearance.Shape(ConnectorMarker.HollowTriangle, new Point(0, 0), 1, 0, 2)!.Value;
        Assert.False(tri.FilledWithStroke);
        Assert.False(tri.StrokeOnly);
        Assert.True(ConnectorAppearance.Shape(ConnectorMarker.OpenArrow, new Point(0, 0), 1, 0, 2)!.Value.StrokeOnly);
        Assert.True(ConnectorAppearance.Shape(ConnectorMarker.Cross, new Point(0, 0), 1, 0, 2)!.Value.StrokeOnly);
        Assert.Null(ConnectorAppearance.Shape(ConnectorMarker.None, new Point(0, 0), 1, 0, 2));
        Assert.Null(ConnectorAppearance.Shape(ConnectorMarker.Arrow, new Point(0, 0), 0, 0, 2));
    }

    [Fact]
    public void Marker_paths_use_invariant_numbers()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var shape = ConnectorAppearance.Shape(ConnectorMarker.Arrow, new Point(10.5, 20.5), 1, 0, 2)!.Value;
            Assert.StartsWith("M 10.5,20.5 L ", shape.PathData);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Connector_builds_end_arrow_facing_into_the_target()
    {
        var c = new DiagramConnectorViewModel { RoutingMode = ConnectorRoutingMode.Straight, EndHead = "Normal" };
        c.UpdateGeometry(new MarkSmith.ViewModels.Mermaid.Point(0, 0), new MarkSmith.ViewModels.Mermaid.Point(200, 0));
        Assert.Equal(string.Empty, c.StartMarkerData);
        var pts = Points(c.EndMarkerData);
        Assert.Equal(new Point(200, 0), pts[0]);
        Assert.All(pts.Skip(1), p => Assert.True(p.X < 200));
        Assert.Equal(c.StrokeColor, c.EndMarkerFill);
    }

    [Fact]
    public void Class_inheritance_puts_hollow_triangle_on_the_source_facing_it()
    {
        var c = new DiagramConnectorViewModel { RoutingMode = ConnectorRoutingMode.Straight, EndHead = "Inheritance" };
        c.UpdateGeometry(new MarkSmith.ViewModels.Mermaid.Point(0, 0), new MarkSmith.ViewModels.Mermaid.Point(200, 0));
        Assert.Equal(string.Empty, c.EndMarkerData);
        var pts = Points(c.StartMarkerData);
        Assert.Equal(new Point(0, 0), pts[0]);
        Assert.All(pts.Skip(1), p => Assert.True(p.X > 0));
        Assert.Equal(DiagramConnectorViewModel.CanvasColor, c.StartMarkerFill); // hollow
    }

    [Fact]
    public void Markers_follow_a_move()
    {
        var c = new DiagramConnectorViewModel { RoutingMode = ConnectorRoutingMode.Straight };
        c.UpdateGeometry(new MarkSmith.ViewModels.Mermaid.Point(0, 0), new MarkSmith.ViewModels.Mermaid.Point(200, 0));
        c.TranslateGeometry(50, 30);
        Assert.Equal(new Point(250, 30), Points(c.EndMarkerData)[0]);
    }

    [Fact]
    public void Style_changes_redraw_without_new_geometry()
    {
        var c = new DiagramConnectorViewModel { RoutingMode = ConnectorRoutingMode.Straight };
        c.UpdateGeometry(new MarkSmith.ViewModels.Mermaid.Point(0, 0), new MarkSmith.ViewModels.Mermaid.Point(200, 0));
        c.LineStyle = "Dashed";
        Assert.True(c.IsDashed);
        c.LineStyle = "Thick";
        Assert.False(c.IsDashed);
        Assert.Equal(c.StrokeWidth * 2, c.DisplayStrokeWidth);
        c.EndHead = "None";
        Assert.Equal(string.Empty, c.EndMarkerData);
    }

    [Fact]
    public void Label_box_only_when_there_is_a_label()
    {
        var c = new DiagramConnectorViewModel();
        Assert.False(c.HasLabel);
        c.Label = "  ";
        Assert.False(c.HasLabel);
        c.Label = "yes";
        Assert.True(c.HasLabel);
    }

    [Fact]
    public void Selection_and_hover_drive_colour_and_halo()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode("flowchart TD\n    A --> B\n    B --> C\n");
        var a = vm.Connectors.First();
        var b = vm.Connectors.Last();
        vm.SelectedConnector = a;
        Assert.True(a.IsSelected);
        Assert.Equal(DiagramConnectorViewModel.SelectionColor, a.DisplayStroke);
        Assert.True(a.HaloOpacity > 0);

        vm.SelectedConnector = b;
        Assert.False(a.IsSelected);
        Assert.Equal(a.StrokeColor, a.DisplayStroke);
        Assert.Equal(0, a.HaloOpacity);

        a.IsHovered = true;
        Assert.InRange(a.HaloOpacity, 0.01, b.HaloOpacity - 0.01);
    }

    [Theory]
    [InlineData("sequenceDiagram\n    A->>B: hi\n", "SolidArrow", "Normal")]
    [InlineData("classDiagram\n    class A\n    class B\n", "Solid", "Association")]
    [InlineData("erDiagram\n    A ||--o{ B : has\n", "Solid", "None")]
    [InlineData("flowchart TD\n    A --> B\n", "Solid", "Normal")]
    public void New_connectors_start_in_the_diagram_type_s_own_vocabulary(string code, string line, string end)
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(code);
        var ids = vm.Nodes.Select(n => n.Id).ToArray();
        vm.AddConnector(ids[0], "Right", ids[1], "Left");
        var added = vm.Connectors.Last();
        Assert.Equal(line, added.LineStyle);
        Assert.Equal(end, added.EndHead);
    }

    [Fact]
    public void Er_links_have_no_arrow_and_non_identifying_ones_are_dashed()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode("erDiagram\n    A ||--o{ B : has\n    B }o..o{ C : maybe\n");
        Assert.All(vm.Connectors, c => Assert.Equal(string.Empty, c.EndMarkerData));
        Assert.False(vm.Connectors.Single(c => c.Label == "has").IsDashed);
        Assert.True(vm.Connectors.Single(c => c.Label == "maybe").IsDashed);
    }

    [Fact]
    public void Class_boxes_grow_to_show_every_member()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode("classDiagram\n    class Customer {\n        +String name\n        +String email\n        +String phone\n        +Date joined\n    }\n");
        var node = vm.Nodes.Single();
        Assert.True(node.Height >= 5 * 19, $"height {node.Height}");
    }

    [Fact]
    public void State_start_and_end_are_dots()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode("stateDiagram-v2\n    [*] --> Idle\n    Idle --> Busy\n");
        var start = vm.Nodes.Single(n => n.Id == "[*]");
        Assert.True(start.IsPseudoState);
        Assert.Equal(28, start.Width);
        Assert.Equal(28, start.Height);
    }

    [Fact]
    public void Loading_a_diagram_announces_it_so_the_view_can_fit()
    {
        var vm = new MermaidStudioViewModel();
        int loads = 0;
        vm.DiagramLoaded += (_, _) => loads++;
        vm.LoadFromMermaidCode("flowchart TD\n    A --> B\n");
        Assert.Equal(1, loads);
        vm.SyncCanvasFromCode("flowchart TD\n    A --> B --> C\n"); // live typing: no refit
        Assert.Equal(1, loads);
    }

    private static Point[] Points(string path) =>
        path.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Contains(','))
            .Select(t => t.Split(','))
            .Select(a => new Point(double.Parse(a[0], CultureInfo.InvariantCulture), double.Parse(a[1], CultureInfo.InvariantCulture)))
            .ToArray();
}
