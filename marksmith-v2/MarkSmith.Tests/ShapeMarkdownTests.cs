using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.Composer;
using MarkSmith.Core.Services;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// :::shapes markdown encoding: codec round-trip, preview SVG transform, native-DrawingML DOCX.
public class ShapeMarkdownTests
{
    private static readonly string SampleBlock = ":::shapes\nellipse 1.0 0.5 0.9 0.7 FFD9B3\nheart 2.5 2.0 0.8 0.8 C0392B rot=15\n# comment\n:::\n";

    [Fact]
    public void Codec_RoundTrips()
    {
        var shapes = ShapeMarkdownCodec.Parse(SampleBlock);
        Assert.Equal(2, shapes.Count);

        Assert.Equal("ellipse", shapes[0].Prst);
        Assert.Equal(1.0, shapes[0].X);
        Assert.Equal(0.7, shapes[0].H);
        Assert.Equal("FFD9B3", shapes[0].Fill);

        Assert.Equal("heart", shapes[1].Prst);
        Assert.Equal(15, shapes[1].Rot);
        Assert.Equal("C0392B", shapes[1].Fill);

        // round-trip: serialize -> parse must preserve geometry
        var again = ShapeMarkdownCodec.Parse(ShapeMarkdownCodec.Serialize(shapes));
        Assert.Equal(shapes.Count, again.Count);
        Assert.Equal(shapes[0].Prst, again[0].Prst);
        Assert.Equal(shapes[1].Rot, again[1].Rot);
    }

    [Fact]
    public void PreviewTransform_EmitsSvg()
    {
        string html = ShapeMarkdownHtml.PreTransform(SampleBlock);
        Assert.Contains("<svg", html);
        Assert.Contains("#FFD9B3", html);
        Assert.DoesNotContain(":::shapes", html); // block consumed
    }

    [Fact]
    public void DocxExport_EmbedsNativeShapes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shapes-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(SampleBlock, path, new Models.AppSettings()).GetAwaiter().GetResult();

            using var doc = WordprocessingDocument.Open(path, false);
            var main = doc.MainDocumentPart!;
            string documentXml;
            using (var r = new StreamReader(main.GetStream()))
            {
                documentXml = r.ReadToEnd();
            }

            Assert.Contains("wordprocessingGroup", documentXml);
            Assert.Contains("wps:wsp", documentXml);
            Assert.Contains("ellipse", documentXml);
            Assert.Contains("heart", documentXml);

            var validator = new OpenXmlValidator();
            var errors = validator.Validate(doc).ToList();
            Assert.Empty(errors);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
