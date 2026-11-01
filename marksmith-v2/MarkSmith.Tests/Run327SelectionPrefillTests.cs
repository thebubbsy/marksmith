using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #327, the line-list Insert dialogs (workflow, timeline, tab group, data grid, chart, code block):
// - selected whole lines open in the dialog instead of its sample (list markers taken off, a pipe
//   table's |---| row dropped, tab / pipe rows turned into label,value for a chart);
// - Insert replaces those lines instead of dropping the block after them;
// - a few words selected inside a paragraph keep the old behaviour (the block goes after the line);
// - the dialog says it was filled from the selection, and the status bar says what was replaced.
public class Run327SelectionPrefillTests
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

    // doc uses '|' for the selection bounds: "a\r|b\rc|\rd".
    private static (string Doc, int Start, int Length) Sel(string marked)
    {
        var first = marked.IndexOf('|');
        var second = marked.IndexOf('|', first + 1);
        var doc = marked.Remove(second, 1).Remove(first, 1);
        return (doc, first, second - first - 1);
    }

    [Theory]
    [InlineData("Intro\r|Step A\rStep B|\rOutro", "Step A\rStep B")]
    [InlineData("Intro\r- |Step A\r- Step B|\rOutro", "- Step A\r- Step B")]
    [InlineData("Intro\r|Step A\rStep B\r|Outro", "Step A\rStep B")]
    [InlineData("|Step A\nStep B  |", "Step A\nStep B  ")]
    [InlineData("  1. |One\r  2. Two|", "  1. One\r  2. Two")]
    [InlineData("- [ ] |Task|", "- [ ] Task")]
    [InlineData("|Only line|", "Only line")]
    public void WholeLines_takes_the_lines_a_selection_covers(string marked, string expected)
    {
        var (doc, start, length) = Sel(marked);
        var range = SelectionPrefill.WholeLines(doc, start, length);
        Assert.NotNull(range);
        Assert.Equal(expected, doc.Substring(range!.Value.Start, range.Value.Length));
    }

    [Theory]
    [InlineData("Read |the docs| today")]
    [InlineData("Read the |docs today|")]
    [InlineData("|Read| the docs")]
    [InlineData("Intro\r||Outro")]
    [InlineData("Intro|\r\r|Outro")]
    [InlineData("Text- |Step|")]
    public void WholeLines_refuses_words_inside_a_paragraph_and_blank_selections(string marked)
    {
        var (doc, start, length) = Sel(marked);
        Assert.Null(SelectionPrefill.WholeLines(doc, start, length));
    }

    [Fact]
    public void ListLines_takes_off_list_markers_and_blank_lines() =>
        Assert.Equal(new[] { "Plan", "Build", "Ship it", "2026: Launch" },
            SelectionPrefill.ListLines("- Plan\r\r* Build\r1. Ship it\r- [x] 2026: Launch\r  "));

    [Fact]
    public void GridRows_keep_the_cells_and_drop_a_pipe_separator()
    {
        Assert.Equal(new[] { "| Name | Score |", "| Ann | 3 |", "a\tb", "x,y" },
            SelectionPrefill.GridRows("| Name | Score |\r| :--- | ---: |\r| Ann | 3 |\ra\tb\r- x,y"));
    }

    [Fact]
    public void ChartRows_turn_tab_and_pipe_rows_into_label_value() =>
        Assert.Equal(new[] { "Quarter,Sales", "Q1,10", "Q2,25", "Q3,15" },
            SelectionPrefill.ChartRows("| Quarter | Sales |\r|---|---|\r| Q1 | 10 |\rQ2\t25\rQ3,15"));

    [Theory]
    [InlineData(1, "Filled from the selected line. Insert replaces it.")]
    [InlineData(3, "Filled from the 3 selected lines. Insert replaces them.")]
    public void Note_says_insert_replaces_the_lines(int lines, string expected) =>
        Assert.Equal(expected, SelectionPrefill.Note(lines));

    [Theory]
    [InlineData("Intro\r|- A\r- B|\rOutro", "Intro\r\r:::workflow\r- A\r- B\r:::\r\rOutro")]
    [InlineData("Intro\r\r|- A\r- B|\r\rOutro", "Intro\r\r:::workflow\r- A\r- B\r:::\r\rOutro")]
    [InlineData("|- A\r- B|", ":::workflow\r- A\r- B\r:::\r")]
    [InlineData("Intro\n|- A\n- B|", "Intro\n\n:::workflow\n- A\n- B\n:::\n")]
    public void Replace_puts_the_block_where_the_lines_were(string marked, string expected)
    {
        var (doc, start, length) = Sel(marked);
        var range = SelectionPrefill.WholeLines(doc, start, length)!.Value;
        var block = InsertSnippetBuilder.Workflow(SelectionPrefill.ListLines(doc.Substring(range.Start, range.Length)));
        var plan = BlockInsertion.Replace(doc, range.Start, range.Length, block);
        var result = doc[..plan.Start] + plan.Text + doc[(plan.Start + plan.Length)..];
        Assert.Equal(expected, result);
        Assert.Equal(":::", result[(plan.CaretStart - 3)..plan.CaretStart]); // caret right after the block
    }

    [Fact]
    public void Dialogs_open_with_the_selection_and_replace_it()
    {
        var main = Read("MarkSmith.Desktop/MainWindow.xaml.cs");
        foreach (var (handler, title, what) in new[]
        {
            ("private async void OnInsertWorkflowClick", "Insert workflow", "a workflow"),
            ("private async void OnInsertTimelineClick", "Insert timeline", "a timeline"),
            ("private async void OnInsertTabsClick", "Insert tab group", "a tab group"),
            ("private async void OnInsertChartClick", "Insert chart", "a chart"),
            ("private async void OnInsertDatagridClick", "Insert data grid", "a data grid"),
            ("private async void OnCodeBlockClick", "Insert code block", "a code block"),
        })
        {
            var body = Method(main, handler);
            Assert.Contains("var source = SelectedLinesForInsert();", body);
            Assert.Contains("NoteSelectionSource(control, source);", body);
            Assert.Contains($"InsertBlockFrom(source, ", body);
            Assert.Contains($"\"{what}\")", body);
            Assert.Contains($"\"{title}\"", body);
        }

        var from = Method(main, "private void InsertBlockFrom(");
        // Only the lines the dialog was filled from, still unchanged, are replaced.
        Assert.Contains("string.Equals(text.Substring(s.Start, s.Length), s.Text, StringComparison.Ordinal)", from);
        Assert.Contains("BlockInsertion.Replace(", from);
        Assert.Contains("ViewModel.BreakUndoBurst();", from);
        Assert.Contains("ViewModel.StatusText = $\"Replaced {Services.SelectionPrefill.TheSelectedLines(", from);
        // The portal edits its own caret, so it never prefills from the main editor.
        Assert.Contains("if (_portalOpen || tb is null) return null;", Method(main, "private (int Start, int Length, string Text)? SelectedLinesForInsert()"));
    }

    [Fact]
    public void Note_row_wraps_and_skips_the_glyph_for_screen_readers()
    {
        var controls = Read("MarkSmith.Desktop/Views/InsertDialogControls.cs");
        var start = controls.IndexOf("public void ShowNote(string text)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = controls[start..controls.IndexOf("\n    }\n", start, StringComparison.Ordinal)];
        Assert.Contains("new Grid", body);
        Assert.Contains("TextWrapping = TextWrapping.Wrap", body);
        Assert.Contains("AccessibilityView.Raw", body);
        Assert.Contains(".Themed(TextBlock.ForegroundProperty, \"TextFillColorSecondaryBrush\")", body);
        Assert.Contains("_root.Children.Insert(1, row);", body);
        Assert.Contains("public CodeBlockInsertControl(string initialBody = \"\")", controls);
        Assert.Contains("public ChartInsertControl(string? initialData = null)", controls);
    }
}
