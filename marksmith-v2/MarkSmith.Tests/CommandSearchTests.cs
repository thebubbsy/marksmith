using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

public class CommandSearchTests
{
    private sealed record Cmd(string Label, string Category, string Keywords = "");

    private static readonly Cmd[] Commands =
    {
        new("Export PDF", "Export", "save print"),
        new("Open a document", "File"),
        new("Find", "Edit", "search look for"),
        new("Find and replace", "Edit", "search substitute"),
        new("Open Diagram Studio", "Studio", "mermaid flowchart"),
        new("Switch theme: Midnight", "Theme"),
        new("Copy as email", "Export", "clipboard mail"),
        new("Make selection lowercase", "Edit"),
        new("Insert table", "Insert"),
    };

    private static List<string> Rank(string q) =>
        CommandSearch.Rank(Commands, q, c => c.Label, c => c.Category, c => c.Keywords).Select(c => c.Label).ToList();

    [Fact]
    public void An_Exact_Label_Beats_Everything_Else() => Assert.Equal("Find", Rank("find")[0]);

    [Fact]
    public void Words_In_Any_Order_Match() => Assert.Equal("Export PDF", Rank("pdf export")[0]);

    [Fact]
    public void Keywords_Find_Commands_By_Other_Names()
    {
        Assert.Equal("Open Diagram Studio", Rank("mermaid")[0]);
        Assert.Equal(new[] { "Find", "Find and replace" }, Rank("search").Take(2));
    }

    [Fact]
    public void A_Word_Prefix_Beats_A_Loose_Subsequence()
    {
        var ranked = Rank("dia");
        Assert.Equal("Open Diagram Studio", ranked[0]);
    }

    [Fact]
    public void An_Empty_Query_Keeps_The_Original_Order() =>
        Assert.Equal(Commands.Select(c => c.Label), Rank("  "));

    [Fact]
    public void Nothing_Matches_Nonsense() => Assert.Empty(Rank("zzqx"));

    [Fact]
    public void Letters_Scattered_Across_Words_Are_Not_A_Match()
    {
        // c(opy) + as + e(mail) used to count; "case" means lowercase/letter case.
        Assert.Equal(new[] { "Make selection lowercase" }, Rank("case"));
    }

    [Theory]
    [InlineData("ep", "Export PDF")]
    [InlineData("exppdf", "Export PDF")]
    [InlineData("instab", "Insert table")]
    [InlineData("far", "Find and replace")]
    public void Word_Start_Abbreviations_Still_Match(string query, string expected) =>
        Assert.Equal(expected, Rank(query)[0]);

    [Fact]
    public void Abbreviations_Rank_Below_Real_Words() =>
        Assert.Equal("Open a document", Rank("open")[0]);

    [Theory]
    [InlineData("Export PDF", "pdf", 7, 3)]
    [InlineData("Find and replace", "find", 0, 4)]
    [InlineData("Insert table", "tab", 7, 3)]
    public void Highlights_Point_At_The_Matched_Text(string label, string query, int start, int length) =>
        Assert.Equal(new[] { (start, length) }, CommandSearch.Highlights(label, query));

    [Fact]
    public void Highlights_Cover_Each_Abbreviation_Piece()
    {
        Assert.Equal(new[] { (0, 3), (7, 3) }, CommandSearch.Highlights("Export PDF", "exppdf"));
        Assert.Equal(new[] { (0, 3), (7, 3) }, CommandSearch.Highlights("Export PDF", "pdf exp"));
    }

    [Fact]
    public void Keyword_Matches_Have_Nothing_To_Highlight() =>
        Assert.Empty(CommandSearch.Highlights("Export PDF", "acrobat"));
}
