using System.Linq;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// The Shape Studio's "Shapes on canvas" list named every connector "Line": the org chart preset
/// listed six identical rows after its boxes. Lines now say what they join.
/// </summary>
public class ShapeStudioLineNamesTests
{
    [Fact]
    public void A_line_between_two_labelled_shapes_names_both()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.AddShapeAt("roundrect", 100, 40, 160, 60, text: "Executive Board");
        vm.AddShapeAt("roundrect", 100, 200, 160, 60, text: "CEO / Operations\nRuns the business");
        var line = vm.AddConnectorLine(180, 100, 180, 200);

        Assert.Equal("Executive Board → CEO / Operations", line.ListTitle);
        Assert.Equal("Line", line.ListSubtitle);
    }

    [Fact]
    public void A_line_touching_one_shape_says_from_or_to()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.AddShapeAt("roundrect", 100, 40, 160, 60, text: "Board");
        vm.AddShapeAt("roundrect", 100, 300, 160, 60, text: "Team");
        var down = vm.AddConnectorLine(180, 100, 180, 150);
        var drop = vm.AddConnectorLine(180, 250, 180, 300);

        Assert.Equal("From Board", down.ListTitle);
        Assert.Equal("To Team", drop.ListTitle);
    }

    [Fact]
    public void A_line_touching_nothing_shows_its_direction()
    {
        var vm = new ShapeDesignStudioViewModel();
        var bar = vm.AddConnectorLine(40, 150, 400, 150);

        Assert.Equal("Line", bar.ListTitle);
        Assert.Equal("Horizontal", bar.ListSubtitle);
    }

    [Fact]
    public void Moving_or_renaming_a_shape_renames_its_lines()
    {
        var vm = new ShapeDesignStudioViewModel();
        var a = vm.AddShapeAt("rect", 0, 0, 100, 50, text: "A");
        vm.AddShapeAt("rect", 0, 200, 100, 50, text: "B");
        var line = vm.AddConnectorLine(50, 50, 50, 200);
        Assert.Equal("A → B", line.ListTitle);

        a.Text = "Alpha";
        Assert.Equal("Alpha → B", line.ListTitle);

        a.X = 500;
        Assert.Equal("To B", line.ListTitle);
    }

    [Fact]
    public void A_card_inside_a_lane_wins_over_the_lane()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.AddShapeAt("rect", 0, 0, 600, 100, text: "Engineering lane");
        vm.AddShapeAt("roundrect", 200, 20, 140, 56, text: "Build");
        var line = vm.AddConnectorLine(270, 76, 270, 180);

        Assert.Equal("From Build", line.ListTitle);
    }

    [Fact]
    public void The_org_chart_preset_has_no_bare_line_rows_left_but_its_bar()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.GenerateOrgChartTemplate();
        var lines = vm.Shapes.Where(s => s.Prst == "line").ToList();

        Assert.NotEmpty(lines);
        Assert.All(lines.Where(l => l.ListTitle == "Line"), l => Assert.Equal("Horizontal", l.ListSubtitle));
        Assert.Contains(lines, l => l.ListTitle.StartsWith("From Executive Board"));
    }

    [Fact]
    public void The_shapes_header_counts_after_a_preset_not_only_after_undo()
    {
        var vm = new ShapeDesignStudioViewModel();
        Assert.Equal("", vm.LineStats);
        vm.GenerateOrgChartTemplate();
        Assert.Equal($"{vm.Shapes.Count} shapes", vm.LineStats);
        vm.AddShapeAt("rect", 0, 0);
        Assert.Equal($"{vm.Shapes.Count} shapes", vm.LineStats);
    }

    [Fact]
    public void A_line_has_a_colour_and_an_editable_weight()
    {
        var vm = new ShapeDesignStudioViewModel();
        var box = vm.AddShapeAt("rect", 0, 0);
        var line = vm.AddConnectorLine(0, 0, 100, 0, strokeWidthPt: 2);

        Assert.False(box.IsLine);
        Assert.Equal("Fill", box.ColourLabel);
        Assert.True(line.IsLine);
        Assert.Equal("Colour", line.ColourLabel);

        line.StrokeWidthPt = 4.5;
        Assert.Equal(4.5, line.StrokeWidthPt);
        line.StrokeWidthPt = double.NaN; // an emptied NumberBox
        Assert.Equal(4.5, line.StrokeWidthPt);
        line.StrokeWidthPt = 500;
        Assert.Equal(ShapeCanvasItemViewModel.MaxStrokeWidthPt, line.StrokeWidthPt);
    }

    [Fact]
    public void A_weight_change_can_be_undone()
    {
        var vm = new ShapeDesignStudioViewModel();
        var line = vm.AddConnectorLine(0, 0, 100, 0, strokeWidthPt: 2);
        vm.RecordUndo();
        line.StrokeWidthPt = 6;

        vm.UndoCommand.Execute(null);

        Assert.Equal(2, vm.Shapes.Single().StrokeWidthPt);
    }
}
