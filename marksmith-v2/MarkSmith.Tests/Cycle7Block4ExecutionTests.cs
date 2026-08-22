using System;
using System.IO;
using Xunit;
using MarkSmith.Services;
using MarkSmith.Services.Audio;
using MarkSmith.Services.Translation;
using MarkSmith.Preview;

namespace MarkSmith.Tests
{
    public class Cycle7Block4ExecutionTests
    {
        [Fact]
        public void TableFormulaEvaluator_ComputesSumAndAverageCorrectly()
        {
            string table = @"| Item | Cost | Tax |
| --- | --- | --- |
| Widget A | 100 | 10 |
| Widget B | 200 | 20 |
| Total | =SUM(B2:B3) | =AVERAGE(C2:C3) |";

            string evaluated = TableFormulaEvaluator.EvaluateTableMarkdown(table);

            Assert.Contains("300", evaluated);
            Assert.Contains("15", evaluated);
        }

        [Fact]
        public void VoiceNoteIngestService_ParsesVttIntoChapters()
        {
            var service = new VoiceNoteIngestService();
            string vtt = @"WEBVTT

00:00:01.000 --> 00:00:04.000
Welcome to the architectural planning meeting.

00:00:05.000 --> 00:00:08.000
We need to design the microservice communication layer.

00:00:15.000 --> 00:00:18.000
Next topic is the database sharding strategy.";

            var result = service.IngestVtt(vtt, "Architecture Meeting");

            Assert.Equal("Architecture Meeting", result.Title);
            Assert.True(result.Chapters.Count >= 2);
            Assert.Contains("# 🎙️ Architecture Meeting", result.FormattedMarkdown);
        }

        [Fact]
        public void MathEquationInspector_InspectsLatexAndEmitsOmml()
        {
            var inspector = new MathEquationInspector();
            string latex = @"\frac{\alpha + \beta}{\sqrt{x^2 + y^2}}";

            var result = inspector.Inspect(latex, isDisplayMode: true);

            Assert.True(result.IsValid);
            Assert.Empty(result.SyntaxIssues);
            Assert.NotEmpty(result.Tokens);
            Assert.Contains("<m:oMath", result.OmmlXml);
        }

        [Fact]
        public void DocumentTranslationCoordinator_AlignsSectionsAndScoresCompleteness()
        {
            var coordinator = new DocumentTranslationCoordinator();
            string docEn = @"# Introduction
Welcome to the documentation.

## Features
Here are the core capabilities.";

            string docEs = @"# Introducción
Bienvenido a la documentación.

## Características
Aquí están las capacidades principales.";

            var report = coordinator.AlignTranslations(docEn, docEs, "en", "es");

            Assert.Equal(2, report.TotalSections);
            Assert.Equal(2, report.TranslatedSections);
            Assert.Equal(1.0, report.CompletenessScore);
        }

        [Fact]
        public void DocumentOutlineHeatmapService_AnalyzesDensityAndReadingTime()
        {
            var service = new DocumentOutlineHeatmapService();
            string doc = @"# Main Chapter
This is a comprehensive overview of the system architecture with lots of words and detailed paragraphs.

:::smartart
matrix
- A
- B
:::

## Subchapter
Quick note.";

            var summary = service.Analyze(doc);

            Assert.True(summary.TotalWords > 10);
            Assert.Equal(1, summary.TotalDiagrams);
            Assert.Equal(2, summary.Sections.Count);
            Assert.NotEmpty(summary.Sections[0].HeatmapColorHex);
        }
    }
}
