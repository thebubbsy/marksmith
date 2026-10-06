using MarkSmith.Services;
using MarkSmith.ViewModels.History;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>Routine run #17: the Version History window audit (checkpoints, selection across
/// filters, empty states, folded diffs, rename/delete, restore guard).</summary>
public class HistoryPolishTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_histpolish_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private VersionHistoryService NewService() => new(Path.Combine(_dir, "history"));

    private static async Task WaitForAsync(Func<bool> condition, int attempts = 60)
    {
        for (int i = 0; i < attempts && !condition(); i++) await Task.Delay(50);
    }

    private static HistoryWindowViewModel NewVm(VersionHistoryService service, string file, Func<string, string?>? editor = null) =>
        new(_ => "<html/>", _ => Task.FromResult(true), service, initialFilePath: file, editorTextFor: editor);

    // ---- LineDiff.Collapse ----

    [Fact]
    public void Collapse_FoldsLongUnchangedRuns_KeepingThreeLinesOfContext()
    {
        var before = string.Join("\n", Enumerable.Range(1, 40).Select(i => "line " + i));
        var after = before.Replace("line 20", "line twenty");
        var segments = LineDiff.Collapse(LineDiff.Diff(before, after));

        // 16 hidden, 3 context, -/+, 3 context, 17 hidden
        Assert.Equal(16, segments[0].HiddenCount);
        Assert.Null(segments[0].Line);
        Assert.Equal("line 17", segments[1].Line!.Text);
        Assert.Contains(segments, s => s.Line is { Kind: LineDiff.Kind.Added, Text: "line twenty" });
        Assert.Equal(17, segments[^1].HiddenCount);
        Assert.True(segments.Count < 15);
    }

    [Fact]
    public void Collapse_ShowsASingleHiddenLineInsteadOfFoldingIt()
    {
        // Changes at lines 1 and 9: context covers 1–4 and 6–9, leaving line 5 alone between them.
        var before = string.Join("\n", Enumerable.Range(1, 9).Select(i => "l" + i));
        var after = before.Replace("l1\n", "L1\n").Replace("l9", "L9");
        var segments = LineDiff.Collapse(LineDiff.Diff(before, after));
        Assert.DoesNotContain(segments, s => s.Line is null);
        Assert.Contains(segments, s => s.Line?.Text == "l5");
    }

    [Fact]
    public void Collapse_ReturnsNothing_WhenTheTextsAreIdentical()
    {
        Assert.Empty(LineDiff.Collapse(LineDiff.Diff("a\nb", "a\nb")));
    }

    [Fact]
    public void AllAdded_ListsAFirstVersion_WithNoPhantomBlankLine()
    {
        var lines = LineDiff.AllAdded("one\ntwo");
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(LineDiff.Kind.Added, l.Kind));
        Assert.Equal(new int?[] { 1, 2 }, lines.Select(l => l.NewNumber));
    }

    [Fact]
    public async Task FirstVersion_DiffHasNoRemovedRows()
    {
        var service = NewService();
        const string file = @"C:\docs\first.md";
        await service.CaptureAsync(file, "alpha\nbeta", "opened");
        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.UnifiedDiffRows.Count > 0);

        Assert.Equal(2, vm.UnifiedDiffRows.Count);
        Assert.DoesNotContain(vm.UnifiedDiffRows, r => r.IsRemoved);
        Assert.Equal("2 added · 0 removed", vm.DiffStats);
    }

    [Fact]
    public void Diff_TreatsBareCarriageReturnsAsLineBreaks()
    {
        // WinUI's TextBox stores breaks as a bare \r; a capture from the editor must still diff by line.
        var lines = LineDiff.Diff("one\rtwo\rthree", "one\rTWO\rthree");
        Assert.Equal(1, lines.Count(l => l.Kind == LineDiff.Kind.Added));
        Assert.Equal(1, lines.Count(l => l.Kind == LineDiff.Kind.Removed));
    }

    [Fact]
    public void SplitAndUnifiedRows_GetAFoldRowPerHiddenRun()
    {
        var before = string.Join("\n", Enumerable.Range(1, 30).Select(i => "row " + i));
        var after = before.Replace("row 15", "row fifteen");
        var segments = LineDiff.Collapse(LineDiff.Diff(before, after));

        var unified = HistoryWindowViewModel.BuildUnifiedRows(segments);
        Assert.Equal(2, unified.Count(r => r.IsGap));
        Assert.Equal("11 unchanged lines", unified.First(r => r.IsGap).Text);

        var split = HistoryWindowViewModel.BuildSplitRows(segments);
        Assert.Equal(2, split.Count(r => r.IsGap));
        var changed = split.Single(r => r.Left is { IsRemoved: true });
        Assert.Equal("row fifteen", changed.Right!.Text); // still paired side by side
    }

    [Fact]
    public void FirstCapture_CountsLinesSplitByBareCarriageReturns()
    {
        var service = NewService();
        service.CaptureAsync(@"C:\docs\cr.md", "a\rb\rc\rd").GetAwaiter().GetResult();
        var v = service.GetVersionsAsync(@"C:\docs\cr.md").GetAwaiter().GetResult().Single();
        Assert.Equal(4, v.LinesAdded); // was 1: the old count split on '\n' only
    }

    // ---- Checkpoints ----

    [Fact]
    public async Task Checkpoint_CapturesTheEditorsText_NotTheSelectedOldVersion()
    {
        var service = NewService();
        const string file = @"C:\docs\cp.md";
        await service.CaptureAsync(file, "version one", "opened");
        await Task.Delay(15);
        await service.CaptureAsync(file, "version two", "autosave");

        var vm = NewVm(service, file, key => key.EndsWith("cp.md") ? "live editor text" : null);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.True(vm.IsSelectedFileOpen);
        vm.SelectVersionCommand.Execute(vm.Bands[0].Items.Last()); // browsing the old version

        await vm.TakeSnapshotCommand.ExecuteAsync("Sent to client");

        var newest = (await service.GetVersionsAsync(file))[0];
        Assert.Equal("live editor text", await service.GetContentAsync(newest.Id));
        Assert.Equal("Sent to client", newest.Label);
        Assert.True(newest.IsStarred);
        Assert.Contains("saved", vm.Notice);
        Assert.False(vm.NoticeIsError);
    }

    [Fact]
    public async Task Checkpoint_WithNoChanges_StarsAndNamesTheLatestVersionInstead()
    {
        var service = NewService();
        const string file = @"C:\docs\same.md";
        await service.CaptureAsync(file, "unchanged", "opened");
        var vm = NewVm(service, file, _ => "unchanged");
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.TakeSnapshotCommand.ExecuteAsync("Milestone");

        var versions = await service.GetVersionsAsync(file);
        Assert.Single(versions);
        Assert.Equal("Milestone", versions[0].Label);
        Assert.True(versions[0].IsStarred);
        Assert.Contains("No changes", vm.Notice);
    }

    [Fact]
    public async Task Checkpoint_ForADocumentThatIsNotOpen_ExplainsInsteadOfSavingOldText()
    {
        var service = NewService();
        const string file = @"C:\docs\closed.md";
        await service.CaptureAsync(file, "old", "opened");
        var vm = NewVm(service, file, _ => null);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.IsSelectedFileOpen);
        Assert.Contains("Open closed.md", vm.CheckpointTooltip);
        await vm.TakeSnapshotCommand.ExecuteAsync("x");

        Assert.Single(await service.GetVersionsAsync(file));
        Assert.True(vm.NoticeIsError);
    }

    // ---- Filters keep the selection; empty states ----

    [Fact]
    public async Task Filtering_KeepsTheSelectedVersionSelected()
    {
        var service = NewService();
        const string file = @"C:\docs\f.md";
        await service.CaptureAsync(file, "one", "opened", "Alpha");
        await Task.Delay(15);
        await service.CaptureAsync(file, "two", "autosave", "Beta");
        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);

        var alpha = vm.Bands[0].Items.Single(i => i.Label == "Alpha");
        vm.SelectVersionCommand.Execute(alpha);
        vm.SearchQuery = "Alp";

        var row = Assert.Single(vm.Bands[0].Items);
        Assert.True(row.IsSelected);
        Assert.Same(row, vm.Selected);
    }

    [Fact]
    public async Task Search_MatchesWhatTheRowShows()
    {
        var service = NewService();
        const string file = @"C:\docs\s.md";
        await service.CaptureAsync(file, "one", "autosave");
        await Task.Delay(15);
        await service.CaptureAsync(file, "two", "export:pdf");
        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchQuery = "Auto-Save"; // the row's label, not the raw "autosave" key
        Assert.Single(vm.Bands[0].Items);
        vm.SearchQuery = "Export · PDF";
        Assert.Single(vm.Bands[0].Items);
    }

    [Fact]
    public async Task AFilterThatHidesEverything_ShowsNoMatches_AndClearFiltersBringsThemBack()
    {
        var service = NewService();
        const string file = @"C:\docs\nm.md";
        await service.CaptureAsync(file, "one", "opened");
        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.IsStarredOnlyFilter = true;
        Assert.True(vm.ShowNoMatches);
        Assert.False(vm.ShowTimeline);
        Assert.Contains("No starred versions", vm.NoMatchesText);

        vm.ClearFiltersCommand.Execute(null);
        Assert.True(vm.ShowTimeline);
        Assert.False(vm.ShowNoMatches);
    }

    [Fact]
    public async Task Unstarring_UnderTheStarredFilter_TakesTheRowOffTheList()
    {
        var service = NewService();
        const string file = @"C:\docs\st.md";
        await service.CaptureAsync(file, "one", "snapshot", "Keep", isStarred: true);
        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.IsStarredOnlyFilter = true;

        await vm.ToggleStarCommand.ExecuteAsync(vm.Bands[0].Items[0]);

        Assert.True(vm.ShowNoMatches);
    }

    [Fact]
    public async Task EmptyStore_ShowsTheNoHistoryState()
    {
        var vm = NewVm(NewService(), "");
        Assert.True(vm.IsLoading);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.False(vm.IsLoading);
        Assert.True(vm.ShowNoFiles);
        Assert.False(vm.HasSelection);
        Assert.False(vm.RestoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task ScratchText_IsListedAsUnsavedText()
    {
        var service = NewService();
        await service.CaptureAsync("scratch://workspace-session.md", "typed", "autosave");
        var vm = NewVm(service, "scratch://workspace-session.md");
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("Unsaved text", vm.Files[0].FileName);
        Assert.Equal("Unsaved text", vm.FileName);
    }

    // ---- Diff pane ----

    [Fact]
    public async Task AVersionIdenticalToTheOneBefore_ReportsNoChanges()
    {
        var service = NewService();
        const string file = @"C:\docs\id.md";
        await service.CaptureAsync(file, "same", "opened");
        await Task.Delay(15);
        await service.CaptureAsync(file, "different", "autosave");
        await Task.Delay(15);
        await service.CaptureAsync(file, "same", "export:pdf"); // back to the first text
        // Same text as an older version within 2 s used to make the store drop that older version.
        Assert.Equal("export:pdf,autosave,opened", string.Join(",", (await service.GetVersionsAsync(file)).Select(v => v.Source)));

        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.DiffTitle != "Select a version to see its changes");
        Assert.False(vm.IsUnchanged); // "different" -> "same" is a change

        // The store refuses back-to-back duplicates, so build the unchanged case by deleting the middle.
        var middle = vm.Bands[0].Items.Single(i => i.Entry.Source == "autosave");
        await vm.DeleteVersionCommand.ExecuteAsync(middle);
        vm.SelectVersionCommand.Execute(vm.Bands[0].Items[0]);
        await WaitForAsync(() => vm.IsUnchanged);

        Assert.Equal("No text changes", vm.DiffTitle);
        Assert.True(vm.IsUnchanged);
        Assert.True(vm.ShowNoChanges);
        Assert.False(vm.HasDiffStats);
        Assert.Empty(vm.UnifiedDiffRows);
    }

    // ---- Rename / delete / restore ----

    [Fact]
    public async Task Rename_UpdatesTheRowImmediately_AndAnEmptyNameClearsTheLabel()
    {
        var service = NewService();
        const string file = @"C:\docs\r.md";
        await service.CaptureAsync(file, "one", "opened");
        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);
        var row = vm.Bands[0].Items[0];

        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await vm.RenameVersionCommand.ExecuteAsync((row, "  Draft for review  "));

        Assert.Equal("Draft for review", row.Snippet);
        Assert.True(row.HasSnippet);
        Assert.Contains(nameof(VersionItemViewModel.Snippet), changed);
        Assert.Equal("Draft for review", (await service.GetVersionsAsync(file))[0].Label);

        await vm.RenameVersionCommand.ExecuteAsync((row, ""));
        Assert.False(row.HasLabel);
        Assert.Null((await service.GetVersionsAsync(file))[0].Label);
    }

    [Fact]
    public async Task DeletingTheLastVersion_RemovesTheDocumentFromTheList()
    {
        var service = NewService();
        await service.CaptureAsync(@"C:\docs\keep.md", "k", "opened");
        await Task.Delay(15);
        await service.CaptureAsync(@"C:\docs\gone.md", "g", "opened");
        var vm = NewVm(service, @"C:\docs\gone.md");
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("gone.md", vm.SelectedFile!.FileName);

        await vm.DeleteVersionCommand.ExecuteAsync(vm.Bands[0].Items[0]);

        Assert.Single(vm.Files);
        Assert.Equal("keep.md", vm.SelectedFile!.FileName);
        Assert.Contains("last version", vm.Notice);
    }

    [Fact]
    public async Task Delete_UpdatesTheDocumentsVersionCount()
    {
        var service = NewService();
        const string file = @"C:\docs\count.md";
        await service.CaptureAsync(file, "one", "opened");
        await Task.Delay(15);
        await service.CaptureAsync(file, "two", "autosave");
        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("2 versions", vm.SelectedFile!.VersionCountLabel);

        await vm.DeleteVersionCommand.ExecuteAsync(vm.Bands[0].Items[1]);

        Assert.Equal("1 version", vm.SelectedFile!.VersionCountLabel);
        Assert.True(vm.SelectedFile.IsSelected);
    }

    [Fact]
    public async Task Restore_ReportsSuccessAndFailure()
    {
        var service = NewService();
        const string file = @"C:\docs\rs.md";
        await service.CaptureAsync(file, "one", "opened");

        var ok = new HistoryWindowViewModel(_ => "", _ => Task.FromResult(true), service, file);
        await ok.LoadCommand.ExecuteAsync(null);
        Assert.True(ok.RestoreCommand.CanExecute(null));
        await ok.RestoreCommand.ExecuteAsync(null);
        Assert.Contains("Restored", ok.Notice);
        Assert.False(ok.NoticeIsError);

        var bad = new HistoryWindowViewModel(_ => "", _ => Task.FromResult(false), service, file);
        await bad.LoadCommand.ExecuteAsync(null);
        await bad.RestoreCommand.ExecuteAsync(null);
        Assert.True(bad.NoticeIsError);
    }
}
