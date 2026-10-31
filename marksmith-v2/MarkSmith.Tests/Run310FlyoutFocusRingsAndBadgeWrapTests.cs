using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #310:
// - Suite Hub: the version / plan / health badges wrap one by one again (an inner one-line
//   StackPanel had brought back the squeezed last badge);
// - Markdown issues, Recent exports and Document outline flyouts opened from the keyboard show the
//   focus ring on the row they land on (Programmatic focus draws none); a mouse open still doesn't;
// - Version History: "Try again" has a name and tooltip, and when it disables or hides itself
//   (the load ran / worked) keyboard focus lands on the documents list's Refresh, not the window's
//   first tab stop;
// - Mermaid shape palette: each category pill says what it shows and that it has a menu.
public class Run310FlyoutFocusRingsAndBadgeWrapTests
{
    private const string Main = "MarkSmith.Desktop/MainWindow.xaml.cs";
    private const string MainXaml = "MarkSmith.Desktop/MainWindow.xaml";
    private const string Hub = "MarkSmith.Desktop/Views/SuiteHubView.xaml";
    private const string History = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml";
    private const string Palette = "MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml";
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

    private static XElement Named(string relative, string name) =>
        XDocument.Parse(Read(relative)).Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"missing: {start}");
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"missing: {end}");
        return text.Substring(a, b - a);
    }

    [Fact]
    public void SuiteHub_BadgeCluster_IsAWrapRow_SoEachBadgeWraps()
    {
        var cluster = Named(Hub, "SuiteBadgesCluster");
        Assert.Equal("WrapRow", cluster.Name.LocalName);
        // Still one tab stop with arrow keys between the badges.
        Assert.Equal("Enabled", (string?)cluster.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cluster.Attribute("TabFocusNavigation"));
        Assert.Equal("WrapRow", cluster.Parent!.Name.LocalName);
    }

    [Fact]
    public void RingIfKeyboard_OnlyKeyboardFocusedOpenersGetARing()
    {
        var code = Read(Main);
        Assert.Contains("private static FocusState RingIfKeyboard(object? opener) =>\n        opener is Control { FocusState: FocusState.Keyboard } ? FocusState.Keyboard : FocusState.Programmatic;", code);
    }

    [Fact]
    public void LintFlyout_CapturesTheOpenerBeforeFocusMoves_AndFocusesWithIt()
    {
        var flyout = Named(MainXaml, "LintFlyout");
        Assert.Equal("OnLintFlyoutOpening", (string?)flyout.Attribute("Opening"));
        var code = Read(Main);
        Assert.Contains("private void OnLintFlyoutOpening(object? sender, object e) => _lintListOpenFocus = RingIfKeyboard(LintButton);", code);
        var opened = Between(code, "private void OnLintFlyoutOpened(", "private void InsertSequentialFootnote(");
        Assert.Contains("LintList?.Focus(_lintListOpenFocus);", opened);
        Assert.DoesNotContain("FocusState.Programmatic", opened);
    }

    [Fact]
    public void OutlineFlyout_ListOnlyOpen_UsesTheOpenerFocusState()
    {
        var flyout = Named(MainXaml, "OutlineFlyout");
        Assert.Equal("OnOutlineFlyoutOpening", (string?)flyout.Attribute("Opening"));
        var code = Read(Main);
        Assert.Contains("private void OnOutlineFlyoutOpening(object? sender, object e) => _tocListOpenFocus = RingIfKeyboard(OutlineButton);", code);
        var opened = Between(code, "private void OnOutlineFlyoutOpened(", "private void OnTocListContainerContentChanging(");
        Assert.Contains("TocList.Focus(_tocListOpenFocus);", opened);
        // The filter box keeps Programmatic focus: a text box shows its caret either way.
        Assert.Contains("TocFilterBox.Focus(FocusState.Programmatic);", opened);
    }

    [Fact]
    public void ExportHistory_MenuOrPaletteOpen_RingsTheFirstRow_ThenResets()
    {
        var code = Read(Main);
        var click = Between(code, "private void OnExportHistoryMenuClick(", "private void OnExportHistoryFlyoutOpened(");
        Assert.Contains("_historyListOpenFocus = ReferenceEquals(sender, this) ? FocusState.Keyboard : RingIfKeyboard(sender);", click);
        // The command palette is what passes the window.
        Assert.Contains("Do(\"Recent exports\", \"Export\", () => OnExportHistoryMenuClick(this, click)),", code);
        var opened = Between(code, "private void OnExportHistoryFlyoutOpened(", "private async void MarkMissingExportFiles(");
        Assert.Contains("HistoryList?.Focus(_historyListOpenFocus);", opened);
        Assert.Contains("_historyListOpenFocus = FocusState.Programmatic;", opened);
    }

    [Fact]
    public void HistoryRetry_IsNamed_AndHandsFocusToRefresh()
    {
        var retry = Named(History, "RetryLoadButton");
        Assert.Equal("Try again", (string?)retry.Attribute("Content"));
        Assert.Equal("Try loading version history again", (string?)retry.Attribute("AutomationProperties.Name"));
        Assert.Contains("(F5)", (string?)retry.Attribute("ToolTipService.ToolTip"));
        var code = Read(History + ".cs");
        Assert.Contains("FocusRescue.Attach(() => DocumentsRefreshButton, RetryLoadButton);", code);
    }

    [Theory]
    [InlineData("CatAll", "Show every shape · Right-click or Shift+F10 for options")]
    [InlineData("CatFlowchart", "Show only Flowchart shapes · Right-click or Shift+F10 for options")]
    [InlineData("CatMindmap", "Show only Mindmap shapes · Right-click or Shift+F10 for options")]
    public void PaletteCategoryPills_SayWhatTheyShow(string pill, string tip)
    {
        Assert.Equal(tip, (string?)Named(Palette, pill).Attribute("ToolTipService.ToolTip"));
    }

    [Fact]
    public void PaletteCategoryPills_AllHaveTooltips_AndKeepTheirMenuBridge()
    {
        var pills = XDocument.Parse(Read(Palette)).Descendants()
            .Where(e => e.Name.LocalName == "ToggleButton" && ((string?)e.Attribute(X + "Name"))?.StartsWith("Cat") == true)
            .ToList();
        Assert.Equal(8, pills.Count);
        Assert.All(pills, p => Assert.Contains("Shift+F10", (string?)p.Attribute("ToolTipService.ToolTip") ?? ""));
        var code = Read(Palette + ".cs");
        Assert.All(pills, p => Assert.Contains($"({(string?)p.Attribute(X + "Name")}, OnCategoryPillRightTapped)", code));
    }
}
