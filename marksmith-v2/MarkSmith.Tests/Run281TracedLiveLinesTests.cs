using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #281: the XAML-declared live lines that never raised LiveRegionChanged, traced to a raise.
// - OCR Studio reads out the page the user moves to (buttons, Left/Right, Home/End, page menu)
//   and, when focus isn't on a card, the element [ ] / Up / Down selects;
// - OCR Studio's session metrics say "1 Element · 1 Word", not "1 Elements · 1 Words";
// - History reads out the search count pill while typing, and why the timeline is empty when a
//   search or the Starred filter hides every version (instead of the bare "0 matches");
// - SmartArt Studio reads out the custom accent's verdict;
// - the Mermaid shape palette reads out "12 of 40 shapes" as a search narrows it, dropping it
//   for the "No shapes match" line when nothing is left (LiveAnnounce.Cancel).
public class Run281TracedLiveLinesTests
{
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

    private const string Live = "MarkSmith.Desktop/Services/LiveAnnounce.cs";
    private const string Ocr = "MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml.cs";
    private const string OcrXaml = "MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml";
    private const string History = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml.cs";
    private const string SmartArt = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string Palette = "MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml.cs";

    // The method body starting at signature (brace-matched).
    private static string Body(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[start..(i + 1)];
        }
        return src[start..];
    }

    private static int CountOf(string src, string needle)
    {
        int n = 0, i = 0;
        while ((i = src.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static void Before(string src, string first, string second)
    {
        var a = src.IndexOf(first, StringComparison.Ordinal);
        var b = src.IndexOf(second, StringComparison.Ordinal);
        Assert.True(a >= 0, first);
        Assert.True(b >= 0, second);
        Assert.True(a < b, $"{first} should come before {second}");
    }

    // ---- LiveAnnounce.Cancel ----

    [Fact]
    public void Cancel_StopsThePendingTimerOnly()
    {
        var cancel = Body(Read(Live), "public static void Cancel(UIElement target)");
        Assert.Contains("if (Timers.TryGetValue(target, out var timer)) timer.Stop();", cancel);
        // It must not create a timer or raise anything itself.
        Assert.DoesNotContain("CreateTimer", cancel);
        Assert.DoesNotContain("Raise(", cancel);
    }

    // ---- OCR Studio: pages ----

    [Fact]
    public void Ocr_RenderPickedPage_RendersThenRaisesThePageIndicator()
    {
        var picked = Body(Read(Ocr), "private void RenderPickedPage()");
        Before(picked, "RenderCurrentPage();", "LiveAnnounce.Raise(PageIndicatorText)");
    }

    [Theory]
    [InlineData("_session.PreviousPage();\n            RenderPickedPage();")]
    [InlineData("_session.NextPage();\n            RenderPickedPage();")]
    [InlineData("_session.GoToFirstPage();\n                    RenderPickedPage();")]
    [InlineData("_session.GoToLastPage();\n                    RenderPickedPage();")]
    [InlineData("_session.CurrentPageIndex = 0;\n                RenderPickedPage();")]
    [InlineData("_session.CurrentPageIndex = _session.Pages.Count - 1;\n                RenderPickedPage();")]
    public void Ocr_EveryUserPageMove_AnnouncesThePage(string move)
    {
        Assert.Contains(move, Read(Ocr));
    }

    [Fact]
    public void Ocr_NoUserPageMove_RendersSilently()
    {
        var src = Read(Ocr);
        Assert.DoesNotContain("_session.PreviousPage();\n            RenderCurrentPage();", src);
        Assert.DoesNotContain("_session.NextPage();\n            RenderCurrentPage();", src);
        Assert.DoesNotContain("_session.GoToFirstPage();\n                    RenderCurrentPage();", src);
        Assert.DoesNotContain("_session.GoToLastPage();\n                    RenderCurrentPage();", src);
    }

    [Fact]
    public void Ocr_ScanDoesNotAlsoAnnounceThePage()
    {
        // The scan banner (tracked) already says "Scanning & Decomposing Page 2 of 3…"; tracking
        // the indicator too would read the page twice per scanned page.
        Assert.DoesNotContain("LiveAnnounce.Track(PageIndicatorText)", Read(Ocr));
    }

    [Fact]
    public void Ocr_PageIndicatorIsStillPoliteInXaml()
    {
        var xaml = Read(OcrXaml);
        var at = xaml.IndexOf("x:Name=\"PageIndicatorText\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var end = xaml.IndexOf("/>", at, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml[at..end]);
    }

    // ---- OCR Studio: selection ----

    [Fact]
    public void Ocr_SelectionMovedFromOutsideTheCards_ReadsTheBadge()
    {
        var keep = Body(Read(Ocr), "private void SelectElementKeepingCardFocus(OcrInspectionElement elem)");
        Assert.Contains("if (focusWasOnCard) FocusSelectedCard();", keep);
        Assert.Contains("else if (SelectedElementBadgeText is not null) LiveAnnounce.Raise(SelectedElementBadgeText);", keep);
        // The badge is raised after SelectElement has written the new element into it.
        Before(keep, "SelectElement(elem);", "LiveAnnounce.Raise(SelectedElementBadgeText)");
    }

    [Theory]
    [InlineData("private void SelectPreviousElement()")]
    [InlineData("private void SelectNextElement()")]
    public void Ocr_FirstBracketPress_GoesThroughTheAnnouncingPath(string signature)
    {
        // With nothing selected, [ / ] used to call SelectElement directly and stay silent.
        var body = Body(Read(Ocr), signature);
        Assert.DoesNotContain("SelectElement(page.Elements", body);
        Assert.Equal(1, CountOf(body, "SelectElementKeepingCardFocus("));
    }

    // ---- OCR Studio: metrics ----

    [Fact]
    public void Ocr_SessionMetrics_CountEachNoun()
    {
        var telemetry = Body(Read(Ocr), "private void UpdateSessionTelemetry()");
        Assert.Contains("CountText.Of(_session.Pages.Count, \"Page\")", telemetry);
        Assert.Contains("CountText.Of(_session.TotalElements, \"Element\")", telemetry);
        Assert.Contains("CountText.Of(_session.TotalWords, \"Word\")", telemetry);
        Assert.DoesNotContain("} Elements ·", telemetry);
        Assert.DoesNotContain("} Words ·", telemetry);
    }

    [Theory]
    [InlineData(1, "Element", "1 Element")]
    [InlineData(0, "Element", "0 Elements")]
    [InlineData(1, "Word", "1 Word")]
    [InlineData(2048, "Word", "2,048 Words")]
    [InlineData(1, "Page", "1 Page")]
    public void Ocr_MetricNouns_ReadAsSentences(int count, string noun, string expected)
    {
        Assert.Equal(expected, CountText.Of(count, noun));
    }

    // ---- History ----

    [Fact]
    public void History_SearchCountPill_IsTracked()
    {
        var src = Read(History);
        Assert.Contains("LiveAnnounce.Track(TimelineSearchMatchText);", src);
        // After InitializeComponent, or the field is null.
        Before(src, "InitializeComponent();", "LiveAnnounce.Track(TimelineSearchMatchText);");
    }

    [Fact]
    public void History_FilterThatHidesEverything_ReadsWhy_AndDropsTheCount()
    {
        var handler = Body(Read(History), "private void OnVmPropertyChanged(");
        var at = handler.IndexOf("nameof(HistoryWindowViewModel.ShowNoMatches) && _vm.ShowNoMatches", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var branch = handler[at..handler.IndexOf("else if", at, StringComparison.Ordinal)];
        // Queued: the refilter sets the count after ShowNoMatches changes, so cancel it later.
        Assert.Contains("DispatcherQueue.TryEnqueue(", branch);
        Assert.Contains("if (!_vm.ShowNoMatches) return;", branch);
        Before(branch, "LiveAnnounce.Cancel(TimelineSearchMatchText);", "LiveAnnounce.Raise(NoMatchesTextBlock);");
    }

    // Lines that change together with one that is already read out (or with the focused item),
    // so tracking them read the same change twice: the scan banner covers the word stream, the
    // footer covers the metrics, the focused timeline row covers the diff title, the queued raise
    // covers the no-match text, the status bar covers the canvas summary, and SmartArt raises its
    // no-match title itself. The custom accent verdict is tracked, so it isn't raised per keystroke.
    [Theory]
    [InlineData(Ocr, "LiveAnnounce.Track(WordStreamText")]
    [InlineData(Ocr, "LiveAnnounce.Track(SessionMetricsText")]
    [InlineData(History, "LiveAnnounce.Track(DiffTitleText")]
    [InlineData(History, "LiveAnnounce.Track(NoMatchesTextBlock")]
    [InlineData("MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml.cs", "LiveAnnounce.Track(CanvasSummaryText")]
    [InlineData(SmartArt, "LiveAnnounce.Track(NoMatchesTitleText")]
    [InlineData(SmartArt, "LiveAnnounce.Raise(CustomPaletteStatusText")]
    public void LinesAlreadyCoveredElsewhere_AreNotReadTwice(string file, string call)
    {
        Assert.DoesNotContain(call, Read(file));
    }

    [Fact]
    public void History_SearchCountPill_IsTrackedOnce()
    {
        Assert.Equal(1, CountOf(Read(History), "LiveAnnounce.Track(TimelineSearchMatchText)"));
    }

    // ---- SmartArt Studio ----

    [Fact]
    public void SmartArt_CustomAccentVerdict_IsTracked()
    {
        var src = Read(SmartArt);
        Assert.Contains("LiveAnnounce.Track(CustomPaletteStatusText);", src);
        Before(src, "this.InitializeComponent();", "LiveAnnounce.Track(CustomPaletteStatusText);");
    }

    // ---- Mermaid shape palette ----

    [Fact]
    public void Palette_Count_IsTracked_AndCancelledWhenNothingMatches()
    {
        var src = Read(Palette);
        Before(src, "InitializeComponent();", "LiveAnnounce.Track(PaletteCountText);");
        var filter = Body(src, "private void FilterItems()");
        // The count is written (and scheduled) first, then dropped for the no-match line.
        Before(filter, "PaletteCountText.Text = ", "LiveAnnounce.Cancel(PaletteCountText);");
        var zero = filter[filter.IndexOf("if (filtered.Count == 0)\n", StringComparison.Ordinal)..];
        Before(zero, "LiveAnnounce.Cancel(PaletteCountText);", "RaiseAutomationEvent");
    }
}
