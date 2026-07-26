using System;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

/// <summary>
/// Unit tests for DocumentStatsService — the reading-time / structure metrics shown in the editor
/// status bar. The service is pure (markdown string -> stats), so these are fully deterministic.
/// </summary>
public class DocumentStatsServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    public void EmptyOrWhitespace_YieldsEmptyStats(string? input)
    {
        var s = DocumentStatsService.Analyze(input);
        Assert.Equal(0, s.Words);
        Assert.Equal(0, s.Headings);
        Assert.Equal(0, s.CodeBlocks);
        Assert.Equal(TimeSpan.Zero, s.ReadingTime);
        Assert.Equal("0 min", s.ReadingTimeText);
    }

    [Fact]
    public void CountsPlainProseWords()
    {
        var s = DocumentStatsService.Analyze("The quick brown fox jumps over the lazy dog");
        Assert.Equal(9, s.Words);
    }

    [Fact]
    public void HeadingMarkersAndEmphasis_DoNotInflateWordCount()
    {
        // Old naive Split counted "##" and the "**" pairs as words; the real prose here is 3 words.
        var s = DocumentStatsService.Analyze("## Big **Bold** Title");
        Assert.Equal(3, s.Words);
        Assert.Equal(1, s.Headings);
    }

    [Fact]
    public void FencedCodeBlock_IsExcludedFromWords_AndCounted()
    {
        var md = "Intro line\n\n```csharp\nvar x = 1;\nConsole.WriteLine(x);\n```\n\nOutro line";
        var s = DocumentStatsService.Analyze(md);
        // Only "Intro line" (2) + "Outro line" (2) count; the code body is excluded.
        Assert.Equal(4, s.Words);
        Assert.Equal(1, s.CodeBlocks);
    }

    [Fact]
    public void MermaidFence_IsCountedAsDiagramAndCodeBlock()
    {
        var md = "# Diagram\n\n```mermaid\nflowchart TD\n  A --> B\n```\n";
        var s = DocumentStatsService.Analyze(md);
        Assert.Equal(1, s.MermaidDiagrams);
        Assert.Equal(1, s.CodeBlocks);
        Assert.Equal(1, s.Headings);
        // "flowchart", "A", "B" are inside the fence and must not be counted; only "Diagram".
        Assert.Equal(1, s.Words);
    }

    [Fact]
    public void GfmTable_IsCounted_AndCellTextCountedAsWords()
    {
        var md = "| Name | Age |\n|------|-----|\n| Alice | 30 |\n| Bob | 25 |";
        var s = DocumentStatsService.Analyze(md);
        Assert.Equal(1, s.Tables);
        // Header + rows: Name Age Alice 30 Bob 25 = 6 words (pipes/dashes excluded).
        Assert.Equal(6, s.Words);
    }

    [Fact]
    public void ImagesAndLinks_CountedSeparately_AndVisibleTextCounted()
    {
        var md = "See [the docs](https://example.com) and ![a diagram](diagram.png) here.";
        var s = DocumentStatsService.Analyze(md);
        Assert.Equal(1, s.Images);
        Assert.Equal(1, s.Links); // the image must not also count as a link
        // Visible words: See the docs and a diagram here = 7
        Assert.Equal(7, s.Words);
    }

    [Fact]
    public void InlineCode_IsNotCountedAsWords()
    {
        var s = DocumentStatsService.Analyze("Run `dotnet build` now");
        // "dotnet build" is inline code -> dropped; "Run" + "now" = 2.
        Assert.Equal(2, s.Words);
    }

    [Fact]
    public void ReadingTime_UsesWordsPerMinuteEstimate()
    {
        // 450 words at 225 wpm -> exactly 2 minutes.
        var words = string.Join(' ', System.Linq.Enumerable.Repeat("word", 450));
        var s = DocumentStatsService.Analyze(words);
        Assert.Equal(450, s.Words);
        Assert.Equal(2, (int)Math.Round(s.ReadingTime.TotalMinutes));
        Assert.Equal("2 min", s.ReadingTimeText);
    }

    [Fact]
    public void ShortDocument_ReadsInUnderAMinute()
    {
        var s = DocumentStatsService.Analyze("Just a few words here.");
        Assert.Equal("< 1 min", s.ReadingTimeText);
    }

    [Fact]
    public void SummaryText_FormatsWordsAndReadingTime()
    {
        var s = DocumentStatsService.Analyze("one two three");
        Assert.Equal("3 words · < 1 min read", s.SummaryText);
    }

    [Fact]
    public void DetailText_OnlyListsPresentElements()
    {
        var md = "# Title\n\nSome prose.\n\n```py\nx=1\n```";
        var detail = DocumentStatsService.Analyze(md).DetailText;
        Assert.Contains("words", detail);
        Assert.Contains("1 heading", detail);
        Assert.Contains("1 code block", detail);
        // No tables/images/links present, so they must not appear.
        Assert.DoesNotContain("table", detail);
        Assert.DoesNotContain("image", detail);
    }

    [Fact]
    public void Characters_ReflectRawInputLength()
    {
        var md = "# Hi";
        var s = DocumentStatsService.Analyze(md);
        Assert.Equal(md.Length, s.Characters);
    }

    [Fact]
    public void CharactersNoSpaces_ExcludesAllWhitespace()
    {
        // "ab cd\n\tef" -> 6 visible chars (a b c d e f), 3 whitespace (space, \n, \t).
        var s = DocumentStatsService.Analyze("ab cd\n\tef");
        Assert.Equal(9, s.Characters);
        Assert.Equal(6, s.CharactersNoSpaces);
    }

    [Fact]
    public void Lines_CountsPhysicalLines()
    {
        Assert.Equal(1, DocumentStatsService.Analyze("single line").Lines);
        Assert.Equal(3, DocumentStatsService.Analyze("a\nb\nc").Lines);
    }

    [Fact]
    public void Lines_SingleTrailingNewline_DoesNotAddPhantomLine()
    {
        // "hello\n" is one line in any editor, not two; but a real blank line still counts.
        Assert.Equal(1, DocumentStatsService.Analyze("hello\n").Lines);
        Assert.Equal(2, DocumentStatsService.Analyze("a\n\n").Lines);
    }

    [Fact]
    public void Lines_NormalizesCrlfLineEndings()
    {
        Assert.Equal(3, DocumentStatsService.Analyze("a\r\nb\r\nc").Lines);
    }

    [Fact]
    public void DetailText_SurfacesCharactersNoSpacesAndLines()
    {
        var detail = DocumentStatsService.Analyze("Hello world\nSecond line").DetailText;
        Assert.Contains("without spaces", detail);
        Assert.Contains("2 lines", detail);
    }
}
