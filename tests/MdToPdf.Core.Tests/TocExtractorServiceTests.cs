using System.Linq;
using Markdig;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

/// <summary>Unit tests for the Task 17 document-outline extractor.</summary>
public sealed class TocExtractorServiceTests
{
    [Fact]
    public void Extract_ReturnsEmpty_ForNullOrWhitespace()
    {
        Assert.Empty(TocExtractorService.Extract(null));
        Assert.Empty(TocExtractorService.Extract(""));
        Assert.Empty(TocExtractorService.Extract("   \n\t  "));
    }

    [Fact]
    public void Extract_ReturnsEmpty_WhenNoHeadings()
    {
        var entries = TocExtractorService.Extract("Just a paragraph.\n\nAnother one.");
        Assert.Empty(entries);
    }

    [Fact]
    public void Extract_CapturesAllSixLevels_InDocumentOrder()
    {
        var md = "# One\n\n## Two\n\n### Three\n\n#### Four\n\n##### Five\n\n###### Six\n";
        var entries = TocExtractorService.Extract(md);
        Assert.Equal(6, entries.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, entries.Select(e => e.Level).ToArray());
        Assert.Equal(new[] { "One", "Two", "Three", "Four", "Five", "Six" }, entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void Extract_ProducesLowercaseHyphenatedAnchors()
    {
        var entries = TocExtractorService.Extract("# Hello World\n");
        var e = Assert.Single(entries);
        Assert.Equal("hello-world", e.Anchor);
    }

    [Fact]
    public void Extract_DisambiguatesDuplicateHeadings_WithNumericSuffix()
    {
        var md = "# Hello World\n\n## Hello World\n\n### Hello World\n";
        var entries = TocExtractorService.Extract(md);
        Assert.Equal(new[] { "hello-world", "hello-world-1", "hello-world-2" }, entries.Select(e => e.Anchor).ToArray());
    }

    [Fact]
    public void Extract_IgnoresHeadingsInsideFencedCodeBlocks()
    {
        var md = "# Real Heading\n\n```\n# Not a heading\n## Also not\n```\n\n## Another Real\n";
        var entries = TocExtractorService.Extract(md);
        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "Real Heading", "Another Real" }, entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void Extract_IgnoresHeadingsInsideTildeFences()
    {
        var md = "~~~\n# fenced out\n~~~\n\n# Visible\n";
        var entries = TocExtractorService.Extract(md);
        var e = Assert.Single(entries);
        Assert.Equal("Visible", e.Text);
    }

    [Fact]
    public void Extract_StripsInlineMarkup_FromHeadingText()
    {
        var md = "# Bold **and** `code` and [a link](https://x.test)\n";
        var e = Assert.Single(TocExtractorService.Extract(md));
        Assert.Equal("Bold and code and a link", e.Text);
    }

    [Fact]
    public void Extract_CollectsImageAltText_AsHeadingText()
    {
        // An image's alt text is the heading's visible fallback, so it counts as outline text.
        var md = "# ![alt](img.png)\n\n## Has Text\n";
        var entries = TocExtractorService.Extract(md);
        Assert.Equal(new[] { "alt", "Has Text" }, entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void Extract_AnchorMatchesRenderedHtmlId()
    {
        // The whole point of Task 17: the anchor must equal the id Markdig's HTML renderer emits,
        // so a flyout can scroll with getElementById(anchor). Verify against the actual renderer.
        var md = "# My Section Title\n";
        var entry = Assert.Single(TocExtractorService.Extract(md));
        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var html = Markdig.Markdown.ToHtml(md, pipeline);
        Assert.Contains($"id=\"{entry.Anchor}\"", html);
    }

    [Fact]
    public void Extract_PreservesDocumentOrder_AcrossNestedLevels()
    {
        var md = "# A\n\n### A.1\n\n## B\n\n### B.1\n";
        var entries = TocExtractorService.Extract(md);
        Assert.Equal(new[] { "A", "A.1", "B", "B.1" }, entries.Select(e => e.Text).ToArray());
        Assert.Equal(new[] { 1, 3, 2, 3 }, entries.Select(e => e.Level).ToArray());
    }
}
