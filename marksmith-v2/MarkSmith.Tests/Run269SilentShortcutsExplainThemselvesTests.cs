using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MarkSmith.Services;
using MarkSmith.ViewModels.History;
using MarkSmith.ViewModels.MindMap;
using Xunit;

namespace MarkSmith.Tests;

// Run #269: keyboard shortcuts and command-palette commands that used to do nothing now say why.
// - Version History: Ctrl+C / Ctrl+Shift+C with no version selected;
// - OCR Inspection Studio: Ctrl+C with no element selected; the OCR overlay's Copy before any page is assembled;
// - Document Galaxy: Ctrl+C on the canvas with no node selected;
// - Shape Studio: Copy as Markdown (Ctrl+C) on an empty canvas, and a real plural on the result line;
// - Main window palette: Copy document statistics with no statistics, Toggle Looking Glass while the email
//   preview disables it, Open recent on a moved/deleted file (it used to blank the editor), Copy the
//   rendered HTML of an empty document (it copied a blank page), and empty heading copies.
public class Run269SilentShortcutsExplainThemselvesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_run269_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

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

    // The body of a method, from its signature to the next member at the same indent.
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

    // ---- Version History ----

    [Fact]
    public async Task History_NothingToCopy_NoDocumentPicked_SaysPickADocument()
    {
        var service = new VersionHistoryService(Path.Combine(_dir, "history"));
        var vm = new HistoryWindowViewModel(_ => "<html/>", _ => Task.FromResult(true), service);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedFile);
        Assert.Equal("Nothing to copy: pick a document on the left, then select a version to copy its Markdown.",
            vm.NothingToCopyMessage(patch: false));
        Assert.EndsWith("to copy its diff patch.", vm.NothingToCopyMessage(patch: true));
    }

    [Fact]
    public async Task History_NothingToCopy_FollowsTheTimelineState()
    {
        var file = Path.Combine(_dir, "doc.md");
        var service = new VersionHistoryService(Path.Combine(_dir, "history"));
        await service.CaptureAsync(file, "one", "opened");
        await Task.Delay(20);
        await service.CaptureAsync(file, "two", "export:pdf");
        var vm = new HistoryWindowViewModel(_ => "<html/>", _ => Task.FromResult(true), service, initialFilePath: file);
        await vm.LoadCommand.ExecuteAsync(null);
        for (int i = 0; i < 50 && !vm.HasVersions; i++) await Task.Delay(50);
        Assert.NotNull(vm.SelectedFile);
        Assert.True(vm.HasVersions);

        vm.Selected = null;
        Assert.Equal("Nothing to copy: select a version on the timeline to copy its diff patch.",
            vm.NothingToCopyMessage(patch: true));

        vm.FilterHidesAll = true;
        Assert.Contains("filter hides every version", vm.NothingToCopyMessage(patch: false));

        vm.HasVersions = false;
        Assert.Equal("Nothing to copy: this document has no saved versions yet.", vm.NothingToCopyMessage(patch: false));
    }

    [Fact]
    public void History_CtrlC_WithNoVersion_ShowsTheNotice_AfterTheRealCopyBranches()
    {
        var src = Read("Views/History/HistoryWindow.xaml.cs");
        var body = Body(src, "private void OnRootKeyDown(");
        int copy = body.IndexOf("_ = CopyVersionMarkdownAsync(copyTarget", StringComparison.Ordinal);
        int notice = body.IndexOf("_vm.ShowNotice(_vm.NothingToCopyMessage(patch: isShift));", StringComparison.Ordinal);
        Assert.True(copy > 0 && notice > copy, "the fallback must come after both copy branches");
        // Text boxes and selectable text keep their own Ctrl+C.
        Assert.Contains("isCtrl && e.Key == VirtualKey.C && !inTextBox && !isAlt && e.OriginalSource is not (TextBlock or RichTextBlock)", body);
    }

    // ---- OCR ----

    [Fact]
    public void OcrStudio_CtrlC_AlwaysReports_EvenWithNoSelectedElement()
    {
        var src = Read("Views/Ocr/OcrInspectionStudioWindow.xaml.cs");
        int key = src.IndexOf("if (e.Key == Windows.System.VirtualKey.C && ctrl && !shift && !alt)", StringComparison.Ordinal);
        Assert.True(key > 0);
        var block = src.Substring(key, 260);
        Assert.DoesNotContain("if (_selectedElement is not null)", block);
        Assert.Contains("CopySelectedElementMarkdown();", block);
        Assert.Contains("Nothing to copy yet: select an element on the page first. Ctrl+Shift+C copies the page's Markdown.",
            Body(src, "private async void CopySelectedElementMarkdown("));
    }

    [Fact]
    public void OcrOverlay_CopyBeforeAnyPage_SaysNothingToCopyYet()
    {
        var body = Body(Read("Controls/OcrExperienceOverlay.xaml.cs"), "private async void CopyFinalMarkdownToClipboard(");
        Assert.DoesNotContain("if (string.IsNullOrWhiteSpace(text)) return;", body);
        Assert.Contains("Nothing to copy yet: the Markdown appears here once the first page is assembled.", body);
    }

    // ---- Document Galaxy ----

    [Fact]
    public void MindMap_CtrlC_WithNoNode_SaysSelectANode_AfterTheNodeCopy()
    {
        var src = Read("Views/MindMap/MindMapGalaxyWindow.xaml.cs");
        int withNode = src.IndexOf("case Windows.System.VirtualKey.C when ctrl && !shift && canvasFocused && ViewModel.SelectedNode != null:", StringComparison.Ordinal);
        int fallback = src.IndexOf("case Windows.System.VirtualKey.C when ctrl && !shift && canvasFocused:", StringComparison.Ordinal);
        Assert.True(withNode > 0 && fallback > withNode, "the fallback case must follow the node copy");
        Assert.Contains("Nothing to copy: select a node to copy its title.", src.Substring(fallback, 300));
    }

    // ---- Shape Studio ----

    [Fact]
    public void ShapeStudio_CopyMarkdown_EmptyCanvas_ExplainsBeforeCopying_AndUsesRealPlurals()
    {
        var body = Body(Read("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs"), "private async void OnCopyMarkdownClick(");
        int guard = body.IndexOf("No shapes on the canvas to copy", StringComparison.Ordinal);
        int copy = body.IndexOf("ClipboardWriter.TrySetTextAsync", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < copy);
        Assert.Contains("Copied {CountText.Of(ViewModel.Shapes.Count, \"shape\")} as a :::shapes Markdown block.", body);
        Assert.DoesNotContain("Copied {ViewModel.Shapes.Count} shapes", body);
    }

    [Theory]
    [InlineData(0, "0 shapes")]
    [InlineData(1, "1 shape")]
    [InlineData(4, "4 shapes")]
    public void CountText_ShapeCounts(int n, string expected) => Assert.Equal(expected, CountText.Of(n, "shape"));

    // ---- Main window ----

    [Fact]
    public void Palette_CopyDocumentStatistics_WithNoStats_SaysSo()
    {
        var src = Read("MainWindow.xaml.cs");
        int cmd = src.IndexOf("Do(\"Copy document statistics\"", StringComparison.Ordinal);
        Assert.True(cmd > 0);
        Assert.Contains("No statistics to copy yet: open or type a document first.", src.Substring(cmd, 700));
    }

    [Fact]
    public void Palette_ToggleLookingGlass_WhileDisabled_ExplainsTheEmailPreview()
    {
        var src = Read("MainWindow.xaml.cs");
        int cmd = src.IndexOf("Do(\"Toggle Looking Glass portal\"", StringComparison.Ordinal);
        Assert.True(cmd > 0);
        // Short enough for the status bar without an ellipsis (the first wording was cut off live).
        Assert.Contains("AnnounceStatus(\"Turn off Preview as email to use Looking Glass.\");", src.Substring(cmd, 500));
    }

    [Fact]
    public void Palette_OpenRecent_MissingFile_KeepsTheDocument_AndWarns()
    {
        var src = Read("MainWindow.xaml.cs");
        int cmd = src.IndexOf("cmds.Add(new($\"Open recent:", StringComparison.Ordinal);
        Assert.True(cmd > 0);
        var block = src.Substring(cmd, 900);
        Assert.DoesNotContain("ViewModel.InputFilePath = p;", block);
        Assert.Contains("the file was moved or deleted.", block);
        Assert.Contains("Models.StatusSeverity.Warning", block);
    }

    [Fact]
    public void CopyRenderedHtml_EmptyDocument_SaysSo_BeforeBuildingHtml()
    {
        var body = Body(Read("MainWindow.xaml.cs"), "private async void OnCopyHtmlClick(");
        int guard = body.IndexOf("Nothing to copy: the document is empty.", StringComparison.Ordinal);
        int build = body.IndexOf("vm.BuildPreviewHtml(", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < build);
    }

    [Fact]
    public void CopyTextToStatusBar_EmptyText_SaysSo()
    {
        var body = Body(Read("MainWindow.xaml.cs"), "private async void CopyTextToStatusBar(");
        Assert.DoesNotContain("if (string.IsNullOrEmpty(text)) return;", body);
        Assert.Contains("Nothing to copy: that item has no text.", body);
    }

    // ---- Shape Studio: Export, Duplicate, Reorder ----

    [Fact]
    public void ShapeStudio_ExportDocxAndDotx_EmptyCanvas_ExplainsBeforePicker()
    {
        var body = Body(Read("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs"), "private async System.Threading.Tasks.Task ExportAsync(bool template)");
        int guard = body.IndexOf("No shapes on the canvas to export", StringComparison.Ordinal);
        int picker = body.IndexOf("NativeFilePicker.PickSaveFileAsync", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < picker, "the empty check must precede the file picker");
    }

    [Fact]
    public void ShapeStudio_Duplicate_NoSelection_Explains()
    {
        var body = Body(Read("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs"), "private void ExecuteDuplicateWithHudFeedback()");
        Assert.Contains("Nothing to duplicate: select a shape on the canvas first.", body);
    }

    [Fact]
    public void ShapeStudio_KeyboardReorderAndDuplicate_NoSelection_Explains()
    {
        var src = Read("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs");
        var body = Body(src, "private void OnRootPreviewKeyDown(");
        Assert.Contains("case VirtualKey.D when ctrl && !ViewModel.HasSelection:", body);
        Assert.Contains("case (VirtualKey)221 or (VirtualKey)219 when ctrl && !ViewModel.HasSelection:", body);
        Assert.Contains("Nothing to reorder: select a shape on the canvas first.", body);
    }

    // ---- Mind Map: Copy Mermaid, Duplicate Node ----

    [Fact]
    public void MindMap_CopyMermaid_EmptyMap_ExplainsBeforeExport()
    {
        var body = Body(Read("Views/MindMap/MindMapGalaxyWindow.xaml.cs"), "private async void CopyMermaid(");
        int guard = body.IndexOf("No nodes on the map to copy", StringComparison.Ordinal);
        int export = body.IndexOf("ViewModel.ExportToMermaid(", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < export, "the empty check must precede exporting");
    }

    [Fact]
    public void MindMap_DuplicateSelectedNode_NoSelection_Explains()
    {
        var vm = new MindMapStudioViewModel();
        vm.SelectedNode = null;
        vm.DuplicateSelectedNode();
        Assert.Equal("Nothing to duplicate: select a node on the map first.", vm.StatusMessage);
    }

    // ---- OCR: Reset Element ----

    [Fact]
    public void OcrStudio_ResetElement_NoSelectionOrNotReclassified_Explains()
    {
        var body = Body(Read("Views/Ocr/OcrInspectionStudioWindow.xaml.cs"), "private void ResetSelectedElement()");
        Assert.Contains("Nothing to reset: select an element on the page first.", body);
        Assert.Contains("already in its original classification", body);
    }

    // ---- Main window: Transform selection ----

    [Fact]
    public void TransformSelection_EmptyDocumentOrLine_Explains()
    {
        var body = Body(Read("MainWindow.xaml.cs"), "private void TransformSelection(");
        Assert.Contains("The document is empty: nothing to transform.", body);
        Assert.Contains("Nothing to transform: select text or place the caret on a line with text.", body);
    }
}
