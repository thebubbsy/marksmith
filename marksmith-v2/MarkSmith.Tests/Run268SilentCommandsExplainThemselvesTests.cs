using System;
using System.IO;
using System.Text.RegularExpressions;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Tests;

// Run #268: commands that used to return without a word now say why nothing happened.
// - SmartArt Studio: outline commands (promote, demote, flatten, reverse, sort, trim, standardise,
//   normalise) on an empty outline, Copy outline on an empty outline, and the SVG copies / Save SVG with
//   no preview; Promote all / Reverse / Sort that change nothing no longer claim they did; SVG copy
//   sizes count characters (they said "bytes");
// - Main window: Replace / Replace All with an empty Find box, Clean up on an empty document and
//   Copy all lint issues on a clean document; Clean up keeps the scroll position and is its own undo step;
// - Shape Studio: Fit inside canvas on an empty canvas, and a plain-language result line;
// - Suite Hub: the recent documents pill when every recent document was moved or deleted.
public class Run268SilentCommandsExplainThemselvesTests
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

    private const string MainWindowCs = "MainWindow.xaml.cs";
    private const string ShapeCs = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string SmartArtCs = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string SuiteHubCs = "Views/SuiteHubView.xaml.cs";

    // ---- SmartArt Studio ----

    [Theory]
    [InlineData("private void OnPromoteAllClick(")]
    [InlineData("private void OnDemoteAllClick(")]
    [InlineData("private void OnFlattenAllClick(")]
    [InlineData("private void OnReverseOutlineClick(")]
    [InlineData("private void OnSortOutlineAscendingClick(")]
    [InlineData("private void OnSortOutlineDescendingClick(")]
    [InlineData("private void OnTrimTrailingWhitespaceClick(")]
    [InlineData("private void OnStandardiseBulletsClick(")]
    [InlineData("private void OnNormalizeIndentationClick(")]
    public void SmartArt_OutlineCommands_ExplainAnEmptyOutline(string signature)
    {
        var body = Body(Read(SmartArtCs), signature);
        Assert.Contains("OutlineForCommand() is not string markdown", body);
        Assert.DoesNotMatch(@"IsNullOrWhiteSpace\(markdown\)\) return;|IsNullOrEmpty\(markdown\)\) return;", body);
    }

    [Fact]
    public void SmartArt_OutlineForCommand_ReportsTheEmptyOutlineMessage()
    {
        var body = Body(Read(SmartArtCs), "private string? OutlineForCommand(");
        Assert.Contains("SmartArtDesignStudioViewModel.EmptyOutlineMessage", body);
        Assert.Contains("return null;", body);
        Assert.StartsWith("The outline is empty", SmartArtDesignStudioViewModel.EmptyOutlineMessage);
    }

    [Fact]
    public void SmartArt_ReverseAndSort_DoNotClaimAChangeThatDidNotHappen()
    {
        var src = Read(SmartArtCs);
        Assert.Contains("if (!ViewModel.ReplaceOutline(SmartArtDesignStudioViewModel.ReverseOutline(markdown)))", Body(src, "private void OnReverseOutlineClick("));
        Assert.Contains("The outline is already in A to Z order.", Body(src, "private void OnSortOutlineAscendingClick("));
        Assert.Contains("The outline is already in Z to A order.", Body(src, "private void OnSortOutlineDescendingClick("));
        Assert.Contains("Every outline item is already at the root level.", Body(src, "private void OnPromoteAllClick("));
    }

    [Fact]
    public void SmartArt_SortingASortedOutline_ChangesNothingAndAddsNoUndoStep()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = "- Alpha\n- Beta\n- Gamma";
        bool canUndoBefore = vm.CanUndo;
        Assert.False(vm.ReplaceOutline(SmartArtDesignStudioViewModel.SortOutlineAlphabetically(vm.OutlineText, ascending: true)));
        Assert.Equal(canUndoBefore, vm.CanUndo);

        vm.MarkdownText = "- Only";
        canUndoBefore = vm.CanUndo;
        Assert.False(vm.ReplaceOutline(SmartArtDesignStudioViewModel.ReverseOutline(vm.OutlineText)));
        Assert.Equal(canUndoBefore, vm.CanUndo);
    }

    [Fact]
    public void SmartArt_ReversingOneLine_IsUnchanged()
    {
        Assert.Equal("- Only", SmartArtDesignStudioViewModel.ReverseOutline("- Only"));
    }

    [Fact]
    public void SmartArt_CopyOutline_ExplainsAnEmptyOutline()
    {
        var body = Body(Read(SmartArtCs), "private void CopyOutlineMarkdown(");
        Assert.Contains("Nothing to copy: the outline is empty.", body);
    }

    [Fact]
    public void SmartArt_SvgExports_ExplainAMissingPreview_AndCountCharacters()
    {
        var src = Read(SmartArtCs);
        Assert.Contains("if (!HasExportSvg(svg)) return;", Body(src, "private void CopySvgToClipboard("));
        Assert.Contains("if (!HasExportSvg(svg)) return;", Body(src, "private void OnCopySvgDataUriClick("));
        Assert.Contains("if (!HasExportSvg(svg)) return;", Body(src, "private async void OnSaveSvgFileClick("));
        Assert.Contains("No diagram preview to export yet", Body(src, "private bool HasExportSvg("));
        Assert.DoesNotContain("bytes).\"", src);
        Assert.DoesNotContain("chars).\"", src);
    }

    // ---- Main window ----

    [Fact]
    public void Replace_WithAnEmptyFindBox_AsksForFindTextAndFocusesIt()
    {
        var src = Read(MainWindowCs);
        Assert.Contains("if (query.Length == 0) { AskForFindText(); return; }", Body(src, "private void OnReplaceClick("));
        Assert.Contains("if (query.Length == 0) { AskForFindText(); return; }", Body(src, "private void OnReplaceAllClick("));
        var ask = Body(src, "private void AskForFindText(");
        Assert.Contains("FindTextBox?.Focus(FocusState.Keyboard);", ask);
        Assert.Contains("Type the text to find first", ask);
    }

    [Fact]
    public void Cleanup_ExplainsAnEmptyDocument_AndUsesTheUndoableEdit()
    {
        var body = Body(Read(MainWindowCs), "private void OnCleanupClick(");
        Assert.Contains("The document is empty: nothing to clean up.", body);
        Assert.Contains("ApplyEditorTextEdit(result, caret, 0);", body);
        Assert.DoesNotContain("tb.Text = result;", body);
        Assert.Contains("CountText.Of(changes, \"issue\")", body);
    }

    [Fact]
    public void CopyAllLintIssues_ExplainsACleanDocument()
    {
        var body = Body(Read(MainWindowCs), "private void OnCopyAllLintIssuesClick(");
        Assert.Contains("Nothing to copy: the document has no Markdown issues.", body);
        Assert.DoesNotContain("if (_lintIssues.Count == 0) return;", body);
    }

    // ---- Shape Studio ----

    [Fact]
    public void ShapeStudio_FitInsideCanvas_ExplainsAnEmptyCanvas_AndReadsPlainly()
    {
        var body = Body(Read(ShapeCs), "private void OnSelectionFitInsideCanvasClick(");
        Assert.Contains("No shapes on the canvas to fit.", body);
        Assert.Contains("inside the canvas · Ctrl+Z to undo", body);
        Assert.DoesNotContain("dx=", body);
        Assert.True(body.IndexOf("No shapes on the canvas to fit.", StringComparison.Ordinal)
            < body.IndexOf("ViewModel.RecordUndo()", StringComparison.Ordinal));
    }

    // ---- Suite Hub ----

    [Fact]
    public void SuiteHub_RecentDocsPill_ExplainsMissingDocuments_AndUsesRealPlurals()
    {
        var body = Body(Read(SuiteHubCs), "private async void OnRecentDocsCountPillClick(");
        Assert.Contains("Nothing to copy: the recent documents were moved or deleted.", body);
        Assert.Contains("CountText.Of(existing.Count, \"path\")", body);
        Assert.DoesNotContain("if (existing.Count == 0) return;", body);
    }
}
