using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using MarkSmith.Services;
using MarkSmith.ViewModels.History;
using Xunit;

namespace MarkSmith.Tests;

// Run #227: History's header readouts (file name, selected version's timestamp, timeline filter
// pill) are buttons the keyboard reaches, the filter pill no longer opens its menu and clears
// every filter on one double-click, and the search match count is announced and clears cleanly.
public class Run227HistoryHeaderKeyboardTests : IDisposable
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_run227_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

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

    private static string HistoryCode => File.ReadAllText(Path.Combine(DesktopDir(), "Views", "History", "HistoryWindow.xaml.cs"));

    private static XDocument HistoryXaml => XDocument.Load(Path.Combine(DesktopDir(), "Views", "History", "HistoryWindow.xaml"));

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Theory]
    [InlineData("FileNameButton", "HyperlinkButton", "OnFileNameRightTapped")]
    [InlineData("SelectedHeaderButton", "HyperlinkButton", "OnTimelineHeaderRightTapped")]
    [InlineData("TimelineFilterStatusPill", "Button", "OnTimelineFilterStatusPillRightTapped")]
    public void HeaderReadouts_AreButtons_BridgedForShiftF10(string name, string type, string rightTapped)
    {
        var el = Named(HistoryXaml, name);
        Assert.Equal(type, el.Name.LocalName);
        Assert.Equal(rightTapped, (string?)el.Attribute("RightTapped"));
        Assert.Null(el.Attribute("Tapped"));
        Assert.Null(el.Attribute("DoubleTapped"));
        Assert.Contains("Shift+F10", (string?)el.Attribute("ToolTipService.ToolTip"));
        Assert.Contains("Shift+F10", (string?)el.Attribute("AutomationProperties.HelpText"));
        // Literal names: a {Binding} here never resolved and UIA read the tooltip instead.
        var spoken = (string?)el.Attribute("AutomationProperties.Name");
        Assert.False(string.IsNullOrWhiteSpace(spoken));
        if (name != "FileNameButton") Assert.DoesNotContain("{Binding", spoken);
        Assert.Contains($"({name}, {rightTapped})", HistoryCode);
    }

    [Fact]
    public void FileName_TextBlockHasNoPointerOnlyHandlers_AndClickCopiesPath()
    {
        var text = Named(HistoryXaml, "FileNameTextBlock");
        Assert.Equal("FileNameButton", (string?)text.Parent!.Attribute(X + "Name"));
        Assert.Null(text.Attribute("RightTapped"));
        Assert.Null(text.Attribute("DoubleTapped"));

        var code = HistoryCode;
        Assert.DoesNotContain("OnFileNameDoubleTapped", code);
        Assert.Contains("private void OnFileNameButtonClick(object sender, RoutedEventArgs e)", code);
        Assert.Contains("menu.ShowAt(FileNameButton,", code);
    }

    [Fact]
    public void FilterPill_ClickOpensItsMenu_DoubleClickNoLongerClearsFilters()
    {
        var pill = Named(HistoryXaml, "TimelineFilterStatusPill");
        // The menu is the button's own Flyout, so Click and Enter open it without a handler.
        Assert.Contains(pill.Elements(), e => e.Name.LocalName == "Button.Flyout");
        Assert.DoesNotContain(pill.Elements(), e => e.Name.LocalName.EndsWith(".ContextFlyout"));
        Assert.Null(pill.Attribute("Click"));
        // It drops below the pill instead of opening upward over the title bar.
        Assert.Equal("BottomEdgeAlignedRight", (string?)Named(HistoryXaml, "TimelineFilterFlyout").Attribute("Placement"));

        var code = HistoryCode;
        Assert.DoesNotContain("OnTimelineFilterStatusPillTapped", code);
        Assert.DoesNotContain("OnTimelineFilterStatusPillDoubleTapped", code);
        // The menu still offers the clear-all action the double-click used to run.
        Assert.Contains(pill.Descendants(), e => (string?)e.Attribute("Text") == "Clear All Filters");
    }

    [Fact]
    public void FilterPill_SpeaksTheUntrimmedFilter()
    {
        var code = HistoryCode;
        var body = code[code.IndexOf("private void UpdateTimelineFilterStatus()", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("AutomationProperties.SetName(TimelineFilterStatusPill, $\"Timeline filter: {spoken}\")", body);
        Assert.Contains("spoken = _vm.SearchQuery;", body);
    }

    [Fact]
    public void SelectedHeaderButton_HidesWithoutSelection_AndHandsFocusOn()
    {
        var button = Named(HistoryXaml, "SelectedHeaderButton");
        Assert.Contains("HasSelectedHeader", (string?)button.Attribute("Visibility"));
        Assert.Equal("OnSelectedHeaderButtonClick", (string?)button.Attribute("Click"));

        var code = HistoryCode;
        var guard = code.IndexOf("e.PropertyName == nameof(HistoryWindowViewModel.SelectedHeader)", StringComparison.Ordinal);
        Assert.True(guard > 0);
        // The whole branch, up to the next property (run #228 added the tooltip to it).
        var branch = code.Substring(guard, code.IndexOf("else if (e.PropertyName", guard, StringComparison.Ordinal) - guard);
        Assert.Contains("SetName(SelectedHeaderButton", branch);
        Assert.Contains("TimelineFilterStatusPill.Focus(FocusState.Programmatic)", branch);
    }

    [Fact]
    public void SearchMatchPill_AnnouncesCount_NamesClear_AndFocusesBeforeCollapse()
    {
        var doc = HistoryXaml;
        Assert.Equal("Polite", (string?)Named(doc, "TimelineSearchMatchText").Attribute("AutomationProperties.LiveSetting"));
        Assert.Equal("Clear search filter", (string?)Named(doc, "TimelineClearSearchBtn").Attribute("AutomationProperties.Name"));
        // The pill itself isn't clickable; its tooltip no longer says it is.
        Assert.DoesNotContain("Click to clear", (string?)Named(doc, "TimelineSearchMatchPill").Attribute("ToolTipService.ToolTip"));

        var code = HistoryCode;
        var body = code[code.IndexOf("private void OnTimelineClearSearchClick", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.True(body.IndexOf("SearchBox.Focus", StringComparison.Ordinal) < body.IndexOf("_vm.SearchQuery = string.Empty", StringComparison.Ordinal));
    }

    [Fact]
    public void DeltaMeter_NameIsSetInCode_NotByBinding()
    {
        Assert.Equal("Line delta", (string?)Named(HistoryXaml, "DeltaMeterButton").Attribute("AutomationProperties.Name"));
        Assert.Contains("SetName(DeltaMeterButton,", HistoryCode);
    }

    [Fact]
    public async Task HasSelectedHeader_FollowsTheSelection()
    {
        var service = new VersionHistoryService(Path.Combine(_dir, "history"));
        var file = Path.Combine(_dir, "doc.md");
        await service.CaptureAsync(file, "# one", "opened");
        await Task.Delay(20);
        await service.CaptureAsync(file, "# two", "snapshot");
        var vm = new HistoryWindowViewModel(_ => "<html/>", _ => Task.FromResult(true), service, initialFilePath: file);

        var raised = 0;
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HistoryWindowViewModel.HasSelectedHeader)) raised++;
        };
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Selected);
        Assert.True(vm.HasSelectedHeader);
        Assert.Equal(!string.IsNullOrWhiteSpace(vm.SelectedHeader), vm.HasSelectedHeader);
        Assert.True(raised > 0);
    }
}
