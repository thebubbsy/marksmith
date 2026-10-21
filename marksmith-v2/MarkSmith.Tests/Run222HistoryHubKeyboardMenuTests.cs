using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using MarkSmith.Services;
using MarkSmith.ViewModels.History;
using Xunit;

namespace MarkSmith.Tests;

// Run #222: History's empty state stops talking about a selection or "the editor's current text",
// Take Checkpoint explains itself instead of doing nothing, the timeline jump arrows disable with
// nothing to jump to, Suite Hub's Quick Start strip shows when there is more to scroll, and the
// status-bar pills' right-click menus open from Shift+F10 / the Menu key.
public class Run222HistoryHubKeyboardMenuTests : IDisposable
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_run222_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private HistoryWindowViewModel NewVm(VersionHistoryService service, string file, Func<string, string?>? editor = null) =>
        new(_ => "<html/>", _ => Task.FromResult(true), service, initialFilePath: file, editorTextFor: editor);

    private VersionHistoryService NewService() => new(Path.Combine(_dir, "history"));

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

    // ── History empty state ───────────────────────────────────────────────

    [Fact]
    public async Task EmptyHistory_DiffTitleSaysSo_InsteadOfAskingForASelection()
    {
        var vm = NewVm(NewService(), "");
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(HistoryWindowViewModel.NoHistoryDiffTitle, vm.DiffTitle);
    }

    [Fact]
    public async Task EmptyHistory_CheckpointTooltip_DoesNotPromiseTheEditorsText()
    {
        var vm = NewVm(NewService(), "", _ => "some text");
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(HistoryWindowViewModel.NoDocumentCheckpointTooltip, vm.CheckpointTooltip);
        Assert.False(vm.IsSelectedFileOpen);
    }

    [Fact]
    public async Task EmptyHistory_TakeCheckpoint_ExplainsInsteadOfDoingNothing()
    {
        var vm = NewVm(NewService(), "", _ => "some text");
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.TakeSnapshotCommand.ExecuteAsync(null);
        Assert.Equal(HistoryWindowViewModel.NoDocumentCheckpointNotice, vm.Notice);
        Assert.True(vm.NoticeIsError);
        // It must not echo the placeholder file name ("Open No document selected in the editor…").
        Assert.DoesNotContain("No document selected", vm.Notice);
    }

    [Fact]
    public async Task WithHistory_DiffTitleAndTooltip_AreUnchanged()
    {
        var service = NewService();
        const string file = @"C:\docs\r222.md";
        await service.CaptureAsync(file, "one", "opened");
        var vm = NewVm(service, file, _ => "one");
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.NotEqual(HistoryWindowViewModel.NoHistoryDiffTitle, vm.DiffTitle);
        Assert.StartsWith("Save the editor's current text", vm.CheckpointTooltip);
    }

    [Fact]
    public async Task JumpArrows_FollowShowTimeline()
    {
        var empty = NewVm(NewService(), "");
        await empty.LoadCommand.ExecuteAsync(null);
        Assert.False(empty.ShowTimeline);

        var xaml = XDocument.Load(DesktopPath("Views", "History", "HistoryWindow.xaml"));
        foreach (var name in new[] { "JumpLatestButton", "JumpOldestButton" })
            Assert.Equal("{Binding ShowTimeline}", (string?)Named(xaml, name).Attribute("IsEnabled"));

        // Disabling them must not strand keyboard focus.
        var code = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("nameof(HistoryWindowViewModel.ShowTimeline) && !_vm.ShowTimeline", code);
        Assert.Contains("SearchBox.Focus(FocusState.Programmatic)", code);
    }

    // ── Suite Hub Quick Start chevrons ───────────────────────────────────────

    [Fact]
    public void QuickStartChevrons_FadeInsteadOfCollapsing_AndAreNotTabStops()
    {
        var xaml = XDocument.Load(DesktopPath("Views", "SuiteHubView.xaml"));
        foreach (var name in new[] { "StarterScrollLeftButton", "StarterScrollRightButton" })
        {
            var b = Named(xaml, name);
            Assert.Equal("0", (string?)b.Attribute("Opacity"));
            Assert.Equal("False", (string?)b.Attribute("IsTabStop"));
            Assert.Null(b.Attribute("Visibility"));
            Assert.False(string.IsNullOrWhiteSpace((string?)b.Attribute("AutomationProperties.Name")));
        }
        var scroller = Named(xaml, "StarterTemplatesScroller");
        Assert.Equal("OnStarterTemplatesViewChanged", (string?)scroller.Attribute("ViewChanged"));
        // The filter changes the extent without moving the offset, so the panel's size is watched too.
        Assert.Equal("OnStarterTemplatesSizeChanged", (string?)Named(xaml, "StarterTemplatesPanel").Attribute("SizeChanged"));

        var code = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("private void UpdateStarterScrollChevrons()", code);
        Assert.Contains("IsHitTestVisible = canLeft", code);
        Assert.Contains("IsHitTestVisible = canRight", code);
    }

    // ── Keyboard context menus for status-bar pills ───────────────────────────

    [Fact]
    public void EveryStatusBarPillWithARightClickMenu_IsBridgedToTheKeyboard()
    {
        var xaml = XDocument.Load(DesktopPath("MainWindow.xaml"));
        var statusBar = Named(xaml, "StatusBarGrid");
        var pills = statusBar.Descendants()
            .Where(e => e.Name.LocalName == "HyperlinkButton" && e.Attribute("RightTapped") is not null)
            .Select(e => (string)e.Attribute(X + "Name")!)
            .ToList();
        Assert.True(pills.Count >= 14);

        var code = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Services.KeyboardContextMenu.Bridge(pill, handler);", code);
        foreach (var pill in pills)
            Assert.Contains($"({pill}, On{pill}RightTapped)", code);
    }

    [Fact]
    public void BridgedHandlers_DoNotReadAPointerPositionFromSyntheticArgs()
    {
        var code = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.DoesNotContain("menu.ShowAt(StatusSectionBreadcrumbButton, e.GetPosition(", code);
        Assert.Contains("Services.KeyboardContextMenu.ShowAt(menu, StatusSectionBreadcrumbButton, e);", code);

        var helper = File.ReadAllText(DesktopPath("Services", "KeyboardContextMenu.cs"));
        Assert.Contains("handledEventsToo: true", helper);
        Assert.Contains("VirtualKey.Application", helper);
        Assert.Contains("if (IsKeyboardInvocation) menu.ShowAt(anchor);", helper);
    }

    [Fact]
    public void CopyMarkdownMenuItem_ReportsAnEmptyDocument()
    {
        var code = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        var start = code.IndexOf("private void OnStatusCopyMarkdownButtonRightTapped(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = code.Substring(start, code.IndexOf("menu.ShowAt(anchor);", start, StringComparison.Ordinal) - start);
        Assert.Contains("AnnounceStatus(\"Document is empty — nothing to copy\");", body);
    }
}
