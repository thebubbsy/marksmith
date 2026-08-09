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
}
