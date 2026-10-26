using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #281: Sweeping remaining XAML-declared live regions across OCR Studio, History Window,
// Mermaid Diagram Studio, Node Palette, and SmartArt Studio into LiveAnnounce.Track or LiveAnnounce.Raise.
// - OCR Studio: PageIndicatorText, WordStreamText, SelectedElementBadgeText, and SessionMetricsText are tracked;
// - History Window: TimelineSearchMatchText, NoMatchesTextBlock, and DiffTitleText are tracked; ChangeStepperButton and DiffMatchCountLabel are reliably raised via LiveAnnounce.Raise;
// - Mermaid Studio: CanvasSummaryText is tracked on CanvasSummaryButton; AnnounceLiveRegion uses LiveAnnounce.Raise;
// - Node Palette: PaletteCountText is tracked; NoMatchesText is raised via LiveAnnounce.Raise;
// - SmartArt Studio: NoMatchesTitleText is tracked; CustomPaletteStatusText, OutlineMatchCountLabel, and AnnounceLiveRegion use LiveAnnounce.Raise;
// - None of the explicitly raised lines are tracked, preventing duplicate screen reader announcements.
public class Run281LiveRegionsAndStudioAnnounceTests
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

    private const string OcrStudio = "MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml.cs";
    private const string History = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml.cs";
    private const string Mermaid = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml.cs";
    private const string NodePalette = "MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml.cs";
    private const string SmartArt = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";

    // ---- Live Tracking Coverage ----

    [Theory]
    [InlineData(OcrStudio, "LiveAnnounce.Track(StatusBannerText);")]
    [InlineData(OcrStudio, "LiveAnnounce.Track(FooterStatusText);")]
    [InlineData(OcrStudio, "LiveAnnounce.Track(PageIndicatorText);")]
    [InlineData(OcrStudio, "LiveAnnounce.Track(WordStreamText);")]
    [InlineData(OcrStudio, "LiveAnnounce.Track(SelectedElementBadgeText);")]
    [InlineData(OcrStudio, "LiveAnnounce.Track(SessionMetricsText, SessionMetricsButton);")]
    [InlineData(History, "LiveAnnounce.Track(TimelineSearchMatchText);")]
    [InlineData(History, "LiveAnnounce.Track(NoMatchesTextBlock);")]
    [InlineData(History, "LiveAnnounce.Track(DiffTitleText);")]
    [InlineData(Mermaid, "LiveAnnounce.Track(CanvasSummaryText, CanvasSummaryButton);")]
    [InlineData(NodePalette, "LiveAnnounce.Track(PaletteCountText);")]
    [InlineData(SmartArt, "LiveAnnounce.Track(NoMatchesTitleText);")]
    public void ChangingStudioLines_AreTracked(string file, string expectedTrackCall)
    {
        var content = Read(file);
        Assert.Contains(expectedTrackCall, content);
    }

    // ---- Explicit Live Raises ----

    [Theory]
    [InlineData(History, "LiveAnnounce.Raise(ChangeStepperButton);")]
    [InlineData(History, "LiveAnnounce.Raise(DiffMatchCountLabel);")]
    [InlineData(Mermaid, "LiveAnnounce.Raise(element);")]
    [InlineData(NodePalette, "LiveAnnounce.Raise(NoMatchesText);")]
    [InlineData(SmartArt, "LiveAnnounce.Raise(CustomPaletteStatusText);")]
    [InlineData(SmartArt, "LiveAnnounce.Raise(OutlineMatchCountLabel);")]
    [InlineData(SmartArt, "LiveAnnounce.Raise(element);")]
    public void StudioStatusLines_AreExplicitlyRaisedViaLiveAnnounce(string file, string expectedRaiseCall)
    {
        var content = Read(file);
        Assert.Contains(expectedRaiseCall, content);
    }

    // ---- No Raw / Fragile FrameworkElementAutomationPeer.FromElement Calls Left for Live Regions ----

    [Theory]
    [InlineData(History, "FromElement(ChangeStepperButton)")]
    [InlineData(History, "FromElement(DiffMatchCountLabel)")]
    [InlineData(Mermaid, "FromElement(element)")]
    [InlineData(NodePalette, "FromElement(NoMatchesText)")]
    [InlineData(SmartArt, "FromElement(OutlineMatchCountLabel)")]
    public void FragilePeerLookups_AreReplacedByLiveAnnounce(string file, string forbiddenPattern)
    {
        var content = Read(file);
        Assert.DoesNotContain(forbiddenPattern, content);
    }

    // ---- Explicitly Raised Lines Must Not Also Be Tracked (Anti-Double-Announcement Guard) ----

    [Theory]
    [InlineData(History, "ChangeStepperLabel")]
    [InlineData(History, "ChangeStepperButton")]
    [InlineData(History, "DiffMatchCountLabel")]
    [InlineData(Mermaid, "StatusBarText")]
    [InlineData(Mermaid, "CodeStatusText")]
    [InlineData(NodePalette, "NoMatchesText")]
    [InlineData(SmartArt, "OutlineMatchCountLabel")]
    [InlineData(SmartArt, "CustomPaletteStatusText")]
    public void ExplicitlyRaisedStudioLines_AreNotAlsoTracked(string file, string name)
    {
        Assert.DoesNotContain($"LiveAnnounce.Track({name})", Read(file));
    }
}
