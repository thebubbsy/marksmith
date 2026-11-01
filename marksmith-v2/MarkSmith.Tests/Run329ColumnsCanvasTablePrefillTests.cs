using System;
using System.IO;
using System.Linq;
using Markdig;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #329:
// - a table without a header row writes a blank header row (GitHub and the preview show a table
//   that starts at its |---| row as plain text);
// - selected paragraphs open the Columns dialog one per column, the count sized to them, extra
//   paragraphs kept in the last column, and Insert replaces them;
// - a selected <svg> drawing opens the Drawing canvas dialog sized as the drawing sizes itself,
//   resizing sets the root's width/height (a viewBox is added so it scales), and Insert replaces it.
public class Run329ColumnsCanvasTablePrefillTests
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

    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = code.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        return end < 0 ? code[start..] : code[start..end];
    }

    [Fact]
    public void Table_without_header_writes_a_blank_header_row()
    {
        Assert.Equal("\n|  |  |\n| --- | --- |\n| Value 1 | Value 2 |\n",
            InsertSnippetBuilder.Table(1, 2, includeHeaderRow: false));
    }

    [Fact]
    public void Table_without_header_renders_as_a_table_in_the_preview()
    {
        var html = Markdig.Markdown.ToHtml(InsertSnippetBuilder.Table(2, 2, includeHeaderRow: false),
            new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        Assert.Contains("<table>", html);
        Assert.Contains("<td>Value 1</td>", html);
        Assert.Contains("<td>Value 4</td>", html);
    }

    [Fact]
    public void ColumnParagraphs_splits_at_blank_lines_and_keeps_each_as_written()
    {
        var p = SelectionPrefill.ColumnParagraphs("First line\r- a point\r\r\rSecond\n  \nThird");
        Assert.Equal(new[] { "First line\n- a point", "Second", "Third" }, p);
    }

    [Theory]
    [InlineData("Intro\n:::\nAfter")]
    [InlineData(":::note\nHi\n:::")]
    [InlineData("  \n \n")]
    [InlineData(null)]
    public void ColumnParagraphs_is_empty_for_a_fence_or_nothing(string? text) =>
        Assert.Empty(SelectionPrefill.ColumnParagraphs(text));

    [Fact]
    public void Columns_with_contents_fills_each_column_and_pads_with_placeholders()
    {
        Assert.Equal("\n:::columns count=\"3\"\nLeft\n===\nMiddle\n===\nColumn 3 content\n:::\n",
            InsertSnippetBuilder.Columns(3, new[] { "Left", "Middle" }));
    }

    [Fact]
    public void Columns_with_more_contents_than_columns_keeps_the_rest_in_the_last_column()
    {
        Assert.Equal("\n:::columns count=\"2\"\nA\n===\nB\n\nC\n\nD\n:::\n",
            InsertSnippetBuilder.Columns(2, new[] { "A", "B", "C", "D" }));
    }

    [Fact]
    public void Columns_with_contents_clamps_the_count()
    {
        Assert.StartsWith("\n:::columns count=\"4\"\n", InsertSnippetBuilder.Columns(9, new[] { "x" }));
        Assert.StartsWith("\n:::columns count=\"2\"\n", InsertSnippetBuilder.Columns(0, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("<svg width=\"300\"><rect/></svg>", true)]
    [InlineData("  <SVG viewBox=\"0 0 10 10\">\n<circle/>\n</SVG>  ", true)]
    [InlineData("<svgfoo></svgfoo></svg>", false)]
    [InlineData("<div><svg></svg></div>", false)]
    [InlineData("<svg>\n:::\n</svg>", false)]
    [InlineData("Some words", false)]
    public void CanvasSvg_takes_only_a_whole_svg_drawing(string text, bool taken) =>
        Assert.Equal(taken ? text.Trim() : "", SelectionPrefill.CanvasSvg(text));

    [Theory]
    [InlineData("<svg width=\"320\" height=\"180px\">", 320, 180)]
    [InlineData("<svg viewBox=\"0 0 64.4 32\">", 64, 32)]
    [InlineData("<svg width=\"100\" viewBox=\"0,0,50,25\">", 100, 25)]
    [InlineData("<svg width='120' height='60'>", 120, 60)]
    public void SvgSize_reads_width_and_height_else_the_viewBox(string tag, int w, int h) =>
        Assert.Equal((w, h), InsertSnippetBuilder.SvgSize(tag));

    [Theory]
    [InlineData("<svg>")]
    [InlineData("<svg width=\"100%\" height=\"100%\">")]
    [InlineData(null)]
    public void SvgSize_is_null_without_a_size(string? tag) =>
        Assert.Null(InsertSnippetBuilder.SvgSize(tag));

    [Fact]
    public void Canvas_with_svg_sets_the_size_and_adds_a_viewBox_so_the_drawing_scales()
    {
        var md = InsertSnippetBuilder.Canvas(200, 100, "<svg width=\"400\" height=\"200\">\n  <rect width=\"10\" height=\"10\"/>\n</svg>");
        Assert.Equal("\n:::canvas\n<svg viewBox=\"0 0 400 200\" width=\"200\" height=\"100\">\n  <rect width=\"10\" height=\"10\"/>\n</svg>\n:::\n", md);
    }

    [Fact]
    public void Canvas_with_svg_keeps_its_viewBox_and_adds_missing_size_attributes()
    {
        var md = InsertSnippetBuilder.Canvas(50, 60, "<svg viewBox=\"0 0 5 6\"><g/></svg>");
        Assert.Equal("\n:::canvas\n<svg viewBox=\"0 0 5 6\" width=\"50\" height=\"60\"><g/></svg>\n:::\n", md);
    }

    [Fact]
    public void Canvas_with_empty_svg_is_the_starter_canvas() =>
        Assert.Equal(InsertSnippetBuilder.Canvas(120, 80), InsertSnippetBuilder.Canvas(120, 80, ""));

    [Fact]
    public void Main_window_columns_and_canvas_prefill_and_replace_the_selection()
    {
        var code = Read("MarkSmith.Desktop/MainWindow.xaml.cs");

        var columns = Method(code, "private async void OnInsertColumnsClick(");
        Assert.Contains("SelectionPrefill.ColumnParagraphs(s.Text)", columns);
        Assert.Contains("Math.Clamp(paragraphs.Count, 2, 4)", columns);
        // Run #330 counts the note and status in paragraphs.
        Assert.Contains("NoteSelectionSource(control, source, counted);", columns);
        Assert.Contains("InsertBlockFrom(source, control.Snippet, \"columns\", counted);", columns);
        Assert.DoesNotContain("InsertBlock(control.Snippet);", columns);

        var canvas = Method(code, "private async void OnInsertCanvasClick(");
        Assert.Contains("SelectionPrefill.CanvasSvg(s.Text)", canvas);
        Assert.Contains("InsertSnippetBuilder.Canvas(v[0], v[1], svg)", canvas);
        Assert.Contains("NoteSelectionSource(control, source);", canvas);
        Assert.Contains("InsertBlockFrom(source, control.Snippet, \"a drawing canvas\");", canvas);
        Assert.DoesNotContain("InsertBlock(control.Snippet);", canvas);
    }

    [Fact]
    public void Table_header_tooltip_says_clearing_it_leaves_a_blank_header()
    {
        var code = Read("MarkSmith.Desktop/Views/InsertDialogControls.cs");
        Assert.Contains("Clear it to leave the header row blank", code);
        Assert.Contains("make that row a body row under a blank header", code);
    }
}
