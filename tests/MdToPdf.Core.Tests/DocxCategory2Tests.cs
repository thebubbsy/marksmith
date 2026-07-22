using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;
using W14 = DocumentFormat.OpenXml.Office2010.Word;

namespace MdToPdf.Core.Tests;

public class DocxCategory2Tests
{
    private static string ExportToTempDocx(string md, AppSettings? settings = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-test-c2-{Guid.NewGuid():N}.docx");
        new DocxExportService().ExportAsync(md, path, settings ?? new AppSettings()).GetAwaiter().GetResult();
        return path;
    }

    private static string ExportXml(string md, AppSettings? settings = null)
    {
        var path = ExportToTempDocx(md, settings);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml")!;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void M2_01_MathBlock_Justification_Precedes_Spacing()
    {
        var md = "$$\nx^2 + y^2 = z^2\n$$";
        var xml = ExportXml(md);
        int jcIndex = xml.IndexOf("w:jc");
        int spacingIndex = xml.IndexOf("w:spacing");
        Assert.True(jcIndex >= 0, "w:jc element should exist");
        Assert.True(spacingIndex >= 0, "w:spacing element should exist");
        Assert.True(jcIndex < spacingIndex, "w:jc must precede w:spacing in math block paragraph properties");
    }

    [Fact]
    public void M2_02_FootnoteLabel_InsertedInParagraph_AfterProperties()
    {
        var md = "Claim[^1]\n\n[^1]: Footnote details";
        var docPath = ExportToTempDocx(md);
        try
        {
            using var pkg = WordprocessingDocument.Open(docPath, false);
            var body = pkg.MainDocumentPart!.Document.Body!;
            var fnParagraphs = body.Descendants<W.Paragraph>().Where(p => p.InnerText.Contains("Footnote details")).ToList();
            Assert.NotEmpty(fnParagraphs);
            var fnPara = fnParagraphs.First();
            var labelRun = fnPara.ChildElements.OfType<W.Run>().FirstOrDefault(r => r.InnerText.StartsWith("["));
            Assert.NotNull(labelRun);
            Assert.Equal(fnPara, labelRun.Parent);
            if (fnPara.ParagraphProperties != null)
            {
                var children = fnPara.ChildElements.ToList();
                Assert.True(children.IndexOf(labelRun) > children.IndexOf(fnPara.ParagraphProperties),
                    "W.Run label must be inserted after W.ParagraphProperties, not inside it");
            }
        }
        finally { if (File.Exists(docPath)) File.Delete(docPath); }
    }

    [Fact]
    public async Task M2_03_ExportAppendAsync_Saves_NumberingDefinitionsPart()
    {
        var docPath = Path.Combine(Path.GetTempPath(), $"mk-append-test-{Guid.NewGuid():N}.docx");
        try
        {
            var service = new DocxExportService();
            var settings = new AppSettings();
            await service.ExportAsync("# Initial Document\n\n- Bullet 1", docPath, settings);
            await service.ExportAppendAsync("1. First ordered item\n2. Second ordered item", docPath, settings);

            using var pkg = WordprocessingDocument.Open(docPath, false);
            var numPart = pkg.MainDocumentPart?.NumberingDefinitionsPart;
            Assert.NotNull(numPart);
            Assert.NotNull(numPart.Numbering);
            var numInstances = numPart.Numbering.Elements<W.NumberingInstance>().ToList();
            Assert.True(numInstances.Count >= 2, "Append mode must save new NumberingInstance elements to NumberingDefinitionsPart");
        }
        finally { if (File.Exists(docPath)) File.Delete(docPath); }
    }

    [Fact]
    public void M2_04_TableCells_EndWith_Paragraph()
    {
        var md = "> [!NOTE]\n> Alert text\n\n| Col A | Col B |\n| --- | --- |\n| Cell 1 | Cell 2 |";
        var docPath = ExportToTempDocx(md);
        try
        {
            using var pkg = WordprocessingDocument.Open(docPath, false);
            var body = pkg.MainDocumentPart!.Document.Body!;
            var tableCells = body.Descendants<W.TableCell>().ToList();
            Assert.NotEmpty(tableCells);
            foreach (var cell in tableCells)
            {
                Assert.NotNull(cell.LastChild);
                Assert.IsType<W.Paragraph>(cell.LastChild);
            }
        }
        finally { if (File.Exists(docPath)) File.Delete(docPath); }
    }

    [Fact]
    public void M2_05_MdTag_Styles_And_Preserves_NoProof()
    {
        var md = "See <span class=\"md-tag\">#tag</span> and <span class=\"wikilink\">[[Page|<b>BoldPage</b>]]</span>";
        var docPath = ExportToTempDocx(md);
        try
        {
            using var pkg = WordprocessingDocument.Open(docPath, false);
            var body = pkg.MainDocumentPart!.Document.Body!;
            var runs = body.Descendants<W.Run>().ToList();

            var tagRun = runs.FirstOrDefault(r => r.InnerText.Contains("#tag"));
            Assert.NotNull(tagRun);
            Assert.NotNull(tagRun.RunProperties);
            Assert.NotNull(tagRun.RunProperties.GetFirstChild<W.NoProof>());

            var boldLinkRun = runs.FirstOrDefault(r => r.InnerText.Contains("BoldPage"));
            Assert.NotNull(boldLinkRun);
            Assert.NotNull(boldLinkRun.RunProperties);
            Assert.NotNull(boldLinkRun.RunProperties.GetFirstChild<W.NoProof>());
        }
        finally { if (File.Exists(docPath)) File.Delete(docPath); }
    }

    [Fact]
    public void M2_06_RenderChart_HandlesNullLabels_DisposesJsonDocument()
    {
        var service = new DocxExportService();
        var docPath = Path.Combine(Path.GetTempPath(), $"mk-chart-test-{Guid.NewGuid():N}.docx");
        try
        {
            var md = "```chart\nlabel,value\nA,10\nB,20\n```";
            var settings = new AppSettings();
            var ex = Record.Exception(() => service.ExportAsync(md, docPath, settings).GetAwaiter().GetResult());
            Assert.Null(ex);
        }
        finally { if (File.Exists(docPath)) File.Delete(docPath); }
    }

    [Fact]
    public void M2_07_RenderReferences_HandlesNullInnerContent()
    {
        var service = new DocxExportService();
        var docPath = Path.Combine(Path.GetTempPath(), $"mk-ref-test-{Guid.NewGuid():N}.docx");
        try
        {
            var md = "# References\n\nSome text.";
            var settings = new AppSettings();
            var ex = Record.Exception(() => service.ExportAsync(md, docPath, settings).GetAwaiter().GetResult());
            Assert.Null(ex);
        }
        finally { if (File.Exists(docPath)) File.Delete(docPath); }
    }

    [Fact]
    public void M2_08_RenderDatagrid_HandlesNullThemePrimary()
    {
        var service = new DocxExportService();
        var docPath = Path.Combine(Path.GetTempPath(), $"mk-grid-test-{Guid.NewGuid():N}.docx");
        try
        {
            var md = "| Header 1 | Header 2 |\n| --- | --- |\n| Val 1 | Val 2 |";
            var settings = new AppSettings { Theme = "Default" };
            var ex = Record.Exception(() => service.ExportAsync(md, docPath, settings).GetAwaiter().GetResult());
            Assert.Null(ex);
        }
        finally { if (File.Exists(docPath)) File.Delete(docPath); }
    }

    [Fact]
    public void M2_09_OpenType_W14_Properties_WrappedIn_AlternateContent()
    {
        var md = "Standard text run";
        var docPath = ExportToTempDocx(md);
        try
        {
            using var pkg = WordprocessingDocument.Open(docPath, false);
            var body = pkg.MainDocumentPart!.Document.Body!;
            var runs = body.Descendants<W.Run>().Where(r => r.InnerText.Contains("Standard")).ToList();
            Assert.NotEmpty(runs);
            var run = runs.First();
            Assert.NotNull(run.RunProperties);
            var altContent = run.RunProperties.Elements<AlternateContent>().FirstOrDefault();
            Assert.NotNull(altContent);
            var choice = altContent.Elements<AlternateContentChoice>().FirstOrDefault();
            Assert.NotNull(choice);
            Assert.Equal("w14", choice.Requires?.Value);
            Assert.NotNull(choice.Elements<W14.Ligatures>().FirstOrDefault());
        }
        finally { if (File.Exists(docPath)) File.Delete(docPath); }
    }

    [Fact]
    public void M2_10_TableColumnAlignment_AppliesTo_NestedBlockParagraphs()
    {
        var md = "| Center Col |\n| :---: |\n| > Nested quote text |";
        var docPath = ExportToTempDocx(md);
        try
        {
            using var pkg = WordprocessingDocument.Open(docPath, false);
            var body = pkg.MainDocumentPart!.Document.Body!;
            var table = body.Descendants<W.Table>().FirstOrDefault();
            Assert.NotNull(table);
            var tableCells = table.Descendants<W.TableCell>().ToList();
            Assert.NotEmpty(tableCells);
            var dataCell = tableCells.Last();
            var paragraphs = dataCell.Descendants<W.Paragraph>().ToList();
            Assert.NotEmpty(paragraphs);
            foreach (var p in paragraphs)
            {
                Assert.NotNull(p.ParagraphProperties);
                Assert.NotNull(p.ParagraphProperties.Justification);
                Assert.Equal(W.JustificationValues.Center, p.ParagraphProperties.Justification.Val?.Value);
            }
        }
        finally { if (File.Exists(docPath)) File.Delete(docPath); }
    }
}
