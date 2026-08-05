using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Core.Composer;
using Xunit;

namespace MarkSmith.Tests;

// Image → line-art tracer: the MLShape "trace a picture into Word line items" engine.
// Guarantees that matter: every line is a selectable line item, lines NEVER overlap at any
// density (row banding + disjoint runs + band-capped thickness), density scales, modes differ.
public class ImageLineTracerTests
{
    private static string WritePng(string path, int w, int h, Func<int, int, SKColorEx> pixel)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var c = pixel(x, y);
            bmp.SetPixel(x, y, new SkiaSharp.SKColor(c.R, c.G, c.B));
        }
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    private readonly record struct SKColorEx(byte R, byte G, byte B);

    private static readonly Func<int, int, SKColorEx> White = (_, _) => new(250, 250, 250);
    private static readonly Func<int, int, SKColorEx> Black = (_, _) => new(18, 18, 18);

    /// <summary>A dark disc on white — a clean single-shape image for tracing.</summary>
    private static string ShapePng(string path, int w = 128, int h = 128)
    {
        double cx = w / 2.0, cy = h / 2.0, r = w * 0.30;
        return WritePng(path, w, h, (x, y) =>
        {
            double dx = x - cx, dy = y - cy;
            return dx * dx + dy * dy <= r * r ? new(18, 18, 18) : new(250, 250, 250);
        });
    }

    private static string GradientPng(string path, int w = 96, int h = 96) =>
        WritePng(path, w, h, (x, y) => new((byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), 128));

    private static readonly (double X, double Y)[] HorizontalLine = { (0, 50), (100, 50) };

    [Fact]
    public void Trace_ProducesSelectableLineItems()
    {
        string png = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.png");
        try
        {
            ShapePng(png);
            var lines = ImageLineTracer.TraceLines(png, new LineTraceOptions { Rows = 200 });

            Assert.NotEmpty(lines);
            Assert.All(lines, s =>
            {
                Assert.Equal("sketch", s.Prst);                    // flows through the Word custGeom stroke path
                Assert.True(s.PathPoints is { Count: >= 2 }, "every traced line is a line item");
                Assert.Equal(HorizontalLine, s.PathPoints);        // straight, 0..100 local space
                Assert.True(s.W > 0 && s.H > 0);
                Assert.True(s.StrokeWidthPt > 0);
                Assert.Equal(6, s.Fill.Length);
            });
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void Trace_LinesNeverOverlap_AtAnyDensity()
    {
        string png = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.png");
        try
        {
            ShapePng(png);
            // Deliberately extreme density to stress the band-capped thickness logic.
            foreach (int rows in new[] { 64, 480, 2000, 6000 })
            {
                var lines = ImageLineTracer.TraceLines(png, new LineTraceOptions { Rows = rows });
                AssertNoOverlap(lines, $"rows={rows}");
            }
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    private static void AssertNoOverlap(List<ComposedShape> lines, string ctx)
    {
        const double eps = 1e-6;
        for (int i = 0; i < lines.Count; i++)
        for (int j = i + 1; j < lines.Count; j++)
        {
            var a = lines[i];
            var b = lines[j];
            // Rows are disjoint bands; only lines in the SAME band can possibly interact.
            if (a.Y + a.H <= b.Y + eps || b.Y + b.H <= a.Y + eps) continue;
            // Same band → runs are disjoint by construction: x-ranges must not overlap.
            bool xOverlap = a.X < b.X + b.W - eps && b.X < a.X + a.W - eps;
            Assert.False(xOverlap, $"{ctx}: lines overlap ({a.X:F3},{a.Y:F3}) {a.W:F3}x{a.H:F3} vs ({b.X:F3},{b.Y:F3}) {b.W:F3}x{b.H:F3}");
        }
    }

    [Fact]
    public void Trace_DensityScalesLineCount()
    {
        string png = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.png");
        try
        {
            GradientPng(png); // busy everywhere → line count tracks the row budget
            int sparse = ImageLineTracer.TraceLines(png, new LineTraceOptions { Rows = 100 }).Count;
            int dense = ImageLineTracer.TraceLines(png, new LineTraceOptions { Rows = 3000 }).Count;
            Assert.True(dense > sparse * 4, $"expected much denser output, got {sparse} vs {dense}");
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void Trace_AllModesProduceLines()
    {
        string png = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.png");
        try
        {
            ShapePng(png);
            foreach (var mode in Enum.GetValues<LineTraceMode>())
            {
                var lines = ImageLineTracer.TraceLines(png, new LineTraceOptions { Rows = 240, Mode = mode });
                Assert.NotEmpty(lines);
            }
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void Trace_RespectsMaxLinesCeiling_WithoutHeadTruncation()
    {
        string png = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.png");
        try
        {
            // Fully black image with a tiny speckle filter → the run count would explode; the cap
            // must hold the line count AND keep the whole image represented (even row sampling),
            // not drop the bottom half.
            WritePng(png, 64, 64, Black);
            var lines = ImageLineTracer.TraceLines(png, new LineTraceOptions
            {
                Rows = 16384,
                MinRunFraction = 0.001,
                MaxLines = 5000
            });
            Assert.True(lines.Count <= 5000, $"expected cap, got {lines.Count}");
            // Square 64px image on a 6.2" canvas → total height = 6.2". Head-truncation would stop
            // at ~38% of the height; even sampling must reach the bottom.
            double maxBottom = lines.Max(s => s.Y + s.H);
            Assert.True(maxBottom > 6.2 * 0.6, $"expected lines to cover the full height, bottom at {maxBottom:F2}");
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void TracedLine_SvgStrokeWidth_TracksBoxHeightNotWidth()
    {
        // A traced line has H = StrokeWidthPt/72". Its SVG stroke-width lives in path space and is
        // scaled by h/100 (perpendicular to the line) — so it must render at exactly StrokeWidthPt.
        // Regression: the old code divided by the run width w, making dense previews ghost-thin.
        var line = new ComposedShape
        {
            Prst = "sketch", X = 0, Y = 0, W = 1.0, H = 1.5 / 72.0, Fill = "000000",
            StrokeWidthPt = 1.5, PathPoints = new() { (0, 50), (100, 50) }
        };
        string svg = ImageShapeComposer.RenderSvg(new List<ComposedShape> { line }, 1.0, 1.5 / 72.0);
        Assert.Contains("stroke-width=\"100.00\"", svg); // (1.5*96/72)*100 / (1.5/72*96) = 100
    }

    [Fact]
    public void Trace_MonochromeUsesFixedInk()
    {
        string png = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.png");
        try
        {
            ShapePng(png);
            var lines = ImageLineTracer.TraceLines(png, new LineTraceOptions { Rows = 120, UseColor = false });
            Assert.NotEmpty(lines);
            Assert.All(lines, s => Assert.Equal("181818", s.Fill));
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void RenderPreviewPng_ProducesPng()
    {
        string png = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.png");
        try
        {
            ShapePng(png);
            var lines = ImageLineTracer.TraceLines(png, new LineTraceOptions { Rows = 300 });
            var bytes = ImageLineTracer.RenderPreviewPng(lines, 6.2, 6.2);
            Assert.NotNull(bytes);
            Assert.True(bytes!.Length > 100);
            Assert.Equal(0x89, bytes[0]); // PNG signature
            Assert.Equal((byte)'P', bytes[1]);
            Assert.Equal((byte)'N', bytes[2]);
            Assert.Equal((byte)'G', bytes[3]);
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void WriteDotx_ProducesWordTemplatePackage()
    {
        string png = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.png");
        string dotx = Path.Combine(Path.GetTempPath(), $"composed-{Guid.NewGuid():N}.dotx");
        try
        {
            ShapePng(png);
            var lines = ImageLineTracer.TraceLines(png, new LineTraceOptions { Rows = 200 });
            ShapeComposerDocxWriter.WriteDotx(dotx, lines, 6.2, 6.2, null);

            using var doc = WordprocessingDocument.Open(dotx, false);
            Assert.Equal(WordprocessingDocumentType.Template, doc.DocumentType);

            // Every traced line made it into the package as a native DrawingML stroke.
            string documentXml;
            using (var reader = new StreamReader(doc.MainDocumentPart!.GetStream()))
                documentXml = reader.ReadToEnd();
            int strokeCount = System.Text.RegularExpressions.Regex.Matches(documentXml, "<wps:wsp>").Count;
            Assert.Equal(lines.Count, strokeCount);
            Assert.Contains("<a:flat/>", documentXml); // flat caps (cap-type element), not the malformed "<a:cap flat/>"
            Assert.DoesNotContain("<a:prstGeom", documentXml); // lines are custGeom strokes, not presets
        }
        finally
        {
            if (File.Exists(png)) File.Delete(png);
            if (File.Exists(dotx)) File.Delete(dotx);
        }
    }
}
