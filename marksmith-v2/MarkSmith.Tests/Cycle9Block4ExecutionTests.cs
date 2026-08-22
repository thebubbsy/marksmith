using System;
using System.Linq;
using MarkSmith.Services;
using MarkSmith.Services.CodePlayground;
using MarkSmith.Services.Presentation;
using MarkSmith.Services.Security;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle9Block4ExecutionTests
{
    [Fact]
    public void BibTeXCitationExporter_ExtractsCitationsAndGeneratesBibTeX()
    {
        string md = """
            # Research Report
            
            This algorithm was proposed in [@knuth1984] and benchmarked by [@turing1950].
            
            [@knuth1984]: author="Donald Knuth" title="Literate Programming" year="1984" journal="The Computer Journal" doi="10.1093/comjnl/27.2.97"
            """;

        var citations = BibTeXCitationExporter.ExtractCitations(md);
        Assert.Equal(2, citations.Count);

        var knuth = citations.FirstOrDefault(c => c.Key == "knuth1984");
        Assert.NotNull(knuth);
        Assert.Equal("Donald Knuth", knuth.Author);
        Assert.Equal("Literate Programming", knuth.Title);

        string bibtex = BibTeXCitationExporter.ToBibTeX(citations);
        Assert.Contains("@article{knuth1984,", bibtex);
        Assert.Contains("author = {Donald Knuth}", bibtex);

        string csl = BibTeXCitationExporter.ToCslJson(citations);
        Assert.Contains("\"id\": \"knuth1984\"", csl);
    }

    [Fact]
    public void DocumentReadabilityService_ComputesScoresAccurately()
    {
        string simpleMd = """
            The cat sat on the mat. The dog saw the cat. The sun was hot.
            """;

        var simpleScore = DocumentReadabilityService.Analyze(simpleMd);
        Assert.True(simpleScore.FleschReadingEase > 80.0);
        Assert.True(simpleScore.FleschKincaidGradeLevel < 4.0);

        string complexMd = """
            The unprecedented architectural reconfiguration facilitates deterministic distributed consensus synchronization.
            Heterogeneous computational subsystems demonstrate extraordinary parallel computational performance.
            """;

        var complexScore = DocumentReadabilityService.Analyze(complexMd);
        Assert.True(complexScore.FleschReadingEase < 40.0);
        Assert.True(complexScore.ComplexWordsCount >= 4);
    }

    [Fact]
    public void MarkdownSlideDeckService_ParsesSlidesAndGeneratesHtml()
    {
        string slidesMd = """
            # Welcome to MarkSmith
            The ultimate markdown processor.
            ??? Welcome everyone to the presentation.
            ---
            <!-- bg: #1a1a2e -->
            # Key Features
            - Native OpenXML Export
            - SmartArt Engine
            --
            # Deep Dive
            Technical architecture.
            """;

        var deck = MarkdownSlideDeckService.Parse(slidesMd);
        Assert.Equal(3, deck.Slides.Count);
        Assert.Equal("Welcome to MarkSmith", deck.Slides[0].Title);
        Assert.Equal("Welcome everyone to the presentation.", deck.Slides[0].SpeakerNote);
        Assert.Equal("#1a1a2e", deck.Slides[1].BackgroundColor);
        Assert.True(deck.Slides[2].IsVertical);

        string html = MarkdownSlideDeckService.GenerateHtmlPresentation(deck);
        Assert.Contains("Welcome to MarkSmith", html);
        Assert.Contains("slide-viewport", html);
    }

    [Fact]
    public void ThemeContrastAuditorService_ComputesContrastAndCompliance()
    {
        // High contrast: Black on White
        var high = ThemeContrastAuditorService.Audit("#000000", "#FFFFFF");
        Assert.True(high.PassesAaNormalText);
        Assert.True(high.PassesAaaNormalText);
        Assert.True(high.ContrastRatio > 20.0);

        // Low contrast: Light grey on white
        var low = ThemeContrastAuditorService.Audit("#CCCCCC", "#FFFFFF");
        Assert.False(low.PassesAaNormalText);
        Assert.NotNull(low.SuggestedForegroundHex);
    }

    [Fact]
    public void MarkdownCodeReplService_ExtractsSnippetsAndWrapsContainers()
    {
        string md = """
            # Code Sample
            
            ```csharp title="CalculateSum"
            int sum = a + b;
            Console.WriteLine(sum);
            ```
            
            ```python
            print("Hello World")
            ```
            """;

        var snippets = MarkdownCodeReplService.ExtractSnippets(md);
        Assert.Equal(2, snippets.Count);
        Assert.Equal("csharp", snippets[0].Language);
        Assert.Equal("CalculateSum", snippets[0].Title);
        Assert.True(snippets[0].IsExecutable);

        string replHtml = MarkdownCodeReplService.RenderReplContainer(snippets[0]);
        Assert.Contains("class=\"ms-code-repl\"", replHtml);
        Assert.Contains("repl-run-btn", replHtml);
    }

    [Fact]
    public void DocumentPiiRedactorService_RedactsSensitiveData()
    {
        string secretMd = """
            Contact Tony at tony@example.com or call +1-800-555-0199.
            Server IP is 192.168.1.100 with AWS key AKIAIOSFODNN7EXAMPLE.
            Card: 4532-1234-5678-9012.
            """;

        var pseudonymResult = DocumentPiiRedactorService.Redact(secretMd, RedactionMode.PseudonymTokens);
        Assert.Equal(5, pseudonymResult.Redactions.Count);
        Assert.DoesNotContain("tony@example.com", pseudonymResult.SanitizedMarkdown);
        Assert.DoesNotContain("192.168.1.100", pseudonymResult.SanitizedMarkdown);
        Assert.Contains("[EMAIL_1]", pseudonymResult.SanitizedMarkdown);
        Assert.Contains("[SECRET_KEY_1]", pseudonymResult.SanitizedMarkdown);

        var maskResult = DocumentPiiRedactorService.Redact(secretMd, RedactionMode.BlackBarMask);
        Assert.Contains("████", maskResult.SanitizedMarkdown);
    }
}
