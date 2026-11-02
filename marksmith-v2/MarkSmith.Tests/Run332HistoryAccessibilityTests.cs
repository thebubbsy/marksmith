using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Services;
using MarkSmith.ViewModels.History;
using Xunit;

namespace MarkSmith.Tests;

// Run #332 (Version History):
// - the header names the document's folder when another document shares its name, on screen
//   ("doc.md · beta") and to a screen reader ("doc.md in beta"), and its tooltip names the path;
// - a document row keeps its folder hint after a version is deleted or a checkpoint is taken;
// - each unified diff row is one accessible element ("Line 3 added: First paragraph.") instead
//   of four Text elements, and each side-by-side cell says which version it belongs to.
public class Run332HistoryAccessibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_run332_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private VersionHistoryService NewService() => new(Path.Combine(_dir, "history"));

    private static HistoryWindowViewModel NewVm(VersionHistoryService service, string file) =>
        new(_ => "<html/>", _ => Task.FromResult(true), service, initialFilePath: file);

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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoDir() }.Concat(parts).ToArray()));

    private static string HistoryXaml() => Read("MarkSmith.Desktop", "Views", "History", "HistoryWindow.xaml");

    // ---- the header's document name ----

    [Fact]
    public async Task Header_NamesTheFolder_WhenTheNameIsShared()
    {
        var service = NewService();
        await service.CaptureAsync(@"C:\work\alpha\doc.md", "a", "opened");
        await service.CaptureAsync(@"C:\work\beta\doc.md", "b", "opened");
        var vm = NewVm(service, @"C:\work\beta\doc.md");
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("doc.md", vm.FileName); // copied diff headers keep the bare name
        Assert.Equal("doc.md · beta", vm.HeaderFileName);
        Assert.Equal("doc.md in beta", vm.HeaderFileAutomationName);
    }

    [Fact]
    public async Task Header_IsJustTheName_WhenNoOtherDocumentSharesIt()
    {
        var service = NewService();
        await service.CaptureAsync(@"C:\work\alpha\doc.md", "a", "opened");
        await service.CaptureAsync(@"C:\work\beta\other.md", "b", "opened");
        var vm = NewVm(service, @"C:\work\alpha\doc.md");
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("doc.md", vm.HeaderFileName);
        Assert.Equal("doc.md", vm.HeaderFileAutomationName);
    }

    [Fact]
    public async Task Header_Updates_WhenSwitchingBetweenSameNamedDocuments()
    {
        var service = NewService();
        await service.CaptureAsync(@"C:\work\alpha\doc.md", "a", "opened");
        await service.CaptureAsync(@"C:\work\beta\doc.md", "b", "opened");
        var vm = NewVm(service, @"C:\work\alpha\doc.md");
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("doc.md · alpha", vm.HeaderFileName);

        // FileName stays "doc.md", so the header must hear about the switch from SelectedFile.
        var raised = 0;
        void OnChanged(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HistoryWindowViewModel.HeaderFileName)) raised++;
        }
        vm.PropertyChanged += OnChanged;
        await vm.SelectFileCommand.ExecuteAsync(vm.Files.Single(f => f.FolderHint == "beta"));
        vm.PropertyChanged -= OnChanged;

        Assert.True(raised > 0);
        Assert.Equal("doc.md · beta", vm.HeaderFileName);
    }

    [Fact]
    public async Task FolderHint_SurvivesDeletingAVersion()
    {
        var service = NewService();
        await service.CaptureAsync(@"C:\work\alpha\doc.md", "a", "opened");
        await service.CaptureAsync(@"C:\work\beta\doc.md", "b", "opened");
        await Task.Delay(15);
        await service.CaptureAsync(@"C:\work\beta\doc.md", "b2", "autosave");
        var vm = NewVm(service, @"C:\work\beta\doc.md");
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("beta", vm.SelectedFile!.FolderHint);

        await vm.DeleteVersionCommand.ExecuteAsync(vm.Bands.SelectMany(b => b.Items).Last());

        // The row is replaced with a fresh view model to update its count; it used to come back
        // without "beta ·", and the header lost its folder with it.
        var row = vm.Files.Single(f => f.Summary.FilePath.EndsWith(@"beta\doc.md", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("beta", row.FolderHint);
        Assert.StartsWith("beta · ", row.SubLabel);
        Assert.Equal("doc.md · beta", vm.HeaderFileName);
    }

    [Fact]
    public void HeaderXaml_BindsTheHintedName()
    {
        var xaml = HistoryXaml();
        Assert.Contains("x:Name=\"FileNameTextBlock\" Text=\"{Binding HeaderFileName}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding HeaderFileAutomationName}\"", xaml);
        Assert.DoesNotContain("AutomationProperties.Name=\"{Binding FileName}\"", xaml);
    }

    [Fact]
    public void HeaderTooltip_NamesThePath()
    {
        var cs = Read("MarkSmith.Desktop", "Views", "History", "HistoryWindow.xaml.cs");
        Assert.Contains("$\"Current document: {_vm.SelectedFile.Detail}\"", cs);
    }

    // ---- diff rows as one accessible element ----

    [Theory]
    [InlineData(LineDiff.Kind.Added, 3, "First paragraph.", null, "Line 3 added: First paragraph.")]
    [InlineData(LineDiff.Kind.Removed, 5, "Old text", null, "Line 5 removed: Old text")]
    [InlineData(LineDiff.Kind.Same, 4, "Kept", 4, "Line 4 unchanged: Kept")]
    [InlineData(LineDiff.Kind.Same, 6, "Moved down", 4, "Line 6 (was 4) unchanged: Moved down")]
    [InlineData(LineDiff.Kind.Added, 2, "", null, "Line 2 added, blank")]
    [InlineData(LineDiff.Kind.Added, 2, "   ", null, "Line 2 added, blank")]
    [InlineData(LineDiff.Kind.Added, null, "x", null, "Line added: x")]
    public void SpokenLine_ReadsAsOneSentence(LineDiff.Kind kind, int? number, string text, int? was, string expected)
    {
        Assert.Equal(expected, LineDiff.SpokenLine(kind, number, text, was));
    }

    [Fact]
    public void UnifiedRows_HaveOneNameEach()
    {
        var rows = LineDiff.BuildUnified(LineDiff.Diff("keep\nold\n", "keep\nnew\nmore\n"));
        var names = rows.Select(r => r.AutomationName).ToList();
        Assert.Contains("Line 1 unchanged: keep", names);
        Assert.Contains("Line 2 removed: old", names);
        Assert.Contains("Line 2 added: new", names);
        Assert.Contains("Line 3 added: more", names);
    }

    [Fact]
    public void GapRow_ReadsItsFoldText()
    {
        var gap = new LineDiff.UnifiedRow(LineDiff.Kind.Same, null, null, "12 unchanged lines", "", IsGap: true);
        Assert.Equal("12 unchanged lines", gap.AutomationName);
    }

    [Fact]
    public void SplitCells_SayWhichVersionTheyBelongTo()
    {
        var row = new DiffRowViewModel(new LineDiff.Row(
            new LineDiff.Cell(LineDiff.Kind.Removed, 3, "Old line"),
            new LineDiff.Cell(LineDiff.Kind.Added, 3, "New line")));
        Assert.Equal("Previous version, line 3 removed: Old line", row.Left!.AutomationName);
        Assert.Equal("Selected version, line 3 added: New line", row.Right!.AutomationName);
    }

    [Fact]
    public void DiffTemplates_HideNumbersAndMarkers_AndNameTheText()
    {
        var xaml = HistoryXaml();
        Assert.Contains("AutomationProperties.Name=\"{Binding AutomationName}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding Left.AutomationName}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding Right.AutomationName}\"", xaml);
        // Old number, new number and marker in the unified row, plus a number on each split side.
        var raw = xaml.Split("AutomationProperties.AccessibilityView=\"Raw\"").Length - 1;
        Assert.True(raw >= 5, $"expected at least 5 Raw diff elements, found {raw}");
    }
}
