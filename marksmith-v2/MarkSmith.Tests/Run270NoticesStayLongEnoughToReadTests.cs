using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #270: self-closing notices stay up long enough to read, and cut-off status lines show the
// whole message on hover.
// - Core NoticeTiming: the close delay grows with the word count, keeps each surface's old minimum
//   for short messages, and caps at 12 s;
// - Version History notice and Suite Hub result line: scaled delay, held open while the pointer
//   rests on them or focus is on their close button (NoticeHold);
// - OCR footer copy note: scaled delay, longer floor for a failure;
// - Copy bubbles (Settings cards, minimap): scaled delay, held open while hovered;
// - Shape Studio, SmartArt, Document Galaxy and Mermaid status bars: full text in a tooltip while trimmed.
public class Run270NoticesStayLongEnoughToReadTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), "MarkSmith.Desktop", relative)).Replace("\r\n", "\n");

    [Fact]
    public void ShortMessageKeepsTheSurfaceMinimum()
    {
        Assert.Equal(TimeSpan.FromSeconds(6), NoticeTiming.For("Copied.", minimumSeconds: 6));
        Assert.Equal(TimeSpan.FromSeconds(3), NoticeTiming.For("Copied the page to the clipboard.", minimumSeconds: 3));
    }

    [Fact]
    public void LongMessageGetsMoreTimeThanTheMinimum()
    {
        var reason = "Couldn't copy the unified diff patch: the clipboard is being used by another app, "
                   + "so close that app or wait a moment and try the copy again from the menu.";
        var delay = NoticeTiming.For(reason, minimumSeconds: 3);
        Assert.True(delay > TimeSpan.FromSeconds(6), $"got {delay}");
        Assert.Equal(1.0 + NoticeTiming.WordCount(reason) * 0.25, delay.TotalSeconds, 3);
    }

    [Fact]
    public void DelayIsCappedButNeverBelowTheMinimum()
    {
        var essay = string.Join(' ', new string[200]).Replace(" ", "word ");
        Assert.Equal(TimeSpan.FromSeconds(12), NoticeTiming.For(essay, minimumSeconds: 3));
        Assert.Equal(TimeSpan.FromSeconds(15), NoticeTiming.For(essay, minimumSeconds: 15));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   \n\t ", 0)]
    [InlineData("one", 1)]
    [InlineData("  two   words\n", 2)]
    public void WordCountIgnoresExtraWhitespace(string? message, int expected) =>
        Assert.Equal(expected, NoticeTiming.WordCount(message));

    [Fact]
    public void EmptyMessageUsesTheMinimum() =>
        Assert.Equal(TimeSpan.FromSeconds(1.6), NoticeTiming.For(null, minimumSeconds: 1.6));

    [Fact]
    public void NoticeHoldPausesOnPointerAndFocusAndRestartsOnLeave()
    {
        var src = Read("Services/NoticeHold.cs");
        foreach (var evt in new[] { "PointerEntered", "PointerExited", "PointerCanceled", "GotFocus", "LostFocus" })
            Assert.Contains($"notice.{evt} +=", src);
        // Leaving restarts only when nothing still holds it and the caller says the notice should close.
        Assert.Contains("if (state.Pointer || state.Focus) return;", src);
        // A message that arrives while held must not start counting down.
        Assert.Contains("public static bool IsHeld(FrameworkElement notice)", src);
        // Closing the InfoBar clears the hold, so a missed PointerExited can't pin later notices open.
        Assert.Contains("bar.Closed += (_, _) => { state.Pointer = false; state.Focus = false; };", src);
        Assert.Contains("if (shouldResume()) timer.Start();", src);
    }

    [Fact]
    public void HistoryNoticeScalesAndHolds()
    {
        var src = Read("Views/History/HistoryWindow.xaml.cs");
        Assert.Contains("NoticeHold.Attach(NoticeBar, _noticeTimer, () => NoticeBar.IsOpen && !_vm.NoticeIsError);", src);
        var interval = src.IndexOf("_noticeTimer.Interval = NoticeTiming.For(_vm.Notice, minimumSeconds: 6);", StringComparison.Ordinal);
        var start = src.IndexOf("if (!_vm.NoticeIsError && !NoticeHold.IsHeld(NoticeBar)) _noticeTimer.Start();", StringComparison.Ordinal);
        Assert.True(interval > 0 && start > interval, "the interval must be set before the timer starts");
    }

    [Fact]
    public void SuiteHubResultLineScalesAndHolds()
    {
        var src = Read("Views/SuiteHubView.xaml.cs");
        Assert.Contains("NoticeHold.Attach(Notification, _notificationTimer, () => Notification.IsOpen && Notification.Severity == InfoBarSeverity.Success);", src);
        var interval = src.IndexOf("_notificationTimer.Interval = NoticeTiming.For(Notification.Message, minimumSeconds: 6);", StringComparison.Ordinal);
        var start = src.IndexOf("if (success && !NoticeHold.IsHeld(Notification)) _notificationTimer.Start();", StringComparison.Ordinal);
        Assert.True(interval > 0 && start > interval, "the interval must be set before the timer starts");
    }

    [Fact]
    public void OcrFooterCopyNoteScalesWithAFailureFloor()
    {
        var src = Read("Views/Ocr/OcrInspectionStudioWindow.xaml.cs");
        Assert.Contains("timer.Interval = NoticeTiming.For(note, minimumSeconds: error is null ? 3 : 5);", src);
        Assert.DoesNotContain("timer.Interval = TimeSpan.FromSeconds(3);", src);
    }

    [Fact]
    public void CopyBubbleScalesAndHoldsWhileHovered()
    {
        var src = Read("Services/CopyFeedback.cs");
        Assert.Contains("timer.Interval = NoticeTiming.For(message, minimumSeconds: success ? 1.6 : 4);", src);
        Assert.Contains("content.PointerEntered += (_, _) => timer.Stop();", src);
        Assert.Contains("content.PointerExited += (_, _) => timer.Start();", src);
        Assert.Contains("flyout.Closed += (_, _) => timer.Stop();", src);
        // A panel with no background isn't hit-testable between its children, so hover would flicker.
        Assert.Contains("content.Background ??=", src);
    }

    [Theory]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs")]
    [InlineData("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs")]
    [InlineData("Views/Mermaid/MermaidDiagramStudioControl.xaml.cs")]
    public void StudioStatusBarShowsFullTextWhileTrimmed(string file)
    {
        var src = Read(file);
        var init = src.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        var attach = src.IndexOf("TrimmedTextToolTip.Attach(StatusBarText);", StringComparison.Ordinal);
        Assert.True(init >= 0 && attach > init, $"{file}: attach after InitializeComponent");

        // The XAML must still trim (otherwise the tooltip never appears) and must not carry its own
        // tooltip, which TrimmedTextToolTip would overwrite.
        var xaml = Read(file[..^3]);
        var tag = xaml.IndexOf("x:Name=\"StatusBarText\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("/>", tag, StringComparison.Ordinal);
        var element = xaml[tag..end];
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", element);
        Assert.DoesNotContain("ToolTipService.ToolTip", element);
    }
}
