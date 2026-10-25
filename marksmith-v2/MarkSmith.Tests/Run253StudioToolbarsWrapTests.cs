using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #253, found with a UIA sweep for controls that are in the tree but laid out to nothing.
// - SmartArt Studio: the outline pane is a fixed 424 px column. Its header put the stats, text-volume and
//   find pills in an Auto column beside the Outline | Markdown tabs, so the tabs got zero width (UIA bounds
//   Empty at every window size) and the find pill's "Next match" button was cut off. The outline toolbar
//   cut Demote to a 9 px sliver.
// - SmartArt Studio: the preview zoom bar used a translucent white layer over the white WebView, so its
//   light text and icons were invisible in the dark theme.
// - Shape Studio: the canvas tool strip was one row in a star column beside the snap / colour controls;
//   7 tools (12 at the minimum window width) were clipped off its right end.
// - Document Galaxy: the connection legend's hint text ran off its right edge on a narrow map.
public class Run253StudioToolbarsWrapTests
{
    private static string DesktopDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MarkSmith.Desktop");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative)).Replace("\r\n", "\n");

    private const string SmartArt = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml";
    private const string Shape = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml";
    private const string Galaxy = "Views/MindMap/MindMapGalaxyWindow.xaml";

    // The text of the WrapRow element that opens at `start` (WrapRows don't nest in these files).
    private static string WrapRowAt(string xaml, int start)
    {
        Assert.StartsWith("<ui:WrapRow", xaml[start..]);
        var end = xaml.IndexOf("</ui:WrapRow>", start, StringComparison.Ordinal);
        Assert.True(end > start, "unclosed WrapRow");
        var body = xaml[start..end];
        Assert.DoesNotContain("<ui:WrapRow", body[1..]);
        return body;
    }

    private static string WrapRowContaining(string xaml, string marker)
    {
        var at = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{marker} not found");
        var start = xaml.LastIndexOf("<ui:WrapRow", at, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{marker} is not inside a WrapRow");
        var body = WrapRowAt(xaml, start);
        Assert.Contains(marker, body);
        return body;
    }

    [Theory]
    [InlineData(SmartArt)]
    [InlineData(Shape)]
    [InlineData(Galaxy)]
    public void EachFile_DeclaresTheControlsNamespace_AndBalancesItsWrapRows(string file)
    {
        var xaml = Read(file);
        Assert.Contains("xmlns:ui=\"using:MarkSmith.Controls\"", xaml);
        var opens = Regex.Matches(xaml, @"<ui:WrapRow\b").Count;
        Assert.True(opens > 0);
        Assert.Equal(opens, Regex.Matches(xaml, @"</ui:WrapRow>").Count);
    }

    [Fact]
    public void SmartArtOutlineHeader_WrapsThePillsInsteadOfSqueezingTheTabs()
    {
        var xaml = Read(SmartArt);
        var header = WrapRowContaining(xaml, "x:Name=\"EditorTabs\"");
        Assert.Contains("x:Name=\"OutlineTab\"", header);
        Assert.Contains("x:Name=\"MarkdownTab\"", header);
        Assert.Contains("x:Name=\"OutlineStatsPill\"", header);
        Assert.Contains("x:Name=\"OutlineMetricsPill\"", header);
        Assert.Contains("x:Name=\"OutlineSearchPill\"", header);
        Assert.Contains("x:Name=\"OutlineNextMatchButton\"", header);
        // The tabs come first, and no Auto column is left to claim the width ahead of them.
        Assert.True(header.IndexOf("x:Name=\"EditorTabs\"", StringComparison.Ordinal)
                    < header.IndexOf("x:Name=\"OutlineStatsPill\"", StringComparison.Ordinal));
        Assert.DoesNotContain("<SelectorBar x:Name=\"EditorTabs\" Grid.Column", xaml);
    }

    [Fact]
    public void SmartArtOutlineToolbar_FitsDemoteAndWrapsRatherThanClips()
    {
        var xaml = Read(SmartArt);
        var bar = WrapRowContaining(xaml, "x:Name=\"OutlineToolbar\"");
        foreach (var name in new[] { "RenameButton", "DuplicateButton", "DeleteButton", "MoveUpButton", "MoveDownButton", "PromoteButton", "DemoteButton" })
            Assert.Contains($"x:Name=\"{name}\"", bar);
        Assert.Contains("HorizontalSpacing=\"2\"", bar[..bar.IndexOf('>')]);
        // The two labelled buttons and both dividers were trimmed so the row fits the 424 px pane.
        Assert.Equal(2, Regex.Matches(bar, @"Height=""32"" Padding=""7,0""").Count);
        Assert.DoesNotContain("Padding=\"9,0\"", bar);
        Assert.Equal(2, Regex.Matches(bar, @"<Rectangle Width=""1"" Height=""20"" Margin=""2,0""").Count);
        // The right-click handler still anchors its menu on the toolbar.
        Assert.Contains("menu.ShowAt(OutlineToolbar, e.GetPosition(OutlineToolbar));", Read("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs"));
    }

    [Fact]
    public void SmartArtPreviewZoomBar_HasAnOpaqueThemedBackground()
    {
        var xaml = Read(SmartArt);
        var at = xaml.IndexOf("x:Name=\"PreviewZoomHudPill\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var tag = xaml[at..xaml.IndexOf('>', at)];
        Assert.Contains("Background=\"{ThemeResource SolidBackgroundFillColorTertiaryBrush}\"", tag);
        Assert.DoesNotContain("LayerOnAcrylic", tag);
        Assert.DoesNotContain("Subtle", tag);
    }

    [Fact]
    public void ShapeStudioToolStrip_WrapsSoNoToolIsCutOff()
    {
        var xaml = Read(Shape);
        var strip = WrapRowContaining(xaml, "x:Name=\"ToolStrip\"");
        foreach (var name in new[] { "ToolUndoButton", "AlignLeftButton", "DistributeVerticalButton", "BringToFrontButton",
                                     "SendToBackButton", "RotateLeftButton", "RotateRightButton", "DuplicateToolButton", "DeleteSelectionToolButton" })
            Assert.Contains($"x:Name=\"{name}\"", strip);
        Assert.DoesNotContain("<StackPanel x:Name=\"ToolStrip\"", xaml);
        // The snap / colour controls share a WrapRow with the strip (not an Auto column that squeezes it).
        var outer = xaml.LastIndexOf("<ui:WrapRow", xaml.IndexOf("x:Name=\"ToolStrip\"", StringComparison.Ordinal) - 12, StringComparison.Ordinal);
        Assert.True(outer >= 0);
        var toolbar = xaml[outer..xaml.IndexOf("x:Name=\"PaletteButton\"", outer, StringComparison.Ordinal)];
        Assert.Contains("x:Name=\"ToolStrip\"", toolbar);
        Assert.Contains("x:Name=\"GridSnapButton\"", toolbar);
        Assert.DoesNotContain("<ColumnDefinition", toolbar);
        // The size handler sums the children instead of measuring the WrapRow out of band at infinite width.
        var code = Read("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs");
        Assert.DoesNotContain("ToolStrip.Measure(", code);
        Assert.Contains("foreach (var child in ToolStrip.Children)", code);
    }

    [Fact]
    public void GalaxyConnectionLegend_WrapsItsHint()
    {
        var xaml = Read(Galaxy);
        var at = xaml.IndexOf("x:Name=\"ConnectionLegend\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var legendEnd = xaml.IndexOf("</Border>", at, StringComparison.Ordinal);
        var wrap = xaml.IndexOf("<ui:WrapRow", at, StringComparison.Ordinal);
        Assert.InRange(wrap, at, legendEnd);
        var body = WrapRowAt(xaml, wrap);
        Assert.Contains("Text=\"Parent and child\"", body);
        Assert.Contains("Drag or Alt+Arrows to move", body);
    }
}
