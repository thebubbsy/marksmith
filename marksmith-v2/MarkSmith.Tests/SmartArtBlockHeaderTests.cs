using MarkSmith.Core.AST;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Preview;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// The preview used to recognise only `type="…"` on a :::smartart header, so `:::smartart process`
// rendered as a plain bullet list in the preview while the DOCX export drew a diagram.
public class SmartArtBlockHeaderTests
{
    [Theory]
    [InlineData(" type=\"cycle\"", "cycle")]
    [InlineData(" type=process", "process")]
    [InlineData(" process", "process")]
    [InlineData(" Cycle", "Cycle")]
    [InlineData(" pyramid1", "pyramid1")]
    [InlineData(" hierarchy extra words", "hierarchy")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" banana", null)]
    [InlineData(" id=abc", null)]
    public void Layout_reads_type_attribute_or_a_known_bare_word(string header, string? expected) =>
        Assert.Equal(expected, SmartArtBlockHeader.Layout(header));

    [Theory]
    [InlineData(":::smartart process")]
    [InlineData(":::smartart type=\"process\"")]
    [InlineData(":::smartart")]
    public void Preview_draws_a_diagram_for_every_header_form(string header)
    {
        var md = $"Intro\n\n{header}\n- Discover\n- Design\n- Build\n:::\n\nOutro";
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));

        Assert.Contains("smartart-svg", html);
        Assert.DoesNotContain("<li>Discover</li>", html);
    }

    [Fact]
    public void Bare_alias_picks_that_layout_in_the_preview()
    {
        var md = ":::smartart cycle\n- Plan\n- Do\n- Check\n- Act\n:::\n";
        var bare = new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));
        var typed = new MarkdownHtmlService().Render(md.Replace(":::smartart cycle", ":::smartart type=\"cycle\""),
            new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));

        Assert.Equal(typed, bare);
    }

    [Fact]
    public void Compound_word_breaks_at_its_own_hyphen()
    {
        var html = HtmlPreviewRenderer.RenderHtml(
            MarkdownAstParser.Parse("- Self-actualisation\n- Esteem\n- Belonging\n- Safety\n- Physiological"),
            "pyramid1", "Basic Pyramid");

        Assert.DoesNotContain("actu-", html);
        Assert.Contains("actualisation", html);
    }
}
