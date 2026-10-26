using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using MarkSmith.ViewModels.MindMap;
using Xunit;

namespace MarkSmith.Tests;

// Run #279: Shape Studio connector port snap ring theme brushes & high-DPI scaling,
// active connector handle drag highlight & hover guard,
// MultiRotateHandle & MultiSelectionBadge keyboard context menu parity and hover lift,
// and natural CountText wording across Shape Studio, Galaxy, and Suite Hub.
public class Run279SnapRingKeyboardAndCountsTests
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

    private static string CoreDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Core", "MarkSmith.Core.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MarkSmith.Core");
    }

    private static string ReadDesktop(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative)).Replace("\r\n", "\n");
    private static string ReadCore(string relative) => File.ReadAllText(Path.Combine(CoreDir(), relative)).Replace("\r\n", "\n");

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing {signature}");
        int lineStart = source.LastIndexOf('\n', start) + 1;
        int indent = start - lineStart - (source.Substring(lineStart, start - lineStart).TrimStart().Length);
        var m = Regex.Match(source.Substring(start + signature.Length),
            @"\n {" + indent + @"}(?! )(private|public|internal|protected|//|\[|///)");
        return m.Success ? source.Substring(start, signature.Length + m.Index) : source.Substring(start);
    }

    private const string ShapeStudio = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string Galaxy = "Views/MindMap/MindMapGalaxyWindow.xaml.cs";
    private const string SuiteHub = "Views/SuiteHubView.xaml.cs";

    // ---- Shape Studio Connector Port Snap Ring & Handles ----

    [Fact]
    public void SnapRing_UsesThemeBrushes_AndHighDpiStrokeThickness()
    {
        var src = ReadDesktop(ShapeStudio);
        var ensureBody = Body(src, "private void EnsurePointAdorner()");
        Assert.Contains("ThemeBrush.Set(_snapRing, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, \"AccentFillColorDefaultBrush\");", ensureBody);
        Assert.Contains("ThemeBrush.Set(_snapRing, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"AccentFillColorTertiaryBrush\");", ensureBody);
        Assert.DoesNotContain("Stroke = new SolidColorBrush(AccentColor(0xFF))", ensureBody);

        var moveBody = Body(src, "private void OnPointHandleMoved(");
        Assert.Contains("_snapRing.StrokeThickness = Math.Max(1.5, 2.0 / z);", moveBody);
        Assert.Contains("AutomationProperties.SetName(_snapRing, $\"Snapped to {target.DisplayName.ToLowerInvariant()}\");", moveBody);
    }

    [Fact]
    public void PointHandles_HighlightWhenDragged_AndResetOnRelease()
    {
        var src = ReadDesktop(ShapeStudio);
        var updateBody = Body(src, "private void UpdatePointHandles(");
        Assert.Contains("h.StrokeThickness = Math.Max(1.25, 1.5 / z);", updateBody);
        Assert.Contains("if (_pointIndex < 0) ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"AccentFillColorSecondaryBrush\");", updateBody);
        Assert.Contains("if (_pointIndex < 0) ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"SolidBackgroundFillColorPrimaryBrush\");", updateBody);

        var pressBody = Body(src, "private void OnPointHandlePressed(");
        Assert.Contains("ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"AccentFillColorDefaultBrush\");", pressBody);

        var releaseBody = Body(src, "private void OnPointHandleReleased(");
        Assert.Contains("ThemeBrush.Set(ell, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"SolidBackgroundFillColorPrimaryBrush\");", releaseBody);
    }

    // ---- MultiRotateHandle & MultiSelectionBadge Accessibility Parity ----

    [Fact]
    public void MultiRotateHandleAndBadge_BridgedInKeyboardContextMenu()
    {
        var src = ReadDesktop(ShapeStudio);
        var bridgeBody = Body(src, "KeyboardContextMenu.BridgeAll(");
        Assert.Contains("(MultiRotateHandle, OnMultiRotateHandleRightTapped)", bridgeBody);
        Assert.Contains("(MultiSelectionBadge, OnMultiSelectionBadgeRightTapped)", bridgeBody);

        var ctorBody = Body(src, "if (MultiRotateHandle != null)");
        Assert.Contains("MultiRotateHandle.PointerEntered += (_, _) => ThemeBrush.Set(MultiRotateHandle, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"AccentFillColorSecondaryBrush\");", ctorBody);
        Assert.Contains("MultiRotateHandle.PointerExited += (_, _) => ThemeBrush.Set(MultiRotateHandle, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"AccentFillColorDefaultBrush\");", ctorBody);

        var rotateRightTapped = Body(src, "private void OnMultiRotateHandleRightTapped(");
        Assert.Contains("flyout.ShowAt(MultiRotateHandle, KeyboardContextMenu.PointIn(e, MultiRotateHandle));", rotateRightTapped);

        var badgeRightTapped = Body(src, "private void OnMultiSelectionBadgeRightTapped(");
        Assert.Contains("ShowMultiSelectionContextMenu(sender is FrameworkElement fe ? KeyboardContextMenu.PointIn(e, fe) : e.GetPosition(MainCanvas));", badgeRightTapped);
    }

    // ---- CountText Integration Across Surfaces ----

    [Fact]
    public void ShapeStudio_UsesCountText_ForStatusMessages()
    {
        var src = ReadDesktop(ShapeStudio);
        Assert.Contains("CountText.Of(n, \"shape\")", src);
        Assert.Contains("CountText.Of(sel.Count, \"shape\")", src);
        Assert.Contains("CountText.Of(movedCount, \"shape\")", src);
        Assert.Contains("CountText.Of(updatedCount, \"shape\")", src);
        Assert.DoesNotContain("n == 1 ? \"\" : \"s\"", src);
    }

    [Fact]
    public void MindMapStudioViewModel_UsesCountText_ForCounters()
    {
        var src = ReadCore("ViewModels/MindMap/MindMapStudioViewModel.cs");
        Assert.Contains("public string NodesCountText => CountText.Of(Nodes.Count, \"node\");", src);
        Assert.Contains("public string LinksCountText => CountText.Of(Links.Count, \"link\");", src);

        var nodeSrc = ReadCore("ViewModels/MindMap/MindMapNodeViewModel.cs");
        Assert.Contains("VersionCountText = CountText.Of(versions.Count, \"version\");", nodeSrc);

        var galaxySrc = ReadDesktop(Galaxy);
        Assert.Contains("Text = CountText.Of(count, \"node\")", galaxySrc);
    }

    [Fact]
    public void SuiteHubView_UsesCountText_ForDocumentAndSearchCounts()
    {
        var src = ReadDesktop(SuiteHub);
        Assert.Contains("private static string DocumentCount(int count) => CountText.Of(count, \"document\");", src);
        Assert.Contains("TemplateSearchStatusText.Text = visibleCount > 0 ? CountText.Of(visibleCount, \"match\", \"matches\") : \"No templates match\";", src);
    }

    [Theory]
    [InlineData(1, "shape", null, "1 shape")]
    [InlineData(0, "shape", null, "0 shapes")]
    [InlineData(4, "shape", null, "4 shapes")]
    [InlineData(1, "match", "matches", "1 match")]
    [InlineData(3, "match", "matches", "3 matches")]
    [InlineData(1, "node", null, "1 node")]
    [InlineData(12, "node", null, "12 nodes")]
    public void CountText_FormatsSingularAndPluralCorrectly(int count, string singular, string? plural, string expected)
    {
        Assert.Equal(expected, CountText.Of(count, singular, plural));
    }
}
