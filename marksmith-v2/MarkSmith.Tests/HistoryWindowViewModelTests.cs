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
        var vm = new HistoryWindowViewModel(file, _ => "<html/>", _ => Task.FromResult(true), service);
        await vm.LoadCommand.ExecuteAsync(null);
        return (vm, service);
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
}
