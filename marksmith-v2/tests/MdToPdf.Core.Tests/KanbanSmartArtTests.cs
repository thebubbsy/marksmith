using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using MdToPdf.Core.Kanban;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class KanbanSmartArtTests
{
    [Fact]
    public void KanbanSmartArt_CreateDataModel_PopulatesColumnsAndCardsCorrectly()
    {
        var blockText = @":::kanban title=""Sprint Kanban""
# To Do
- [ ] Task A #urgent
- [ ] Task B
# In Progress
- [x] Task C #in-review
# Done
- [x] Task D
:::";

        var kanban = KanbanParser.Parse(blockText);
        XDocument doc = SmartArtKanbanBuilder.CreateDataModel(kanban);

        Assert.NotNull(doc.Root);
        XNamespace dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";

        var pts = doc.Root.Element(dgm + "ptLst")?.Elements(dgm + "pt").ToList();
        Assert.NotNull(pts);
        Assert.True(pts.Count >= 1 + 3 + 4); // Root + 3 columns + 4 cards

        // Check root point
        var rootPt = pts.FirstOrDefault(p => p.Attribute("modelId")?.Value == "1");
        Assert.NotNull(rootPt);
        Assert.Equal("doc", rootPt.Attribute("type")?.Value);

        // Check Column titles in text nodes
        var texts = pts.SelectMany(p => p.Descendants(a + "t")).Select(t => t.Value).ToList();
        Assert.Contains("To Do", texts);
        Assert.Contains("In Progress", texts);
        Assert.Contains("Done", texts);

        // Check Card text with tags and checkboxes
        Assert.Contains("☐ Task A #urgent", texts);
        Assert.Contains("☑ Task C #in-review", texts);

        // Check Connection List
        var cxns = doc.Root.Element(dgm + "cxnLst")?.Elements(dgm + "cxn").ToList();
        Assert.NotNull(cxns);
        Assert.NotEmpty(cxns);
        Assert.All(cxns, c => Assert.Equal("parOf", c.Attribute("type")?.Value));
    }

    [Fact]
    public void KanbanSmartArt_InjectsAllSmartArtParts_WhenExportedToDocx()
    {
        var exporter = new DocxExportService();
        var markdown = @":::kanban title=""Native Word Kanban Board""
# Backlog
- Task 1
- Task 2
# Doing
- Task 3
# Done
- Task 4
:::";

        var tempPath = Path.Combine(Path.GetTempPath(), $"kanban_smartart_{Guid.NewGuid():N}.docx");
        try
        {
            exporter.ExportAsync(markdown, tempPath, new AppSettings()).GetAwaiter().GetResult();
            Assert.True(File.Exists(tempPath));

            using var doc = WordprocessingDocument.Open(tempPath, false);
            var mainPart = doc.MainDocumentPart;
            Assert.NotNull(mainPart);

            Assert.NotEmpty(mainPart.DiagramDataParts);
            Assert.NotEmpty(mainPart.DiagramLayoutDefinitionParts);
            Assert.NotEmpty(mainPart.DiagramColorsParts);
            Assert.NotEmpty(mainPart.DiagramStyleParts);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void KanbanSmartArt_DocumentXml_IsValidWithOpenXmlValidator()
    {
        var exporter = new DocxExportService();
        var markdown = @":::kanban title=""Validation Board""
# Column 1
- Item 1
# Column 2
- Item 2
:::";

        var tempPath = Path.Combine(Path.GetTempPath(), $"kanban_val_{Guid.NewGuid():N}.docx");
        try
        {
            exporter.ExportAsync(markdown, tempPath, new AppSettings()).GetAwaiter().GetResult();
            Assert.True(File.Exists(tempPath));

            using var doc = WordprocessingDocument.Open(tempPath, false);
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(doc).ToList();

            if (errors.Count > 0)
            {
                var msg = string.Join("\n", errors.Select(e => $"Part: {e.Part?.Uri}, Node: {e.Node?.LocalName}, Error: {e.Description}"));
                Assert.Fail($"Validation failed with {errors.Count} errors:\n{msg}");
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void KanbanSmartArt_FallbackToShapes_WhenForceFallbackIsTrue()
    {
        var blockText = @":::kanban title=""Fallback Board""
# Column 1
- Task 1
# Column 2
- Task 2
:::";

        var kanban = KanbanParser.Parse(blockText);
        var tempPath = Path.Combine(Path.GetTempPath(), $"kanban_fallback_{Guid.NewGuid():N}.docx");

        try
        {
            using (var doc = WordprocessingDocument.Create(tempPath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body());

                uint docPrId = 1;
                var theme = new ThemeCatalog().GetOrDefault("Default");

                SmartArtKanbanBuilder.BuildKanban(kanban, mainPart, mainPart.Document.Body, theme, ref docPrId, forceFallback: true);
                mainPart.Document.Save();
            }

            Assert.True(File.Exists(tempPath));
            using var readDoc = WordprocessingDocument.Open(tempPath, false);
            var main = readDoc.MainDocumentPart;
            Assert.NotNull(main);
            // In fallback mode, no DiagramDataParts are created
            Assert.Empty(main.DiagramDataParts);
            Assert.NotNull(main.Document.Body?.FirstChild);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void SmartArtKanbanBuilder_OpenXml_Schema_Validation_Passes()
    {
        var rawBlock = @":::kanban
# Backlog
- Task 1
# Done
- Task 2
:::";
        var kanban = KanbanParser.Parse(rawBlock);
        var theme = new ThemeCatalog().GetOrDefault("Default");

        var testDocPath = Path.Combine(Path.GetTempPath(), $"kanban_schema_val_{Guid.NewGuid():N}.docx");
        try
        {
            using (var doc = WordprocessingDocument.Create(testDocPath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new W.Body();
                main.Document = new W.Document(body);

                uint docPrId = 500;
                SmartArtKanbanBuilder.BuildKanban(kanban, main, body, theme, ref docPrId, forceFallback: false);

                main.Document.Save();
            }

            using (var doc = WordprocessingDocument.Open(testDocPath, false))
            {
                var validator = new OpenXmlValidator();
                var errors = validator.Validate(doc).ToList();

                var criticalErrors = errors.Where(e => !e.Description.Contains("w14") && !e.Description.Contains("w15")).ToList();
                if (criticalErrors.Count > 0)
                {
                    var msg = string.Join("\n", criticalErrors.Select(e => $"{e.Part?.Uri} | Node: {e.Node?.LocalName} | {e.Description}"));
                    Assert.Fail($"OpenXmlValidator failed with errors:\n{msg}");
                }
            }
        }
        finally
        {
            if (File.Exists(testDocPath)) File.Delete(testDocPath);
        }
    }

    [Fact]
    public void Generate_Sample_Kanban_Docx()
    {
        var markdown = @"# Executive Project Kanban Board

:::kanban title=""Q3 Product Roadmap""
# Backlog
- [ ] Design System Refactoring #design #v2
- [ ] API Rate Limiting Infrastructure #backend
- [ ] Security Audit Compliance #security

# In Progress
- [ ] SmartArt Kanban Generation #docx #openxml
- [x] Mermaid Diagram Shape Forge Engine #feature

# Review
- [ ] Code Review for Pull Request #42 #pr
- [x] QA Integration Suite #testing

# Done
- [x] Milestone 1 Core Parser #complete
- [x] Milestone 2 PDF Renderer #complete
:::
";

        var currentDir = Directory.GetCurrentDirectory();
        var repoRoot = currentDir;
        while (!string.IsNullOrEmpty(repoRoot) && !File.Exists(Path.Combine(repoRoot, "MARKSMITH_AI_CONTEXT.md")))
        {
            var parent = Directory.GetParent(repoRoot)?.FullName;
            if (parent == repoRoot) break;
            repoRoot = parent;
        }

        if (string.IsNullOrEmpty(repoRoot) || !File.Exists(Path.Combine(repoRoot, "MARKSMITH_AI_CONTEXT.md")))
        {
            repoRoot = currentDir;
        }

        var primaryPath = Path.Combine(repoRoot, "test_outputs", "sample_kanban.docx");
        var exporter = new DocxExportService();
        Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);

        exporter.ExportAsync(markdown, primaryPath, new AppSettings()).GetAwaiter().GetResult();

        Assert.True(File.Exists(primaryPath), $"Expected output file at {primaryPath}");
        var fileInfo = new FileInfo(primaryPath);
        Assert.True(fileInfo.Length > 0, "Generated file should not be empty");

        using var doc = WordprocessingDocument.Open(primaryPath, false);
        var mainPart = doc.MainDocumentPart;
        Assert.NotNull(mainPart);
        Assert.NotEmpty(mainPart.DiagramDataParts);
        Assert.NotEmpty(mainPart.DiagramLayoutDefinitionParts);
        Assert.NotEmpty(mainPart.DiagramColorsParts);
        Assert.NotEmpty(mainPart.DiagramStyleParts);
    }
}
