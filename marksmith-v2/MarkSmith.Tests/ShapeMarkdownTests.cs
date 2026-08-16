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

    private static string MakeTestPng(string path, int w = 24, int h = 24)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            bmp.SetPixel(x, y, new SkiaSharp.SKColor((byte)(x * 255 / w), (byte)(y * 255 / h), 128));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

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
    public void Sketch_ProducesCurvedStrokes_AsCustGeomPolylines()
    {
        string png = Path.Combine(Path.GetTempPath(), $"sketch-{Guid.NewGuid():N}.png");
        try
        {
            MakeTestPng(png);
            var shapes = ImageShapeComposer.ComposeSketch(png, new ShapeComposerOptions { Grid = 40 });

            Assert.NotEmpty(shapes);
            Assert.All(shapes, s =>
            {
                Assert.Equal("sketch", s.Prst);
                Assert.True(s.PathPoints is { Count: >= 2 });
                Assert.True(s.StrokeWidthPt > 0);
                Assert.False(string.IsNullOrWhiteSpace(s.Fill));
            });
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void Sketch_RoundTrips_ThroughMarkdown()
    {
        string png = Path.Combine(Path.GetTempPath(), $"sketch-{Guid.NewGuid():N}.png");
        try
        {
            MakeTestPng(png);
            var shapes = ImageShapeComposer.ComposeSketch(png, new ShapeComposerOptions { Grid = 24 });
            string block = ShapeMarkdownCodec.Serialize(shapes.Take(5));

            var parsed = ShapeMarkdownCodec.Parse(block);
            Assert.Equal(5, parsed.Count);
            Assert.Equal("sketch", parsed[0].Prst);
            Assert.NotNull(parsed[0].PathPoints);
            Assert.Equal(shapes[0].PathPoints!.Count, parsed[0].PathPoints!.Count);
            Assert.Equal(shapes[0].StrokeWidthPt, parsed[0].StrokeWidthPt, 1);
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void Sketch_Docx_IsSchemaValid_WithCustGeomStrokes()
    {
        string png = Path.Combine(Path.GetTempPath(), $"sketch-{Guid.NewGuid():N}.png");
        string docx = Path.Combine(Path.GetTempPath(), $"sketch-{Guid.NewGuid():N}.docx");
        try
        {
            MakeTestPng(png);
            var shapes = ImageShapeComposer.ComposeSketch(png, new ShapeComposerOptions { Grid = 32 });
            ShapeComposerDocxWriter.WriteDocx(docx, shapes, 6.0, 6.0, null);

            using var doc = WordprocessingDocument.Open(docx, false);
            using var r = new StreamReader(doc.MainDocumentPart!.GetStream());
            string xml = r.ReadToEnd();
            Assert.Contains("custGeom", xml);
            Assert.Contains("fill=\"none\"", xml);
            Assert.Contains("<a:noFill/>", xml);
            Assert.Contains("<a:lnTo>", xml);

            var validator = new OpenXmlValidator();
            Assert.Empty(validator.Validate(doc).ToList());
        }
        finally
        {
            if (File.Exists(png)) File.Delete(png);
            if (File.Exists(docx)) File.Delete(docx);
        }
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

    [Fact]
    public void Codec_RoundTrips_CompressedBinary()
    {
        var originalShapes = Enumerable.Range(0, 50).Select(i => new ComposedShape
        {
            Prst = i % 2 == 0 ? "roundrect" : "ellipse",
            X = i * 0.1,
            Y = i * 0.05,
            W = 0.5,
            H = 0.4,
            Fill = $"{i * 5 % 256:X2}78D4",
            Rot = i * 10 % 360,
            StrokeWidthPt = 1.5,
            Text = $"Node {i}",
            TextColor = "FFFFFF"
        }).ToList();

        string compressedMd = ShapeMarkdownCodec.Serialize(originalShapes, compact: true);
        Assert.Contains(ShapeMarkdownCodec.CompressedPrefix, compressedMd);
        Assert.Contains("compact=true", compressedMd);

        var decoded = ShapeMarkdownCodec.Parse(compressedMd);
        Assert.Equal(originalShapes.Count, decoded.Count);
        for (int i = 0; i < originalShapes.Count; i++)
        {
            Assert.Equal(originalShapes[i].Prst, decoded[i].Prst);
            Assert.Equal(originalShapes[i].X, decoded[i].X, 2);
            Assert.Equal(originalShapes[i].Y, decoded[i].Y, 2);
            Assert.Equal(originalShapes[i].Fill, decoded[i].Fill);
            Assert.Equal(originalShapes[i].Text, decoded[i].Text);
        }
    }

    [Fact]
    public void CompressedShapes_PreviewAndDocx_WorkIdentically()
    {
        var shapes = Enumerable.Range(0, 25).Select(i => new ComposedShape
        {
            Prst = "hexagon",
            X = i * 0.2,
            Y = i * 0.1,
            W = 0.8,
            H = 0.6,
            Fill = "0078D4",
            Text = $"Hex {i}"
        }).ToList();

        string md = ShapeMarkdownCodec.Serialize(shapes, compact: true);
        string html = ShapeMarkdownHtml.PreTransform(md);
        Assert.Contains("<svg", html);
        Assert.Contains("Hex 0", html);

        string path = Path.Combine(Path.GetTempPath(), $"compact-shapes-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(md, path, new Models.AppSettings()).GetAwaiter().GetResult();
            using var doc = WordprocessingDocument.Open(path, false);
            var validator = new OpenXmlValidator();
            Assert.Empty(validator.Validate(doc));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
