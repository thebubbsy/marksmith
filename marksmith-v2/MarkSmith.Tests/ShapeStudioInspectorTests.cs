using MarkSmith.ViewModels.ShapeStudio;
using Xunit;

namespace MarkSmith.Core.Tests;

public class ShapeStudioInspectorTests
{
    [Theory]
    [InlineData("roundrect", "Rounded rectangle")]
    [InlineData("circulararrow", "Circular arrow")]
    [InlineData("ELLIPSE", "Ellipse")]
    [InlineData("sketch", "Sketch stroke")]
    [InlineData("flowChartProcess", "Flow chart process")]
    [InlineData("star10", "Star 10")]
    [InlineData("", "Shape")]
    [InlineData(null, "Shape")]
    public void DisplayNameFor_never_shows_a_raw_token(string? prst, string expected)
    {
        Assert.Equal(expected, ShapeCanvasItemViewModel.DisplayNameFor(prst));
    }

    [Fact]
    public void Every_palette_entry_has_a_curated_name()
    {
        foreach (var prst in ShapeDesignStudioViewModel.Palette)
        {
            var name = ShapeCanvasItemViewModel.DisplayNameFor(prst);
            Assert.False(string.Equals(name, prst, System.StringComparison.Ordinal), prst);
            Assert.True(char.IsUpper(name[0]), prst);
        }
    }

    [Fact]
    public void DisplayName_follows_Prst()
    {
        var shape = new ShapeCanvasItemViewModel { Prst = "rect" };
        var raised = false;
        shape.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(ShapeCanvasItemViewModel.DisplayName);
        shape.Prst = "diamond";
        Assert.True(raised);
        Assert.Equal("Diamond", shape.DisplayName);
    }

    [Fact]
    public void Cleared_number_box_does_not_poison_geometry()
    {
        var shape = new ShapeCanvasItemViewModel { X = 40, Width = 120 };
        shape.X = double.NaN;
        shape.Width = double.NaN;
        Assert.Equal(40, shape.X);
        Assert.Equal(120, shape.Width);
    }
}
