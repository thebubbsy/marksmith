using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #273 (part 2): controls that remove or hide themselves keep keyboard focus in their window,
// and the Shape Studio colour flyout's recent swatches are as reachable from the keyboard as with
// a mouse.
// - Shape Studio fill flyout: recent swatches open their menu from Shift+F10, "Remove from
//   recent" lands on the neighbouring swatch, Clear hands focus to the colour picker, and the
//   flyout's swatches get the hover lift (the popup is outside the window's tracked tree);
// - Mermaid Studio: Delete selection disables itself after a keyboard press; focus goes to the canvas;
// - Document Galaxy: the tag filter's Clear button hides itself; focus goes to "All tags";
// - Version History: deleting a document's last version focuses the document, else the search box;
// - SmartArt Studio / Shape Studio: an empty outline no longer copies "0 lines"; counts read right.
public class Run273StudioFlyoutsAndDeletesKeepFocusTests
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
            @"\n {" + indent + @"}(?! )(private|public|internal|protected|//|\[)");
        return m.Success ? source.Substring(start, signature.Length + m.Index) : source.Substring(start);
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ");

    private const string ShapeStudio = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";

    // ---- Shape Studio fill flyout ----

    [Fact]
    public void RecentSwatch_MenuOpensFromShiftF10()
    {
        var body = Body(Read(ShapeStudio), "private void RebuildRecentColorsSwatches(string activeHex)");
        Assert.Contains("RightTappedEventHandler openMenu = (s, e) =>", body);
        Assert.Contains("btn.RightTapped += openMenu;", body);
        Assert.Contains("KeyboardContextMenu.Bridge(btn, openMenu);", body);
        // Keyboard requests anchor to the swatch (there is no pointer position).
        Assert.Contains("KeyboardContextMenu.ShowAt(menu, btn, e);", body);
        Assert.DoesNotContain("menu.ShowAt(btn, e.GetPosition(btn))", body);
        Assert.Contains("Right-click or Shift+F10 for options", body);
    }

    [Fact]
    public void RecentSwatch_RemoveLandsOnTheNeighbouringSwatch()
    {
        var src = Read(ShapeStudio);
        var body = Squash(Body(src, "private void RebuildRecentColorsSwatches(string activeHex)"));
        Assert.Contains("int capturedIndex = RecentColorsSwatches.Children.Count;", body);
        Assert.Contains("FocusRecentSwatchOrPicker(capturedIndex, how)", body);
        Assert.Contains("Removed #{capturedHex} from recent colours.", body);

        var helper = Squash(Body(src, "private void FocusRecentSwatchOrPicker(int index, FocusState how)"));
        Assert.Contains("!FillFlyout.IsOpen) return;", helper);
        Assert.Contains("Math.Clamp(index, 0, swatches.Count - 1)", helper);
        Assert.Contains("FillPicker.Focus(how);", helper);
        Assert.Contains("KeyboardContextMenu.IsKeyboardInvocation ? FocusState.Keyboard : FocusState.Programmatic", body);
    }

    [Fact]
    public void ClearRecentColours_MovesFocusBeforeTheRowCollapses()
    {
        var body = Body(Read(ShapeStudio), "private void OnClearRecentColorsClick(object sender, RoutedEventArgs e)");
        int focus = body.IndexOf("FillPicker.Focus(", StringComparison.Ordinal);
        int clear = body.IndexOf("_recentColors.Clear();", StringComparison.Ordinal);
        Assert.True(focus > 0 && clear > focus, "focus must move before the Recent colours row collapses");
        Assert.Contains("ClearRecentColorsBtn.FocusState != FocusState.Unfocused", body);
    }

    [Fact]
    public void FillFlyoutSwatches_GetHoverPolish()
    {
        var src = Read(ShapeStudio);
        Assert.Contains("HoverPolish.Apply(RecentColorsSwatches);", Body(src, "private void RebuildRecentColorsSwatches(string activeHex)"));
        Assert.Contains("HoverPolish.Apply(FillFlyoutSwatches);", Body(src, "private void RebuildFillFlyoutSwatches(string activeHex)"));
    }

    [Fact]
    public void ShapeStudio_CountsReadRightAtOne()
    {
        var src = Read(ShapeStudio);
        Assert.Contains("Selected all {CountText.Of(ViewModel.Shapes.Count, \"shape\")}", src);
        Assert.Contains("({CountText.Of(ViewModel.FilteredPresets.Count, \"preset\")})", src);
        Assert.DoesNotContain("{ViewModel.Shapes.Count} shapes", src);
        Assert.DoesNotContain("{ViewModel.FilteredPresets.Count} presets", src);
    }

    // ---- Mermaid Studio ----

    [Fact]
    public void MermaidDeleteSelection_FallsBackToTheCanvas()
    {
        var studio = Read("Views/Mermaid/MermaidDiagramStudioControl.xaml.cs");
        Assert.Contains("FocusRescue.Attach(() => _canvas.FocusTarget, DeleteSelectedButton);", studio);
        // The rescue is wired after the canvas exists.
        Assert.True(studio.IndexOf("_canvas = new MermaidCanvasControl();", StringComparison.Ordinal)
                    < studio.IndexOf("FocusRescue.Attach(() => _canvas.FocusTarget", StringComparison.Ordinal));

        var canvas = Read("Views/Mermaid/MermaidCanvasControl.xaml.cs");
        Assert.Contains("internal UIElement FocusTarget => CanvasScrollViewer;", canvas);
    }

    // ---- Document Galaxy ----

    [Fact]
    public void GalaxyTagFilterClear_HandsFocusToAllTags()
    {
        var body = Squash(Body(Read("Views/MindMap/MindMapGalaxyWindow.xaml.cs"),
            "private void OnTagFilterClearClick(object sender, RoutedEventArgs e)"));
        Assert.Contains("var state = TagFilterClearButton.FocusState;", body);
        Assert.Contains("if (state != FocusState.Unfocused) AllTagsPill?.Focus(", body);
        Assert.Contains("FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard", body);
    }

    [Fact]
    public void GalaxyTagPills_NameAndKeyHint()
    {
        var xaml = Read("Views/MindMap/MindMapGalaxyWindow.xaml");
        Assert.Contains("AutomationProperties.Name=\"Clear tag filter\"", xaml);
        Assert.Contains("Filter nodes by tag · Right-click or Shift+F10 for options", xaml);
        Assert.DoesNotContain("Filter nodes by tag · Right-click for options\"", xaml);
    }

    // ---- Version History ----

    [Fact]
    public void HistoryDelete_LastVersionFocusesTheDocument()
    {
        var src = Read("Views/History/HistoryWindow.xaml.cs");
        var delete = Body(src, "private async Task DeleteAsync(VersionItemViewModel item)");
        Assert.Contains("DispatcherQueuePriority.Low, FocusAfterDelete);", delete);

        var helper = Squash(Body(src, "private void FocusAfterDelete()"));
        Assert.Contains("if (_vm.Selected is not null) { FocusSelectedVersionButton(); return; }", helper);
        Assert.Contains("FindVersionButton(FilesScroll, file)", helper);
        Assert.Contains("SearchBox.Focus(FocusState.Programmatic);", helper);
    }

    // ---- SmartArt Studio ----

    [Fact]
    public void SmartArtPlainOutlineCopy_EmptyOutlineSaysSo()
    {
        var body = Body(Read("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs"),
            "private void OnCopyPlainOutlineTextClick(object sender, RoutedEventArgs e)");
        int guard = body.IndexOf("ViewModel.OutlineRows.Count == 0", StringComparison.Ordinal);
        int copy = body.IndexOf("CopyAndReport(", StringComparison.Ordinal);
        Assert.True(guard > 0 && copy > guard, "the empty guard must run before anything is copied");
        Assert.Contains("The outline is empty: nothing to copy.", body);
        Assert.Contains("CountText.Of(plainLines.Count, \"line\")", body);
    }

    [Fact]
    public void SmartArtOutlineMetrics_CountsReadRightAtOne()
    {
        var src = Read("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs");
        Assert.Contains("{CountText.Of(totalWords, \"word\")}, {CountText.Of(totalChars, \"character\")}, {CountText.Of(ViewModel.OutlineRows.Count, \"node\")}", src);
        Assert.Contains("Outline hierarchy: {CountText.Of(count, \"node\")}", src);
        Assert.DoesNotContain("Outline hierarchy: {count} nodes", src);
    }

    [Theory]
    [InlineData(1, "shape", "1 shape")]
    [InlineData(2, "preset", "2 presets")]
    [InlineData(1, "line", "1 line")]
    [InlineData(1204, "character", "1,204 characters")]
    public void CountText_ReadsAsASentence(int count, string noun, string expected)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(expected, MarkSmith.Services.CountText.Of(count, noun));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }
}
