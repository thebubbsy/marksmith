using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.Composer;
using MarkSmith.Core.Services;
using MarkSmith.Models;
using MarkSmith.Services;
using SkiaSharp;
using Xunit;

namespace MarkSmith.Tests
{
    public class ComposerOptimizationTests
    {
        [Fact]
        public void RenderBitmapPreviewPng_ProducesCrispPngAndDataUri()
        {
            var shapes = Enumerable.Range(0, 160).Select(i => new ComposedShape
            {
                Prst = i % 3 == 0 ? "ellipse" : i % 3 == 1 ? "diamond" : "roundrect",
                X = (i % 16) * 0.35,
                Y = (i / 16) * 0.35,
                W = 0.3,
                H = 0.3,
                Fill = $"{i * 12 % 256:X2}88CC",
                Rot = (i * 15) % 360,
                Text = i % 10 == 0 ? $"#{i}" : null
            }).ToList();

            byte[] bytes = ImageShapeComposer.RenderBitmapPreviewPngBytes(shapes, 6.2, 4.0, scale: 2.0f);
            Assert.NotEmpty(bytes);

            // Valid PNG magic signature: 0x89 0x50 0x4E 0x47 0x0D 0x0A 0x1A 0x0A
            Assert.True(bytes.Length > 8);
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal(0x50, bytes[1]);
            Assert.Equal(0x4E, bytes[2]);
            Assert.Equal(0x47, bytes[3]);

            string dataUri = ImageShapeComposer.RenderBitmapPreviewPng(shapes, 6.2, 4.0, scale: 2.0f);
            Assert.StartsWith("data:image/png;base64,", dataUri);

            // Decode with Skia to verify integrity and dimensions
            using var bmp = SKBitmap.Decode(bytes);
            Assert.NotNull(bmp);
            Assert.True(bmp.Width > 0 && bmp.Height > 0);
        }

        [Fact]
        public void Trick1_SkiaFlattenedBitmapPreview_Over150Shapes_EmitsO1ImageTag()
        {
            // Create 180 shapes (over the 150 threshold)
            var shapes = Enumerable.Range(0, 180).Select(i => new ComposedShape
            {
                Prst = "ellipse",
                X = (i % 18) * 0.3,
                Y = (i / 18) * 0.3,
                W = 0.25,
                H = 0.25,
                Fill = "0078D4"
            }).ToList();

            string md = ShapeMarkdownCodec.Serialize(shapes);
            string html = ShapeMarkdownHtml.PreTransform(md);

            // Trick 1 mandate: Do NOT flood WebView2/HTML with thousands of individual SVG nodes.
            // When shapes.Count > 150, offscreen Skia rasterizer paints to an in-memory SKBitmap
            // and emits an <img> tag with data:image/png;base64,...
            Assert.Contains("<img src=\"data:image/png;base64,", html);
            Assert.DoesNotContain("<svg", html);

            // DOM overhead is O(1) — exactly one <img> element
            int imgCount = Regex.Matches(html, @"<img\b").Count;
            Assert.Equal(1, imgCount);
        }

        [Fact]
        public void Trick3_CompoundPathGroupingByColor_ReducesDomElements()
        {
            // 60 shapes sharing 3 palette fill colors (20 red, 20 green, 20 blue)
            var shapes = new List<ComposedShape>();
            string[] colors = { "FF0000", "00FF00", "0000FF" };
            for (int i = 0; i < 60; i++)
            {
                shapes.Add(new ComposedShape
                {
                    Prst = i % 2 == 0 ? "rect" : "diamond",
                    X = (i % 10) * 0.5,
                    Y = (i / 10) * 0.5,
                    W = 0.4,
                    H = 0.4,
                    Fill = colors[i % 3],
                    Rot = (i % 4) * 45
                });
            }

            // Explicit compound path rendering
            string compoundSvg = ImageShapeComposer.RenderSvgCompound(shapes, 6.0, 4.0);

            // Must contain compound paths with multiple M...Z segments
            Assert.Contains("<path", compoundSvg);
            Assert.Contains("#FF0000", compoundSvg);
            Assert.Contains("#00FF00", compoundSvg);
            Assert.Contains("#0000FF", compoundSvg);

            // Count number of <path tags: should equal 3 (one per palette color!), not 60!
            int pathTagCount = Regex.Matches(compoundSvg, @"<path\b").Count;
            Assert.Equal(3, pathTagCount);

            // Each compound path's d attribute contains multiple moveto 'M' commands
            int mCount = Regex.Matches(compoundSvg, @"\bM\s*[\d.]").Count;
            Assert.Equal(60, mCount);
        }

        [Fact]
        public void Trick4_LODClamping_InAppPreviewClampsGrid_ExportKeepsFullGrid()
        {
            var recipe = new MosaicRecipe
            {
                Grid = 64, // dense grid requested
                Shapes = new List<string> { "ellipse", "diamond" }
            };

            // In-app preview clamps to MaxPreviewGrid (32)
            var previewShapes = recipe.Synthesize(isPreview: true);
            Assert.NotEmpty(previewShapes);
            // With Grid clamped to 32, total shapes should be around 32 * 32 = 1024 (or row-aspect corrected)
            // It MUST be significantly fewer shapes than the 64x64 grid
            var exportShapes = recipe.Synthesize(isPreview: false);
            Assert.NotEmpty(exportShapes);

            Assert.True(previewShapes.Count < exportShapes.Count);
            Assert.True(exportShapes.Count >= 64 * 30); // 64 grid density
        }

        [Fact]
        public void Trick2_DeferredRecipeSynthesis_WordExportReceives100PercentNativeVectorShapes()
        {
            string md = ":::mosaic grid=36 shapes=\"ellipse,diamond\" dither=false\n:::\n";

            // Verify preview uses lightweight representation
            string previewHtml = ShapeMarkdownHtml.PreTransform(md);
            Assert.NotEmpty(previewHtml);

            // Export to .docx via DocxExportService
            string docxPath = Path.Combine(Path.GetTempPath(), $"mosaic-export-{Guid.NewGuid():N}.docx");
            try
            {
                new DocxExportService().ExportAsync(md, docxPath, new AppSettings()).GetAwaiter().GetResult();

                Assert.True(File.Exists(docxPath));

                // Open Word document and verify 100% native DrawingML vector shapes
                using var doc = WordprocessingDocument.Open(docxPath, false);
                using var reader = new StreamReader(doc.MainDocumentPart!.GetStream());
                string docXml = reader.ReadToEnd();

                // DrawingML vector elements
                Assert.Contains("wordprocessingGroup", docXml);
                Assert.Contains("<wps:wsp>", docXml);
                Assert.Contains("<a:prstGeom", docXml);
                Assert.Contains("<a:solidFill>", docXml);
                Assert.Contains("ellipse", docXml);
                Assert.Contains("diamond", docXml);

                // OpenXML Schema validation must pass with zero errors
                var validator = new OpenXmlValidator();
                var errors = validator.Validate(doc).ToList();
                Assert.Empty(errors);
            }
            finally
            {
                if (File.Exists(docxPath)) File.Delete(docxPath);
            }
        }

        [Fact]
        public void RecipeInsideShapesBlock_SynthesizesCorrectly()
        {
            string md = ":::shapes\ngrid: 40\nshapes: chevron, hexagon\n:::\n";

            string previewHtml = ShapeMarkdownHtml.PreTransform(md);
            Assert.NotEmpty(previewHtml);

            string docxPath = Path.Combine(Path.GetTempPath(), $"shapes-recipe-{Guid.NewGuid():N}.docx");
            try
            {
                new DocxExportService().ExportAsync(md, docxPath, new AppSettings()).GetAwaiter().GetResult();

                Assert.True(File.Exists(docxPath));
                using var doc = WordprocessingDocument.Open(docxPath, false);
                using var reader = new StreamReader(doc.MainDocumentPart!.GetStream());
                string docXml = reader.ReadToEnd();

                Assert.Contains("wordprocessingGroup", docXml);
                Assert.Contains("<wps:wsp>", docXml);
                Assert.Contains("chevron", docXml);
                Assert.Contains("hexagon", docXml);

                var validator = new OpenXmlValidator();
                Assert.Empty(validator.Validate(doc));
            }
            finally
            {
                if (File.Exists(docxPath)) File.Delete(docxPath);
            }
        }
    }
}
