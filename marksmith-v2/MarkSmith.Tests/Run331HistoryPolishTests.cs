using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Services;
using MarkSmith.ViewModels.History;
using Xunit;

namespace MarkSmith.Tests;

// Run #331 (Version History):
// - a newline that ends both texts is no longer an empty last diff line, and a first version's
//   card counts its lines the way the diff pane lists them ("+7", not "+8");
// - the word pill leaves out a zero side ("+11 words", not "+11 words / -0 words");
// - the search pill says "No matches" rather than "0 matches";
// - with the search or starred filter hiding every version, the diff pane says so instead of
//   asking for a selection the empty timeline can't give;
// - documents that share a file name name their folder on the date line, and every document
//   row reads its name, folder, version count and last change to a screen reader;
// - the search box no longer jumps left when the match pill appears.
public class Run331HistoryPolishTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_run331_" + Guid.NewGuid().ToString("N"));

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

    // ---- the final newline ----

    [Theory]
    [InlineData("a\nb\n", 2)]
    [InlineData("a\r\nb\r\n", 2)]
    [InlineData("a\rb\r", 2)]
    [InlineData("a\nb", 2)]
    [InlineData("a\n\n", 2)]
    [InlineData("\n", 1)]
    [InlineData("", 1)]
    public void AllAdded_DoesNotCountTheFinalNewlineAsALine(string text, int lines)
    {
        Assert.Equal(lines, LineDiff.AllAdded(text).Count);
    }

    [Fact]
    public void Diff_OfTwoTextsEndingInANewline_HasNoEmptyLastLine()
    {
        var lines = LineDiff.Diff("one\ntwo\n", "one\ntwo\nthree\n");
        Assert.Equal(new[] { "one", "two", "three" }, lines.Select(l => l.Text));
        Assert.Single(lines, l => l.Kind == LineDiff.Kind.Added);
        Assert.Empty(lines.Where(l => l.Kind == LineDiff.Kind.Removed));
    }

    [Fact]
    public void Diff_KeepsTheEmptyLine_WhenOnlyOneSideEndsWithANewline()
    {
        // The texts differ only by the final newline; that difference must still show.
        var lines = LineDiff.Diff("one\n", "one");
        Assert.Contains(lines, l => l.Kind != LineDiff.Kind.Same);
    }

    [Fact]
    public async Task FirstVersion_CardCount_MatchesTheDiffPane()
    {
        var service = NewService();
        const string file = @"C:\docs\first.md";
        await service.CaptureAsync(file, "# Title\n\nBody.\n", "opened");
        var versions = await service.GetVersionsAsync(file);
        Assert.Equal(3, versions[0].LinesAdded);
    }

    // ---- the word pill ----

    [Theory]
    [InlineData(11, 0, "+11 words")]
    [InlineData(1, 0, "+1 word")]
    [InlineData(0, 3, "-3 words")]
    [InlineData(2, 1, "+2 words / -1 word")]
    [InlineData(0, 0, "no word changes")]
    [InlineData(1204, 0, "+1,204 words")]
    public void Change_LeavesOutAZeroSide(int added, int removed, string expected)
    {
        Assert.Equal(expected, CountText.Change(added, removed, "word"));
    }

    [Fact]
    public void WordPill_UsesChange()
    {
        var src = Read("MarkSmith.Desktop", "Views", "History", "HistoryWindow.xaml.cs");
        Assert.Contains("DiffWordDeltaText.Text = CountText.Change(addedWords, removedWords, \"word\");", src);
    }

    // ---- search wording and the filtered diff pane ----

    [Fact]
    public async Task SearchPill_SaysNoMatches_AndCountsWithTheRightNoun()
    {
        var service = NewService();
        const string file = @"C:\docs\sm.md";
        await service.CaptureAsync(file, "one", "opened");
        await service.CaptureAsync(file, "two", "autosave");
        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchQuery = "zzz";
        Assert.Equal("No matches", vm.SearchMatchText);
        vm.SearchQuery = "Opened";
        Assert.Equal("1 match", vm.SearchMatchText);
        vm.SearchQuery = "";
        Assert.Equal("", vm.SearchMatchText);
    }

    [Fact]
    public async Task DiffPlaceholder_SaysTheSearchHidesEveryVersion()
    {
        var service = NewService();
        const string file = @"C:\docs\ph2.md";
        await service.CaptureAsync(file, "one", "opened");
        await service.CaptureAsync(file, "two", "autosave");
        await service.CaptureAsync(file, "three", "autosave");
        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchQuery = "zzz";
        Assert.Equal("The search hides all 3 versions of this document. Clear filters on the timeline to pick one.",
            vm.DiffPlaceholderText);

        vm.SearchQuery = "";
        Assert.Equal("Select a version on the timeline to see what changed in it.", vm.DiffPlaceholderText);
    }

    [Fact]
    public async Task DiffPlaceholder_NamesTheStarredFilter_AndAnOnlyVersion()
    {
        var service = NewService();
        const string file = @"C:\docs\ph3.md";
        await service.CaptureAsync(file, "one", "opened");
        var vm = NewVm(service, file);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.IsStarredOnlyFilter = true;
        Assert.Equal("The starred filter hides this document's only version. Clear filters on the timeline to pick one.",
            vm.DiffPlaceholderText);

        vm.SearchQuery = "zzz";
        Assert.StartsWith("The search and the starred filter hide", vm.DiffPlaceholderText);
    }

    // ---- documents that share a name ----

    [Fact]
    public async Task SharedFileNames_NameTheirFolder_OthersDoNot()
    {
        var service = NewService();
        await service.CaptureAsync(@"C:\work\alpha\doc.md", "a", "opened");
        await service.CaptureAsync(@"C:\work\beta\doc.md", "b", "opened");
        await service.CaptureAsync(@"C:\work\beta\other.md", "c", "opened");
        var vm = NewVm(service, @"C:\work\alpha\doc.md");
        await vm.LoadCommand.ExecuteAsync(null);

        var docs = vm.Files.Where(f => f.FileName == "doc.md").ToList();
        Assert.Equal(2, docs.Count);
        Assert.Equal(new[] { "alpha", "beta" }, docs.Select(f => f.FolderHint).OrderBy(x => x));
        Assert.All(docs, f => Assert.StartsWith(f.FolderHint + " · ", f.SubLabel));
        // This year's date drops its year beside a folder, so the time still fits the row.
        Assert.All(docs, f => Assert.DoesNotContain(DateTime.Now.Year.ToString(), f.SubLabel));

        var other = vm.Files.Single(f => f.FileName == "other.md");
        Assert.Equal("", other.FolderHint);
        Assert.Equal(other.LastModifiedLabel, other.SubLabel);
    }

    [Fact]
    public async Task DocumentRow_AutomationName_SaysWhichDocumentItIs()
    {
        var service = NewService();
        await service.CaptureAsync(@"C:\work\alpha\doc.md", "a", "opened");
        await service.CaptureAsync(@"C:\work\beta\doc.md", "b", "opened");
        await service.CaptureAsync(@"C:\work\beta\doc.md", "b2", "autosave");
        var vm = NewVm(service, @"C:\work\alpha\doc.md");
        await vm.LoadCommand.ExecuteAsync(null);

        var beta = vm.Files.Single(f => f.FolderHint == "beta");
        Assert.StartsWith("doc.md in beta, 2 versions, last changed ", beta.AutomationName);
        Assert.DoesNotContain(" · ", beta.AutomationName);
        var alpha = vm.Files.Single(f => f.FolderHint == "alpha");
        Assert.Contains(", 1 version, ", alpha.AutomationName);
    }

    [Fact]
    public void FolderOf_UsesTheOnDiskSpelling()
    {
        var real = Directory.CreateDirectory(Path.Combine(_dir, "MixedCase Folder"));
        var lowered = Path.Combine(real.FullName.ToLowerInvariant(), "doc.md");
        Assert.Equal("MixedCase Folder", FileSummaryViewModel.FolderOf(lowered));
        Assert.Equal("gone", FileSummaryViewModel.FolderOf(@"C:\no\such\gone\doc.md"));
    }

    [Fact]
    public void ShortDate_DropsOnlyThisYearsYear()
    {
        var now = new DateTime(2026, 11, 3, 9, 0, 0);
        Assert.Equal("2 Nov", FileSummaryViewModel.ShortDate(new DateTime(2026, 11, 2, 1, 13, 0), now));
        Assert.Equal("30 Dec 2025", FileSummaryViewModel.ShortDate(new DateTime(2025, 12, 30, 8, 0, 0), now));
    }

    [Fact]
    public void DocumentRowTemplate_BindsTheNewLabels()
    {
        var xaml = HistoryXaml();
        Assert.Contains("AutomationProperties.Name=\"{Binding AutomationName}\"", xaml);
        Assert.Contains("Text=\"{Binding SubLabel}\"", xaml);
        Assert.DoesNotContain("Text=\"{Binding LastModifiedLabel}\"", xaml);
    }

    // ---- search box layout ----

    [Fact]
    public void SearchBox_SitsInACappedColumn_WithAFillerAfterThePill()
    {
        var xaml = HistoryXaml();
        int start = xaml.IndexOf("x:Name=\"TimelineSearchCluster\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("<TextBox", start, StringComparison.Ordinal);
        var defs = xaml[start..end];
        // Nearly all the star weight, so the box keeps its width when the pill takes space.
        Assert.Contains("<ColumnDefinition x:Name=\"SearchBoxColumn\" Width=\"100*\" MinWidth=\"160\" MaxWidth=\"280\" />", defs);
        Assert.Equal(3, defs.Split("<ColumnDefinition ").Length - 1);
    }
}
