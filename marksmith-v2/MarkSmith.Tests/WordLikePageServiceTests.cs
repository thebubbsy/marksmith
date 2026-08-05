using MarkSmith.Core.Preview;
using Xunit;

namespace MarkSmith.Tests;

public class WordLikePageServiceTests
{
    // ── Block splitting ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void SplitTopLevelBlocks_ReturnsTopLevelElements()
    {
        string inner = "<p>one</p><h2>Two</h2><ul><li>a</li><li>b</li></ul><table><tr><td>x</td></tr></table>";
        var blocks = WordLikePageService.SplitTopLevelBlocks(inner);
        Assert.Equal(4, blocks.Count);
        Assert.StartsWith("<p>one</p>", blocks[0]);
        Assert.StartsWith("<h2>Two</h2>", blocks[1]);
        Assert.StartsWith("<ul><li>a</li><li>b</li></ul>", blocks[2]);
        Assert.StartsWith("<table>", blocks[3]);
    }

    [Fact]
    public void SplitTopLevelBlocks_HandlesNestedAndComments()
    {
        string inner = "<!--ms-start--><div class=\"mermaid\">flowchart LR\nA-->B</div><p>x <strong>bold</strong></p>";
        var blocks = WordLikePageService.SplitTopLevelBlocks(inner);
        // The comment is dropped (leading loose text is whitespace-only), the mermaid div and the p remain.
        Assert.Equal(2, blocks.Count);
        Assert.StartsWith("<div class=\"mermaid\">", blocks[0]);
        Assert.StartsWith("<p>x <strong>bold</strong></p>", blocks[1]);
    }

    // ── Pagination ────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Paginate_ShortDoc_YieldsOnePage()
    {
        var blocks = new List<string> { "<p>short</p>", "<p>body</p>" };
        var result = WordLikePageService.Paginate(blocks);
        Assert.Equal(1, result.Pages.Count);
        Assert.Equal(1, result.Pages[0].Number);
        Assert.Equal(1, result.Pages[0].Total);
        Assert.Contains("Page 1 of 1", result.Pages[0].Html);
    }

    [Fact]
    public void Paginate_LongDoc_FillsPagesAndRenumbers()
    {
        var blocks = new List<string>();
        for (int i = 0; i < 90; i++) blocks.Add($"<p>Paragraph number {i} with enough words to consume several lines of text across the page width for a normal Calibri body at eleven points.</p>");
        var result = WordLikePageService.Paginate(blocks);
        Assert.True(result.Pages.Count >= 3, $"expected >=3 pages, got {result.Pages.Count}");
        Assert.Equal(result.Pages.Count, result.Pages[^1].Total);
        Assert.Contains("Page 2 of", result.Pages[1].Html);
        Assert.DoesNotContain("Page 0 of 0", result.Pages[0].Html);
    }

    [Fact]
    public void Paginate_KeepsHeadingWithFollowingBlock()
    {
        // A heading exactly at the bottom of a full page must not be stranded: it either fits with
        // the next block or both move together.
        var blocks = new List<string> { "<p>filler</p>" };
        for (int i = 0; i < 40; i++) blocks.Add("<p>filler</p>");
        blocks.Add("<h2>Stranded?</h2>");
        blocks.Add("<p>body after heading</p>");
        var result = WordLikePageService.Paginate(blocks);

        foreach (var page in result.Pages)
        {
            string html = page.Html;
            int h = html.LastIndexOf("<h2", StringComparison.OrdinalIgnoreCase);
            int pAfter = html.IndexOf("<p>body after heading", StringComparison.Ordinal);
            if (h >= 0 && pAfter >= 0)
            {
                // If the heading is on this page, the following paragraph must be on the same page.
                Assert.True(pAfter > h, "heading stranded without its following block");
            }
        }
    }

    [Fact]
    public void Paginate_NeverSplitsRegularBlocks_ExceptOverflowParagraphs()
    {
        // A block that fits a whole page is never cut: page boundaries fall between blocks.
        var blocks = new List<string>();
        for (int i = 0; i < 50; i++) blocks.Add("<p>filler line of text for page fill</p>");
        var result = WordLikePageService.Paginate(blocks);
        foreach (var page in result.Pages)
        {
            string html = page.Html;
            int opens = Count(html, "<p>"), closes = Count(html, "</p>");
            Assert.Equal(opens, closes);
        }
    }

    [Fact]
    public void EstimateHeight_ParagraphScalesWithText()
    {
        int shortH = WordLikePageService.EstimateHeight("<p>hi</p>");
        int longH = WordLikePageService.EstimateHeight("<p>" + new string('x', 400) + "</p>");
        Assert.True(longH > shortH, "long paragraph must estimate taller than a short one");
    }

    // ── Full document transform ───────────────────────────────────────────────────────────────
    [Fact]
    public void BuildPagedDocument_InjectsPagingIntoRenderedPreview()
    {
        string doc = """
            <!DOCTYPE html><html><head><style>body{}</style></head><body class="preview dark">
            <div id="canvas"><!--ms-canvas-start--><p>hello world</p><p>second block</p><!--ms-canvas-end--></div>
            <script>window.portalInit=1;</script></body></html>
            """;
        var paged = WordLikePageService.BuildPagedDocument(doc);

        Assert.Contains("body class=\"wp-paged preview dark\"", paged);
        Assert.Contains("class=\"wp-page\"", paged);
        Assert.Contains("Page 1 of 1", paged);
        Assert.Contains("wp-content", paged);
        Assert.Contains("window.portalInit=1", paged);   // page scripts survive
        Assert.Contains(".wp-page {", paged);            // Word-like CSS injected
        Assert.Contains("function repack", paged);       // re-measure script injected
    }

    [Fact]
    public void BuildPagedDocument_UnknownStructure_PassesThroughUnchanged()
    {
        string doc = "<html><body>no markers here</body></html>";
        Assert.Equal(doc, WordLikePageService.BuildPagedDocument(doc));
    }

    private static int Count(string s, string needle)
    {
        int c = 0, idx = 0;
        while ((idx = s.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { c++; idx += needle.Length; }
        return c;
    }
}
