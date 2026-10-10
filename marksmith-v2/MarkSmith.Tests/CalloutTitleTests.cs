using System.Text.RegularExpressions;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Email;
using MarkSmith.Services.Presentation;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// Run #67: a callout's own title. `:::tip Pro tip` printed a "Tip" label over a bold "Pro tip"
/// that ran into the first sentence of the body; `:::danger` was labelled "Caution";
/// Obsidian's `> [!tip] Pro tip` leaked as the text "[!tip] Pro tip" in a plain quote; and
/// `> [!faq]` drew an unstyled box with no label at all. Now the title (or the kind's own name)
/// IS the label, in the preview, PDF, Word, email and slides alike.
/// </summary>
public class CalloutTitleTests
{
    private static string Render(string md) =>
        new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));

    private static string Title(string html) =>
        Regex.Match(html, "<p class=\"markdown-alert-title\">(?:<svg.*?</svg>)?(.*?)</p>", RegexOptions.Singleline).Groups[1].Value;

    [Theory]
    [InlineData(":::tip Pro tip\nBe careful here.\n:::\n")]
    [InlineData(":::tip[Pro tip]\nBe careful here.\n:::\n")]
    [InlineData(":::tip \"Pro tip\"\nBe careful here.\n:::\n")]
    [InlineData("!!! tip \"Pro tip\"\n    Be careful here.\n")]
    [InlineData("> [!tip] Pro tip\n> Be careful here.\n")]
    [InlineData("> [!TIP] Pro tip\n> Be careful here.\n")]
    public void Title_replaces_the_label(string md)
    {
        var html = Render(md);
        Assert.Contains("markdown-alert-tip", html);
        Assert.Equal("Pro tip", Title(html));
        Assert.Contains("<p>Be careful here.</p>", html);       // body is its own paragraph
        Assert.DoesNotContain("md-callout-title", html);
        Assert.DoesNotContain("[!", html);
    }

    [Theory]
    [InlineData(":::danger\nHot.\n:::\n", "caution", "Danger")]
    [InlineData(":::info\nFYI.\n:::\n", "note", "Info")]
    [InlineData("> [!faq]\n> Why?\n", "note", "FAQ")]
    [InlineData("> [!success]\n> Done.\n", "tip", "Success")]
    [InlineData("> [!custom]\n> Mine.\n", "note", "Custom")]
    [InlineData("> [!warning]\n> Careful.\n", "warning", "Warning")]
    [InlineData(":::note\nPlain.\n:::\n", "note", "Note")]
    [InlineData("> [!caution]\n> GitHub red.\n", "caution", "Caution")]
    [InlineData("> [!CAUTION]\n> GitHub red.\n", "caution", "Caution")]
    [InlineData(":::caution\nDocusaurus amber.\n:::\n", "warning", "Caution")]
    public void Kind_names_keep_their_own_label(string md, string kind, string label)
    {
        var html = Render(md);
        Assert.Contains($"markdown-alert-{kind}", html);
        Assert.Equal(label, Title(html));
    }

    [Fact]
    public void Title_markup_is_escaped()
    {
        var html = Render(":::note <b>Bold</b> & co\nX.\n:::\n");
        Assert.Equal("&lt;b&gt;Bold&lt;/b&gt; &amp; co", Title(html));
    }

    [Fact]
    public void Plain_github_alerts_are_untouched()
    {
        Assert.Equal("> [!NOTE]\n> Body.", AdmonitionNormalizer.Apply("> [!NOTE]\n> Body."));
        Assert.Equal("Note", Title(Render("> [!NOTE]\n> Body.\n")));
    }

    [Fact]
    public void Code_samples_keep_their_syntax()
    {
        var md = "```\n> [!tip] Pro tip\n:::danger\n```\n";
        Assert.Equal(md, AdmonitionNormalizer.Apply(md));
    }

    [Fact]
    public void Folded_callout_summary_is_the_title_or_kind_name()
    {
        Assert.Contains("<summary>Pro tip</summary>", AdmonitionNormalizer.Apply("> [!tip]- Pro tip\n> Body."));
        Assert.Contains("<summary>FAQ</summary>", AdmonitionNormalizer.Apply("> [!faq]+\n> Body."));
    }

    [Fact]
    public void Saved_markdown_keeps_a_bold_title_line()
    {
        var md = AdmonitionNormalizer.Apply(":::tip Pro tip\nBody.\n:::\n", markdownFile: true);
        Assert.Contains("> [!TIP]\n> **Pro tip**\n>\n> Body.", md);
        Assert.DoesNotContain("<span", md);
    }

    [Fact]
    public void Word_label_is_the_title()
    {
        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml(":::tip Pro tip\nBe careful here.\n:::\n\n:::danger\nHot.\n:::\n");
        Assert.Contains("Pro tip</w:t>", xml);
        Assert.Contains("Danger</w:t>", xml);
        Assert.DoesNotContain(">TIP<", xml);
        Assert.DoesNotContain("md-callout-title", xml);
    }

    [Fact]
    public void Folded_callout_is_a_printed_box_in_Word()
    {
        // Was a collapsed heading: Word prints and saves to PDF without a collapsed section's body.
        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml("> [!tip]- Folded tip\n> Hidden until opened.\n");
        Assert.Contains("Folded tip</w:t>", xml);
        Assert.Contains("Hidden until opened.", xml);
        Assert.Contains("<w:shd", xml);
        Assert.DoesNotContain("defaultCollapsed", xml);
    }

    [Fact]
    public void Email_label_is_the_title()
    {
        var doc = EmailComposer.Compose(new EmailComposeRequest { Markdown = ":::tip Pro tip\nBe careful here.\n:::\n" },
            new AppSettings(), new ThemeDefinition("Light", "#ffffff", "#222222", "#0b3d91", "#f5f5f5", "#dddddd", "#0b5cad", "#eeeeee", "#333333"));
        Assert.Contains(">Pro tip</p>", doc.HtmlBody);
        Assert.Contains("Pro tip:", doc.TextBody);
        Assert.DoesNotContain("md-callout-title", doc.HtmlBody);
    }

    [Theory]
    [InlineData(":::tip Pro tip\nBe careful here.\n:::\n")]
    [InlineData("> [!tip] Pro tip\n> Be careful here.\n")]
    public void Slide_label_is_the_title(string md)
    {
        var path = Path.Combine(Path.GetTempPath(), $"callout-{Guid.NewGuid():N}.pptx");
        try
        {
            new PptxExportService().ExportAsync("# Deck\n\n## Slide\n\n" + md, path, new AppSettings()).GetAwaiter().GetResult();
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var text = string.Concat(zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide"))
                .Select(e => new StreamReader(e.Open()).ReadToEnd()));
            var runs = Regex.Matches(text, "<a:t>([^<]*)</a:t>").Select(m => m.Groups[1].Value).ToList();
            Assert.Contains("Pro tip", runs);
            Assert.Contains("Be careful here.", text);
            Assert.DoesNotContain(":::", text);
            Assert.DoesNotContain("[!", text);
            Assert.DoesNotContain("md-callout-title", text);
        }
        finally { File.Delete(path); }
    }
}
