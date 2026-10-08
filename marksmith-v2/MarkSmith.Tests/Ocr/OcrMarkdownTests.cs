using System.Collections.Generic;
using System.Linq;
using MarkSmith.Ocr;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests.Ocr;

/// <summary>How OCR'd lines become Markdown (any engine).</summary>
public class OcrMarkdownTests
{
    // A line of words starting at x, 30 px tall, at row y, with words 60 px apart.
    private static OcrLine Line(string text, float x, float y)
    {
        var words = text.Split(' ').Select((w, i) => new OcrWord(w, x + i * 60, y, 55, 30)).ToList();
        return new OcrLine(text, words, y, 30);
    }

    [Fact]
    public void A_wrapped_list_item_stays_one_item()
    {
        var page = new OcrPageResult(new List<OcrLine>
        {
            Line("• First item that wraps", 100, 100),
            Line("onto a second line", 140, 140),
            Line("• Next item", 100, 180),
        }, 1000, 1000);
        Assert.Equal("- First item that wraps onto a second line\n- Next item", OcrMarkdown.FromPage(page));
    }

    [Fact]
    public void A_right_hand_column_reads_as_paragraphs_not_one_line_each()
    {
        var lines = new List<OcrLine>();
        for (int i = 0; i < 4; i++) lines.Add(Line("left column words here and more", 50, 100 + i * 40));
        for (int i = 0; i < 4; i++) lines.Add(Line("right column words here and more", 520, 100 + i * 40));
        var md = OcrMarkdown.FromPage(new OcrPageResult(lines, 1000, 1000));
        Assert.Equal(2, md.Split("\n\n").Length);
    }
}
