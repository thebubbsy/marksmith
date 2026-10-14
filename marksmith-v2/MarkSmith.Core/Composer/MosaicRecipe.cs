using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace MarkSmith.Core.Composer
{
    /// <summary>
    /// A lightweight recipe for synthesizing a shape mosaic or diagram on demand.
    /// Enables deferred evaluation: in-app preview synthesizes clamped LOD / raster preview,
    /// while Word (.docx) export synthesizes 100% full-density native DrawingML vector shapes.
    /// </summary>
    public class MosaicRecipe
    {
        public const int DefaultGrid = 48;
        public const int MaxPreviewGrid = 32;

        public string? ImagePath { get; set; }
        public string? ImageDataBase64 { get; set; }
        public int Grid { get; set; } = DefaultGrid;
        public List<string> Shapes { get; set; } = new() { "auto" };
        public bool Dither { get; set; } = true;
        public int PaletteColors { get; set; } = 64;
        public double Padding { get; set; } = -6.0;
        public double InsetInches { get; set; } = 0.0;
        public bool? AdaptiveShapes { get; set; } = null;
        public string Mode { get; set; } = "shapes"; // "shapes", "line"/"scanlines", "sketch"

        private static readonly Regex HeaderAttrRegex = new(
            @"(?<key>[\w\-]+)\s*=\s*(?:""(?<val>[^""]*)""|(?<val>[^\s]+))",
            RegexOptions.Compiled);

        public static bool IsRecipe(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Contains(ShapeMarkdownCodec.CompressedPrefix, StringComparison.OrdinalIgnoreCase)) return false;

            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
            if (lines.Count == 0) return false;

            // If line starts with a prst shape token and coordinates, e.g. "ellipse 1.0 0.5 0.9 0.7 FFD9B3", it's raw shapes
            var first = lines[0];
            var parts = first.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            return lines.Any(l =>
                l.StartsWith("grid", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("image", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("img", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("shapes", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("shape", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("recipe", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("mode", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("dither", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("padding", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("palette", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("adaptive", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase));
        }

        public static MosaicRecipe Parse(string? header, string innerContent, IDictionary<string, string>? attributes = null)
        {
            var recipe = new MosaicRecipe();

            // 1. Feature node attributes if parsed upstream
            if (attributes != null)
            {
                foreach (var (k, v) in attributes)
                {
                    ApplyKeyValue(recipe, k.ToLowerInvariant(), v);
                }
            }

            // 2. Header attributes (e.g. :::mosaic grid=64 shapes="ellipse,diamond")
            if (!string.IsNullOrWhiteSpace(header))
            {
                foreach (Match m in HeaderAttrRegex.Matches(header))
                {
                    var k = m.Groups["key"].Value.ToLowerInvariant();
                    var v = m.Groups["val"].Value.Trim('"', '\'');
                    ApplyKeyValue(recipe, k, v);
                }
            }

            // 2. Inner key-value lines
            if (!string.IsNullOrWhiteSpace(innerContent))
            {
                foreach (var raw in innerContent.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int colon = line.IndexOf(':');
                    int equals = line.IndexOf('=');
                    int sep = (colon >= 0 && equals >= 0) ? Math.Min(colon, equals) : Math.Max(colon, equals);

                    if (sep > 0)
                    {
                        string k = line[..sep].Trim().ToLowerInvariant();
                        string v = line[(sep + 1)..].Trim().Trim('"', '\'');
                        ApplyKeyValue(recipe, k, v);
                    }
                    else if (line.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    {
                        int b64Idx = line.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
                        if (b64Idx >= 0)
                        {
                            recipe.ImageDataBase64 = line[(b64Idx + 7)..].Trim();
                        }
                    }
                }
            }

            return recipe;
        }

        private static void ApplyKeyValue(MosaicRecipe recipe, string key, string val)
        {
            switch (key)
            {
                case "grid":
                    if (int.TryParse(val, out var g)) recipe.Grid = Math.Clamp(g, 4, 1024);
                    break;
                case "shapes":
                case "shape":
                    var shapes = val.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList();
                    if (shapes.Count > 0) recipe.Shapes = shapes;
                    break;
                case "image":
                case "img":
                case "src":
                    if (val.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    {
                        int b64Idx = val.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
                        if (b64Idx >= 0) recipe.ImageDataBase64 = val[(b64Idx + 7)..].Trim();
                    }
                    else
                    {
                        recipe.ImagePath = val;
                    }
                    break;
                case "data":
                case "base64":
                    recipe.ImageDataBase64 = val;
                    break;
                case "dither":
                    if (bool.TryParse(val, out var d)) recipe.Dither = d;
                    break;
                case "palette":
                case "colors":
                    if (int.TryParse(val, out var p)) recipe.PaletteColors = Math.Clamp(p, 2, 256);
                    break;
                case "padding":
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var pad))
                        recipe.Padding = Math.Clamp(pad, -20.0, 20.0);
                    break;
                case "inset":
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var ins))
                        recipe.InsetInches = Math.Max(0.0, ins);
                    break;
                case "adaptive":
                    if (bool.TryParse(val, out var a)) recipe.AdaptiveShapes = a;
                    break;
                case "mode":
                    recipe.Mode = val.ToLowerInvariant();
                    break;
            }
        }

        public List<ComposedShape> Synthesize(bool isPreview = false)
        {
            int effectiveGrid = isPreview ? Math.Min(Grid, MaxPreviewGrid) : Grid;

            bool adaptive = AdaptiveShapes ?? Shapes.Contains("auto");
            var opt = new ShapeComposerOptions
            {
                Grid = effectiveGrid,
                Shapes = new List<string>(Shapes),
                Dither = Dither,
                PaletteColors = PaletteColors,
                Padding = Padding,
                InsetInches = InsetInches,
                AdaptiveShapes = adaptive
            };

            SKBitmap? src = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(ImageDataBase64))
                {
                    try
                    {
                        byte[] raw = Convert.FromBase64String(ImageDataBase64);
                        src = SKBitmap.Decode(raw);
                    }
                    catch
                    {
                        src = null;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath))
                {
                    try
                    {
                        src = SKBitmap.Decode(ImagePath);
                    }
                    catch
                    {
                        src = null;
                    }
                }

                if (Mode.Equals("sketch", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath))
                    {
                        return ImageShapeComposer.ComposeSketch(ImagePath, opt);
                    }
                    return ImageLineTracer.TraceLines(ImagePath ?? "", new LineTraceOptions
                    {
                        Rows = Math.Clamp(effectiveGrid, 8, 8192),
                        Mode = LineTraceMode.TopographicWaves
                    });
                }

                return ImageShapeComposer.Compose(src, opt);
            }
            finally
            {
                src?.Dispose();
            }
        }
    }
}
