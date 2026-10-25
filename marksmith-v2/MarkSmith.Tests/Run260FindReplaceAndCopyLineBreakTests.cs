using System;
using System.IO;
using System.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #260 (run #259's Next up item 1: the remaining readers of the editor's bare-'\r' text):
// - Find with Regex on: '^' / '$' matched only at the ends of the document, '.' ran across lines
//   ("TODO.*" selected to the end) and a typed '\n' never matched;
// - single Replace compared the selection with the query's literal text, so with Regex on it never
//   replaced and stepped to the pattern's own characters;
// - "Auto-fix safe Markdown lint issues" saw the editor text as one line, put its summary on the
//   clipboard, and jumped the caret and scroll to the top;
// - app copies kept the TextBox's bare '\r' breaks;
// - SmartArt Studio's Markdown box: Ctrl+Z after an outline command did nothing.
public class Run260FindReplaceAndCopyLineBreakTests
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
    private const string SmartArt = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";

    // ---- Regex find over editor text ----

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    public void RegexCaret_AnchorsEveryLine(string nl)
    {
        var text = string.Join(nl, "# One", "body", "# Two", "## Three");
        var matches = TextSearch.FindMatches(text, "^# ", Any, useRegex: true);
        Assert.Equal(new[] { 0, text.IndexOf("# Two", StringComparison.Ordinal) }, matches.Select(m => m.Index));
    }

    [Fact]
    public void RegexDollar_AnchorsEachLineEnd_OnBareCrText()
    {
        var text = "alpha end\rbeta\rgamma end";
        var matches = TextSearch.FindMatches(text, "end$", Any, useRegex: true);
        Assert.Equal(2, matches.Count);
        Assert.Equal(6, matches[0].Index);
    }

    [Fact]
    public void RegexDot_StopsAtTheLineBreak()
    {
        var text = "TODO: write it\rnext line\rTODO: test it";
        var matches = TextSearch.FindMatches(text, "TODO.*", Any, useRegex: true);
        Assert.Equal(2, matches.Count);
        Assert.Equal("TODO: write it".Length, matches[0].Length);
        Assert.Equal("TODO: test it", text.Substring(matches[1].Index, matches[1].Length));
    }

    [Fact]
    public void RegexNewline_MatchesTheEditorsBreak()
    {
        var text = "a\rb";
        var m = Assert.Single(TextSearch.FindMatches(text, @"a\nb", Any, useRegex: true));
        Assert.Equal((0, 3), (m.Index, m.Length));
    }

    [Fact]
    public void RegexSubject_KeepsLengthAndLeavesCrLfAndExplicitCrAlone()
    {
        Assert.Equal("a\nb", TextSearch.RegexSubject("a\rb", "x"));
        Assert.Equal("a\r\nb", TextSearch.RegexSubject("a\r\nb", "x"));
        Assert.Equal("a\rb", TextSearch.RegexSubject("a\rb", @"a\rb"));
        Assert.Single(TextSearch.FindMatches("a\rb", @"a\rb", Any, useRegex: true));
    }

    [Fact]
    public void RegexReplaceAll_ReplacesEachLineStart()
    {
        var (text, count, _) = TextSearch.ReplaceAll("one\rtwo\rthree", "^", "- ", Any, 0, useRegex: true);
        // Zero-length matches are skipped by FindMatches, so anchor on a character instead.
        Assert.Equal(0, count);
        (text, count, _) = TextSearch.ReplaceAll("one\rtwo\rthree", "^t", "T", Any, 0, useRegex: true);
        Assert.Equal(2, count);
        Assert.Equal("one\rTwo\rThree", text);
    }

    [Fact]
    public void LiteralFind_Unchanged()
    {
        var text = "cat\rcategory\rcat";
        Assert.Equal(3, TextSearch.FindMatches(text, "cat", Any).Count);
        Assert.Equal(2, TextSearch.FindMatches(text, "cat", Any, matchWholeWord: true).Count);
    }

    // ---- Single Replace helpers ----

    [Fact]
    public void IsMatchAt_MatchesRegexSelection()
    {
        var text = "see doc12 and doc7";
        var matches = TextSearch.FindMatches(text, @"doc\d+", Any, useRegex: true);
        Assert.True(TextSearch.IsMatchAt(matches, 4, 5));
        Assert.False(TextSearch.IsMatchAt(matches, 4, 3));
        Assert.False(TextSearch.IsMatchAt(matches, 4, 0));
    }

    [Fact]
    public void NextMatchFrom_StepsOnThenWraps()
    {
        var matches = TextSearch.FindMatches("x1 x2 x3", @"x\d", Any, useRegex: true);
        Assert.Equal((3, false), TextSearch.NextMatchFrom(matches, 2));
        Assert.Equal((0, true), TextSearch.NextMatchFrom(matches, 7));
        Assert.Equal((0, false), TextSearch.NextMatchFrom(matches, 0));
        Assert.Equal((-1, false), TextSearch.NextMatchFrom(Array.Empty<TextSearch.TextMatch>(), 0));
    }

    [Fact]
    public void NextMatchFrom_HonoursWholeWord()
    {
        var text = "cat category cat";
        var matches = TextSearch.FindMatches(text, "cat", Any, matchWholeWord: true);
        Assert.Equal((13, false), TextSearch.NextMatchFrom(matches, 1));
    }

    [Fact]
    public void ReplaceClick_UsesTheFindRules()
    {
        var body = Method(Read(Main), "private void OnReplaceClick(");
        Assert.Contains("TextSearch.IsMatchAt(", body);
        Assert.Contains("TextSearch.NextMatchFrom(", body);
        Assert.Contains("useRegex", body);
        Assert.Contains("wholeWord", body);
        Assert.DoesNotContain("text.IndexOf(query", body);
        Assert.DoesNotContain("string.Compare(text, selStart, query", body);
    }

    // ---- Auto-fix safe lint issues ----

    [Fact]
    public void SafeLintFix_FixesEveryLineOfBareCrText()
    {
        var text = "#Title\rtext   \r\r\r\r##Next\r\tindented";
        var (fixedText, trailing, tabs, headings, blanks, total) = TextSearch.FixSafeMarkdownLintIssues(text);
        Assert.Equal(2, headings);
        Assert.Equal(1, trailing);
        Assert.Equal(1, tabs);
        Assert.Equal(1, blanks);
        Assert.Equal(5, total);
        Assert.Equal("# Title\rtext\r\r\r## Next\r    indented", fixedText);
        Assert.DoesNotContain('\n', fixedText);
    }

    [Fact]
    public void SafeLintFix_KeepsLfAndCrLfBreaks()
    {
        Assert.Equal("# A\nb", TextSearch.FixSafeMarkdownLintIssues("#A\nb").FixedMarkdown);
        Assert.Equal("# A\r\nb", TextSearch.FixSafeMarkdownLintIssues("#A\r\nb").FixedMarkdown);
    }

    [Fact]
    public void SafeLintFix_IsAnEditNotACopy()
    {
        var body = Method(Read(Main), "private void ApplySafeMarkdownLintFixes(");
        Assert.DoesNotContain("CopyAndAnnounce", body);
        Assert.Contains("BreakUndoBurst", body);
        Assert.Contains("SyncDocumentFromEditor()", body);
        Assert.Contains("ChangeView(null, offset", body);
        Assert.Contains("Ctrl+Z undoes it", body);
    }

    // ---- Clipboard line breaks ----

    [Theory]
    [InlineData("a\rb", "a\r\nb")]
    [InlineData("a\nb", "a\r\nb")]
    [InlineData("a\r\nb", "a\r\nb")]
    [InlineData("a\r\rb\n", "a\r\n\r\nb\r\n")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void ClipboardText_UsesWindowsBreaks(string input, string expected)
    {
        Assert.Equal(expected, ClipboardText.WindowsLineBreaks(input));
    }

    [Fact]
    public void ClipboardText_NullIsEmpty() => Assert.Equal(string.Empty, ClipboardText.WindowsLineBreaks(null));

    [Fact]
    public void ClipboardWriter_NormalisesText()
    {
        var body = Method(Read("Services/ClipboardWriter.cs"), "public static Task<string?> TrySetTextAsync(");
        Assert.Contains("ClipboardText.WindowsLineBreaks(text)", body);
    }

    // ---- SmartArt Markdown box undo ----

    [Fact]
    public void SmartArtMarkdownBox_RoutesUndoToTheStudio()
    {
        var xaml = Read("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml");
        Assert.Contains("PreviewKeyDown=\"OnMarkdownTextBoxPreviewKeyDown\"", xaml);
        var body = Method(Read(SmartArt), "private void OnMarkdownTextBoxPreviewKeyDown(", "\n        }\n\n");
        Assert.Contains("!MarkdownTextBox.CanUndo && ViewModel.CanUndo", body);
        Assert.Contains("!MarkdownTextBox.CanRedo && ViewModel.CanRedo", body);
        Assert.Contains("ViewModel.UndoCommand.Execute(null)", body);
        Assert.Contains("ViewModel.RedoCommand.Execute(null)", body);
    }
}
