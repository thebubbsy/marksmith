using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Regression coverage for the preview shell cache (perf audit #18): the ~50 KB JS/CSS shell is
// built once per theme/settings fingerprint and concatenated with the per-render body, so the
// most important property is that a cached render is BYTE-IDENTICAL to a fresh one, and that
// different fingerprints produce different shells.
public class PreviewShellCacheTests
{
    private static readonly ThemeDefinition ThemeA = new(
        "A", "#ffffff", "#111111", "#222222", "#f4f4f4", "#d9d9d9", "#0078d4", "#e8f4fd", "#bfbfbf");

    private static readonly ThemeDefinition ThemeB = new(
        "B", "#000000", "#eeeeee", "#cccccc", "#1a1a1a", "#333333", "#ff8800", "#3a2a1a", "#555555");

    [Fact]
    public void Render_TwiceWithSameSettings_ProducesIdenticalHtml()
    {
        const string md = "# Title\n\nSome **bold** text with `code`.\n\n- item\n- item 2\n";
        var settings = new AppSettings();

        var html1 = new MarkdownHtmlService().Render(md, settings, ThemeA);
        var html2 = new MarkdownHtmlService().Render(md, settings, ThemeA); // cache hit path

        Assert.Equal(html1, html2);
    }

    [Fact]
    public void Render_DifferentThemes_ProduceDifferentShells()
    {
        const string md = "Hello world.\n";
        var settings = new AppSettings();

        var htmlA = new MarkdownHtmlService().Render(md, settings, ThemeA);
        var htmlB = new MarkdownHtmlService().Render(md, settings, ThemeB);

        Assert.NotEqual(htmlA, htmlB);
        // The theme body color must actually differ between the two shells (proves the cache key
        // discriminates themes rather than serving a stale shell).
        Assert.Contains("background: #ffffff", htmlA);
        Assert.Contains("background: #000000", htmlB);
    }

    [Fact]
    public void Render_InteractiveVsStatic_DifferAndBothWork()
    {
        const string md = "# T\n\nBody.\n";
        var settings = new AppSettings();

        var live = new MarkdownHtmlService().Render(md, settings, ThemeA, interactive: true);
        var staticHtml = new MarkdownHtmlService().Render(md, settings, ThemeA, interactive: false);

        Assert.NotEqual(live, staticHtml);
        Assert.Contains("marksmith-fit-width", live);   // interactive-only script
        Assert.DoesNotContain("marksmith-fit-width", staticHtml);
    }

    [Fact]
    public void Render_ContentWidthChange_ProducesDifferentShell()
    {
        const string md = "Hello.\n";
        var narrow = new AppSettings { ContentWidth = 700 };
        var wide = new AppSettings { ContentWidth = 900 };

        var htmlNarrow = new MarkdownHtmlService().Render(md, narrow, ThemeA);
        var htmlWide = new MarkdownHtmlService().Render(md, wide, ThemeA);

        Assert.NotEqual(htmlNarrow, htmlWide);
        Assert.Contains("width: 700px", htmlNarrow);
        Assert.Contains("width: 900px", htmlWide);
    }

    [Fact]
    public void Render_NoEmojiChange_ProducesDifferentShell()
    {
        const string md = "Hello 😀.\n";
        var withEmoji = new AppSettings { NoEmoji = false };
        var withoutEmoji = new AppSettings { NoEmoji = true };

        var htmlWith = new MarkdownHtmlService().Render(md, withEmoji, ThemeA);
        var htmlWithout = new MarkdownHtmlService().Render(md, withoutEmoji, ThemeA);

        Assert.NotEqual(htmlWith, htmlWithout);
        Assert.Contains("😀", htmlWith);
        Assert.DoesNotContain("😀", htmlWithout);
    }

    [Fact]
    public void Render_HasBalancedMarkupAndExportReadinessScript()
    {
        const string md = "# Heading\n\nBody with `code` and a [link](https://example.com).\n";
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), ThemeA);

        Assert.Equal(Count(html, "<html"), Count(html, "</html>"));
        Assert.Equal(Count(html, "<body"), Count(html, "</body>"));
        Assert.Contains("<div id=\"canvas\">", html);
        Assert.Contains("<!--ms-canvas-start-->", html);
        Assert.Contains("<!--ms-canvas-end-->", html);
        // The export-readiness contract script must be present AND syntactically complete
        // (regression: a pre-existing stray "});" broke the whole <script>, so the readiness
        // promise could never resolve).
        Assert.Contains("window.marksmithWaitForExportReady = function", html);
        Assert.Contains("new MutationObserver(check)", html);
        Assert.DoesNotContain("});\n                        setTimeout", html);
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
