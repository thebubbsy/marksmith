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

public class NativeTabsTests
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
        return Path.GetFullPath(Path.Combine(outputDir, fileName));
    }

    [Fact]
    public void TabsDetector_Validates_Both_Tab_Syntaxes()
    {
        var detector = new TabsDetector();

        var tabSyntax = @":::tabs
:::tab title=""Overview""
Overview body
:::
:::tab title=""Details""
Details body
:::
:::";

        var headingSyntax = @":::tabs
== Tab 1
Content 1
== Tab 2
Content 2
:::";

        var tripleEqSyntax = @":::tabs
=== ""First Tab""
Content A
=== ""Second Tab""
Content B
:::";

        Assert.True(detector.Matches(tabSyntax));
        var res1 = detector.Validate(tabSyntax);
        Assert.True(res1.IsValid, string.Join("; ", res1.Errors));

        Assert.True(detector.Matches(headingSyntax));
        var res2 = detector.Validate(headingSyntax);
        Assert.True(res2.IsValid, string.Join("; ", res2.Errors));

        Assert.True(detector.Matches(tripleEqSyntax));
        var res3 = detector.Validate(tripleEqSyntax);
        Assert.True(res3.IsValid, string.Join("; ", res3.Errors));
    }

    [Fact]
    public void RenderTabs_Creates_TabHeaderBarTable_Hyperlinks_OutlineLvl_And_CollapsedSections()
    {
        var markdown = @"# Interactive Tabbed Content

:::tabs
:::tab title=""Overview""
This is the **Overview** section content.
:::
:::tab title=""Specifications""
These are the **Specifications** details.
:::
:::tab title=""Architecture""
This is the **Architecture** system design.
:::
:::
";

        var outputPath = GetTestOutputPath("sample_tabs.docx");
        if (File.Exists(outputPath)) File.Delete(outputPath);

        new DocxExportService().ExportAsync(markdown, outputPath, new AppSettings()).GetAwaiter().GetResult();

        Assert.True(File.Exists(outputPath), $"Expected output file at '{outputPath}'");

        using var doc = WordprocessingDocument.Open(outputPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        // 1. Verify Tab Header Bar Table (W.Table)
        var tables = body.Descendants<W.Table>().ToList();
        Assert.NotEmpty(tables);
        var tabTable = tables[0];

        var rows = tabTable.Descendants<W.TableRow>().ToList();
        Assert.Single(rows);

        var cells = rows[0].Descendants<W.TableCell>().ToList();
        Assert.Equal(3, cells.Count);

        // 2. Verify Tab Headers Visual Styling (Active vs Inactive)
        var cell0Shd = cells[0].TableCellProperties?.Shading?.Fill?.Value;
        Assert.Equal("EBF3FE", cell0Shd);

        var cell1Shd = cells[1].TableCellProperties?.Shading?.Fill?.Value;
        Assert.Equal("F8F9FA", cell1Shd);

        var cell2Shd = cells[2].TableCellProperties?.Shading?.Fill?.Value;
        Assert.Equal("F8F9FA", cell2Shd);

        // 3. Verify Hyperlinks pointing to Bookmarks
        var hyperlinks = tabTable.Descendants<W.Hyperlink>().ToList();
        Assert.Equal(3, hyperlinks.Count);

        var anchors = hyperlinks.Select(h => h.Anchor?.Value).ToList();
        Assert.All(anchors, a => Assert.False(string.IsNullOrEmpty(a)));
        Assert.All(anchors, a => Assert.StartsWith("tab_", a));

        // 4. Verify Tab Section Headings with OutlineLevel 8 and DefaultCollapsed
        var sectionHeadings = body.Descendants<W.Paragraph>()
            .Where(p => p.ParagraphProperties?.OutlineLevel?.Val?.Value == 8)
            .ToList();

        Assert.Equal(3, sectionHeadings.Count);

        // Heading 0 (Active Tab): DefaultCollapsed is false (0)
        var heading0Xml = sectionHeadings[0].OuterXml;
        Assert.Contains("collapsed", heading0Xml.ToLowerInvariant());
        Assert.True(heading0Xml.Contains("val=\"false\"", StringComparison.OrdinalIgnoreCase) || heading0Xml.Contains("val=\"0\"", StringComparison.OrdinalIgnoreCase));

        // Heading 1 & 2 (Inactive Tabs): DefaultCollapsed is true (1)
        var heading1Xml = sectionHeadings[1].OuterXml;
        Assert.Contains("collapsed", heading1Xml.ToLowerInvariant());
        Assert.True(heading1Xml.Contains("val=\"true\"", StringComparison.OrdinalIgnoreCase) || heading1Xml.Contains("val=\"1\"", StringComparison.OrdinalIgnoreCase));

        var heading2Xml = sectionHeadings[2].OuterXml;
        Assert.Contains("collapsed", heading2Xml.ToLowerInvariant());
        Assert.True(heading2Xml.Contains("val=\"true\"", StringComparison.OrdinalIgnoreCase) || heading2Xml.Contains("val=\"1\"", StringComparison.OrdinalIgnoreCase));

        // 5. Verify Bookmarks match Hyperlink anchors
        var bookmarkStarts = body.Descendants<W.BookmarkStart>().ToList();
        foreach (var anchor in anchors)
        {
            Assert.Contains(bookmarkStarts, b => b.Name?.Value == anchor);
        }

        // 6. Verify Markdown body blocks rendered under tabs
        var textContent = body.InnerText;
        Assert.Contains("This is the Overview section content.", textContent);
        Assert.Contains("These are the Specifications details.", textContent);
        Assert.Contains("This is the Architecture system design.", textContent);
    }

    [Fact]
    public void RenderTabs_InternalEqualsSeparators_ParsesAndRendersCorrectly()
    {
        var markdown = @":::tabs
== Overview Tab
Overview body paragraph.

== Specs Tab
Specs body paragraph.
:::";

        var tempPath = Path.Combine(Path.GetTempPath(), $"tabs_equals_{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(markdown, tempPath, new AppSettings()).GetAwaiter().GetResult();

            using var doc = WordprocessingDocument.Open(tempPath, false);
            var body = doc.MainDocumentPart!.Document.Body!;

            var tables = body.Descendants<W.Table>().ToList();
            Assert.NotEmpty(tables);

            var cells = tables[0].Descendants<W.TableCell>().ToList();
            Assert.Equal(2, cells.Count);

            var headings = body.Descendants<W.Paragraph>()
                .Where(p => p.ParagraphProperties?.OutlineLevel?.Val?.Value == 8)
                .ToList();
            Assert.Equal(2, headings.Count);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void NativeTabs_OpenXmlSchemaValidation_ZeroErrors()
    {
        var outputPath = GetTestOutputPath("sample_tabs.docx");
        if (!File.Exists(outputPath))
        {
            RenderTabs_Creates_TabHeaderBarTable_Hyperlinks_OutlineLvl_And_CollapsedSections();
        }

        using var doc = WordprocessingDocument.Open(outputPath, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
        var errors = validator.Validate(doc)
            .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility &&
                        e.Node?.LocalName != "collapsed" &&
                        !(e.Description?.Contains("collapsed") ?? false))
            .ToList();

        if (errors.Count > 0)
        {
            var msg = string.Join("\n", errors.Select(e => $"[{e.Id}] {e.Description} AT NODE: {e.Node?.OuterXml}"));
            Assert.Fail($"OpenXML Validation failed for sample_tabs.docx with {errors.Count} errors:\n{msg}");
        }
    }
}
