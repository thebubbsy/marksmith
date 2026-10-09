using System;
using System.Linq;
using MarkSmith.Services.Editor;
using Xunit;

namespace MarkSmith.Tests;

// Folding is a view: the document behind the editor never changes, whatever is folded.
public class EditorFoldsTests
{
    // The WinUI TextBox stores line breaks as a bare '\r'.
    private static string Cr(params string[] lines) => string.Join("\r", lines);

    private static readonly string Doc = Cr(
        "# Report",            // 1
        "",                    // 2
        "## Summary",          // 3
        "",                    // 4
        "Revenue grew.",       // 5
        "",                    // 6
        "```mermaid",          // 7
        "flowchart LR",        // 8
        "  A-->B",             // 9
        "```",                 // 10
        "",                    // 11
        "## Next steps",       // 12
        "",                    // 13
        "See the plan.");      // 14

    [Fact]
    public void FoldingASection_KeepsTheHeadingAndTheGapBeforeTheNextOne()
    {
        var folds = new EditorFolds();
        var visible = folds.Toggle(Doc, 3, out var at);
        Assert.Equal(3, at);
        var lines = visible.Split('\r');
        Assert.Equal("## Summary" + EditorFolds.Marker(7, 1), lines[2]);
        Assert.Equal("", lines[3]);              // the blank line before "## Next steps" stays
        Assert.Equal("## Next steps", lines[4]);
        Assert.Equal(Doc, folds.Document(visible));
        Assert.Equal(1, folds.Count(visible));
    }

    [Fact]
    public void FoldingInsideACodeBlock_FoldsTheCodeBlock_NotTheSection()
    {
        var folds = new EditorFolds();
        var visible = folds.Toggle(Doc, 9, out var at);
        Assert.Equal(7, at);
        Assert.Contains("```mermaid" + EditorFolds.Marker(3, 1), visible);
        Assert.DoesNotContain("A-->B", visible);
        Assert.Equal(Doc, folds.Document(visible));
    }

    [Fact]
    public void TogglingAFoldedLine_OpensIt()
    {
        var folds = new EditorFolds();
        var visible = folds.Toggle(Doc, 3);
        Assert.Equal(Doc, folds.Toggle(visible, 3));
    }

    [Fact]
    public void NestedFolds_OpenOneLevelAtATime_AndTheDocumentIsAlwaysWhole()
    {
        var folds = new EditorFolds();
        var inner = folds.Toggle(Doc, 7);          // the code block
        var outer = folds.Toggle(inner, 3);        // the section around it
        Assert.Equal(Doc, folds.Document(outer));
        Assert.Contains(EditorFolds.Marker(7, 2), outer); // counts the folded code in full
        var opened = folds.Toggle(outer, 3);
        Assert.Equal(inner, opened);               // the code block is still folded
    }

    [Fact]
    public void EditsAroundAFold_SurviveInTheDocument()
    {
        var folds = new EditorFolds();
        var visible = folds.Toggle(Doc, 7);
        visible = visible.Replace("## Summary", "## Overview").Replace("```mermaid", "```mermaid ");
        var doc = folds.Document(visible);
        Assert.Contains("## Overview", doc);
        Assert.Contains("```mermaid \rflowchart LR", doc);
    }

    [Fact]
    public void FoldAllCode_FoldsCodeAndBlocks_NotHeadings()
    {
        var text = Cr("# T", "```js", "a()", "```", ":::note", "```py", "x", "```", ":::", "after");
        var folds = new EditorFolds();
        var visible = folds.FoldAllCode(text);
        var lines = visible.Split('\r');
        Assert.Equal(new[] { "# T", "```js" + EditorFolds.Marker(2, 2), ":::note" + EditorFolds.Marker(4, 1), "after" }, lines);
        Assert.Equal(text, folds.Document(visible));
    }

    [Fact]
    public void HeadingsInsideCode_AreNotSections()
    {
        var text = Cr("```bash", "# not a heading", "echo", "```");
        Assert.DoesNotContain(EditorFolds.Regions(text), r => r.Kind == FoldKind.Section);
        Assert.Single(EditorFolds.Regions(text));
    }

    [Fact]
    public void TildeAndLongerFences_CloseOnlyOnAMatchingFence()
    {
        var text = Cr("~~~~", "```", "inside", "~~~~", "tail");
        var r = Assert.Single(EditorFolds.Regions(text));
        Assert.Equal((1, 4), (r.Start, r.End));
    }

    [Fact]
    public void DocumentLine_CountsHiddenLines()
    {
        var folds = new EditorFolds();
        var visible = folds.Toggle(Doc, 3);          // hides 4..10
        Assert.Equal(12, folds.DocumentLine(visible, 5)); // "## Next steps" is visible line 5
        Assert.Equal(2, folds.DocumentLine(visible, 2));
    }

    [Fact]
    public void GutterNumbers_JumpPastAFold()
    {
        var folds = new EditorFolds();
        var visible = folds.Toggle(Doc, 7);          // hides 8..10
        var numbers = folds.DocumentLineNumbers(visible, visible.Split('\r').Length);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 11, 12, 13, 14 }, numbers);
        Assert.Equal(new[] { 1, 2, 3 }, new EditorFolds().DocumentLineNumbers("a\rb\rc", 3));
    }

    [Fact]
    public void VisibleLine_IsZeroInsideAFold()
    {
        var folds = new EditorFolds();
        var visible = folds.Toggle(Doc, 7);          // hides 8..10
        Assert.Equal(7, folds.VisibleLine(visible, 7));
        Assert.Equal(0, folds.VisibleLine(visible, 9));
        Assert.Equal(9, folds.VisibleLine(visible, 12));
        Assert.Equal(5, new EditorFolds().VisibleLine(Doc, 5));
    }

    [Fact]
    public void AMarkerFromAnotherSession_IsJustText()
    {
        var folds = new EditorFolds();
        var pasted = Cr("## Summary" + EditorFolds.Marker(3, 9), "x");
        Assert.Equal(pasted, folds.Document(pasted));
        Assert.Equal(0, folds.Count(pasted));
    }

    [Fact]
    public void NothingToFold_ReturnsTheTextUnchanged()
    {
        var folds = new EditorFolds();
        var text = Cr("just", "prose");
        Assert.Equal(text, folds.Toggle(text, 1));
        Assert.Equal(text, folds.FoldAllCode(text));
    }

    [Fact]
    public void LineSeparatorsArePreserved()
    {
        var folds = new EditorFolds();
        var lf = Doc.Replace('\r', '\n');
        var visible = folds.Toggle(lf, 3);
        Assert.DoesNotContain('\r', visible);
        Assert.Equal(lf, folds.Document(visible));
    }

    [Fact]
    public void LegacyFoldComments_AreRepaired()
    {
        var legacy = EditorFoldingService.FoldAllCodeBlocks(Doc.Replace('\r', '\n'));
        Assert.Contains("<!-- FOLDED:", legacy);
        var repaired = EditorFolds.RepairLegacy(legacy, out var n);
        Assert.Equal(1, n);
        Assert.Equal(Doc.Replace('\r', '\n'), repaired);
        Assert.Equal("plain", EditorFolds.RepairLegacy("plain", out var none));
        Assert.Equal(0, none);
    }
}
