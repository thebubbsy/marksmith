using System.IO.Compression;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace MarkSmith.Core.Tests;

public class AdvancedLayoutsTests
{
    private static (string docxPath, string xml) ExportToXml(string md, AppSettings? s = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-adv-layout-test-{Guid.NewGuid():N}.docx");
        new DocxExportService().ExportAsync(md, path, s ?? new AppSettings()).GetAwaiter().GetResult();
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml")!;
        using var reader = new StreamReader(entry.Open());
        return (path, reader.ReadToEnd());
    }

    // =========================================================================
    // 1. Native Collapsible Sections & Toggles
    // =========================================================================

    [Fact]
    public void CollapsibleSection_DetailsSummary_EmitsOutlineLevel8AndCollapsedAttributes()
    {
        var md = "<details>\n<summary>Server Config</summary>\nIP: 192.168.1.1\n</details>\n";
        var (path, xml) = ExportToXml(md);
        try
        {
            Assert.Contains("Server Config", xml);
            Assert.Contains("IP: 192.168.1.1", xml);
            Assert.Contains("outlineLvl", xml);
            Assert.Contains("val=\"8\"", xml);
            Assert.True(xml.Contains("collapsed", StringComparison.OrdinalIgnoreCase) || xml.Contains("defaultCollapsed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CollapsibleSection_ToggleSyntax_EmitsOutlineLevel8AndCollapsedAttributes()
    {
        var md = ":::toggle [Security Policy]\nFirewall rules enabled.\n:::\n";
        var (path, xml) = ExportToXml(md);
        try
        {
            Assert.Contains("Security Policy", xml);
            Assert.Contains("Firewall rules enabled", xml);
            Assert.Contains("outlineLvl", xml);
            Assert.Contains("val=\"8\"", xml);
            Assert.True(xml.Contains("collapsed", StringComparison.OrdinalIgnoreCase) || xml.Contains("defaultCollapsed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CollapsibleSection_InactiveTabs_EmitOutlineLevel8AndCollapsedAttributes()
    {
        var md = ":::tabs\n=== ActiveTab\nActive content\n=== InactiveTab\nInactive content\n:::\n";
        var (path, xml) = ExportToXml(md);
        try
        {
            Assert.Contains("ActiveTab", xml);
            Assert.Contains("InactiveTab", xml);
            Assert.Contains("outlineLvl", xml);
            Assert.Contains("val=\"8\"", xml);
            Assert.True(xml.Contains("collapsed", StringComparison.OrdinalIgnoreCase) || xml.Contains("defaultCollapsed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CollapsibleSection_NestedDetails_PreservesOuterAndInnerContent()
    {
        var md = "<details>\n<summary>Outer</summary>\nOuter body.\n<details>\n<summary>Inner</summary>\nInner body.\n</details>\nOuter tail.\n</details>\n";
        var (path, xml) = ExportToXml(md);
        try
        {
            Assert.Contains("Outer", xml);
            Assert.Contains("Outer body", xml);
            Assert.Contains("Inner", xml);
            Assert.Contains("Inner body", xml);
            Assert.Contains("Outer tail", xml);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // =========================================================================
    // 2. Multi-Column Blocks (:::columns)
    // =========================================================================

    [Fact]
    public void MultiColumns_Docx_EmitsContinuousSectionBreaksAndColumnBreaks()
    {
        var md = ":::columns count=\"3\"\nLeft column text\n===\nMiddle column text\n===\nRight column text\n:::\n";
        var (path, xml) = ExportToXml(md);
        try
        {
            Assert.Contains("Left column text", xml);
            Assert.Contains("Middle column text", xml);
            Assert.Contains("Right column text", xml);

            // Verify continuous section break with 3 columns
            Assert.Contains("<w:type w:val=\"continuous\"", xml);
            Assert.Contains("<w:cols", xml);
            Assert.Contains("num=\"3\"", xml);
            Assert.Contains("space=\"720\"", xml);

            // Verify column break between columns
            Assert.Contains("<w:br w:type=\"column\"", xml);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MultiColumns_HtmlPreview_EmitsResponsiveGridContainer()
    {
        var md = ":::columns count=\"2\"\nColumn 1 content\n===\nColumn 2 content\n:::\n";
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));

        Assert.Contains("class=\"ms-columns\"", html);
        Assert.Contains("grid-template-columns: repeat(2, 1fr)", html);
        Assert.Contains("gap: 1.5rem", html);
        Assert.Contains("class=\"ms-column\"", html);
        Assert.Contains("Column 1 content", html);
        Assert.Contains("Column 2 content", html);
    }

    [Fact]
    public void MultiColumns_HtmlPreview_SanitizesXssVectorsInsideColumns()
    {
        var md = ":::columns count=\"2\"\n<script>alert('xss')</script>Safe column\n===\n<img src=\"x\" onerror=\"alert(1)\">Column 2\n:::\n";
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));

        Assert.DoesNotContain("alert('xss')", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Safe column", html);
        Assert.Contains("Column 2", html);
    }

    // =========================================================================
    // 3. Nested Grid HTML Table Engine
    // =========================================================================

    [Fact]
    public void HtmlTable_Colspan_EmitsGridSpanInDocx()
    {
        var md = @"<table>
  <tr><th colspan=""2"">Header Span 2</th></tr>
  <tr><td>Col 1</td><td>Col 2</td></tr>
</table>";
        var (path, xml) = ExportToXml(md);
        try
        {
            Assert.Contains("Header Span 2", xml);
            Assert.Contains("Col 1", xml);
            Assert.Contains("Col 2", xml);
            Assert.Contains("<w:gridSpan w:val=\"2\"", xml);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void HtmlTable_Rowspan_EmitsVerticalMergeRestartAndContinuation()
    {
        var md = @"<table>
  <tr><td rowspan=""2"">RowSpan Cell</td><td>Row 1 Right</td></tr>
  <tr><td>Row 2 Right</td></tr>
</table>";
        var (path, xml) = ExportToXml(md);
        try
        {
            Assert.Contains("RowSpan Cell", xml);
            Assert.Contains("Row 1 Right", xml);
            Assert.Contains("Row 2 Right", xml);
            Assert.Contains("<w:vMerge w:val=\"restart\"", xml);
            Assert.Contains("<w:vMerge", xml);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void HtmlTable_NestedTable_RendersNestedTableInsideTableCell()
    {
        var md = @"<table>
  <tr>
    <td>Outer Left</td>
    <td>
      <table>
        <tr><td>Inner Left</td><td>Inner Right</td></tr>
      </table>
    </td>
  </tr>
</table>";
        var (path, xml) = ExportToXml(md);
        try
        {
            Assert.Contains("Outer Left", xml);
            Assert.Contains("Inner Left", xml);
            Assert.Contains("Inner Right", xml);

            using var pkg = WordprocessingDocument.Open(path, false);
            var body = pkg.MainDocumentPart!.Document.Body!;
            var tables = body.Descendants<W.Table>().ToList();
            Assert.True(tables.Count >= 2, $"Expected at least 2 tables (nested), found {tables.Count}");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void HtmlTable_RichInlineFormatting_EmitsBoldItalicCodeAndHyperlinks()
    {
        var md = @"<table>
  <tr>
    <td><b>BoldText</b> <i>ItalicText</i> <code>CodeText</code> <a href=""https://example.com"">LinkText</a></td>
  </tr>
</table>";
        var (path, xml) = ExportToXml(md);
        try
        {
            Assert.Contains("BoldText", xml);
            Assert.Contains("ItalicText", xml);
            Assert.Contains("CodeText", xml);
            Assert.Contains("LinkText", xml);
            Assert.Contains("<w:b", xml);
            Assert.Contains("<w:i", xml);
            Assert.Contains("Consolas", xml);
            Assert.Contains("<w:hyperlink", xml);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // =========================================================================
    // 4. DrawingML Chart Dynamic IDs
    // =========================================================================

    [Fact]
    public void DrawingMLChart_MultipleCharts_EmitUniqueDrawingIds()
    {
        var md = @":::chart type=""bar""
Item A, 10
Item B, 20
:::

:::chart type=""pie""
Slice 1, 40
Slice 2, 60
:::";
        var (path, xml) = ExportToXml(md);
        try
        {
            using var pkg = WordprocessingDocument.Open(path, false);
            var body = pkg.MainDocumentPart!.Document.Body!;
            var docProperties = body.Descendants<DW.DocProperties>().ToList();
            Assert.True(docProperties.Count >= 2, $"Expected at least 2 charts, found {docProperties.Count}");

            var ids = docProperties.Select(d => d.Id?.Value).Where(id => id.HasValue).Select(id => id!.Value).ToList();
            Assert.Equal(ids.Distinct().Count(), ids.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // =========================================================================
    // 5. OpenXML Schema Validation Integrity
    // =========================================================================

    [Fact]
    public void AdvancedLayouts_ComprehensiveDocument_PassesOpenXmlValidation()
    {
        var md = @"# Document Header

:::columns count=""2""
### Left Side
- Fast performance
- High reliability

===

### Right Side
<details>
<summary>Detailed Specs</summary>
Server CPU: 8 Cores
</details>
:::

<table>
  <thead>
    <tr><th colspan=""2"">Architecture Summary</th></tr>
  </thead>
  <tbody>
    <tr>
      <td rowspan=""2""><b>Components</b></td>
      <td><code>Service A</code> - <a href=""https://example.com/a"">Docs</a></td>
    </tr>
    <tr>
      <td><code>Service B</code> - <a href=""https://example.com/b"">Docs</a></td>
    </tr>
  </tbody>
</table>

:::chart type=""bar""
Metric, Value
Alpha, 100
Beta, 200
:::
";

        var (path, _) = ExportToXml(md);
        try
        {
            using var pkg = WordprocessingDocument.Open(path, false);
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(pkg).ToList();

            // Filter out known benign Office 2013 compatibility hints if any
            var criticalErrors = errors.Where(e => !e.Description.Contains("attribute 'http://schemas.microsoft.com/office/word/2012/wordml")).ToList();
            Assert.Empty(criticalErrors);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
