using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Email;
using MarkSmith.Services.Presentation;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Tests;

// Run #330:
// - a header-less table's blank header row (written since #329 so GitHub shows a table) is hidden
//   in the preview and left out of the Word, PowerPoint and email exports;
// - the Table dialog's header checkbox says so;
// - the Columns dialog counts the selection in paragraphs ("the 2 selected paragraphs"), in its
//   note and the status line;
// - a selected setext heading ("Title" over "===") stays a heading in its column instead of
//   splitting it, and a "===" line on its own starts the next column.
public class Run330BlankHeaderColumnsTests
{
    private const string HeaderLess = "|  |  |\n| --- | --- |\n| Value 1 | Value 2 |\n| Value 3 | Value 4 |\n";

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

    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = code.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        return end < 0 ? code[start..] : code[start..end];
    }

    private static TableRow FirstRow(string markdown) =>
        Markdown.Parse(markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build())
            .Descendants<Table>().First().OfType<TableRow>().First();

    [Theory]
    [InlineData(HeaderLess, true)]
    [InlineData("| A | B |\n| --- | --- |\n| 1 | 2 |\n", false)]
    [InlineData("|  | B |\n| --- | --- |\n| 1 | 2 |\n", false)]
    [InlineData("| ![x](a.png) |  |\n| --- | --- |\n| 1 | 2 |\n", false)]
    [InlineData("| `` `x` `` |  |\n| --- | --- |\n| 1 | 2 |\n", false)]
    public void IsBlank_only_for_a_header_row_with_nothing_in_it(string markdown, bool blank)
    {
        Assert.Equal(blank, BlankTableHeader.IsBlank(FirstRow(markdown)));
    }

    [Fact]
    public void IsBlank_keeps_a_table_that_is_only_a_blank_header()
    {
        Assert.False(BlankTableHeader.IsBlank(FirstRow("|  |  |\n| --- | --- |\n")));
    }

    [Fact]
    public void IsBlank_is_false_for_body_rows()
    {
        var table = Markdown.Parse(HeaderLess, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build())
            .Descendants<Table>().First();
        Assert.All(table.OfType<TableRow>().Skip(1), r => Assert.False(BlankTableHeader.IsBlank(r)));
    }

    [Fact]
    public void Word_export_leaves_the_blank_header_row_out()
    {
        var path = Path.Combine(Path.GetTempPath(), $"run330_{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync("# Report\n\n" + HeaderLess, path, new AppSettings()).GetAwaiter().GetResult();
            using var doc = WordprocessingDocument.Open(path, false);
            var table = doc.MainDocumentPart!.Document.Body!.Descendants<W.Table>()
                .First(t => t.InnerText.Contains("Value 1"));
            var rows = table.Elements<W.TableRow>().ToList();
            Assert.Equal(2, rows.Count);
            Assert.StartsWith("Value 1", rows[0].InnerText);
            Assert.Empty(table.Descendants<W.TableHeader>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Word_export_keeps_a_real_header_row()
    {
        var path = Path.Combine(Path.GetTempPath(), $"run330_{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync("| Name | Qty |\n| --- | --- |\n| Pens | 4 |\n", path, new AppSettings()).GetAwaiter().GetResult();
            using var doc = WordprocessingDocument.Open(path, false);
            var table = doc.MainDocumentPart!.Document.Body!.Descendants<W.Table>().First(t => t.InnerText.Contains("Pens"));
            Assert.Equal(2, table.Elements<W.TableRow>().Count());
            Assert.Single(table.Descendants<W.TableHeader>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Preview_hides_an_empty_header_with_css_its_cells_can_match()
    {
        var html = new MarkdownHtmlService().Render(HeaderLess, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));
        Assert.Contains("table:has(> tbody) > thead:not(:has(th:not(:empty))) { display: none; }", html);
        // :empty only matches a cell with no text at all, white space included.
        Assert.Matches(new Regex(@"<thead>\s*<tr>\s*<th></th>\s*<th></th>"), html);
    }

    [Fact]
    public void Slides_leave_the_blank_header_row_out()
    {
        var deck = SlideDeckBuilder.Build("## Data\n\n" + HeaderLess);
        var table = deck.Slides.SelectMany(s => s.Blocks).OfType<TableSlideBlock>().Single();
        Assert.Empty(table.Header);
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void Email_leaves_the_blank_header_row_out()
    {
        var result = new EmailHtmlRenderer(new AppSettings(), AppServices.Themes.GetOrDefault("GitHub Light")).Render(HeaderLess);
        Assert.Contains("Value 1", result.Html);
        Assert.DoesNotContain("<th", result.Html);
        // The plain-text part has no underline row for a header that isn't there.
        Assert.DoesNotMatch(new Regex(@"(?m)^\s*-{3,}(\s+-{3,})*\s*$"), result.Text);
        Assert.Contains("Value 1", result.Text);
    }

    [Fact]
    public void Header_checkbox_tooltip_says_the_blank_row_is_left_out()
    {
        var code = Read("MarkSmith.Desktop/Views/InsertDialogControls.cs");
        Assert.Contains("Clear it to leave the header row blank (the preview and exports leave it out)", code);
        Assert.Contains("under a blank header (the preview and exports leave it out)", code);
    }

    [Fact]
    public void Note_and_status_can_count_paragraphs()
    {
        Assert.Equal("Filled from the 2 selected paragraphs. Insert replaces them.", SelectionPrefill.Note(2, "selected paragraph"));
        Assert.Equal("Filled from the selected paragraph. Insert replaces it.", SelectionPrefill.Note(1, "selected paragraph"));
        Assert.Equal("the 3 selected paragraphs", SelectionPrefill.TheSelected(3, "selected paragraph"));
        // The line wording is unchanged.
        Assert.Equal("Filled from the 4 selected lines. Insert replaces them.", SelectionPrefill.Note(4));
        Assert.Equal("the selected line", SelectionPrefill.TheSelectedLines(1));
    }

    [Fact]
    public void Columns_handler_counts_paragraphs_in_its_note_and_status()
    {
        var code = Read("MarkSmith.Desktop/MainWindow.xaml.cs");
        var handler = Method(code, "private async void OnInsertColumnsClick");
        Assert.Contains("(paragraphs.Count, \"selected paragraph\")", handler);
        Assert.Contains("NoteSelectionSource(control, source, counted);", handler);
        Assert.Contains("InsertBlockFrom(source, control.Snippet, \"columns\", counted);", handler);
        var insert = Method(code, "private void InsertBlockFrom(");
        Assert.Contains("SelectionPrefill.TheSelected(c.Count, c.Noun)", insert);
    }

    [Fact]
    public void ColumnParagraphs_keeps_a_setext_heading_as_a_heading()
    {
        var p = SelectionPrefill.ColumnParagraphs("Plan\n===\nSet the goals.\n\nDo\nthe work\n=====\nThen check.");
        Assert.Equal(new[] { "# Plan\nSet the goals.", "# Do the work\nThen check." }, p);
    }

    [Fact]
    public void ColumnParagraphs_starts_a_column_at_a_lone_break_line()
    {
        var p = SelectionPrefill.ColumnParagraphs("Left side\n\n===\n\nRight side\n===");
        Assert.Equal(new[] { "Left side", "# Right side" }, p);
        Assert.Equal(new[] { "One", "Two" }, SelectionPrefill.ColumnParagraphs("One\n\n  ===  \nTwo"));
    }

    [Fact]
    public void ColumnParagraphs_does_not_make_a_heading_of_a_list()
    {
        var p = SelectionPrefill.ColumnParagraphs("- a\n- b\n===\nAfter");
        Assert.Equal(new[] { "- a\n- b", "After" }, p);
        // Only the plain lines after the list become the heading.
        Assert.Equal(new[] { "- a\n# Title" }, SelectionPrefill.ColumnParagraphs("- a\nTitle\n==="));
        Assert.Equal(new[] { "- a", "# Title" }, SelectionPrefill.ColumnParagraphs("- a\n\nTitle\n==="));
    }

    [Fact]
    public void A_column_with_a_setext_heading_previews_as_one_column()
    {
        var p = SelectionPrefill.ColumnParagraphs("Plan\n===\nSet goals.\n\nDo it.");
        var snippet = InsertSnippetBuilder.Columns(2, p);
        // Only the dialog's own column separator remains.
        Assert.Single(Regex.Matches(snippet, @"(?m)^===$"));
        Assert.Contains("# Plan", snippet);
    }
}
