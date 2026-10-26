using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #278: Shape Studio theme handles, Document Galaxy WCAG badge ink,
// Status bar StatusMoreCopyButton keyboard/mouse parity, and Suite Hub count wording.
public class Run278HandlesPalettesAndStatusBarTests
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
    private const string MainWindowXaml = "MainWindow.xaml";
    private const string MainWindowCs = "MainWindow.xaml.cs";
    private const string SuiteHub = "Views/SuiteHubView.xaml.cs";

    // ---- Shape Studio Theme Handles ----

    [Fact]
    public void ShapeStudioHandles_UseThemeBrushes_NotHardcodedWhite()
    {
        var src = Read(ShapeStudio);
        var resizeBody = Body(src, "private void BuildResizeHandles()");
        Assert.Contains("ThemeBrush.Set(_rotateStem, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, \"AccentFillColorDefaultBrush\");", resizeBody);
        Assert.Contains("ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"SolidBackgroundFillColorPrimaryBrush\");", resizeBody);
        Assert.Contains("ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, \"AccentFillColorDefaultBrush\");", resizeBody);
        Assert.Contains("ThemeBrush.Set(_rotateHandle, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"SolidBackgroundFillColorPrimaryBrush\");", resizeBody);
        Assert.Contains("ThemeBrush.Set(_rotateHandle, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, \"AccentFillColorDefaultBrush\");", resizeBody);

        var pointBody = Body(src, "private void UpdatePointHandles(");
        Assert.Contains("ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"SolidBackgroundFillColorPrimaryBrush\");", pointBody);
        Assert.Contains("ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, \"AccentFillColorDefaultBrush\");", pointBody);

        Assert.DoesNotContain("Fill = new SolidColorBrush(Microsoft.UI.Colors.White)", resizeBody);
        Assert.DoesNotContain("Fill = new SolidColorBrush(Microsoft.UI.Colors.White)", pointBody);
    }

    [Fact]
    public void ShapeStudioHandles_PointerEnteredAndExited_ToggleAccentFill()
    {
        var src = Read(ShapeStudio);
        var resizeBody = Body(src, "private void BuildResizeHandles()");
        Assert.Contains("ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"AccentFillColorSecondaryBrush\")", resizeBody);
        Assert.Contains("ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"SolidBackgroundFillColorPrimaryBrush\")", resizeBody);
        Assert.Contains("ThemeBrush.Set(_rotateHandle, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"AccentFillColorSecondaryBrush\")", resizeBody);
        Assert.Contains("ThemeBrush.Set(_rotateHandle, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"SolidBackgroundFillColorPrimaryBrush\")", resizeBody);

        var pointBody = Body(src, "private void UpdatePointHandles(");
        Assert.Contains("ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"AccentFillColorSecondaryBrush\")", pointBody);
        Assert.Contains("ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, \"SolidBackgroundFillColorPrimaryBrush\")", pointBody);
    }

    // ---- Document Galaxy Badge Ink ----

    [Fact]
    public void GalaxyBadgeInk_UsesReadableInk_AndInitializesFromBadgeColor()
    {
        var src = Read(Galaxy);
        var buildBody = Body(src, "private NodeVisual BuildNodeVisual(");
        Assert.Contains("BadgeFill = new SolidColorBrush(ColorFromHex(node.ColorHex)),", buildBody);
        Assert.Contains("BadgeInk = new SolidColorBrush(ReadableOn(ColorFromHex(node.ColorHex))),", buildBody);
        Assert.DoesNotContain("BadgeInk = new SolidColorBrush(Colors.White),", buildBody);

        var readableOnBody = Body(src, "private static Color ReadableOn(");
        Assert.Contains("ReadableInk.PrefersDarkInk(background.R, background.G, background.B)", readableOnBody);
        Assert.DoesNotContain("0.299 * background.R", readableOnBody);
    }

    [Theory]
    [InlineData(255, 160, 0)]   // Amber #FFA000
    [InlineData(0, 150, 136)]   // Teal #009688
    [InlineData(0, 188, 212)]   // Cyan #00BCD4
    [InlineData(205, 220, 57)]  // Lime #CDDC39
    [InlineData(76, 175, 80)]   // Light Green #4CAF50
    public void GalaxyBadgeInk_LightAndMediumAccents_TakeDarkInkWithHighContrast(byte r, byte g, byte b)
    {
        Assert.True(ReadableInk.PrefersDarkInk(r, g, b));
        string hex = $"#{r:X2}{g:X2}{b:X2}";
        double onDark = ContrastGuard.GetContrastRatio(hex, "#111827");
        double onWhite = ContrastGuard.GetContrastRatio(hex, "#FFFFFF");
        Assert.True(onDark >= 4.5, $"{hex} dark ink contrast {onDark:F2}:1 is below 4.5:1");
        Assert.True(onDark > onWhite, $"{hex} dark ink ({onDark:F2}:1) should exceed white ink ({onWhite:F2}:1)");
    }

    [Theory]
    [InlineData(30, 58, 138)]   // Dark Blue #1E3A8A
    [InlineData(76, 29, 149)]   // Dark Purple #4C1D95
    [InlineData(51, 65, 85)]    // Slate #334155
    [InlineData(0, 0, 0)]       // Black #000000
    public void GalaxyBadgeInk_DarkAccents_KeepWhiteInkWithHighContrast(byte r, byte g, byte b)
    {
        Assert.False(ReadableInk.PrefersDarkInk(r, g, b));
        string hex = $"#{r:X2}{g:X2}{b:X2}";
        double onWhite = ContrastGuard.GetContrastRatio(hex, "#FFFFFF");
        Assert.True(onWhite >= 4.5, $"{hex} white ink contrast {onWhite:F2}:1 is below 4.5:1");
    }

    // ---- Status Bar More Copy Button ----

    [Fact]
    public void StatusMoreCopyButton_HasRightTappedAndUpdatedTooltip()
    {
        var xaml = Read(MainWindowXaml);
        Assert.Contains("x:Name=\"StatusMoreCopyButton\"", xaml);
        Assert.Contains("RightTapped=\"OnStatusMoreCopyButtonRightTapped\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"More copy actions: code, tables, tasks, math, callouts, images, footnotes, diagrams and frontmatter · Click, Right-click or Shift+F10 for menu\"", xaml);
    }

    [Fact]
    public void StatusMoreCopyButton_IsRegisteredInKeyboardContextMenuAndDispatches()
    {
        var cs = Read(MainWindowCs);
        Assert.Contains("(StatusMoreCopyButton, OnStatusMoreCopyButtonRightTapped),", cs);
        Assert.Contains("private void OnStatusMoreCopyButtonRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)", cs);
        Assert.Contains("OnStatusMoreCopyButtonClick(sender, new RoutedEventArgs());", cs);
    }

    // ---- Suite Hub Audit Count Wording ----

    [Theory]
    [InlineData(SuiteHub, "({CountText.Of(result.MissingKeysViolationsCount, \"missing key\")}, {CountText.Of(result.DateFormatViolationsCount, \"date error\")})", "{result.MissingKeysViolationsCount} missing keys")]
    [InlineData(SuiteHub, "Tag Taxonomy: {CountText.Of(result.UniqueTagsCount, \"unique tag\")}", "{result.UniqueTagsCount} unique tags")]
    [InlineData(SuiteHub, "Math Formulas: {CountText.Of(result.TotalFormulasFound, \"formula\")}", "{result.TotalFormulasFound} formulas")]
    [InlineData(SuiteHub, "Table Alignment: {CountText.Of(result.TotalTablesFound, \"table\")}", "{result.TotalTablesFound} tables")]
    [InlineData(SuiteHub, "across {CountText.Of(result.TotalFilesScanned, \"doc\")})", "{result.TotalFilesScanned} docs")]
    [InlineData(SuiteHub, "across {CountText.Of(result.DocumentsWithTasksCount, \"doc\")}", "{result.DocumentsWithTasksCount} docs")]
    [InlineData(SuiteHub, "Cross-Doc Links: {CountText.Of(result.TotalLinksAudited, \"link\")}", "{result.TotalLinksAudited} links")]
    [InlineData(SuiteHub, "{CountText.Of(result.MissingFileCount, \"missing file\")}", "{result.MissingFileCount} missing files")]
    [InlineData(SuiteHub, "{CountText.Of(result.MissingAnchorCount, \"missing anchor\")}", "{result.MissingAnchorCount} missing anchors")]
    public void SuiteHubCounts_ReadRightAtOne(string file, string expected, string old)
    {
        var src = Read(file);
        Assert.Contains(expected, src);
        Assert.DoesNotContain(old, src);
    }
}
