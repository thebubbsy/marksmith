using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #288:
// - Diagram Studio: the nudge accelerators claimed every arrow key nothing else had taken, so an
//   arrow-navigated pill group (and a combo box or slider leaving an arrow unhandled) lost it;
//   focus in a list, combo box, slider or pill group now keeps its arrows, the canvas still nudges;
// - the shape palette's category pills, Version History's Unified / Side by side / Preview
//   switch and SmartArt Studio's harmony modes are arrow-navigated groups with one Tab stop;
// - OCR studio's session metrics read in sentence case ("1 page · 3 elements · 120 words")
//   and the header counts group thousands;
// - Diagram Studio's canvas summary and History's diff title are no longer marked live without
//   ever being raised (each change is already read out by the status line / timeline).
public class Run288ArrowKeysAndQuietLinesTests
{
    private const string Studio = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml.cs";
    private const string StudioXaml = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml";
    private const string PaletteXaml = "MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml";
    private const string HistoryXaml = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml";
    private const string SmartArtXaml = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml";
    private const string Ocr = "MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml.cs";
    private const string OcrXaml = "MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml";

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

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

    private static XElement Named(string xamlFile, string name) =>
        XDocument.Parse(Read(xamlFile)).Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"missing: {start}");
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"missing: {end}");
        return text.Substring(a, b - a);
    }

    // ---- Diagram Studio: arrows belong to the focused list / combo / slider / pill group ----

    [Fact]
    public void Nudge_SkipsWhenFocusOwnsItsArrowKeys()
    {
        var nudge = Between(Read(Studio), "private void OnNudgeAcceleratorInvoked", "private void OnZoomInAcceleratorInvoked");
        Assert.Contains("FocusOwnsArrowKeys()", nudge);
        // The check comes before the nudge itself and before marking the key handled.
        Assert.True(nudge.IndexOf("FocusOwnsArrowKeys()", StringComparison.Ordinal)
                    < nudge.IndexOf("NudgeSelected(", StringComparison.Ordinal));
    }

    [Fact]
    public void FocusOwnsArrowKeys_CoversListsCombosSlidersAndPillGroups_ButNotTheCanvas()
    {
        var body = Between(Read(Studio), "private bool FocusOwnsArrowKeys()", "private bool IsFocusWithinCanvas()");
        Assert.Contains("ListViewBase", body);
        Assert.Contains("ListViewItem", body);
        Assert.Contains("ComboBox", body);
        Assert.Contains("Slider", body);
        Assert.Contains("XYFocusKeyboardNavigationMode.Enabled", body);
        // The canvas keeps nudging even if it sits inside one of those (a tab host, say): the walk
        // stops at CanvasContainer first.
        Assert.True(body.IndexOf("ReferenceEquals(cur, CanvasContainer)) return false", StringComparison.Ordinal)
                    < body.IndexOf("ListViewBase", StringComparison.Ordinal));
    }

    // ---- Segmented choices: arrows between options, one Tab stop ----

    [Theory]
    [InlineData(HistoryXaml, "DiffModeSegment", "UnifiedToggle")]
    [InlineData(SmartArtXaml, "HarmonyModeSegment", "HarmonyMonoBtn")]
    public void SegmentedToggles_AreOneArrowNavigatedTabStop(string file, string panel, string firstToggle)
    {
        var host = Named(file, panel);
        Assert.Equal("Enabled", (string?)host.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)host.Attribute("TabFocusNavigation"));
        Assert.Contains(host.Elements(), e => (string?)e.Attribute(X + "Name") == firstToggle);
    }

    [Fact]
    public void PaletteCategoryPills_AreOneArrowNavigatedTabStop()
    {
        var host = Named(PaletteXaml, "CatAll").Parent!;
        Assert.Equal("VariableSizedWrapGrid", host.Name.LocalName);
        Assert.Equal("Enabled", (string?)host.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)host.Attribute("TabFocusNavigation"));
        Assert.Equal(8, host.Elements().Count(e => e.Name.LocalName == "ToggleButton"));
    }

    // ---- OCR: sentence-case metrics ----

    [Fact]
    public void OcrMetrics_AreSentenceCase()
    {
        var telemetry = Between(Read(Ocr), "private void UpdateSessionTelemetry()", "SessionMetricsText.Text = metrics;");
        Assert.Contains("\"page\")", telemetry);
        Assert.Contains("\"element\")", telemetry);
        Assert.Contains("\"word\")", telemetry);
        Assert.Contains("} reclassified\"", telemetry);
        Assert.DoesNotContain("\"Page\"", telemetry);
        Assert.DoesNotContain("Reclassified\"", telemetry);
        Assert.Equal("1 page · 0 elements · 0 words", (string?)Named(OcrXaml, "SessionMetricsText").Attribute("Text"));
    }

    [Fact]
    public void OcrHeaderCounts_GroupThousands()
    {
        var telemetry = Between(Read(Ocr), "private void UpdateSessionTelemetry()", "SessionMetricsText.Text = metrics;");
        Assert.Contains("_session.TotalElements.ToString(\"N0\")", telemetry);
        Assert.Contains("_session.TotalReclassifications.ToString(\"N0\")", telemetry);
        Assert.DoesNotContain("TotalElements.ToString();", telemetry);
    }

    [Theory]
    [InlineData(1, "page", "1 page")]
    [InlineData(3, "element", "3 elements")]
    [InlineData(1200, "word", "1,200 words")]
    public void CountText_LowercaseNouns(int n, string noun, string expected)
    {
        Assert.Equal(expected, CountText.Of(n, noun));
    }

    // ---- Lines that changed silently are no longer marked live ----

    [Theory]
    [InlineData(StudioXaml, "CanvasSummaryText")]
    [InlineData(HistoryXaml, "DiffTitleText")]
    public void SilentLines_AreNotMarkedLive(string file, string name)
    {
        Assert.Null(Named(file, name).Attribute("AutomationProperties.LiveSetting"));
    }
}
