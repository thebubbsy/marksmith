using System;
using System.Collections.Generic;
using MarkSmith.Services.Academic;
using MarkSmith.Services.Data;
using MarkSmith.Services.Diagrams;
using MarkSmith.Services.Legal;
using MarkSmith.Services.Project;
using MarkSmith.Services.Typography;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle14Block4ExecutionTests
{
    [Fact]
    public void LatexPaperTranspilerService_TranspilesMetadataAndEquations()
    {
        string md = """
            ---
            title: High-Performance OpenXML Generation
            author: Tony Stark, Alex Turing
            institution: Research Labs
            abstract: We present an advanced OpenXML compilation engine.
            ---
            
            # Introduction
            OpenXML uses zipped XML parts [@iso29500].
            
            ## Architecture
            The energy equation is:
            $$E = mc^2$$
            """;

        string latex = LatexPaperTranspilerService.TranspileToLatex(md);
        Assert.Contains(@"\documentclass[conference]{IEEEtran}", latex);
        Assert.Contains(@"\title{High-Performance OpenXML Generation}", latex);
        Assert.Contains(@"\IEEEauthorblockN{Tony Stark}", latex);
        Assert.Contains(@"\section{Introduction}", latex);
        Assert.Contains(@"\cite{iso29500}", latex);
        Assert.Contains(@"\begin{equation}", latex);
        Assert.Contains(@"E = mc^2", latex);
    }

    [Fact]
    public void TableSparklineGeneratorService_CalculatesMetricsAndRendersSvg()
    {
        var points = new List<double> { 10, 15, 12, 25, 30 };
        var metrics = TableSparklineGeneratorService.CalculateMetrics(points);
        Assert.Equal(10, metrics.Min);
        Assert.Equal(30, metrics.Max);
        Assert.Equal(20, metrics.Delta);
        Assert.True(metrics.IsUpTrend);

        string svg = TableSparklineGeneratorService.GenerateSparklineSvg(points);
        Assert.Contains("<svg", svg);
        Assert.Contains("<polyline", svg);
        Assert.Contains("#3fb950", svg); // Green uptrend

        string tableMd = "| Quarter | Trend |\n| Q1..Q4 | [sparkline: 5, 10, 15, 20] |";
        string transformed = TableSparklineGeneratorService.TransformSparklines(tableMd);
        Assert.Contains("<svg width=\"60\"", transformed);
    }

    [Fact]
    public void MarkdownGanttTimelineService_ParsesScheduleAndRendersSvg()
    {
        string md = """
            [2026-09-01 -> 2026-09-15] Sprint Planning %100
            [2026-09-10 -> 2026-09-25] Core Implementation %50
            """;

        var timeline = MarkdownGanttTimelineService.ParseTimeline(md, "Q3 Roadmap");
        Assert.Equal("Q3 Roadmap", timeline.Title);
        Assert.Equal(2, timeline.Tasks.Count);
        Assert.Equal(100, timeline.Tasks[0].ProgressPercent);
        Assert.Equal(50, timeline.Tasks[1].ProgressPercent);

        string svg = MarkdownGanttTimelineService.RenderGanttSvg(timeline);
        Assert.Contains("<svg", svg);
        Assert.Contains("Sprint Planning", svg);
        Assert.Contains("100%", svg);
    }

    [Fact]
    public void EpigraphMarginaliaService_TransformsEpigraphsAndSidenotes()
    {
        string md = """
            :::epigraph author="Donald Knuth" source="The Art of Computer Programming"
            Premature optimization is the root of all evil.
            :::
            
            Text with a marginal note^[sidenote: This is a margin explanation].
            """;

        string html = EpigraphMarginaliaService.TransformTypography(md);
        Assert.Contains("class=\"ms-epigraph\"", html);
        Assert.Contains("Donald Knuth", html);
        Assert.Contains("class=\"ms-sidenote\"", html);
        Assert.Contains("This is a margin explanation", html);
    }

    [Fact]
    public void MarkdownMindmapService_ParsesOutlineAndRendersSvg()
    {
        string md = """
            - MarkSmith Engine
              - Document Parser
                - CommonMark
                - Extensions
              - Exporters
                - OpenXML
                - PDF
            """;

        var root = MarkdownMindmapService.ParseTree(md);
        Assert.NotNull(root);
        Assert.Equal("MarkSmith Engine", root.Text);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(2, root.Children[0].Children.Count);

        string svg = MarkdownMindmapService.RenderSvg(root);
        Assert.Contains("<svg", svg);
        Assert.Contains("class=\"ms-mindmap-svg\"", svg);
        Assert.Contains("class=\"mm-branch\"", svg);
    }

    [Fact]
    public void DependencyAttributionService_ParsesDependenciesAndGeneratesNotices()
    {
        string md = """
            :::dependencies title="Core Dependencies"
            - DocumentFormat.OpenXml v3.0.1 (MIT) | Microsoft Corporation
            - Markdig v0.37.0 (BSD-2-Clause) | Alexandre Mutel
            - SkiaSharp v2.88.8 (MIT) | Microsoft / Xamarin
            :::
            """;

        var report = DependencyAttributionService.GenerateAttributions(md);
        Assert.Equal(3, report.Packages.Count);
        Assert.Equal("DocumentFormat.OpenXml", report.Packages[0].Name);
        Assert.Equal("3.0.1", report.Packages[0].Version);
        Assert.Equal("MIT", report.Packages[0].SpdxLicense);

        Assert.Contains("## Third-Party Software Notices", report.GeneratedNoticeAppendix);
        Assert.Contains("DocumentFormat.OpenXml (v3.0.1)", report.GeneratedNoticeAppendix);
        Assert.Contains("Alexandre Mutel", report.GeneratedNoticeAppendix);
    }
}
