using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #314:
// - every Insert dialog's "Inserts" card reads its snippet once (the scroller around it was named
//   after the snippet too) and names its keys in help text (they were only in a mouse tooltip);
// - the line-list and chart dialogs put their live count ("3 tabs", "2 points") on the edit box;
// - the Video embed card says "Nothing yet" until a link is pasted, not a made-up EXAMPLE_ID block;
// - Sort lines / Remove duplicate lines on one line say to select two or more lines, and an
//   unchanged caret-line transform talks about "this line", not "the selection".
public class Run314InsertDialogAndLineToolsTests
{
    private const string Dialogs = "MarkSmith.Desktop/Views/InsertDialogControls.cs";
    private const string Main = "MarkSmith.Desktop/MainWindow.xaml.cs";

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

    private static string Between(string code, string start, string end)
    {
        var a = code.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, start);
        var b = code.IndexOf(end, a, StringComparison.Ordinal);
        Assert.True(b > a, end);
        return code[a..b];
    }

    private static string Class(string code, string name) =>
        Between(code, $"public sealed class {name}", "\n}\n");

    // ---- Inserts card ----

    [Fact]
    public void PreviewScroller_IsRaw_SoTheSnippetIsReadOnce()
    {
        var code = Read(Dialogs);
        Assert.Contains("AutomationProperties.SetAccessibilityView(previewScroller, AccessibilityView.Raw);", code);
        Assert.Contains("previewScroller,", code);
        Assert.DoesNotContain("new ScrollViewer { Content = _preview, MaxHeight = 132, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },", code);
        // The text inside still carries the snippet.
        Assert.Contains("$\"Inserts: {snippet}\"", code);
    }

    [Fact]
    public void PreviewCard_HelpText_NamesItsKeys()
    {
        var code = Read(Dialogs);
        Assert.Contains("AutomationProperties.SetHelpText(previewCard, PreviewCardHelp);", code);
        var help = Views_PreviewCardHelp(code);
        Assert.Contains("Ctrl+Enter", help);
        Assert.Contains("Ctrl+Shift+C", help);
        Assert.Contains("Shift+F10", help);
        // The keys it names are the ones the card's menu and key handler use.
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+Shift+C\"", code);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+Enter\"", code);
    }

    private static string Views_PreviewCardHelp(string code)
    {
        var line = Between(code, "public const string PreviewCardHelp = \"", "\";");
        return line["public const string PreviewCardHelp = \"".Length..];
    }

    // ---- Counts on the edit box ----

    [Fact]
    public void LinesDialog_CountIsTheBoxHelpText()
    {
        var lines = Class(Read(Dialogs), "LinesInsertControl");
        var count = lines.IndexOf("_count.Text = Services.InsertCountText.Lines(", StringComparison.Ordinal);
        // Run #316: the count now leads a longer help text (the line rule and Ctrl+Enter follow it).
        var help = lines.IndexOf("AutomationProperties.SetHelpText(_box, Services.InsertCountText.BoxHelp(_count.Text,", StringComparison.Ordinal);
        Assert.True(count >= 0 && help > count, "help text is set from the fresh count");
        // Before the early returns, so a short or malformed list still gets its count.
        Assert.True(help < lines.IndexOf("return Services.InsertCountText.Shortfall(", StringComparison.Ordinal));
    }

    [Fact]
    public void ChartDialog_PointCountIsTheBoxHelpText()
    {
        var chart = Class(Read(Dialogs), "ChartInsertControl");
        var count = chart.IndexOf("_count.Text = $\"{points} point", StringComparison.Ordinal);
        var help = chart.IndexOf("AutomationProperties.SetHelpText(_box, $\"{_count.Text}. {DataHelp}\");", StringComparison.Ordinal);
        Assert.True(count >= 0 && help > count);
        Assert.True(help < chart.IndexOf("if (points == 0) return", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3, "tab", false, "3 tabs")]
    [InlineData(1, "step", false, "1 step")]
    public void LineCount_ReadsNaturally(int n, string noun, bool headerFirst, string expected) =>
        Assert.Equal(expected, InsertCountText.Lines(n, noun, headerFirst));

    // ---- Video embed ----

    [Fact]
    public void EmbedDialog_EmptyUrl_HasNoSnippet()
    {
        var embed = Class(Read(Dialogs), "EmbedInsertControl");
        Assert.Contains("public override string Snippet => Url.Length == 0 ? \"\" : Snippets.Embed(Provider, Url);", embed);
        // Insert stays disabled for an empty link, so nothing is lost by the empty card.
        Assert.Contains("if (Url.Length == 0) return \"Paste the video's link.\";", embed);
    }

    [Fact]
    public void EmbedBuilder_StillFallsBack_ForQuickInsert()
    {
        // Quick insert and other callers keep the builder's placeholder; only the dialog card changed.
        Assert.Contains("EXAMPLE_ID", InsertSnippetBuilder.Embed("youtube", ""));
        Assert.Contains("src=\"https://vimeo.com/1\"", InsertSnippetBuilder.Embed("vimeo", "https://vimeo.com/1"));
    }

    [Fact]
    public void EmptyCard_ShowsNothingYet_AndDisablesCopy()
    {
        var code = Read(Dialogs);
        Assert.Contains("_preview.Text = snippet.Length == 0 ? \"Nothing yet\" : snippet;", code);
        Assert.Contains("_copySnippetButton.IsEnabled = canCopy;", code);
        // Focus leaves the Copy button before it is disabled.
        var refresh = Between(code, "protected void Refresh()", "\n    }\n");
        Assert.True(refresh.IndexOf("FocusFirstField();", StringComparison.Ordinal)
                    < refresh.IndexOf("_copySnippetButton.IsEnabled = canCopy;", StringComparison.Ordinal));
    }

    // ---- Sort / dedupe on one line ----

    [Fact]
    public void SortAndDedupe_OnOneLine_AskForMoreLines()
    {
        var code = Read(Main);
        Assert.Contains("private const string SortNeedsLines = \"Select two or more lines to sort.\";", code);
        Assert.Contains("lines A to Z.\", SortNeedsLines);", code);
        Assert.Contains("lines Z to A.\", SortNeedsLines);", code);
        Assert.Contains("}, \"Select two or more lines to look for duplicates.\");", code);
    }

    [Fact]
    public void TransformSelection_ChecksLineCount_BeforeTransforming()
    {
        var code = Read(Main);
        var body = Between(code, "private void TransformSelection(", "\n    }\n");
        Assert.Contains("string? needsLines = null)", body);
        var check = body.IndexOf("if (needsLines is not null && LineCount(segment) < 2)", StringComparison.Ordinal);
        var transform = body.IndexOf("var transformed = transform(segment);", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < transform);
        // Focus goes back to the editor, the same as the other early outs.
        Assert.Contains("AnnounceStatus(needsLines);", body);
    }

    [Fact]
    public void TransformSelection_UnchangedCaretLine_SaysThisLine()
    {
        var body = Between(Read(Main), "private void TransformSelection(", "\n    }\n");
        Assert.Contains("bool caretLine = selLen == 0;", body);
        Assert.Contains("\"Nothing to change: this line is already like that.\"", body);
        Assert.Contains("\"Nothing to change: the selection is already like that.\"", body);
    }

    // ---- Status bar word count ----

    [Theory]
    [InlineData("x", "1 word · < 1 min read")]
    [InlineData("two words", "2 words · < 1 min read")]
    [InlineData("", "0 words · 0 min read")]
    public void WordCountSummary_SaysOneWord(string text, string expected) =>
        Assert.Equal(expected, DocumentStatsService.Analyze(text).SummaryText);

    [Fact]
    public void WordCountDetail_SingularWordAndCharacter()
    {
        var detail = DocumentStatsService.Analyze("x").DetailText;
        Assert.StartsWith("1 word · 1 character (1 without spaces)", detail);
        Assert.DoesNotContain("1 words", detail);
    }

    [Theory]
    [InlineData("one line", 1)]
    [InlineData("one line\n", 1)]
    [InlineData("b\na", 2)]
    [InlineData("b\na\n", 2)]
    public void LineCount_TrailingBreakIsNotALine(string text, int expected) =>
        // Mirrors MainWindow.LineCount, which decides whether sort has anything to do.
        Assert.Equal(expected, text.TrimEnd('\n', '\r').Split('\n').Length);
}
