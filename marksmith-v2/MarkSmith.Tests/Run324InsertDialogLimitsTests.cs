using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #324, the Insert dialogs (tab group, columns, table, canvas, tile map, references, chart):
// - the number boxes refresh the "Inserts" card while digits are typed, not only on Tab/Enter;
// - the number boxes' help text gives their range and the keys that change them;
// - the tab group dialog refuses more tabs than the preview draws (16) and titles starting ":::";
// - the references dialog refuses a citation id that [@id] can't cite;
// - the chart's point count goes through CountText.
public class Run324InsertDialogLimitsTests
{
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

    private const string Dialogs = "MarkSmith.Desktop/Views/InsertDialogControls.cs";
    private const string MainCode = "MarkSmith.Desktop/MainWindow.xaml.cs";

    [Fact]
    public void Excess_names_the_limit_and_how_many_to_remove()
    {
        Assert.Equal("At most 16 tabs fit; remove 2 tabs.", InsertCountText.Excess(16, 18, "tab"));
        Assert.Equal("At most 16 tabs fit; remove 1 tab.", InsertCountText.Excess(16, 17, "tab"));
    }

    [Fact]
    public void NumberHelp_gives_the_range_and_the_keys()
    {
        Assert.Equal("From 2 to 4. Up and Down change it by 1, Page Up and Page Down by 5. Ctrl+Enter inserts.",
            InsertCountText.NumberHelp(2, 4));
        Assert.StartsWith("From 10 to 4000.", InsertCountText.NumberHelp(10, 4000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("smith2024")]
    [InlineData("knuth-1984_a")]
    [InlineData("müller2020")]
    public void CitationIdProblem_accepts_ids_a_citation_can_name(string id) =>
        Assert.Null(InsertCountText.CitationIdProblem(id));

    [Theory]
    [InlineData("smith 2024", "a space")]
    [InlineData("smith.2024", "\u201C.\u201D")]
    [InlineData("a:b", "\u201C:\u201D")]
    public void CitationIdProblem_names_the_character_that_breaks_it(string id, string what)
    {
        var problem = InsertCountText.CitationIdProblem(id);
        Assert.NotNull(problem);
        Assert.Contains("letters, digits, - and _", problem);
        Assert.Contains($"has {what}", problem);
    }

    [Fact]
    public void Preview_and_dialog_share_the_tab_limit()
    {
        Assert.Equal(16, ContainerBlockParsers.MaxTabs);
        var html = Read("MarkSmith.Core/Services/MarkdownHtmlService.Containers.cs");
        Assert.Contains("n <= ContainerBlockParsers.MaxTabs", html);
        Assert.Contains("Math.Min(tabs.Count, ContainerBlockParsers.MaxTabs)", html);
        Assert.DoesNotContain("Math.Min(tabs.Count, 16)", html);
    }

    [Fact]
    public void Tab_group_dialog_refuses_too_many_tabs_and_colon_titles()
    {
        var main = Read(MainCode);
        var at = main.IndexOf("private async void OnInsertTabsClick", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var body = main.Substring(at, main.IndexOf("ShowInsertDialogAsync(\"Insert tab group\"", at, StringComparison.Ordinal) - at);
        Assert.Contains("maximum: Services.ContainerBlockParsers.MaxTabs", body);
        Assert.Contains("lineIsValid: l => !l.StartsWith(\":::\", StringComparison.Ordinal)", body);
        Assert.Contains("not starting with :::", body);

        var dialogs = Read(Dialogs);
        Assert.Contains("if (lines.Count > _maximum)\n                return Services.InsertCountText.Excess(_maximum, lines.Count, _noun);", dialogs);
    }

    [Fact]
    public void Number_boxes_refresh_while_typing_and_carry_key_help()
    {
        var dialogs = Read(Dialogs);
        Assert.Contains("AutomationProperties.SetHelpText(box, InsertCountText.NumberHelp(min, max, box.SmallChange, box.LargeChange));", dialogs);
        Assert.Contains("inner.TextChanged += (_, _) => Refresh();", dialogs);
        Assert.Contains("AutomationProperties.SetHelpText(inner, AutomationProperties.GetHelpText(box));", dialogs);
        // IntOf reads the typed text first, so the card and Insert agree with what the box shows.
        Assert.Contains("if (InnerTextBox(box)?.Text is { Length: > 0 } typed", dialogs);
        // Every number box goes through RefreshOnEdit; none listens to ValueChanged alone.
        Assert.Contains("RefreshOnEdit(_rows);", dialogs);
        Assert.Contains("RefreshOnEdit(_cols);", dialogs);
        Assert.Contains("RefreshOnEdit(box);\n            _boxes.Add((box, value));", dialogs);
        Assert.Equal(1, Count(dialogs, "ValueChanged += (_, _) => Refresh();"));
    }

    [Fact]
    public void References_dialog_checks_the_id_and_chart_counts_through_CountText()
    {
        var dialogs = Read(Dialogs);
        Assert.Contains("protected override string? Problem => InsertCountText.CitationIdProblem(Id);", dialogs);
        Assert.Contains("([@id]): letters, digits, - and _", dialogs);
        Assert.Contains("_count.Text = CountText.Of(points, \"point\");", dialogs);
        Assert.DoesNotContain("point{(points == 1", dialogs);
    }

    private static int Count(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
