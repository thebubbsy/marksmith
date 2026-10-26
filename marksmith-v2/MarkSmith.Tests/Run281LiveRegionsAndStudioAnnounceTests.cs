using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #281: Sweeping remaining XAML-declared live regions across OCR Studio, History Window,
// Mermaid Diagram Studio, Node Palette, and SmartArt Studio into LiveAnnounce.Track or LiveAnnounce.Raise.
// - OCR Studio: PageIndicatorText, WordStreamText, SelectedElementBadgeText, and SessionMetricsText are tracked;
// - History Window: TimelineSearchMatchText and NoMatchesTextBlock are tracked (DiffTitleText is quiet since run #288); ChangeStepperButton and DiffMatchCountLabel are reliably raised via LiveAnnounce.Raise;
// - Mermaid Studio: CanvasSummaryText is quiet since run #288 (the status line reads each change); AnnounceLiveRegion uses LiveAnnounce.Raise;
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
    [InlineData(History, "LiveAnnounce.Track(TimelineSearchMatchText);")]
    [InlineData(NodePalette, "LiveAnnounce.Track(PaletteCountText);")]
    [InlineData(SmartArt, "LiveAnnounce.Track(CustomPaletteStatusText);")]
    public void ChangingStudioLines_AreTracked(string file, string expectedTrackCall)
    {
        var content = Read(file);
        Assert.Contains(expectedTrackCall, content);
    }

    // ---- Explicit Live Raises ----

    [Theory]
    [InlineData(OcrStudio, "LiveAnnounce.Raise(PageIndicatorText)")]
    [InlineData(OcrStudio, "LiveAnnounce.Raise(SelectedElementBadgeText);")]
    [InlineData(History, "LiveAnnounce.Raise(ChangeStepperButton);")]
    [InlineData(History, "LiveAnnounce.Raise(DiffMatchCountLabel);")]
    [InlineData(Mermaid, "LiveAnnounce.Raise(element);")]
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
    [InlineData(SmartArt, "FromElement(OutlineMatchCountLabel)")]
    public void FragilePeerLookups_AreReplacedByLiveAnnounce(string file, string forbiddenPattern)
    {
        var content = Read(file);
        Assert.DoesNotContain(forbiddenPattern, content);
    }

    // ---- Explicitly Raised Lines Must Not Also Be Tracked (Anti-Double-Announcement Guard) ----

    [Theory]
    [InlineData(OcrStudio, "PageIndicatorText")]
    [InlineData(OcrStudio, "SelectedElementBadgeText")]
    [InlineData(History, "ChangeStepperLabel")]
    [InlineData(History, "ChangeStepperButton")]
    [InlineData(History, "DiffMatchCountLabel")]
    [InlineData(Mermaid, "StatusBarText")]
    [InlineData(Mermaid, "CodeStatusText")]
    [InlineData(NodePalette, "NoMatchesText")]
    [InlineData(SmartArt, "OutlineMatchCountLabel")]
    public void ExplicitlyRaisedStudioLines_AreNotAlsoTracked(string file, string name)
    {
        Assert.DoesNotContain($"LiveAnnounce.Track({name})", Read(file));
    }
}
