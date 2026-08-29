using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Column splitting for <c>:::parallel</c> rows. Only the <c>===</c> separator was implemented, so
/// the pipe-prefixed form used by the bundled bilingual-contract example collapsed both languages
/// into column one and left column two empty — in both pipelines.
/// </summary>
public class ParallelRowParserTests
{
    [Fact]
    public void Splits_Pipe_Prefixed_Lines_Into_Columns()
    {
        var row = "| English text here.\n| Deutscher Text hier.";
        var cols = ParallelRowParser.SplitColumns(row, 2);
        Assert.Equal("English text here.", cols[0]);
        Assert.Equal("Deutscher Text hier.", cols[1]);
    }

    [Fact]
    public void Splits_On_An_Explicit_Separator_Line()
    {
        var row = "Left side.\n===\nRight side.";
        var cols = ParallelRowParser.SplitColumns(row, 2);
        Assert.Equal("Left side.", cols[0]);
        Assert.Equal("Right side.", cols[1]);
    }

    [Fact]
    public void A_Column_Can_Span_Several_Lines_After_Its_Pipe()
    {
        var row = "| First line\ncontinued here\n| Second column";
        var cols = ParallelRowParser.SplitColumns(row, 2);
        Assert.Equal("First line\ncontinued here", cols[0]);
        Assert.Equal("Second column", cols[1]);
    }

    [Fact]
    public void Pads_Missing_Columns_Rather_Than_Overflowing()
    {
        var cols = ParallelRowParser.SplitColumns("| only one", 3);
        Assert.Equal(3, cols.Length);
        Assert.Equal("only one", cols[0]);
        Assert.Equal("", cols[1]);
        Assert.Equal("", cols[2]);
    }

    [Fact]
    public void Tolerates_Carriage_Returns_And_Blank_Lines()
    {
        var row = "\r\n| English.\r\n\r\n| Deutsch.\r\n";
        var cols = ParallelRowParser.SplitColumns(row, 2);
        Assert.Equal("English.", cols[0]);
        Assert.Equal("Deutsch.", cols[1]);
    }

    [Fact]
    public void Text_Without_Any_Separator_Is_A_Single_Column()
    {
        var cols = ParallelRowParser.SplitColumns("just prose", 2);
        Assert.Equal("just prose", cols[0]);
        Assert.Equal("", cols[1]);
    }
}
