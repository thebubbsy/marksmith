using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #221: the status bar no longer overflows under the status message at the default window
// width, the Suite Hub's recent-documents strip tells "no history" apart from "no matches", and a
// few title-bar controls get real accessible names instead of their long tooltips.
public class Run221StatusBarAndHubPolishTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string DesktopPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName, "MarkSmith.Desktop" }.Concat(parts).ToArray());
    }

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    // ── StatusBarLayout ────────────────────────────────────────────────────────

    [Fact]
    public void Fold_WhenFullRowLeavesTooLittleRoomOnTheLeft()
    {
        // 1220px launch width: the full pill row measured ~1180px.
        Assert.True(StatusBarLayout.ShouldFoldSecondaryPills(1188, 1180));
        // Maximised on 1920: plenty of room.
        Assert.False(StatusBarLayout.ShouldFoldSecondaryPills(1904, 1180));
        // Boundary: exactly the minimum left width still fits.
        Assert.False(StatusBarLayout.ShouldFoldSecondaryPills(1180 + StatusBarLayout.MinimumLeftWidth, 1180));
    }

    [Fact]
    public void Fold_NeverDecidesBeforeLayout()
    {
        Assert.False(StatusBarLayout.ShouldFoldSecondaryPills(0, 1180));
        Assert.False(StatusBarLayout.ShouldFoldSecondaryPills(1200, 0));
    }

    [Fact]
    public void MessageWidth_IsClampedBetweenReadableAndMaximum()
    {
        Assert.Equal(StatusBarLayout.MaximumMessageWidth, StatusBarLayout.MessageMaxWidth(3000, 600, 60));
        Assert.Equal(StatusBarLayout.MinimumMessageWidth, StatusBarLayout.MessageMaxWidth(500, 480, 60));
        Assert.Equal(1188 - 560 - 60 - 12, StatusBarLayout.MessageMaxWidth(1188, 560, 60));
    }

    // ── Recent documents empty state ──────────────────────────────────────────

    [Fact]
    public void RecentEmptyState_NamesTheFilter_WhenHistoryExists()
    {
        var text = RecentFilesService.FormatRecentFilesEmptyState(5, "  report ");
        Assert.Contains("“report”", text);
        Assert.Contains("Esc", text);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0, "report")]
    [InlineData(4, "")]
    public void RecentEmptyState_SaysNothingYet_WithoutHistoryOrFilter(int total, string? filter)
    {
        Assert.StartsWith("No recent documents yet", RecentFilesService.FormatRecentFilesEmptyState(total, filter));
    }

    // ── XAML / code-behind contracts ──────────────────────────────────────────

    [Fact]
    public void StatusBar_SecondaryCopyPills_AreGroupedBehindAMoreButton()
    {
        var doc = XDocument.Load(DesktopPath("MainWindow.xaml"));
        var group = Named(doc, "StatusSecondaryCopyPills");
        var names = group.Descendants().Select(e => (string?)e.Attribute(X + "Name")).Where(n => n is not null).ToList();
        foreach (var pill in new[] { "Code", "Tables", "Tasks", "Math", "Callouts", "Images", "Footnotes", "Diagrams" })
        {
            Assert.Contains($"StatusCopy{pill}Button", names);
        }
        // The primary pills stay outside the group so they never fold away.
        Assert.DoesNotContain("StatusCopyMarkdownButton", names);
        Assert.DoesNotContain("StatusCopyLinksButton", names);

        var more = Named(doc, "StatusMoreCopyButton");
        Assert.Equal("Collapsed", (string?)more.Attribute("Visibility"));
        Assert.Equal("More copy actions", (string?)more.Attribute("AutomationProperties.Name"));

        var code = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("StatusBarLayout.ShouldFoldSecondaryPills", code);
        Assert.Contains("StatusMoreCopyButton.Focus(FocusState.Keyboard)", code); // focus never stranded on a collapsed pill
        foreach (var pill in new[] { "Code", "Tables", "Tasks", "Math", "Callouts", "Images", "Footnotes", "Diagrams" })
        {
            Assert.Contains($"OnStatusCopy{pill}ButtonClick);", code);
        }
    }

    [Theory]
    [InlineData("SettingsTitleButton", "Settings")]
    [InlineData("FocusModeToggle", "Focus mode")]
    [InlineData("LookingGlassToggle", "Looking Glass portal mode")]
    [InlineData("OutputFolderTextBox", "Output folder")]
    public void TitleBarAndOutputControls_HaveShortAccessibleNames(string control, string expected)
    {
        var doc = XDocument.Load(DesktopPath("MainWindow.xaml"));
        Assert.Equal(expected, (string?)Named(doc, control).Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void SettingsTooltip_HasNoDoubledComma()
    {
        var xaml = File.ReadAllText(DesktopPath("MainWindow.xaml"));
        Assert.DoesNotContain("Ctrl+,,", xaml);
    }

    [Fact]
    public void EditorStepHeader_ShowsFullTextWhenTrimmed()
    {
        var code = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("TrimmedTextToolTip.Attach(EditorStepTitle, EditorStepCaption", code);
        var helper = File.ReadAllText(DesktopPath("Services", "TrimmedTextToolTip.cs"));
        Assert.Contains("IsTextTrimmedChanged", helper);
        Assert.Contains("TextBlock.TextProperty", helper);
    }

    [Fact]
    public void SuiteHub_FilterBoxes_HaveUnclippedPlaceholdersAndNames()
    {
        var doc = XDocument.Load(DesktopPath("Views", "SuiteHubView.xaml"));
        var recent = Named(doc, "RecentSearchBox");
        Assert.Equal("Filter…", (string?)recent.Attribute("PlaceholderText"));
        var templates = Named(doc, "TemplateSearchBox");
        Assert.Equal("Filter…", (string?)templates.Attribute("PlaceholderText"));
        Assert.Equal("Filter starter templates", (string?)templates.Attribute("AutomationProperties.Name"));
        Assert.NotNull(Named(doc, "StarterTemplatesScroller"));
    }

    [Fact]
    public void SuiteHub_RecentStrip_HidesFilterChromeFocusSafely()
    {
        var code = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("RecentFilesService.FormatRecentFilesEmptyState(existingFiles.Count, _recentDocsFilter)", code);
        Assert.Contains("SetRecentFilterChromeVisible(existingFiles.Count > 0)", code);
        Assert.Contains("RecentDocsActionBtn.Focus(FocusState.Programmatic)", code);
        Assert.Contains("new PointerEventHandler(OnStarterTemplatesWheel), handledEventsToo: true", code);
        // Esc-to-clear only works when the handlers also see keys the inner TextBox handled.
        Assert.Contains("new KeyEventHandler(OnRecentSearchBoxKeyDown), handledEventsToo: true", code);
        Assert.Contains("new KeyEventHandler(OnTemplateSearchKeyDown), handledEventsToo: true", code);
    }

    [Fact]
    public void History_EmptyTimeline_NoLongerPointsAtAnEmptyList()
    {
        var xaml = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml"));
        Assert.DoesNotContain("Pick a document on the left", xaml);
        Assert.Contains("{Binding DiffPlaceholderText}", xaml);
    }
}
