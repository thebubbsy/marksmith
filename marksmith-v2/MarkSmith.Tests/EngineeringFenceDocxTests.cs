using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Batch 14 (#69): the Cycle 22–29 engineering/science fences (49 visualizers) used to render only
// in the HTML preview — the DOCX export left raw :::fence text in the document (the biggest open
// MD_ENGINE_GOVERNANCE two-pipeline divergence). These tests pin the DOCX wiring: detection via the
// shared fence-name table, real image embedding (SVG + PNG parts), the code-fence exclusion, and
// preview parity.
public class EngineeringFenceDocxTests
{
    private const string DopplerFence = """
        :::doppler "Supersonic Concorde"
        mach: 2.0
        waves: 10
        :::
        """;

    private static string ExportToTempDocx(string md, AppSettings? settings = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-eng-fence-{Guid.NewGuid():N}.docx");
        new DocxExportService().ExportAsync(md, path, settings ?? new AppSettings()).GetAwaiter().GetResult();
        return path;
    }

    // ── Detection ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pipeline_Detects_EngineeringFence_As_EngineeringDiagram()
    {
        var pipeline = new AdvancedFeaturePipeline();
        var nodes = pipeline.Process($"# Mach Waves\n\n{DopplerFence}\n", "doc-eng");

        var node = Assert.Single(nodes);
        Assert.Equal("EngineeringDiagram", node.Detector.FeatureName);
    }

    [Fact]
    public void Pipeline_Ignores_EngineeringFence_Inside_CodeFence()
    {
        // The tokenizer must keep its code-fence exclusion: a :::doppler example inside a ```
        // block is documentation, not a diagram.
        var md = "# Example\n\n```\n" + DopplerFence + "\n```\n";
        var pipeline = new AdvancedFeaturePipeline();
        var nodes = pipeline.Process(md, "doc-eng-code");

        Assert.Empty(nodes);
    }

    [Fact]
    public void TryGetEngineeringFenceName_Rejects_LookaheadAlias_Prefixes()
    {
        // Preview semantics: :::filter matches, :::filter-x must not (filter(?![-\w]) alias).
        Assert.True(MarkdownHtmlService.TryGetEngineeringFenceName(":::filter\norder: 2\n:::", out var name));
        Assert.Equal("filter", name);
        Assert.False(MarkdownHtmlService.TryGetEngineeringFenceName(":::filter-x\norder: 2\n:::", out _));
        Assert.False(MarkdownHtmlService.TryGetEngineeringFenceName(":::notafence\nx: 1\n:::", out _));
    }

    // ── DOCX embedding ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Docx_Export_Embeds_EngineeringDiagram_As_Picture()
    {
        var path = ExportToTempDocx($"# Doppler\n\n{DopplerFence}\n");
        try
        {
            using var doc = WordprocessingDocument.Open(path, false);
            var main = doc.MainDocumentPart!;

            // Real picture: PNG raster part (+ the SVG part Word 2016+ renders crisply).
            Assert.Contains(main.ImageParts, p => p.ContentType == "image/png");
            Assert.Contains(main.Parts.Select(p => p.OpenXmlPart.ContentType),
                c => c == "image/svg+xml");

            string documentXml;
            using (var r = new StreamReader(main.GetStream()))
                documentXml = r.ReadToEnd();

            Assert.Contains("wp:inline", documentXml);        // inline picture frame
            Assert.DoesNotContain("mach: 2.0", documentXml);  // fence source must not leak as body text

            var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
            var errors = validator.Validate(doc)
                .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
                .ToList();
            Assert.Empty(errors);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Docx_Export_CodeFenced_Example_Keeps_Source_Text()
    {
        var md = "# Example\n\n```\n" + DopplerFence + "\n```\n";
        var path = ExportToTempDocx(md);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();

            // Inside a code fence the source is a listing: no picture, text preserved.
            Assert.DoesNotContain("wp:inline", xml);
            Assert.Contains("mach: 2.0", xml);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ── Preview parity (the new detector must not alter the preview path) ──────────────────

    [Fact]
    public void Preview_Still_Lifts_EngineeringFences_To_Svg()
    {
        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        var html = new MarkdownHtmlService().RenderCanvasOnly($"# Doppler\n\n{DopplerFence}\n", new AppSettings(), theme);

        Assert.NotNull(html);
        Assert.DoesNotContain(":::doppler", html);
        Assert.Contains("<svg", html);
    }
}
