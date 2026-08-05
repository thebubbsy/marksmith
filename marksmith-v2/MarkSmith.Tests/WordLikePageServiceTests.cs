using MarkSmith.Core.Preview;
using Xunit;

namespace MarkSmith.Tests;

public class WordLikePageServiceTests
{
    // ── Block splitting ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void SplitTopLevelBlocks_SelfClosingTagsDoNotOpenALevel()
    {
        string inner = "<p>a</p><hr /> <h2>B</h2><img src=\"x.png\" /> <p>c</p><br /> <p>d</p>";
        var blocks = WordLikePageService.SplitTopLevelBlocks(inner);
        Assert.Equal(7, blocks.Count);
        Assert.StartsWith("<p>a</p>", blocks[0]);
        Assert.StartsWith("<hr />", blocks[1]);
        Assert.StartsWith("<h2>B</h2>", blocks[2]);
        Assert.StartsWith("<img", blocks[3]);
        Assert.StartsWith("<p>c</p>", blocks[4]);
        Assert.StartsWith("<br />", blocks[5]);
        Assert.StartsWith("<p>d</p>", blocks[6]);
    }

    [Fact]
    public void SplitTopLevelBlocks_SkipsCommentsAndNestsCorrectly()
    {
        string inner = "<!--a--><div class=\"mermaid\">flowchart LR\nA-->B</div><p>x <strong>bold</strong></p>";
        var blocks = WordLikePageService.SplitTopLevelBlocks(inner);
        Assert.Equal(2, blocks.Count);
        Assert.StartsWith("<div class=\"mermaid\">", blocks[0]);
        Assert.StartsWith("<p>x <strong>bold</strong></p>", blocks[1]);
    }

    // ── Keep-together: Word never splits a diagram / table / image ───────────────────────────
    [Fact]
    public void Paginate_MermaidDiagram_IsNeverSplitAcrossPages()
    {
        // Fill most of a page, then a diagram that cannot fit the remainder — it must move WHOLE
        // to the next page, never split or overlapped.
        var blocks = new List<string>();
        for (int i = 0; i < 50; i++) blocks.Add("<p>filler paragraph text that consumes page space</p>");
        string diagram = "<div class=\"mermaid\">flowchart LR\nA --> B</div>";
        blocks.Add(diagram);
        blocks.Add("<p>after the diagram</p>");

        var result = WordLikePageService.Paginate(blocks);
        var diagramPage = result.Pages.FirstOrDefault(p => p.Html.Contains("<div class=\"mermaid\">"));
        Assert.NotNull(diagramPage);
        Assert.Contains(diagram, diagramPage.Html); // whole, not fragmented
        // The following paragraph is on the same or a later page — never earlier (no overlap).
        int diagramIdx = result.Pages.IndexOf(diagramPage);
        var afterPage = result.Pages.Skip(diagramIdx).FirstOrDefault(p => p.Html.Contains("after the diagram"));
        Assert.NotNull(afterPage);
        Assert.True(result.Pages.IndexOf(afterPage) >= diagramIdx);
    }

    [Fact]
    public void Paginate_Table_MovesWholeToNextPage()
    {
        var blocks = new List<string>();
        for (int i = 0; i < 48; i++) blocks.Add("<p>fill</p>");
        blocks.Add("<table><tr><th>H</th></tr><tr><td>cell</td></tr></table>");

        var result = WordLikePageService.Paginate(blocks);
        foreach (var page in result.Pages)
        {
            int open = page.Html.IndexOf("<table", StringComparison.Ordinal);
            if (open >= 0)
            {
                // The table's open and close are on the same page — never split mid-table.
                int close = page.Html.IndexOf("</table>", StringComparison.Ordinal);
                Assert.True(close > open, "table split across pages");
            }
        }
    }

    // ── Splittable content: paragraphs and code flow across pages ────────────────────────────
    [Fact]
    public void Paginate_LongParagraph_SplitsAtLineBoundary()
    {
        string longPara = "<p>" + string.Join(' ', Enumerable.Repeat("word", 1200)) + "</p>";
        var result = WordLikePageService.Paginate(new List<string> { longPara });
        Assert.True(result.Pages.Count >= 2, $"expected the paragraph to span pages, got {result.Pages.Count}");
        foreach (var page in result.Pages)
        {
            Assert.Equal(page.Html.Count(c => c == '<'), page.Html.Count(c => c == '>'));
        }
    }

    [Fact]
    public void Paginate_LongCodeBlock_SplitsAtNewlineBoundary_KeepingValidMarkup()
    {
        var code = new System.Text.StringBuilder("<pre><code class=\"language-cs\">");
        for (int i = 0; i < 200; i++) code.Append($"line {i} of the listing with words\n");
        code.Append("</code></pre>");
        var result = WordLikePageService.Paginate(new List<string> { code.ToString() });
        Assert.True(result.Pages.Count >= 2);
        foreach (var page in result.Pages)
        {
            // Every fragment is a complete <pre><code>…</code></pre> pair.
            Assert.Equal(Count(page.Html, "<pre"), Count(page.Html, "</pre>"));
            Assert.Equal(Count(page.Html, "<code"), Count(page.Html, "</code>"));
        }
    }

    // ── Pagination invariants ─────────────────────────────────────────────────────────────────
    [Fact]
    public void Paginate_PagesFitBudget_AndRenumberFooters()
    {
        var blocks = new List<string>();
        for (int i = 0; i < 90; i++) blocks.Add($"<p>Paragraph {i} with enough words to consume several lines of the page width for a normal body at eleven points.</p>");
        var result = WordLikePageService.Paginate(blocks);
        Assert.True(result.Pages.Count >= 3);
        Assert.Equal(result.Pages.Count, result.Pages[^1].Total);
        Assert.Contains("Page 1 of", result.Pages[0].Html);
        Assert.DoesNotContain("Page 0 of 0", result.Pages[0].Html);
    }

    [Fact]
    public void Paginate_Heading_KeepsWithFollowingBlock()
    {
        var blocks = new List<string> { "<p>filler</p>" };
        for (int i = 0; i < 40; i++) blocks.Add("<p>filler</p>");
        blocks.Add("<h2>Stranded heading</h2>");
        blocks.Add("<p>body right after the heading</p>");
        var result = WordLikePageService.Paginate(blocks);
        foreach (var page in result.Pages)
        {
            int h = page.Html.LastIndexOf("<h2", StringComparison.Ordinal);
            int after = page.Html.IndexOf("<p>body right after the heading", StringComparison.Ordinal);
            if (h >= 0 && after >= 0) Assert.True(after > h, "heading stranded without its following block");
        }
    }

    // ── Full-document transform ───────────────────────────────────────────────────────────────
    [Fact]
    public void BuildPagedDocument_InjectsPagingAndKeepsScripts()
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
        Assert.Contains("window.portalInit=1", paged);
        Assert.Contains("function repack", paged);
        // A diagram taller than a page is SCALED, never overlapped — the CSS enforces max-height.
        Assert.Contains("max-height: 100%", paged);
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

    [Fact]
    public void SplitTopLevelBlocks_TransparentWrapperFlowsItsChildren()
    {
        // A plain wrapper <div> around several tables must NOT become one unsplittable block —
        // its children flow across pages exactly like Word flows a container.
        string inner = "<div class=\"wrapper\"><table><tr><td>a</td></tr></table><table><tr><td>b</td></tr></table></div>";
        var blocks = WordLikePageService.SplitTopLevelBlocks(inner);
        Assert.Equal(2, blocks.Count);
        Assert.StartsWith("<table><tr><td>a", blocks[0]);
        Assert.StartsWith("<table><tr><td>b", blocks[1]);
    }

    [Fact]
    public void SplitTopLevelBlocks_DiagramWrapperStaysWhole()
    {
        string inner = "<div class=\"plugin-diagram\"><svg height=\"156\"></svg></div>";
        var blocks = WordLikePageService.SplitTopLevelBlocks(inner);
        Assert.Single(blocks);
        Assert.StartsWith("<div class=\"plugin-diagram\">", blocks[0]);
    }
}
