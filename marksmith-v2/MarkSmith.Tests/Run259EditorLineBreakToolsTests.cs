using System;
using System.IO;
using System.Linq;
using MarkSmith.Services;
using MarkSmith.Services.Editor;
using Xunit;

namespace MarkSmith.Tests;

// Run #259 (run #258's Next up item 1: other text boxes' line-based tools):
// - the main editor is a WinUI TextBox, which stores every line break as a bare '\r'. The status bar's
//   indentation pill, "Indent using Spaces / Tabs", "Trim trailing whitespace", the § section breadcrumb,
//   the ten status-bar Copy extractors and the Diagnostics reports all split on '\n' only, so they read
//   the whole document as one line;
// - Insert ▸ SmartArt's "Reset all indentations" only reset the first typed line;
// - Shape Studio's spacing badges: double-click spaced LEFT EDGES (not the gaps the badge shows), then
//   overwrote the VM's status with "Distributed N shapes evenly"; the handlers captured the first
//   selection's axis and gap forever, and the brushes were raw Resources lookups.
public class Run259EditorLineBreakToolsTests
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

    private static string Method(string code, string signature, string end = "\n    }\n")
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var stop = code.IndexOf(end, start, StringComparison.Ordinal);
        return code[start..stop];
    }

    private const string Main = "MainWindow.xaml.cs";
    private const string ShapeWindow = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";

    // ---- EditorIndentation (Core) ----

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Detect_ReadsEveryLine(string nl)
    {
        var text = string.Join(nl, "# Title", "- a", "  - b", "  - c", "    - d");
        var style = EditorIndentation.Detect(text);
        Assert.False(style.UseTabs);
        Assert.Equal(2, style.Size);
        Assert.Equal("Spaces: 2", style.Summary);
    }

    [Fact]
    public void Detect_Tabs()
    {
        Assert.Equal("Tabs", EditorIndentation.Detect("a\r\tb\r\tc\r    d").Summary);
        Assert.Equal("Spaces: 4", EditorIndentation.Detect("").Summary);
    }

    [Fact]
    public void Convert_ReindentsEveryLine_KeepingBreaks()
    {
        var result = EditorIndentation.Convert("- a\r    - b\r        - c\r", toTabs: false, targetSpaces: 2);
        Assert.Equal("- a\r  - b\r    - c\r", result.Text);
        Assert.Equal(2, result.ChangedLines);
    }

    [Fact]
    public void Convert_ToTabs_AndMixedBreaks()
    {
        var result = EditorIndentation.Convert("x\r\n    y\n        z\r", toTabs: true, targetSpaces: 4);
        Assert.Equal("x\r\n\ty\n\t\tz\r", result.Text);
        Assert.Equal(2, result.ChangedLines);
    }

    [Fact]
    public void Convert_AlreadyInStyle_ChangesNothing()
    {
        var text = "- a\r  - b\r    - c";
        var result = EditorIndentation.Convert(text, toTabs: false, targetSpaces: 2);
        Assert.Equal(text, result.Text);
        Assert.Equal(0, result.ChangedLines);
    }

    [Fact]
    public void TrimTrailingWhitespace_EveryLine()
    {
        var result = EditorIndentation.TrimTrailingWhitespace("a  \rb\t\rc\r\nd \n");
        Assert.Equal("a\rb\rc\r\nd\n", result.Text);
        Assert.Equal(3, result.ChangedLines);
        Assert.Equal(0, EditorIndentation.TrimTrailingWhitespace("a\rb").ChangedLines);
    }

    // ---- Section budgets (breadcrumb) ----

    [Fact]
    public void SectionBudgets_SplitOnBareCarriageReturns()
    {
        var budgets = TextSearch.ExtractMarkdownSectionBudgets("# One\rtext\r## Two\rmore\r## Three\rend");
        Assert.Equal(new[] { "One", "Two", "Three" }, budgets.Select(b => b.Title).ToArray());
        Assert.Equal(new[] { 1, 3, 5 }, budgets.Select(b => b.LineNumber).ToArray());
    }

    // ---- MainWindow source contracts ----

    [Fact]
    public void EditorDocumentLf_NormalisesBreaks()
    {
        var code = Read(Main);
        Assert.Contains("private string EditorDocumentLf() => EditorDocument().Replace(\"\\r\\n\", \"\\n\").Replace('\\r', '\\n');", code);
    }

    [Theory]
    [InlineData("ExtractFencedCodeBlocks()")]
    [InlineData("ExtractMarkdownPipeTables()")]
    [InlineData("ExtractMarkdownTaskItems()")]
    [InlineData("ExtractMarkdownMathExpressions()")]
    [InlineData("ExtractMarkdownCalloutBlocks()")]
    [InlineData("ExtractMarkdownImages()")]
    [InlineData("ExtractMarkdownFootnotes()")]
    [InlineData("ExtractMarkdownDiagrams()")]
    [InlineData("BuildMarkdownTocOutline(bool linkedLinks)")]
    public void StatusBarExtractors_ReadLfDocument(string signature)
    {
        var code = Read(Main);
        var start = code.IndexOf("> " + signature, StringComparison.Ordinal);
        if (start < 0) start = code.IndexOf(") " + signature, StringComparison.Ordinal);
        if (start < 0) start = code.IndexOf(" " + signature + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var head = code.Substring(start, 400);
        Assert.Contains("EditorDocumentLf()", head);
    }

    [Fact]
    public void DiagnosticsReports_ReadLfDocument()
    {
        var code = Read(Main);
        var lines = code.Split('\n');
        var raw = 0;
        for (var i = 0; i < lines.Length - 2; i++)
        {
            var s = lines[i].Trim();
            if (s is not ("var text = EditorDocument();" or "var docText = EditorDocument();")) continue;
            var next = lines[i + 1] + lines[i + 2];
            if (next.Contains("TextSearch.") || next.Contains("MarkSmith.Services.")) raw++;
        }
        Assert.Equal(0, raw);
        Assert.True(code.Split("EditorDocumentLf()").Length - 1 >= 50);
    }

    [Fact]
    public void Breadcrumb_UsesDocumentText()
    {
        var code = Read(Main);
        Assert.Contains("UpdateStatusSectionBreadcrumb(EditorDocumentLf(), line);", code);
        Assert.DoesNotContain("UpdateStatusSectionBreadcrumb(text, line);", code);
    }

    [Fact]
    public void IndentationTools_UseCoreHelper_AndReportHonestly()
    {
        var code = Read(Main);
        var detect = Method(code, "private void UpdateStatusIndentation(string? text)");
        Assert.Contains("EditorIndentation.Detect(text)", detect);
        Assert.DoesNotContain("Split('\\n')", detect);

        var convert = Method(code, "private void ConvertIndentation(bool toTabs, int targetSpaces)");
        Assert.Contains("EditorIndentation.Convert(text, toTabs, targetSpaces)", convert);
        Assert.Contains("nothing to convert", convert);
        Assert.Contains("result.ChangedLines", convert);
        Assert.DoesNotContain("Split('\\n')", convert);

        var trim = Method(code, "private void TrimTrailingWhitespace()");
        Assert.Contains("EditorIndentation.TrimTrailingWhitespace(text)", trim);
        Assert.Contains("nothing to trim", trim);
        Assert.DoesNotContain("across all document lines", trim);
    }

    // ---- Insert ▸ SmartArt ----

    [Fact]
    public void SmartArtInsert_ResetIndentations_SplitsAnyBreak()
    {
        var code = Read("Views/SmartArtInsertControl.cs");
        Assert.DoesNotContain("(_box.Text ?? \"\").Split('\\n')", code);
        Assert.Contains("(_box.Text ?? \"\").Replace(\"\\r\\n\", \"\\n\").Replace('\\r', '\\n').Split('\\n')", code);
    }

    // ---- Shape Studio spacing badges ----

    [Fact]
    public void SpacingBadge_DoubleClickEqualisesGaps_FromCurrentState()
    {
        var code = Read(ShapeWindow);
        var body = Method(code, "private void UpdateDistributionSpacingBadges(", "\n        }\n\n");
        Assert.DoesNotContain("shapes evenly · Ctrl+Z", body);
        Assert.DoesNotContain("DistributeHorizontalCommand", body);
        Assert.Contains("ViewModel.DistributeShapesEvenly(state.Horizontal ? DistributionAxis.Horizontal : DistributionAxis.Vertical)", body);
        Assert.Contains("ReportEdit(summary);", body);
        Assert.Contains("b.Tag = new SpacingBadgeState(isHorizontal, avgGap);", body);
        Assert.Contains("ShowSpacingPresetsMenu(e.GetPosition(MainCanvas), state.Horizontal, state.AverageGap)", body);
        Assert.DoesNotContain("if (totalW >= totalH)", body);
        Assert.DoesNotContain("totalW >= totalH, avgGap", body);
    }

    [Fact]
    public void Convert_FourSpaceToTwoSpace_HalvesIndent()
    {
        // 4-space levels to 2-space levels and back: the old code divided by the target size, so a
        // 4-space document converted to "Spaces: 2" kept every indent as it was.
        var four = "- a\r    - b\r        - c";
        var two = EditorIndentation.Convert(four, toTabs: false, targetSpaces: 2);
        Assert.Equal("- a\r  - b\r    - c", two.Text);
        Assert.Equal("Spaces: 2", EditorIndentation.Detect(two.Text).Summary);
        Assert.Equal(four, EditorIndentation.Convert(two.Text, toTabs: false, targetSpaces: 4).Text);
        Assert.Equal("- a\r\t- b\r\t\t- c", EditorIndentation.Convert(two.Text, toTabs: true, targetSpaces: 4).Text);
    }

    [Fact]
    public void Detect_TwoSpaceDocumentWithDeepLevels()
    {
        // Depths 2, 4, 4, 8: the old "more 2s than 4s" rule called this Spaces: 4.
        Assert.Equal("Spaces: 2", EditorIndentation.Detect("a\r  b\r    c\r    d\r        e").Summary);
        Assert.Equal("Spaces: 4", EditorIndentation.Detect("a\r    b\r    c\r        d").Summary);
    }

    [Fact]
    public void SpacingBadge_UsesThemeBrushes()
    {
        var code = Read(ShapeWindow);
        var body = Method(code, "private void UpdateDistributionSpacingBadges(", "\n        }\n\n");
        Assert.DoesNotContain("Application.Current.Resources[", body);
        Assert.Contains("ThemeBrush.Set(badge, Border.BackgroundProperty, \"AcrylicBackgroundFillColorDefaultBrush\")", body);
        Assert.Contains("ThemeBrush.Set(tb, TextBlock.ForegroundProperty, \"AccentFillColorDefaultBrush\")", body);
    }
}
