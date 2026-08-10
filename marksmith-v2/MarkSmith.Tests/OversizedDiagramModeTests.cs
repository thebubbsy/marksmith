using MarkSmith.Models;
using MarkSmith.Services;
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

    [Fact]
    public void AppSettings_Default_IsAggressiveShrink()
    {
        // Product mandate: Aggressive Shrink (4) is the default mode always — and SettingsService
        // must never migrate a current 4 back to 1 (the migration is SettingsVersion-gated).
        Assert.Equal(4, new AppSettings().OversizedDiagramMode);
        Assert.Equal(2, new AppSettings().SettingsVersion); // fresh defaults skip the old migration
    }

    [Theory]
    [InlineData(7, 6)] // old 7 -> 6
    [InlineData(6, 5)] // old 6 -> 5
    [InlineData(5, 4)] // old 5 -> 4
    [InlineData(4, 1)] // old 4 (Grid) -> 1 (Keep Original Size)
    public void Migration_OldFileWithoutVersionKey_MapsModes(int stored, int expected)
    {
        var settings = new AppSettings { OversizedDiagramMode = stored };
        SettingsService.MigrateOversizedDiagramModes($$"""{"OversizedDiagramMode": {{stored}}}""", settings);
        Assert.Equal(expected, settings.OversizedDiagramMode);
        Assert.Equal(2, settings.SettingsVersion); // bumped so a later save never re-migrates
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Migration_CurrentFileWithVersionKey_PreservesModes(int stored)
    {
        var settings = new AppSettings { OversizedDiagramMode = stored };
        SettingsService.MigrateOversizedDiagramModes($$"""{"SettingsVersion": 2, "OversizedDiagramMode": {{stored}}}""", settings);
        Assert.Equal(stored, settings.OversizedDiagramMode); // current modes are never rewritten
        Assert.Equal(2, settings.SettingsVersion);
    }

    [Fact]
    public void Migration_StringValueContainingVersionKey_StillMigrates()
    {
        // Regression for the raw-substring false positive: a string VALUE that contains the
        // literal "SettingsVersion" (e.g. a custom cleanup-rule name) must NOT suppress the
        // migration of a genuinely old file — only the root property KEY gates.
        var settings = new AppSettings { OversizedDiagramMode = 5 };
        var json = """{"CustomNormalizationRules": [{"Name": "aSettingsVersionRule"}], "OversizedDiagramMode": 5}""";
        SettingsService.MigrateOversizedDiagramModes(json, settings);
        Assert.Equal(4, settings.OversizedDiagramMode); // migrated despite the lookalike value
        Assert.Equal(2, settings.SettingsVersion);
    }

    [Fact]
    public void Mode4_AggressiveShrink_ReanchorsConnectorsOntoShapes()
    {
        // Regression: the glue path (ReanchorConnectorsToShapes) only ran for modes 5/6/7, so
        // Aggressive Shrink (mode 4 — the forced default) emitted connectors whose endpoints
        // stayed at the pre-shrink coordinates: "nothing is connected" after downsizing.
        var d = BigDiagram();
        d.Connectors.Add(new MConnector
        {
            FromShapeId = 2, ToShapeId = 3,      // ToParagraphXml assigns shapes Ids 2,3 in list order
            FromConnectionSite = 3, ToConnectionSite = 0, // right edge of A → top centre of B
            X1 = 0, Y1 = 0, X2 = 700, Y2 = 900,  // deliberately pre-shrink — must be re-anchored
        });

        var theme = new ThemeDefinition("Light", "#ffffff", "#111111", "#222222", "#f4f4f4", "#d9d9d9", "#0078d4", "#e8f4fd", "#bfbfbf");
        DocxShapeEmitter.ToParagraphXml(d, theme, 1u, out _, oversizedMode: 4, smartConnectors: true);

        var from = d.Shapes[0];
        var to = d.Shapes[1];
        Assert.Equal(from.X + from.W, d.Connectors[0].X1, 3);   // right edge of scaled A
        Assert.Equal(from.Y + from.H / 2, d.Connectors[0].Y1, 3);
        Assert.Equal(to.X + to.W / 2, d.Connectors[0].X2, 3);   // top centre of scaled B
        Assert.Equal(to.Y, d.Connectors[0].Y2, 3);
        Assert.True(d.Connectors[0].X1 < 700); // endpoints moved off the pre-shrink canvas
    }
}
