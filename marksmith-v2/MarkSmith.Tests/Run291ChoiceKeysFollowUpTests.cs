using System;
using System.IO;
using Xunit;

namespace MarkSmith.Tests;

// Run #291 (follow-up to run #290's studio choice keys):
// - Version History: S, F2 and Delete on the Unified / Side by side / Preview switch no longer star,
//   rename or delete the selected version behind it;
// - SmartArt Studio: Ctrl+D on a layout or colour scheme no longer duplicates the selected outline
//   item, and Ctrl+C on a layout copies the layout's name (it copied the unrelated outline item);
// - SmartArt Studio: the layout menu offers "Copy layout name" and names its shortcuts (Enter,
//   Ctrl+C, Ctrl+Shift+C), and Shift+F10 opens it on the focused row instead of the list's top;
// - OCR studio: a closed combo box keeps its arrows / Home / End instead of turning the page or
//   moving to another element;
// - Diagram Studio: Ctrl+D and Ctrl+[ / Ctrl+] in the palette, pills or a combo box leave the
//   canvas selection alone.
public class Run291ChoiceKeysFollowUpTests
{
    private const string History = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml.cs";
    private const string SmartArt = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string Ocr = "MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml.cs";
    private const string Diagram = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml.cs";

    private static string RepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoDir(), relative)).Replace("\r\n", "\n");

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"missing: {start}");
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"missing: {end}");
        return text.Substring(a, b - a);
    }

    // ---- Version History ----

    [Fact]
    public void History_StarRenameDelete_SkipTheModeSwitch()
    {
        var keys = Between(Read(History), "private void OnRootKeyDown", "private void OnSearchBoxKeyDown");
        Assert.Contains("bool inChoice = ChoiceKeys.FocusIsInChoiceControl(RootGrid.XamlRoot);", keys);
        Assert.Contains("!inTextBox && !inFileList && !inChoice && !isCtrl && e.Key == VirtualKey.S", keys);
        Assert.Contains("!inTextBox && !inFileList && !inChoice && e.Key == VirtualKey.F2", keys);
        Assert.Contains("!inTextBox && !inFileList && !inChoice && e.Key == VirtualKey.Delete", keys);
    }

    [Fact]
    public void History_ModeSwitchIsAnArrowNavigatedGroup_SoChoiceKeysSeesIt()
    {
        var xaml = Read("MarkSmith.Desktop/Views/History/HistoryWindow.xaml").Replace("\r\n", "\n");
        int seg = xaml.IndexOf("x:Name=\"DiffModeSegment\"", StringComparison.Ordinal);
        Assert.True(seg > 0);
        var tag = xaml.Substring(seg, xaml.IndexOf('>', seg) - seg);
        Assert.Contains("XYFocusKeyboardNavigation=\"Enabled\"", tag);
    }

    [Fact]
    public void History_VersionStepping_StillOwnsUpDown()
    {
        // Up/Down from the switch still step versions (run #288's documented behaviour).
        var preview = Between(Read(History), "private void OnRootPreviewKeyDown", "private void OnRootKeyDown");
        Assert.DoesNotContain("ChoiceKeys", preview);
    }

    // ---- SmartArt Studio ----

    [Fact]
    public void SmartArt_CtrlCOnALayout_CopiesItsName_BeforeTheOutlineCopy()
    {
        var keys = Between(Read(SmartArt), "private void OnRootGridKeyDown", "private StudioLayoutItem? FocusedLayoutItem()");
        int choice = keys.IndexOf("if (ctrl && e.Key == VirtualKey.C && inChoice)", StringComparison.Ordinal);
        int outline = keys.IndexOf("ViewModel.SelectedNode is { } selected", StringComparison.Ordinal);
        Assert.True(choice > 0 && outline > choice, "the gallery's Ctrl+C must be checked before the outline copy");
        Assert.Contains("FocusedLayoutItem() is { } layout", keys);
        Assert.Contains("Copied layout name", keys);
    }

    [Fact]
    public void SmartArt_DuplicateAndDelete_SkipChoiceControls()
    {
        var keys = Between(Read(SmartArt), "private void OnRootGridKeyDown", "private StudioLayoutItem? FocusedLayoutItem()");
        Assert.Contains("bool inChoice = ChoiceKeys.FocusIsInChoiceControl(RootGrid.XamlRoot);", keys);
        Assert.Contains("ctrl && e.Key == VirtualKey.D && !inChoice", keys);
        Assert.Contains("(e.Key is VirtualKey.Delete or VirtualKey.Back) && !inChoice", keys);
        // Computed once; the old inline call is gone.
        Assert.Equal(1, Count(keys, "ChoiceKeys.FocusIsInChoiceControl("));
    }

    [Fact]
    public void SmartArt_FocusedLayoutItem_StopsAtTheRootGrid()
    {
        var helper = Between(Read(SmartArt), "private StudioLayoutItem? FocusedLayoutItem()", "private void OnMarkdownTextBoxDoubleTapped");
        Assert.Contains("ListViewItem { Content: StudioLayoutItem item }", helper);
        Assert.Contains("ReferenceEquals(cur, RootGrid)", helper);
    }

    [Fact]
    public void SmartArt_LayoutMenu_NamesItsShortcuts_AndOffersCopyLayoutName()
    {
        var menu = Between(Read(SmartArt), "private void ShowLayoutContextMenu", "private void OnPreviewPaneRightTapped");
        Assert.Contains("Text = \"Copy layout name\"", menu);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+C\"", menu);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+Shift+C\"", menu);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Enter\"", menu);
        int name = menu.IndexOf("Copy layout name", StringComparison.Ordinal);
        int block = menu.IndexOf("Copy SmartArt Markdown block", StringComparison.Ordinal);
        Assert.True(name > 0 && block > name);
    }

    [Fact]
    public void SmartArt_ShiftF10InTheGallery_OpensOnTheFocusedRow()
    {
        var keys = Between(Read(SmartArt), "private void OnLayoutListKeyDown", "private void OnLayoutTilePointerEntered");
        Assert.Contains("(FocusedLayoutItem() ?? ViewModel.SelectedLayout) is { } item", keys);
        Assert.Contains("LayoutList.ContainerFromItem(item) is ListViewItem row", keys);
        Assert.Contains("ShowLayoutContextMenu(item, row,", keys);
    }

    // ---- OCR studio ----

    [Fact]
    public void Ocr_ClosedComboKeepsItsArrows_PageAndElementKeysComeAfter()
    {
        var keys = Between(Read(Ocr), "private void OnRootKeyDown", "catch (Exception ex)");
        int guard = keys.IndexOf("if (ChoiceKeys.FocusIsInChoiceControl(RootGrid.XamlRoot)) return;", StringComparison.Ordinal);
        int modifiers = keys.IndexOf("if (ctrl || alt) return;", StringComparison.Ordinal);
        int left = keys.IndexOf("e.Key == Windows.System.VirtualKey.Left", StringComparison.Ordinal);
        int contextMenu = keys.IndexOf("KeyboardContextMenu.IsContextMenuKey(e.Key)", StringComparison.Ordinal);
        Assert.True(guard > modifiers && left > guard, "the choice check must sit before the arrow / page keys");
        Assert.True(contextMenu < guard, "Shift+F10 and Ctrl+C must still work from a combo box");
    }

    // ---- Diagram Studio ----

    [Fact]
    public void Diagram_Duplicate_SkipsChoiceControls()
    {
        var dup = Between(Read(Diagram), "private void OnDuplicateAcceleratorInvoked", "private void OnCopyAcceleratorInvoked");
        int guard = dup.IndexOf("ChoiceKeys.FocusIsInChoiceControl(XamlRoot, CanvasContainer)) return;", StringComparison.Ordinal);
        int act = dup.IndexOf("ViewModel?.DuplicateSelected();", StringComparison.Ordinal);
        Assert.True(guard > 0 && act > guard);
    }

    [Fact]
    public void Diagram_ZOrderKeys_SkipChoiceControls()
    {
        var src = Read(Diagram);
        var zorder = Between(src, "z-ordering shortcuts", "DataContextChanged +=");
        int guard = zorder.IndexOf("ChoiceKeys.FocusIsInChoiceControl(XamlRoot, CanvasContainer)) return;", StringComparison.Ordinal);
        int act = zorder.IndexOf("_canvas.BringToFront();", StringComparison.Ordinal);
        Assert.True(guard > 0 && act > guard);
    }

    private static int Count(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
