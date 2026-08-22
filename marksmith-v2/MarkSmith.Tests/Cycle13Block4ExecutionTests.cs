using System;
using System.Collections.Generic;
using MarkSmith.Services.Analytics;
using MarkSmith.Services.Audio;
using MarkSmith.Services.Diagrams;
using MarkSmith.Services.Education;
using MarkSmith.Services.Forms;
using MarkSmith.Services.Legal;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle13Block4ExecutionTests
{
    [Fact]
    public void VoiceOverScriptService_GeneratesSsmlAndCalculatesDuration()
    {
        string md = """
            # Episode 10: OpenXML Deep Dive
            [Speaker: Tony] Welcome to the podcast. Today we break down OpenXML structures.
            [Speaker: Alex] Glad to be here Tony. Let's start with document packaging.
            """;

        var script = VoiceOverScriptService.GenerateScript(md, wordsPerMinute: 150);
        Assert.Equal(3, script.Cues.Count);
        Assert.Equal("Narrator", script.Cues[0].Speaker);
        Assert.Equal("Tony", script.Cues[1].Speaker);
        Assert.Equal("Alex", script.Cues[2].Speaker);
        Assert.True(script.TotalEstimatedDurationSeconds > 3.0);

        Assert.Contains("<speak", script.SsmlXml);
        Assert.Contains("<voice name=\"Tony\">", script.SsmlXml);
        Assert.Contains("<voice name=\"Alex\">", script.SsmlXml);
    }

    [Fact]
    public void DocumentRevisionHeatmapService_CalculatesChurnAndRendersSvg()
    {
        var history = new List<(string Section, int Added, int Deleted, int Edits)>
        {
            ("Introduction", 5, 2, 1),
            ("Core Architecture", 40, 20, 5),
            ("Appendix", 2, 0, 1)
        };

        var stats = DocumentRevisionHeatmapService.AnalyzeSections(history);
        Assert.Equal(3, stats.Count);
        Assert.Equal("Stable", stats[0].StabilityRating);
        Assert.Equal("Active Iteration", stats[1].StabilityRating);

        string svg = DocumentRevisionHeatmapService.RenderHeatmapSvg(stats);
        Assert.Contains("<svg", svg);
        Assert.Contains("Core Architecture", svg);
        Assert.Contains("ms-revision-heatmap", svg);
    }

    [Fact]
    public void NetworkGraphRendererService_ParsesGraphAndRendersSvg()
    {
        string graphDef = """
            NodeA -> NodeB [10]
            NodeB <-> NodeC [5]
            NodeC -- NodeD
            """;

        var graph = NetworkGraphRendererService.Parse(graphDef);
        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(3, graph.Edges.Count);
        Assert.True(graph.Edges[0].IsDirected);
        Assert.True(graph.Edges[1].IsBidirectional);

        string svg = NetworkGraphRendererService.RenderSvg(graph);
        Assert.Contains("<svg", svg);
        Assert.Contains("NodeA", svg);
        Assert.Contains("marker-end=\"url(#net-arrow)\"", svg);
    }

    [Fact]
    public void MarkdownSurveyFormService_ParsesQuestionsAndRendersHtmlForm()
    {
        string surveyMd = """
            (? Your Full Name) [text: required]
            (? Contact Email) [email]
            (? Preferred Track) [choice: Developer | Designer | Executive]
            (? Satisfaction Level) [rating: 1..5]
            """;

        var form = MarkdownSurveyFormService.ParseSurvey(surveyMd, "Product Feedback");
        Assert.Equal("Product Feedback", form.FormTitle);
        Assert.Equal(4, form.Questions.Count);
        Assert.Equal(SurveyQuestionType.Text, form.Questions[0].QuestionType);
        Assert.True(form.Questions[0].IsRequired);
        Assert.Equal(3, form.Questions[2].Options.Count);

        string html = MarkdownSurveyFormService.RenderFormHtml(form);
        Assert.Contains("class=\"ms-survey-form\"", html);
        Assert.Contains("type=\"email\"", html);
        Assert.Contains("Developer", html);
    }

    [Fact]
    public void FlashcardDeckService_ExtractsCardsAndExportsAnkiTsv()
    {
        string md = """
            Q: What is OMML?
            A: Office Math Markup Language.
            
            :::flashcard category="OpenXML"
            What element wraps Word math equations?
            ---
            <m:oMathPara> and <m:oMath>
            :::
            """;

        var deck = FlashcardDeckService.ExtractDeck(md, "OpenXML Mastery");
        Assert.Equal(2, deck.Cards.Count);
        Assert.Equal("What is OMML?", deck.Cards[0].Front);
        Assert.Equal("OpenXML", deck.Cards[1].Category);

        string tsv = FlashcardDeckService.ExportToAnkiTsv(deck);
        Assert.Contains("What is OMML?\tOffice Math Markup Language.", tsv);
        Assert.Contains("OpenXML", tsv);

        string html = FlashcardDeckService.RenderDeckHtml(deck);
        Assert.Contains("ms-flashcard-deck", html);
    }

    [Fact]
    public void ContractClauseValidatorService_ValidatesClausesAndSignatures()
    {
        string contractMd = """
            # Master Services Agreement
            
            :::clause:confidentiality title="Confidentiality Obligation"
            Parties agree to hold all proprietary data confidential.
            :::
            
            :::clause:governing-law title="Governing Law"
            Governed under the laws of California.
            :::
            
            [Signatory: Alice Smith, Title: CEO, Date: 2026-08-16]
            [Signatory: Bob Jones, Title: CTO, Date: 2026-08-16]
            """;

        var result = ContractClauseValidatorService.ValidateContract(contractMd);
        Assert.Equal(2, result.DetectedClauses.Count);
        Assert.Equal(2, result.DetectedSignatories.Count);
        Assert.Contains("indemnity", result.MissingMandatoryClauses);
        Assert.Contains("termination", result.MissingMandatoryClauses);
        Assert.False(result.IsContractExecutionReady); // missing indemnity & termination
    }
}
