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

// Run #228: History's diff toolbar wraps instead of clipping its right end, the version-ID pill
// and the hunk counter are buttons the keyboard reaches (one action per click), the hunk counter
// and diff-search count speak whole sentences, and the timeline header shows a compact timestamp.
public class Run228HistoryDiffToolbarTests : IDisposable
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_run228_" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void DiffToolbar_ControlsFlowOntoWrappingLines()
    {
        var doc = HistoryXaml;
        var wrap = doc.Descendants().Single(e => e.Name.LocalName == "WrapRow");
        Assert.Equal("1", (string?)wrap.Attribute("Grid.Row"));
        // Always shown: the view switcher lives here, and collapsed children take no space anyway.
        Assert.Null(wrap.Attribute("Visibility"));

        foreach (var name in new[] { "UnifiedToggle", "DiffChangeStepperPill", "DiffSearchPill", "DeltaMeterButton",
                     "DiffWordDeltaPill", "DiffWrapButton", "DiffLineJumpButton", "DiffIgnoreWhitespaceButton" })
        {
            var el = Named(doc, name);
            Assert.True(el.Ancestors().Contains(wrap), $"{name} should flow inside the WrapRow");
            Assert.Null(el.Attribute("Grid.Column"));
        }

        // The title and Copy keep the first line; the title's column takes the leftover width.
        var toolbar = (XElement)wrap.Parent!;
        var widths = toolbar.Elements().Single(e => e.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements().Select(c => (string?)c.Attribute("Width")).ToArray();
        Assert.Equal(new[] { "*", "Auto" }, widths);
        Assert.Equal("1", (string?)Named(doc, "CopyToolbarButton").Attribute("Grid.Column"));
        Assert.False(Named(doc, "CopyToolbarButton").Ancestors().Contains(wrap));
    }

    [Fact]
    public void WrapRow_SkipsCollapsedChildren_AndWrapsOnOverflow()
    {
        var code = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "WrapRow.cs"));
        Assert.Contains("class WrapRow : Panel", code);
        Assert.Contains("Visibility.Collapsed", code);
        Assert.Contains("needed > maxWidth", code);
        Assert.Contains("x + HorizontalSpacing + size.Width > finalSize.Width", code);
    }

    [Fact]
    public void DiffTitle_TrimsInAGrid_SoTheVersionIdStaysVisible()
    {
        var title = Named(HistoryXaml, "DiffTitleText");
        var row = title.Parent!;
        Assert.Equal("Grid", row.Name.LocalName);
        Assert.Equal("CharacterEllipsis", (string?)title.Attribute("TextTrimming"));
        Assert.Equal("1", (string?)Named(HistoryXaml, "CommitShaCopyPill").Attribute("Grid.Column"));
    }

    [Fact]
    public void VersionIdPill_IsAButton_BridgedToTheSummaryMenu()
    {
        var pill = Named(HistoryXaml, "CommitShaCopyPill");
        Assert.Equal("Button", pill.Name.LocalName);
        Assert.Equal("OnCommitShaCopyPillClick", (string?)pill.Attribute("Click"));
        Assert.Null(pill.Attribute("Tapped"));
        Assert.Equal("Copy version ID", (string?)pill.Attribute("AutomationProperties.Name"));
        Assert.Contains("Shift+F10", (string?)pill.Attribute("ToolTipService.ToolTip"));
        Assert.DoesNotContain("SHA", (string?)pill.Attribute("ToolTipService.ToolTip"));

        var code = HistoryCode;
        Assert.Contains("(CommitShaCopyPill, OnDiffSummaryRightTapped)", code);
        Assert.DoesNotContain("OnCommitShaCopyPillTapped", code);
        Assert.Contains("Copied version ID:", code);
    }

    [Fact]
    public void DoubleClickOnVersionId_DoesNotAlsoCopyTheTitle()
    {
        var code = HistoryCode;
        var start = code.IndexOf("private void OnDiffSummaryDoubleTapped", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = code.Substring(start, code.IndexOf("ClipboardWriter.Set", start, StringComparison.Ordinal) - start);
        Assert.Contains("IsWithin(e.OriginalSource as DependencyObject, CommitShaCopyPill)) return;", body);
    }

    [Fact]
    public void HunkCounter_IsAButton_OneActionPerClick_ShiftF10Bridged()
    {
        var doc = HistoryXaml;
        var button = Named(doc, "ChangeStepperButton");
        Assert.Equal("Button", button.Name.LocalName);
        Assert.Equal("OnChangeStepperButtonClick", (string?)button.Attribute("Click"));
        Assert.Null(button.Attribute("DoubleTapped"));
        Assert.Equal("Polite", (string?)button.Attribute("AutomationProperties.LiveSetting"));
        Assert.Contains("Shift+F10", (string?)button.Attribute("AutomationProperties.HelpText"));
        Assert.Equal("TextBlock", Named(doc, "ChangeStepperLabel").Name.LocalName);
        Assert.Null(Named(doc, "ChangeStepperLabel").Attribute("Tapped"));

        var code = HistoryCode;
        Assert.DoesNotContain("OnChangeStepperLabelTapped", code);
        Assert.DoesNotContain("OnChangeStepperLabelDoubleTapped", code);
        Assert.DoesNotContain("double-click for first", code);
        // Bridged so the window's own Shift+F10 handler (the version menu) never sees it.
        Assert.Contains("(ChangeStepperButton, OnChangeStepperButtonRightTapped)", code);
        Assert.Contains("DiffContextFlyout?.ShowAt(DiffChangeStepperPill", code);
    }

    [Fact]
    public void CollapsingDiffTools_HandFocusToTheViewSwitcher()
    {
        var code = HistoryCode;
        Assert.Contains("if (!_vm.HasDiffStats) HandFocusOffCollapsingDiffTools();", code);
        var start = code.IndexOf("private void HandFocusOffCollapsingDiffTools()", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = code.Substring(start, code.IndexOf("private static bool IsWithin", start, StringComparison.Ordinal) - start);
        foreach (var name in new[] { "DiffChangeStepperPill", "CommitShaCopyPill", "CopyToolbarButton", "DiffSearchPill" })
            Assert.Contains(name, body);
        Assert.Contains("target.Focus(FocusState.Programmatic)", body);
    }

    [Fact]
    public void HunkCounter_SpeaksWholeSentences_ThroughOneHelper()
    {
        var code = HistoryCode;
        Assert.Contains("SetName(ChangeStepperButton, spoken)", code);
        Assert.Contains("$\"Change hunk {_currentHunkIndex + 1} of {hunks.Count}, line {targetRow + 1}\"", code);
        // Every text change goes through SetChangeStepper, so the name never lags the label.
        Assert.Equal(1, CountOf(code, "ChangeStepperLabel.Text ="));
    }

    [Fact]
    public void DiffSearch_IsNamed_AndItsCountIsSpoken()
    {
        var doc = HistoryXaml;
        Assert.Equal("Find in diff", (string?)Named(doc, "DiffSearchBox").Attribute("AutomationProperties.Name"));
        Assert.Contains("F3", (string?)Named(doc, "DiffSearchBox").Attribute("AutomationProperties.HelpText"));
        Assert.Equal("Polite", (string?)Named(doc, "DiffMatchCountLabel").Attribute("AutomationProperties.LiveSetting"));

        var code = HistoryCode;
        Assert.Equal(1, CountOf(code, "DiffMatchCountLabel.Text ="));
        Assert.Contains("$\"No matches for {query}\"", code);
        Assert.Contains("$\"Match 1 of {_diffSearchMatches.Count}\"", code);
    }

    [Fact]
    public void TimelineHeader_ShowsTheCompactTimestamp_FullOneInTooltip()
    {
        var button = Named(HistoryXaml, "SelectedHeaderButton");
        var text = button.Descendants().Single(e => e.Name.LocalName == "TextBlock");
        Assert.Equal("{Binding SelectedHeaderCompact}", (string?)text.Attribute("Text"));
        Assert.Contains("SetToolTip(SelectedHeaderButton", HistoryCode);
    }

    [Fact]
    public async Task SelectedHeaderCompact_IsShorter_AndClearsWithTheSelection()
    {
        var service = new VersionHistoryService(Path.Combine(_dir, "history"));
        var file = Path.Combine(_dir, "doc.md");
        await service.CaptureAsync(file, "# one", "opened");
        await Task.Delay(20);
        await service.CaptureAsync(file, "# two", "snapshot");
        var vm = new HistoryWindowViewModel(_ => "<html/>", _ => Task.FromResult(true), service, initialFilePath: file);

        string? compactWhenHeaderRaised = null;
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HistoryWindowViewModel.SelectedHeader)) compactWhenHeaderRaised = vm.SelectedHeaderCompact;
        };
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Selected);
        var t = vm.Selected!.Entry.CreatedAt.LocalDateTime;
        Assert.Equal(t.ToString("ddd d MMM yyyy") + " · " + t.ToString("HH:mm"), vm.SelectedHeaderCompact);
        Assert.True(vm.SelectedHeaderCompact.Length < vm.SelectedHeader.Length);
        // Set before SelectedHeader, so the header's change handler sees the matching compact form.
        Assert.Equal(vm.SelectedHeaderCompact, compactWhenHeaderRaised);

        vm.SelectVersion(null);
        Assert.Equal("", vm.SelectedHeaderCompact);
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
