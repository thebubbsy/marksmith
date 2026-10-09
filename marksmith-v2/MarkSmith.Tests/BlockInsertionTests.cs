using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

public class BlockInsertionTests
{
    private const string Table = "\n| A | B |\n| --- | --- |\n| 1 | 2 |\n";

    private static (string Doc, string Caret) Apply(string doc, int selStart, int selLength, string prefix, string suffix = "", bool caretPlaced = true)
    {
        var p = BlockInsertion.Plan(doc, selStart, selLength, prefix, suffix, caretPlaced);
        var result = doc.Substring(0, p.Start) + p.Text + doc.Substring(p.Start + p.Length);
        var caret = result.Substring(0, p.CaretStart) + "|" + result.Substring(p.CaretStart + p.CaretLength);
        return (result, caret);
    }

    [Fact]
    public void Caret_never_placed_appends_after_the_document_not_above_the_title()
    {
        var (doc, _) = Apply("# Title\rIntro text", 0, 0, ":::shapes\rbox\r:::", caretPlaced: false);
        Assert.Equal("# Title\rIntro text\r\r:::shapes\rbox\r:::\r", doc);
    }

    [Fact]
    public void Caret_in_the_middle_of_a_line_inserts_below_that_line_without_splitting_it()
    {
        var (doc, caret) = Apply("Hello world\rNext", 5, 0, Table);
        Assert.Equal("Hello world\r\r| A | B |\r| --- | --- |\r| 1 | 2 |\r\rNext", doc);
        Assert.Contains("| 1 | 2 ||\r", caret);
    }

    [Fact]
    public void Caret_at_the_start_of_a_line_inserts_above_it()
    {
        var (doc, _) = Apply("Para one\r\rPara two", 10, 0, ":::x\r:::");
        Assert.Equal("Para one\r\r:::x\r:::\r\rPara two", doc);
    }

    [Fact]
    public void An_empty_line_between_paragraphs_is_filled_not_padded_further()
    {
        var (doc, _) = Apply("a\r\r\rb", 2, 0, ":::x\r:::");
        Assert.Equal("a\r\r:::x\r:::\r\rb", doc);
    }

    [Fact]
    public void Empty_document_gets_the_block_and_one_trailing_break()
    {
        var (doc, _) = Apply("", 0, 0, Table);
        Assert.Equal("| A | B |\r| --- | --- |\r| 1 | 2 |\r", doc);
    }

    [Fact]
    public void A_selection_is_kept_and_the_block_goes_after_its_last_line()
    {
        var (doc, _) = Apply("Keep me\rmore", 0, 4, ":::x\r:::");
        Assert.Equal("Keep me\r\r:::x\r:::\r\rmore", doc);
    }

    [Fact]
    public void A_selection_of_whole_lines_including_the_break_inserts_before_the_next_line()
    {
        var (doc, _) = Apply("One\rTwo\r", 0, 4, ":::x\r:::");
        Assert.Equal("One\r\r:::x\r:::\r\rTwo\r", doc);
    }

    [Fact]
    public void A_fence_with_no_selection_puts_the_caret_on_its_empty_body_line()
    {
        var (doc, caret) = Apply("Text", 4, 0, "\n```js\n", "\n```\n");
        Assert.Equal("Text\r\r```js\r\r```\r", doc);
        Assert.Equal("Text\r\r```js\r|\r```\r", caret);
    }

    [Fact]
    public void A_fence_wraps_the_selected_lines_whole_and_selects_them()
    {
        var (doc, caret) = Apply("before\rlet a = 1;\rlet b = 2;\rafter", 9, 10, "\n```\n", "\n```\n");
        Assert.Equal("before\r\r```\rlet a = 1;\rlet b = 2;\r```\r\rafter", doc);
        var p = BlockInsertion.Plan("before\rlet a = 1;\rlet b = 2;\rafter", 9, 10, "\n```\n", "\n```\n");
        Assert.Equal("let a = 1;\rlet b = 2;", doc.Substring(p.CaretStart, p.CaretLength));
        Assert.Contains("```\r", caret);
    }

    [Fact]
    public void Crlf_documents_keep_crlf()
    {
        var (doc, _) = Apply("a\r\nb", 4, 0, "\n:::x\n:::\n");
        Assert.Equal("a\r\nb\r\n\r\n:::x\r\n:::\r\n", doc);
    }

    [Fact]
    public void Inserting_on_the_last_line_leaves_no_blank_line_at_the_end()
    {
        var (doc, _) = Apply("Para\r| a |\r", 6, 0, ":::x\r:::");
        Assert.Equal("Para\r| a |\r\r:::x\r:::\r", doc);
    }

    [Fact]
    public void Existing_blank_lines_after_the_caret_are_reused()
    {
        var (doc, _) = Apply("a\r\r\rb", 1, 0, ":::x\r:::");
        // Caret at the end of "a": the block takes the gap, with one blank line either side.
        Assert.Equal("a\r\r:::x\r:::\r\r\rb", doc);
    }
}
