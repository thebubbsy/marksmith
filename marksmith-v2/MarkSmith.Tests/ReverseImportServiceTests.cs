using System.IO;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MdToPdf.Core.Tests;

// Tests for the Smart Dual-Mode DOCX engine (ReverseImportService):
//   Tier 1 — a Marksmith-made .docx carries its source and reopens byte-for-byte (with staleness).
//   Tier 2 — ANY .docx is parsed by the Universal Engine: images extracted, headings/lists generalized,
//            shapes recovered as Mermaid or rasterized, quotes/code detected.
public class ReverseImportServiceTests
{
    // OOXML namespace URIs used when injecting raw DrawingML into test paragraphs.
    private const string NsW = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string NsWp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsPic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsWps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private const string NsWpg = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";

    // A 1x1 PNG (valid, decodable) used for the image-extraction test.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    // ============================================================================================
    // Tier 1 — embedded source
    // ============================================================================================

    [Fact]
    public async Task Embedded_round_trips_byte_for_byte()
    {
        var md = "# Lossless Round Trip\n\n" +
                 "Inline math $E=mc^2$ and a diagram:\n\n" +
                 "```mermaid\nflowchart TD\n    A[Start] --> B[End]\n```\n\n" +
                 "| H1 | H2 |\n| --- | --- |\n| a | b |\n";
        var dir = Directory.CreateTempSubdirectory().FullName;
        var docx = Path.Combine(dir, "lossless.docx");

        await new DocxExportService().ExportAsync(md, docx, new AppSettings());
        var result = await new ReverseImportService().ImportFromDocxAsync(docx);

        Assert.Equal(ImportTier.EmbeddedSource, result.Tier);
        Assert.False(result.IsStale);
        Assert.Null(result.Warning);
        Assert.Equal(md, result.Markdown); // byte-for-byte: the embedded source IS the input
    }

    [Fact]
    public async Task No_marker_uses_universal_engine()
    {
        var md = "# Universal Fallback\n\nSome body text here.\n";
        var dir = Directory.CreateTempSubdirectory().FullName;
        var docx = Path.Combine(dir, "nomarker.docx");

        await new DocxExportService().ExportAsync(md, docx, new AppSettings());
        StripSourcePart(docx); // simulate a non-Marksmith generator

        var result = await new ReverseImportService().ImportFromDocxAsync(docx);

        Assert.Equal(ImportTier.UniversalEngine, result.Tier);
        Assert.False(string.IsNullOrWhiteSpace(result.Markdown));
        Assert.Contains("Universal Fallback", result.Markdown);
        Assert.Contains("Some body text here.", result.Markdown);
    }

    [Fact]
    public async Task Staleness_flag_set_when_modified_after_export()
    {
        var md = "# Staleness Check\n\nContent.\n";
        var dir = Directory.CreateTempSubdirectory().FullName;
        var docx = Path.Combine(dir, "stale.docx");

        await new DocxExportService().ExportAsync(md, docx, new AppSettings());
        BumpModified(docx, TimeSpan.FromDays(1)); // edited in Word after export

        var result = await new ReverseImportService().ImportFromDocxAsync(docx);

        Assert.Equal(ImportTier.EmbeddedSource, result.Tier);
        Assert.True(result.IsStale);
        Assert.NotNull(result.Warning);
    }

    // ============================================================================================
    // Tier 2 — Universal Engine
    // ============================================================================================

    [Fact]
    public async Task Universal_extracts_images()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var docx = Path.Combine(dir, "withimage.docx");
        var mediaDir = Path.Combine(dir, "media_out");
        BuildDocxWithImage(docx, TinyPng, alt: "test image");

        var result = await new ReverseImportService().ImportFromDocxAsync(docx, mediaDir);

        Assert.Equal(ImportTier.UniversalEngine, result.Tier);
        Assert.Contains("![", result.Markdown);
        Assert.Contains("test image", result.Markdown);
        // The extracted file must exist on disk under the requested media directory.
        var extracted = Path.Combine(mediaDir, "media", "image1.png");
        Assert.True(File.Exists(extracted), "expected extracted image at " + extracted);
        Assert.Contains(result.ExtractedMedia, m => m.EndsWith("image1.png"));
    }

    [Fact]
    public async Task Universal_detects_headings_and_lists()
    {
        var md = "# Top Heading\n\n## Sub Heading\n\n- bullet one\n- bullet two\n\n1. first\n2. second\n";
        var dir = Directory.CreateTempSubdirectory().FullName;
        var docx = Path.Combine(dir, "headings.docx");

        await new DocxExportService().ExportAsync(md, docx, new AppSettings());
        StripSourcePart(docx); // force the Universal Engine

        var result = await new ReverseImportService().ImportFromDocxAsync(docx);

        Assert.Equal(ImportTier.UniversalEngine, result.Tier);
        Assert.Contains("# Top Heading", result.Markdown);
        Assert.Contains("## Sub Heading", result.Markdown);
        Assert.Contains("- bullet one", result.Markdown);
        Assert.Contains("- bullet two", result.Markdown);
        Assert.Contains("1. first", result.Markdown);
        Assert.Contains("2. second", result.Markdown);
    }

    [Fact]
    public async Task Universal_converts_simple_shapes_to_mermaid()
    {
        var md = "# Diagram\n\n```mermaid\nflowchart TD\n    A[Start] --> B[End]\n```\n";
        var dir = Directory.CreateTempSubdirectory().FullName;
        var docx = Path.Combine(dir, "shapes.docx");

        await new DocxExportService().ExportAsync(md, docx, new AppSettings());
        StripSourcePart(docx); // force recovery from the shapes themselves

        var result = await new ReverseImportService().ImportFromDocxAsync(docx);

        Assert.Equal(ImportTier.UniversalEngine, result.Tier);
        Assert.Contains("```mermaid", result.Markdown);
        Assert.Contains("Start", result.Markdown);
        Assert.Contains("End", result.Markdown);
    }

    [Fact]
    public async Task Universal_raster_fallback_for_complex_shapes()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var docx = Path.Combine(dir, "complex.docx");
        var mediaDir = Path.Combine(dir, "media_out");
        BuildDocxWithFreeformShape(docx); // custom geometry: no tags, no connectors, no preset

        var result = await new ReverseImportService().ImportFromDocxAsync(docx, mediaDir);

        Assert.Equal(ImportTier.UniversalEngine, result.Tier);
        // Content must survive — either as a rasterized image or, if rasterization is unavailable in
        // the environment, as an explicit placeholder comment. Never silently dropped, never throws.
        Assert.Contains("Diagram", result.Markdown);
    }

    [Fact]
    public async Task Universal_handles_blockquote_and_code()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var docx = Path.Combine(dir, "quotecode.docx");

        var quote = new W.Paragraph(
            new W.ParagraphProperties(new W.ParagraphStyleId { Val = "IntenseQuote" }),
            new W.Run(new W.Text("quoted wisdom")));
        var code = new W.Paragraph(
            new W.ParagraphProperties(new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = "F0F0F0" }),
            new W.Run(new W.RunProperties(new W.RunFonts { Ascii = "Consolas" }), new W.Text("var x = 1;")));
        BuildDocx(docx, quote, code);

        var result = await new ReverseImportService().ImportFromDocxAsync(docx);

        Assert.Equal(ImportTier.UniversalEngine, result.Tier);
        Assert.Contains("> quoted wisdom", result.Markdown);
        Assert.Contains("```", result.Markdown);
        Assert.Contains("var x = 1;", result.Markdown);
    }

    // ============================================================================================
    // helpers
    // ============================================================================================

    // Builds a minimal valid .docx whose body is the given paragraphs.
    private static void BuildDocx(string path, params W.Paragraph[] paragraphs)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        var body = new W.Body();
        foreach (var p in paragraphs) body.Append(p);
        main.Document = new W.Document(body);
        main.Document.Save();
    }

    // Builds a .docx containing a single standalone picture paragraph referencing an embedded PNG.
    private static void BuildDocxWithImage(string path, byte[] png, string alt)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        var imagePart = main.AddImagePart(ImagePartType.Png);
        using (var s = imagePart.GetStream(FileMode.Create)) s.Write(png, 0, png.Length);
        var relId = main.GetIdOfPart(imagePart);

        var drawing =
            "<w:drawing>" +
              "<wp:inline xmlns:wp=\"" + NsWp + "\">" +
                "<wp:docPr id=\"1\" name=\"Picture\" descr=\"" + alt + "\"/>" +
                "<a:graphic xmlns:a=\"" + NsA + "\">" +
                  "<a:graphicData uri=\"" + NsPic + "\">" +
                    "<pic:pic xmlns:pic=\"" + NsPic + "\">" +
                      "<pic:blipFill><a:blip r:embed=\"" + relId + "\" xmlns:r=\"" + NsR + "\"/></pic:blipFill>" +
                    "</pic:pic>" +
                  "</a:graphicData>" +
                "</a:graphic>" +
              "</wp:inline>" +
            "</w:drawing>";

        var p = new W.Paragraph();
        p.InnerXml = "<w:r xmlns:w=\"" + NsW + "\">" + drawing + "</w:r>";

        var body = new W.Body(p);
        main.Document = new W.Document(body);
        main.Document.Save();
    }

    // Builds a .docx with a single shape group whose only shape uses CUSTOM geometry (no preset, no
    // identity tags, no connectors) — the case the Mermaid recoverer cannot handle, forcing raster.
    private static void BuildDocxWithFreeformShape(string path)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();

        var drawing =
            "<w:drawing>" +
              "<wp:inline xmlns:wp=\"" + NsWp + "\">" +
                "<wp:docPr id=\"2\" name=\"FreeForm\"/>" +
                "<a:graphic xmlns:a=\"" + NsA + "\">" +
                  "<a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\">" +
                    "<wpg:wgp xmlns:wpg=\"" + NsWpg + "\">" +
                      "<wps:wsp xmlns:wps=\"" + NsWps + "\">" +
                        "<wps:cNvPr id=\"1\" name=\"FreeForm\"/>" +
                        "<wps:spPr>" +
                          "<a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm>" +
                          "<a:custGeom><a:pathLst><a:path w=\"100\" h=\"100\">" +
                            "<a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo>" +
                            "<a:lnTo><a:pt x=\"100\" y=\"100\"/></a:lnTo>" +
                          "</a:path></a:pathLst></a:custGeom>" +
                        "</wps:spPr>" +
                        "<wps:txbx><w:txbxContent xmlns:w=\"" + NsW + "\"><w:p><w:r><w:t>Box</w:t></w:r></w:p></w:txbxContent></wps:txbx>" +
                      "</wps:wsp>" +
                    "</wpg:wgp>" +
                  "</a:graphicData>" +
                "</a:graphic>" +
              "</wp:inline>" +
            "</w:drawing>";

        var p = new W.Paragraph();
        p.InnerXml = "<w:r xmlns:w=\"" + NsW + "\">" + drawing + "</w:r>";

        var body = new W.Body(p);
        main.Document = new W.Document(body);
        main.Document.Save();
    }

    // Removes the embedded marksmith-source custom-XML part so a Marksmith file imports as if foreign.
    private static void StripSourcePart(string docxPath)
    {
        using var package = WordprocessingDocument.Open(docxPath, true);
        var main = package.MainDocumentPart!;
        foreach (var part in main.CustomXmlParts.ToList())
        {
            try
            {
                using var s = part.GetStream(FileMode.Open, FileAccess.Read);
                var root = XDocument.Load(s).Root;
                if (root?.Name.LocalName == "marksmithSource") main.DeletePart(part);
            }
            catch { /* ignore unreadable custom parts */ }
        }
        main.Document.Save();
    }

    // Sets the package Modified timestamp (simulates an edit in Word after export).
    private static void BumpModified(string docxPath, TimeSpan delta)
    {
        using var package = WordprocessingDocument.Open(docxPath, true);
        package.PackageProperties.Modified = DateTime.UtcNow.Add(delta);
    }
}
