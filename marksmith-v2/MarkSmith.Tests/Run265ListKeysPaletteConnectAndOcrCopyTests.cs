using System;
using System.IO;
using System.Linq;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

// Run #265: list keys act only on the row they came from (Recent exports, Outline, Lint), the
// Diagram Studio palette's "Connect to" names the shape it added, and the OCR Inspection Studio's
// copy commands say why nothing was copied.
public class Run265ListKeysPaletteConnectAndOcrCopyTests
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
    private const string PaletteCs = "Views/Mermaid/NodePaletteControl.xaml.cs";
    private const string OcrCs = "Views/Ocr/OcrInspectionStudioWindow.xaml.cs";

    private static MermaidStudioViewModel FlowchartVm()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMarkdown(string.Join(Environment.NewLine, "```mermaid", "flowchart TD", "    A[Start] --> B[End]", "```"), 0);
        return vm;
    }

    // ── Recent exports / Outline / Lint list keys ─────────────────────────────

    [Theory]
    [InlineData("private void OnHistoryListKeyDown(", "ViewModel.History.FirstOrDefault()")]
    [InlineData("private void OnTocListKeyDown(", "IEnumerable<Services.TocEntry>)?.FirstOrDefault()")]
    [InlineData("private void OnLintListKeyDown(", "IEnumerable<LintRow>)?.FirstOrDefault()")]
    public void List_key_handlers_do_not_fall_back_to_the_first_row(string signature, string fallback)
    {
        var body = Method(Read(MainWindowCs), signature);
        Assert.DoesNotContain(fallback, body);
        Assert.Contains("FindListItemFromSource<", body);
    }

    [Fact]
    public void Lint_copy_all_runs_before_a_row_is_resolved()
    {
        var body = Method(Read(MainWindowCs), "private void OnLintListKeyDown(");
        int copyAll = body.IndexOf("OnCopyAllLintIssuesClick", StringComparison.Ordinal);
        int resolve = body.IndexOf("FindListItemFromSource<LintRow>", StringComparison.Ordinal);
        Assert.True(copyAll > 0 && resolve > copyAll, "Ctrl+Shift+C must not need a resolved row");
        Assert.Equal(1, body.Split("OnCopyAllLintIssuesClick").Length - 1);
    }

    [Fact]
    public void Toc_handler_still_resolves_from_source_before_acting()
    {
        var body = Method(Read(MainWindowCs), "private void OnTocListKeyDown(");
        int resolve = body.IndexOf("FindListItemFromSource<Services.TocEntry>", StringComparison.Ordinal);
        int copy = body.IndexOf("Copied heading:", StringComparison.Ordinal);
        Assert.True(resolve > 0 && copy > resolve);
    }

    // ── Diagram Studio palette "Connect to" ───────────────────────────────────

    [Fact]
    public void QuickAddPaletteShape_throws_on_null_arguments()
    {
        var vm = FlowchartVm();
        var source = vm.Nodes.First();
        var item = new MermaidPaletteItem { DisplayName = "Process", ShapeType = "Rectangle", Category = "Flowchart", DefaultText = "Task" };

        Assert.Throws<ArgumentNullException>(() => vm.QuickAddPaletteShape(null!, item));
        Assert.Throws<ArgumentNullException>(() => vm.QuickAddPaletteShape(source, null!));
    }

    [Fact]
    public void QuickAddPaletteShape_adds_the_palette_shape_linked_from_the_source()
    {
        var vm = FlowchartVm();
        var source = vm.Nodes.First(n => n.Id == "A");
        int nodes = vm.Nodes.Count, edges = vm.Connectors.Count;
        var item = new MermaidPaletteItem { DisplayName = "Decision", ShapeType = "Rhombus", Category = "Flowchart", DefaultText = "Decision?" };

        var added = vm.QuickAddPaletteShape(source, item);

        Assert.Equal(nodes + 1, vm.Nodes.Count);
        Assert.Equal(edges + 1, vm.Connectors.Count);
        Assert.Equal("Rhombus", added.Shape);
        Assert.Equal("Decision?", added.LabelText);
        Assert.Contains(vm.Connectors, c => c.SourceNodeId == "A" && c.TargetNodeId == added.Id);
        Assert.True(added.X > source.X);
        Assert.Same(added, vm.SelectedNode);
    }

    [Fact]
    public void QuickAddPaletteShape_status_names_the_shape_and_the_source()
    {
        var vm = FlowchartVm();
        var source = vm.Nodes.First(n => n.Id == "A");
        var item = new MermaidPaletteItem { DisplayName = "Decision", ShapeType = "Rhombus", Category = "Flowchart", DefaultText = "Decision?" };

        vm.QuickAddPaletteShape(source, item);

        Assert.StartsWith("Added Decision connected to '", vm.StatusText);
        Assert.Contains(MermaidStudioViewModel.DisplayName(source), vm.StatusText);
        Assert.DoesNotContain("Added a shape", vm.StatusText);
        Assert.EndsWith("Type to rename it.", vm.StatusText);
    }

    [Fact]
    public void QuickAddPaletteShape_pseudo_state_is_small_and_has_no_rename_hint()
    {
        var vm = FlowchartVm();
        var source = vm.Nodes.First(n => n.Id == "A");
        var item = new MermaidPaletteItem { DisplayName = "End state", ShapeType = "End", Category = "State", DefaultText = "" };

        var added = vm.QuickAddPaletteShape(source, item);

        Assert.True(added.IsPseudoState);
        Assert.Equal(28, added.Width);
        Assert.Equal(28, added.Height);
        Assert.DoesNotContain("rename", vm.StatusText);
    }

    [Fact]
    public void QuickAddPaletteShape_is_one_undo_step()
    {
        var vm = FlowchartVm();
        var source = vm.Nodes.First(n => n.Id == "A");
        int nodes = vm.Nodes.Count;
        var item = new MermaidPaletteItem { DisplayName = "Decision", ShapeType = "Rhombus", Category = "Flowchart", DefaultText = "Decision?" };

        vm.QuickAddPaletteShape(source, item);
        vm.Undo();

        Assert.Equal(nodes, vm.Nodes.Count);
    }

    [Fact]
    public void Palette_connect_item_uses_the_view_model_and_has_a_tooltip()
    {
        var src = Read(PaletteCs);
        Assert.Contains("vm.QuickAddPaletteShape(selected, item)", src);
        Assert.DoesNotContain("vm.QuickAddNode(selected, \"Right\")", src);
        Assert.Contains("to the right of the selected shape, linked from it", src);
    }

    [Fact]
    public void Palette_menu_shows_a_disabled_connect_hint_with_no_selection()
    {
        var src = Read(PaletteCs);
        Assert.Contains("SelectedNode: null", src);
        Assert.Contains("Connect to the selected shape (select a shape first)", src);
        var hint = src[src.IndexOf("Connect to the selected shape (select a shape first)", StringComparison.Ordinal)..];
        Assert.Contains("IsEnabled = false", hint[..300]);
    }

    // ── OCR Inspection Studio copy commands ───────────────────────────────────

    [Fact]
    public void Ocr_copy_preview_reports_nothing_to_copy()
    {
        var body = Method(Read(OcrCs), "private async void OnCopyMarkdownClick(");
        Assert.Contains("Nothing to copy yet: no pages have been scanned.", body);
        Assert.Contains("has no Markdown.", body);
        Assert.Contains("\"character\")}) to clipboard.", body); // run #279: CountText
        Assert.DoesNotContain("chars) to clipboard.", body);
    }

    [Fact]
    public void Ocr_copy_element_reports_why_nothing_was_copied()
    {
        var body = Method(Read(OcrCs), "private async void CopySelectedElementMarkdown(");
        Assert.Contains("Nothing to copy yet: select an element on the page first.", body);
        Assert.Contains("has no Markdown.", body);
        Assert.Contains("$\"Couldn't copy: {error}\"", body);
        Assert.DoesNotContain("SetFooterStatusText(error);", body);
    }

    [Fact]
    public void Ocr_preview_menu_disables_copy_and_select_all_when_empty()
    {
        var body = Method(Read(OcrCs), "private void FillMarkdownPreviewMenu(");
        Assert.Contains("bool hasPreview = !string.IsNullOrWhiteSpace(MarkdownPreviewTextBox.Text);", body);
        Assert.Equal(2, body.Split("IsEnabled = hasPreview").Length - 1);
    }
}
