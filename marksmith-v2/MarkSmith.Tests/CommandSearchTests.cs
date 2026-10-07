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
}
