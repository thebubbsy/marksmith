using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MdToPdf.Core.Kanban;
using MdToPdf.Models;
using MdToPdf.Services;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace MdToPdf.Core.Tests;

public class KanbanChallenger1SmartArtTests
{
    private static readonly XNamespace dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private static readonly XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";

    #region 1. Single Column Boundary Conditions

    [Fact]
    public void Boundary_SingleColumn_ZeroCards_BuildsValidDataModelAndDocx()
    {
        var blockText = ":::kanban title=\"Single Column Zero Cards\"\n# Only Column\n:::";
        var kanban = KanbanParser.Parse(blockText);
        Assert.Single(kanban.Columns);
        Assert.Empty(kanban.Columns[0].Cards);

        // Verify DataModel XML
        var doc = SmartArtKanbanBuilder.CreateDataModel(kanban);
        Assert.NotNull(doc.Root);

        var pts = doc.Root.Element(dgm + "ptLst")?.Elements(dgm + "pt").ToList();
        Assert.NotNull(pts);
        Assert.Equal(2, pts.Count); // Root doc node (modelId=1) + 1 column node (modelId=2)

        var cxns = doc.Root.Element(dgm + "cxnLst")?.Elements(dgm + "cxn").ToList();
        Assert.NotNull(cxns);
        Assert.Single(cxns); // Root -> Column 1 connection

        // Verify DOCX export and OpenXML validation
        VerifyDocxExportAndValidation(kanban, forceFallback: false);
        VerifyDocxExportAndValidation(kanban, forceFallback: true);
    }

    [Fact]
    public void Boundary_SingleColumn_MultipleCards_BuildsValidDataModelAndDocx()
    {
        var blockText = ":::kanban title=\"Single Column 10 Cards\"\n# Lone Column\n- Card 1\n- Card 2\n- Card 3\n- Card 4\n- Card 5\n- Card 6\n- Card 7\n- Card 8\n- Card 9\n- Card 10\n:::";
        var kanban = KanbanParser.Parse(blockText);
        Assert.Single(kanban.Columns);
        Assert.Equal(10, kanban.Columns[0].Cards.Count);

        var doc = SmartArtKanbanBuilder.CreateDataModel(kanban);
        var pts = doc.Root?.Element(dgm + "ptLst")?.Elements(dgm + "pt").ToList();
        Assert.NotNull(pts);
        Assert.Equal(1 + 1 + 10, pts.Count); // 1 doc + 1 col + 10 cards

        var cxns = doc.Root?.Element(dgm + "cxnLst")?.Elements(dgm + "cxn").ToList();
        Assert.NotNull(cxns);
        Assert.Equal(1 + 10, cxns.Count); // 1 root->col + 10 col->card

        VerifyDocxExportAndValidation(kanban, forceFallback: false);
        VerifyDocxExportAndValidation(kanban, forceFallback: true);
    }

    #endregion

    #region 2. 20 Columns Boundary Conditions

    [Fact]
    public void Boundary_TwentyColumns_BuildsValidDataModelAndDocx()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(":::kanban title=\"Twenty Columns Board\"");
        for (int i = 1; i <= 20; i++)
        {
            sb.AppendLine($"# Column {i}");
            sb.AppendLine($"- [ ] Task A in Col {i}");
            sb.AppendLine($"- [x] Task B in Col {i}");
        }
        sb.AppendLine(":::");

        var kanban = KanbanParser.Parse(sb.ToString());
        Assert.Equal(20, kanban.Columns.Count);
        Assert.Equal(40, kanban.Columns.Sum(c => c.Cards.Count));

        // Verify DataModel XML structure for 20 columns + 40 cards
        var doc = SmartArtKanbanBuilder.CreateDataModel(kanban);
        var pts = doc.Root?.Element(dgm + "ptLst")?.Elements(dgm + "pt").ToList();
        Assert.NotNull(pts);
        Assert.Equal(1 + 20 + 40, pts.Count); // 1 root doc + 20 cols + 40 cards

        var cxns = doc.Root?.Element(dgm + "cxnLst")?.Elements(dgm + "cxn").ToList();
        Assert.NotNull(cxns);
        Assert.Equal(20 + 40, cxns.Count); // 20 root->col + 40 col->card connections

        // Test SmartArt mode export & validation
        VerifyDocxExportAndValidation(kanban, forceFallback: false);

        // Test Fallback mode export & validation
        VerifyDocxExportAndValidation(kanban, forceFallback: true);

        // Test Fallback diagram geometry calculations
        var theme = new ThemeCatalog().GetOrDefault("Default");
        var diagram = SmartArtKanbanBuilder.BuildKanbanDiagram(kanban, theme);
        Assert.Equal(460, diagram.Width);
        Assert.True(diagram.Shapes.Count >= 60); // 20 header shapes + 40 card shapes
    }

    #endregion

    #region 3. Special XML Characters & Escaping

    [Fact]
    public void SpecialCharacters_XML_Quotes_Ampersands_HandledSafelyInSmartArtAndFallback()
    {
        var blockText = ":::kanban title=\"Board & <XmlTest> 'Quotes' & \\\"DoubleQuotes\\\"\"\n" +
                        "# Column <1> & \"Header\" 'Sub'\n" +
                        "- Card <A> & \"Test\" 'Single' <script>alert('xss')</script>\n" +
                        "- [x] Card <B> & \"Done\" 'Single' <iframe src=\"javascript:void(0)\"></iframe>\n" +
                        ":::";

        var kanban = KanbanParser.Parse(blockText);
        Assert.Single(kanban.Columns);
        Assert.Equal(2, kanban.Columns[0].Cards.Count);

        // Test DataModel XML creation does not throw XML syntax exception
        var doc = SmartArtKanbanBuilder.CreateDataModel(kanban);
        Assert.NotNull(doc.Root);

        // Read text elements from DataModel XML
        var texts = doc.Root.Descendants(a + "t").Select(t => t.Value).ToList();
        Assert.Contains("Column <1> & \"Header\" 'Sub'", texts);
        Assert.Contains("Card <A> & \"Test\" 'Single' <script>alert('xss')</script>", texts);
        Assert.Contains("☑ Card <B> & \"Done\" 'Single' <iframe src=\"javascript:void(0)\"></iframe>", texts);

        // Verify exported DOCX files validate with OpenXmlValidator
        VerifyDocxExportAndValidation(kanban, forceFallback: false);
        VerifyDocxExportAndValidation(kanban, forceFallback: true);
    }

    #endregion

    #region 4. Checkbox States Verification

    [Fact]
    public void CheckboxStates_Prefixes_RenderedCorrectlyInSmartArtAndFallback()
    {
        var blockText = ":::kanban title=\"Checkbox Board\"\n" +
                        "# Checkbox Column\n" +
                        "- [ ] Unchecked item\n" +
                        "- [x] Checked item lowercase\n" +
                        "- [X] Checked item uppercase\n" +
                        "- Plain item without checkbox\n" +
                        ":::";

        var kanban = KanbanParser.Parse(blockText);
        Assert.Single(kanban.Columns);
        var cards = kanban.Columns[0].Cards;
        Assert.Equal(4, cards.Count);

        Assert.False(cards[0].IsCompleted);
        Assert.True(cards[1].IsCompleted);
        Assert.True(cards[2].IsCompleted);
        Assert.Null(cards[3].IsCompleted);

        // Check SmartArt DataModel prefix rendering
        var doc = SmartArtKanbanBuilder.CreateDataModel(kanban);
        var texts = doc.Root?.Descendants(a + "t").Select(t => t.Value).ToList();
        Assert.NotNull(texts);

        Assert.Contains("☐ Unchecked item", texts);
        Assert.Contains("☑ Checked item lowercase", texts);
        Assert.Contains("☑ Checked item uppercase", texts);
        Assert.Contains("Plain item without checkbox", texts);

        // Check Fallback diagram MShape.Text prefix rendering
        var theme = new ThemeCatalog().GetOrDefault("Default");
        var diagram = SmartArtKanbanBuilder.BuildKanbanDiagram(kanban, theme);
        var shapeTexts = diagram.Shapes.Select(s => s.Text).ToList();

        Assert.Contains("☐ Unchecked item", shapeTexts);
        Assert.Contains("☑ Checked item lowercase", shapeTexts);
        Assert.Contains("☑ Checked item uppercase", shapeTexts);
        Assert.Contains("Plain item without checkbox", shapeTexts);
    }

    #endregion

    #region 5. Forced Fallback Execution & Automatic Error Fallback

    [Fact]
    public void ForcedFallback_True_DoesNotAddSmartArtParts()
    {
        var blockText = ":::kanban title=\"Fallback Test\"\n# Col 1\n- Task 1\n:::";
        var kanban = KanbanParser.Parse(blockText);
        var tempPath = Path.Combine(Path.GetTempPath(), $"kanban_forced_fallback_{Guid.NewGuid():N}.docx");

        try
        {
            using (var doc = WordprocessingDocument.Create(tempPath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                mainPart.Document = new W.Document(new W.Body());
                uint docPrId = 1;
                var theme = new ThemeCatalog().GetOrDefault("Default");

                SmartArtKanbanBuilder.BuildKanban(kanban, mainPart, mainPart.Document.Body, theme, ref docPrId, forceFallback: true);
                mainPart.Document.Save();
            }

            using var readDoc = WordprocessingDocument.Open(tempPath, false);
            var main = readDoc.MainDocumentPart;
            Assert.NotNull(main);

            Assert.Empty(main.DiagramDataParts);
            Assert.Empty(main.DiagramLayoutDefinitionParts);
            Assert.Empty(main.DiagramColorsParts);
            Assert.Empty(main.DiagramStyleParts);

            var validator = new OpenXmlValidator();
            var errors = validator.Validate(readDoc).Where(e => !e.Description.Contains("w14") && !e.Description.Contains("w15")).ToList();
            Assert.Empty(errors);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void AutomaticFallback_OccursWhenSmartArtDiagramFails()
    {
        var blockText = ":::kanban title=\"Auto Fallback Test\"\n# Col 1\n- Task 1\n:::";
        var kanban = KanbanParser.Parse(blockText);
        var tempPath = Path.Combine(Path.GetTempPath(), $"kanban_auto_fallback_{Guid.NewGuid():N}.docx");

        try
        {
            using (var doc = WordprocessingDocument.Create(tempPath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                mainPart.Document = new W.Document(new W.Body());
                uint docPrId = 10;
                var theme = new ThemeCatalog().GetOrDefault("Default");

                SmartArtKanbanBuilder.BuildKanban(kanban, mainPart, mainPart.Document.Body, theme, ref docPrId, forceFallback: false);
                mainPart.Document.Save();
            }

            using var readDoc = WordprocessingDocument.Open(tempPath, false);
            var main = readDoc.MainDocumentPart;
            Assert.NotNull(main);

            // In normal operation without error, SmartArt parts are added
            Assert.NotEmpty(main.DiagramDataParts);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    #endregion

    #region Helper Verification Methods

    private static void VerifyDocxExportAndValidation(KanbanBlock kanban, bool forceFallback)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"kanban_verify_{Guid.NewGuid():N}.docx");
        try
        {
            using (var doc = WordprocessingDocument.Create(tempPath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                var body = new W.Body();
                mainPart.Document = new W.Document(body);

                uint docPrId = 100;
                var theme = new ThemeCatalog().GetOrDefault("Default");

                SmartArtKanbanBuilder.BuildKanban(kanban, mainPart, body, theme, ref docPrId, forceFallback: forceFallback);
                mainPart.Document.Save();
            }

            Assert.True(File.Exists(tempPath));

            using (var doc = WordprocessingDocument.Open(tempPath, false))
            {
                var validator = new OpenXmlValidator();
                var errors = validator.Validate(doc)
                    .Where(e => !e.Description.Contains("w14") && !e.Description.Contains("w15"))
                    .ToList();

                if (errors.Count > 0)
                {
                    var msg = string.Join("\n", errors.Select(e => $"{e.Part?.Uri} | Node: {e.Node?.LocalName} | {e.Description}"));
                    Assert.Fail($"OpenXmlValidator failed (forceFallback={forceFallback}):\n{msg}");
                }

                if (!forceFallback)
                {
                    Assert.NotEmpty(doc.MainDocumentPart!.DiagramDataParts);
                    Assert.NotEmpty(doc.MainDocumentPart!.DiagramLayoutDefinitionParts);
                    Assert.NotEmpty(doc.MainDocumentPart!.DiagramColorsParts);
                    Assert.NotEmpty(doc.MainDocumentPart!.DiagramStyleParts);
                }
                else
                {
                    Assert.Empty(doc.MainDocumentPart!.DiagramDataParts);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    #endregion
}
