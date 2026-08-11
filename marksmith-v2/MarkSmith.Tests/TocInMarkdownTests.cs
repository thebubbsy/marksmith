using System.Text.RegularExpressions;
using Markdig;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>Pins the TOC-as-raw-markdown service: the slugger must produce EXACTLY the ids Markdig's
/// AutoIdentifiers generates (that's what the anchors resolve to), and the block insert/replace/
/// remove round-trip must be reliable.</summary>
public class TocInMarkdownTests
{
    [Fact]
    public void Slugs_MatchMarkdigAutoIdentifiers()
    {
        // Every heading style Markdig slugifies differently — and duplicates get suffixed.
        var md = """
        # Hello, World!
        ## Foo Bar & Baz
        ### Already-kebab
        # Hello, World!
        ## UNDER_SCORE test
        # 123 Numbers 456
        ### 50% done!
        """;
        var html = Markdown.ToHtml(md, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        var markdigIds = Regex.Matches(html, @"<h[1-6] id=""([^""]+)""")
            .Select(m => m.Groups[1].Value).ToList();

        var myIds = TocInMarkdownService.ExtractHeadings(md).Select(h => h.Slug).ToList();

        Assert.Equal(markdigIds, myIds);
    }

    [Fact]
    public void ExtractHeadings_SkipsFencedCodeAndExistingTocRegion()
    {
        var md = """
        # Real Heading

        ```
        # Not A Heading
        ```

        <!-- MARKSMITH-TOC:START -->
        ## Table of Contents

        - [Real Heading](#real-heading)
        <!-- MARKSMITH-TOC:END -->

        ## Another Real One
        """;
        var headings = TocInMarkdownService.ExtractHeadings(md);
        Assert.Equal(new[] { "Real Heading", "Another Real One" }, headings.Select(h => h.Text));
        Assert.Equal(new[] { "real-heading", "another-real-one" }, headings.Select(h => h.Slug));
        Assert.Equal(new[] { 1, 2 }, headings.Select(h => h.Level));
    }

    [Fact]
    public void InsertOrReplace_AddsBlockAtTop_ThenReplacesInPlace()
    {
        var md = "# First\n\n## Second\n";
        var headings = TocInMarkdownService.ExtractHeadings(md);
        var block = TocInMarkdownService.BuildBlock(headings);

        var withToc = TocInMarkdownService.InsertOrReplace(md, block);
        Assert.StartsWith(TocInMarkdownService.StartMarker, withToc);
        Assert.Contains("## Table of Contents", withToc);
        Assert.Contains("- [First](#first)", withToc);
        Assert.Contains("- [Second](#second)", withToc);
        Assert.EndsWith("## Second\n", withToc);

        // Re-running replaces the old block in place (no stacking of TOCs).
        var md2 = withToc + "# Third\n";
        var headings2 = TocInMarkdownService.ExtractHeadings(md2);
        var block2 = TocInMarkdownService.BuildBlock(headings2);
        var withToc2 = TocInMarkdownService.InsertOrReplace(md2, block2);
        Assert.Single(Regex.Matches(withToc2, Regex.Escape(TocInMarkdownService.StartMarker)));
        Assert.Contains("- [Third](#third)", withToc2);
        Assert.EndsWith("# Third\n", withToc2);
    }

    [Fact]
    public void Remove_StripsTheBlockAndKeepsContent()
    {
        var md = "# First\n\n## Second\n";
        var block = TocInMarkdownService.BuildBlock(TocInMarkdownService.ExtractHeadings(md));
        Assert.NotEqual("", block); // two headings -> a real block
        var withToc = TocInMarkdownService.InsertOrReplace(md, block);

        var stripped = TocInMarkdownService.Remove(withToc);
        Assert.DoesNotContain(TocInMarkdownService.StartMarker, stripped);
        Assert.DoesNotContain("Table of Contents", stripped);
        Assert.Equal("# First\n\n## Second\n", stripped);
    }

    [Fact]
    public void BuildBlock_Empty_WhenFewerThanTwoHeadings()
    {
        Assert.Equal("", TocInMarkdownService.BuildBlock(new List<(int, string, string)> { (1, "Only One", "only-one") }));
        Assert.Equal("", TocInMarkdownService.BuildBlock(new List<(int, string, string)>()));
    }

    [Fact]
    public void HasMaintainedToc_FenceSafe_And_IgnoresStrayMarker()
    {
        // A code block that merely mentions the markers is NOT a maintained TOC — including a
        // full ordered pair inside the fence (replacing it mid-fence would unbalance the fence).
        var fenceOnly = "```\n<!-- MARKSMITH-TOC:START -->\n<!-- MARKSMITH-TOC:END -->\n```\n";
        Assert.False(TocInMarkdownService.HasMaintainedToc(fenceOnly));
        Assert.Equal(fenceOnly, TocInMarkdownService.Remove(fenceOnly)); // untouched

        // A stray unpaired START marker is not a block either — the doc is left untouched.
        Assert.False(TocInMarkdownService.HasMaintainedToc("# Hi\n\n" + TocInMarkdownService.StartMarker));

        var real = TocInMarkdownService.InsertOrReplace("# Hi\n\n## There\n",
            TocInMarkdownService.BuildBlock(TocInMarkdownService.ExtractHeadings("# Hi\n\n## There\n")));
        Assert.True(TocInMarkdownService.HasMaintainedToc(real));
    }

    [Fact]
    public void InsertOrReplace_IsIdempotent_OnRepeatRuns()
    {
        var md = "# First\n\n## Second\n";
        var block = TocInMarkdownService.BuildBlock(TocInMarkdownService.ExtractHeadings(md));

        var once = TocInMarkdownService.InsertOrReplace(md, block);
        var twice = TocInMarkdownService.InsertOrReplace(once, block); // no doc change between runs
        Assert.Equal(once, twice);
        Assert.StartsWith(TocInMarkdownService.StartMarker, once);
    }

    [Fact]
    public void BuildBlock_EscapesLinkLabelMetacharacters()
    {
        var block = TocInMarkdownService.BuildBlock(new List<(int, string, string)>
        {
            (1, "C# (CSharp)", "c-csharp"),
            (2, "Bug [1]", "bug-1"),
        });
        Assert.Contains("- [C# \\(CSharp\\)](#c-csharp)", block);
        Assert.Contains("- [Bug \\[1\\]](#bug-1)", block);
    }

    [Fact]
    public void ExtractHeadings_AllowsIndentedAtx()
    {
        var md = "# Top\n\n   ## Indented\n";
        var headings = TocInMarkdownService.ExtractHeadings(md);
        Assert.Contains(headings, h => h.Text == "Indented" && h.Level == 2);
    }
}
