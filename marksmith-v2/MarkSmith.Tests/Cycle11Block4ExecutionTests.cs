using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Services;
using MarkSmith.Services.MathTranspiler;
using MarkSmith.Services.Media;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle11Block4ExecutionTests
{
    [Fact]
    public void MathMatrixOmmlTranspiler_ParsesMatrixAndEmitsOmml()
    {
        string latex = @"\begin{pmatrix} 1 & 0 \\ 0 & 1 \end{pmatrix}";
        var matrix = MathMatrixOmmlTranspiler.ParseMatrix(latex);

        Assert.NotNull(matrix);
        Assert.Equal("pmatrix", matrix.Environment);
        Assert.Equal(2, matrix.Rows.Count);
        Assert.Equal(2, matrix.Rows[0].Cells.Count);
        Assert.Equal("1", matrix.Rows[0].Cells[0].Text);

        string omml = MathMatrixOmmlTranspiler.TranspileToOmml(matrix);
        Assert.Contains("<m:d", omml);
        Assert.Contains("<m:begChr m:val=\"(\"/>", omml);
        Assert.Contains("<m:endChr m:val=\")\"/>", omml);
        Assert.Contains("<m:m>", omml);
        Assert.Contains("<m:mr>", omml);
        Assert.Contains("xml:space=\"preserve\">1</m:t>", omml);
    }

    [Fact]
    public void MediaTranscriptSyncService_ExtractsCuesAndRendersPlayer()
    {
        string md = """
            :::audio url="podcasts/ep1.mp3" title="Episode 1"
            [00:00:05] Welcome to the MarkSmith podcast.
            [00:00:15] Today we explore OpenXML architecture.
            :::
            """;

        var blocks = MediaTranscriptSyncService.ExtractMediaBlocks(md);
        Assert.Single(blocks);
        Assert.Equal("audio", blocks[0].MediaType);
        Assert.Equal("podcasts/ep1.mp3", blocks[0].MediaUrl);
        Assert.Equal("Episode 1", blocks[0].Title);
        Assert.Equal(2, blocks[0].Cues.Count);
        Assert.Equal(5.0, blocks[0].Cues[0].StartSeconds);
        Assert.Equal("Welcome to the MarkSmith podcast.", blocks[0].Cues[0].SpokenText);

        string html = MediaTranscriptSyncService.RenderSyncedPlayerHtml(blocks[0]);
        Assert.Contains("<audio controls", html);
        Assert.Contains("data-time=\"5\"", html);
        Assert.Contains("data-time=\"15\"", html);
    }

    [Fact]
    public void TableOfFiguresService_ExtractsFiguresAndTables()
    {
        string md = """
            # System Design
            ![Figure 1: High Level Architecture](diagrams/arch.png)
            
            *Figure 2: Data Flow Diagram*
            
            Table 1: Latency Benchmarks
            | Model | Time |
            | :--- | :--- |
            | Flash | 5ms |
            """;

        var manifest = TableOfFiguresService.ExtractManifest(md);
        Assert.Equal(2, manifest.Figures.Count);
        Assert.Equal(1, manifest.Figures[0].Number);
        Assert.Equal("High Level Architecture", manifest.Figures[0].Caption);
        Assert.Equal(2, manifest.Figures[1].Number);

        Assert.Single(manifest.Tables);
        Assert.Equal(1, manifest.Tables[0].Number);
        Assert.Equal("Latency Benchmarks", manifest.Tables[0].Caption);

        string lof = TableOfFiguresService.GenerateListOfFiguresMarkdown(manifest.Figures);
        Assert.Contains("## List of Figures", lof);
        Assert.Contains("**Figure 1**", lof);

        string lot = TableOfFiguresService.GenerateListOfTablesMarkdown(manifest.Tables);
        Assert.Contains("## List of Tables", lot);
        Assert.Contains("**Table 1**", lot);
    }

    [Fact]
    public void SqlMarkdownTransclusionService_ExecutesQueryAndFormatsMarkdownTable()
    {
        var dataset = new TableDataSet(
            new List<string> { "Name", "Role", "Salary" },
            new List<List<string>>
            {
                new() { "Alice", "Engineer", "120000" },
                new() { "Bob", "Designer", "95000" },
                new() { "Charlie", "Manager", "140000" }
            });

        string query = "SELECT Name, Salary FROM dataset WHERE Salary > 100000 ORDER BY Salary DESC LIMIT 2";
        var result = SqlMarkdownTransclusionService.ExecuteQuery(dataset, query);

        Assert.Equal(2, result.Columns.Count);
        Assert.Equal("Name", result.Columns[0]);
        Assert.Equal("Salary", result.Columns[1]);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Charlie", result.Rows[0][0]); // Highest salary (140000)
        Assert.Equal("Alice", result.Rows[1][0]);   // Second highest (120000)

        string mdTable = SqlMarkdownTransclusionService.ToMarkdownTable(result);
        Assert.Contains("| Name | Salary |", mdTable);
        Assert.Contains("| Charlie | 140000 |", mdTable);
    }

    [Fact]
    public void DocumentWordBudgetService_CalculatesProgressAndWarnings()
    {
        string md = """
            <!-- doc-budget: 50 words -->
            # Section 1 <!-- section-budget: 20 words -->
            This is a test section with some words to verify word counts.
            
            # Section 2 <!-- section-budget: 5 words -->
            This section has way too many words and should exceed the budget easily.
            """;

        var report = DocumentWordBudgetService.Analyze(md);
        Assert.Equal(50, report.OverallBudgetWords);
        Assert.Equal(2, report.Sections.Count);

        var s1 = report.Sections[0];
        Assert.Equal(20, s1.BudgetWords);
        Assert.False(s1.IsOverBudget);

        var s2 = report.Sections[1];
        Assert.Equal(5, s2.BudgetWords);
        Assert.True(s2.IsOverBudget);
        Assert.True(s2.ProgressPercentage > 100.0);
    }

    [Fact]
    public void DocumentAnchorIndexerService_IndexesAndValidatesAnchors()
    {
        string md = """
            # Getting Started {#sec:intro}
            Welcome to the guide.
            
            # Installation
            See [Introduction](#sec:intro) and [Setup](#installation).
            Also see [Broken Reference](#non-existent).
            """;

        var report = DocumentAnchorIndexerService.IndexAndValidate(md);
        Assert.Equal(2, report.Anchors.Count);
        Assert.Contains(report.Anchors, a => a.Slug == "sec:intro");
        Assert.Contains(report.Anchors, a => a.Slug == "installation");

        Assert.Equal(3, report.References.Count);
        Assert.Equal(2, report.ResolvedReferencesCount);
        Assert.Equal(1, report.BrokenReferencesCount);
    }
}
