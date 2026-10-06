using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>Diagram Studio node labels pick the colour that reads on the node's fill — a fixed light
/// label vanished on light fills (white Monochrome Print nodes, light-theme heading colours).</summary>
public class DiagramNodeLabelColorTests
{
    [Theory]
    [InlineData("#FFFFFF")]  // Monochrome Print preset
    [InlineData("#F5F5F5")]
    [InlineData("#4CC9F0")]  // studio cyan
    [InlineData("#FFD166")]
    public void Light_fills_get_dark_labels(string fill) =>
        Assert.Equal("#111827", DiagramNodeViewModel.ReadableLabelOn(fill));

    [Theory]
    [InlineData("#2B2D42")]  // studio default
    [InlineData("#313244")]  // Catppuccin Slate
    [InlineData("#062E23")]  // Emerald Corporate
    [InlineData("#000000")]
    public void Dark_fills_keep_light_labels(string fill) =>
        Assert.Equal("#EDF2F4", DiagramNodeViewModel.ReadableLabelOn(fill));
}
