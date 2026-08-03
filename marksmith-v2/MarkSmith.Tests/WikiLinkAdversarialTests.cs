using System.IO.Compression;
using System.Xml.Linq;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class WikiLinkAdversarialTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static XDocument ExportToXml(string md, AppSettings? s = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-wikilink-test-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(md, path, s ?? new AppSettings()).GetAwaiter().GetResult();
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml")!;
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void MultipleWikiLinksInSingleParagraph_AllRunsHaveNoProofAndCorrectText()
    {
        var md = "Check out [[LinkA]] and also [[LinkB|Alias B]] in the same paragraph.";
        var doc = ExportToXml(md);

        var xmlString = doc.ToString();
        Assert.Contains("LinkA", xmlString);
        Assert.Contains("Alias B", xmlString);
        Assert.DoesNotContain("LinkB", xmlString);
        Assert.DoesNotContain("[[", xmlString);

        var runs = doc.Descendants(W + "r").ToList();
        var linkARun = runs.FirstOrDefault(r => r.Element(W + "t")?.Value == "LinkA");
        var aliasBRun = runs.FirstOrDefault(r => r.Element(W + "t")?.Value == "Alias B");

        Assert.NotNull(linkARun);
        Assert.NotNull(aliasBRun);

        Assert.NotNull(linkARun!.Element(W + "rPr")?.Element(W + "noProof"));
        Assert.NotNull(aliasBRun!.Element(W + "rPr")?.Element(W + "noProof"));
    }

    [Fact]
    public void WikiLinksWithDoubleHyphens_NormalizesAndPreservesNoProof()
    {
        // DashMode=1 enables the -- → — → - pipeline; the test verifies noProof survives that.
        var md = "WikiLink with dashes: [[Link -- Dash|Alias -- Dash]] and [[Direct--Link]].";
        var doc = ExportToXml(md, new AppSettings { DashMode = 1 });
        var xmlString = doc.ToString();

        Assert.Contains("Alias-Dash", xmlString);
        Assert.Contains("Direct-Link", xmlString);
        Assert.DoesNotContain("[[", xmlString);

        var runs = doc.Descendants(W + "r").ToList();
        var aliasRun = runs.FirstOrDefault(r => r.Element(W + "t")?.Value == "Alias-Dash");
        var directRun = runs.FirstOrDefault(r => r.Element(W + "t")?.Value == "Direct-Link");

        Assert.NotNull(aliasRun);
        Assert.NotNull(directRun);

        Assert.NotNull(aliasRun!.Element(W + "rPr")?.Element(W + "noProof"));
        Assert.NotNull(directRun!.Element(W + "rPr")?.Element(W + "noProof"));
    }

    [Fact]
    public void WikiLinksInsideListItems_RendersWithNoProof()
    {
        var md = @"- Unordered item 1 with [[UnorderedLinkA]]
- Unordered item 2 with [[UnorderedTarget|Unordered Alias]]

1. Ordered item 1 with [[OrderedLinkA]]
2. Ordered item 2 with [[OrderedTarget|Ordered Alias]]";

        var doc = ExportToXml(md);
        var runs = doc.Descendants(W + "r").ToList();

        string[] expectedTexts = ["UnorderedLinkA", "Unordered Alias", "OrderedLinkA", "Ordered Alias"];
        foreach (var expected in expectedTexts)
        {
            var run = runs.FirstOrDefault(r => r.Element(W + "t")?.Value == expected);
            Assert.True(run != null, $"Expected text run '{expected}' not found in DOCX.");
            var noProof = run.Element(W + "rPr")?.Element(W + "noProof");
            Assert.True(noProof != null, $"Run '{expected}' in list item missing <w:noProof/>.");
        }
    }

    [Fact]
    public void WikiLinksInsideBlockquotes_RendersWithNoProof()
    {
        var md = "> This blockquote contains [[QuoteLinkA]] and [[QuoteTarget|Quote Alias]].";

        var doc = ExportToXml(md);
        var runs = doc.Descendants(W + "r").ToList();

        string[] expectedTexts = ["QuoteLinkA", "Quote Alias"];
        foreach (var expected in expectedTexts)
        {
            var run = runs.FirstOrDefault(r => r.Element(W + "t")?.Value == expected);
            Assert.True(run != null, $"Expected text run '{expected}' in blockquote not found.");
            var noProof = run.Element(W + "rPr")?.Element(W + "noProof");
            Assert.True(noProof != null, $"Run '{expected}' in blockquote missing <w:noProof/>.");
        }
    }

    [Fact]
    public void WikiLinksInsideTables_RendersWithNoProof()
    {
        var md = @"| Header 1 | Header 2 |
| --- | --- |
| [[TableCellLinkA]] | [[TableTarget|Table Alias]] |";

        var doc = ExportToXml(md);
        var runs = doc.Descendants(W + "r").ToList();

        string[] expectedTexts = ["TableCellLinkA", "Table Alias"];
        foreach (var expected in expectedTexts)
        {
            var run = runs.FirstOrDefault(r => r.Element(W + "t")?.Value == expected);
            Assert.True(run != null, $"Expected text run '{expected}' in table cell not found.");
            var noProof = run.Element(W + "rPr")?.Element(W + "noProof");
            Assert.True(noProof != null, $"Run '{expected}' in table cell missing <w:noProof/>.");
        }
    }

    [Fact]
    public void NonDictionaryCamelCaseSnakeCaseAndUUIDWikiLinks_AllHaveNoProof()
    {
        var md = @"Here are technical identifiers as WikiLinks:
- Snake case: [[Project_Phoenix_v2_2026]]
- Deep snake case: [[my_snake_case_target_page]]
- UUID: [[550e8400-e29b-41d4-a716-446655440000]]
- camelCase: [[camelCasePageNameWithNumbers123]]
- Aliased UUID: [[550e8400-e29b-41d4-a716-446655440000|UUID Custom Alias]]";

        var doc = ExportToXml(md);
        var runs = doc.Descendants(W + "r").ToList();

        string[] expectedTexts = [
            "Project_Phoenix_v2_2026",
            "my_snake_case_target_page",
            "550e8400-e29b-41d4-a716-446655440000",
            "camelCasePageNameWithNumbers123",
            "UUID Custom Alias"
        ];

        foreach (var expected in expectedTexts)
        {
            var run = runs.FirstOrDefault(r => r.Element(W + "t")?.Value == expected);
            Assert.True(run != null, $"Expected run with text '{expected}' not found.");
            var noProof = run.Element(W + "rPr")?.Element(W + "noProof");
            Assert.True(noProof != null, $"Run '{expected}' missing <w:noProof/> element for spellcheck suppression.");
        }

        Assert.DoesNotContain("550e8400-e29b-41d4-a716-446655440000|UUID Custom Alias", doc.ToString());
    }

    [Fact]
    public void WikiLinksInInlineCodeAndFences_StayLiteralWithoutWikiLinkSpan()
    {
        var md = @"Inline code `[[LiteralInlineLink]]` and fenced block:

```
[[LiteralFenceLink]]
```";

        var doc = ExportToXml(md);
        var xmlString = doc.ToString();

        Assert.Contains("[[LiteralInlineLink]]", xmlString);
        Assert.Contains("[[LiteralFenceLink]]", xmlString);
    }
}
