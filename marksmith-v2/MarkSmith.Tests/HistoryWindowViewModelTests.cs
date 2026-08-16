using MarkSmith.Services;
using MarkSmith.ViewModels.History;
using Xunit;

namespace MarkSmith.Tests;

public class HistoryWindowViewModelTests : IDisposable
{
    private readonly string _dir;

    public HistoryWindowViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_histvm_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task WaitForAsync(Func<bool> condition, int attempts = 50)
    {
        for (int i = 0; i < attempts && !condition(); i++) await Task.Delay(50);
    }

    private async Task<(HistoryWindowViewModel Vm, VersionHistoryService Service)> BuildAsync(
        string file, string v1, string v2)
    {
        var service = new VersionHistoryService(Path.Combine(_dir, "history"));
        await service.CaptureAsync(file, v1, "opened");
        await Task.Delay(20);
        await service.CaptureAsync(file, v2, "export:pdf");
        var vm = new HistoryWindowViewModel(_ => "<html/>", _ => Task.FromResult(true), service, initialFilePath: file);
        await vm.LoadCommand.ExecuteAsync(null);
        return (vm, service);
    }

    [Fact]
    public async Task Hub_ListsEveryTouchedFile_NewestFirst_AndPreselectsTheOpenFile()
    {
        var service = new VersionHistoryService(Path.Combine(_dir, "history"));
        await service.CaptureAsync(@"C:\docs\older.md", "old");
        await Task.Delay(20);
        await service.CaptureAsync(@"C:\\docs\\newer.md", "new");
        var vm = new HistoryWindowViewModel(_ => "<html/>", _ => Task.FromResult(true), service,
            initialFilePath: @"C:\docs\older.md");
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Files.Count);
        Assert.Equal("newer.md", vm.Files[0].FileName);            // newest first in the hub
        Assert.Equal("1 version", vm.Files[1].VersionCountLabel);
        Assert.Equal("older.md", vm.SelectedFile!.FileName);       // the open file is pre-selected
        Assert.True(vm.HasVersions);                               // its timeline loaded
    }

    [Fact]
    public async Task Loading_SelectsNewest_AndPopulatesTheTimeline()
    {
        var (vm, _) = await BuildAsync(@"C:\docs\a.md", "line a\nline b", "line a\nline b changed\nline c");
        Assert.True(vm.HasVersions);
        Assert.Single(vm.Bands);
        Assert.NotEmpty(vm.Bands[0].Items);
        Assert.Equal("export:pdf", vm.Selected!.Entry.Source);
    }

    [Fact]
    public async Task SelectingAVersion_FillsTheDiffRows_WithStats()
    {
        var (vm, _) = await BuildAsync(@"C:\docs\a.md", "keep\nold line\nkeep", "keep\nnew line\nkeep");
        await WaitForAsync(() => vm.DiffRows.Count > 0);
        Assert.True(vm.DiffRows.Count >= 3, "expected at least the keep/change/keep rows");
        Assert.Contains("added", vm.DiffStats);
        Assert.Contains("removed", vm.DiffStats);
        Assert.StartsWith("Changes in this version", vm.DiffTitle);
    }

    [Fact]
    public async Task DiffVsPrevious_ShowsReplacementSideBySide()
    {
        var (vm, _) = await BuildAsync(@"C:\docs\a.md", "keep\nold line\nkeep", "keep\nnew line\nkeep");
        await WaitForAsync(() => vm.DiffRows.Count > 0);
        // The changed middle row pairs a removed LEFT cell with an added RIGHT cell.
        var changed = vm.DiffRows.FirstOrDefault(r => r.Left is { IsRemoved: true } && r.Right is { IsAdded: true });
        Assert.NotNull(changed);
        Assert.Equal("old line", changed!.Left!.Text);
        Assert.Equal("new line", changed.Right!.Text);
    }

    [Fact]
    public async Task OldestVersion_DiffsAgainstEmpty_AllAdded()
    {
        var (vm, _) = await BuildAsync(@"C:\docs\a.md", "first line\nsecond", "first line\nsecond\nthird");
        await WaitForAsync(() => vm.DiffRows.Count > 0);
        // Select the OLDEST version (last item of the first band).
        var oldest = vm.Bands[0].Items.Last();
        vm.SelectVersionCommand.Execute(oldest);
        // Wait for THAT version's diff (the header flips to 'first version' when it completes) —
        // the initial auto-selected newest version's stats already contain 'added'.
        await WaitForAsync(() => vm.DiffHeader.Contains("first version"));
        Assert.Contains("added", vm.DiffStats);
    }

    [Fact]
    public async Task UnifiedDiff_IsGeneratedAlongsideSplitDiff()
    {
        var (vm, _) = await BuildAsync(@"C:\docs\a.md", "line 1\nline 2", "line 1\nline 2 modified\nline 3");
        await WaitForAsync(() => vm.UnifiedDiffRows.Count > 0);
        Assert.NotEmpty(vm.UnifiedDiffRows);
        Assert.Contains(vm.UnifiedDiffRows, r => r.IsAdded && r.Text.Contains("line 3"));
    }

    [Fact]
    public async Task DiffMode_SwitchesBetweenUnifiedSplitAndPreview()
    {
        var (vm, _) = await BuildAsync(@"C:\docs\a.md", "a", "b");
        vm.SetDiffMode(HistoryDiffMode.Unified);
        Assert.True(vm.ShowUnifiedDiff);
        Assert.False(vm.ShowSplitDiff);
        Assert.False(vm.ShowPreview);

        vm.SetDiffMode(HistoryDiffMode.Split);
        Assert.False(vm.ShowUnifiedDiff);
        Assert.True(vm.ShowSplitDiff);
        Assert.False(vm.ShowPreview);

        vm.SetDiffMode(HistoryDiffMode.Preview);
        Assert.False(vm.ShowUnifiedDiff);
        Assert.False(vm.ShowSplitDiff);
        Assert.True(vm.ShowPreview);
    }

    [Fact]
    public async Task StarFilter_And_SearchFilter_NarrowTimeline()
    {
        var service = new VersionHistoryService(Path.Combine(_dir, "history"));
        await service.CaptureAsync(@"C:\docs\search.md", "content v1", "manual", "Special Milestone", isStarred: true);
        await Task.Delay(20);
        await service.CaptureAsync(@"C:\docs\search.md", "content v2", "autosave", "Quick edit", isStarred: false);

        var vm = new HistoryWindowViewModel(_ => "<html/>", _ => Task.FromResult(true), service, initialFilePath: @"C:\docs\search.md");
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Bands[0].Items.Count);

        // Filter by Starred
        vm.IsStarredOnlyFilter = true;
        Assert.Single(vm.Bands[0].Items);
        Assert.Equal("Special Milestone", vm.Bands[0].Items[0].Label);

        // Filter by Search Query
        vm.IsStarredOnlyFilter = false;
        vm.SearchQuery = "Quick";
        Assert.Single(vm.Bands[0].Items);
        Assert.Equal("Quick edit", vm.Bands[0].Items[0].Label);
    }
}
