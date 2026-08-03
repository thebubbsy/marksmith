using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class ToggleTests
{
    private static string ExportToXml(string md, AppSettings? s = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-toggle-test-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(md, path, s ?? new AppSettings()).GetAwaiter().GetResult();
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml")!;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // =========================================================================
    // Tier 1: Core Feature Coverage
    // =========================================================================

    [Fact]
    public void Tier1_BracketedToggleSyntax_ParsesAndCreatesCollapsibleHeading()
    {
        var md = ":::toggle [System Configuration]\nContains server setup parameters.\n:::\n";
        var xml = ExportToXml(md);

        Assert.Contains("System Configuration", xml);
        Assert.Contains("Contains server setup parameters", xml);
        Assert.Contains("outlineLvl", xml);
        Assert.Contains("val=\"8\"", xml);
        Assert.Contains("collapsed", xml);
    }

    [Fact]
    public void Tier1_UnbracketedToggleSyntax_ParsesAndCreatesCollapsibleHeading()
    {
        var md = ":::toggle Network Ports\nPort 8080 is open for internal traffic.\n:::\n";
        var xml = ExportToXml(md);

        Assert.Contains("Network Ports", xml);
        Assert.Contains("Port 8080 is open", xml);
        Assert.Contains("val=\"8\"", xml);
    }

    [Fact]
    public void Tier1_HtmlDetailsSummarySyntax_ParsesAndCreatesCollapsibleHeading()
    {
        var md = "<details>\n<summary>Advanced Settings</summary>\nCustom registry keys.\n</details>\n";
        var xml = ExportToXml(md);

        Assert.Contains("Advanced Settings", xml);
        Assert.Contains("Custom registry keys", xml);
        Assert.Contains("val=\"8\"", xml);
    }

    // =========================================================================
    // Tier 2: Boundary & Corner Cases
    // =========================================================================

    [Fact]
    public void Tier2_EmptyTitle_FallsBackToDefaultTitle()
    {
        var md = ":::toggle\nImplicit title body.\n:::\n";
        var xml = ExportToXml(md);

        Assert.Contains("Toggle", xml);
        Assert.Contains("Implicit title body", xml);
    }

    [Fact]
    public void Tier2_EmptyBody_RendersHeaderWithoutCrashing()
    {
        var md = ":::toggle [Header Only]\n:::\n";
        var xml = ExportToXml(md);

        Assert.Contains("Header Only", xml);
        Assert.Contains("val=\"8\"", xml);
    }

    [Fact]
    public void Tier2_NestedToggles_RendersBothOuterAndInnerCollapsibleHeaders()
    {
        var md = @":::toggle [Outer Section]
Outer explanation text.

:::toggle [Inner Subsection]
Detailed inner info.
:::

Outer closing note.
:::
";
        var xml = ExportToXml(md);

        Assert.Contains("Outer Section", xml);
        Assert.Contains("Inner Subsection", xml);
        Assert.Contains("Detailed inner info", xml);
    }

    [Fact]
    public void Tier2_SpecialCharactersInTitle_EscapesSafelyInOpenXml()
    {
        var md = ":::toggle [API & <Endpoints> \"v1.0\"]\nEscaped parameters.\n:::\n";
        var xml = ExportToXml(md);

        Assert.Contains("API &amp; &lt;Endpoints&gt;", xml);
        Assert.Contains("v1.0", xml);
        Assert.Contains("Escaped parameters", xml);
    }

    [Fact]
    public void Tier2_MultipleSequentialToggles_RenderInSequence()
    {
        var md = @":::toggle [First Accordion]
Content 1
:::

:::toggle [Second Accordion]
Content 2
:::
";
        var xml = ExportToXml(md);

        Assert.Contains("First Accordion", xml);
        Assert.Contains("Second Accordion", xml);
        Assert.Contains("Content 1", xml);
        Assert.Contains("Content 2", xml);
    }

    [Fact]
    public void Tier2_ToggleContainingCodeBlockAndTable_RendersCodeAndTableInside()
    {
        var md = @":::toggle [Developer Diagnostics]
```json
{ ""status"": ""healthy"" }
```

| Service | Port |
|---|---|
| Gateway | 443 |
:::
";
        var xml = ExportToXml(md);

        Assert.Contains("Developer Diagnostics", xml);
        Assert.Contains("healthy", xml);
        Assert.Contains("Gateway", xml);
        Assert.Contains("<w:tbl>", xml);
    }

    // =========================================================================
    // Tier 3: Cross-Feature Combinations
    // =========================================================================

    [Fact]
    public void Tier3_ToggleWithCalloutBox_RendersAlertInsideToggle()
    {
        var md = @":::toggle [Security Policy]
> [!WARNING]
> Do not expose API keys in repository commits.
:::
";
        var xml = ExportToXml(md);

        Assert.Contains("Security Policy", xml);
        Assert.Contains("WARNING", xml);
        Assert.Contains("Do not expose API keys", xml);
    }

    [Fact]
    public void Tier3_ToggleWithInnerHeadings_RendersHeadingsInsideToggle()
    {
        var md = @":::toggle [Architecture Guide]
### Microservices Overview
All services communicate via gRPC.
:::
";
        var xml = ExportToXml(md);

        Assert.Contains("Architecture Guide", xml);
        Assert.Contains("Microservices Overview", xml);
    }

    [Fact]
    public void Tier3_ToggleWithListItems_RendersListInsideToggle()
    {
        var md = @":::toggle [Deployment Steps]
1. Build release binaries
2. Run automated test suite
3. Deploy to production
:::
";
        var xml = ExportToXml(md);

        Assert.Contains("Deployment Steps", xml);
        Assert.Contains("Build release binaries", xml);
        Assert.Contains("Deploy to production", xml);
    }

    // =========================================================================
    // Tier 4: Real-World Scenario & OpenXML DOM Assertions
    // =========================================================================

    [Fact]
    public void Tier4_RealWorldScenario_GeneratesSampleToggleDocxAndValidatesDom()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        var outputDir = Path.Combine(projectRoot, "test_outputs");
        Directory.CreateDirectory(outputDir);
        var sampleDocxPath = Path.Combine(outputDir, "sample_toggle.docx");

        var md = @"# Marksmith Native Collapsible Toggle Feature Demo

This document demonstrates native Word accordion toggle containers.

:::toggle [Database Connection Settings]
The primary database connection string is configured via environment variables.

| Property | Value |
|---|---|
| Host | db.internal.net |
| Port | 5432 |

> [!NOTE]
> Ensure SSL mode is set to Require in production environments.
:::

:::toggle [Troubleshooting Guide]
If connection timeouts occur:
1. Verify security group rules.
2. Check network latency.
```bash
ping db.internal.net
```
:::

<details>
<summary>Legacy HTML Details Section</summary>
This collapsible section was created using standard HTML details/summary syntax.
</details>
";

        new DocxExportService().ExportAsync(md, sampleDocxPath, new AppSettings()).GetAwaiter().GetResult();

        Assert.True(File.Exists(sampleDocxPath), $"Expected generated output file at {sampleDocxPath}");
        Assert.True(new FileInfo(sampleDocxPath).Length > 0, "Generated .docx should not be empty");

        using var wordDoc = WordprocessingDocument.Open(sampleDocxPath, false);
        var mainPart = wordDoc.MainDocumentPart;
        Assert.NotNull(mainPart);

        var body = mainPart.Document.Body;
        Assert.NotNull(body);

        var outline8Paragraphs = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
            .Where(p => p.ParagraphProperties?.OutlineLevel?.Val?.Value == 8)
            .ToList();

        Assert.True(outline8Paragraphs.Count >= 3, $"Expected at least 3 level-8 collapsible paragraphs, found {outline8Paragraphs.Count}");

        foreach (var p in outline8Paragraphs)
        {
            Assert.Contains("collapsed", p.OuterXml.ToLowerInvariant());

            var runs = p.Descendants<DocumentFormat.OpenXml.Wordprocessing.Run>().ToList();
            Assert.NotEmpty(runs);
            var firstRun = runs.First();
            Assert.NotNull(firstRun.RunProperties?.Bold);
        }

        var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
        var errors = validator.Validate(wordDoc)
            .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility &&
                        e.Node?.LocalName != "collapsed" &&
                        !(e.Description?.Contains("collapsed") ?? false))
            .ToList();

        if (errors.Count > 0)
        {
            var msg = string.Join("\n", errors.Select(e => $"[{e.Id}] {e.Description} AT NODE: {e.Node?.OuterXml}"));
            Assert.Fail($"Validation failed with {errors.Count} errors:\n{msg}");
        }
    }
}
