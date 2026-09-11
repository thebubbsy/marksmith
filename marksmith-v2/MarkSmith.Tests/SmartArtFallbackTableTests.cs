using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Tests;

// RenderSmartArtFallback (the plain-table renderer used when native SmartArt generation
// throws, e.g. an unresolvable :::workflow / :::timeline layout) built its TableBorders with
// the wrong CT_TblBorders child sequence (top, bottom, left, right instead of the schema's
// top, left, bottom, right, insideH, insideV) and was missing InsideVerticalBorder entirely.
// That's the same class of bug already fixed for TableCellMarginDefault elsewhere in this
// file (see the tblCellMar sequence fix); this pins the fallback table specifically.
public class SmartArtFallbackTableTests
{
    // RenderSmartArtFallback itself is private, so it must be invoked via reflection, but its
    // Ctx parameter type is `internal` and thus directly constructible here (MarkSmith.Tests is
    // an InternalsVisibleTo friend assembly).
    private static void InvokeRenderSmartArtFallback(FeatureNode node, OpenXmlCompositeElement target, DocxExportService.Ctx ctx)
    {
        var method = typeof(DocxExportService).GetMethod(
            "RenderSmartArtFallback", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, new object?[] { node, target, ctx });
    }

    private static DocxExportService.Ctx BuildMinimalCtx(MainDocumentPart mainPart)
    {
        var theme = new ThemeDefinition(
            "Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");

        return new DocxExportService.Ctx
        {
            MainPart = mainPart,
            Numbering = new W.Numbering(),
            Settings = new AppSettings(),
            Theme = theme,
            Alerts = new Dictionary<string, (string Color, string Icon)>(),
            LinkColor = "#2563EB",
            NoEmoji = false,
            AdvancedFeatures = new Dictionary<string, FeatureNode>()
        };
    }

    [Fact]
    public void RenderSmartArtFallback_Produces_SchemaValid_TableBorders()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-smartart-fallback-{System.Guid.NewGuid():N}.docx");
        try
        {
            using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                mainPart.Document = new W.Document(new W.Body());
                var body = mainPart.Document.Body!;

                var ctx = BuildMinimalCtx(mainPart);
                var node = new FeatureNode
                {
                    Block = new MarkdownBlock { RawText = ":::workflow\n:::", Start = 0, End = 0 },
                    Detector = new WorkflowDetector(),
                    StableId = "wf-1",
                    InnerContent = "- Design\n- Build\n- Ship"
                };

                InvokeRenderSmartArtFallback(node, body, ctx);
                mainPart.Document.Save();
            }

            using var readback = WordprocessingDocument.Open(path, false);
            var xml = readback.MainDocumentPart!.Document.OuterXml;

            // The fallback table must render (regression guard for the reflection plumbing itself).
            Assert.Contains("<w:tbl>", xml);

            var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
            var errors = validator.Validate(readback)
                .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
                .ToList();
            Assert.True(errors.Count == 0,
                "OpenXmlValidator errors: " + string.Join("; ", errors.Select(e => e.Description)));

            // InsideVerticalBorder must be present and every TableBorders child must appear in
            // the CT_TblBorders schema order: top, left, bottom, right, insideH, insideV.
            var table = readback.MainDocumentPart.Document.Body!.Descendants<W.Table>().Single();
            var borders = table.Descendants<W.TableProperties>().Single().TableBorders!;
            var order = borders.Elements().Select(e => e.LocalName).ToList();
            Assert.Equal(
                new[] { "top", "left", "bottom", "right", "insideH", "insideV" },
                order);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
