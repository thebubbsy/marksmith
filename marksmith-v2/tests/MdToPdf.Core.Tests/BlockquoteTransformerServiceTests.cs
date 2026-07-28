using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class BlockquoteTransformerServiceTests
{
    [Fact]
    public void Transform_TurnsNoteCalloutIntoAlertContainer()
    {
        var md = """
            > [!NOTE]
            > This is a note.
            """;
        var html = BlockquoteTransformerService.Transform(md);

        Assert.Contains("<div class=\"alert alert-note\">", html);
        Assert.Contains("<p class=\"alert-title\">Note</p>", html);
        Assert.Contains("<div class=\"alert-body\">This is a note.</div>", html);
        Assert.Contains("</div>", html);
    }

    [Fact]
    public void Transform_SupportsAllFiveKinds()
    {
        Assert.Contains("alert-warning", BlockquoteTransformerService.Transform("> [!WARNING]\n> x"));
        Assert.Contains("alert-tip", BlockquoteTransformerService.Transform("> [!TIP]\n> x"));
        Assert.Contains("alert-important", BlockquoteTransformerService.Transform("> [!IMPORTANT]\n> x"));
        Assert.Contains("alert-caution", BlockquoteTransformerService.Transform("> [!CAUTION]\n> x"));
        Assert.Contains("alert-note", BlockquoteTransformerService.Transform("> [!NOTE]\n> x"));
    }

    [Fact]
    public void Transform_HtmlEscapesBodyContent()
    {
        var md = "> [!WARNING]\n> Use <b> & \"quotes\" safely.";
        var html = BlockquoteTransformerService.Transform(md);

        Assert.Contains("&lt;b&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("<b>", html);
    }

    [Fact]
    public void Transform_KeepsTrailingMarkerTextInBody()
    {
        var md = "> [!TIP] Pro tip right here";
        var html = BlockquoteTransformerService.Transform(md);

        Assert.Contains("alert-tip", html);
        Assert.Contains("Pro tip right here", html);
    }

    [Fact]
    public void Transform_LeavesOrdinaryBlockquoteUntouched()
    {
        var md = "> Just a genuine quotation\n> with two lines.";
        var html = BlockquoteTransformerService.Transform(md);

        Assert.Equal("> Just a genuine quotation\n> with two lines.", html);
        Assert.DoesNotContain("alert", html);
    }

    [Fact]
    public void Transform_LeavesUnrecognizedMarkerUntouched()
    {
        var md = "> [!FANCY]\n> Not a real GitHub kind.";
        var html = BlockquoteTransformerService.Transform(md);

        Assert.DoesNotContain("alert", html);
        Assert.Contains("[!FANCY]", html);
    }

    [Fact]
    public void IsCalloutKind_ClassifiesCorrectly()
    {
        Assert.True(BlockquoteTransformerService.IsCalloutKind("NOTE"));
        Assert.True(BlockquoteTransformerService.IsCalloutKind("warning")); // case-insensitive
        Assert.False(BlockquoteTransformerService.IsCalloutKind("FANCY"));
        Assert.False(BlockquoteTransformerService.IsCalloutKind(null));
    }

    [Fact]
    public void Transform_EmptyInputReturnsEmpty()
    {
        Assert.Equal("", BlockquoteTransformerService.Transform(""));
        Assert.Equal("", BlockquoteTransformerService.Transform("   \n  "));
        Assert.Equal("", BlockquoteTransformerService.Transform(null));
    }
}
