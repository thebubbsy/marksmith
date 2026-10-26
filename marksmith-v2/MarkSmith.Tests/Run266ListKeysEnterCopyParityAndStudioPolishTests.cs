using System;
using System.IO;
using System.Linq;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

// Run #266:
// 1. List keys (History, Outline, Lint) handle Enter/Space to activate the row action (matching their context menus' Enter accelerators).
// 2. Status message host handles Ctrl+C to copy the status message with bubble, matching double-tap.
// 3. OCR Inspection Studio page/all copy items disable when empty, report "characters", and explain empty states.
// 4. MindMap Galaxy Studio status/topology copy items disable when empty and explain empty states.
// 5. Diagram Studio status text names deleted/pasted nodes and connectors, and avoids robotic "node(s)".
public class Run266ListKeysEnterCopyParityAndStudioPolishTests
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

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative)).ReplaceLineEndings();

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing {signature}");
        int next = source.IndexOf("    private ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private const string MainWindowCs = "MainWindow.xaml.cs";
    private const string OcrCs = "Views/Ocr/OcrInspectionStudioWindow.xaml.cs";
    private const string MindMapCs = "Views/MindMap/MindMapGalaxyWindow.xaml.cs";

    private static MermaidStudioViewModel CreateFlowchartVm()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMarkdown(string.Join(Environment.NewLine, "```mermaid", "flowchart TD", "    A[Start Node] --> B[Final Node]", "```"), 0);
        return vm;
    }

    // ── 1. MainWindow list keys Enter/Space activation ─────────────────────────

    [Fact]
    public void HistoryList_KeyDown_handles_Enter_and_Space_and_checks_missing()
    {
        var body = Method(Read(MainWindowCs), "private void OnHistoryListKeyDown(");
        Assert.Contains("VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space", body);
        Assert.Contains("OpenExportedFile(entry.OutputPath)", body);
        Assert.Contains("entry.OutputMissing", body);
        Assert.Contains("file was moved or deleted", body);
    }

    [Fact]
    public void TocList_KeyDown_handles_Enter_and_Space_to_navigate()
    {
        var body = Method(Read(MainWindowCs), "private void OnTocListKeyDown(");
        Assert.Contains("VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space", body);
        Assert.Contains("NavigateToTocEntry(entry)", body);
    }

    [Fact]
    public void LintList_KeyDown_handles_Enter_and_Space_to_jump_to_issue()
    {
        var body = Method(Read(MainWindowCs), "private void OnLintListKeyDown(");
        Assert.Contains("VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space", body);
        Assert.Contains("JumpToLintIssue(issue)", body);
    }

    [Fact]
    public void StatusMsgHost_KeyDown_handles_Ctrl_C_for_copy_with_bubble()
    {
        var body = Method(Read(MainWindowCs), "private void OnStatusMsgHostKeyDown(");
        Assert.Contains("VirtualKey.C", body);
        Assert.Contains("Services.CopyFeedback.CopyWithBubble(StatusMsgHost, text, \"Copied status message\")", body);
    }

    // ── 2. OCR Inspection Studio copy menu polish ──────────────────────────────

    [Theory]
    [InlineData("private void OnCopyMarkdownButtonRightTapped(")]
    [InlineData("private void OnPageSheetRightTapped(")]
    public void Ocr_copy_page_and_all_items_disable_when_empty_and_use_characters(string signature)
    {
        var body = Method(Read(OcrCs), signature);
        Assert.Contains("IsEnabled = hasPageMd", body);
        Assert.Contains("\"character\")}) to clipboard.", body); // run #279: CountText
        Assert.DoesNotContain("chars) to clipboard.", body);
    }

    [Theory]
    [InlineData("private void OnCopyMarkdownButtonRightTapped(")]
    [InlineData("private void OnPageSheetRightTapped(")]
    public void Ocr_copy_page_and_all_explain_empty_states(string signature)
    {
        var body = Method(Read(OcrCs), signature);
        Assert.Contains("Nothing to copy yet: no pages have been scanned.", body);
        Assert.Contains("Nothing to copy yet: the current page has no Markdown.", body);
        Assert.Contains("Nothing to copy yet: the document has no Markdown.", body);
    }

    // ── 3. MindMap Galaxy Studio status & topology menu polish ────────────────

    [Fact]
    public void MindMap_copy_status_and_topology_disable_when_empty_and_explain()
    {
        var body = Method(Read(MindMapCs), "private void ShowStatusBarContextMenu(");
        Assert.Contains("IsEnabled = hasStatus", body);
        Assert.Contains("IsEnabled = hasTopology", body);
        Assert.Contains("Nothing to copy yet: status line is empty.", body);
        Assert.Contains("Nothing to copy yet: the map has no nodes.", body);
    }

    // ── 4. MermaidStudioViewModel status announcements ─────────────────────────

    [Fact]
    public void MermaidStudio_DeleteSelected_single_node_names_the_deleted_node()
    {
        var vm = CreateFlowchartVm();
        var nodeA = vm.Nodes.First(n => n.Id == "A");
        vm.SelectNode(nodeA, false);

        vm.DeleteSelected();

        Assert.Equal("Deleted 'Start Node'.", vm.StatusText);
        Assert.Single(vm.Nodes);
    }

    [Fact]
    public void MermaidStudio_DeleteSelected_multiple_nodes_formats_cleanly()
    {
        var vm = CreateFlowchartVm();
        vm.SelectAll();

        vm.DeleteSelected();

        Assert.Equal("Deleted 2 nodes.", vm.StatusText);
        Assert.Empty(vm.Nodes);
    }

    [Fact]
    public void MermaidStudio_DeleteSelected_connector_names_endpoints()
    {
        var vm = CreateFlowchartVm();
        var conn = vm.Connectors.First();
        vm.SelectedConnector = conn;

        vm.DeleteSelected();

        Assert.Equal("Deleted connector: 'Start Node' → 'Final Node'.", vm.StatusText);
        Assert.Empty(vm.Connectors);
    }

    [Fact]
    public void MermaidStudio_SelectAll_with_empty_canvas_gives_helpful_notice()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();

        vm.SelectAll();

        Assert.Equal("No shapes on the canvas to select.", vm.StatusText);
    }

    [Fact]
    public void MermaidStudio_SelectAll_with_single_node_names_the_node()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        var single = new DiagramNodeViewModel { Id = "Solo", LabelText = "Solo Step" };
        vm.Nodes.Add(single);

        vm.SelectAll();

        Assert.Equal("Selected 'Solo Step'.", vm.StatusText);
    }

    [Fact]
    public void MermaidStudio_SelectAll_with_multiple_nodes_formats_cleanly()
    {
        var vm = CreateFlowchartVm();

        vm.SelectAll();

        Assert.Equal("Selected all 2 nodes.", vm.StatusText);
    }

    [Fact]
    public void MermaidStudio_PasteClipboard_single_node_names_the_pasted_node()
    {
        var vm = CreateFlowchartVm();
        var nodeA = vm.Nodes.First(n => n.Id == "A");
        vm.SelectNode(nodeA, false);
        vm.CopySelected();

        vm.PasteClipboard();

        Assert.Equal("Pasted 'Start Node'.", vm.StatusText);
    }
}
