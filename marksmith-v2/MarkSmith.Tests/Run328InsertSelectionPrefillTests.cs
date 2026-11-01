using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #328, the selection prefill run #327 gave the line-list dialogs, carried to the Table, SmartArt,
// Image and Video Insert dialogs:
// - selected comma / tab / pipe rows open the Table dialog as its cells (sized to them), and Insert
//   replaces them; clearing "Include header row" moves the header into the body instead of dropping a row;
// - a selected (nested) list opens as the SmartArt outline with its levels kept, and is replaced;
// - a selected image path or web address opens as the Image source, other words as its alt text;
// - a selected link opens as the Video address;
// - whole selected lines are replaced (with the dialog's note), words inside a paragraph stay.
public class Run328InsertSelectionPrefillTests
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

    private static string[][] Flat(IReadOnlyList<IReadOnlyList<string>>? cells) =>
        cells!.Select(r => r.ToArray()).ToArray();

    [Fact]
    public void TableCells_reads_pipe_rows_and_drops_the_separator_row()
    {
        var cells = Flat(SelectionPrefill.TableCells("| Name | Qty |\r|---|:---:|\r| Pens | 4 |"));
        Assert.Equal(new[] { new[] { "Name", "Qty" }, new[] { "Pens", "4" } }, cells);
    }

    [Fact]
    public void TableCells_reads_tab_and_comma_rows()
    {
        Assert.Equal(new[] { new[] { "Name", "Qty" }, new[] { "Pens", "4" } },
            Flat(SelectionPrefill.TableCells("Name\tQty\nPens\t4")));
        Assert.Equal(new[] { new[] { "a", "b", "c" }, new[] { "1", "2", "3" } },
            Flat(SelectionPrefill.TableCells("- a, b, c\r- 1,2,3")));
    }

    [Fact]
    public void TableCells_keeps_an_escaped_pipe_inside_its_cell() =>
        Assert.Equal(new[] { new[] { @"a \| b", "c" } }, Flat(SelectionPrefill.TableCells(@"| a \| b | c |")));

    [Theory]
    [InlineData("Plan\rDo\rCheck")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TableCells_is_null_without_a_separator(string? text) =>
        Assert.Null(SelectionPrefill.TableCells(text));

    [Fact]
    public void TableCells_caps_rows_and_columns_at_the_dialogs_limits()
    {
        var text = string.Join("\n", Enumerable.Range(0, 80).Select(i => string.Join(",", Enumerable.Range(0, 30))));
        var cells = SelectionPrefill.TableCells(text)!;
        Assert.Equal(51, cells.Count);
        Assert.All(cells, r => Assert.Equal(20, r.Count));
    }

    [Fact]
    public void Table_with_cells_fills_header_and_body_and_pads_with_empty_cells()
    {
        var cells = new List<IReadOnlyList<string>> { new[] { "Name", "Qty" }, new[] { "Pens" } };
        Assert.Equal("\n| Name | Qty |\n| --- | --- |\n| Pens |  |\n|  |  |\n",
            InsertSnippetBuilder.Table(2, 2, true, cells));
    }

    [Fact]
    public void Table_with_cells_and_no_header_puts_every_row_in_the_body()
    {
        var cells = new List<IReadOnlyList<string>> { new[] { "Name", "Qty" }, new[] { "Pens", "4" } };
        Assert.Equal("\n| --- | --- |\n| Name | Qty |\n| Pens | 4 |\n",
            InsertSnippetBuilder.Table(2, 2, false, cells));
    }

    [Fact]
    public void Table_with_cells_escapes_a_bare_pipe_and_cuts_extra_columns()
    {
        var cells = new List<IReadOnlyList<string>> { new[] { "a|b", "x", "dropped" }, new[] { @"c\|d", "y" } };
        Assert.Equal("\n| a\\|b | x |\n| --- | --- |\n| c\\|d | y |\n",
            InsertSnippetBuilder.Table(1, 2, true, cells));
    }

    [Fact]
    public void OutlineLines_keeps_nesting_relative_to_the_least_indented_line()
    {
        var lines = SelectionPrefill.OutlineLines("  - Plan\r    - Set goals\r\r  - Do\r\t\t- Deep");
        Assert.Equal(new[] { "Plan", "  Set goals", "Do", "      Deep" }, lines);
        var rows = SmartArtInsert.Parse(string.Join("\n", lines));
        Assert.Equal(new[] { 0, 1, 0, 1 }, rows.Select(r => r.Level).ToArray());
    }

    [Theory]
    [InlineData("https://example.com/cat.png", "https://example.com/cat.png", "")]
    [InlineData("<https://example.com/a b.png>", "https://example.com/a b.png", "")]
    [InlineData("\"C:\\Pictures\\My chart.PNG\"", "C:\\Pictures\\My chart.PNG", "")]
    [InlineData("images/diagram.svg", "images/diagram.svg", "")]
    [InlineData("- https://example.com/photo", "https://example.com/photo", "")]
    [InlineData("A chart of the quarter", "", "A chart of the quarter")]
    [InlineData("two\rlines", "", "")]
    [InlineData("   ", "", "")]
    public void ImagePrefill_sorts_a_source_from_alt_text(string selection, string source, string alt) =>
        Assert.Equal((source, alt), SelectionPrefill.ImagePrefill(selection));

    [Fact]
    public void ImagePrefill_ignores_words_too_long_for_alt_text() =>
        Assert.Equal(("", ""), SelectionPrefill.ImagePrefill(new string('w', 251)));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/watch?v=abc")]
    [InlineData("  <https://vimeo.com/12345>  ", "https://vimeo.com/12345")]
    [InlineData("* https://loom.com/share/x", "https://loom.com/share/x")]
    [InlineData("watch this video", "")]
    [InlineData("ftp://example.com/v.mp4", "")]
    [InlineData("https://a.com\rhttps://b.com", "")]
    [InlineData(null, "")]
    public void VideoPrefill_takes_only_a_one_line_web_address(string? selection, string expected) =>
        Assert.Equal(expected, SelectionPrefill.VideoPrefill(selection));

    [Theory]
    [InlineData(1, "the selected line")]
    [InlineData(4, "the 4 selected lines")]
    public void TheSelectedLines_drops_the_count_for_one_line(int lines, string expected) =>
        Assert.Equal(expected, SelectionPrefill.TheSelectedLines(lines));

    [Fact]
    public void Main_window_handlers_prefill_and_replace_the_selection()
    {
        var code = Read("MarkSmith.Desktop/MainWindow.xaml.cs");

        var smartArt = Method(code, "private async void OnInsertSmartArtClick(");
        Assert.Contains("SelectionPrefill.OutlineLines", smartArt);
        Assert.Contains("NoteSelectionSource(control, source);", smartArt);
        Assert.Contains("InsertBlockFrom(source, control.GeneratedSnippet, \"SmartArt\");", smartArt);

        var image = Method(code, "private async void OnImageClick(");
        Assert.Contains("SelectionPrefill.ImagePrefill(PasteTextBox.SelectedText)", image);
        Assert.Contains("new Views.ImageInsertControl(ViewModel.DocumentFolder, initialSource, initialAlt)", image);
        Assert.Contains("InsertBlockFrom(source, control.Snippet, \"a picture\");", image);

        var table = Method(code, "private async void OnTableClick(");
        Assert.Contains("SelectionPrefill.TableCells(s.Text)", table);
        Assert.Contains("new Views.TableInsertControl(cells)", table);
        Assert.Contains("InsertBlockFrom(source, control.Snippet, \"a table\");", table);

        var video = Method(code, "private async void OnInsertEmbedClick(");
        Assert.Contains("SelectionPrefill.VideoPrefill(PasteTextBox.SelectedText)", video);
        Assert.Contains("new Views.EmbedInsertControl(initialUrl)", video);
        Assert.Contains("InsertBlockFrom(source, control.Snippet, \"a video card\");", video);

        // A dialog that found nothing to use in the selection drops the source, so Insert never
        // replaces lines it didn't open with, and the note isn't shown.
        foreach (var handler in new[] { smartArt, image, table, video })
        {
            Assert.Contains("source = null;", handler);
            Assert.True(handler.IndexOf("source = null;", StringComparison.Ordinal)
                < handler.IndexOf("NoteSelectionSource(", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Dialog_controls_take_their_prefill()
    {
        var controls = Read("MarkSmith.Desktop/Views/InsertDialogControls.cs");
        Assert.Contains("public TableInsertControl(IReadOnlyList<IReadOnlyList<string>>? cells = null)", controls);
        Assert.Contains("_rows.Value = Math.Clamp(Rows + (IncludeHeaderRow ? -1 : 1), 1, 50);", controls);
        Assert.Contains("public EmbedInsertControl(string? initialUrl = null)", controls);
        // TextChanged doesn't come for text set before the dialog shows: the provider and the alt
        // text are worked out straight after the prefill.
        Assert.Contains("_url.Text = initialUrl.Trim();\n            FollowLink();", controls);

        var image = Read("MarkSmith.Desktop/Views/ImageInsertControl.cs");
        Assert.Contains("string initialSource = \"\", string initialAlt = \"\")", image);
        // Alt before source, so the source's automatic alt text doesn't replace the selected words.
        Assert.True(image.IndexOf("_alt.Text = initialAlt;", StringComparison.Ordinal)
            < image.IndexOf("_source.Text = initialSource;", StringComparison.Ordinal));

        Assert.Contains("_source.Text = initialSource;", image);
        Assert.True(image.IndexOf("OnSourceChanged();", image.IndexOf("_source.Text = initialSource;", StringComparison.Ordinal), StringComparison.Ordinal) > 0);

        var smartArt = Read("MarkSmith.Desktop/Views/SmartArtInsertControl.cs");
        Assert.Contains("public SmartArtInsertControl(string? initialOutline = null)", smartArt);
        Assert.True(smartArt.IndexOf("ApplyExample(SmartArt.Examples[0]);\n        if (!string.IsNullOrWhiteSpace(initialOutline))", StringComparison.Ordinal) >= 0);
    }
}
