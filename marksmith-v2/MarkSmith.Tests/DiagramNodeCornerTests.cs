using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

// Diagram Studio drew every box-shaped node with one fixed 10 px radius, so Rectangle, Rounded
// Rectangle, Stadium and Subroutine were indistinguishable on the canvas.
public class DiagramNodeCornerTests
{
    [Theory]
    [InlineData("Rectangle", 60, 2)]
    [InlineData("Subroutine", 60, 2)]
    [InlineData("RoundedRectangle", 60, 10)]
    [InlineData("NormalState", 60, 10)]
    [InlineData("Stadium", 60, 30)]   // a true pill: half the height
    [InlineData("Stadium", 84, 42)]
    public void BoxCornerRadius_FollowsTheShape(string shape, double height, double expected)
    {
        var node = new DiagramNodeViewModel { Shape = shape, Height = height };
        Assert.Equal(expected, node.BoxCornerRadius);
    }

    [Fact]
    public void BoxCornerRadius_UpdatesWhenShapeOrHeightChanges()
    {
        var node = new DiagramNodeViewModel { Shape = "Rectangle", Height = 60 };
        var raised = 0;
        node.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DiagramNodeViewModel.BoxCornerRadius)) raised++; };
        node.Shape = "Stadium";
        node.Height = 100;
        Assert.Equal(2, raised);
        Assert.Equal(50, node.BoxCornerRadius);
    }
}
