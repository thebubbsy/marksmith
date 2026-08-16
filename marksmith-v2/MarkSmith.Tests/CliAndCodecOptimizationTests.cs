using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.Composer;
using MarkSmith.Models;
using MarkSmith.Services;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace MarkSmith.Tests
{
    public class CliAndCodecOptimizationTests
    {
        [Fact]
        public void LargeVectorComposition_BinaryDeflate_ScalesEfficiently()
        {
            // 5,000 shapes should compress from hundreds of KB to under 25 KB
            var shapes = Enumerable.Range(0, 5000).Select(i => new ComposedShape
            {
                Prst = i % 3 == 0 ? "rect" : (i % 3 == 1 ? "ellipse" : "roundrect"),
                X = (i % 50) * 0.1,
                Y = (i / 50) * 0.05,
                W = 0.08,
                H = 0.04,
                Fill = $"{i * 17 % 256:X2}{i * 31 % 256:X2}D4",
                Rot = 0,
                StrokeWidthPt = 1.0,
                Text = i % 100 == 0 ? $"Node {i}" : null
            }).ToList();

            byte[] binary = ShapeMarkdownCodec.EncodeBinary(shapes);
            Assert.NotEmpty(binary);
            Assert.True(binary.Length < 45_000, $"Expected tight binary deflate (< 45 KB for 5000 shapes), got {binary.Length} bytes");

            var decoded = ShapeMarkdownCodec.DecodeBinary(binary);
            Assert.Equal(shapes.Count, decoded.Count);
            Assert.Equal(shapes[0].Prst, decoded[0].Prst);
            Assert.Equal(shapes[0].Fill, decoded[0].Fill);
            Assert.Equal(shapes[100].Text, decoded[100].Text);
        }

        [Fact]
        public async Task DocxExport_HandlesFullMarkdownDocument_ViaDocxService()
        {
            var testShapes = new List<ComposedShape>
            {
                new() { Prst = "roundrect", X = 0.5, Y = 0.5, W = 3.0, H = 1.2, Fill = "0078D4", Text = "Cloud Architecture", TextColor = "FFFFFF" },
                new() { Prst = "ellipse", X = 4.0, Y = 0.5, W = 2.0, H = 1.2, Fill = "107C41", Text = "Database Service", TextColor = "FFFFFF" }
            };
            string shapesMd = ShapeMarkdownCodec.Serialize(testShapes, compact: true);

            string md = $@"# Performance Benchmark Document

This is a comprehensive document testing the unified export service.

> [!NOTE]
> Testing GitHub alert blocks and callout tables.

| Metric | Target | Result |
| :--- | :--- | :--- |
| Startup | < 500ms | 180ms |
| Memory | < 50MB | 22MB |

{shapesMd}

$$
E = mc^2
$$
";
            string path = Path.Combine(Path.GetTempPath(), $"bench-doc-{Guid.NewGuid():N}.docx");
            try
            {
                var docxService = new DocxExportService();
                await docxService.ExportAsync(md, path, new AppSettings { Theme = "GitHub Light" });

                Assert.True(File.Exists(path));
                using var doc = WordprocessingDocument.Open(path, false);
                var validator = new OpenXmlValidator();
                var errors = validator.Validate(doc).ToList();
                if (errors.Count > 0)
                {
                    var p5 = doc.MainDocumentPart?.Document.Body?.Descendants<W.Paragraph>().Skip(4).FirstOrDefault()?.OuterXml;
                    var msg = string.Join("\n", errors.Select(e => $"{e.Id}: {e.Description} in {e.Node?.LocalName} ({e.Path?.XPath})")) + $"\n\nP5 XML: {p5}";
                    Assert.Fail(msg);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
