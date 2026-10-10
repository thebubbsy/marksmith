using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// Run #67: inline features through the preview/PDF and Word. A footnote definition straight after
/// a definition list printed raw ("[^1]") in every format: Markdig took it for the next term,
/// then a link reference definition.
/// </summary>
public class InlineFeatureSweepTests
{
    private const string DlThenFootnote = "Footnote here.[^1]\n\nTerm\n: Its definition.\n\n[^1]: The footnote text.\n";

    private static string Render(string md) =>
        new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));

    [Fact]
    public void Footnote_after_a_definition_list_links_in_the_preview()
    {
        var html = Render(DlThenFootnote);
        Assert.Contains("class=\"footnote-ref\"", html);
        Assert.Contains("<dt>Term</dt>", html);
        Assert.DoesNotContain("[^1]", html);
    }

    [Fact]
    public void Footnote_after_a_definition_list_is_a_Word_footnote()
    {
        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml(DlThenFootnote);
        Assert.Contains("footnoteReference", xml);
        Assert.DoesNotContain("[^1]", xml);
    }

    // Run #68: Word, email, slides, Google Docs and the TOC have no case for Markdig's
    // AbbreviationInline and printed "the HTML spec" as "the  spec".
    private const string Abbreviated = "# The HTML guide\n\nRead the HTML spec from the W3C.\n\n*[HTML]: HyperText Markup Language\n*[W3C]: World Wide Web Consortium\n";

    [Fact]
    public void Abbreviations_keep_their_word_in_Word()
    {
        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml(Abbreviated);
        var text = string.Concat(System.Text.RegularExpressions.Regex.Matches(xml, "<w:t[^>]*>([^<]*)</w:t>").Select(m => m.Groups[1].Value));
        Assert.Contains("Read the HTML spec from the W3C.", text);
        Assert.Contains("The HTML guide", text);
    }

    [Fact]
    public void Abbreviations_keep_their_word_in_email()
    {
        var doc = MarkSmith.Services.Email.EmailComposer.Compose(new MarkSmith.Services.Email.EmailComposeRequest { Markdown = Abbreviated },
            new AppSettings(), new ThemeDefinition("Light", "#ffffff", "#222222", "#0b3d91", "#f5f5f5", "#dddddd", "#0b5cad", "#eeeeee", "#333333"));
        Assert.Contains("Read the HTML spec from the W3C.", doc.TextBody);
        Assert.Contains("HTML spec from the W3C.", doc.HtmlBody);
    }

    [Fact]
    public void Abbreviations_keep_their_tooltip_in_the_preview()
    {
        var html = Render(Abbreviated);
        Assert.Contains("<abbr title=\"HyperText Markup Language\">HTML</abbr>", html);
    }

    [Fact]
    public void Inline_footnote_becomes_a_real_footnote()
    {
        const string md = "Main text.^[An inline note with a [link](https://example.com).] More text.\n\nTerm\n: Definition.\n";
        var html = Render(md);
        Assert.Contains("class=\"footnote-ref\"", html);
        Assert.Contains("An inline note with a <a href=\"https://example.com\"", html);
        Assert.DoesNotContain("^[", html);

        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml(md);
        Assert.Contains("footnoteReference", xml);
        Assert.DoesNotContain("^[", xml);
    }

    [Fact]
    public void Slides_read_the_same_dialects_as_every_other_format()
    {
        const string md = "# Deck\n\n## Slide\n\n- Track {++in++} {--out--} and {~~old~>new~~}\n- Note.^[Inline note.] [[Wiki Page]]\n- Seen {==this==}{>>Reviewer: check<<}\n";
        var path = Path.Combine(Path.GetTempPath(), $"dialects-{Guid.NewGuid():N}.pptx");
        try
        {
            new PptxExportService().ExportAsync(md, path, new AppSettings()).GetAwaiter().GetResult();
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var text = string.Concat(zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide"))
                .Select(e => new StreamReader(e.Open()).ReadToEnd()));
            foreach (var raw in new[] { "{++", "{--", "~>", "^[", "[[", "{==", "{&gt;&gt;", "ms-comment" })
                Assert.DoesNotContain(raw, text);
            Assert.Contains("Wiki Page", text);
            Assert.Contains("Inline note.", text);
            Assert.Contains("strike=\"sngStrike\"", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Email_keeps_critic_highlights_and_separates_substitutions()
    {
        var doc = MarkSmith.Services.Email.EmailComposer.Compose(new MarkSmith.Services.Email.EmailComposeRequest { Markdown = "A {==critic==} mark and {~~old~>new~~}.\n" },
            new AppSettings(), new ThemeDefinition("Light", "#ffffff", "#222222", "#0b3d91", "#f5f5f5", "#dddddd", "#0b5cad", "#eeeeee", "#333333"));
        Assert.Contains("<span style=\"background-color:#fff3a3;\">critic</span>", doc.HtmlBody);
        Assert.Contains("old</del> <ins", doc.HtmlBody);
    }

    [Fact]
    public void Epub_draws_emoji_shortcodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"emoji-{Guid.NewGuid():N}.epub");
        try
        {
            new EpubExportService().ExportAsync("# Book\n\nLaunch :rocket: today.\n", path, new AppSettings()).GetAwaiter().GetResult();
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var text = string.Concat(zip.Entries.Where(e => e.FullName.EndsWith(".xhtml")).Select(e => new StreamReader(e.Open()).ReadToEnd()));
            Assert.Contains("Launch 🚀 today.", text);
            Assert.DoesNotContain(":rocket:", text);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("Code `x^[1]` stays.")]
    [InlineData("```\nnote^[kept]\n```")]
    [InlineData("^[Reviewer: \"Check this\"] stays a comment.")]
    public void Inline_footnote_leaves_code_and_comments_alone(string md)
    {
        Assert.DoesNotContain("ms-inline-", DialectNormalizer.Apply(md));
    }
}
