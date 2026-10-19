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
    public async Task FirstCapture_CountsLinesSplitByBareCarriageReturns()
    {
        var service = NewService();
        await service.CaptureAsync(@"C:\docs\cr.md", "a\rb\rc\rd");
        var v = (await service.GetVersionsAsync(@"C:\docs\cr.md")).Single();
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

    [Fact]
    public async Task TimelineNavigation_MovesAcrossVisibleVersions_AndClampsAtBounds()
    {
        var service = NewService();
        const string file = @"C:\docs\nav.md";
        await service.CaptureAsync(file, "v1", "opened", "First");
        await Task.Delay(15);
        await service.CaptureAsync(file, "v2", "autosave", "Second");
        await Task.Delay(15);
        await service.CaptureAsync(file, "v3", "snapshot", "Third", isStarred: true);

        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("Third", vm.Selected!.Label);
        Assert.False(vm.SelectPreviousVersion()); // already at newest

        Assert.True(vm.SelectNextVersion());
        Assert.Equal("Second", vm.Selected!.Label);
        Assert.True(vm.Selected.IsSelected);

        Assert.True(vm.SelectNextVersion());
        Assert.Equal("First", vm.Selected!.Label);
        Assert.False(vm.SelectNextVersion()); // already at oldest

        Assert.True(vm.SelectFirstVersion());
        Assert.Equal("Third", vm.Selected!.Label);
        Assert.False(vm.SelectFirstVersion());

        Assert.True(vm.SelectLastVersion());
        Assert.Equal("First", vm.Selected!.Label);
        Assert.False(vm.SelectLastVersion());
    }

    [Fact]
    public async Task Filtering_WhenPreviousSelectionIsHidden_AutoSelectsFirstVisibleVersion()
    {
        var service = NewService();
        const string file = @"C:\docs\autoselect.md";
        await service.CaptureAsync(file, "v1", "snapshot", "Milestone", isStarred: true);
        await Task.Delay(15);
        await service.CaptureAsync(file, "v2", "autosave", "Recent draft", isStarred: false);

        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("Recent draft", vm.Selected!.Label);

        // Starred filter hides "Recent draft", so "Milestone" becomes selected automatically
        vm.IsStarredOnlyFilter = true;
        Assert.NotNull(vm.Selected);
        Assert.Equal("Milestone", vm.Selected!.Label);
        Assert.True(vm.Selected.IsSelected);

        // Search filter that hides everything clears selection and diff rows (and hides PreviewWeb in Preview mode); clearing filters restores selection
        await WaitForAsync(() => vm.UnifiedDiffRows.Count > 0);
        vm.SetDiffMode(HistoryDiffMode.Preview);
        Assert.True(vm.ShowPreviewPane);
        vm.SearchQuery = "nonexistent-query";
        Assert.Null(vm.Selected);
        Assert.False(vm.ShowUnifiedRows);
        Assert.False(vm.ShowSplitRows);
        Assert.False(vm.ShowPreviewPane);
        Assert.True(vm.ShowDiffPlaceholder);
        Assert.Empty(vm.UnifiedDiffRows);
        Assert.Empty(vm.DiffRows);
        Assert.Equal("", vm.SelectedHeader);
        Assert.Equal("", vm.DiffStats);

        vm.ClearFiltersCommand.Execute(null);
        Assert.False(vm.IsStarredOnlyFilter);
        Assert.Equal("", vm.SearchQuery);
        Assert.NotNull(vm.Selected);
        Assert.True(vm.ShowPreviewPane);
        Assert.False(vm.ShowDiffPlaceholder);
        Assert.Equal("Recent draft", vm.Selected!.Label);
        Assert.Equal("This version as a document", vm.DiffTitle);
    }

    [Fact]
    public async Task GetVersionContent_And_BuildUnifiedDiffPatch_ReturnStoredMarkdownAndUnifiedPatch()
    {
        var service = NewService();
        const string file = @"C:\docs\patch.md";
        await service.CaptureAsync(file, "# Title\nOld line", "opened", "Initial");
        await Task.Delay(15);
        await service.CaptureAsync(file, "# Title\nMiddle line", "autosave", "Middle");
        await Task.Delay(15);
        await service.CaptureAsync(file, "# Title\nNew line  ", "snapshot", "Latest", isStarred: true);

        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.UnifiedDiffRows.Count > 0);

        var md = await vm.GetVersionContentAsync();
        Assert.Equal("# Title\nNew line  ", md);

        var patch = await vm.BuildUnifiedDiffPatchAsync();
        Assert.Contains("--- a/patch.md", patch);
        Assert.Contains("+++ b/patch.md", patch);
        Assert.Contains("- Middle line", patch);
        Assert.EndsWith("+ New line  ", patch);

        // Requesting a patch for an unselected older version computes that version's diff instead of copying Selected's rows.
        var middleItem = vm.VisibleVersions.Single(v => v.Label == "Middle");
        var middlePatch = await vm.BuildUnifiedDiffPatchAsync(middleItem);
        Assert.Contains("- Old line", middlePatch);
        Assert.Contains("+ Middle line", middlePatch);
        Assert.DoesNotContain("New line", middlePatch);

        // Selecting a version and immediately requesting its patch before RefreshDiffAsync completes still computes the right diff.
        var initialItem = vm.VisibleVersions.Single(v => v.Label == "Initial");
        vm.SelectVersion(initialItem);
        var initialPatch = await vm.BuildUnifiedDiffPatchAsync(initialItem);
        Assert.Contains("+ # Title", initialPatch);
        Assert.Contains("+ Old line", initialPatch);
        Assert.DoesNotContain("Middle line", initialPatch);
    }

    [Fact]
    public async Task RenameUnderActiveSearch_ReappliesFilter_And_DeleteSelectsAdjacentAndUpdatesMetadata()
    {
        var service = NewService();
        const string file = @"C:\docs\edit_ops.md";
        await service.CaptureAsync(file, "v1", "opened", "First");
        await Task.Delay(15);
        await service.CaptureAsync(file, "v2", "autosave", "Draft two");
        await Task.Delay(15);
        await service.CaptureAsync(file, "v3", "autosave", "Draft three");
        await Task.Delay(15);
        await service.CaptureAsync(file, "v4", "snapshot", "Final milestone", isStarred: true);

        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);

        // 1. Renaming a version while SearchQuery is active immediately re-filters the timeline
        vm.SearchQuery = "Draft";
        Assert.Equal(2, vm.VisibleVersions.Count);
        var draftThree = vm.VisibleVersions.Single(v => v.Label == "Draft three");
        await vm.RenameVersionCommand.ExecuteAsync((draftThree, "Polished three"));
        Assert.Single(vm.VisibleVersions);
        Assert.Equal("Draft two", vm.Selected!.Label);

        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(4, vm.VisibleVersions.Count);

        // 2. Deleting a selected middle version selects the adjacent version at the same index rather than jumping to index 0
        var middle = vm.VisibleVersions[2]; // "Draft two"
        vm.SelectVersion(middle);
        await vm.DeleteVersionCommand.ExecuteAsync(middle);
        Assert.Equal("First", vm.Selected!.Label);

        // 3. Deleting the newest version updates SelectedFile.Summary.LastModified and LatestSource
        var newest = vm.VisibleVersions[0]; // "Final milestone"
        var secondNewest = vm.VisibleVersions[1]; // "Polished three"
        await vm.DeleteVersionCommand.ExecuteAsync(newest);
        Assert.Equal(secondNewest.Entry.CreatedAt, vm.SelectedFile!.Summary.LastModified);
        Assert.Equal(secondNewest.Entry.Source, vm.SelectedFile.Summary.LatestSource);

        // 4. Overlapping LoadAsync and ShowFileAsync calls never duplicate Files
        await Task.WhenAll(vm.LoadAsync(), vm.ShowFileAsync(file));
        Assert.Single(vm.Files);
    }

    [Fact]
    public void Run125_SetDiffContextLines_UpdatesContextLabel_AndRecalculatesSegments()
    {
        var service = NewService();
        var vm = NewVm(service, "doc.md");

        Assert.Equal(3, vm.DiffContextLines);
        Assert.Equal("3 lines context", vm.DiffContextLabel);

        vm.SetDiffContextLines(0);
        Assert.Equal(0, vm.DiffContextLines);
        Assert.Equal("Changes only", vm.DiffContextLabel);

        vm.SetDiffContextLines(1);
        Assert.Equal(1, vm.DiffContextLines);
        Assert.Equal("1 line context", vm.DiffContextLabel);

        vm.SetDiffContextLines(-1);
        Assert.Equal(-1, vm.DiffContextLines);
        Assert.Equal("Full document", vm.DiffContextLabel);

        vm.SetDiffContextLines(3);
        Assert.Equal(3, vm.DiffContextLines);
        Assert.Equal("3 lines context", vm.DiffContextLabel);
    }

    [Fact]
    public void LineDiff_ignoreWhitespace_TreatsTrailingAndLeadingWhitespaceAsUnchanged()
    {
        var before = "hello world\nalpha beta";
        var after = "  hello world  \nalpha beta";

        // Default: detects modification as removed + added
        var diffNormal = LineDiff.Diff(before, after, ignoreWhitespace: false);
        Assert.Contains(diffNormal, d => d.Kind == LineDiff.Kind.Removed);
        Assert.Contains(diffNormal, d => d.Kind == LineDiff.Kind.Added);

        // With ignoreWhitespace: treats as same line
        var diffIgnore = LineDiff.Diff(before, after, ignoreWhitespace: true);
        Assert.DoesNotContain(diffIgnore, d => d.Kind == LineDiff.Kind.Removed);
        Assert.DoesNotContain(diffIgnore, d => d.Kind == LineDiff.Kind.Added);
        Assert.All(diffIgnore, d => Assert.Equal(LineDiff.Kind.Same, d.Kind));
    }

    [Fact]
    public void Run131_ExtractModifiedLines_ExtractsOnlySpecifiedKindExcludingGaps()
    {
        var rows = new List<LineDiff.UnifiedRow>
        {
            new(LineDiff.Kind.Same, 1, 1, "unchanged line", "  "),
            new(LineDiff.Kind.Removed, 2, null, "old deleted line", "- "),
            new(LineDiff.Kind.Added, null, 2, "new inserted line 1", "+ "),
            new(LineDiff.Kind.Added, null, 3, "new inserted line 2", "+ "),
            new(LineDiff.Kind.Same, null, null, "fold gap text", "", IsGap: true),
            new(LineDiff.Kind.Added, null, null, "another fold gap", "", IsGap: true)
        };

        var added = LineDiff.ExtractModifiedLines(rows, LineDiff.Kind.Added);
        Assert.Equal(2, added.Count);
        Assert.Equal("new inserted line 1", added[0]);
        Assert.Equal("new inserted line 2", added[1]);

        var removed = LineDiff.ExtractModifiedLines(rows, LineDiff.Kind.Removed);
        Assert.Single(removed);
        Assert.Equal("old deleted line", removed[0]);

        var same = LineDiff.ExtractModifiedLines(rows, LineDiff.Kind.Same);
        Assert.Single(same);
        Assert.Equal("unchanged line", same[0]);

        Assert.Empty(LineDiff.ExtractModifiedLines(null!, LineDiff.Kind.Added));
    }

    [Theory]
    [InlineData(5, 3, 24, "25.0% churn (+5 / -3 / =24 lines)")]
    [InlineData(10, 0, 10, "50.0% churn (+10 / -0 / =10 lines)")]
    [InlineData(0, 0, 100, "0.0% churn (+0 / -0 / =100 lines)")]
    [InlineData(0, 0, 0, "0% modified (0 lines)")]
    public void Run132_FormatDiffRatio_FormatsAccuratePercentagesAndSummaries(int added, int removed, int unchanged, string expected)
    {
        var summary = LineDiff.FormatDiffRatio(added, removed, unchanged);
        Assert.Equal(expected, summary);
    }

    [Theory]
    [InlineData(5, 3, 24, 2, "25.0% churn (+5 / -3 / =24 lines) · 2 change hunks")]
    [InlineData(10, 0, 10, 1, "50.0% churn (+10 / -0 / =10 lines) · 1 change hunk")]
    [InlineData(0, 0, 100, 0, "0.0% churn (+0 / -0 / =100 lines) · 0 change hunks")]
    [InlineData(0, 0, 0, 0, "0% modified (0 lines) · 0 change hunks")]
    public void Run133_FormatDiffStatistics_FormatsCompositeRatioAndHunks(int added, int removed, int unchanged, int hunks, string expected)
    {
        var summary = LineDiff.FormatDiffStatistics(added, removed, unchanged, hunks);
        Assert.Equal(expected, summary);
    }

    [Fact]
    public void Run134_FormatVersionTimestampHeader_FormatsTimestampDisplay()
    {
        var time = new DateTimeOffset(2026, 10, 17, 14, 30, 0, TimeSpan.Zero);
        var header = LineDiff.FormatVersionTimestampHeader("Draft v2", time);
        Assert.Contains("Draft v2", header);
        Assert.Contains("2026", header);
        Assert.Contains("October", header);

        // Null timestamp falls back to version name or "Version"
        Assert.Equal("Milestone", LineDiff.FormatVersionTimestampHeader("Milestone", null));
        Assert.Equal("Version", LineDiff.FormatVersionTimestampHeader("", null));
    }

    [Fact]
    public void Run135_FormatVersionDeltaSummary_FormatsAccurately()
    {
        var both = LineDiff.FormatVersionDeltaSummary("Draft v2", 5, 2);
        Assert.Equal("Draft v2 · +5 added, -2 removed", both);

        var addedOnly = LineDiff.FormatVersionDeltaSummary("Initial commit", 10, 0);
        Assert.Equal("Initial commit · +10 lines added", addedOnly);

        var singleAdded = LineDiff.FormatVersionDeltaSummary("Hotfix", 1, 0);
        Assert.Equal("Hotfix · +1 line added", singleAdded);

        var removedOnly = LineDiff.FormatVersionDeltaSummary("Prune", 0, 4);
        Assert.Equal("Prune · -4 lines removed", removedOnly);

        var singleRemoved = LineDiff.FormatVersionDeltaSummary("Cleanup", 0, 1);
        Assert.Equal("Cleanup · -1 line removed", singleRemoved);

        var noChange = LineDiff.FormatVersionDeltaSummary("Unchanged", 0, 0);
        Assert.Equal("Unchanged · No line changes recorded", noChange);

        var emptyName = LineDiff.FormatVersionDeltaSummary("", 3, 1);
        Assert.Equal("Version · +3 added, -1 removed", emptyName);
    }

    [Fact]
    public void Run136_FormatVersionStatsCard_FormatsAccurately()
    {
        var timestamp = new DateTimeOffset(2026, 10, 17, 14, 30, 0, TimeSpan.FromHours(11));
        var card = LineDiff.FormatVersionStatsCard("Version 2.0", timestamp, 5, 2);
        Assert.Contains("Version 2.0", card);
        Assert.Contains("Saturday", card);
        Assert.Contains("17 October 2026", card);
        Assert.Contains("+5 added, -2 removed", card);

        var noTime = LineDiff.FormatVersionStatsCard("Draft", null, 1, 0);
        Assert.Equal("Draft · +1 line added", noTime);

        var noChange = LineDiff.FormatVersionStatsCard("", null, 0, 0);
        Assert.Equal("Version · No line changes recorded", noChange);
    }

    [Fact]
    public void Run137_FormatRelativeAge_CalculatesCorrectBuckets()
    {
        var refTime = new DateTimeOffset(2026, 10, 17, 16, 0, 0, TimeSpan.FromHours(11));

        Assert.Equal("Just now", LineDiff.FormatRelativeAge(refTime.AddSeconds(-30), refTime));
        Assert.Equal("1 minute ago", LineDiff.FormatRelativeAge(refTime.AddMinutes(-1), refTime));
        Assert.Equal("25 minutes ago", LineDiff.FormatRelativeAge(refTime.AddMinutes(-25), refTime));
        Assert.Equal("1 hour ago", LineDiff.FormatRelativeAge(refTime.AddHours(-1), refTime));
        Assert.Equal("4 hours ago", LineDiff.FormatRelativeAge(refTime.AddHours(-4), refTime));
        Assert.Equal("1 day ago", LineDiff.FormatRelativeAge(refTime.AddDays(-1), refTime));
        Assert.Equal("5 days ago", LineDiff.FormatRelativeAge(refTime.AddDays(-5), refTime));
        Assert.Equal("2 months ago", LineDiff.FormatRelativeAge(refTime.AddDays(-65), refTime));
    }

    [Fact]
    public void Run137_FormatVersionAuditRecord_FormatsCompleteRecord()
    {
        var refTime = new DateTimeOffset(2026, 10, 17, 16, 0, 0, TimeSpan.FromHours(11));
        var createdAt = refTime.AddMinutes(-15);

        var record = LineDiff.FormatVersionAuditRecord("Auto-Save", createdAt, 12, 3, refTime);
        Assert.Contains("Auto-Save", record);
        Assert.Contains("(15 minutes ago)", record);
        Assert.Contains("+12/-3 lines", record);

        var emptyRecord = LineDiff.FormatVersionAuditRecord("", null, 0, 0, refTime);
        Assert.Equal("Version · Unknown time · no line changes", emptyRecord);
    }

    [Fact]
    public void Run138_FormatVersionChangeVelocity_FormatsNetAndInterval()
    {
        var v1 = LineDiff.FormatVersionChangeVelocity(15, 3, TimeSpan.FromMinutes(25));
        Assert.Equal("Velocity: 18 lines (+15/-3) (Net: +12) · 25m interval", v1);

        var v2 = LineDiff.FormatVersionChangeVelocity(0, 5, TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(10)));
        Assert.Equal("Velocity: -5 lines (Net: -5) · 2h 10m interval", v2);

        var v3 = LineDiff.FormatVersionChangeVelocity(0, 0, null);
        Assert.Equal("Velocity: 0 lines (Net: 0)", v3);
    }

    [Fact]
    public void Run139_FormatVersionGrowthAndChurn_CalculatesGrowthAndChurn()
    {
        var zero = LineDiff.FormatVersionGrowthAndChurn(0, 0);
        Assert.Equal("Growth: 0 lines (0% churn)", zero);

        var pureAdd = LineDiff.FormatVersionGrowthAndChurn(10, 0);
        Assert.Equal("Growth: +10 lines (Pure addition, 0% churn)", pureAdd);

        var pureDel = LineDiff.FormatVersionGrowthAndChurn(0, 8);
        Assert.Equal("Contraction: -8 lines (Pure deletion, 100% churn)", pureDel);

        var mixed = LineDiff.FormatVersionGrowthAndChurn(15, 5);
        Assert.Equal("Net: +10 lines · Total churn: 20 lines (25% deletion churn)", mixed);
    }

    [Fact]
    public void Run140_FormatVersionRetentionRate_CalculatesRetention()
    {
        var empty = LineDiff.FormatVersionRetentionRate(0, 0);
        Assert.Equal("Retention: 100.0% (Empty baseline, 0 lines)", empty);

        var perfect = LineDiff.FormatVersionRetentionRate(100, 100);
        Assert.Equal("Retention: 100.0% (All 100 original lines preserved · 0.0% loss)", perfect);

        var partial = LineDiff.FormatVersionRetentionRate(200, 160);
        Assert.Equal("Retention: 80.0% (160/200 lines preserved · 20.0% loss)", partial);
    }

    [Fact]
    public void Run141_FormatVersionEditEntropy_CalculatesEntropy()
    {
        var zero = LineDiff.FormatVersionEditEntropy(0, 0);
        Assert.Equal("Edit entropy: 0.00 (0 lines changed · Stable)", zero);

        var pureAdd = LineDiff.FormatVersionEditEntropy(20, 0);
        Assert.Equal("Edit entropy: 0.00 (+20 lines · Pure addition)", pureAdd);

        var pureDel = LineDiff.FormatVersionEditEntropy(0, 15);
        Assert.Equal("Edit entropy: 0.00 (-15 lines · Pure deletion)", pureDel);

        // Equal additions and deletions: max entropy 1.00
        var balanced = LineDiff.FormatVersionEditEntropy(50, 50, 500);
        Assert.Contains("Edit entropy: 1.00 (+50/-50 lines · High entropy (Balanced refactor))", balanced);
        Assert.Contains("20% document impact", balanced);

        // Asymmetric
        var asymmetric = LineDiff.FormatVersionEditEntropy(95, 5);
        Assert.Contains("Localized / Asymmetric", asymmetric);
    }

    [Fact]
    public void Run142_AuditDiffHunks_And_FormatDiffHunkSummary()
    {
        var zero = LineDiff.FormatDiffHunkSummary("", "");
        Assert.Equal("Edit distribution: 0 edit hunks (Identical versions · 0 lines changed)", zero);

        var before = "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10";
        var after = "line 1\nline 2 MOD\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8 NEW\nline 8 NEW2\nline 9\nline 10";

        var hunks = LineDiff.AuditDiffHunks(before, after);
        Assert.Equal(2, hunks.Count);
        Assert.Equal(1, hunks[0].HunkIndex);
        Assert.Equal(2, hunks[1].HunkIndex);

        var summary = LineDiff.FormatDiffHunkSummary(before, after, totalDocumentLines: 10);
        Assert.Contains("2 edit hunks", summary);
        Assert.Contains("Concentrated refactor", summary);
        Assert.Contains("Largest hunk:", summary);

        // Pre-aggregated metrics overload
        var preAgg = LineDiff.FormatDiffHunkSummary(3, 15, 5, totalDocumentLines: 100, largestHunk: 10);
        Assert.Contains("3 edit hunks (+15/-5 lines · Concentrated refactor)", preAgg);
        Assert.Contains("Largest hunk: ~10 lines", preAgg);
        Assert.Contains("Avg: 6.7 lines", preAgg);
        Assert.Contains("20% doc impact", preAgg);
    }

    [Fact]
    public void Run143_ComputeDiffAgainstLatestSummary_ComputesDeltaAndSimilarity()
    {
        var identical = LineDiff.ComputeDiffAgainstLatestSummary("alpha\nbeta", "alpha\nbeta", "10:00", "12:00");
        Assert.Equal("Diff vs Latest ('10:00' → '12:00'): Identical to latest version (0 changes · 100% similarity)", identical);

        var oldVer = "line 1\nline 2\nline 3\nline 4";
        var latestVer = "line 1\nline 2 modified\nline 3\nline 4\nline 5 added";
        var diffSummary = LineDiff.ComputeDiffAgainstLatestSummary(oldVer, latestVer, "v1", "HEAD");
        Assert.Contains("Diff vs Latest ('v1' → 'HEAD'): +2/-1 lines across", diffSummary);
        Assert.Contains("Net +1 lines", diffSummary);
        Assert.Contains("% similarity)", diffSummary);
    }

    [Fact]
    public void Run144_FormatCherryPickHunksPayload_ExtractsNumberedHunks()
    {
        Assert.Equal("Cherry-pick hunks: 0 hunks (Identical versions)",
            LineDiff.FormatCherryPickHunksPayload("same\ncontent", "same\ncontent"));

        var before = "header\nold line A\nmiddle\nold line B";
        var after = "header\nnew line A1\nnew line A2\nmiddle\nnew line B1";
        var payload = LineDiff.FormatCherryPickHunksPayload(before, after);

        Assert.Contains("Cherry-pick hunks: 2 hunks (+3/-2 lines)", payload);
        Assert.Contains("=== Hunk #1 (@ line 2, +2/-1) ===", payload);
        Assert.Contains("new line A1", payload);
        Assert.Contains("new line A2", payload);
        Assert.Contains("=== Hunk #2 (@ line 5, +1/-1) ===", payload);
        Assert.Contains("new line B1", payload);
    }

    [Fact]
    public void Run145_AuditStructuralHeadingChanges_And_FormatStructuralHeadingDiffSummary()
    {
        Assert.Equal("Structural heading diff: 0 headings found in compared versions",
            LineDiff.FormatStructuralHeadingDiffSummary("plain text", "more plain text"));

        var oldDoc = "# Architecture Overview\n## Legacy Pipeline\n### Submodule A\n```markdown\n# Ignored Fenced Heading\n```";
        var newDoc = "# Architecture Overview\n##Submodule A\n## Submodule A\n### New Telemetry Section";

        var (added, removed, levelChanged, unchanged, details) = LineDiff.AuditStructuralHeadingChanges(oldDoc, newDoc);
        Assert.Equal(1, unchanged);    // # Architecture Overview
        Assert.Equal(1, levelChanged); // ### Submodule A -> ## Submodule A (H3->H2)
        Assert.Equal(1, removed);      // ## Legacy Pipeline
        Assert.Equal(1, added);        // ### New Telemetry Section
        Assert.Contains("H3→H2 'Submodule A'", details);
        Assert.Contains("-H2 'Legacy Pipeline'", details);
        Assert.Contains("+H3 'New Telemetry Section'", details);

        var summary = LineDiff.FormatStructuralHeadingDiffSummary(oldDoc, newDoc);
        Assert.Equal("Structural heading diff: +1 added, -1 removed, 1 level-shifted, =1 unchanged (H3→H2 'Submodule A', -H2 'Legacy Pipeline', +H3 'New Telemetry Section')", summary);
    }

    [Fact]
    public void Run146_AuditLowEntropySnapshots_And_FormatLowEntropySnapshotAudit()
    {
        Assert.Equal("Micro-save audit (1 snapshot): Baseline history (No redundant saves to prune)",
            LineDiff.FormatLowEntropySnapshotAudit(new[] { "only version" }));

        // Test with string snapshots:
        // Snap 0: Baseline
        // Snap 1: Identical / whitespace only -> low entropy
        // Snap 2: +1 word ("edit") -> low entropy (<= 2 lines delta, <= 10 chars delta)
        // Snap 3: Major paragraph addition -> substantive revision
        var snap0 = "Line one\nLine two\nLine three";
        var snap1 = "Line one\nLine two\nLine three  ";
        var snap2 = "Line one\nLine two\nLine three edited";
        var snap3 = "Line one\nLine two\nLine three edited\nAdded major section\nWith extensive multi-line documentation block.";

        var history = new[] { snap0, snap1, snap2, snap3 };
        var (flaggedCount, totalSnapshots, flagged, pct) = LineDiff.AuditLowEntropySnapshots(history);

        Assert.Equal(2, flaggedCount); // snap1 and snap2
        Assert.Equal(4, totalSnapshots);
        Assert.True(flagged[0].IsWhitespaceOnly);
        Assert.False(flagged[1].IsWhitespaceOnly);
        Assert.True(pct > 60.0); // 2 / 3 = 66.7%

        var auditText = LineDiff.FormatLowEntropySnapshotAudit(history);
        Assert.Contains("Micro-save audit (4 snapshots · 66.7% redundant): 2 micro-saves identified", auditText);
        Assert.Contains("#1 [ws-only]", auditText);

        // Test with delta tuples
        var deltas = new List<(int Added, int Removed)>
        {
            (10, 0), // #0 baseline
            (1, 0),  // #1 micro-save
            (0, 0),  // #2 whitespace / 0 delta micro-save
            (25, 8)  // #3 substantive
        };
        var (deltaFlagged, deltaTotal, _, deltaPct) = LineDiff.AuditLowEntropySnapshots(deltas, maxTotalDelta: 2);
        Assert.Equal(2, deltaFlagged);
        Assert.Equal(4, deltaTotal);
        var deltaSummary = LineDiff.FormatLowEntropySnapshotAudit(deltas);
        Assert.Contains("Micro-save audit (4 snapshots · 66.7% redundant): 2 micro-saves identified", deltaSummary);
    }

    [Fact]
    public void Run147_AuditFencedBlockChanges_And_FormatFencedBlockDiffSummary()
    {
        Assert.Equal("Fenced block diff audit: 0 code or diagram blocks detected in either version",
            LineDiff.FormatFencedBlockDiffSummary("", ""));

        var before = """
            # Architecture Guide

            ```csharp
            var app = new MarkSmithApp();
            app.Run();
            ```

            :::smartart
            - Core
              - Pipeline
            :::

            ```mermaid
            graph TD; A-->B;
            ```
            """;

        var after = """
            # Architecture Guide

            ```csharp
            var app = new MarkSmithApp();
            app.ConfigureServices();
            app.Run();
            ```

            :::chart
            Type: Bar
            Data: [10, 20, 30]
            :::

            ```mermaid
            graph TD; A-->B;
            ```
            """;

        var (added, removed, modified, unchanged, details) = LineDiff.AuditFencedBlockChanges(before, after);

        Assert.Equal(1, added);     // :::chart added
        Assert.Equal(1, removed);   // :::smartart removed
        Assert.Equal(1, modified);  // csharp modified (1 extra line)
        Assert.Equal(1, unchanged); // mermaid unchanged

        var summary = LineDiff.FormatFencedBlockDiffSummary(before, after);
        Assert.Contains("Fenced block diff audit (+1/-1/~1 across 4 blocks):", summary);
        Assert.Contains("~ Modified 'csharp'", summary);
        Assert.Contains("- Removed ':::smartart'", summary);
        Assert.Contains("+ Added ':::chart'", summary);
    }

    [Fact]
    public void Run148_AuditTimelineVolatilityIndex()
    {
        Assert.Equal("Timeline volatility audit: 0-1 versions (Insufficient timeline history for churn volatility)",
            LineDiff.FormatTimelineVolatilitySummary(Array.Empty<string>()));

        var v1 = "Line 1";
        var v2 = "Line 1\nLine 2"; // +1
        var v3 = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5\nLine 6\nLine 7\nLine 8"; // +6
        var v4 = "Line 1\nLine 2 modified"; // -7, +1 = 8

        var versions = new List<string> { v1, v2, v3, v4 };
        var (mean, total, maxSpike, spikeRev, rating, summary) =
            LineDiff.AuditTimelineVolatilityIndex(versions);

        Assert.True(total >= 15);
        Assert.True(mean >= 5.0);
        Assert.True(maxSpike >= 6);
        Assert.True(spikeRev >= 3);
        Assert.Equal("Moderate", rating);
        Assert.Contains("Timeline volatility audit (Rating: Moderate · 4 revisions", summary);
        Assert.Contains("Total", summary);

        // Test UnifiedRow overload
        var rows = new List<LineDiff.UnifiedRow>
        {
            new LineDiff.UnifiedRow(LineDiff.Kind.Same, 1, 1, "Same", "  "),
            new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 2, "Added 1", "+ "),
            new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 3, "Added 2", "+ "),
            new LineDiff.UnifiedRow(LineDiff.Kind.Removed, 2, null, "Removed 1", "- ")
        };

        var rowSummary = LineDiff.FormatTimelineVolatilitySummary(rows);
        Assert.Contains("Timeline volatility audit (Rating: Low · Active delta: +2/-1 · Churn: 3 lines)", rowSummary);
    }

    [Fact]
    public void Run149_AssessCherryPickConflictRisk()
    {
        // 1. Identical versions = 0 hunks
        var (t0, _, _, _, r0, s0) = LineDiff.AssessCherryPickConflictRisk("Line 1", "Line 1", "Line 1");
        Assert.Equal(0, t0);
        Assert.Equal("None", r0);
        Assert.Contains("0 hunks", s0);

        // 2. Clean application (target lines match at expected line)
        var before = "Line 1\nTarget Old\nLine 3";
        var after = "Line 1\nReplacement New\nLine 3";
        var currentClean = "Line 1\nTarget Old\nLine 3";

        var (t1, clean1, shifted1, conf1, r1, s1) = LineDiff.AssessCherryPickConflictRisk(before, after, currentClean);
        Assert.Equal(1, t1);
        Assert.Equal(1, clean1);
        Assert.Equal(0, shifted1);
        Assert.Equal(0, conf1);
        Assert.Equal("Clean", r1);
        Assert.Contains("All hunks apply cleanly without collision", s1);

        // 3. Shifted application (target lines match at offset +10 lines)
        var currentShifted = "Intro 1\nIntro 2\nIntro 3\nIntro 4\nIntro 5\nLine 1\nTarget Old\nLine 3";
        var (t2, clean2, shifted2, conf2, r2, s2) = LineDiff.AssessCherryPickConflictRisk(before, after, currentShifted);
        Assert.Equal(1, t2);
        Assert.Equal(0, clean2);
        Assert.Equal(1, shifted2);
        Assert.Equal(0, conf2);
        Assert.Contains("Low (Shifted)", r2);

        // 4. Conflicted application (target line has already been edited)
        var currentConflicted = "Line 1\nTarget Was Completely Rewritten\nLine 3";
        var (t3, clean3, shifted3, conf3, r3, s3) = LineDiff.AssessCherryPickConflictRisk(before, after, currentConflicted);
        Assert.Equal(1, t3);
        Assert.Equal(1, conf3);
        Assert.Equal("High", r3);
        Assert.Contains("conflicted", s3);
    }

    [Fact]
    public void Run150_SynthesizeSemanticVersionMessage_GeneratesConventionalCommit()
    {
        // 1. Identical content
        var snapshot = LineDiff.SynthesizeSemanticVersionMessage("Line 1\nLine 2", "Line 1\nLine 2");
        Assert.Contains("docs: checkpoint snapshot", snapshot);
        Assert.Contains("- Unchanged document content", snapshot);

        // 2. Added section heading (feat)
        var featHeading = LineDiff.SynthesizeSemanticVersionMessage("Intro", "Intro\n# Architecture Roadmap\nRoadmap details");
        Assert.Contains("feat(docs): add section 'Architecture Roadmap'", featHeading);
        Assert.Contains("- Add section: #Architecture Roadmap", featHeading);
        Assert.Contains("Net churn: +2/-0 lines", featHeading);

        // 3. Removed section heading (refactor)
        var refactorHeading = LineDiff.SynthesizeSemanticVersionMessage("# Legacy Architecture\nOld details", "Clean details");
        Assert.Contains("refactor(docs): remove section 'Legacy Architecture'", refactorHeading);
        Assert.Contains("- Remove section: #Legacy Architecture", refactorHeading);

        // 4. Diagram & code block changes (fix(diagrams))
        var diagramDiff = LineDiff.SynthesizeSemanticVersionMessage("Text intro", "Text intro\n```mermaid\nflowchart TD\n```");
        Assert.Contains("fix(diagrams): update code fences and diagram blocks", diagramDiff);
        Assert.Contains("- Modify diagram/code fence structures", diagramDiff);

        // 5. Data table changes (feat(tables))
        var tableDiff = LineDiff.SynthesizeSemanticVersionMessage("Text intro", "Text intro\n| Name | Value |\n| --- | --- |");
        Assert.Contains("feat(tables): update document data tables", tableDiff);
        Assert.Contains("- Modify structured table layout and cell data", tableDiff);

        // 6. Prose expansion (docs)
        var proseAdd = LineDiff.SynthesizeSemanticVersionMessage("Intro", "Intro\nLine 2\nLine 3");
        Assert.Contains("docs(docs): expand document content (+2 lines)", proseAdd);
        Assert.Contains("- Append 2 new content lines", proseAdd);

        // 7. Prose pruning (refactor)
        var proseRemove = LineDiff.SynthesizeSemanticVersionMessage("Intro\nLine 2\nLine 3", "Intro");
        Assert.Contains("refactor(docs): condense document prose (-2 lines)", proseRemove);
        Assert.Contains("- Prune 2 redundant content lines", proseRemove);
    }

    [Fact]
    public void Run151_SynthesizeConventionalCommitWithScope_ExtractsHeadingScopeAndDetectsBreakingChanges()
    {
        // 1. Scoped addition from heading
        var oldDoc = "# Introduction\nIntro details";
        var newDoc = "# Introduction\nIntro details\n# Quantum Telemetry\nTelemetry metrics";
        var scopedAdd = LineDiff.SynthesizeConventionalCommitWithScope(oldDoc, newDoc);

        Assert.Contains("feat(quantum-telemetry): add section 'Quantum Telemetry'", scopedAdd);
        Assert.Contains("- Add section: #Quantum Telemetry", scopedAdd);

        // 2. Breaking change detection on removed heading
        var breakingDocOld = "# Core Architecture\nImportant design rules\n# Legacy Storage\nOld system";
        var breakingDocNew = "# Core Architecture\nImportant design rules";
        var breakingCommit = LineDiff.SynthesizeConventionalCommitWithScope(breakingDocOld, breakingDocNew);

        Assert.Contains("refactor(legacy-storage): remove section 'Legacy Storage'", breakingCommit);
        Assert.Contains("BREAKING CHANGE: Removed section 'Legacy Storage'", breakingCommit);

        // 3. Breaking change detection on deleted table
        var tableDocOld = "Overview\n| Key | Value |\n| --- | --- |\n| Port | 8080 |";
        var tableDocNew = "Overview";
        var tableDelete = LineDiff.SynthesizeConventionalCommitWithScope(tableDocOld, tableDocNew);

        Assert.Contains("refactor(tables):", tableDelete);
        Assert.Contains("BREAKING CHANGE: Removed structured data table", tableDelete);
    }

    [Fact]
    public void Run152_FormatConventionalChangelogSection()
    {
        // 1. Identical documents
        var sameDoc = "# Introduction\nContent here";
        var emptySection = LineDiff.FormatConventionalChangelogSection(sameDoc, sameDoc, "v1.2.0");
        Assert.Contains("## [v1.2.0]", emptySection);
        Assert.Contains("### Documentation", emptySection);
        Assert.Contains("identical version contents", emptySection);

        // 2. Feature addition with breaking change
        var oldDoc = """
            # Architecture
            Original architecture details.
            # Legacy Protocol
            Old communication system.
            """;

        var newDoc = """
            # Architecture
            Original architecture details.
            # Quantum Telemetry
            High-speed telemetry engine.
            | Metric | Value |
            | --- | --- |
            | Ping | 1ms |
            """;

        var changelog = LineDiff.FormatConventionalChangelogSection(oldDoc, newDoc, "v2.0.0");
        Assert.Contains("## [v2.0.0]", changelog);
        Assert.Contains("### Breaking Changes", changelog);
        Assert.Contains("- **legacy-protocol**: removed section 'Legacy Protocol'", changelog);
        Assert.Contains("### Features", changelog);
        Assert.Contains("- **quantum-telemetry**: add section 'Quantum Telemetry'", changelog);
        Assert.Contains("- **tables**: add structured data tables", changelog);
        Assert.Contains("### Refactoring", changelog);
        Assert.Contains("- **legacy-protocol**: remove section 'Legacy Protocol'", changelog);
    }

    [Fact]
    public void Run153_SynthesizeReleaseMilestoneHeader()
    {
        // 1. Direct synthesis with parameter overrides
        var header1 = LineDiff.SynthesizeReleaseMilestoneHeader(
            "1.4.0", "Quantum Architecture", "2026-10-18",
            featureCount: 5, fixCount: 2, breakingCount: 1, totalRevisions: 12);

        Assert.Contains("## [v1.4.0] - 2026-10-18 (Milestone: Quantum Architecture)", header1);
        Assert.Contains("> **Release Overview**: 5 features, 2 fixes, 1 breaking change across 12 revisions.", header1);

        // 2. Default fallback values
        var header2 = LineDiff.SynthesizeReleaseMilestoneHeader();
        Assert.Contains("## [v1.0.0]", header2);
        Assert.Contains("(Milestone: General Availability)", header2);
        Assert.Contains("0 features, 0 fixes, 0 breaking changes", header2);

        // 3. Synthesis from diff rows
        var oldDoc = "# Architecture\nOverview";
        var newDoc = "# Architecture\nOverview\n# Telemetry\nReal-time monitoring\n```csharp\ncode\n```";
        var diff = LineDiff.Diff(oldDoc, newDoc);
        var unified = LineDiff.BuildUnified(diff);

        var header3 = LineDiff.SynthesizeReleaseMilestoneHeader(unified, "v2.1.0", "Cloud Engine");
        Assert.Contains("## [v2.1.0]", header3);
        Assert.Contains("Milestone: Cloud Engine", header3);
        Assert.Contains("1 feature", header3);
        Assert.Contains("1 fix", header3);
    }

    [Fact]
    public void Run154_SynthesizeReleaseBadgesMarkdown()
    {
        // 1. Direct synthesis with parameter overrides
        var badges1 = LineDiff.SynthesizeReleaseBadgesMarkdown(
            "1.4.0", "Quantum Architecture", "stable",
            featureCount: 5, fixCount: 2);

        Assert.Contains("release-v1.4.0-blue.svg", badges1);
        Assert.Contains("milestone-Quantum%20Architecture-success.svg", badges1);
        Assert.Contains("status-stable-green.svg", badges1);
        Assert.Contains("changes-5%20feat%20%C2%B7%202%20fix-orange.svg", badges1);

        // 2. Default fallback values without changes
        var badges2 = LineDiff.SynthesizeReleaseBadgesMarkdown();
        Assert.Contains("release-v1.0.0-blue.svg", badges2);
        Assert.Contains("milestone-General%20Availability-success.svg", badges2);
        Assert.Contains("status-stable-green.svg", badges2);
        Assert.DoesNotContain("changes-", badges2);

        // 3. Synthesis from diff rows
        var oldDoc = "# Architecture\nOverview";
        var newDoc = "# Architecture\nOverview\n# Telemetry\nReal-time monitoring\n```csharp\ncode\n```";
        var diff = LineDiff.Diff(oldDoc, newDoc);
        var unified = LineDiff.BuildUnified(diff);

        var badges3 = LineDiff.SynthesizeReleaseBadgesMarkdown(unified, "v2.1.0", "Cloud Engine", "active");
        Assert.Contains("release-v2.1.0-blue.svg", badges3);
        Assert.Contains("milestone-Cloud%20Engine-success.svg", badges3);
        Assert.Contains("status-active-green.svg", badges3);
        Assert.Contains("changes-1%20feat%20%C2%B7%201%20fix-orange.svg", badges3);
    }

    [Fact]
    public void Run155_SynthesizeReleaseAnnouncementSnippet()
    {
        // 1. Direct synthesis with custom highlights
        var highlights = new[]
        {
            "Added Code Fence Language Coverage & Untagged Block Auditor",
            "Added Leaf Node Termination Depth & Periphery Eccentricity Metrics"
        };
        var snippet1 = LineDiff.SynthesizeReleaseAnnouncementSnippet(
            "1.5.0", "Studio Polish", "Production",
            highlights, featureCount: 2, fixCount: 0, docCount: 1,
            new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("# 🚀 MarkSmith Release v1.5.0 — Studio Polish", snippet1);
        Assert.Contains("> **Status:** `Production` · **Released:** `2026-10-18`", snippet1);
        Assert.Contains("Added Code Fence Language Coverage", snippet1);
        Assert.Contains("- **New Features & Enhancements:** 2", snippet1);
        Assert.Contains("- **Documentation & Specs:** 1", snippet1);
        Assert.Contains("*Crafted with MarkSmith Desktop Studio.*", snippet1);

        // 2. Synthesis from diff rows
        var oldDoc = "# Architecture\nOverview";
        var newDoc = "# Architecture\nOverview\n# Telemetry Pipeline\nReal-time events\n| Col | Val |\n|---|---|\n| A | 1 |";
        var diff = LineDiff.Diff(oldDoc, newDoc);
        var unified = LineDiff.BuildUnified(diff);

        var snippet2 = LineDiff.SynthesizeReleaseAnnouncementSnippet(unified, "v2.0.0", "Data Platform", "Stable", new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains("# 🚀 MarkSmith Release v2.0.0 — Data Platform", snippet2);
        Assert.Contains("Added new section: **Telemetry Pipeline**", snippet2);
        Assert.Contains("Added structured data tables and grid representations.", snippet2);
        Assert.Contains("- **New Features & Enhancements:** 2", snippet2);
    }

    [Fact]
    public void Run156_SynthesizeMultiChannelReleaseDigest()
    {
        // 1. Direct synthesis with custom parameters
        var highlights = new[]
        {
            "Added list item hierarchy & indentation consistency audit",
            "Added radial layout concentric rings & angular spacing audit"
        };
        var digest1 = LineDiff.SynthesizeMultiChannelReleaseDigest(
            "1.6.0", "Ecosystem Launch", "Production",
            highlights, featureCount: 2, fixCount: 0, docCount: 1,
            new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("# 📢 MarkSmith Multi-Channel Release Digest — v1.6.0 (Ecosystem Launch)", digest1);
        Assert.Contains("### 🟦 Microsoft Teams / GitHub Discussion Format", digest1);
        Assert.Contains("### 🟨 Slack Format (mrkdwn)", digest1);
        Assert.Contains("### 🟪 Discord Format", digest1);
        Assert.Contains("*:rocket: MarkSmith v1.6.0 Released — Ecosystem Launch*", digest1);
        Assert.Contains("### 🚀 MarkSmith v1.6.0 — Ecosystem Launch", digest1);
        Assert.Contains("Added list item hierarchy", digest1);

        // 2. Synthesis from diff rows
        var oldDoc = "# Introduction\nOverview";
        var newDoc = "# Introduction\nOverview\n# Vector Graphics\nNew shape tools\n```csharp\nvar x = 1;\n```";
        var diff = LineDiff.Diff(oldDoc, newDoc);
        var unified = LineDiff.BuildUnified(diff);

        var digest2 = LineDiff.SynthesizeMultiChannelReleaseDigest(unified, "v2.2.0", "Core Tools", "GA", new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains("v2.2.0", digest2);
        Assert.Contains("Core Tools", digest2);
        Assert.Contains("Added new section: **Vector Graphics**", digest2);
        Assert.Contains("Added fenced code snippets", digest2);
    }

    [Fact]
    public void Run157_AuditDiffChurnAcceleration()
    {
        // 1. Identical documents (zero churn)
        var sameDoc = "# Heading\nConstant body paragraph.";
        var zeroAudit = LineDiff.AuditDiffChurnAcceleration(sameDoc, sameDoc);
        Assert.Equal(0, zeroAudit.AddedCount);
        Assert.Equal(0, zeroAudit.RemovedCount);
        Assert.Equal(0.0, zeroAudit.ChurnRatio);
        Assert.Equal("Identical (Zero Churn)", zeroAudit.VolatilityCategory);
        Assert.Contains("0 changes", zeroAudit.Summary);

        // 2. High volatility churn with section hotspot
        var oldDoc = """
            # Architecture
            Original monolithic pipeline.
            Single thread processing.
            # Security
            Standard RBAC controls.
            """;
        var newDoc = """
            # Architecture
            High-throughput distributed actor model.
            Multi-core SIMD vector pipelines.
            Lock-free memory channels.
            Dynamic batch scheduling.
            # Security
            Standard RBAC controls.
            """;
        var churnAudit = LineDiff.AuditDiffChurnAcceleration(oldDoc, newDoc);
        Assert.True(churnAudit.AddedCount >= 4);
        Assert.True(churnAudit.RemovedCount >= 2);
        Assert.True(churnAudit.ChurnRatio >= 0.50);
        Assert.Equal("High Volatility (Rapid Revision Churn)", churnAudit.VolatilityCategory);
        Assert.Equal("Architecture", churnAudit.HotspotSectionHeading);
        Assert.Contains("Architecture", churnAudit.Summary);
        Assert.Contains("churn ratio", churnAudit.Summary);
    }

    [Fact]
    public void Run158_SynthesizePullRequestDescription()
    {
        // 1. Zero diff changes
        var emptyPR = LineDiff.SynthesizePullRequestDescription(Array.Empty<LineDiff.UnifiedRow>());
        Assert.Equal(0, emptyPR.LinesAdded);
        Assert.Equal(0, emptyPR.LinesRemoved);
        Assert.Equal("Low (No Changes)", emptyPR.RiskTier);
        Assert.Contains("0 changes", emptyPR.Summary);

        // 2. Meaningful feature PR
        var beforeDoc = """
            # Engine Overview
            Baseline engine v1.
            """;
        var afterDoc = """
            # Engine Overview
            High performance SIMD engine v2.
            Accelerated OpenXML processing.
            Zero allocation pipeline.
            # Metrics
            Benchmark throughput: 120MB/s.
            """;
        var pr = LineDiff.SynthesizePullRequestDescription(beforeDoc, afterDoc, "feat(engine): SIMD acceleration overhaul");
        Assert.True(pr.LinesAdded >= 4);
        Assert.Equal(1, pr.LinesRemoved);
        Assert.Equal("Low (Targeted Refinement)", pr.RiskTier);
        Assert.Contains("Engine Overview", pr.AffectedAreas);
        Assert.Contains("feat(engine): SIMD acceleration overhaul", pr.PullRequestMarkdown);
        Assert.Contains("Verification Checklist", pr.PullRequestMarkdown);
        Assert.Contains("PR synthesis", pr.Summary);

        // Medium risk (>50 delta lines)
        var bigAfter = string.Join("\n", Enumerable.Range(0, 55).Select(i => $"Line {i} content."));
        var medPr = LineDiff.SynthesizePullRequestDescription("", bigAfter);
        Assert.Equal("Medium (Component / Feature Polish)", medPr.RiskTier);

        // 3. High risk breaking change (removed heading or table)
        var breakBefore = """
            # Public API Contract
            | Method | Return |
            | --- | --- |
            | Execute() | bool |
            """;
        var breakAfter = """
            # Internal Pipeline
            No more tables.
            """;
        var breakPr = LineDiff.SynthesizePullRequestDescription(breakBefore, breakAfter);
        Assert.Equal("High (Structural / Breaking Changes)", breakPr.RiskTier);
    }

    [Fact]
    public void Run159_SynthesizeConventionalCommitMessage_ClassifiesTypesAndBreakingChanges()
    {
        // 1. Empty changes
        var emptyCommit = LineDiff.SynthesizeConventionalCommitMessage(Array.Empty<LineDiff.UnifiedRow>(), scope: "docs");
        Assert.Equal("chore", emptyCommit.CommitType);
        Assert.Equal("docs", emptyCommit.Scope);
        Assert.False(emptyCommit.IsBreakingChange);
        Assert.Contains("chore(docs): no file changes recorded", emptyCommit.FullCommitMessage);

        // 2. Feat additions
        var beforeFeat = "Line 1\nLine 2";
        var afterFeat = string.Join("\n", Enumerable.Range(1, 15).Select(i => $"Line {i} feature detail"));
        var featCommit = LineDiff.SynthesizeConventionalCommitMessage(beforeFeat, afterFeat, scope: "desktop");
        Assert.Equal("feat", featCommit.CommitType);
        Assert.Equal("desktop", featCommit.Scope);
        Assert.False(featCommit.IsBreakingChange);
        Assert.Contains("feat(desktop):", featCommit.FullCommitMessage);
        Assert.Contains("Delta:", featCommit.Body);

        // 3. Fix with bug keywords
        var beforeFix = "Line 1\nBroken error logic\nLine 3";
        var afterFix = "Line 1\nFixed bug in error handler\nLine 3";
        var fixCommit = LineDiff.SynthesizeConventionalCommitMessage(beforeFix, afterFix, scope: "engine");
        Assert.Equal("fix", fixCommit.CommitType);
        Assert.Equal("engine", fixCommit.Scope);
        Assert.Contains("fix(engine):", fixCommit.FullCommitMessage);

        // 4. Breaking change (removed heading and tables)
        var breakBefore = """
            # Public Endpoint
            | Arg | Type |
            | --- | --- |
            | Id  | int  |
            """;
        var breakAfter = "Replaced with internal handler.";
        var breakCommit = LineDiff.SynthesizeConventionalCommitMessage(breakBefore, breakAfter, scope: "api");
        Assert.True(breakCommit.IsBreakingChange);
        Assert.Contains("!", breakCommit.FullCommitMessage);
        Assert.Contains("BREAKING CHANGE:", breakCommit.FullCommitMessage);
        Assert.Contains("BREAKING CHANGE:", breakCommit.BreakingChangeNotice);
    }

    [Fact]
    public void SynthesizeSemanticVersionRecommendation_AccuratelyCalculatesBumpsAndChangelogs()
    {
        // 1. Unchanged
        var unch = LineDiff.SynthesizeSemanticVersionRecommendation("Same text", "Same text", "1.2.3");
        Assert.Equal("1.2.3", unch.CurrentVersion);
        Assert.Equal("1.2.3", unch.RecommendedVersion);
        Assert.Equal("None", unch.BumpType);

        // 2. Patch bump (minor revisions)
        var beforePatch = "Line 1\nSome typos\nLine 3";
        var afterPatch = "Line 1\nFixed typos\nLine 3";
        var patchRes = LineDiff.SynthesizeSemanticVersionRecommendation(beforePatch, afterPatch, "1.2.3");
        Assert.Equal("1.2.3", patchRes.CurrentVersion);
        Assert.Equal("1.2.4", patchRes.RecommendedVersion);
        Assert.Equal("Patch", patchRes.BumpType);
        Assert.Contains("## [1.2.4]", patchRes.ChangelogMarkdown);

        // 3. Minor bump (new section / headings)
        var beforeMinor = "# Overview\nInitial content";
        var afterMinor = """
            # Overview
            Initial content

            # New Feature Architecture
            Extensive feature additions with substantial content across multiple lines.
            """;
        var minorRes = LineDiff.SynthesizeSemanticVersionRecommendation(beforeMinor, afterMinor, "1.2.3");
        Assert.Equal("1.2.3", minorRes.CurrentVersion);
        Assert.Equal("1.3.0", minorRes.RecommendedVersion);
        Assert.Equal("Minor", minorRes.BumpType);
        Assert.Contains("Added section `New Feature Architecture`", minorRes.ChangelogMarkdown);

        // 4. Major bump (breaking change - removed heading & table)
        var beforeMajor = """
            # Public API Contract
            | Method | Return |
            | --- | --- |
            | Execute | void |
            """;
        var afterMajor = "Internalized API.";
        var majorRes = LineDiff.SynthesizeSemanticVersionRecommendation(beforeMajor, afterMajor, "1.2.3");
        Assert.Equal("1.2.3", majorRes.CurrentVersion);
        Assert.Equal("2.0.0", majorRes.RecommendedVersion);
        Assert.Equal("Major", majorRes.BumpType);
        Assert.True(majorRes.HasBreakingChanges);
        Assert.Contains("### Breaking Changes", majorRes.ChangelogMarkdown);
    }

    [Fact]
    public void Run161_SynthesizeReleaseNotesAndMigrationGuide_ExtractsFeaturesFixesAndMigrationSteps()
    {
        // 1. Minor feature additions
        var beforeFeat = "# Base Doc\nExisting content";
        var afterFeat = """
            # Base Doc
            Existing content

            # New Feature Module
            feat: Add interactive chart rendering support.
            fix: Fix tooltip clipping on small viewports.
            """;
        var featNotes = LineDiff.SynthesizeReleaseNotesAndMigrationGuide(beforeFeat, afterFeat, "1.4.2");
        Assert.Equal("1.4.2", featNotes.CurrentVersion);
        Assert.Equal("1.5.0", featNotes.RecommendedVersion);
        Assert.Equal("Minor (Feature Enhancements)", featNotes.RiskTier);
        Assert.True(featNotes.FeatureCount >= 1);
        Assert.True(featNotes.FixCount >= 1);
        Assert.Equal(0, featNotes.BreakingCount);
        Assert.Contains("Zero breaking changes", featNotes.MigrationSteps[0]);
        Assert.Contains("# Release Notes — v1.5.0", featNotes.ReleaseNotesMarkdown);
        Assert.Contains("Features & Enhancements", featNotes.ReleaseNotesMarkdown);

        // 2. Breaking changes with migration guide
        var beforeBreak = """
            # Legacy Authentication API
            | Param | Value |
            | --- | --- |
            | token | string |
            """;
        var afterBreak = """
            BREAKING CHANGE: Removed legacy auth endpoint in favor of OAuth2.
            feat: Added OAuth2 authorization flows.
            """;
        var breakNotes = LineDiff.SynthesizeReleaseNotesAndMigrationGuide(beforeBreak, afterBreak, "1.4.2");
        Assert.Equal("1.4.2", breakNotes.CurrentVersion);
        Assert.Equal("2.0.0", breakNotes.RecommendedVersion);
        Assert.Equal("Major (Breaking Changes)", breakNotes.RiskTier);
        Assert.True(breakNotes.BreakingCount >= 1);
        Assert.NotEmpty(breakNotes.MigrationSteps);
        Assert.Contains("Migration Guide", breakNotes.ReleaseNotesMarkdown);
        Assert.Contains("Release notes", breakNotes.Summary);
    }

    [Fact]
    public void Run162_AuditCommitChurnHotspots_DetectsHotspotsAndCalculatesVolatility()
    {
        // 1. Unchanged diff (0 additions, 0 deletions)
        var unchangedAudit = LineDiff.AuditCommitChurnHotspots("constant text", "constant text");
        Assert.Equal(0, unchangedAudit.AddedRows);
        Assert.Equal(0, unchangedAudit.RemovedRows);
        Assert.Empty(unchangedAudit.Hotspots);
        Assert.Equal(0.0, unchangedAudit.VolatilityIndex);
        Assert.Equal("Stable Baseline (Zero Churn)", unchangedAudit.VolatilityCategory);
        Assert.Equal("Commit churn audit: 0 modifications · Document is unchanged", LineDiff.FormatCommitChurnHotspotsSummary(
            unchangedAudit.TotalRows, unchangedAudit.AddedRows, unchangedAudit.RemovedRows,
            unchangedAudit.Hotspots.Count, unchangedAudit.VolatilityIndex, unchangedAudit.ChurnEntropy,
            unchangedAudit.VolatilityCategory, unchangedAudit.Hotspots));
        Assert.Contains("100% stable", unchangedAudit.Summary);

        // 2. Localized hotspot cluster
        var beforeDoc = """
            # Title
            Unchanged line 1
            Unchanged line 2
            Unchanged line 3
            Old paragraph A
            Old paragraph B
            Unchanged footer 1
            Unchanged footer 2
            """;
        var afterDoc = """
            # Title
            Unchanged line 1
            Unchanged line 2
            Unchanged line 3
            New replacement line 1
            New replacement line 2
            New replacement line 3
            New replacement line 4
            Unchanged footer 1
            Unchanged footer 2
            """;
        var hotspotAudit = LineDiff.AuditCommitChurnHotspots(beforeDoc, afterDoc);
        Assert.True(hotspotAudit.AddedRows >= 4);
        Assert.True(hotspotAudit.RemovedRows >= 2);
        Assert.NotEmpty(hotspotAudit.Hotspots);
        Assert.True(hotspotAudit.VolatilityIndex > 0.0);
        Assert.True(hotspotAudit.PeakDensity > 0.0);
        Assert.Contains("hotspot", hotspotAudit.Summary.ToLowerInvariant());

        // 3. Unified row overload directly
        var diff = LineDiff.Diff(beforeDoc, afterDoc);
        var unified = LineDiff.BuildUnified(diff);
        var unifiedAudit = LineDiff.AuditCommitChurnHotspots(unified);
        Assert.Equal(hotspotAudit.AddedRows, unifiedAudit.AddedRows);
        Assert.Equal(hotspotAudit.RemovedRows, unifiedAudit.RemovedRows);
        Assert.Equal(hotspotAudit.VolatilityIndex, unifiedAudit.VolatilityIndex);
    }

    [Fact]
    public void Run163_AuditChunkContextBoundaries_DetectsHunkSizesAndDiscontinuities()
    {
        // 1. Unchanged diff (0 hunks)
        var emptyAudit = LineDiff.AuditChunkContextBoundaries(Array.Empty<LineDiff.UnifiedRow>());
        Assert.Equal(0, emptyAudit.TotalHunks);
        Assert.Equal(0, emptyAudit.SemanticallyAlignedHunks);
        Assert.Equal(0, emptyAudit.TruncatedConstructCount);
        Assert.Equal(0, emptyAudit.OverlappingHunkCount);
        Assert.Equal("Clean Baseline (Zero Diff Deltas)", emptyAudit.AlignmentCategory);
        Assert.Equal(100.0, emptyAudit.BoundaryIntegrityScore);
        Assert.Equal("Diff chunk boundary audit: 0 hunks · Document is unchanged (Zero diff deltas)", emptyAudit.Summary);

        // 2. Multi-chunk diff with separated modifications
        var beforeLines = new List<string>();
        for (int i = 1; i <= 30; i++) beforeLines.Add($"Unchanged line {i}");
        var afterLines = new List<string>(beforeLines);
        afterLines[2] = "Modified line 3";
        afterLines[25] = "Modified line 26";

        var beforeDoc = string.Join("\n", beforeLines);
        var afterDoc = string.Join("\n", afterLines);

        var chunkAudit = LineDiff.AuditChunkContextBoundaries(beforeDoc, afterDoc);
        Assert.True(chunkAudit.TotalHunks >= 2);
        Assert.True(chunkAudit.BoundaryIntegrityScore > 0);
        Assert.NotEmpty(chunkAudit.Hunks);
        Assert.Contains("Diff chunk boundary audit", chunkAudit.Summary);

        // 3. Verify format summary consistency
        string formatted = LineDiff.FormatChunkBoundarySummary(
            chunkAudit.TotalHunks, chunkAudit.SemanticallyAlignedHunks, chunkAudit.TruncatedConstructCount,
            chunkAudit.OverlappingHunkCount, chunkAudit.MeanContextPadding, chunkAudit.BoundaryIntegrityScore,
            chunkAudit.AlignmentCategory);
        Assert.Equal(chunkAudit.Summary, formatted);
    }

    [Fact]
    public void Run164_AuditSemanticHunkFoldingAndTokenContext_PinsScopesAndHunkFolding()
    {
        // 1. Unchanged document (0 hunks)
        var emptyAudit = LineDiff.AuditSemanticHunkFoldingAndTokenContext("identical document", "identical document");
        Assert.Equal(0, emptyAudit.TotalHunks);
        Assert.Equal(0, emptyAudit.PinnedHunkCount);
        Assert.Equal(0, emptyAudit.UnpinnedHunkCount);
        Assert.Equal(0, emptyAudit.FoldableHunkCount);
        Assert.Equal(100.0, emptyAudit.SemanticCoverageScore);
        Assert.Equal("Clean Baseline (Zero Diff Deltas)", emptyAudit.FoldingCategory);
        Assert.Equal("Diff semantic hunk folding: 0 hunks · Document is unchanged (Zero diff deltas)", emptyAudit.Summary);

        // 2. Multi-hunk diff inside markdown sections with pinned heading scopes
        var beforeDoc = """
            # Architecture Overview
            Unchanged line 1
            Old architecture paragraph
            Unchanged line 2

            # Telemetry Metrics
            Unchanged metric 1
            Old telemetry parameter
            Unchanged metric 2
            """;

        var afterDoc = """
            # Architecture Overview
            Unchanged line 1
            New architecture section
            New architecture pipeline
            Unchanged line 2

            # Telemetry Metrics
            Unchanged metric 1
            Updated telemetry parameter
            Unchanged metric 2
            """;

        var foldingAudit = LineDiff.AuditSemanticHunkFoldingAndTokenContext(beforeDoc, afterDoc);
        Assert.Equal(2, foldingAudit.TotalHunks);
        Assert.Equal(2, foldingAudit.PinnedHunkCount);
        Assert.Equal(0, foldingAudit.UnpinnedHunkCount);
        Assert.Equal(2, foldingAudit.DistinctScopesCount);
        Assert.Equal(100.0, foldingAudit.SemanticCoverageScore);
        Assert.Contains("Comprehensive Syntactic Scope Pinning", foldingAudit.FoldingCategory);
        Assert.Contains(foldingAudit.DiscoveredScopes, s => s.Contains("Architecture Overview"));
        Assert.Contains(foldingAudit.DiscoveredScopes, s => s.Contains("Telemetry Metrics"));
        Assert.All(foldingAudit.Hunks, h => Assert.True(h.HasPinnedContext));

        // 3. Verify format summary consistency
        string formatted = LineDiff.FormatSemanticHunkFoldingSummary(
            foldingAudit.TotalHunks, foldingAudit.PinnedHunkCount, foldingAudit.UnpinnedHunkCount,
            foldingAudit.FoldableHunkCount, foldingAudit.DistinctScopesCount, foldingAudit.SemanticCoverageScore,
            foldingAudit.FoldingCategory);
        Assert.Equal(foldingAudit.Summary, formatted);
    }

    [Fact]
    public void Run165_AuditWordLevelDiffChunkingAndTokenShifts_EvaluatesTokenGranularityAndShifts()
    {
        // 1. Identical text (0 modified lines)
        var unchAudit = LineDiff.AuditWordLevelDiffChunkingAndTokenShifts("Single line unchanged", "Single line unchanged");
        Assert.Equal(0, unchAudit.TotalModifiedLinePairs);
        Assert.Equal(0, unchAudit.TotalWordSubstitutions);
        Assert.Equal(100.0, unchAudit.IntraLineDiffResolutionScore);
        Assert.Equal("Zero Diffs (Unchanged)", unchAudit.ResolutionCategory);
        Assert.Contains("0 modified lines", unchAudit.Summary);

        // 2. Surgical word-level replacements
        var oldDoc = "The quick brown fox jumps over the lazy dog";
        var newDoc = "The fast brown fox leaps over the sleepy dog";
        var wordAudit = LineDiff.AuditWordLevelDiffChunkingAndTokenShifts(oldDoc, newDoc);

        Assert.Equal(1, wordAudit.TotalModifiedLinePairs);
        Assert.True(wordAudit.TotalWordSubstitutions >= 1);
        Assert.True(wordAudit.IntraLineDiffResolutionScore > 0.0);
        Assert.NotEmpty(wordAudit.WordChunks);
        Assert.Equal(wordAudit.Summary, LineDiff.FormatWordLevelDiffSummary(
            1, wordAudit.TotalWordSubstitutions, wordAudit.TotalWordInsertions, wordAudit.TotalWordDeletions,
            wordAudit.IdenticalWordCount, wordAudit.MeanTokenSimilarity, wordAudit.IntraLineDiffResolutionScore,
            wordAudit.ResolutionCategory, wordAudit.WordChunks));

        // 3. Word shift reordering
        var oldShift = "alpha beta gamma delta";
        var newShift = "delta alpha beta gamma";
        var shiftAudit = LineDiff.AuditWordLevelDiffChunkingAndTokenShifts(oldShift, newShift);

        Assert.Equal(1, shiftAudit.TotalModifiedLinePairs);
        Assert.NotEmpty(shiftAudit.WordChunks);
    }

    [Fact]
    public void Run166_AuditCharacterLevenshteinEditDistance_CalculatesTypoAndEditDistances()
    {
        // 1. Empty diff
        var emptyAudit = LineDiff.AuditCharacterLevenshteinEditDistance("", "");
        Assert.Equal(0, emptyAudit.TotalModifiedLinePairs);
        Assert.Equal(0, emptyAudit.TotalEditDistance);
        Assert.Equal("No Modified Lines", emptyAudit.GranularityCategory);
        Assert.Equal(emptyAudit.Summary, LineDiff.FormatLevenshteinDistanceSummary(0, 0, 0.0, 0, 0, 0, 0, 0.0, "None", "No Modified Lines"));

        // 2. Single-character typo edit: "recived" -> "received"
        var oldTypo = "int recivedCount = 5;";
        var newTypo = "int receivedCount = 5;";
        var typoAudit = LineDiff.AuditCharacterLevenshteinEditDistance(oldTypo, newTypo);

        Assert.Equal(1, typoAudit.TotalModifiedLinePairs);
        Assert.Equal(1, typoAudit.TotalEditDistance);
        Assert.Equal(1, typoAudit.TypoCount);
        Assert.Equal(100.0, typoAudit.TypoConcentrationRatio);
        Assert.Equal("Single-Character Typos", typoAudit.DominantEditType);
        Assert.NotEmpty(typoAudit.Pairs);
        Assert.Equal(typoAudit.Summary, LineDiff.FormatLevenshteinDistanceSummary(
            1, typoAudit.TotalEditDistance, typoAudit.MeanEditDistance, typoAudit.TypoCount,
            typoAudit.MinorTweakCount, typoAudit.ModerateRevisionCount, typoAudit.MajorRewriteCount,
            typoAudit.TypoConcentrationRatio, typoAudit.DominantEditType, typoAudit.GranularityCategory));

        // 3. Major rewrite
        var oldMajor = "int x = 42;";
        var newMajor = "string formattedOutputSummaryReport = BuildDetailedWorkspaceReportHierarchy(model);";
        var rewriteAudit = LineDiff.AuditCharacterLevenshteinEditDistance(oldMajor, newMajor);

        Assert.Equal(1, rewriteAudit.TotalModifiedLinePairs);
        Assert.Equal(1, rewriteAudit.MajorRewriteCount);
        Assert.Equal("Major Rewrites", rewriteAudit.DominantEditType);
    }

    [Fact]
    public void Run167_AuditSemanticDiffTokenEntropy_CalculatesShannonBitsAndDrift()
    {
        // 1. Unchanged document (0 diffs)
        var emptyAudit = LineDiff.AuditSemanticDiffTokenEntropy("", "");
        Assert.Equal(0, emptyAudit.TotalBeforeTokens);
        Assert.Equal(0, emptyAudit.TotalAfterTokens);
        Assert.Equal(0.0, emptyAudit.EntropyDeltaBits);
        Assert.Equal(100.0, emptyAudit.RevisionDriftScore);
        Assert.Equal("Zero Diff Deltas (Identical)", emptyAudit.DriftCategory);
        Assert.Equal(emptyAudit.Summary, LineDiff.FormatSemanticDiffTokenEntropySummary(0, 0, 0, 0, 0.0, 0.0, 0.0, 1.0, 100.0, "Zero Diff Deltas (Identical)"));

        // 2. Syntactic equilibrium refactor (equivalent information density)
        var oldCode = "int totalUserCount = CalculateUsers(activeList);";
        var newCode = "int totalUsersActive = ComputeActiveUsers(activeList);";
        var eqAudit = LineDiff.AuditSemanticDiffTokenEntropy(oldCode, newCode);

        Assert.True(eqAudit.TotalBeforeTokens > 0);
        Assert.True(eqAudit.TotalAfterTokens > 0);
        Assert.True(Math.Abs(eqAudit.EntropyDeltaBits) < 0.25);
        Assert.Equal("Syntactic Equilibrium / Equivalent Information Density", eqAudit.DriftCategory);
        Assert.True(eqAudit.RevisionDriftScore >= 80.0);
        Assert.NotEmpty(eqAudit.TopTokens);

        // 3. Information expanding refactor
        var simpleCode = "int x = 1;";
        var complexCode = "int complexTelemetryDataPointWithTimestamp = CalculateStandardDeviationOfAllRegisteredNodesInGraph(payloadMatrix);";
        var expandAudit = LineDiff.AuditSemanticDiffTokenEntropy(simpleCode, complexCode);

        Assert.True(expandAudit.EntropyDeltaBits > 0.5);
        Assert.Equal("Information Expanding Refactor", expandAudit.DriftCategory);
        Assert.True(expandAudit.VocabularyExpansionRatio > 1.5);

        // 4. Formatter test
        string formatted = LineDiff.FormatSemanticDiffTokenEntropySummary(
            expandAudit.TotalBeforeTokens, expandAudit.TotalAfterTokens, expandAudit.DistinctBeforeTokens,
            expandAudit.DistinctAfterTokens, expandAudit.BeforeEntropyBits, expandAudit.AfterEntropyBits,
            expandAudit.EntropyDeltaBits, expandAudit.VocabularyExpansionRatio, expandAudit.RevisionDriftScore,
            expandAudit.DriftCategory);
        Assert.Equal(expandAudit.Summary, formatted);
    }

    [Fact]
    public void AuditDiffLineWrapAndOverflow_DetectsOverflows()
    {
        // 1. Empty rows
        var emptyAudit = LineDiff.AuditDiffLineWrapAndOverflow(Array.Empty<LineDiff.UnifiedRow>());
        Assert.Equal(0, emptyAudit.TotalRowsEvaluated);
        Assert.Equal(100.0, emptyAudit.HorizontalStabilityScore);
        Assert.Equal("Empty Diff", emptyAudit.Category);

        // 2. Clean rows <= 120 cols
        var r1 = new LineDiff.UnifiedRow(LineDiff.Kind.Same, 1, 1, "Standard line 1", "  ");
        var r2 = new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 2, "Standard line 2", "+ ");
        var cleanAudit = LineDiff.AuditDiffLineWrapAndOverflow(new[] { r1, r2 }, maxColumnWidth: 120);

        Assert.Equal(2, cleanAudit.TotalRowsEvaluated);
        Assert.Equal(2, cleanAudit.StandardWidthCount);
        Assert.Equal(0, cleanAudit.ModerateOverflowCount);
        Assert.Equal(0, cleanAudit.SevereOverflowCount);
        Assert.Equal(100.0, cleanAudit.HorizontalStabilityScore);
        Assert.Equal("Zero Horizontal Spill / Optimal Wrap Boundaries", cleanAudit.Category);

        // 3. Overflow row > 160 cols
        var longLine = new string('A', 170);
        var rLong = new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 3, longLine, "+ ");
        var overflowAudit = LineDiff.AuditDiffLineWrapAndOverflow(new[] { r1, rLong }, maxColumnWidth: 120);

        Assert.Equal(2, overflowAudit.TotalRowsEvaluated);
        Assert.Equal(1, overflowAudit.StandardWidthCount);
        Assert.Equal(1, overflowAudit.SevereOverflowCount);
        Assert.True(overflowAudit.HorizontalStabilityScore < 100.0);
        Assert.Single(overflowAudit.Overflows);
        Assert.Equal(170, overflowAudit.Overflows[0].ColumnLength);
    }

    [Fact]
    public void Run169_AuditGitConflictMarkersAndRebaseResidue_DetectsConflictBlocksAndOrphans()
    {
        // 1. Clean diff / document
        var cleanAudit = LineDiff.AuditGitConflictMarkersAndRebaseResidue("line 1\nline 2\nline 3");
        Assert.Equal(3, cleanAudit.TotalLinesEvaluated);
        Assert.Equal(0, cleanAudit.ConflictBlockCount);
        Assert.Equal(0, cleanAudit.UnresolvedMarkerCount);
        Assert.False(cleanAudit.HasActiveConflicts);
        Assert.Equal(100.0, cleanAudit.ConflictFreeIntegrityScore);
        Assert.Equal("Clean Working Tree / Zero Conflict Residue", cleanAudit.Category);

        // 2. Document with valid Git merge conflict block
        var conflictDoc = string.Join("\n",
            "# Document",
            "<<<<<<< HEAD",
            "feature: added in local branch",
            "=======",
            "feature: modified in incoming branch",
            ">>>>>>> feature-branch",
            "Footer note"
        );
        var conflictAudit = LineDiff.AuditGitConflictMarkersAndRebaseResidue(conflictDoc);
        Assert.Equal(7, conflictAudit.TotalLinesEvaluated);
        Assert.Equal(1, conflictAudit.ConflictBlockCount);
        Assert.Equal(3, conflictAudit.UnresolvedMarkerCount);
        Assert.Equal(0, conflictAudit.MalformedConflictCount);
        Assert.True(conflictAudit.HasActiveConflicts);
        Assert.Equal("HEAD", conflictAudit.ConflictBlocks[0].OursLabel);
        Assert.Equal("feature-branch", conflictAudit.ConflictBlocks[0].TheirsLabel);
        Assert.Equal(1, conflictAudit.ConflictBlocks[0].OursLineCount);
        Assert.Equal(1, conflictAudit.ConflictBlocks[0].TheirsLineCount);
        Assert.False(conflictAudit.ConflictBlocks[0].IsMalformed);
        Assert.Equal("Active Merge Conflict Block(s) Detected", conflictAudit.Category);

        // 3. Document with orphan conflict marker residue
        var orphanDoc = string.Join("\n",
            "line 1",
            "=======",
            "line 2"
        );
        var orphanAudit = LineDiff.AuditGitConflictMarkersAndRebaseResidue(orphanDoc);
        Assert.Equal(1, orphanAudit.ConflictBlockCount);
        Assert.Equal(1, orphanAudit.MalformedConflictCount);
        Assert.True(orphanAudit.ConflictBlocks[0].IsMalformed);
        Assert.Equal("Malformed / Corrupted Git Conflict Markers", orphanAudit.Category);
    }

    [Fact]
    public void Run170_AuditSemanticDiffCoChangeClusters_DetectsCoupledHunksAndClusters()
    {
        // 1. Empty rows
        var emptyAudit = LineDiff.AuditSemanticDiffCoChangeClusters(Array.Empty<LineDiff.UnifiedRow>());
        Assert.Equal(0, emptyAudit.TotalChangedLines);
        Assert.Equal(0, emptyAudit.ClusterCount);
        Assert.Equal(100.0, emptyAudit.SemanticCouplingScore);
        Assert.Equal("Identical Versions", emptyAudit.CouplingCategory);

        // 2. Diff with two hunks sharing identifier tokens (e.g. UserService and Authenticate)
        var r1 = new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 10, "public class UserService : IUserService", "+ ");
        var r2 = new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 11, "    public bool Authenticate(string user, string pass)", "+ ");
        var r3 = new LineDiff.UnifiedRow(LineDiff.Kind.Same, 12, 12, "    {", "  ");
        var r4 = new LineDiff.UnifiedRow(LineDiff.Kind.Same, 13, 13, "        // context line", "  ");
        var r5 = new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 40, "var userToken = UserService.Authenticate(user, pass);", "+ ");

        var audit = LineDiff.AuditSemanticDiffCoChangeClusters(new[] { r1, r2, r3, r4, r5 });
        Assert.Equal(3, audit.TotalChangedLines);
        Assert.True(audit.ClusterCount >= 1);
        Assert.True(audit.ClusteredLineCount >= 1);
        Assert.True(audit.SemanticCouplingScore >= 70.0);
        Assert.NotEmpty(audit.Clusters);
        Assert.True(audit.Clusters[0].PrimaryIdentifier == "UserService" || audit.Clusters[0].AssociatedTokens.Contains("UserService"));

        // 3. Formatter test
        string formatted = LineDiff.FormatCoChangeClusterSummary(
            audit.TotalDiffLines, audit.TotalChangedLines, audit.ClusterCount,
            audit.ClusteredLineCount, audit.IsolatedLineCount,
            audit.SemanticCouplingScore, audit.CouplingCategory);
        Assert.Equal(audit.Summary, formatted);
    }

    [Fact]
    public void Run171_AuditSemanticHunkCouplingAndPropagation_ValidatesCrossHunkDependencies()
    {
        // 1. Empty rows
        var emptyAudit = LineDiff.AuditSemanticHunkCouplingAndPropagation(Array.Empty<LineDiff.UnifiedRow>());
        Assert.Equal(0, emptyAudit.TotalHunks);
        Assert.Equal(0, emptyAudit.CoupledHunkCount);
        Assert.Equal(0, emptyAudit.IndependentHunkCount);
        Assert.Equal(100.0, emptyAudit.HunkPropagationScore);
        Assert.Equal("Zero Diffs (Unchanged)", emptyAudit.CouplingCategory);

        // 2. Single isolated hunk
        var rSingle = new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 1, "var customerService = new CustomerService();", "+ ");
        var singleAudit = LineDiff.AuditSemanticHunkCouplingAndPropagation(new[] { rSingle });
        Assert.Equal(1, singleAudit.TotalHunks);
        Assert.Equal(0, singleAudit.CoupledHunkCount);
        Assert.Equal(1, singleAudit.IndependentHunkCount);
        Assert.Equal(0, singleAudit.TotalCouplingLinks);
        Assert.Equal("Isolated Single Hunk (Local Change)", singleAudit.CouplingCategory);

        // 3. Multi-hunk edit sharing identifiers
        var r1 = new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 1, "public class CustomerService", "+ ");
        var r2 = new LineDiff.UnifiedRow(LineDiff.Kind.Same, 2, 2, "    // context line 1", "  ");
        var r3 = new LineDiff.UnifiedRow(LineDiff.Kind.Same, 3, 3, "    // context line 2", "  ");
        var r4 = new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 25, "var cs = new CustomerService();", "+ ");
        var coupledAudit = LineDiff.AuditSemanticHunkCouplingAndPropagation(new[] { r1, r2, r3, r4 });

        Assert.Equal(2, coupledAudit.TotalHunks);
        Assert.Equal(2, coupledAudit.CoupledHunkCount);
        Assert.Equal(0, coupledAudit.IndependentHunkCount);
        Assert.Equal(1, coupledAudit.TotalCouplingLinks);
        Assert.Equal(100.0, coupledAudit.HunkPropagationScore);
        Assert.Equal("CustomerService", coupledAudit.Couplings[0].SharedToken);
        Assert.Contains("High Semantic Propagation", coupledAudit.CouplingCategory);
        Assert.Contains("CustomerService", coupledAudit.SharedDominantIdentifiers);

        // 4. Formatter test
        string formatted = LineDiff.FormatSemanticHunkCouplingSummary(
            coupledAudit.TotalHunks, coupledAudit.CoupledHunkCount, coupledAudit.IndependentHunkCount,
            coupledAudit.TotalCouplingLinks, coupledAudit.MeanCouplingStrength, coupledAudit.HunkPropagationScore,
            coupledAudit.CouplingCategory, coupledAudit.SharedDominantIdentifiers);
        Assert.Equal(coupledAudit.Summary, formatted);
    }

    [Fact]
    public void Run172_AuditWordLevelDiffResolution_ValidatesLevenshteinDistanceAndTypoDetection()
    {
        var empty = LineDiff.AuditWordLevelDiffResolution(Array.Empty<LineDiff.UnifiedRow>());
        Assert.Equal(0, empty.TotalDiffRows);
        Assert.Equal(0, empty.TotalModifiedPairs);
        Assert.Equal(100.0, empty.WordLevelResolutionScore);
        Assert.Equal("Unchanged Document", empty.ResolutionCategory);

        var rOld = new LineDiff.UnifiedRow(LineDiff.Kind.Removed, 1, null, "The quik brown fox jumps over the lazy dog.", "- ");
        var rNew = new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 1, "The quick brown fox jumps over the lazy dog.", "+ ");
        var typoAudit = LineDiff.AuditWordLevelDiffResolution(new[] { rOld, rNew });

        Assert.Equal(2, typoAudit.TotalDiffRows);
        Assert.Equal(1, typoAudit.TotalModifiedPairs);
        Assert.Equal(1, typoAudit.MinorTypoCount);
        Assert.Equal(0, typoAudit.SubstantialRewriteCount);
        Assert.True(typoAudit.MeanLevenshteinSimilarity >= 0.90);
        Assert.Equal("High-Resolution Word Edits", typoAudit.ResolutionCategory);

        var pair = typoAudit.Pairs[0];
        Assert.Equal(1, pair.LevenshteinDistance);
        Assert.Equal("Minor Typo / Rename", pair.ModificationKind);

        var wsOld = new LineDiff.UnifiedRow(LineDiff.Kind.Removed, 2, null, "    return true;  ", "- ");
        var wsNew = new LineDiff.UnifiedRow(LineDiff.Kind.Added, null, 2, "return true;", "+ ");
        var wsAudit = LineDiff.AuditWordLevelDiffResolution(new[] { wsOld, wsNew });
        Assert.Equal(1, wsAudit.WhitespaceOnlyCount);
        Assert.Equal("Whitespace Only", wsAudit.Pairs[0].ModificationKind);

        string formatted = LineDiff.FormatWordLevelDiffResolutionSummary(
            typoAudit.TotalDiffRows, typoAudit.TotalModifiedPairs, typoAudit.PureAdditionCount,
            typoAudit.PureDeletionCount, typoAudit.WhitespaceOnlyCount, typoAudit.MinorTypoCount,
            typoAudit.ModerateEditCount, typoAudit.SubstantialRewriteCount, typoAudit.MeanLevenshteinSimilarity,
            typoAudit.WordLevelResolutionScore, typoAudit.ResolutionCategory);
        Assert.Equal(typoAudit.Summary, formatted);
    }
}






