using System;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels.History;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// Run #282: Version History checkpoint notice improvements.
/// Empty editor text is an informational condition rather than an error condition.
/// </summary>
public class Run282EmptyEditorCheckpointNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_hist282_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private VersionHistoryService NewService() => new(Path.Combine(_dir, "history"));

    private static HistoryWindowViewModel NewVm(VersionHistoryService service, string file, Func<string, string?>? editor = null) =>
        new(_ => "<html/>", _ => Task.FromResult(true), service, initialFilePath: file, editorTextFor: editor);

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

    [Fact]
    public async Task EmptyEditor_TakeCheckpoint_HasInformationalSeverityAndNotError()
    {
        var service = NewService();
        const string file = @"C:\docs\empty.md";
        await service.CaptureAsync(file, "initial", "opened");

        var vm = NewVm(service, file, _ => "");
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.TakeSnapshotCommand.ExecuteAsync("My Milestone");

        Assert.Equal("The editor is empty, so there's nothing to save as a checkpoint.", vm.Notice);
        Assert.Equal(StatusSeverity.Informational, vm.NoticeSeverity);
        Assert.False(vm.NoticeIsError);
        Assert.True(vm.HasNotice);
    }

    [Fact]
    public async Task WhitespaceEditor_TakeCheckpoint_HasInformationalSeverityAndNotError()
    {
        var service = NewService();
        const string file = @"C:\docs\whitespace.md";
        await service.CaptureAsync(file, "initial", "opened");

        var vm = NewVm(service, file, _ => "   \t\r\n   ");
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.TakeSnapshotCommand.ExecuteAsync(null);

        Assert.Equal("The editor is empty, so there's nothing to save as a checkpoint.", vm.Notice);
        Assert.Equal(StatusSeverity.Informational, vm.NoticeSeverity);
        Assert.False(vm.NoticeIsError);
    }

    [Fact]
    public async Task NoDocumentSelected_TakeCheckpoint_RemainsError()
    {
        var vm = NewVm(NewService(), "", _ => "some text");
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.TakeSnapshotCommand.ExecuteAsync(null);

        Assert.Equal(HistoryWindowViewModel.NoDocumentCheckpointNotice, vm.Notice);
        Assert.Equal(StatusSeverity.Error, vm.NoticeSeverity);
        Assert.True(vm.NoticeIsError);
    }

    [Fact]
    public async Task FileNotOpenInEditor_TakeCheckpoint_RemainsError()
    {
        var service = NewService();
        const string file = @"C:\docs\closed.md";
        await service.CaptureAsync(file, "initial", "opened");

        var vm = NewVm(service, file, _ => null);
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.TakeSnapshotCommand.ExecuteAsync(null);

        Assert.Equal($"Open closed.md in the editor first. A checkpoint saves the editor's current text.", vm.Notice);
        Assert.Equal(StatusSeverity.Error, vm.NoticeSeverity);
        Assert.True(vm.NoticeIsError);
    }

    [Fact]
    public async Task ValidCheckpoint_HasSuccessSeverity()
    {
        var service = NewService();
        const string file = @"C:\docs\doc.md";
        await service.CaptureAsync(file, "initial text", "opened");

        var vm = NewVm(service, file, _ => "updated text");
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.TakeSnapshotCommand.ExecuteAsync("Release Milestone");

        Assert.Equal("Checkpoint “Release Milestone” saved.", vm.Notice);
        Assert.Equal(StatusSeverity.Success, vm.NoticeSeverity);
        Assert.False(vm.NoticeIsError);
    }

    [Fact]
    public void ShowNotice_SeverityOverloads_SynchronizeNoticeSeverityAndNoticeIsError()
    {
        var vm = NewVm(NewService(), "");

        vm.ShowNotice("Info test", StatusSeverity.Informational);
        Assert.Equal(StatusSeverity.Informational, vm.NoticeSeverity);
        Assert.False(vm.NoticeIsError);

        vm.ShowNotice("Warning test", StatusSeverity.Warning);
        Assert.Equal(StatusSeverity.Warning, vm.NoticeSeverity);
        Assert.False(vm.NoticeIsError);

        vm.ShowNotice("Error test", StatusSeverity.Error);
        Assert.Equal(StatusSeverity.Error, vm.NoticeSeverity);
        Assert.True(vm.NoticeIsError);

        vm.ShowNotice("Bool false test", isError: false);
        Assert.Equal(StatusSeverity.Success, vm.NoticeSeverity);
        Assert.False(vm.NoticeIsError);

        vm.ShowNotice("Bool true test", isError: true);
        Assert.Equal(StatusSeverity.Error, vm.NoticeSeverity);
        Assert.True(vm.NoticeIsError);
    }

    [Fact]
    public void HistoryWindow_SetsNoticeBarSeverity_FromNoticeSeverity()
    {
        var src = Read("MarkSmith.Desktop/Views/History/HistoryWindow.xaml.cs");
        Assert.Contains("NoticeBar.Severity = (InfoBarSeverity)(int)_vm.NoticeSeverity;", src);
        Assert.Contains("NoticeHold.Attach(NoticeBar, _noticeTimer, () => NoticeBar.IsOpen && !_vm.NoticeIsError);", src);
        Assert.Contains("if (!_vm.NoticeIsError && !NoticeHold.IsHeld(NoticeBar)) _noticeTimer.Start();", src);
    }
}
