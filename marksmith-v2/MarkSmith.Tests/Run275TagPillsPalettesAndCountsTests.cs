using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using MarkSmith.Services.MindMap;
using MarkSmith.ViewModels.MindMap;
using Xunit;

namespace MarkSmith.Tests;

// Run #275: tag pills, palettes and counts.
// - Document Galaxy: a tag pill's "Add/Remove '<tag>' … selected node" went straight to the node's
//   Tags collection, so the pills, their counts, the filter and undo never heard about it; it now
//   goes through TagsText (like the inspector's box), clears a filter on a tag no node carries any
//   more, and puts focus back on the rebuilt pill (or "All tags");
// - Document Galaxy: the pill tooltip set from code dropped the XAML's Shift+F10 hint;
// - SmartArt Studio: harmony swatches were tapped Borders (no keyboard, no name); they are buttons
//   now, and the custom accent's status line follows the theme and is announced;
// - Shape Studio: snap guides, crosshair, cursor HUD and the snap grid follow the studio's theme;
// - Counts read right at one: frontmatter "properties", spreadsheet imports, the section
//   breadcrumb, the OCR overlay and the Hub's path audit.
public class Run275TagPillsPalettesAndCountsTests
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

    private const string Galaxy = "Views/MindMap/MindMapGalaxyWindow.xaml.cs";
    private const string SmartArt = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string ShapeStudio = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";

    // ---- Document Galaxy tag pills ----

    [Fact]
    public void TagPillMenu_EditsTagsThroughTagsText()
    {
        var body = Body(Read(Galaxy), "private void OnDynamicTagPillRightTapped(");
        Assert.DoesNotContain("selNode.Tags.Add(tag)", body);
        Assert.DoesNotContain("selNode.Tags.Remove(tag)", body);
        Assert.Contains("selNode.TagsText = string.Join(\" \", selNode.Tags.Append(tag));", body);
        Assert.Contains("selNode.TagsText = string.Join(\" \", selNode.Tags.Where(t => !MindMapGraph.TagEquals(t, tag)));", body);
        Assert.Contains("Ctrl+Z to undo", body);
    }

    [Fact]
    public void TagPillMenu_RemovingLastCarrierClearsItsFilterAndRefocuses()
    {
        var body = Body(Read(Galaxy), "private void OnDynamicTagPillRightTapped(");
        Assert.Contains("var pillFocus = btn.FocusState;", body);
        Assert.Contains("ViewModel.SelectedTagFilter = null;", body);
        Assert.Contains("(no nodes carry it now)", body);
        Assert.Equal(2, Regex.Matches(body, @"RefocusTagPill\(btn, tag, pillFocus\);").Count);
    }

    [Fact]
    public void RefocusTagPill_LandsOnRebuiltPillElseAllTags()
    {
        var body = Body(Read(Galaxy), "private void RefocusTagPill(");
        // Right-click on an unfocused pill leaves focus where it was.
        Assert.Contains("if (pillFocus == FocusState.Unfocused) return;", body);
        Assert.Contains("FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard", body);
        Assert.Contains("DispatcherQueuePriority.Low", body);
        Assert.Contains("b != oldPill", body);
        Assert.Contains("?? AllTagsPill;", body);
    }

    [Fact]
    public void TagPillTooltip_KeepsShiftF10Hint()
    {
        var body = Body(Read(Galaxy), "private void UpdateTagPills()");
        Assert.Contains("\"Active filter · Click or Esc to clear · Right-click or Shift+F10 for options\"", body);
        Assert.Contains("\"Filter nodes by this tag · Right-click or Shift+F10 for options\"", body);
        Assert.DoesNotContain("\"Filter nodes by this tag\")", body);
    }

    [Fact]
    public void TagsText_RefreshesDistinctTagsFilterAndUndo()
    {
        var vm = new MindMapStudioViewModel();
        var node = vm.AddNodeAt(100, 100, announce: false);
        node.TagsText = "";
        Assert.DoesNotContain(vm.DistinctTags, t => MindMapGraph.TagEquals(t, "zeta"));

        // What the pill menu's "Add" now does.
        node.TagsText = string.Join(" ", node.Tags.Append("zeta"));
        Assert.Contains(vm.DistinctTags, t => MindMapGraph.TagEquals(t, "zeta"));
        Assert.True(vm.CanUndo);

        // And "Remove", case-insensitively.
        node.TagsText = string.Join(" ", node.Tags.Where(t => !MindMapGraph.TagEquals(t, "ZETA")));
        Assert.DoesNotContain(node.Tags, t => MindMapGraph.TagEquals(t, "zeta"));
        Assert.DoesNotContain(vm.DistinctTags, t => MindMapGraph.TagEquals(t, "zeta"));
    }

    // ---- SmartArt Studio palette flyout ----

    [Fact]
    public void HarmonySwatches_AreNamedButtons()
    {
        var body = Body(Read(SmartArt), "private void GenerateHarmonySwatches(string mode)");
        Assert.Contains("var swatch = new Button", body);
        Assert.DoesNotContain("swatch.Tapped +=", body);
        Assert.Contains("swatch.Click +=", body);
        Assert.Contains("AutomationProperties.SetName(swatch, $\"Apply harmony colour #{hex.ToUpperInvariant()}\")", body);
        Assert.Contains("HoverPolish.Apply(HarmonySwatchesPanel);", body);
    }

    [Fact]
    public void CustomPaletteStatus_FollowsThemeAndIsAnnounced()
    {
        var code = Read(SmartArt);
        var helper = Body(code, "private void SetCustomPaletteStatus(string text, string brushKey)");
        Assert.Contains("ThemeBrush.Set(CustomPaletteStatusText, TextBlock.ForegroundProperty, brushKey);", helper);
        Assert.DoesNotContain("CustomPaletteStatusText.Foreground = (Brush)Application.Current.Resources", code);
        Assert.DoesNotContain("(Brush)Application.Current.Resources[\"AccentFillColorDefaultBrush\"]", code);

        var xaml = Read("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml");
        var status = Regex.Match(xaml, "<TextBlock x:Name=\"CustomPaletteStatusText\"[^>]*>").Value;
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", status);
        Assert.Contains("AutomationProperties.Name=\"Apply custom accent\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Reset custom accent\"", xaml);

        // A swatch fills the hex box after applying; the box's TextChanged keeps "Applied".
        var changed = Body(code, "private void OnCustomPaletteHexBoxTextChanged(");
        Assert.Contains("string.Equals(hex, _customHexOverride, StringComparison.OrdinalIgnoreCase)", changed);
        Assert.Contains("$\"Applied #{_customHexOverride}\"", changed);
    }

    [Fact]
    public void ApplyCustomPalette_InvalidHexSaysWhyAndReturnsToTheBox()
    {
        var body = Body(Read(SmartArt), "private void OnApplyCustomPaletteClick(");
        Assert.Contains("Type a hex color first, e.g. #0078D4", body);
        Assert.Contains("Invalid hex color: use 6 hex characters (#RRGGBB)", body);
        Assert.Contains("CustomPaletteHexBox?.Focus(FocusState.Programmatic);", body);
    }

    // ---- Shape Studio canvas adornments ----

    [Fact]
    public void ShapeStudioAdornments_FollowTheme()
    {
        var code = Read(ShapeStudio);
        Assert.DoesNotContain("Application.Current.Resources[\"AccentFillColorDefaultBrush\"]", code);
        Assert.DoesNotContain("Application.Current.Resources[\"CardStrokeColorDefaultBrush\"]", code);
        var guides = Body(code, "private void EnsureSnapGuides()");
        foreach (var name in new[] { "_snapGuideX", "_snapGuideY", "_crosshairLineX", "_crosshairLineY" })
            Assert.Contains($"ThemeBrush.Set({name}, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, \"AccentFillColorDefaultBrush\");", guides);
        Assert.Contains("ThemeBrush.Set(_cursorHudBadge, Border.BorderBrushProperty, \"AccentFillColorDefaultBrush\");", guides);
        Assert.Contains("ThemeBrush.For(GridOverlayCanvas, \"CardStrokeColorDefaultBrush\")", Body(code, "private void UpdateVisualGridOverlay()"));
        Assert.Matches(@"ActualThemeChanged \+= \(_, _\) =>\s*\{[^}]*UpdateVisualGridOverlay\(\);", code);
    }

    // ---- Counts ----

    [Fact]
    public void Counts_UseCountText()
    {
        var main = Read("MainWindow.xaml.cs");
        Assert.DoesNotContain("\"property\")", main);
        Assert.Equal(3, Regex.Matches(main, Regex.Escape("\"property\", \"properties\")")).Count);
        Assert.DoesNotContain("columns from {", main);
        Assert.Contains("{CountText.Of(activeSection.WordCount, \"word\")}", main);
        Assert.Contains("Copied {CountText.Of(text.Length, \"character\")} of Markdown source.", main);

        var ocr = Read("Controls/OcrExperienceOverlay.xaml.cs");
        Assert.DoesNotContain("{blockCount} blocks pieced", ocr);
        Assert.DoesNotContain("Recognising all {wordCount} words", ocr);

        Assert.Contains("across {CountText.Of(audit.TotalFiles, \"file\")}", Read("Views/SuiteHubView.xaml.cs"));
    }

    [Theory]
    [InlineData(1, "property", "properties", "1 property")]
    [InlineData(2, "property", "properties", "2 properties")]
    [InlineData(1, "column", null, "1 column")]
    [InlineData(4, "column", null, "4 columns")]
    [InlineData(1, "file", null, "1 file")]
    public void CountText_ReadsRight(int n, string singular, string? plural, string expected) =>
        Assert.Equal(expected, CountText.Of(n, singular, plural));
}
