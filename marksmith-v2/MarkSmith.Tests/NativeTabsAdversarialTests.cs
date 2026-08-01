using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MdToPdf.Core.AdvancedFeatures;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;
using W15 = DocumentFormat.OpenXml.Office2013.Word;

namespace MdToPdf.Core.Tests;

public class NativeTabsAdversarialTests
{
    private static string GetTestOutputPath(string fileName)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        var outputDir = Path.Combine(repoRoot, "test_outputs");
        if (!Directory.Exists(outputDir))
        {
            outputDir = @"C:\Users\Tony\.gemini\antigravity\scratch\marksmith\test_outputs";
        }
        Directory.CreateDirectory(outputDir);
        return Path.GetFullPath(Path.Combine(outputDir, $"{Guid.NewGuid():N}_{fileName}"));
    }

    private static void ValidateOpenXmlSchema(string docxPath)
    {
        using var doc = WordprocessingDocument.Open(docxPath, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
        var errors = validator.Validate(doc)
            .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility &&
                        e.Node?.LocalName != "collapsed" &&
                        !(e.Description?.Contains("collapsed") ?? false))
            .ToList();

        if (errors.Count > 0)
        {
            var msg = string.Join("\n", errors.Select(e => $"[{e.Id}] {e.Description} AT NODE: {e.Node?.OuterXml}"));
            Assert.Fail($"OpenXML Validation failed for {Path.GetFileName(docxPath)} with {errors.Count} errors:\n{msg}");
        }
    }

    [Fact]
    public void Tabs_TabCount_0_DetectorRejects_And_ExportGraceful()
    {
        var detector = new TabsDetector();
        var zeroTabsMarkdown = @":::tabs
No tab markers here
:::";

        var res = detector.Validate(zeroTabsMarkdown);
        Assert.False(res.IsValid, "Detector should reject :::tabs with zero child tabs");
        Assert.Contains(res.Errors, e => e.Contains("No :::tab or == tab header children found"));

        // When exported, zero-tab block should not throw and should output standard docx
        var outputPath = GetTestOutputPath("adv_tabs_count_0.docx");
        if (File.Exists(outputPath)) File.Delete(outputPath);

        new DocxExportService().ExportAsync(zeroTabsMarkdown, outputPath, new AppSettings()).GetAwaiter().GetResult();
        Assert.True(File.Exists(outputPath));
        ValidateOpenXmlSchema(outputPath);
    }

    [Fact]
    public void Tabs_TabCount_1_RendersSingleCellTable_And_ActiveHeading()
    {
        var markdown = @":::tabs
:::tab title=""Solo Tab""
Single tab content body paragraph.
:::
:::";

        var outputPath = GetTestOutputPath("adv_tabs_count_1.docx");
        if (File.Exists(outputPath)) File.Delete(outputPath);

        new DocxExportService().ExportAsync(markdown, outputPath, new AppSettings()).GetAwaiter().GetResult();
        Assert.True(File.Exists(outputPath));

        using var doc = WordprocessingDocument.Open(outputPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var tables = body.Descendants<W.Table>().ToList();
        Assert.NotEmpty(tables);
        var cells = tables[0].Descendants<W.TableCell>().ToList();
        Assert.Single(cells);

        var headings = body.Descendants<W.Paragraph>()
            .Where(p => p.ParagraphProperties?.OutlineLevel?.Val?.Value == 8)
            .ToList();
        Assert.Single(headings);

        ValidateOpenXmlSchema(outputPath);
    }

    [Fact]
    public void Tabs_TabCount_10_Renders10HeaderCells_And_10Headings()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(":::tabs");
        for (int i = 1; i <= 10; i++)
        {
            sb.AppendLine($":::tab title=\"Tab {i}\"");
            sb.AppendLine($"Body content for Tab {i}");
            sb.AppendLine(":::");
        }
        sb.AppendLine(":::");

        var markdown = sb.ToString();

        var outputPath = GetTestOutputPath("adv_tabs_count_10.docx");
        if (File.Exists(outputPath)) File.Delete(outputPath);

        new DocxExportService().ExportAsync(markdown, outputPath, new AppSettings()).GetAwaiter().GetResult();
        Assert.True(File.Exists(outputPath));

        using var doc = WordprocessingDocument.Open(outputPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var tables = body.Descendants<W.Table>().ToList();
        Assert.NotEmpty(tables);
        var cells = tables[0].Descendants<W.TableCell>().ToList();
        Assert.Equal(10, cells.Count);

        var headings = body.Descendants<W.Paragraph>()
            .Where(p => p.ParagraphProperties?.OutlineLevel?.Val?.Value == 8)
            .ToList();
        Assert.Equal(10, headings.Count);

        ValidateOpenXmlSchema(outputPath);
    }

    [Fact]
    public void Tabs_EmptyBodySections_RendersHeadersWithoutContent()
    {
        var markdown = @":::tabs
:::tab title=""Empty A""
:::
:::tab title=""Empty B""
:::
:::tab title=""Populated C""
Content C
:::
:::";

        var outputPath = GetTestOutputPath("adv_tabs_empty_bodies.docx");
        if (File.Exists(outputPath)) File.Delete(outputPath);

        new DocxExportService().ExportAsync(markdown, outputPath, new AppSettings()).GetAwaiter().GetResult();
        Assert.True(File.Exists(outputPath));

        using var doc = WordprocessingDocument.Open(outputPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var headings = body.Descendants<W.Paragraph>()
            .Where(p => p.ParagraphProperties?.OutlineLevel?.Val?.Value == 8)
            .ToList();
        Assert.Equal(3, headings.Count);

        var text = body.InnerText;
        Assert.Contains("Empty A", text);
        Assert.Contains("Empty B", text);
        Assert.Contains("Content C", text);

        ValidateOpenXmlSchema(outputPath);
    }

    [Fact]
    public void Tabs_NestedComplexMarkdown_TablesCodeBlocksCallouts()
    {
        var markdown = @":::tabs
:::tab title=""Table Tab""
| Feature | Supported |
| --- | --- |
| Tabs | Yes |
| Tables | Yes |
:::
:::tab title=""Code Tab""
```csharp
public class Demo {
    public int Value { get; set; }
}
```
:::
:::tab title=""Callout Tab""
> [!NOTE]
> This is a nested note inside a tab.
:::
:::";

        var outputPath = GetTestOutputPath("adv_tabs_nested_complex.docx");
        if (File.Exists(outputPath)) File.Delete(outputPath);

        new DocxExportService().ExportAsync(markdown, outputPath, new AppSettings()).GetAwaiter().GetResult();
        Assert.True(File.Exists(outputPath));

        using var doc = WordprocessingDocument.Open(outputPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        // 1 header table + 1 nested table inside tab 1
        var tables = body.Descendants<W.Table>().ToList();
        Assert.True(tables.Count >= 2, $"Expected at least 2 tables (1 tab bar + 1 inner table), found {tables.Count}");

        var text = body.InnerText;
        Assert.Contains("public class Demo", text);
        Assert.Contains("This is a nested note inside a tab.", text);

        ValidateOpenXmlSchema(outputPath);
    }

    [Fact]
    public void Tabs_CodeBlockWithNestedTabSyntax_Challenge()
    {
        var markdown = @":::tabs
:::tab title=""Code Example Tab""
Here is an example of tab syntax:
```markdown
:::tabs
:::tab title=""Inner Tab""
Inner content
:::
:::
```
:::
:::";

        var outputPath = GetTestOutputPath("adv_tabs_nested_syntax_in_code.docx");
        if (File.Exists(outputPath)) File.Delete(outputPath);

        new DocxExportService().ExportAsync(markdown, outputPath, new AppSettings()).GetAwaiter().GetResult();
        Assert.True(File.Exists(outputPath));

        using var doc = WordprocessingDocument.Open(outputPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var tables = body.Descendants<W.Table>().ToList();
        Assert.NotEmpty(tables);

        var cells = tables[0].Descendants<W.TableCell>().ToList();
        var headings = body.Descendants<W.Paragraph>()
            .Where(p => p.ParagraphProperties?.OutlineLevel?.Val?.Value == 8)
            .ToList();

        Assert.True(cells.Count == 1, $"Code block inside tab caused false tab splitting into {cells.Count} tabs! Headings count: {headings.Count}");

        ValidateOpenXmlSchema(outputPath);
    }

    [Fact]
    public void Tabs_TitleWithEscapedOrNestedQuotes_TruncationChallenge()
    {
        var markdown = @":::tabs
:::tab title=""Overview ""v2.0"" Details""
Content A
:::
:::";

        var outputPath = GetTestOutputPath("adv_tabs_nested_quotes.docx");
        if (File.Exists(outputPath)) File.Delete(outputPath);

        new DocxExportService().ExportAsync(markdown, outputPath, new AppSettings()).GetAwaiter().GetResult();
        Assert.True(File.Exists(outputPath));

        using var doc = WordprocessingDocument.Open(outputPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var headings = body.Descendants<W.Paragraph>()
            .Where(p => p.ParagraphProperties?.OutlineLevel?.Val?.Value == 8)
            .ToList();
        Assert.Single(headings);

        // Verify title text: if regex truncated at inner quote, it won't contain "Details"
        var headingText = headings[0].InnerText;
        Assert.True(headingText.Contains("Details"), $"Tab title was truncated by inner double-quotes: '{headingText}'");

        ValidateOpenXmlSchema(outputPath);
    }

    [Fact]
    public void Tabs_SyntaxVariants_EqualsAndTripleEqualsAndUnquoted()
    {
        var markdown = @":::tabs
:::tab title=""Syntax 1: Tab title""
Body 1
:::
== Syntax 2: Double Equals
Body 2
=== ""Syntax 3: Triple Equals Quoted""
Body 3
:::tab title=UnquotedTitle
Body 4
:::tab BareTabNoTitleAttribute
Body 5
:::
:::";

        var outputPath = GetTestOutputPath("adv_tabs_syntax_variants.docx");
        if (File.Exists(outputPath)) File.Delete(outputPath);

        new DocxExportService().ExportAsync(markdown, outputPath, new AppSettings()).GetAwaiter().GetResult();
        Assert.True(File.Exists(outputPath));

        using var doc = WordprocessingDocument.Open(outputPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var tables = body.Descendants<W.Table>().ToList();
        Assert.NotEmpty(tables);
        var cells = tables[0].Descendants<W.TableCell>().ToList();
        Assert.True(cells.Count >= 4, $"Expected at least 4 tab cells, found {cells.Count}");

        ValidateOpenXmlSchema(outputPath);
    }
}
