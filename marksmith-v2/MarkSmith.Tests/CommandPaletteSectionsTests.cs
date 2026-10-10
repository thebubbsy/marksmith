using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

public class CommandPaletteSectionsTests
{
    private sealed record Cmd(string Label, string Category, string Keywords = "");

    private static readonly Cmd[] Commands =
    {
        new("Export PDF", "Export", "save print"),
        new("Export Word (.docx)", "Export"),
        new("Open a document", "File"),
        new("Find", "Edit", "search"),
        new("Find and replace", "Edit", "search"),
        new("Open Diagram Studio", "Studio"),
        new("Switch theme: Midnight", "Theme"),
        new("Open recent: notes.md", "Recent"),
    };

    private static List<PaletteSection<Cmd>> Build(string query, params string[] recent) =>
        CommandPaletteSections.Build(Commands, query, c => c.Label, c => c.Category, c => c.Keywords, recent);

    [Fact]
    public void Empty_query_lists_every_command_under_its_category_in_declared_order()
    {
        var sections = Build("");
        Assert.Equal(new[] { "Export", "File", "Edit", "Studios", "Themes", "Recent files" }, sections.Select(s => s.Header));
        Assert.Equal(Commands.Length, sections.Sum(s => s.Items.Count));
        Assert.Equal(new[] { "Find", "Find and replace" }, sections[2].Items.Select(c => c.Label));
    }

    [Fact]
    public void Recently_used_commands_come_first_and_are_not_repeated_below()
    {
        var sections = Build("", "Find", "Open Diagram Studio", "Gone command", "Find");
        Assert.Equal(CommandPaletteSections.RecentHeader, sections[0].Header);
        Assert.Equal(new[] { "Find", "Open Diagram Studio" }, sections[0].Items.Select(c => c.Label));
        Assert.DoesNotContain(sections.Skip(1), s => s.Header == "Studios");
        Assert.Equal(Commands.Length, sections.Sum(s => s.Items.Count));
    }

    [Fact]
    public void Recently_used_is_capped()
    {
        var sections = Build("", Commands.Select(c => c.Label).ToArray());
        Assert.Equal(CommandPaletteSections.RecentLimit, sections[0].Items.Count);
    }

    [Fact]
    public void A_query_is_one_ranked_run_with_recent_commands_winning_ties()
    {
        // "search" is a keyword of both Find rows (same score); the recently used one goes first.
        var plain = Build("search").Single();
        Assert.Equal("", plain.Header);
        Assert.Equal("Find", plain.Items[0].Label);
        var withRecent = Build("search", "Find and replace").Single();
        Assert.Equal("Find and replace", withRecent.Items[0].Label);
    }

    [Fact]
    public void A_better_match_still_beats_a_recent_one()
    {
        var run = Build("find", "Find and replace").Single();
        Assert.Equal("Find", run.Items[0].Label);
    }

    [Fact]
    public void No_match_is_no_sections()
    {
        Assert.Empty(Build("zzzz"));
    }

    [Fact]
    public void Recents_persist_newest_first_without_duplicates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-palette-" + Guid.NewGuid().ToString("N"));
        try
        {
            var recents = PaletteRecents.ForConfigDir(dir);
            Assert.Empty(recents.Labels);
            recents.Push("Export PDF");
            recents.Push("Find");
            recents.Push("Export PDF");
            Assert.Equal(new[] { "Export PDF", "Find" }, PaletteRecents.ForConfigDir(dir).Labels);

            for (var i = 0; i < PaletteRecents.Capacity + 3; i++) recents.Push($"Command {i}");
            Assert.Equal(PaletteRecents.Capacity, PaletteRecents.ForConfigDir(dir).Labels.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void An_unreadable_recents_file_is_an_empty_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-palette-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "palette-recent.json"), "{not json");
            Assert.Empty(PaletteRecents.ForConfigDir(dir).Labels);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
