using System;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class DraftSummaryTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 23, 30, 0);

    [Fact]
    public void Title_is_the_first_heading_without_its_markers()
        => Assert.Equal("Quarterly report", DraftSummary.Title("\n\n# Quarterly report\n\nRevenue grew."));

    [Fact]
    public void Title_skips_code_fences_and_strips_bold()
        => Assert.Equal("Meeting notes", DraftSummary.Title("```\n---\n- **Meeting notes**\n"));

    [Fact]
    public void Long_titles_are_cut_at_a_word()
    {
        var title = DraftSummary.Title(string.Join(' ', System.Linq.Enumerable.Repeat("alphabet", 20)));
        Assert.EndsWith("…", title);
        Assert.True(title.Length <= 71);
        Assert.DoesNotContain("alphab…", title);
    }

    [Fact]
    public void Blank_draft_has_a_placeholder_title()
        => Assert.Equal("Untitled draft", DraftSummary.Title("#\n\n>\n"));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(12, "12 min ago")]
    public void Recent_saves_read_as_an_age(int minutes, string expected)
        => Assert.Equal(expected, DraftSummary.When(Now.AddMinutes(-minutes), Now));

    [Fact]
    public void Older_saves_name_the_day()
    {
        Assert.StartsWith("today at ", DraftSummary.When(Now.AddHours(-3), Now));
        Assert.StartsWith("yesterday at ", DraftSummary.When(Now.AddDays(-1), Now));
        Assert.Contains(" at ", DraftSummary.When(Now.AddDays(-9), Now));
    }

    [Fact]
    public void Detail_counts_words()
    {
        var (_, detail) = DraftSummary.Describe("# Hello\n\nOne two three.", Now.AddMinutes(-5), Now);
        Assert.Equal("Last saved 5 min ago · 4 words", detail);
    }
}
