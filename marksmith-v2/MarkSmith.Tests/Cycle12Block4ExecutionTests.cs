using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Services;
using MarkSmith.Services.Chemistry;
using MarkSmith.Services.Citations;
using MarkSmith.Services.Data;
using MarkSmith.Services.Kanban;
using MarkSmith.Services.Themes;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle12Block4ExecutionTests
{
    [Fact]
    public void CitationBacklinkService_ExtractsBacklinksAndFormatsHtml()
    {
        string md = """
            # Introduction
            According to [@turing1950], machines can compute.
            
            # Architecture
            The system builds upon [@turing1950] and [@knuth1984].
            """;

        var map = CitationBacklinkService.ExtractCitationBacklinks(md);
        Assert.Equal(2, map.Count);
        Assert.Equal(2, map["turing1950"].Count);
        Assert.Single(map["knuth1984"]);

        string html = CitationBacklinkService.FormatBacklinkHtml("turing1950", map);
        Assert.Contains("ms-citation-backlinks", html);
        Assert.Contains("Introduction", html);
        Assert.Contains("Architecture", html);
    }

    [Fact]
    public void HighContrastThemeGenerator_GeneratesWcagCompliantCss()
    {
        string darkCss = HighContrastThemeGenerator.GenerateCss(HighContrastMode.OledDark);
        Assert.Contains("#000000", darkCss);
        Assert.Contains("#ffffff", darkCss);
        Assert.Contains("border: 2px solid", darkCss);

        string lightCss = HighContrastThemeGenerator.GenerateCss(HighContrastMode.PureLight);
        Assert.Contains("#ffffff", lightCss);
        Assert.Contains("#000000", lightCss);
        Assert.Contains("text-decoration: underline", lightCss);
    }

    [Fact]
    public void ChemicalFormulaRendererService_FormatsSubscriptsAndReactions()
    {
        string md = @"Reaction: \ce{2H2 + O2 -> 2H2O} and acid \ce{H2SO4 + 2NaOH -> Na2SO4 + 2H2O}";
        string html = ChemicalFormulaRendererService.RenderChemicalFormulas(md);

        Assert.Contains("ms-chem-formula", html);
        Assert.Contains("H<sub>2</sub>", html);
        Assert.Contains("O<sub>2</sub>", html);
        Assert.Contains("&#8594;", html); // Right arrow
    }

    [Fact]
    public void MarkdownPivotTableService_ComputesMultidimensionalAggregations()
    {
        var headers = new List<string> { "Region", "Quarter", "Revenue" };
        var rows = new List<List<string>>
        {
            new() { "North", "Q1", "100" },
            new() { "North", "Q2", "150" },
            new() { "South", "Q1", "80" },
            new() { "South", "Q2", "120" }
        };

        var pivot = MarkdownPivotTableService.Pivot(headers, rows, 0, 1, 2, PivotAggregateType.Sum);
        Assert.Equal(4, pivot.ColumnHeaders.Count); // Region, Q1, Q2, Total
        Assert.Equal(3, pivot.Rows.Count); // North, South, Grand Total

        Assert.Equal("North", pivot.Rows[0][0]);
        Assert.Equal("100.0", pivot.Rows[0][1]); // North Q1
        Assert.Equal("150.0", pivot.Rows[0][2]); // North Q2
        Assert.Equal("250.0", pivot.Rows[0][3]); // North Total

        Assert.Contains("**Grand Total**", pivot.Rows[2][0]);
        Assert.Contains("**450.0**", pivot.Rows[2][3]); // Grand Total
    }

    [Fact]
    public void EisenhowerMatrixService_CategorizesQuadrantsAndRendersSvg()
    {
        string md = """
            - [ ] Fix production outage #urgent #important
            - [ ] Plan Q4 roadmap #important
            - [ ] Respond to non-critical survey #urgent #not-important
            - [x] Browse social media #not-urgent #not-important
            """;

        var matrix = EisenhowerMatrixService.ParseMatrix(md);
        Assert.Equal(4, matrix.Tasks.Count);
        Assert.Equal(1, matrix.Q1Count);
        Assert.Equal(1, matrix.Q2Count);
        Assert.Equal(1, matrix.Q3Count);
        Assert.Equal(1, matrix.Q4Count);

        string svg = EisenhowerMatrixService.RenderMatrixSvg(matrix);
        Assert.Contains("<svg", svg);
        Assert.Contains("Q1: DO FIRST", svg);
        Assert.Contains("Q2: SCHEDULE", svg);
        Assert.Contains("Fix production outage", svg);
    }

    [Fact]
    public void InteractiveAccordionService_ExtractsAndTransformsAccordions()
    {
        string md = """
            :::details Technical Specs
            Here are the details about the system.
            :::
            
            <details open>
              <summary>FAQ</summary>
              Common questions.
            </details>
            """;

        var accordions = InteractiveAccordionService.ExtractAccordions(md);
        Assert.Equal(2, accordions.Count);
        Assert.Equal("Technical Specs", accordions[0].Title);
        Assert.Equal("FAQ", accordions[1].Title);
        Assert.True(accordions[1].IsDefaultOpen);

        string transformed = InteractiveAccordionService.TransformToHtmlAccordions(md);
        Assert.Contains("<details class=\"ms-accordion\"", transformed);
        Assert.Contains("<summary class=\"ms-accordion-header\">Technical Specs</summary>", transformed);
    }
}
