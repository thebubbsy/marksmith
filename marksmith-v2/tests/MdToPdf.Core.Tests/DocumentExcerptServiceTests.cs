using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class DocumentExcerptServiceTests
{
    [Fact]
    public void GenerateExcerpt_TakesFirstTwoSentencesOfOpeningProse()
    {
        var md = "First sentence here. Second sentence follows. Third is dropped. Fourth too.\n";
        var excerpt = DocumentExcerptService.GenerateExcerpt(md);
        Assert.Equal("First sentence here. Second sentence follows.", excerpt);
    }

    [Fact]
    public void GenerateExcerpt_SkipsHeadingsCodeAndQuotes()
    {
        var md = """
            # Title Heading

            ```csharp
            var notProse = true;
            ```

            > A blockquote line.

            The real intro sentence. It continues here.
            """;
        var excerpt = DocumentExcerptService.GenerateExcerpt(md);
        Assert.Equal("The real intro sentence. It continues here.", excerpt);
    }

    [Fact]
    public void GenerateExcerpt_StripsFormattingAndMediaButKeepsLinkText()
    {
        var md = "See [the docs](https://example.com) for **bold** and `code` plus ![alt](img.png) media.\n";
        var excerpt = DocumentExcerptService.GenerateExcerpt(md, maxSentences: 1);
        Assert.Equal("See the docs for bold and code plus media.", excerpt);
    }

    [Fact]
    public void GenerateExcerpt_TruncatesToWordBoundaryWithEllipsis()
    {
        var md = "Supercalifragilistic expialidocious antidisestablishmentarianism flows.\n";
        var excerpt = DocumentExcerptService.GenerateExcerpt(md, maxSentences: 1, maxLength: 30);
        Assert.True(excerpt.Length <= 31); // 30 chars + ellipsis
        Assert.EndsWith("…", excerpt);
        Assert.DoesNotContain("flows", excerpt);
    }

    [Fact]
    public void GenerateExcerpt_EmptyOrWhitespaceInputYieldsEmpty()
    {
        Assert.Equal("", DocumentExcerptService.GenerateExcerpt(null));
        Assert.Equal("", DocumentExcerptService.GenerateExcerpt("   \n  "));
        Assert.Equal("", DocumentExcerptService.GenerateExcerpt("# Only a heading\n"));
    }
}
