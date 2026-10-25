using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services.Editor;
using Xunit;

namespace MarkSmith.Tests;

// Run #242: Go to line counts the document's lines when it ends inside a fold, and the copies that
// were still silent (status line, Settings cards, the diagram minimap, the Welcome tour) or that
// threw on a busy clipboard (History, Suite Hub, Diagram Studio, the Find bar) now say what happened.
public class Run242FoldCountAndSilentCopiesTests
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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(DesktopDir(), Path.Combine(parts)));

    private static string Method(string code, string name)
    {
        var decl = Regex.Match(code, $@"^\s*(private|protected|public|internal)\b[^\n;=]*\s{Regex.Escape(name)}\(", RegexOptions.Multiline);
        Assert.True(decl.Success, $"{name} not found");
        var open = code.IndexOf('{', decl.Index);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        throw new InvalidOperationException($"{name} is unbalanced");
    }

    [Fact]
    public void DocumentLineCount_CountsTheLinesOfAFoldAtTheEnd()
    {
        const string doc = "# Title\r\rintro\r## Last\ra\rb\rc";
        var folds = new EditorFolds();
        var visible = folds.Toggle(doc, 4);
        Assert.True(folds.HasFolds(visible));
        Assert.Equal(4, visible.Split('\r').Length);
        // The old count: the last visible line's document line is the fold's first line.
        Assert.Equal(4, folds.DocumentLine(visible, 4));
        Assert.Equal(7, folds.DocumentLineCount(visible));
    }

    [Fact]
    public void DocumentLineCount_MatchesTheDocumentWithNestedFolds()
    {
        const string doc = "# A\r## S\r```\rx\ry\r```\rtail\r## T\rend";
        var folds = new EditorFolds();
        var visible = folds.FoldAllCode(doc);
        visible = folds.Toggle(visible, 2);
        Assert.True(folds.HasFolds(visible));
        Assert.Equal(folds.Document(visible).Split('\r').Length, folds.DocumentLineCount(visible));
        Assert.Equal(9, folds.DocumentLineCount(visible));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("one", 1)]
    [InlineData("a\rb\r", 3)]
    [InlineData("a\r\nb", 2)]
    public void DocumentLineCount_WithoutFolds_CountsLines(string text, int expected)
    {
        Assert.Equal(expected, new EditorFolds().DocumentLineCount(text));
    }

    [Fact]
    public void GoToLine_AndQuickJump_CountTheDocumentsLines()
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.DoesNotContain("_folds.DocumentLine(text, totalLines)", code);
        foreach (var name in new[] { "ShowGoToLineDialogAsync", "OnCursorPosButtonRightTapped" })
            Assert.Contains("_folds.DocumentLineCount(text)", Method(code, name));
    }

    [Fact]
    public void CopyWithBubble_ReportsTheResultWithoutTakingFocus()
    {
        var code = Read("Services", "CopyFeedback.cs");
        var copy = Method(code, "CopyWithBubble");
        Assert.Contains("await ClipboardWriter.TrySetTextAsync(text)", copy);
        Assert.Contains("Couldn't copy", copy);
        var show = Method(code, "ShowBubble");
        Assert.Contains("FlyoutShowMode.Transient", show);
        Assert.Contains("RaiseNotificationEvent", show);
        Assert.Contains("ThemeBrush.Set", show);
        Assert.Contains("flyout.Hide()", show);
    }

    [Fact]
    public void StatusLineCopies_UseTheBubble_SoExportActionsSurvive()
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.Contains("CopyFeedback.CopyWithBubble(StatusMsgHost", Method(code, "OnStatusMsgTextDoubleTapped"));
        var menu = Method(code, "OnStatusMsgTextRightTapped");
        Assert.Contains("CopyWithBubble(StatusMsgHost, text", menu);
        Assert.Contains("CopyWithBubble(StatusMsgHost, ViewModel.StatusOutputPath!", menu);
        // Setting StatusText would clear StatusOutputPath (and the Open / Show actions with it).
        Assert.DoesNotContain("CopyAndAnnounce", menu);
        Assert.DoesNotContain("_ = Services.ClipboardWriter.TrySetTextAsync", code);
        Assert.DoesNotContain("Clipboard.SetContent(", code);
    }

    [Theory]
    [InlineData("Controls", "SettingsCard.xaml.cs")]
    [InlineData("Views", "Mermaid", "MermaidMinimapControl.cs")]
    [InlineData("Views", "WelcomeTour.xaml.cs")]
    public void SilentCopies_NowConfirm(params string[] path)
    {
        var code = Read(path);
        Assert.Contains("CopyFeedback.CopyWithBubble(", code);
        Assert.DoesNotContain("ClipboardWriter.Set(", code);
        Assert.DoesNotContain("_ = Services.ClipboardWriter.TrySetTextAsync", code);
        Assert.DoesNotContain("_ = MarkSmith.Services.ClipboardWriter.TrySetTextAsync", code);
    }

    // ClipboardWriter.Set throws after its retries. These surfaces called it outside any handler,
    // so a busy clipboard made the click do nothing (App.UnhandledException swallowed the throw).
    [Theory]
    [InlineData("Views", "History", "HistoryWindow.xaml.cs")]
    [InlineData("Views", "SuiteHubView.xaml.cs")]
    [InlineData("Views", "Mermaid", "MermaidCanvasControl.xaml.cs")]
    [InlineData("Views", "WelcomeTour.xaml.cs")]
    public void ThrowingClipboardSet_IsGone(params string[] path)
    {
        Assert.DoesNotContain("ClipboardWriter.Set(", Read(path));
    }

    [Fact]
    public void History_ToolbarCopies_TickFromTheResult()
    {
        var code = Read("Views", "History", "HistoryWindow.xaml.cs");
        Assert.Contains("CopyFeedback.ApplyButtonTick(tickButton, error, tickLabel)", code);
        Assert.DoesNotContain("CopyFeedback.ApplyButtonTick(CopyToolbarButton, null,", code);
        Assert.True(Regex.Matches(code, @"CopyAndNotify\([^;]+, CopyToolbarButton, ").Count >= 4);
    }

    [Fact]
    public void SuiteHub_RecentPathsPill_WarnsInsteadOfTicking()
    {
        var body = Method(Read("Views", "SuiteHubView.xaml.cs"), "OnRecentDocsCountPillClick");
        var copy = body.IndexOf("await ClipboardWriter.TrySetTextAsync(summary)", StringComparison.Ordinal);
        // Run #268 added an earlier "success: false" for a list with no documents left; look after the copy.
        var fail = copy >= 0 ? body.IndexOf("success: false", copy, StringComparison.Ordinal) : -1;
        var tick = body.IndexOf("\\u2713", StringComparison.Ordinal);
        Assert.True(copy >= 0 && fail > copy && tick > fail, "the failure returns before the tick");
    }

    [Fact]
    public void DiagramStudio_SourceCopy_ReportsOnTheStatusLine()
    {
        var code = Read("Views", "Mermaid", "MermaidDiagramStudioControl.xaml.cs");
        Assert.DoesNotContain("Clipboard.SetContent(dp)", code);
        Assert.Equal(2, Regex.Matches(code, @"copyCodeItem\.Click \+= \(_, _\) => CopyMermaidSource\(\);").Count);
        var body = Method(code, "CopyMermaidSource");
        Assert.Contains("await MarkSmith.Services.ClipboardWriter.TrySetTextAsync(code)", body);
        Assert.Contains("ViewModel.StatusText = error is null", body);
        Assert.Contains("Nothing to copy yet", body);

        var canvas = Read("Views", "Mermaid", "MermaidCanvasControl.xaml.cs");
        Assert.Contains("The zoom level wasn't copied.", canvas);
        Assert.Contains("The Mermaid code wasn't copied.", canvas);
    }

    [Fact]
    public void FindBar_CopySearchQuery_NoLongerSwallowsFailure()
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.Contains("CopyAndAnnounce(FindTextBox.Text, $\"Copied search query", code);
    }
}
