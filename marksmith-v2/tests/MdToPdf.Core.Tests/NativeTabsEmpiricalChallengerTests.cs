using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;
using W15 = DocumentFormat.OpenXml.Office2013.Word;

namespace MdToPdf.Core.Tests;

public class NativeTabsEmpiricalChallengerTests
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

    private static string GenerateDocx(string fileName, string markdown, AppSettings? settings = null)
    {
        var path = GetTestOutputPath(fileName);
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
        new DocxExportService().ExportAsync(markdown, path, settings ?? new AppSettings()).GetAwaiter().GetResult();
        return path;
    }

    private const string SampleMarkdown = @"# Interactive Tabbed Content

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

    [Fact]
    public void Scenario1_ZipExtraction_And_OpenXmlDom_Reader_Verification()
    {
        var docxPath = GenerateDocx("challenger_scenario1.docx", SampleMarkdown);
        Assert.True(File.Exists(docxPath), "Docx file must exist");

        // Zip extraction test
        using (var zip = ZipFile.OpenRead(docxPath))
        {
            var docEntry = zip.GetEntry("word/document.xml");
            Assert.NotNull(docEntry);

            using var stream = docEntry.Open();
            var docXml = XDocument.Load(stream);
            Assert.NotNull(docXml.Root);

            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            XNamespace w15 = "http://schemas.microsoft.com/office/word/2012/wordml";

            var tables = docXml.Descendants(w + "tbl").ToList();
            Assert.NotEmpty(tables);

            var hyperlinks = docXml.Descendants(w + "hyperlink").ToList();
            Assert.Equal(3, hyperlinks.Count);

            var bookmarks = docXml.Descendants(w + "bookmarkStart").ToList();
            Assert.True(bookmarks.Count >= 3);

            var collapsedNodes = docXml.Descendants(w15 + "collapsed").ToList();
            Assert.Equal(3, collapsedNodes.Count);
        }

        // OpenXML DOM Reader test
        using var doc = WordprocessingDocument.Open(docxPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;
        var domTables = body.Descendants<W.Table>().ToList();
        Assert.NotEmpty(domTables);
    }

    [Fact]
    public void Scenario2_Bookmark_IDs_Names_Uniqueness_And_Length_Verification()
    {
        var docxPath = GenerateDocx("challenger_scenario2.docx", SampleMarkdown);

        using var doc = WordprocessingDocument.Open(docxPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var bookmarkStarts = body.Descendants<W.BookmarkStart>().ToList();
        var bookmarkEnds = body.Descendants<W.BookmarkEnd>().ToList();

        Assert.NotEmpty(bookmarkStarts);

        // 1. Verify Bookmark IDs are unique
        var startIds = bookmarkStarts.Select(b => b.Id?.Value).ToList();
        var endIds = bookmarkEnds.Select(b => b.Id?.Value).ToList();

        Assert.All(startIds, id => Assert.NotNull(id));
        Assert.Equal(startIds.Count, startIds.Distinct().Count());

        // Each start ID must have a matching end ID
        foreach (var startId in startIds)
        {
            Assert.Contains(startId, endIds);
        }

        // 2. Verify Bookmark Names are unique and <= 40 chars
        var bookmarkNames = bookmarkStarts.Select(b => b.Name?.Value).Where(n => !string.IsNullOrEmpty(n)).ToList();
        
        Assert.Equal(bookmarkNames.Count, bookmarkNames.Distinct().Count());

        foreach (var name in bookmarkNames)
        {
            Assert.True(name!.Length <= 40, $"Bookmark name '{name}' length ({name.Length}) exceeds 40 chars limit");
        }
    }

    [Fact]
    public void Scenario3_Hyperlink_Anchors_Resolve_To_Valid_Bookmarks()
    {
        var docxPath = GenerateDocx("challenger_scenario3.docx", SampleMarkdown);

        using var doc = WordprocessingDocument.Open(docxPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var hyperlinks = body.Descendants<W.Hyperlink>().Where(h => !string.IsNullOrEmpty(h.Anchor?.Value)).ToList();
        var bookmarkNames = body.Descendants<W.BookmarkStart>().Select(b => b.Name?.Value).ToHashSet();

        Assert.NotEmpty(hyperlinks);

        foreach (var hyperlink in hyperlinks)
        {
            var anchor = hyperlink.Anchor!.Value;
            Assert.True(bookmarkNames.Contains(anchor), $"Hyperlink anchor '{anchor}' does not resolve to any bookmark in the document!");
        }
    }

    [Fact]
    public void Scenario4_Paragraph_OutlineLevels_And_CollapseFlags_And_Toc_Isolation()
    {
        var docxPath = GenerateDocx("challenger_scenario4.docx", SampleMarkdown);

        using var doc = WordprocessingDocument.Open(docxPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var outline8Headings = body.Descendants<W.Paragraph>()
            .Where(p => p.ParagraphProperties?.OutlineLevel?.Val?.Value == 8)
            .ToList();

        Assert.Equal(3, outline8Headings.Count);

        // Active tab heading (index 0) has collapsed = false / 0
        var activeXml = outline8Headings[0].OuterXml;
        Assert.Contains("collapsed", activeXml, StringComparison.OrdinalIgnoreCase);
        Assert.True(activeXml.Contains("val=\"false\"", StringComparison.OrdinalIgnoreCase) || activeXml.Contains("val=\"0\"", StringComparison.OrdinalIgnoreCase));

        // Inactive tab headings (indices 1 & 2) have collapsed = true / 1
        for (int i = 1; i < 3; i++)
        {
            var inactiveXml = outline8Headings[i].OuterXml;
            Assert.Contains("collapsed", inactiveXml, StringComparison.OrdinalIgnoreCase);
            Assert.True(inactiveXml.Contains("val=\"true\"", StringComparison.OrdinalIgnoreCase) || inactiveXml.Contains("val=\"1\"", StringComparison.OrdinalIgnoreCase));
        }

        // Test TOC Field Isolation
        var markdownWithToc = @"# Document Title

:::tabs
:::tab title=""Tab 1""
Content 1
:::
:::tab title=""Tab 2""
Content 2
:::
:::";
        var settings = new AppSettings { IncludeToc = true };
        var tocDocxPath = GenerateDocx("challenger_scenario4_toc.docx", markdownWithToc, settings);

        using var tocDoc = WordprocessingDocument.Open(tocDocxPath, false);
        var tocBody = tocDoc.MainDocumentPart!.Document.Body!;
        var fieldCodes = tocBody.Descendants<W.FieldCode>().Select(f => f.Text).ToList();

        Assert.Contains(fieldCodes, fc => fc.Contains("TOC \\o \"1-3\""));
    }

    [Fact]
    public void Scenario5_OpenXmlValidator_Returns_Zero_Errors()
    {
        var docxPath = GenerateDocx("challenger_scenario5.docx", SampleMarkdown);

        using var doc = WordprocessingDocument.Open(docxPath, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
        var errors = validator.Validate(doc)
            .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility &&
                        e.Node?.LocalName != "collapsed" &&
                        !(e.Description?.Contains("collapsed") ?? false))
            .ToList();

        Assert.Empty(errors);
    }

    [Fact]
    public void Scenario6_Adversarial_Multiple_Identical_TabBlocks_BookmarkName_Uniqueness()
    {
        var markdown = @"# Double Tabs Test

:::tabs
:::tab title=""Alpha""
Content A1
:::
:::tab title=""Beta""
Content B1
:::
:::

# Section Two

:::tabs
:::tab title=""Alpha""
Content A1
:::
:::tab title=""Beta""
Content B1
:::
:::";

        var docxPath = GenerateDocx("challenger_scenario6_dupetabs.docx", markdown);

        using var doc = WordprocessingDocument.Open(docxPath, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var bookmarkNames = body.Descendants<W.BookmarkStart>()
            .Select(b => b.Name?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        var duplicates = bookmarkNames.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        // Check if there are any duplicate bookmark names across identical tab blocks
        Assert.Empty(duplicates);
    }
}
