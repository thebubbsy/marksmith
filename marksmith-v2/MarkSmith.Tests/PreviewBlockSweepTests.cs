using System.Text.RegularExpressions;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// Run #66: a sweep of the preview's <c>:::</c> blocks, each checked by rendering the PDF export.
/// Data grids split quoted CSV cells on their commas ("$1,240,000" became three columns); formula
/// tables read "=B2*C2" as the number 22 (and a "Q1" label as 1) and never evaluated arithmetic;
/// references stacked a second heading under the author's own and left [@key] citations raw;
/// multi-level index terms printed as "NoSQL:Redis"; and :::line-numbers numbered paragraphs,
/// ignoring count-by.
/// </summary>
public class PreviewBlockSweepTests
{
    private static string Render(string md) =>
        new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));

    [Fact]
    public void Quoted_csv_cells_keep_their_commas()
    {
        var rows = ContainerBlockParsers.ParseDatagrid("Region,Revenue,Note\nNorth,\"$1,240,000\",\"said \"\"hi\"\"\"\n");
        Assert.Equal(new[] { "North", "$1,240,000", "said \"hi\"" }, rows[1]);
        Assert.Equal(3, rows[0].Length);
    }

    [Fact]
    public void Datagrid_renders_one_column_per_header()
    {
        var html = Render(":::datagrid\nRegion,Revenue\nNorth,\"$1,240,000\"\n:::\n");
        Assert.Equal(2, Regex.Matches(html, "<th ").Count);
        Assert.Contains(">$1,240,000</td>", html);
    }

    [Theory]
    [InlineData("=B2*C2")]
    [InlineData("Q1")]
    [InlineData("v2.1")]
    public void Text_is_not_a_number(string text) =>
        Assert.False(TableFormulaEvaluator.TryParseNumber(text, out _));

    [Theory]
    [InlineData("$1,240.50", 1240.5)]
    [InlineData("-3%", -3)]
    [InlineData("(100)", -100)]
    public void Numbers_still_parse(string text, double expected)
    {
        Assert.True(TableFormulaEvaluator.TryParseNumber(text, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Fact]
    public void Arithmetic_formulas_evaluate_and_feed_totals()
    {
        var md = "| Item | Qty | Price | Total |\n| --- | --: | --: | --: |\n| Pens | 4 | 2.50 | =B2*C2 |\n| Paper | 2 | 6.00 | =B3*C3 |\n| Sum |  |  | =SUM(D2:D3) |";
        var lines = TableFormulaEvaluator.EvaluateTableMarkdown(md).Split('\n');
        Assert.EndsWith("| 10 |", lines[2]);
        Assert.EndsWith("| 12 |", lines[3]);
        Assert.EndsWith("| 22 |", lines[4]);
    }

    [Fact]
    public void A_total_above_its_inputs_waits_for_them()
    {
        var md = "| a | b |\n|---|---|\n| =SUM(B3:B4) | x |\n| 1 | =A4*2 |\n| 3 | =A3+A4 |";
        var lines = TableFormulaEvaluator.EvaluateTableMarkdown(md).Split('\n');
        // B3 = 3*2 = 6, B4 = 1+3 = 4, A2 = 6+4 = 10.
        Assert.Equal("| 10 | x |", lines[2]);
    }

    [Fact]
    public void Arithmetic_carries_a_word_field_code()
    {
        var grid = new double?[,] { { null, null }, { 4, 2.5 } };
        Assert.True(TableFormulaEvaluator.TryEvaluateCell("= b2 * a2", 0, 0, grid, 2, 2, out var v, out var shown, out var instr));
        Assert.Equal(10, v);
        Assert.Equal("10", shown);
        Assert.Equal("=B2*A2", instr);
    }

    [Theory]
    [InlineData("=B2/0")]
    [InlineData("=SUM(B2")]
    [InlineData("=FOO(1)")]
    public void Bad_formulas_stay_as_written(string formula)
    {
        var grid = new double?[,] { { null, null }, { 4, 2.5 } };
        Assert.False(TableFormulaEvaluator.TryEvaluateCell(formula, 0, 0, grid, 2, 2, out _, out _, out _));
    }

    private const string Refs = "As Knuth showed [@knuth1984], and others [@knuth1984; @lamport1978].\n\n## References\n\n:::references\n@knuth1984\nauthor: Donald E. Knuth\ntitle: Literate Programming\nyear: 1984\n\n@lamport1978\nauthor: Lamport, Leslie\ntitle: Time, Clocks\nyear: 1978\n:::\n\nUnknown [@nobody] stays.\n";

    [Fact]
    public void Citations_link_to_their_reference()
    {
        var html = Render(Refs);
        Assert.Contains("<a href=\"#ref-knuth1984\">(Knuth, 1984)</a>", html);
        Assert.Contains("<a href=\"#ref-lamport1978\">Lamport, 1978</a>", html);
        Assert.Contains("id=\"ref-knuth1984\"", html);
        Assert.Contains("[@nobody]", html);
        Assert.DoesNotContain("[@knuth1984]", html);
    }

    [Fact]
    public void References_under_a_heading_do_not_add_another()
    {
        Assert.DoesNotContain(">Bibliography</h2>", Render(Refs));
        Assert.Contains(">Bibliography</h2>", Render("Text.\n\n:::references\n@a\nauthor: A\ntitle: T\nyear: 2020\n:::\n"));
    }

    [Fact]
    public void Index_terms_nest_every_level()
    {
        var html = Render("Pg^[index: \"Storage:Relational:PostgreSQL\"] and Redis^[index: \"Storage:NoSQL:Redis\"].\n\n## Index\n\n:::index\n:::\n");
        Assert.DoesNotContain(">NoSQL:Redis<", html);
        Assert.Matches(@"ms-index-subentry"">NoSQL</div>\s*<div class=""ms-index-subentry"" style=""margin-left:32px"">Redis</div>", html);
        Assert.DoesNotContain("class=\"ms-index-title\"", html);
    }

    [Fact]
    public void Word_export_with_an_ai_context_block_succeeds_and_keeps_its_variables()
    {
        // RenderAiContext created the settings part, then AddSettings added a second: every Word
        // export with an AI context block threw "Only one instance of the type is allowed".
        var md = ":::cover-page\ntitle: \"Plan\"\n:::\n\n:::ai-context\nmodel: Claude\ntimestamp: 2026-10-10\n:::\n\n# Plan\n";
        var settings = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml(md, entryPath: "word/settings.xml");
        Assert.Single(Regex.Matches(settings, "<w:docVars>"));
        Assert.Contains("w:name=\"MARKSMITH_AI_MODEL\" w:val=\"Claude\"", settings);
        Assert.Contains("w:name=\"Title\" w:val=\"Plan\"", settings);
    }

    [Fact]
    public void Word_datagrid_and_formulas_match_the_preview()
    {
        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml(
            ":::datagrid\nRegion,Revenue\nNorth,\"$1,240,000\"\n:::\n\n| Qty | Price | Total |\n|---|---|---|\n| 4 | 2.50 | =A2*B2 |\n");
        Assert.Contains(">$1,240,000</w:t>", xml);
        Assert.Contains("w:instr=\"=A2*B2\"", xml);
        Assert.Contains(">10</w:t>", xml);
    }

    [Fact]
    public void Word_bibliography_reads_without_a_field_update_and_citations_are_text()
    {
        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml(Refs);
        Assert.DoesNotContain("update fields", xml);
        Assert.Contains("BIBLIOGRAPHY", xml);
        Assert.Contains(">Donald E. Knuth (1984). </w:t>", xml);
        Assert.Contains("(Knuth, 1984)", xml);
        Assert.Contains("(Knuth, 1984; Lamport, 1978)", xml);
        Assert.DoesNotContain(">Bibliography<", xml); // "## References" already titles it
    }

    [Fact]
    public void Word_line_numbers_block_numbers_only_its_own_section()
    {
        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml(
            "# Contract\n\nIntro paragraph.\n\n:::line-numbers count-by=1\nOne.\nTwo.\n\nThree, a second paragraph.\n:::\n\nAfter.\n");
        // A blank line inside the block used to end it, so Word numbered the whole document.
        Assert.Single(Regex.Matches(xml, "<w:lnNumType "));
        Assert.Contains(">Three, a second paragraph.</w:t>", xml);
    }

    [Fact]
    public void Word_tabs_never_collapse_their_content()
    {
        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml(":::tabs\n=== A\nFirst.\n=== B\nSecond.\n:::\n");
        Assert.DoesNotContain("collapsed", xml);
        Assert.Contains(">Second.</w:t>", xml);
    }

    [Fact]
    public void Word_index_entries_are_complex_fields()
    {
        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml("Pg^[index: \"Storage:Relational\"] rocks.\n\n:::index\n:::\n");
        Assert.DoesNotContain("w:instr=\"XE", xml);
        Assert.Contains(" XE \"Storage:Relational\" </w:instrText>", xml);
    }

    [Fact]
    public void Line_numbers_count_written_lines_by_count_by()
    {
        var html = Render(":::line-numbers count-by=2\nOne **bold**\nTwo\n\nThree\nFour\n:::\n");
        Assert.Equal(4, Regex.Matches(html, "class=\"ms-ln\"").Count);
        Assert.Contains("<span class=\"ms-ln-n\" aria-hidden=\"true\">2</span>", html);
        Assert.Contains("<span class=\"ms-ln-n\" aria-hidden=\"true\">4</span>", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("ms-ln-gap", html);
    }
}
