using System;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Services;
using MarkSmith.ViewModels.History;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

// Run #282:
// - Version History's guidance notices (open the document first, the editor is empty, nothing to
//   copy, no added lines in this diff) show as information that clears itself, not as a red error
//   that interrupts the screen reader and stays until dismissed;
// - Suite Hub's "Turn on" and Extension setup's "Turn on connection" park focus on a sibling for
//   the wait instead of the window's first tab stop, and give it back only if the user hasn't moved on;
// - Settings › License "Activate" moves focus afterwards only while it still waits on the key box,
//   keeping the keyboard focus rectangle;
// - Mermaid Studio's style presets expose which one is applied, explain themselves in a tooltip,
//   answer a press of the applied preset, and mention Ctrl+Z after applying one.
public class Run282GuidanceNoticesAndParkedFocusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_run282_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

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

    private const string History = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml.cs";
    private const string SuiteHub = "MarkSmith.Desktop/Views/SuiteHubView.xaml.cs";
    private const string Settings = "MarkSmith.Desktop/Views/SettingsView.xaml.cs";
    private const string Extension = "MarkSmith.Desktop/Views/ExtensionSetupView.xaml.cs";
    private const string Rescue = "MarkSmith.Desktop/Services/FocusRescue.cs";
    private const string Mermaid = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml.cs";

    private HistoryWindowViewModel NewHistoryVm(string file, Func<string, string?> editor) =>
        new(_ => "<html/>", _ => Task.FromResult(true), new VersionHistoryService(Path.Combine(_dir, "history")),
            initialFilePath: file, editorTextFor: editor);

    // ---- Version History: guidance is information ----

    [Fact]
    public void ShowInfo_IsInformation_AndShowNoticeClearsIt()
    {
        var vm = NewHistoryVm("", _ => null);
        vm.ShowInfo("No added lines in this diff.");
        Assert.True(vm.NoticeIsInfo);
        Assert.False(vm.NoticeIsError);
        Assert.Equal("No added lines in this diff.", vm.Notice);

        vm.ShowNotice("Couldn't copy: busy", isError: true);
        Assert.False(vm.NoticeIsInfo);
        Assert.True(vm.NoticeIsError);

        vm.ShowNotice("Copied.");
        Assert.False(vm.NoticeIsInfo);
        Assert.False(vm.NoticeIsError);
    }

    [Fact]
    public async Task Checkpoint_FromAnEmptyEditor_IsGuidance_NotAnError()
    {
        var service = new VersionHistoryService(Path.Combine(_dir, "history"));
        const string file = @"C:\docs\empty.md";
        await service.CaptureAsync(file, "old", "opened");
        var vm = new HistoryWindowViewModel(_ => "<html/>", _ => Task.FromResult(true), service,
            initialFilePath: file, editorTextFor: _ => "   ");
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.TakeSnapshotCommand.ExecuteAsync("x");

        Assert.Contains("editor is empty", vm.Notice);
        Assert.True(vm.NoticeIsInfo);
        Assert.False(vm.NoticeIsError);
        Assert.Single(await service.GetVersionsAsync(file));
    }

    [Fact]
    public async Task Checkpoint_WithNoDocument_IsGuidance_NotAnError()
    {
        var vm = NewHistoryVm("", _ => "some text");
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.TakeSnapshotCommand.ExecuteAsync(null);
        Assert.Equal(HistoryWindowViewModel.NoDocumentCheckpointNotice, vm.Notice);
        Assert.True(vm.NoticeIsInfo);
        Assert.False(vm.NoticeIsError);
    }

    [Fact]
    public void HistoryNoticeBar_MapsInfoToInformational_AndOnlyErrorsAreAssertive()
    {
        var body = Body(Read(History), "private void ShowNotice()");
        Assert.Contains("_vm.NoticeIsInfo ? InfoBarSeverity.Informational", body);
        Assert.Contains("_vm.NoticeIsError\n            ? Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive\n            : Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite", body);
        // Information clears itself like a confirmation; only errors wait to be dismissed.
        Assert.Contains("if (!_vm.NoticeIsError && !NoticeHold.IsHeld(NoticeBar)) _noticeTimer.Start();", body);
    }

    [Theory]
    [InlineData("_vm.ShowInfo(\"No added lines in this diff.\");")]
    [InlineData("_vm.ShowInfo(\"No removed lines in this diff.\");")]
    [InlineData("_vm.ShowInfo(\"No unchanged context lines in this diff.\");")]
    [InlineData("_vm.ShowInfo(_vm.NothingToCopyMessage(patch: isShift));")]
    [InlineData("_vm.ShowInfo(\"No line changes in this version to copy as a diff patch.\");")]
    public void HistoryNothingToDoNotices_AreInformation(string call)
    {
        Assert.Contains(call, Read(History));
    }

    // ---- Busy buttons park focus ----

    [Fact]
    public void SuiteHub_TurnOn_ParksFocusOnGetExtension_AndReturnsOnlyWhileWaiting()
    {
        var body = Body(Read(SuiteHub), "private async void OnTurnOnApiClick").Replace("\n\n", "\n");
        var park = body.IndexOf("var parked = FocusRescue.Park(TurnOnApiButton, GetExtensionButton);", StringComparison.Ordinal);
        var disable = body.IndexOf("TurnOnApiButton.IsEnabled = false;", StringComparison.Ordinal);
        Assert.True(park >= 0 && park < disable, "focus must be parked before the button disables itself");
        Assert.Contains("if (parked.Waiting)", body);
        Assert.Contains("FocusAndBringIntoView(nextTarget, parked.How);", body);
        Assert.DoesNotContain("hadFocus", body);
    }

    [Fact]
    public void ExtensionSetup_TurnOn_ParksOnCheckNow_AndReturnsAfterAnError()
    {
        var body = Body(Read(Extension), "private async void OnTurnOnClick");
        var park = body.IndexOf("var parked = FocusRescue.Park(TurnOnButton, CheckNowButton);", StringComparison.Ordinal);
        var disable = body.IndexOf("TurnOnButton.IsEnabled = false;", StringComparison.Ordinal);
        Assert.True(park >= 0 && park < disable);
        Assert.Contains("ShowError(error);\n            parked.Return();", body);
        Assert.DoesNotContain("FocusState.Programmatic", body);
    }

    [Fact]
    public void FocusRescue_StillOn_TreatsLostFocusAsWaiting()
    {
        var src = Read(Rescue);
        Assert.Contains("public static bool StillOn(Control control) => control.XamlRoot is not null && FocusWasLost(control);", src);
    }

    [Fact]
    public void Settings_Activate_MovesFocusOnlyWhileItStillWaits_KeepingKeyboardState()
    {
        var body = Body(Read(Settings), "private async void OnActivateLicense");
        Assert.Contains("var parked = FocusRescue.Park(ActivateButton, KeyBox);", body);
        Assert.Contains("|| (keyBoxFocus != FocusState.Unfocused && FocusRescue.StillOn(KeyBox));", body);
        Assert.Contains("if (stillWaiting && ok && DeactivateButton.Visibility == Visibility.Visible)\n                DeactivateButton.Focus(how);", body);
        Assert.Contains("else if (stillWaiting && KeyEntryRow.Visibility == Visibility.Visible)\n                KeyBox.Focus(how);", body);
        Assert.DoesNotContain("Focus(FocusState.Programmatic)", body);
    }

    // ---- Mermaid style presets ----

    [Fact]
    public void ApplyStylePreset_IsOneUndoStep_AndMentionsCtrlZ()
    {
        var vm = new MermaidStudioViewModel();
        Assert.False(vm.CanUndo);

        vm.ApplyStylePreset("Nord Ocean");

        Assert.Equal("Nord Ocean", vm.ActivePalette);
        Assert.Equal("Applied 'Nord Ocean' palette · Ctrl+Z to undo", vm.StatusText);
        Assert.True(vm.CanUndo);
    }

    [Fact]
    public void ApplyStylePreset_TheAppliedOne_SaysSo_WithoutAnotherUndoStep()
    {
        var vm = new MermaidStudioViewModel();
        vm.ApplyStylePreset("Nord Ocean");
        vm.ApplyStylePreset("Nord Ocean");

        Assert.Equal("'Nord Ocean' is already applied.", vm.StatusText);
        vm.Undo();
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public void ApplyStylePreset_UnknownName_ChangesNothing()
    {
        var vm = new MermaidStudioViewModel();
        var before = vm.StatusText;
        vm.ApplyStylePreset("No Such Preset");
        Assert.False(vm.CanUndo);
        Assert.Equal(before, vm.StatusText);
        Assert.NotEqual("No Such Preset", vm.ActivePalette);
    }

    [Fact]
    public void ActivePalette_FromALoadedDiagram_HasNoUndoHint()
    {
        var vm = new MermaidStudioViewModel();
        vm.ActivePalette = "Monochrome Print";
        Assert.Equal("Applied 'Monochrome Print' palette.", vm.StatusText);
    }

    [Fact]
    public void PresetButtons_ExposeTheAppliedOne_AndExplainThemselves()
    {
        var src = Read(Mermaid);
        var click = Body(src, "private void OnPalettePresetClick");
        Assert.Contains("ViewModel.ApplyStylePreset(name);", click);
        Assert.DoesNotContain("SnapshotForUndo", click);
        var highlight = Body(src, "private void HighlightActivePalette");
        Assert.Contains("AutomationProperties.SetItemStatus(btn, isActive ? \"Applied\" : \"\");", highlight);
        Assert.Contains("AutomationProperties.SetName(btn, $\"{label} style preset\");", highlight);
        Assert.Contains("Recolour every node and connector with {label} · Ctrl+Z undoes it", highlight);
    }
}
