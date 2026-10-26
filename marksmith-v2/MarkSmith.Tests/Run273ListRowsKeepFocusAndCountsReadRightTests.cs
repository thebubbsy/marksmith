using System;
using System.IO;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #273: list rows that remove themselves keep keyboard focus, outline rows tell a screen reader
// their heading level, and count messages read right at one ("1 heading", not "1 headings").
// - Main window: a ListView's FocusState stays Unfocused while a row holds focus, so emptying the
//   outline (filter) or Recent exports (Delete on the last row) skipped the focus hand-off;
// - Outline flyout rows: UIA level + help text naming their keys; tooltip names Enter / Ctrl+C;
// - SmartArt Studio: a row's own "Delete item" button removed the focused element;
// - Outline copies, Document Galaxy, OCR studio and Mermaid studio count messages use CountText.
public class Run273ListRowsKeepFocusAndCountsReadRightTests
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

    // ---- Focus inside a list ----

    [Fact]
    public void FocusIsWithin_WalksUpFromTheFocusedElement()
    {
        var src = Read("MainWindow.xaml.cs");
        var helper = Squash(Body(src, "private static bool FocusIsWithin(UIElement root)"));
        Assert.Contains("FocusManager.GetFocusedElement(xamlRoot) is DependencyObject", helper);
        Assert.Contains("!PopupKeys.FocusIsOutside(root)", helper);
    }

    [Fact]
    public void OutlineNoMatch_HandsFocusFromARowToTheFilterBox()
    {
        var src = Read("MainWindow.xaml.cs");
        var body = Body(src, "private void UpdateOutlineEmptyState()");
        Assert.DoesNotContain("TocList.FocusState", body);
        int check = body.IndexOf("FocusIsWithin(TocList)", StringComparison.Ordinal);
        Assert.True(check > 0);
        // Focus moves before the list collapses under it.
        Assert.True(check < body.IndexOf("TocList.Visibility = noMatch", StringComparison.Ordinal));
    }

    [Fact]
    public void RecentExports_DeletingTheLastRow_MovesFocusBeforeTheListCollapses()
    {
        var src = Read("MainWindow.xaml.cs");
        var body = Body(src, "private void UpdateExportHistoryFlyoutState()");
        Assert.DoesNotContain("HistoryList.FocusState", body);
        int check = body.IndexOf("FocusIsWithin(HistoryList)", StringComparison.Ordinal);
        Assert.True(check > 0);
        Assert.True(check < body.IndexOf("HistoryList.Visibility =", StringComparison.Ordinal));
        // Delete on a row is the path that empties the list from inside it.
        Assert.Contains("RemoveHistoryEntryFromFlyout(entry);", src);
    }

    [Fact]
    public void SmartArtRowDelete_FocusesTheOutline_OnlyWhenTheButtonHadFocus()
    {
        var src = Read("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs");
        var body = Body(src, "private void OnRowDeleteClick(");
        int state = body.IndexOf("var state = (sender as Control)?.FocusState ?? FocusState.Unfocused;", StringComparison.Ordinal);
        int delete = body.IndexOf("ViewModel.DeleteSelectedCommand.Execute(node);", StringComparison.Ordinal);
        int focus = body.IndexOf("OutlineScroll.Focus(", StringComparison.Ordinal);
        Assert.True(state > 0 && delete > state && focus > delete, "focus state is read before the row goes, focus moves after");
        Assert.Contains("if (state != FocusState.Unfocused)", body);
        // A mouse click gets no focus rectangle.
        Assert.Contains("state == FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard", body);
    }

    // ---- Outline rows for screen readers ----

    [Fact]
    public void OutlineRows_CarryHeadingLevelAndKeys()
    {
        var xaml = Read("MainWindow.xaml");
        Assert.Contains("ContainerContentChanging=\"OnTocListContainerContentChanging\"", xaml);
        Assert.Contains("Click or Enter to jump to heading · Ctrl+C copies it · Right-click or Shift+F10 for options", xaml);
        Assert.DoesNotContain("Right-click to copy heading or link", xaml);

        var src = Read("MainWindow.xaml.cs");
        var body = Body(src, "private void OnTocListContainerContentChanging(");
        Assert.Contains("args.InRecycleQueue", body);
        Assert.Contains("AutomationProperties.SetLevel(args.ItemContainer, Math.Clamp(entry.Level, 1, 6))", body);
        Assert.Contains("AutomationProperties.SetHelpText(args.ItemContainer,", body);
        // The help text names the keys OnTocListKeyDown actually handles.
        var keys = Body(src, "private void OnTocListKeyDown(");
        Assert.Contains("VirtualKey.Enter", keys);
        Assert.Contains("e.Key == Windows.System.VirtualKey.C && ctrl && shift", keys);
    }

    // ---- Counts ----

    [Theory]
    [InlineData("MainWindow.xaml.cs", "$\"Copied {CountText.Of(entries.Count, \"heading\")} as Markdown TOC.\"")]
    [InlineData("MainWindow.xaml.cs", "$\"Copied {CountText.Of(entries.Count, \"heading\")} as plain text.\"")]
    [InlineData("MainWindow.xaml.cs", "$\"Copy Table of Contents ({CountText.Of(count, \"heading\")})\"")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "({CountText.Of(descendants.Count, \"node\")} hidden)")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "({CountText.Of(descendants.Count, \"node\")} revealed)")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "Reversed {CountText.Of(nodeLinks.Count, \"link\")} for")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "MindMap Galaxy: {CountText.Of(ViewModel.Nodes.Count, \"node\")}, {CountText.Of(ViewModel.Links.Count, \"link\")}")]
    [InlineData("Views/Ocr/OcrInspectionStudioWindow.xaml.cs", "WordCountBadgeText.Text = CountText.Of(page.Words.Count, \"word\");")]
    [InlineData("Views/Ocr/OcrInspectionStudioWindow.xaml.cs", "Assembled {CountText.Of(page.Words.Count, \"word\")} into {CountText.Of(page.Elements.Count, \"block\")}.")]
    [InlineData("Views/Ocr/OcrInspectionStudioWindow.xaml.cs", "$\"Element {elem.Id}, {elem.KindDisplayName}, {CountText.Of(elem.WordCount, \"word\")}\"")]
    [InlineData("Views/Mermaid/MermaidDiagramStudioControl.xaml.cs", "{CountText.Of(ViewModel.Nodes.Count, \"node\")}, {CountText.Of(ViewModel.Connectors.Count, \"edge\")}")]
    [InlineData("Views/Mermaid/MermaidMinimapControl.cs", "CountText.Of(vm.Nodes.Count, \"node\")")]
    public void CountMessages_UseCountText(string file, string expected)
    {
        Assert.Contains(expected, Read(file));
    }

    [Theory]
    [InlineData("MainWindow.xaml.cs", "{entries.Count} headings")]
    [InlineData("MainWindow.xaml.cs", "({count} headings)")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "{descendants.Count} nodes")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "{nodeLinks.Count} links")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "{ViewModel.Links.Count} links")]
    [InlineData("Views/Ocr/OcrInspectionStudioWindow.xaml.cs", "{page.Words.Count} words")]
    [InlineData("Views/Ocr/OcrInspectionStudioWindow.xaml.cs", "{elem.WordCount} words")]
    [InlineData("Views/Ocr/OcrInspectionStudioWindow.xaml.cs", "Words.Count ?? 0} words")]
    [InlineData("Views/Mermaid/MermaidDiagramStudioControl.xaml.cs", "{ViewModel.Connectors.Count} edges")]
    [InlineData("Views/Mermaid/MermaidMinimapControl.cs", "{vm.Connectors.Count} connectors")]
    public void CountMessages_NoLongerPluraliseOne(string file, string stale)
    {
        Assert.DoesNotContain(stale, Read(file));
    }

    [Theory]
    [InlineData(1, "heading", "1 heading")]
    [InlineData(4, "heading", "4 headings")]
    [InlineData(1, "link", "1 link")]
    [InlineData(0, "word", "0 words")]
    [InlineData(1204, "word", "1,204 words")]
    public void CountText_ReadsRight(int n, string noun, string expected)
    {
        var text = CountText.Of(n, noun);
        // Group separator follows the current culture; compare digits only for the large case.
        if (n >= 1000) Assert.EndsWith(" words", text);
        else Assert.Equal(expected, text);
    }
}
