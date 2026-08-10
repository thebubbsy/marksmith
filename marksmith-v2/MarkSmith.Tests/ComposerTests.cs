using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.Composer;
using MarkSmith.Core.Glox;
using Xunit;

namespace MarkSmith.Tests;

// Image → shapes composer: cell count, fills, shape-set cycling, and schema-valid docx output.
public class ComposerTests
{
    private static string MakeTestPng(string path, int w = 24, int h = 24)
    {
        // Tiny gradient PNG via SkiaSharp (no external assets needed).
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
    public void Compose_ProducesOneShapePerCell_WithHexFills()
    {
        string png = Path.Combine(Path.GetTempPath(), $"composer-{Guid.NewGuid():N}.png");
        try
        {
            MakeTestPng(png);
            var shapes = ImageShapeComposer.Compose(png, new ShapeComposerOptions { Grid = 8, Shapes = new() { "ellipse" } });

            Assert.Equal(64, shapes.Count);
            Assert.All(shapes, s =>
            {
                Assert.Equal("ellipse", s.Prst);
                Assert.Equal(6, s.Fill.Length);
                Assert.All(s.Fill, c => Assert.True(Uri.IsHexDigit(c)));
                Assert.True(s.W > 0 && s.H > 0);
            });
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void Compose_CyclesThroughShapeSet()
    {
        string png = Path.Combine(Path.GetTempPath(), $"composer-{Guid.NewGuid():N}.png");
        try
        {
            MakeTestPng(png);
            var shapes = ImageShapeComposer.Compose(png, new ShapeComposerOptions
            {
                Grid = 10,
                Shapes = new() { "ellipse", "chevron", "diamond" }
            });

            Assert.Equal(100, shapes.Count);
            var kinds = shapes.Select(s => s.Prst).Distinct().ToList();
            Assert.Equal(3, kinds.Count);
            Assert.Contains("ellipse", kinds);
            Assert.Contains("chevron", kinds);
            Assert.Contains("diamond", kinds);
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void Compose_LineAlone_UsesScanlineMode_OneStrokePerRow()
    {
        string png = Path.Combine(Path.GetTempPath(), $"composer-{Guid.NewGuid():N}.png");
        try
        {
            MakeTestPng(png);
            var shapes = ImageShapeComposer.Compose(png, new ShapeComposerOptions
            {
                Grid = 120,
                Shapes = new() { "line" }
            });

            // Scanline mode: full-width strokes, one per row, thickness follows darkness.
            Assert.Equal(120, shapes.Count);
            Assert.All(shapes, s =>
            {
                Assert.Equal("rect", s.Prst);
                Assert.Equal(0, s.X, 3);           // full-width
                Assert.True(s.W > 4.0, "strokes span the canvas width");
                Assert.True(s.H > 0 && s.H < 0.2, "stroke thickness follows luminance");
                Assert.Equal(6, s.Fill.Length);
            });
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void Compose_HighDensity_ClampsToPracticalCellBudget()
    {
        string png = Path.Combine(Path.GetTempPath(), $"composer-{Guid.NewGuid():N}.png");
        try
        {
            MakeTestPng(png, 800, 600);
            var shapes = ImageShapeComposer.Compose(png, new ShapeComposerOptions
            {
                Grid = 4096, // ~16.7M cells requested
                Shapes = new() { "ellipse" }
            });

            Assert.True(shapes.Count <= ImageShapeComposer.MaxCells);
            Assert.True(shapes.Count > 1_000_000, $"expected dense output, got {shapes.Count}");
        }
        finally { if (File.Exists(png)) File.Delete(png); }
    }

    [Fact]
    public void RenderSvg_ContainsAllShapes()
    {
        var shapes = new System.Collections.Generic.List<ComposedShape>
        {
            new() { Prst = "ellipse", X = 0, Y = 0, W = 1, H = 1, Fill = "FF0000" },
            new() { Prst = "chevron", X = 1, Y = 0, W = 1, H = 1, Fill = "00FF00" },
            new() { Prst = "diamond", X = 2, Y = 0, W = 1, H = 1, Fill = "0000FF" },
        };
        string svg = ImageShapeComposer.RenderSvg(shapes, 3, 1);
        Assert.Contains("<ellipse", svg);
        Assert.Contains("<polygon", svg);
        Assert.Contains("#FF0000", svg);
        Assert.Contains("#00FF00", svg);
    }

    [Fact]
    public void WriteDocx_ProducesSchemaValidPackage()
    {
        string png = Path.Combine(Path.GetTempPath(), $"composer-{Guid.NewGuid():N}.png");
        string docx = Path.Combine(Path.GetTempPath(), $"composed-{Guid.NewGuid():N}.docx");
        try
        {
            MakeTestPng(png);
            var shapes = ImageShapeComposer.Compose(png, new ShapeComposerOptions { Grid = 8 });
            ShapeComposerDocxWriter.WriteDocx(docx, shapes, 3.0, 3.0, null);

            using var doc = WordprocessingDocument.Open(docx, false);
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(doc).ToList();
            Assert.Empty(errors);
        }
        finally
        {
            if (File.Exists(png)) File.Delete(png);
            if (File.Exists(docx)) File.Delete(docx);
        }
    }

    [Fact]
    public void WriteDocx_WithTheme_EveryRelationshipTargetResolvesToAnExistingPart()
    {
        // Regression for the dangling-theme-rel defect: the studio passes a theme, and the theme
        // relationship used to be written into the package-ROOT _rels/.rels with Target=
        // "theme/theme1.xml" — which resolves to /theme/theme1.xml, a part that does not exist
        // (the theme lives at word/theme/theme1.xml). A dangling relationship is an OPC integrity
        // violation that Word flags with a repair prompt.
        string png = Path.Combine(Path.GetTempPath(), $"composer-{Guid.NewGuid():N}.png");
        string docx = Path.Combine(Path.GetTempPath(), $"composed-theme-{Guid.NewGuid():N}.docx");
        try
        {
            MakeTestPng(png);
            var shapes = ImageShapeComposer.Compose(png, new ShapeComposerOptions { Grid = 8 });
            ShapeComposerDocxWriter.WriteDocx(docx, shapes, 3.0, 3.0, SmartArtLayoutCatalog.Shared.ThemeXml);

            using var zip = ZipFile.OpenRead(docx);
            var parts = zip.Entries
                .Select(e => "/" + e.FullName.Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Contains("/word/theme/theme1.xml", parts);
            Assert.Contains("/_rels/.rels", parts);
            Assert.Contains("/word/_rels/document.xml.rels", parts);

            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                // A rel file lives at <dir>/_rels/<part>.rels; targets resolve relative to <dir>
                // (the part's directory) — and for the package root (_rels/.rels) relative to "/".
                string relDir = (Path.GetDirectoryName(entry.FullName.Replace('\\', '/')) ?? "").Replace('\\', '/');
                string baseDir = relDir == "_rels" || relDir.EndsWith("/_rels", StringComparison.Ordinal)
                    ? relDir[..^"_rels".Length].TrimEnd('/')
                    : relDir;
                string xml;
                using (var r = new StreamReader(entry.Open()))
                    xml = r.ReadToEnd();

                foreach (Match m in Regex.Matches(xml, @"Target=""([^""]+)"""))
                {
                    string target = m.Groups[1].Value;
                    string resolved = ResolveRelTarget(baseDir, target);
                    Assert.True(
                        parts.Contains(resolved),
                        $"Relationship target '{target}' in {entry.FullName} resolves to '{resolved}', which is not a part in the package.");
                }
            }
        }
        finally
        {
            if (File.Exists(png)) File.Delete(png);
            if (File.Exists(docx)) File.Delete(docx);
        }
    }

    private static string ResolveRelTarget(string relDir, string target)
    {
        var segments = new List<string>();
        if (target.StartsWith("/", StringComparison.Ordinal))
        {
            segments.AddRange(target.Split('/').Where(s => s.Length > 0));
        }
        else
        {
            segments.AddRange((relDir + "/" + target).Split('/').Where(s => s.Length > 0));
        }

        var stack = new List<string>();
        foreach (var seg in segments)
        {
            if (seg == ".") continue;
            if (seg == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else stack.Add(seg);
        }
        return "/" + string.Join("/", stack);
    }
}
