using System;
using System.IO;
using Xunit;

namespace MarkSmith.Tests;

// Run #255, found with the clip sweep plus PrintWindow screenshots of every studio at its minimum size.
// - SmartArt Studio: the Preview header's layout name trimmed to "O…" beside eight tool buttons.
// - SmartArt Studio: the colour palette button showed a plain solid dot (EA3B), the background button
//   borrowed the palette glyph, and node geometry used a picture glyph.
// - SmartArt Studio: the quick-layout strip laid each name over the lower half of its miniature.
// - Diagram Studio: the 200×150 minimap covered the sample's decision node on a small canvas.
// - OCR Studio: the solid "#N Kind" badges sat on the text they label, hiding headings on the page.
// - Document Galaxy: the Icon box drew its glyph about 3 px above centre, and the icon picker button
//   used the colour palette glyph.
// - Version History: the search placeholder was cut mid-phrase ("Search labels, kinds,").
public class Run255StudioHeadersAndGlyphsTests
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

    private const string SmartArtXaml = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml";
    private const string SmartArtCode = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string CanvasCode = "Views/Mermaid/MermaidCanvasControl.xaml.cs";
    private const string OcrCode = "Views/Ocr/OcrInspectionStudioWindow.xaml.cs";
    private const string GalaxyXaml = "Views/MindMap/MindMapGalaxyWindow.xaml";
    private const string HistoryXaml = "Views/History/HistoryWindow.xaml";

    // The opening tag of the element named `name`.
    private static string Tag(string xaml, string name)
    {
        var at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{name} not found");
        var start = xaml.LastIndexOf('<', at);
        var end = xaml.IndexOf('>', at);
        return xaml[start..(end + 1)];
    }

    // From the element named `name` to the next element named `next`.
    private static string Between(string xaml, string name, string next)
    {
        var at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{name} not found");
        var end = xaml.IndexOf($"x:Name=\"{next}\"", at, StringComparison.Ordinal);
        Assert.True(end > at, $"{next} not found after {name}");
        return xaml[at..end];
    }

    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found");
        var next = code.IndexOf("\n        private ", start + signature.Length, StringComparison.Ordinal);
        var next4 = code.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        if (next < 0 || (next4 >= 0 && next4 < next)) next = next4;
        return next < 0 ? code[start..] : code[start..next];
    }

    [Fact]
    public void SmartArtPreviewHeader_HasASecondRowForTheTools()
    {
        var xaml = Read(SmartArtXaml);
        var header = Tag(xaml, "PreviewHeaderGrid");
        Assert.Contains("SizeChanged=\"OnPreviewHeaderSizeChanged\"", header);
        var body = Between(xaml, "PreviewHeaderGrid", "PreviewHeaderLabel");
        Assert.Contains("<Grid.RowDefinitions>", body);
        Assert.Contains("x:Name=\"PreviewHeaderTools\"", Tag(xaml, "PreviewHeaderTools"));
        // The tools still start with the palette button and the title still trims as a last resort.
        Assert.Contains("x:Name=\"PaletteButton\"", Between(xaml, "PreviewHeaderTools", "CopyOutlineMarkdownButton"));
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", Tag(xaml, "PreviewTitleLabel"));
    }

    [Fact]
    public void SmartArtPreviewHeader_StacksOnlyWhenTheTitleWouldBeSqueezed()
    {
        var code = Read(SmartArtCode);
        var method = Method(code, "private void UpdatePreviewHeaderLayout()");
        Assert.Contains("PreviewHeaderGrid.ActualWidth <= 0) return;", method);
        // Measures a detached probe, never the live label (an out-of-band Measure leaves stale sizes).
        Assert.Contains("var probe = new TextBlock", method);
        Assert.DoesNotContain("PreviewTitleLabel.Measure(", code);
        Assert.Contains("room < Math.Min(titleNeeds, PreviewTitleMinimumRoom)", method);
        Assert.Contains("Grid.SetRow(PreviewHeaderTools, stacked ? 1 : 0);", method);
        Assert.Contains("Grid.SetColumn(PreviewHeaderTools, stacked ? 0 : 2);", method);
        Assert.Contains("Grid.SetColumnSpan(PreviewHeaderTools, stacked ? 3 : 1);", method);
        // No row gap while the second row is empty.
        Assert.Contains("PreviewHeaderGrid.RowSpacing = stacked ? 4 : 0;", method);
        Assert.Contains("private const double PreviewTitleMinimumRoom = 120;", code);
        // A new layout name re-runs the check even when the header's size doesn't change.
        Assert.Contains("PreviewTitleLabel.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdatePreviewHeaderLayout());", code);
    }

    [Theory]
    [InlineData("PaletteButton", "&#xE790;")]   // colour palette
    [InlineData("DiagramBgButton", "&#xE81E;")] // layers (the canvas backdrop)
    [InlineData("NodeGeometryButton", "&#xE7FB;")] // rounded box (node shape)
    public void SmartArtPreviewTools_UseGlyphsThatMatchTheirJob(string button, string glyph)
    {
        var xaml = Read(SmartArtXaml);
        var at = xaml.IndexOf($"x:Name=\"{button}\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var icon = xaml.IndexOf("<FontIcon Glyph=\"", at, StringComparison.Ordinal);
        Assert.StartsWith($"<FontIcon Glyph=\"{glyph}\"", xaml[icon..]);
    }

    [Fact]
    public void SmartArtPreviewTools_NoLongerUseTheSolidDotOrPictureGlyph()
    {
        var xaml = Read(SmartArtXaml);
        var tools = Between(xaml, "PreviewHeaderTools", "ZoomButton");
        Assert.DoesNotContain("<FontIcon Glyph=\"&#xEA3B;\" FontSize=\"12\" />", tools);
        Assert.DoesNotContain("<FontIcon Glyph=\"&#xEB9F;\" FontSize=\"12\" />", tools);
        // The background and palette buttons no longer share a glyph.
        Assert.Single(tools.Split("<FontIcon Glyph=\"&#xE790;\" FontSize=\"12\" />")[1..]);
        // The right-click submenu header matches the button.
        var code = Read(SmartArtCode);
        var sub = Method(code, "private void AddPaletteSubmenu(MenuFlyout menu)");
        var header = sub[..sub.IndexOf("foreach", StringComparison.Ordinal)];
        Assert.Contains("Glyph = \"\\uE790\"", header);
    }

    [Fact]
    public void SmartArtQuickLayoutStrip_PutsTheNameUnderTheMiniature()
    {
        var xaml = Read(SmartArtXaml);
        var start = xaml.IndexOf("x:Name=\"QuickLayoutCarouselList\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("</ListView>", start, StringComparison.Ordinal);
        var template = xaml[start..end];
        Assert.Contains("<RowDefinition Height=\"38\" />", template);
        Assert.Contains("<TextBlock Grid.Row=\"1\" Text=\"{Binding DisplayName}\"", template);
        // No overlay pinned to the tile's bottom edge any more.
        Assert.DoesNotContain("VerticalAlignment=\"Bottom\"", template);
        Assert.DoesNotContain("AcrylicBackgroundFillColorDefaultBrush", template);
        // Still named and tooltipped for UIA and hover.
        Assert.Contains("AutomationProperties.Name=\"{Binding DisplayName}\"", template);
        Assert.Contains("ToolTipService.ToolTip=\"{Binding DisplayName}\"", template);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", template);
    }

    [Fact]
    public void DiagramMinimap_StepsAsideOnASmallCanvas()
    {
        var code = Read(CanvasCode);
        Assert.Contains("CanvasScrollViewer.SizeChanged += (_, _) => UpdateMinimapVisibility();", code);
        Assert.Contains("internal const double MinimapMinViewportWidth = 600;", code);
        Assert.Contains("internal const double MinimapMinViewportHeight = 420;", code);
        var method = Method(code, "private void UpdateMinimapVisibility()");
        Assert.Contains("CanvasScrollViewer.ActualWidth >= MinimapMinViewportWidth", method);
        Assert.Contains("CanvasScrollViewer.ActualHeight >= MinimapMinViewportHeight", method);
        Assert.Contains("if (MinimapControl.Visibility == want) return;", method);
        // Coming back it redraws against the current viewport.
        Assert.Contains("if (fits) MinimapControl.Refresh();", method);
        // Fit-to-content already reads the minimap's visibility, so a hidden map frees the bottom band.
        Assert.Contains("MinimapControl.Visibility == Visibility.Visible ? MinimapControl.ActualHeight + 20 : 0", code);
    }

    [Fact]
    public void DiagramMinimap_ThresholdsFitTheDefaultButNotTheMinimumCanvas()
    {
        // Default window 1380×860 leaves a ~820×735 canvas; the 1080×600 minimum leaves ~524×475.
        const double defaultW = 820, defaultH = 735, minW = 524, minH = 475;
        Assert.True(defaultW >= 600 && defaultH >= 420);
        Assert.False(minW >= 600 && minH >= 420);
        // The minimap takes no focus, so collapsing it cannot strand the keyboard.
        var minimap = Read("Views/Mermaid/MermaidMinimapControl.cs");
        Assert.DoesNotContain("IsTabStop = true", minimap);
        Assert.DoesNotContain(".Focus(", minimap);
    }

    [Fact]
    public void OcrCanvasBadges_AreSeeThroughAtRest()
    {
        var code = Read(OcrCode);
        Assert.Contains("internal const double RestingBadgeOpacity = 0.45;", code);
        Assert.Contains("if (frame.Child is Border badge) badge.Opacity = strong ? 1.0 : RestingBadgeOpacity;", code);
        var create = Method(code, "private Border CreateCanvasElementFrame(OcrInspectionElement elem)");
        Assert.Contains("Opacity = ReferenceEquals(elem, _selectedElement) ? 1.0 : RestingBadgeOpacity,", create);
        // Hover brings it up, leaving drops it back unless selected.
        Assert.Contains("SetBadgeEmphasis(border, true);", create);
        Assert.Contains("SetBadgeEmphasis(border, isSel);", create);
    }

    [Fact]
    public void OcrCanvasBadges_FollowSelectionCardHoverAndReclassify()
    {
        var code = Read(OcrCode);
        var select = Method(code, "private void SelectElement(OcrInspectionElement elem)");
        Assert.Contains("SetBadgeEmphasis(frame, isTarget);", select);
        var card = Method(code, "private Border CreateInspectorCard(OcrInspectionElement elem)");
        Assert.Contains("SetBadgeEmphasis(frame, true);", card);
        Assert.Contains("SetBadgeEmphasis(frame, isSel);", card);
        var reclassify = Method(code, "private void ReclassifyElement(");
        Assert.Contains("badge.Opacity = isSel ? 1.0 : RestingBadgeOpacity;", reclassify);
    }

    [Fact]
    public void GalaxyIconBox_CentresItsGlyph_AndThePickerShowsAnEmojiFace()
    {
        var xaml = Read(GalaxyXaml);
        Assert.Contains("Padding=\"10,8,6,3\"", Tag(xaml, "NodeIconBox"));
        var at = xaml.IndexOf("x:Name=\"NodeIconPickerButton\"", StringComparison.Ordinal);
        var icon = xaml.IndexOf("<FontIcon Glyph=\"", at, StringComparison.Ordinal);
        Assert.StartsWith("<FontIcon Glyph=\"&#xE76E;\"", xaml[icon..]);
        Assert.Contains("AutomationProperties.Name=\"Pick node icon\"", Tag(xaml, "NodeIconPickerButton"));
    }

    [Fact]
    public void HistorySearch_PlaceholderFitsAndTheShortcutMovedToTheTooltip()
    {
        var box = Tag(Read(HistoryXaml), "SearchBox");
        Assert.Contains("PlaceholderText=\"Search versions\"", box);
        Assert.Contains("ToolTipService.ToolTip=\"Search versions (Ctrl+F) · ", box);
        Assert.Contains("AutomationProperties.Name=\"Search versions\"", box);
        Assert.Contains("MinWidth=\"160\"", box);
    }
}
