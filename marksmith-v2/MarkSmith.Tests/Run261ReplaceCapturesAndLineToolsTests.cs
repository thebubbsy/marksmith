using System;
using System.IO;
using MarkSmith.Services;
using MarkSmith.Services.Editor;
using Xunit;

namespace MarkSmith.Tests;

// Run #261 (run #260's "noticed" items and the editor's remaining line tools):
// - regex Replace / Replace all inserted "$1" literally instead of the match's capture;
// - "Replace all preserving match casing" overwrote the clipboard, sent the caret and scroll to
//   the top and wasn't its own undo step;
// - "Inspect match location" / "Copy match line context" counted only '\n', so on the editor's
//   bare-'\r' text every match was on line 1 and the "line" was the whole document;
// - "Copy regex capture groups" had no Multiline and no RegexSubject, so it disagreed with the count;
// - Transform ▸ Sort lines moved the selection's trailing break to the top as a blank line;
// - Transform, Alt+Up/Down and Ctrl+D set Text directly (view jumped to the top);
// - the orphaned status-bar indentation / line-ending helpers (no UI caller) ran on every caret move.
public class Run261ReplaceCapturesAndLineToolsTests
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

    private const StringComparison Any = StringComparison.OrdinalIgnoreCase;
    private const string Main = "MainWindow.xaml.cs";

    // ---- Regex replacement captures ----

    [Fact]
    public void RegexReplaceAll_ExpandsNumberedCaptures()
    {
        var (text, count, _) = TextSearch.ReplaceAll("doc12 and doc7", @"doc(\d+)", "page-$1", Any, 0, useRegex: true);
        Assert.Equal("page-12 and page-7", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void RegexReplaceAll_ExpandsNamedWholeAndDollar()
    {
        var (text, _, _) = TextSearch.ReplaceAll("a=1", @"(?<k>\w)=(?<v>\d)", "${v}:${k} [$&] $$", Any, 0, useRegex: true);
        Assert.Equal("1:a [a=1] $", text);
    }

    [Fact]
    public void LiteralReplaceAll_KeepsDollarAsTyped()
    {
        var (text, count, _) = TextSearch.ReplaceAll("cost", "cost", "$1", Any, 0);
        Assert.Equal("$1", text);
        Assert.Equal(1, count);
    }

    [Fact]
    public void RegexReplaceAll_CaptureAcrossBreak_KeepsEditorBreak()
    {
        // A capture spanning a line break comes from RegexSubject's '\n' copy; the document's '\r' goes back in.
        var (text, _, _) = TextSearch.ReplaceAll("a\rb", @"(a\nb)", "[$1]", Any, 0, useRegex: true);
        Assert.Equal("[a\rb]", text);
    }

    [Fact]
    public void RegexReplaceAll_CaretMovesWithExpandedLength()
    {
        // Caret after "x1 x2" (offset 5): each "x1"->"item-1" grows by 4.
        var (text, _, caret) = TextSearch.ReplaceAll("x1 x2 end", @"x(\d)", "item-$1", Any, 5, useRegex: true);
        Assert.Equal("item-1 item-2 end", text);
        Assert.Equal(13, caret);
    }

    [Fact]
    public void ReplacementAt_ExpandsTheSelectedMatchOnly()
    {
        const string text = "doc12 doc7";
        Assert.Equal("page-7", TextSearch.ReplacementAt(text, @"doc(\d+)", "page-$1", Any, 6, 4, useRegex: true));
        Assert.Equal("page-12", TextSearch.ReplacementAt(text, @"doc(\d+)", "page-$1", Any, 0, 5, useRegex: true));
        Assert.Null(TextSearch.ReplacementAt(text, @"doc(\d+)", "page-$1", Any, 0, 3, useRegex: true));
        Assert.Null(TextSearch.ReplacementAt(text, @"doc(\d+)", "x", Any, 0, 0, useRegex: true));
        Assert.Equal("$1", TextSearch.ReplacementAt("doc", "doc", "$1", Any, 0, 3));
    }

    [Fact]
    public void FindReplacements_InvalidPattern_IsEmpty()
    {
        Assert.Empty(TextSearch.FindReplacements("abc", "(", "x", Any, useRegex: true));
        Assert.Empty(TextSearch.FindReplacements("", "a", "x", Any));
    }

    [Fact]
    public void FindMatches_UnchangedByRefactor()
    {
        var m = TextSearch.FindMatches("TODO: a\rTODO: b", "^TODO.*", Any, useRegex: true);
        Assert.Equal(2, m.Count);
        Assert.Equal(new TextSearch.TextMatch(0, 7), m[0]);
        Assert.Equal(new TextSearch.TextMatch(8, 7), m[1]);
    }

    [Fact]
    public void SingleReplace_UsesReplacementAt()
    {
        var body = Method(Read(Main), "private void OnReplaceClick(object sender, RoutedEventArgs e)");
        Assert.Contains("TextSearch.ReplacementAt(", body);
        Assert.Contains("PasteTextBox.SelectedText = replacementHere;", body);
        Assert.DoesNotContain("PasteTextBox.SelectedText = replacement;", body);
    }

    // ---- Case-preserving Replace all ----

    [Fact]
    public void CasePreservingReplace_IsAnEditNotACopy()
    {
        var code = Read(Main);
        var start = code.IndexOf("casePreserveReplaceItem.Click += (_, _) =>", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var stop = code.IndexOf("menu.Items.Add(casePreserveReplaceItem);", start, StringComparison.Ordinal);
        var body = code[start..stop];
        Assert.DoesNotContain("CopyAndAnnounce", body);
        Assert.Contains("ViewModel.BreakUndoBurst();", body);
        Assert.Contains("SyncDocumentFromEditor();", body);
        Assert.Contains("sv.ChangeView(null, offset", body);
        Assert.Contains("Ctrl+Z undoes it.", body);
    }

    // ---- Match location / line context on bare-'\r' text ----

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void GetMatchLocation_CountsEveryBreak(string nl)
    {
        var text = "one" + nl + "two" + nl + "find me";
        var at = text.IndexOf("me", StringComparison.Ordinal);
        var (line, col, len) = TextSearch.GetMatchLocation(text, at, 2);
        Assert.Equal(3, line);
        Assert.Equal(6, col);
        Assert.Equal(2, len);
    }

    [Fact]
    public void MatchInspector_ReportsLineOnEditorText()
    {
        var text = "a\rb\rcc target";
        var s = TextSearch.FormatMatchInspectorSummary(text, text.IndexOf("target", StringComparison.Ordinal), 6, 1, 1);
        Assert.Equal("Match 1 of 1 · Line 3, Col 4 (Len: 6)", s);
    }

    [Fact]
    public void MatchLineContext_IsOnlyTheMatchLine()
    {
        var text = "first line\rsecond has word\rthird";
        var ctx = TextSearch.FormatMatchLineContext(text, text.IndexOf("word", StringComparison.Ordinal), 4);
        Assert.Equal("L2 (Col 12): second has word", ctx);
    }

    [Fact]
    public void CaptureGroups_AnchorPerLineOnEditorText()
    {
        var caps = TextSearch.ExtractRegexCaptureGroups("# One\rtext\r## Two", @"^(#+) (.*)$", ignoreCase: true);
        Assert.Equal(2, caps.Count);
        Assert.Equal("Two", caps[1].Groups[1].Value);
        Assert.Equal(11, caps[1].Offset);
    }

    // ---- Sort / dedupe lines ----

    [Fact]
    public void SortLines_KeepsTrailingBreakAtTheEnd()
    {
        Assert.Equal("apple\nBanana\ncherry\n", EditorLineTools.SortLines("cherry\napple\nBanana\n"));
        Assert.Equal("cherry\nBanana\napple\n", EditorLineTools.SortLines("cherry\napple\nBanana\n", descending: true));
        Assert.Equal("a\nb", EditorLineTools.SortLines("b\na"));
        Assert.Equal("\n\n", EditorLineTools.SortLines("\n\n"));
        Assert.Equal("", EditorLineTools.SortLines(""));
    }

    [Fact]
    public void DedupeLines_KeepsFirstAndCounts()
    {
        var (text, removed) = EditorLineTools.DedupeLines("a\nb\na\nb\nc\n");
        Assert.Equal("a\nb\nc\n", text);
        Assert.Equal(2, removed);
        Assert.Equal(("x\ny", 0), EditorLineTools.DedupeLines("x\ny"));
    }

    [Fact]
    public void SortAndDedupe_UseCoreHelpersAndReport()
    {
        var code = Read(Main);
        Assert.Contains("EditorLineTools.SortLines(s)", code);
        Assert.Contains("EditorLineTools.SortLines(s, descending: true)", code);
        Assert.Contains("EditorLineTools.DedupeLines(s).Text", code);
        Assert.Contains("duplicate line", code);
        Assert.DoesNotContain("private static string DedupeLines(string s)", code);
    }

    // ---- One helper for whole-text line edits ----

    [Fact]
    public void LineEdits_KeepScrollAndUndoStep()
    {
        var code = Read(Main);
        var helper = Method(code, "private void ApplyEditorTextEdit(string newText, int start, int length)");
        Assert.Contains("ViewModel.BreakUndoBurst();", helper);
        Assert.Contains("SyncDocumentFromEditor();", helper);
        Assert.Contains("sv.ChangeView(null, offset, null, disableAnimation: true)", helper);

        var transform = Method(code, "private void TransformSelection(Func<string, string> transform, Func<string, string, string>? done = null)");
        Assert.Contains("ApplyEditorTextEdit(", transform);
        Assert.Contains("transformed == segment", transform);
        Assert.DoesNotContain("tb.Text =", transform);
        Assert.Contains("ApplyEditorTextEdit(", Method(code, "private void MoveSelectedLines(int direction)"));
        Assert.Contains("ApplyEditorTextEdit(", Method(code, "private void DuplicateCurrentLines()"));
    }

    // ---- Status bar: message never drawn under the right-hand row ----

    [Fact]
    public void StatusBar_MessageAndRightRow_HaveTheirOwnColumns()
    {
        var xaml = Read("MainWindow.xaml");
        var grid = xaml.IndexOf("<Grid x:Name=\"StatusBarGrid\"", StringComparison.Ordinal);
        Assert.True(grid >= 0);
        var right = xaml.IndexOf("x:Name=\"StatusRightPanel\"", grid, StringComparison.Ordinal);
        var section = xaml[grid..right];
        Assert.Contains("<ColumnDefinition Width=\"*\" />", section);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", section);
        var rightTag = xaml[right..xaml.IndexOf('>', right)];
        Assert.Contains("Grid.Column=\"1\"", rightTag);
        Assert.Contains("SizeChanged=\"OnStatusRightPanelSizeChanged\"", rightTag);

        var handler = Method(Read(Main), "private void OnStatusRightPanelSizeChanged(object sender, SizeChangedEventArgs e)");
        Assert.Contains("UpdateStatusBarOverflow();", handler);
    }

    [Fact]
    public void SectionBreadcrumb_HiddenWithoutHeadings_FocusHandedOn()
    {
        var code = Read(Main);
        var update = Method(code, "private void UpdateStatusSectionBreadcrumb(string text, int currentDocLine)");
        Assert.Contains("!budgets.Any(b => b.Level > 0)", update);
        Assert.Contains("HideStatusSectionBreadcrumb();", update);
        var hide = Method(code, "private void HideStatusSectionBreadcrumb()");
        Assert.True(hide.IndexOf("Focus(FocusState.Keyboard)", StringComparison.Ordinal)
            < hide.IndexOf("Visibility.Collapsed", StringComparison.Ordinal));
    }

    [Fact]
    public void SectionBudgets_NoHeadings_HaveNoLeveledSection()
    {
        // The rule the breadcrumb relies on: a headless document has only the level-0 intro.
        Assert.DoesNotContain(TextSearch.ExtractMarkdownSectionBudgets("just text\nmore text"), s => s.Level > 0);
        Assert.Contains(TextSearch.ExtractMarkdownSectionBudgets("intro\n# Title\nbody"), s => s.Level > 0);
    }

    // ---- Orphaned status helpers removed ----

    [Fact]
    public void OrphanedStatusHelpers_AreGone_AndCaretMovesDontScanForThem()
    {
        var code = Read(Main);
        foreach (var name in new[] { "ToggleLineEndings", "ShowEncodingFlyout", "UpdateStatusEncoding", "ToggleIndentation", "ShowIndentFlyout", "UpdateStatusIndentation", "_currentIndentSummary" })
            Assert.DoesNotContain(name, code);
        var caret = Method(code, "private void UpdateCursorPosition()");
        Assert.Contains("UpdateStatusSectionBreadcrumb(EditorDocumentLf(), line);", caret);
    }
}
