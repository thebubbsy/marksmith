using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Word export on paper (routine run #60): every page of real exports was printed through Word
/// itself and checked. These pin what was wrong: invisible plain-text code, tables wider than the
/// page, sections that dropped headers or ran the wrong way round (:::columns), footnotes printed
/// as body text, lists glued to the next paragraph, and the rest.
/// </summary>
public class DocxPrintTests
{
    private sealed class Exported : IDisposable
    {
        private readonly string _path;
        public WordprocessingDocument Doc { get; }
        public MainDocumentPart Main => Doc.MainDocumentPart!;
        public W.Body Body => Main.Document.Body!;

        public Exported(string md, AppSettings? settings = null)
        {
            _path = Path.Combine(Path.GetTempPath(), $"ms-print-{Guid.NewGuid():N}.docx");
            new DocxExportService().ExportAsync(md, _path, settings ?? new AppSettings()).GetAwaiter().GetResult();
            Doc = WordprocessingDocument.Open(_path, false);
        }

        public List<W.SectionProperties> Sections =>
            Body.Descendants<W.SectionProperties>().ToList();

        public void Dispose()
        {
            Doc.Dispose();
            try { File.Delete(_path); } catch { /* temp */ }
        }
    }

    private static void AssertValid(Exported x)
    {
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(x.Doc)
            .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    // ---- Code ----

    [Theory]
    [InlineData("text")]
    [InlineData("ascii")]
    [InlineData("plaintext")]
    public void A_fence_in_a_language_without_highlighting_is_printed_in_the_text_colour(string lang)
    {
        using var x = new Exported($"```{lang}\n+---+\n| A |\n+---+\n```");
        var p = x.Body.Descendants<W.Paragraph>().First(q => q.InnerText.Contains("| A |"));
        var fill = p.ParagraphProperties!.Shading!.Fill!.Value!;
        foreach (var color in p.Descendants<W.Color>().Select(c => c.Val!.Value!))
            Assert.NotEqual(fill, color, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_long_listing_may_split_across_pages_but_a_short_one_stays_whole()
    {
        var longCode = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}"));
        using var x = new Exported($"```csharp\nvar a = 1;\n```\n\n```csharp\n{longCode}\n```");
        var code = x.Body.Elements<W.Paragraph>().Where(p => p.ParagraphProperties?.Shading is not null).ToList();
        Assert.Equal(2, code.Count);
        Assert.NotNull(code[0].ParagraphProperties!.KeepLines);
        Assert.Null(code[1].ParagraphProperties!.KeepLines);
    }

    [Fact]
    public void Back_to_back_code_blocks_stay_separate_boxes()
    {
        using var x = new Exported("```yaml\na: 1\n```\n\n```text\nplain\n```");
        var spaces = x.Body.Elements<W.Paragraph>()
            .Where(p => p.ParagraphProperties?.ParagraphBorders is not null)
            .Select(p => p.ParagraphProperties!.ParagraphBorders!.GetFirstChild<W.TopBorder>()!.Space!.Value)
            .ToList();
        Assert.Equal(2, spaces.Count);
        Assert.NotEqual(spaces[0], spaces[1]); // identical borders would be drawn as one box
    }

    [Fact]
    public void Inline_code_is_a_shaded_pill_not_a_black_box()
    {
        using var x = new Exported("Run `make all` now.");
        var run = x.Body.Descendants<W.Run>().First(r => r.InnerText == "make all");
        var border = run.RunProperties!.Border!;
        var shading = run.RunProperties!.Shading!;
        Assert.NotEqual("auto", border.Color!.Value);
        Assert.NotEqual("auto", shading.Fill!.Value);
        Assert.Equal(shading.Fill!.Value, border.Color!.Value);
    }

    // ---- Tables ----

    [Theory]
    [InlineData(true, 11906 - 2880)]
    [InlineData(false, 12240 - 2880)]
    public void A_table_is_exactly_as_wide_as_the_text_and_keeps_its_widths(bool a4, int textWidth)
    {
        using var x = new Exported(
            "| Area | Due | Notes |\n|---|---|---|\n| Notifications | 2026-10-14 | `platform node drain --id node-42 --grace-period 300s` |",
            new AppSettings { A4FixedWidth = a4 });
        var table = x.Body.Elements<W.Table>().Single();
        var grid = table.GetFirstChild<W.TableGrid>()!.Elements<W.GridColumn>().Select(g => int.Parse(g.Width!.Value!)).ToList();
        Assert.Equal(textWidth, grid.Sum());
        Assert.Equal(W.TableLayoutValues.Fixed, table.GetFirstChild<W.TableProperties>()!.TableLayout!.Type!.Value);
    }

    [Fact]
    public void Column_widths_give_every_column_its_longest_word_first()
    {
        var widths = DocxExportService.SolveColumnWidths(new List<(int, int)>
        {
            (2, 2),       // "#"
            (10, 10),     // a date
            (200, 30),    // long prose with a long word
        }, 9026);
        Assert.Equal(9026, widths.Sum());
        Assert.True(widths[1] >= 10 * 115, $"date column {widths[1]} would break the date");
        Assert.True(widths[2] > widths[1]);
    }

    [Fact]
    public void Column_widths_still_sum_to_the_page_when_even_the_words_do_not_fit()
    {
        var cols = Enumerable.Repeat((40, 40), 8).ToList();
        var widths = DocxExportService.SolveColumnWidths(cols, 9026);
        Assert.Equal(9026, widths.Sum());
        Assert.All(widths, w => Assert.True(w > 0));
    }

    [Fact]
    public void Table_cells_do_not_carry_the_body_paragraph_spacing()
    {
        using var x = new Exported("| A | B |\n|---|---|\n| 1 | 2 |");
        foreach (var p in x.Body.Descendants<W.TableCell>().SelectMany(c => c.Elements<W.Paragraph>()))
            Assert.Equal("0", p.ParagraphProperties!.SpacingBetweenLines!.After!.Value);
    }

    // ---- Lists ----

    [Fact]
    public void List_items_use_List_Paragraph_so_the_gap_to_the_next_paragraph_survives()
    {
        using var x = new Exported("- one\n- two\n\nAfter the list.");
        var paras = x.Body.Elements<W.Paragraph>().ToList();
        Assert.All(paras.Where(p => p.InnerText is "one" or "two"),
            p => Assert.Equal(DocxExportService.ListParagraphStyleId, p.ParagraphProperties!.ParagraphStyleId!.Val!.Value));
        var after = paras.Single(p => p.InnerText.Contains("After the list"));
        Assert.Null(after.ParagraphProperties?.ParagraphStyleId); // Normal: contextual spacing doesn't reach it
        Assert.Contains(x.Main.StyleDefinitionsPart!.Styles!.Elements<W.Style>(),
            s => s.StyleId?.Value == DocxExportService.ListParagraphStyleId);
    }

    [Fact]
    public void A_task_item_hangs_its_checkbox_where_the_bullet_would_be_instead_of_both()
    {
        using var x = new Exported("- [x] Done\n- [ ] Open\n- plain");
        var paras = x.Body.Elements<W.Paragraph>().ToList();
        var done = paras.Single(p => p.InnerText.Contains("Done"));
        Assert.Null(done.ParagraphProperties!.NumberingProperties);
        Assert.Equal("360", done.ParagraphProperties!.Indentation!.Hanging!.Value);
        var plain = paras.Single(p => p.InnerText.Contains("plain"));
        Assert.NotNull(plain.ParagraphProperties!.NumberingProperties);
    }

    [Fact]
    public void A_bulletless_task_item_still_imports_as_a_task()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms-print-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync("- [x] Done\n- [ ] Open", path, new AppSettings()).GetAwaiter().GetResult();
            // Tier 2: read the document itself, not the embedded source.
            var md = new ReverseImportService().ConvertDocxToMarkdown(path);
            Assert.Contains("- [x] Done", md);
            Assert.Contains("- [ ] Open", md);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- Footnotes ----

    [Fact]
    public void A_footnote_is_a_real_Word_footnote_at_the_foot_of_the_page()
    {
        using var x = new Exported("Claim one.[^a] Claim two.[^b] Again.[^a]\n\n[^a]: First source.\n[^b]: Second source.");
        var notes = x.Main.FootnotesPart!.Footnotes!.Elements<W.Footnote>().Where(f => f.Id!.Value > 0).ToList();
        Assert.Equal(2, notes.Count); // the repeated [^a] reuses its note
        Assert.Contains(notes, n => n.InnerText.Contains("First source."));
        Assert.Equal(2, x.Body.Descendants<W.FootnoteReference>().Count());
        Assert.DoesNotContain(x.Body.Descendants<W.Paragraph>(), p => p.InnerText.Contains("First source."));
        Assert.DoesNotContain("↩", x.Main.FootnotesPart.Footnotes.InnerText);
        AssertValid(x);
    }

    [Fact]
    public void A_footnote_referenced_from_a_table_cell_is_a_Word_footnote_too()
    {
        using var x = new Exported("| Metric | Value |\n|---|---|\n| Uptime[^u] | 99.9% |\n\n[^u]: Measured monthly.");
        Assert.Single(x.Body.Descendants<W.TableCell>().SelectMany(c => c.Descendants<W.FootnoteReference>()));
        Assert.Contains(x.Main.FootnotesPart!.Footnotes!.Elements<W.Footnote>(), f => f.InnerText.Contains("Measured monthly."));
        AssertValid(x);
    }

    // ---- Sections ----

    private const string Cover = ":::cover-page\ntitle: \"Report\"\nauthor: \"Team\"\n:::\n\n";

    [Fact]
    public void After_a_cover_the_first_body_page_has_its_header_and_numbers_from_one()
    {
        using var x = new Exported(Cover + "# Report\n\nBody.", new AppSettings { A4FixedWidth = true });
        var sections = x.Sections;
        Assert.Equal(2, sections.Count);
        var (cover, body) = (sections[0], sections[1]);
        Assert.NotNull(cover.GetFirstChild<W.TitlePage>());
        Assert.Null(body.GetFirstChild<W.TitlePage>()); // titlePg here blanked the first body page
        Assert.Equal(1, body.GetFirstChild<W.PageNumberType>()!.Start!.Value);
        Assert.NotEmpty(body.Elements<W.HeaderReference>());
        // The cover is printed on the same paper as the body.
        Assert.Equal(body.GetFirstChild<W.PageSize>()!.Width!.Value, cover.GetFirstChild<W.PageSize>()!.Width!.Value);
        Assert.Equal(11906u, cover.GetFirstChild<W.PageSize>()!.Width!.Value);
        // "Page X of Y" counts the body's pages, not the cover.
        var footer = x.Main.FooterParts.Single().Footer!;
        Assert.Contains(footer.Descendants<W.SimpleField>(), f => f.Instruction!.Value!.Contains("SECTIONPAGES"));
        AssertValid(x);
    }

    [Fact]
    public void Columns_apply_to_the_block_itself_and_every_section_keeps_header_and_paper()
    {
        using var x = new Exported("# Doc\n\nBefore.\n\n:::columns\nLeft\n===\nRight\n:::\n\nAfter.");
        var sections = x.Sections;
        Assert.Equal(3, sections.Count);
        // A section break describes the content BEFORE it: text above is one column, the block is two.
        Assert.Null(sections[0].GetFirstChild<W.Columns>());
        Assert.Equal(2, (int)sections[1].GetFirstChild<W.Columns>()!.ColumnCount!.Value);
        Assert.All(sections, s =>
        {
            Assert.NotEmpty(s.Elements<W.HeaderReference>());
            Assert.NotEmpty(s.Elements<W.FooterReference>());
            Assert.NotNull(s.GetFirstChild<W.PageSize>());
        });
        // The text after the block carries on on the same page.
        Assert.Equal(W.SectionMarkValues.Continuous, sections[2].GetFirstChild<W.SectionType>()!.Val!.Value);
        // The column break opens the right column's first paragraph, not a line of its own.
        var right = x.Body.Elements<W.Paragraph>().Single(p => p.InnerText == "Right");
        Assert.Equal(W.BreakValues.Column, right.Elements<W.Run>().First().GetFirstChild<W.Break>()!.Type!.Value);
        Assert.DoesNotContain(x.Body.Elements<W.Paragraph>(), p => p.InnerText.Length == 0 && p.Descendants<W.Break>().Any());
        AssertValid(x);
    }

    [Fact]
    public void With_a_cover_and_columns_the_body_starts_on_a_new_page_numbered_from_one()
    {
        using var x = new Exported(Cover + "Intro.\n\n:::columns\nA\n===\nB\n:::\n\nEnd.");
        var sections = x.Sections;
        Assert.Equal(4, sections.Count); // cover, intro, columns, end
        Assert.Equal(W.SectionMarkValues.NextPage, sections[1].GetFirstChild<W.SectionType>()!.Val!.Value);
        Assert.Equal(1, sections[1].GetFirstChild<W.PageNumberType>()!.Start!.Value);
        Assert.Null(sections[3].GetFirstChild<W.PageNumberType>()); // numbering carries on
        AssertValid(x);
    }

    [Fact]
    public void A_branded_cover_is_its_own_unnumbered_section()
    {
        using var x = new Exported("# Report\n\nBody.", new AppSettings { BrandCoverPage = true });
        var sections = x.Sections;
        Assert.Equal(2, sections.Count);
        Assert.Empty(sections[0].Elements<W.HeaderReference>());   // the cover printed the running header
        Assert.NotNull(sections[0].GetFirstChild<W.TitlePage>());
        Assert.Equal(1, sections[1].GetFirstChild<W.PageNumberType>()!.Start!.Value);
        AssertValid(x);
    }

    // ---- Contents ----

    [Fact]
    public void The_contents_lists_the_headings_before_Word_updates_it()
    {
        using var x = new Exported("# Title\n\n## Alpha\n\nText.\n\n### Beta\n\nText.\n\n#### Too deep\n\nText.",
            new AppSettings { IncludeToc = true });
        var toc = x.Body.Elements<W.Paragraph>()
            .Where(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value?.StartsWith("TOC") == true).ToList();
        Assert.Equal(new[] { "Title", "Alpha", "Beta" }, toc.Select(p => string.Concat(p.Descendants<W.Text>().Select(t => t.Text)).Trim()));
        Assert.All(toc, p => Assert.NotNull(p.GetFirstChild<W.Hyperlink>()?.Anchor));
        // Still one TOC field around them, so Word rebuilds it with page numbers.
        Assert.Contains(toc[0].Descendants<W.FieldCode>(), c => c.Text.Contains("TOC"));
        Assert.Contains(toc[^1].Descendants<W.FieldChar>(), c => c.FieldCharType!.Value == W.FieldCharValues.End);
        Assert.DoesNotContain("Word fills this in", x.Body.InnerText);
        AssertValid(x);
    }

    // ---- Page furniture ----

    [Fact]
    public void Word_does_not_hyphenate_what_the_preview_never_splits()
    {
        using var x = new Exported("Plain text.");
        Assert.Null(x.Main.DocumentSettingsPart!.Settings!.GetFirstChild<W.AutoHyphenation>());
    }

    [Fact]
    public void A_watermark_without_a_colour_uses_the_page_text_colour_in_values_VML_reads()
    {
        using var x = new Exported(":::watermark \"DRAFT\" opacity=0.12\n\n# Doc\n\nText.");
        var header = string.Concat(x.Main.HeaderParts.Select(h => h.Header!.OuterXml));
        Assert.Contains("string=\"DRAFT\"", header);
        Assert.Matches("fillcolor=\"#[0-9a-fA-F]{6}\"", header);   // a bare hex was ignored
        Assert.Contains("opacity=\"0.12\"", header);                // "12%" was ignored too
        Assert.DoesNotContain("fillcolor=\"#CCCCCC\"", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exports_open_in_current_Word_layout_not_Compatibility_Mode()
    {
        using var x = new Exported("Text.");
        var mode = x.Main.DocumentSettingsPart!.Settings!.GetFirstChild<W.Compatibility>()!
            .Elements<W.CompatibilitySetting>().Single(c => c.Name!.Value == W.CompatSettingNameValues.CompatibilityMode);
        Assert.Equal("15", mode.Val!.Value);
        AssertValid(x);
    }

    [Fact]
    public void Two_callouts_in_a_row_stay_two_cards_inside_the_margins()
    {
        using var x = new Exported("> [!NOTE]\n> One.\n\n> [!WARNING]\n> Two.", new AppSettings { A4FixedWidth = true });
        var tables = x.Body.Elements<W.Table>().ToList();
        Assert.Equal(2, tables.Count);
        Assert.IsType<W.Paragraph>(tables[0].NextSibling()); // adjacent tables are drawn as one
        Assert.All(tables, t => Assert.Equal((11906 - 2880).ToString(), t.GetFirstChild<W.TableProperties>()!.TableWidth!.Width!.Value));
        AssertValid(x);
    }

    [Fact]
    public void Cover_subtitle_stays_readable_on_a_dark_theme()
    {
        using var x = new Exported(":::cover-page\ntitle: \"Report\"\nsubtitle: \"Quarterly\"\n:::\n\nBody.", new AppSettings { Theme = "Dracula" });
        var run = x.Body.Descendants<W.Run>().First(r => r.InnerText == "Quarterly");
        var bg = AppServices.Themes.GetOrDefault("Dracula").Background;
        Assert.True(ContrastGuard.GetContrastRatio(run.RunProperties!.Color!.Val!.Value!, bg) >= 4.5);
    }
}
