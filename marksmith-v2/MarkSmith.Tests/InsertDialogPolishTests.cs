using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class InsertDialogPolishTests
{
    [Fact]
    public void Table_snippet_generates_correct_dimensions_and_header_row()
    {
        var snippetWithHeader = InsertSnippetBuilder.Table(rows: 3, cols: 2, includeHeaderRow: true);
        Assert.Contains("| Header 1 | Header 2 |", snippetWithHeader);
        Assert.Contains("| --- | --- |", snippetWithHeader);
        Assert.Contains("| Value 1 | Value 2 |", snippetWithHeader);

        var snippetNoHeader = InsertSnippetBuilder.Table(rows: 2, cols: 3, includeHeaderRow: false);
        Assert.DoesNotContain("Header", snippetNoHeader);
        Assert.Contains("| Value 1 | Value 2 | Value 3 |", snippetNoHeader);
    }

    [Fact]
    public void Chart_snippet_preserves_type_and_data_points()
    {
        var lines = new[] { "Apples,50", "Bananas,30", "Cherries,15" };
        var barSnippet = InsertSnippetBuilder.Chart("bar", lines);
        Assert.Contains(":::chart type=\"bar\"", barSnippet);
        Assert.Contains("Apples,50", barSnippet);
        Assert.Contains(":::", barSnippet);

        var pieSnippet = InsertSnippetBuilder.Chart("pie", lines);
        Assert.Contains(":::chart type=\"pie\"", pieSnippet);
    }

    [Fact]
    public void Embed_provider_detection_and_snippet_format()
    {
        var ytUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
        var detected = ContainerBlockParsers.DetectProvider(ytUrl);
        Assert.Equal("youtube", detected);

        var embedSnippet = InsertSnippetBuilder.Embed(detected!, ytUrl);
        Assert.Contains(":::embed provider=\"youtube\"", embedSnippet);
        Assert.Contains($"src=\"{ytUrl}\"", embedSnippet);
    }

    [Fact]
    public void References_derived_id_formats_author_and_year()
    {
        var id = "knuth1984";
        var author = "Donald Knuth";
        var title = "Literate Programming";
        var year = "1984";
        var snippet = InsertSnippetBuilder.References(id, author, title, year);
        Assert.Contains(":::references", snippet);
        Assert.Contains("@knuth1984", snippet);
        Assert.Contains("author: Donald Knuth", snippet);
        Assert.Contains("year: 1984", snippet);
    }

    [Fact]
    public void SmartArt_shift_lines_indents_and_outdents_properly()
    {
        var text = "Root\nChild 1\nChild 2";
        // Indent line 2 (offset at "Child 1")
        var child1Pos = text.IndexOf("Child 1", StringComparison.Ordinal);
        var indented = SmartArtInsert.ShiftLines(text, child1Pos, child1Pos + "Child 1".Length, +1);
        Assert.Equal("Root\n  Child 1\nChild 2", indented.Text);

        // Outdent line 2 back
        var outdented = SmartArtInsert.ShiftLines(indented.Text, indented.SelectionStart, indented.SelectionStart + indented.SelectionLength, -1);
        Assert.Equal("Root\nChild 1\nChild 2", outdented.Text);
    }

    [Fact]
    public void SmartArt_layouts_have_valid_aliases_and_descriptions()
    {
        foreach (var layout in SmartArtInsert.Layouts)
        {
            Assert.False(string.IsNullOrWhiteSpace(layout.Alias));
            Assert.False(string.IsNullOrWhiteSpace(layout.Name));
            Assert.False(string.IsNullOrWhiteSpace(layout.Hint));
            Assert.NotNull(SmartArtInsert.Find(layout.Alias));
        }
    }
}
