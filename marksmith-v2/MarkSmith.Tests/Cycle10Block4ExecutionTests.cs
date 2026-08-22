using System;
using System.Linq;
using MarkSmith.Services;
using MarkSmith.Services.Diagrams;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle10Block4ExecutionTests
{
    [Fact]
    public void GlossaryLexiconService_ExtractsGlossaryAndInjectsAbbr()
    {
        string md = """
            # Web Standards
            The HTML specification is maintained by WHATWG.
            CSS provides styling for web pages.
            
            *[HTML]: HyperText Markup Language
            *[CSS]: Cascading Style Sheets
            """;

        var glossary = GlossaryLexiconService.ExtractGlossary(md);
        Assert.Equal(2, glossary.Count);
        Assert.Contains(glossary, g => g.Term == "HTML" && g.Definition == "HyperText Markup Language");
        Assert.Contains(glossary, g => g.Term == "CSS" && g.Definition == "Cascading Style Sheets");

        string html = "<p>The HTML specification is styled with CSS.</p>";
        string injected = GlossaryLexiconService.InjectAbbrTooltips(html, glossary);
        Assert.Contains("<abbr title=\"HyperText Markup Language\"", injected);
        Assert.Contains("<abbr title=\"Cascading Style Sheets\"", injected);

        string appendix = GlossaryLexiconService.GenerateGlossaryAppendixMarkdown(glossary);
        Assert.Contains("## Glossary & Abbreviations", appendix);
        Assert.Contains("| **HTML** |", appendix);
    }

    [Fact]
    public void PlantUmlDotConverterService_ParsesAndRendersSvg()
    {
        string dot = """
            digraph {
                Client -> Gateway [label="HTTPS"]
                Gateway -> Service [label="gRPC"]
            }
            """;

        var diagram = PlantUmlDotConverterService.Parse(dot);
        Assert.Equal(3, diagram.Nodes.Count);
        Assert.Equal(2, diagram.Edges.Count);

        string svg = PlantUmlDotConverterService.RenderSvg(diagram);
        Assert.Contains("<svg", svg);
        Assert.Contains("Gateway", svg);
        Assert.Contains("marker-end=\"url(#dot-arrow)\"", svg);
    }

    [Fact]
    public void TableSortingFilterService_SortsNumericAndCurrencyRows()
    {
        string tableMd = """
            | Product | Price | Qty |
            | :--- | :--- | :--- |
            | Widget C | $30.00 | 5 |
            | Widget A | $10.00 | 15 |
            | Widget B | $20.00 | 2 |
            """;

        var table = TableSortingFilterService.Parse(tableMd);
        Assert.Equal(3, table.Headers.Count);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(TableColumnDataType.Currency, table.ColumnTypes[1]);
        Assert.Equal(TableColumnDataType.Number, table.ColumnTypes[2]);

        // Sort ascending by Price (column 1)
        var sortedByPrice = TableSortingFilterService.SortRows(table, 1, ascending: true);
        Assert.Equal("Widget A", sortedByPrice[0][0]); // $10.00
        Assert.Equal("Widget B", sortedByPrice[1][0]); // $20.00
        Assert.Equal("Widget C", sortedByPrice[2][0]); // $30.00

        // Sort ascending by Qty (column 2)
        var sortedByQty = TableSortingFilterService.SortRows(table, 2, ascending: true);
        Assert.Equal("Widget B", sortedByQty[0][0]); // 2
        Assert.Equal("Widget C", sortedByQty[1][0]); // 5
        Assert.Equal("Widget A", sortedByQty[2][0]); // 15
    }

    [Fact]
    public void HeaderAutoNumberingService_ComputesHierarchyAndPrefixes()
    {
        string md = """
            # Introduction
            ## Background
            ### Prior Work
            ## Scope <!-- nonumber -->
            # Architecture
            ## System Components
            """;

        var headings = HeaderAutoNumberingService.ComputeHeadingNumbers(md);
        Assert.Equal(6, headings.Count);
        Assert.Equal("1.", headings[0].NumberPrefix);
        Assert.Equal("1.1.", headings[1].NumberPrefix);
        Assert.Equal("1.1.1.", headings[2].NumberPrefix);
        Assert.True(headings[3].IsSkipped); // Scope is skipped
        Assert.Equal("2.", headings[4].NumberPrefix);
        Assert.Equal("2.1.", headings[5].NumberPrefix);

        string numberedMd = HeaderAutoNumberingService.ApplyNumberingToMarkdown(md);
        Assert.Contains("# 1. Introduction", numberedMd);
        Assert.Contains("### 1.1.1. Prior Work", numberedMd);
        Assert.Contains("# 2. Architecture", numberedMd);
    }

    [Fact]
    public void MarkdownAssetBundleService_ExtractsImageAndLinkManifest()
    {
        string md = """
            # Manual
            ![Logo](images/logo.png)
            ![Diagram](diagrams/arch.svg)
            For details, see [Whitepaper](docs/whitepaper.pdf).
            Also see [External Website](https://example.com).
            """;

        var manifest = MarkdownAssetBundleService.ExtractManifest(md, "C:\\Docs");
        Assert.Equal(2, manifest.TotalImages);
        Assert.Equal(1, manifest.TotalLinks); // only local whitepaper.pdf, https link excluded
        Assert.Contains(manifest.Assets, a => a.RelativeUri == "images/logo.png");
    }

    [Fact]
    public void DocumentSemanticDiffService_IdentifiesAdditionsDeletionsAndModifications()
    {
        string oldDoc = """
            # Chapter 1
            
            This is the initial paragraph.
            
            ```csharp
            int x = 1;
            ```
            """;

        string newDoc = """
            # Chapter 1
            
            This is the updated paragraph with extra details.
            
            ```csharp
            int x = 1;
            ```
            
            > A newly added quote block.
            """;

        var diff = DocumentSemanticDiffService.Compare(oldDoc, newDoc);
        Assert.Equal(1, diff.ModificationsCount); // paragraph modified
        Assert.Equal(1, diff.AdditionsCount); // quote block added
        Assert.Equal(0, diff.DeletionsCount);
    }
}
