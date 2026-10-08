using System.Linq;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Preview;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Insert ▸ SmartArt: the layouts it offers must be real Word layouts that draw as what their name
/// says, and indentation must survive into the block (it used to be trimmed away, so an org chart
/// came out as one flat row).
/// </summary>
public class SmartArtInsertTests
{
    [Theory]
    [InlineData("default", SmartArtPreviewFamily.BlockList)]
    [InlineData("vList2", SmartArtPreviewFamily.VerticalList)]
    [InlineData("process1", SmartArtPreviewFamily.Process)]
    [InlineData("chevron1", SmartArtPreviewFamily.Chevron)]
    [InlineData("process2", SmartArtPreviewFamily.VerticalProcess)]
    [InlineData("hProcess11", SmartArtPreviewFamily.Timeline)]
    [InlineData("cycle2", SmartArtPreviewFamily.Cycle)]
    [InlineData("radial1", SmartArtPreviewFamily.Radial)]
    [InlineData("orgChart1", SmartArtPreviewFamily.Hierarchy)]
    [InlineData("venn1", SmartArtPreviewFamily.Venn)]
    [InlineData("matrix3", SmartArtPreviewFamily.Matrix)]
    [InlineData("pyramid1", SmartArtPreviewFamily.Pyramid)]
    public void Every_layout_draws_as_its_family(string alias, SmartArtPreviewFamily family)
    {
        var layout = SmartArtInsert.Find(alias);
        Assert.NotNull(layout);
        Assert.Equal(family, layout!.Family);
    }

    [Fact]
    public void Every_layout_is_in_the_catalog_and_named_as_Word_names_it()
    {
        foreach (var layout in SmartArtInsert.Layouts)
        {
            Assert.NotNull(SmartArtLayoutCatalog.Shared.TryResolve(layout.Alias));
            Assert.Equal(ViewModels.SmartArtStudio.StudioLayoutItem.WordNames[layout.Alias], layout.Name);
            Assert.StartsWith("<svg", layout.ThumbnailSvg);
        }
        Assert.Equal(SmartArtInsert.Layouts.Count, SmartArtInsert.Layouts.Select(l => l.Family).Distinct().Count());
        Assert.NotNull(SmartArtInsert.Find(SmartArtInsert.DefaultAlias));
    }

    [Fact]
    public void Every_example_uses_an_offered_layout_and_gets_no_advice()
    {
        foreach (var example in SmartArtInsert.Examples)
        {
            var layout = SmartArtInsert.Find(example.Alias);
            Assert.NotNull(layout);
            var rows = SmartArtInsert.Parse(example.Text);
            Assert.NotEmpty(rows);
            Assert.Null(SmartArtInsert.Advice(layout, rows));
        }
    }

    [Fact]
    public void Indentation_becomes_nested_bullets()
    {
        var md = SmartArtInsert.Build("orgChart1", "CEO\n  CTO\n    Dev\n  CFO");
        Assert.Equal("\n:::smartart type=\"orgChart1\"\n- CEO\n  - CTO\n    - Dev\n  - CFO\n:::\n", md);
    }

    [Fact]
    public void Bare_carriage_returns_from_a_WinUI_TextBox_are_line_breaks()
    {
        var rows = SmartArtInsert.Parse("Plan\r  Goals\rDo");
        Assert.Equal(new[] { new SmartArtInsert.Row(0, "Plan"), new(1, "Goals"), new(0, "Do") }, rows);
    }

    [Fact]
    public void Levels_never_jump_and_the_first_line_is_top_level()
    {
        var rows = SmartArtInsert.Parse("    A\n          B\n C\n\tD");
        // A opens level 0 at width 4; B is deeper (level 1, not 3); C dedents past everything
        // (a new top); D at width 4 is deeper than C.
        Assert.Equal(new[] { 0, 1, 0, 1 }, rows.Select(r => r.Level));
    }

    [Fact]
    public void Dedenting_to_a_known_level_returns_to_it()
    {
        var rows = SmartArtInsert.Parse("A\n  B\n    C\n  D\nE");
        Assert.Equal(new[] { 0, 1, 2, 1, 0 }, rows.Select(r => r.Level));
    }

    [Theory]
    [InlineData("- Plan", "Plan")]
    [InlineData("* Plan", "Plan")]
    [InlineData("+ Plan", "Plan")]
    [InlineData("1. Plan", "Plan")]
    [InlineData("12) Plan", "Plan")]
    [InlineData("-Plan", "-Plan")]
    [InlineData("2026: Launch", "2026: Launch")]
    public void A_pasted_list_marker_is_not_doubled(string line, string text)
    {
        Assert.Equal(text, Assert.Single(SmartArtInsert.Parse(line)).Text);
    }

    [Fact]
    public void Blank_lines_are_skipped_and_empty_gives_the_placeholder()
    {
        Assert.Empty(SmartArtInsert.Parse("\n   \n\t\n"));
        Assert.Equal("\n:::smartart type=\"process1\"\n- Step 1\n:::\n", SmartArtInsert.Build(null, ""));
    }

    [Fact]
    public void The_count_is_worded_for_how_the_layout_uses_sub_items()
    {
        var rows = SmartArtInsert.Parse("A\n  a1\n  a2\nB");
        Assert.Equal("2 shapes · 2 sub-points", SmartArtInsert.Describe(SmartArtInsert.Find("process1"), rows));
        Assert.Equal("4 boxes in 2 levels", SmartArtInsert.Describe(SmartArtInsert.Find("orgChart1"), rows));
        Assert.Equal("1 shape", SmartArtInsert.Describe(SmartArtInsert.Find("process1"), SmartArtInsert.Parse("A")));
        Assert.Equal("No items", SmartArtInsert.Describe(SmartArtInsert.Find("process1"), SmartArtInsert.Parse("")));
    }

    [Fact]
    public void A_flat_org_chart_is_told_how_to_indent()
    {
        var org = SmartArtInsert.Find("orgChart1");
        Assert.Contains("Indent", SmartArtInsert.Advice(org, SmartArtInsert.Parse("CEO\nCTO\nCFO")));
        Assert.Contains("separate tops", SmartArtInsert.Advice(org, SmartArtInsert.Parse("CEO\n  CTO\nCFO")));
        Assert.Null(SmartArtInsert.Advice(org, SmartArtInsert.Parse("CEO")));
    }

    [Fact]
    public void A_matrix_wants_four_and_a_venn_wants_few()
    {
        Assert.Contains("four quadrants", SmartArtInsert.Advice(SmartArtInsert.Find("matrix3"), SmartArtInsert.Parse("A\nB\nC")));
        Assert.Contains("two to four", SmartArtInsert.Advice(SmartArtInsert.Find("venn1"), SmartArtInsert.Parse("A\nB\nC\nD\nE")));
        Assert.Contains("a lot", SmartArtInsert.Advice(SmartArtInsert.Find("process1"), SmartArtInsert.Parse("1\n2\n3\n4\n5\n6\n7\n8\n9")));
    }

    [Fact]
    public void Indent_shifts_the_caret_line_and_the_caret()
    {
        // Caret in "CTO" (bare-CR line breaks, as WinUI stores them).
        var e = SmartArtInsert.ShiftLines("CEO\rCTO\rCFO", 5, 5, +1);
        Assert.Equal("CEO\r  CTO\rCFO", e.Text);
        Assert.Equal(7, e.SelectionStart);
        Assert.Equal(0, e.SelectionLength);
    }

    [Fact]
    public void Indent_covers_every_selected_line_but_not_one_the_selection_only_reaches()
    {
        // Select "CEO\nCTO\n": the selection ends right after the break, so CFO isn't touched.
        var e = SmartArtInsert.ShiftLines("CEO\nCTO\nCFO", 0, 8, +1);
        Assert.Equal("  CEO\n  CTO\nCFO", e.Text);
        Assert.Equal(2, e.SelectionStart);
        Assert.Equal(10, e.SelectionLength); // CTO's new indent is inside the selection
    }

    [Fact]
    public void Outdent_removes_up_to_two_spaces_or_a_tab_and_never_goes_negative()
    {
        Assert.Equal("CEO\r  CTO", SmartArtInsert.ShiftLines("CEO\r    CTO", 9, 9, -1).Text);
        Assert.Equal("CTO", SmartArtInsert.ShiftLines("\tCTO", 2, 2, -1).Text);
        Assert.Equal("CTO", SmartArtInsert.ShiftLines(" CTO", 2, 2, -1).Text);
        var none = SmartArtInsert.ShiftLines("CTO", 1, 1, -1);
        Assert.Equal(("CTO", 1), (none.Text, none.SelectionStart));
        // Caret inside the removed indent snaps to the line start.
        Assert.Equal(4, SmartArtInsert.ShiftLines("CEO\r  CTO", 5, 5, -1).SelectionStart);
    }

    [Fact]
    public void Shifting_keeps_CRLF_breaks_intact()
    {
        Assert.Equal("  A\r\n  B", SmartArtInsert.ShiftLines("A\r\nB", 0, 4, +1).Text);
        Assert.Equal("  ", SmartArtInsert.ShiftLines("", 0, 0, +1).Text);
    }

    [Fact]
    public void The_block_renders_in_the_preview_with_its_hierarchy()
    {
        var md = SmartArtInsert.Build("orgChart1", "CEO\n  CTO\n  CFO");
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));
        Assert.Contains("data-family=\"Hierarchy\"", html);
        Assert.Contains("CTO", html);
    }
}
