using MarkSmith.Models;
using MarkSmith.Services.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Pins the oversized-diagram strategy numbering AFTER the poster-grid (2×2/3×3) mode was
/// removed. Old mode 4 (Grid) is gone; old 5-8 shifted to 4-7. These numbers are the contract
/// between the Settings combo, the export prompt, the SettingsService migration and the
/// emitter — a future reindex breaks this file and the export silently.
/// </summary>
public class OversizedDiagramModeTests
{
    private static MDiagram BigDiagram()
    {
        var d = new MDiagram { Width = 700, Height = 900 }; // moderately past the 460x640 printable window
        d.Shapes.Add(new MShape { X = 0, Y = 0, W = 300, H = 200, Kind = ShapeKind.Rect, Text = "A" });
        d.Shapes.Add(new MShape { X = 350, Y = 500, W = 300, H = 200, Kind = ShapeKind.Rect, Text = "B" });
        return d;
    }

    [Fact]
    public void Mode3_MultiPageVertical_NeverWebLayout()
    {
        var d = BigDiagram();
        bool oversized = DocxShapeEmitter.ScaleToFit(d, oversizedMode: 3);
        Assert.False(oversized); // caller splits into page bands
        Assert.True(d.Width <= 460); // width constrained to the printable window
    }

    [Fact]
    public void Mode1_Exact_PreservesSizeAndReportsOversized()
    {
        var d = BigDiagram();
        bool oversized = DocxShapeEmitter.ScaleToFit(d, oversizedMode: 1);
        Assert.True(oversized); // opens in Web Layout
        Assert.Equal(700, d.Width); // untouched
    }

    [Fact]
    public void Mode4_AggressiveShrink_FitsOnePage()
    {
        var d = BigDiagram();
        bool oversized = DocxShapeEmitter.ScaleToFit(d, oversizedMode: 4);
        Assert.False(oversized); // never Web Layout — squeezed onto one page
        Assert.True(d.Width <= 460 && d.Height <= 640);
    }

    [Theory]
    [InlineData(5)] // Compress Gaps
    [InlineData(6)] // Compress Nodes
    [InlineData(7)] // Compress Both
    public void CompactModes_NeverWebLayout(int mode)
    {
        var d = BigDiagram();
        bool oversized = DocxShapeEmitter.ScaleToFit(d, oversizedMode: mode);
        Assert.False(oversized);
        Assert.True(d.Width <= 460 && d.Height <= 640);
    }

    [Fact]
    public void Mode4_IsTheAggressiveFloor_NotTheRemovedGridMode()
    {
        // The removed poster-grid mode used to sit at 4. A grid-sized canvas would have been
        // width*2 — assert the diagram is NOT scaled by a grid multiplier anywhere.
        var d = BigDiagram();
        DocxShapeEmitter.ScaleToFit(d, oversizedMode: 4);
        Assert.Equal(460, d.Width); // uniform shrink to the printable window, not 2x width
    }
}
