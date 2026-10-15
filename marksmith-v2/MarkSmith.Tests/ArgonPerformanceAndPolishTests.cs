using System;
using System.Linq;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Tests;

public class ArgonPerformanceAndPolishTests
{
    [Fact]
    public void Improvement1_MarkdownHtmlService_TripleColonFastPath_PreservesProseAndLiftsDirectives()
    {
        var svc = new MarkdownHtmlService();
        var settings = new AppSettings();
        var theme = new ThemeDefinition("Default", "#ffffff", "#1e293b", "#0f172a", "#f1f5f9", "#e2e8f0", "#2563eb", "#f8fafc", "#cbd5e1");

        // Fast-path: no ::: anywhere in document
        var plainHtml = svc.Render("# Hello World\n\nPlain paragraph without directives.", settings, theme);
        Assert.Contains("Hello World", plainHtml);
        Assert.Contains("Plain paragraph without directives.", plainHtml);

        var canvasHtml = svc.RenderCanvasOnly("# Hello World\n\nPlain paragraph.", settings, theme);
        Assert.NotNull(canvasHtml);
        Assert.Contains("Hello World", canvasHtml);

        // Directive path: :::watermark, :::dropcap, :::metrics, :::index with ^[index: "Search:Indexing"]
        var richMd = """
            :::watermark "DRAFT" color=FF0000 opacity=0.25
            :::

            :::dropcap 4
            Once upon a time in MarkSmith.
            :::

            Here is an indexed term ^[index: "Performance:Argon"].

            :::metrics cols=2
            - **10x** - Faster Pipeline
            - **0 B** - Span Allocations
            :::

            :::index 2
            :::
            """;
        var richHtml = svc.Render(richMd, settings, theme);
        Assert.Contains("mk-watermark-overlay", richHtml);
        Assert.Contains("DRAFT", richHtml);
        Assert.Contains("--dropcap-lines: 4", richHtml);
        Assert.Contains("metrics-grid", richHtml);
        Assert.Contains("10x", richHtml);
        Assert.Contains("ms-index-block", richHtml);
        Assert.Contains("Performance", richHtml);
        Assert.Contains("Argon", richHtml);
    }

    [Fact]
    public void Improvement2_DialectNormalizer_GeneratedRegexAndTriggerGuards_PreserveAllConstructs()
    {
        var input = """
            Plain line that skips all inline regex passes.
            Wiki link: [[Architecture|System Architecture]] and #roadmap tag (not #FF5733 hex).
            CriticMarkup: {++added++} {--removed--} {~~old~>new~~} {==highlighted==}{>>@Alice [2026-10-15]: Great<<}
            Reviewer note: ^[Bob (2026-10-15): "Check this"] and inline footnote ^[Footnote text].
            Form controls: [dropdown: Alpha | Beta] [date: 2026-10-15] [text: "Enter name"]
            - [x] Completed task
            - [ ] Pending task
            Inline code `[[NotAWikiLink]] {++not critic++}` stays literal.
            """;

        var normalized = DialectNormalizer.Apply(input);
        Assert.Contains("<span class=\"wikilink\">System Architecture</span>", normalized);
        Assert.Contains("<span class=\"md-tag\">#roadmap</span>", normalized);
        Assert.Contains("#FF5733", normalized);
        Assert.Contains("<ins>added</ins>", normalized);
        Assert.Contains("<del>removed</del>", normalized);
        Assert.Contains("<del>old</del><ins>new</ins>", normalized);
        Assert.Contains("<mark>highlighted</mark>", normalized);
        Assert.Contains("ms-inline-1", normalized);
        Assert.Contains("ms-form-dropdown", normalized);
        Assert.Contains("ms-form-date", normalized);
        Assert.Contains("ms-form-text", normalized);
        Assert.Contains("ms-form-checkbox", normalized);
        Assert.Contains("`[[NotAWikiLink]] {++not critic++}`", normalized);
    }

    [Fact]
    public void Improvement3_HtmlSanitizer_FastPathAndZeroAllocJavascriptUrlCheck()
    {
        // Fast-path: clean HTML without script/embed/on*/href/src returns untouched
        var clean = "<p>Hello <strong>world</strong> &amp; friends</p>";
        Assert.Same(clean, HtmlSanitizer.Apply(clean));

        // Obfuscated javascript:, vbscript:, and data:text/html URLs are neutralized
        var malicious = """
            <p><script>alert(1)</script><iframe src="evil"></iframe></p>
            <a href="  j&#97;va&#x09;script:alert(1)">Click</a>
            <a href="vbscript:msgbox(1)">VB</a>
            <a href="data:text/html,<script>alert(1)</script>">Data</a>
            <a href="https://example.com/safe" onclick="steal()">Safe</a>
            """;
        var sanitized = HtmlSanitizer.Apply(malicious);
        Assert.DoesNotContain("<script", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"https://example.com/safe\"", sanitized);
    }

    [Fact]
    public void Improvement4_DocumentStatsService_SpanScanner_HandlesTrailingNewlinesAndFences()
    {
        Assert.Equal(1, DocumentStatsService.Analyze("hello\n").Lines);
        Assert.Equal(2, DocumentStatsService.Analyze("a\n\n").Lines);
        Assert.Equal(2, DocumentStatsService.Analyze("a\r\nb\r\n").Lines);

        var md = "# Heading One\r\n\r\nSome prose with ![alt word](img.png) and [two words](https://example.com).\r\n\r\n| A | B |\r\n|---|---|\r\n| 1 | 2 |\r\n\r\n```mermaid\r\ngraph TD;\r\nA-->B;\r\n```\r\n";
        var stats = DocumentStatsService.Analyze(md);
        Assert.Equal(1, stats.Headings);
        Assert.Equal(1, stats.Tables);
        Assert.Equal(1, stats.Images);
        Assert.Equal(1, stats.Links);
        Assert.Equal(1, stats.CodeBlocks);
        Assert.Equal(1, stats.MermaidDiagrams);
    }

    [Fact]
    public void Improvement5_MarkdownLintService_SpanLineScanner_ReportsExactLineNumbers()
    {
        var md = "#NoSpace\r\nLine with trailing space \r\nLine with hard\ttab\r\n\r\n\r\n\r\n![](missing-alt.png)\r\n```csharp\r\n#NotFlaggedInCode\r\n";
        var issues = MarkdownLintService.Analyze(md);

        Assert.Contains(issues, i => i.Line == 1 && i.Message.Contains("Missing space after '#'"));
        Assert.Contains(issues, i => i.Line == 2 && i.Message.Contains("Trailing whitespace"));
        Assert.Contains(issues, i => i.Line == 3 && i.Message.Contains("Hard tab"));
        Assert.Contains(issues, i => i.Line == 6 && i.Message.Contains("3+ consecutive blank lines"));
        Assert.Contains(issues, i => i.Line == 7 && i.Message.Contains("Image missing alt text"));
        Assert.Contains(issues, i => i.Line == 10 && i.Message.Contains("Unclosed code fence"));
    }

    [Fact]
    public void Improvement6_LlmSourceService_FastPathRepairAndPrepareMarkdownMemoization()
    {
        var svc = new LlmSourceService();
        var plain = "Just a normal paragraph with no citations, code fences, or math.";
        var cls = svc.Classify(plain);
        var (cleaned, fixes) = svc.RepairArtifacts(plain, cls);
        Assert.Equal(plain, cleaned);
        Assert.Empty(fixes);

        var vm = new MainViewModel();
        var first = vm.PrepareMarkdown(plain);
        var second = vm.PrepareMarkdown(plain);
        Assert.Same(first, second);
    }

    [Fact]
    public void Improvement7_TableFormulaEvaluator_PipeGuardAndGeneratedRegex()
    {
        // Non-table markdown with '=' skips line splitting and returns the exact same string reference
        var proseWithEquals = "In math, E = mc^2 and x = 42.";
        Assert.Same(proseWithEquals, TableFormulaEvaluator.EvaluateTableMarkdown(proseWithEquals));

        var table = """
            | Item | Qty | Price | Total |
            | --- | --- | --- | --- |
            | A | 2 | 10 | =B2*C2 |
            | B | 3 | 20 | =B3*C3 |
            | Sum | =SUM(ABOVE) | =AVERAGE(C2:C3) | =SUM(ABOVE) |
            """;
        var evaluated = TableFormulaEvaluator.EvaluateTableMarkdown(table);
        Assert.Contains("| A | 2 | 10 | 20 |", evaluated);
        Assert.Contains("| B | 3 | 20 | 60 |", evaluated);
        Assert.Contains("| Sum | 5 | 15 | 80 |", evaluated);
    }

    [Fact]
    public void Improvement8_CommandSearch_SearchValuesWordBreaksAndDeferredHighlights()
    {
        var scoreExact = CommandSearch.Score("Export PDF", "Export", "Export PDF");
        var scoreAbbrev = CommandSearch.Score("Export PDF", "Export", "ep");
        var scoreMultiTerm = CommandSearch.Score("Export PDF (Ctrl+P)", "Export", "pdf export");

        Assert.Equal(1000, scoreExact);
        Assert.NotNull(scoreAbbrev);
        Assert.NotNull(scoreMultiTerm);

        var highlights = CommandSearch.Highlights("Export PDF (Ctrl+P)", "pdf");
        Assert.Single(highlights);
        Assert.Equal((7, 3), highlights[0]);
    }

    [Fact]
    public void Improvement9_TocExtractorService_FastGuardAndRefreshTocSequenceEqual()
    {
        // Fast guard when no #, =, or - is present
        var emptyToc = TocExtractorService.Extract("Plain paragraph without any heading characters.");
        Assert.Empty(emptyToc);

        var vm = new MainViewModel
        {
            UsePasteSource = true,
            PastedMarkdown = "# Section One\n\n## Section Two"
        };
        vm.RefreshToc();
        Assert.Equal(2, vm.TocEntries.Count);

        int collectionChanges = 0;
        vm.TocEntries.CollectionChanged += (_, _) => collectionChanges++;
        // Calling RefreshToc again when headings haven't changed should not reset TocEntries
        vm.RefreshToc();
        Assert.Equal(0, collectionChanges);
    }

    [Fact]
    public void Improvement10_DocumentStatsService_FormatCursorAndSelection_RichTelemetry()
    {
        Assert.Equal("Ln 5, Col 12", DocumentStatsService.FormatCursorAndSelection(5, 12, ReadOnlySpan<char>.Empty));
        Assert.Equal("Ln 1, Col 1  (1 word, 5 chars selected)", DocumentStatsService.FormatCursorAndSelection(1, 1, "Hello".AsSpan()));
        Assert.Equal("Ln 3, Col 4  (3 words, 17 chars selected)", DocumentStatsService.FormatCursorAndSelection(3, 4, "Three words here.".AsSpan()));
        Assert.Equal("Ln 10, Col 2  (5 words, 22 chars, 3 lines selected)", DocumentStatsService.FormatCursorAndSelection(10, 2, "Line one\r\nLine two\r\nGo".AsSpan()));
    }
}
