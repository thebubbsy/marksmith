using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Core.AST;
using SkiaSharp;

namespace MarkSmith.Core.Mosaic
{
    public class RasterMosaicOptions
    {
        public int GridWidth { get; set; } = 8;
        public int GridHeight { get; set; } = 8;
        public int PaletteColors { get; set; } = 16;
        public bool EnableDithering { get; set; } = true;
        public string TargetLayout { get; set; } = "picturelist";
    }

    public static class RasterMosaicEngine
    {
        /// <summary>
        /// Real raster→SmartArt mosaic: resamples the source image to a W×H grid of average
        /// cell colors, quantizes them to a median-cut palette, optionally applies Floyd–
        /// Steinberg error diffusion, and emits one AST tile node per cell with its hex fill.
        /// </summary>
        public static CanonicalAst GenerateMosaicAst(string imagePath, RasterMosaicOptions options)
        {
            var ast = new CanonicalAst
            {
                RequestedLayout = options.TargetLayout
            };

            int gw = Math.Max(1, options.GridWidth);
            int gh = Math.Max(1, options.GridHeight);

            string[,] colors = BuildMosaicColors(imagePath, gw, gh, options);

            for (int y = 0; y < gh; y++)
            {
                for (int x = 0; x < gw; x++)
                {
                    var node = new AstNode
                    {
                        NodeId = $"mosaic_{x}_{y}",
                        Depth = 1,
                        ParentId = ast.Root.NodeId,
                        NodeType = AstNodeType.Image,
                        Text = $"({x},{y})",
                        ImagePath = imagePath,
                        SemanticTags = new List<string> { "mosaic", "picture" }
                    };

                    node.Attributes["hexColor"] = colors[y, x];
                    node.Attributes["gridX"] = x.ToString();
                    node.Attributes["gridY"] = y.ToString();

                    ast.Root.Children.Add(node);
                }
            }

            return ast;
        }

        private static string[,] BuildMosaicColors(string imagePath, int gw, int gh, RasterMosaicOptions options)
        {
            var result = new string[gh, gw];
            var cellColors = new SKColor[gh, gw];

            try
            {
                using var src = SKBitmap.Decode(imagePath);
                if (src == null || src.Width <= 0 || src.Height <= 0)
                {
                    return FallbackGradient(gw, gh);
                }

                // 1. Resample: average color over each cell's source-pixel region.
                for (int y = 0; y < gh; y++)
                {
                    for (int x = 0; x < gw; x++)
                    {
                        int x0 = x * src.Width / gw;
                        int x1 = Math.Max(x0 + 1, (x + 1) * src.Width / gw);
                        int y0 = y * src.Height / gh;
                        int y1 = Math.Max(y0 + 1, (y + 1) * src.Height / gh);

                        long r = 0, g = 0, b = 0, count = 0;
                        for (int py = y0; py < y1; py++)
                        {
                            for (int px = x0; px < x1; px++)
                            {
                                var c = src.GetPixel(px, py);
                                r += c.Red; g += c.Green; b += c.Blue; count++;
                            }
                        }

                        if (count > 0)
                        {
                            cellColors[y, x] = new SKColor(
                                (byte)(r / count), (byte)(g / count), (byte)(b / count));
                        }
                        else
                        {
                            cellColors[y, x] = new SKColor(0, 0, 0);
                        }
                    }
                }
            }
            catch
            {
                // Unreadable image -> deterministic gradient so the pipeline never throws.
                return FallbackGradient(gw, gh);
            }

            // 2. Median-cut palette of up to PaletteColors entries.
            var palette = BuildPalette(cellColors, gw, gh, Math.Max(2, options.PaletteColors));
            if (palette.Count == 0)
            {
                return FallbackGradient(gw, gh);
            }

            // 3. Quantize; diffuse error with Floyd–Steinberg when enabled.
            if (options.EnableDithering)
            {
                var grid = cellColors;
                var errR = new double[gh, gw];
                var errG = new double[gh, gw];
                var errB = new double[gh, gw];

                for (int y = 0; y < gh; y++)
                {
                    for (int x = 0; x < gw; x++)
                    {
                        var c = grid[y, x];
                        double cr = Clamp(c.Red + errR[y, x]);
                        double cg = Clamp(c.Green + errG[y, x]);
                        double cb = Clamp(c.Blue + errB[y, x]);

                        var nearest = Nearest(palette, cr, cg, cb);
                        result[y, x] = ToHex(nearest);

                        double dr = cr - nearest.Red;
                        double dg = cg - nearest.Green;
                        double db = cb - nearest.Blue;

                        if (x + 1 < gw) { errR[y, x + 1] += dr * 7 / 16.0; errG[y, x + 1] += dg * 7 / 16.0; errB[y, x + 1] += db * 7 / 16.0; }
                        if (y + 1 < gh)
                        {
                            if (x > 0) { errR[y + 1, x - 1] += dr * 3 / 16.0; errG[y + 1, x - 1] += dg * 3 / 16.0; errB[y + 1, x - 1] += db * 3 / 16.0; }
                            errR[y + 1, x] += dr * 5 / 16.0; errG[y + 1, x] += dg * 5 / 16.0; errB[y + 1, x] += db * 5 / 16.0;
                            if (x + 1 < gw) { errR[y + 1, x + 1] += dr * 1 / 16.0; errG[y + 1, x + 1] += dg * 1 / 16.0; errB[y + 1, x + 1] += db * 1 / 16.0; }
                        }
                    }
                }
            }
            else
            {
                for (int y = 0; y < gh; y++)
                {
                    for (int x = 0; x < gw; x++)
                    {
                        var c = cellColors[y, x];
                        result[y, x] = ToHex(Nearest(palette, c.Red, c.Green, c.Blue));
                    }
                }
            }

            return result;
        }

        private static List<SKColor> BuildPalette(SKColor[,] colors, int gw, int gh, int maxColors)
        {
            var distinct = new List<SKColor>(gw * gh);
            for (int y = 0; y < gh; y++)
            {
                for (int x = 0; x < gw; x++)
                {
                    distinct.Add(colors[y, x]);
                }
            }

            if (distinct.Count <= maxColors)
            {
                return distinct.Distinct().ToList();
            }

            // Median cut: recursively split the color box along its widest channel.
            var boxes = new List<List<SKColor>> { distinct };
            while (boxes.Count < maxColors)
            {
                var box = boxes.OrderByDescending(b => ChannelRange(b)).FirstOrDefault();
                if (box == null || box.Count < 2) break;
                boxes.Remove(box);

                var (lo, hi) = SplitBox(box);
                boxes.Add(lo);
                if (hi.Count > 0) boxes.Add(hi);
            }

            return boxes
                .Where(b => b.Count > 0)
                .Select(b => Average(b))
                .ToList();
        }

        private static int ChannelRange(List<SKColor> box)
        {
            int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
            foreach (var c in box)
            {
                rMin = Math.Min(rMin, c.Red); rMax = Math.Max(rMax, c.Red);
                gMin = Math.Min(gMin, c.Green); gMax = Math.Max(gMax, c.Green);
                bMin = Math.Min(bMin, c.Blue); bMax = Math.Max(bMax, c.Blue);
            }
            return Math.Max(rMax - rMin, Math.Max(gMax - gMin, bMax - bMin));
        }

        private static (List<SKColor> lo, List<SKColor> hi) SplitBox(List<SKColor> box)
        {
            // Split along the channel with the largest range, at the median value.
            int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
            foreach (var c in box)
            {
                rMin = Math.Min(rMin, c.Red); rMax = Math.Max(rMax, c.Red);
                gMin = Math.Min(gMin, c.Green); gMax = Math.Max(gMax, c.Green);
                bMin = Math.Min(bMin, c.Blue); bMax = Math.Max(bMax, c.Blue);
            }

            int rRange = rMax - rMin, gRange = gMax - gMin, bRange = bMax - bMin;
            var sorted = box.OrderBy(c =>
                rRange >= gRange && rRange >= bRange ? c.Red :
                gRange >= bRange ? c.Green : c.Blue).ToList();

            int mid = sorted.Count / 2;
            return (sorted.Take(mid).ToList(), sorted.Skip(mid).ToList());
        }

        private static SKColor Average(List<SKColor> box)
        {
            long r = 0, g = 0, b = 0;
            foreach (var c in box) { r += c.Red; g += c.Green; b += c.Blue; }
            int n = Math.Max(1, box.Count);
            return new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }

        private static SKColor Nearest(List<SKColor> palette, double r, double g, double b)
        {
            SKColor best = palette[0];
            double bestDist = double.MaxValue;
            foreach (var c in palette)
            {
                double dr = c.Red - r, dg = c.Green - g, db = c.Blue - b;
                double dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist) { bestDist = dist; best = c; }
            }
            return best;
        }

        private static double Clamp(double v) => Math.Clamp(v, 0, 255);

        private static string ToHex(SKColor c) => $"{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

        /// <summary>Deterministic coordinate gradient — used only when the image cannot be decoded.</summary>
        private static string[,] FallbackGradient(int gw, int gh)
        {
            var result = new string[gh, gw];
            for (int y = 0; y < gh; y++)
            {
                for (int x = 0; x < gw; x++)
                {
                    byte r = (byte)((x * 255) / Math.Max(1, gw - 1));
                    byte g = (byte)((y * 255) / Math.Max(1, gh - 1));
                    byte b = (byte)(255 - r);
                    result[y, x] = $"{r:X2}{g:X2}{b:X2}";
                }
            }
            return result;
        }
    }
}
