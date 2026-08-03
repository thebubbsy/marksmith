using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SkiaSharp;

namespace MarkSmith.Core.Composer
{
    /// <summary>Options for composing an image from native DrawingML shapes.</summary>
    public class ShapeComposerOptions
    {
        /// <summary>Cells per row (and column, aspect-corrected).</summary>
        public int Grid { get; set; } = 24;

        /// <summary>Allowed preset geometries, e.g. { "ellipse", "chevron" }. Must be non-empty.</summary>
        public List<string> Shapes { get; set; } = new() { "ellipse" };

        public bool Dither { get; set; } = true;
        public int PaletteColors { get; set; } = 16;
        public double InsetInches { get; set; } = 0.04;
    }

    /// <summary>One absolutely-positioned native DrawingML shape (wps:wsp / prstGeom).</summary>
    public class ComposedShape
    {
        public string Prst { get; set; } = "ellipse";
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public string Fill { get; set; } = "000000";
        public int Rot { get; set; }
    }

    /// <summary>
    /// Composes a raster image out of DrawingML shape primitives (the same prstGeom
    /// vocabulary SmartArt uses) — the "picture made of shapes" engine. The user picks the
    /// shape set; each image cell is rendered as the next shape from that set, filled with
    /// a sampled, palette-quantized (optionally Floyd–Steinberg dithered) color.
    /// </summary>
    public static class ImageShapeComposer
    {
        public const double DefaultCanvasWidthInches = 6.2;

        /// <summary>Hard ceiling on per-cell shape counts so the .docx stays practical in Word.</summary>
        public const int MaxCells = 1_500_000;

        public static List<ComposedShape> Compose(string imagePath, ShapeComposerOptions? options = null)
        {
            var opt = options ?? new ShapeComposerOptions();
            int grid = Math.Clamp(opt.Grid, 4, 8192);
            var shapes = opt.Shapes.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToLower()).Distinct().ToList();
            if (shapes.Count == 0) shapes.Add("ellipse");

            // "Line" alone => scanline mode: full-width strokes per row whose thickness follows
            // image darkness — reads as an image, not a grid of shapes.
            if (shapes.Count == 1 && shapes[0] == "line")
            {
                return ComposeScanlines(imagePath, opt, grid);
            }

            double imgW, imgH;
            int gx = grid, gy = 1;
            SKColor[,]? cells = null;
            try
            {
                using var src = SKBitmap.Decode(imagePath);
                if (src == null || src.Width <= 0 || src.Height <= 0) throw new InvalidDataException("decode failed");
                imgW = src.Width;
                imgH = src.Height;
                gx = grid;
                gy = (int)Math.Max(1, Math.Round(grid * src.Height / (double)src.Width));
                // Keep the docx practical: clamp total cells, preserving aspect.
                while ((long)gx * gy > MaxCells && gx > 4 && gy > 4)
                {
                    gx = Math.Max(4, (int)(gx * 0.75));
                    gy = (int)Math.Max(1, Math.Round(gx * imgH / imgW));
                }
                cells = SampleCells(src, gx, gy);
                grid = gx;
            }
            catch
            {
                // Unreadable image -> deterministic gradient so composition never throws.
                imgW = 32; imgH = 32;
                gy = (int)Math.Max(1, Math.Round(grid * imgH / imgW));
                cells = GradientCells(grid, gy);
            }

            gx = cells.GetLength(1);
            gy = cells.GetLength(0);

            var palette = BuildPalette(cells, gx, gy, Math.Max(2, opt.PaletteColors));

            double totalW = DefaultCanvasWidthInches;
            double totalH = totalW * (imgH / imgW);
            double cellW = totalW / gx;
            double cellH = totalH / gy;
            double inset = opt.InsetInches;

            var result = new List<ComposedShape>(gx * gy);
            if (opt.Dither)
            {
                var errR = new double[gy, gx];
                var errG = new double[gy, gx];
                var errB = new double[gy, gx];
                for (int y = 0; y < gy; y++)
                {
                    for (int x = 0; x < gx; x++)
                    {
                        var c = cells[y, x];
                        double cr = Math.Clamp(c.Red + errR[y, x], 0, 255);
                        double cg = Math.Clamp(c.Green + errG[y, x], 0, 255);
                        double cb = Math.Clamp(c.Blue + errB[y, x], 0, 255);
                        var nearest = Nearest(palette, cr, cg, cb);
                        AddShape(result, shapes, x, y, gx, gy, cellW, cellH, inset, nearest);
                        double dr = cr - nearest.Red, dg = cg - nearest.Green, db = cb - nearest.Blue;
                        if (x + 1 < gx) { errR[y, x + 1] += dr * 7 / 16.0; errG[y, x + 1] += dg * 7 / 16.0; errB[y, x + 1] += db * 7 / 16.0; }
                        if (y + 1 < gy)
                        {
                            if (x > 0) { errR[y + 1, x - 1] += dr * 3 / 16.0; errG[y + 1, x - 1] += dg * 3 / 16.0; errB[y + 1, x - 1] += db * 3 / 16.0; }
                            errR[y + 1, x] += dr * 5 / 16.0; errG[y + 1, x] += dg * 5 / 16.0; errB[y + 1, x] += db * 5 / 16.0;
                            if (x + 1 < gx) { errR[y + 1, x + 1] += dr / 16.0; errG[y + 1, x + 1] += dg / 16.0; errB[y + 1, x + 1] += db / 16.0; }
                        }
                    }
                }
            }
            else
            {
                for (int y = 0; y < gy; y++)
                {
                    for (int x = 0; x < gx; x++)
                    {
                        var c = cells[y, x];
                        AddShape(result, shapes, x, y, gx, gy, cellW, cellH, inset, Nearest(palette, c.Red, c.Green, c.Blue));
                    }
                }
            }

            return result;
        }

        private static void AddShape(List<ComposedShape> result, List<string> shapes, int x, int y, int gx, int gy,
            double cellW, double cellH, double inset, SKColor color)
        {
            string prst = shapes[(x + y * gx) % shapes.Count];
            double cx = x * cellW + cellW / 2;
            double cy = y * cellH + cellH / 2;

            double w = Math.Max(0.05, cellW - inset);
            double h = Math.Max(0.05, cellH - inset);
            if (prst is "chevron" or "parallelogram") { w = Math.Max(0.08, cellW - inset * 0.4); h = Math.Max(0.05, cellH * 0.62); }
            if (prst == "line") { w = Math.Max(0.08, cellW - inset * 0.4); h = Math.Max(0.02, cellH * 0.34); }

            result.Add(new ComposedShape
            {
                Prst = prst,
                X = cx - w / 2,
                Y = cy - h / 2,
                W = w,
                H = h,
                Fill = $"{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
            });
        }

        /// <summary>
        /// Scanline mode ("Line" selected alone): one full-width stroke per image row whose
        /// thickness follows luminance (darker rows -> thicker strokes), colored with the row's
        /// average color. This reads as an image — like an engraving / scanline rendering —
        /// with only rows×1 shapes, so densities of thousands are practical in Word.
        /// </summary>
        private static List<ComposedShape> ComposeScanlines(string imagePath, ShapeComposerOptions opt, int rows)
        {
            rows = Math.Clamp(rows, 8, 8192);
            double imgW, imgH;
            SKColor[] rowColors;
            try
            {
                using var src = SKBitmap.Decode(imagePath);
                if (src == null || src.Width <= 0 || src.Height <= 0) throw new InvalidDataException("decode failed");
                imgW = src.Width;
                imgH = src.Height;

                rowColors = new SKColor[rows];
                for (int y = 0; y < rows; y++)
                {
                    int y0 = y * src.Height / rows;
                    int y1 = Math.Max(y0 + 1, (y + 1) * src.Height / rows);
                    long r = 0, g = 0, b = 0, n = 0;
                    for (int py = y0; py < y1; py++)
                    for (int px = 0; px < src.Width; px++)
                    {
                        var c = src.GetPixel(px, py);
                        r += c.Red; g += c.Green; b += c.Blue; n++;
                    }
                    rowColors[y] = n > 0 ? new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n)) : new SKColor(0, 0, 0);
                }
            }
            catch
            {
                imgW = 32; imgH = 32;
                rowColors = new SKColor[rows];
                for (int y = 0; y < rows; y++)
                    rowColors[y] = new SKColor(0, (byte)(y * 255 / Math.Max(1, rows - 1)), 128);
            }

            double totalW = DefaultCanvasWidthInches;
            double totalH = totalW * (imgH / imgW);
            double rowH = totalH / rows;
            double maxThick = Math.Max(0.02, rowH * 0.96);
            double minThick = Math.Max(0.006, rowH * 0.06);

            var result = new List<ComposedShape>(rows);
            for (int y = 0; y < rows; y++)
            {
                var c = rowColors[y];
                double lum = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue; // 0..255
                double thickness = minThick + (1 - lum / 255.0) * (maxThick - minThick);
                result.Add(new ComposedShape
                {
                    Prst = "rect",
                    X = 0,
                    Y = y * rowH + (rowH - thickness) / 2,
                    W = totalW,
                    H = thickness,
                    Fill = $"{c.Red:X2}{c.Green:X2}{c.Blue:X2}"
                });
            }
            return result;
        }

        // ---- sampling / palette / dithering (same pipeline as RasterMosaicEngine) ----

        private static SKColor[,] SampleCells(SKBitmap src, int gx, int gy)
        {
            var cells = new SKColor[gy, gx];
            for (int y = 0; y < gy; y++)
            {
                for (int x = 0; x < gx; x++)
                {
                    int x0 = x * src.Width / gx;
                    int x1 = Math.Max(x0 + 1, (x + 1) * src.Width / gx);
                    int y0 = y * src.Height / gy;
                    int y1 = Math.Max(y0 + 1, (y + 1) * src.Height / gy);
                    long r = 0, g = 0, b = 0, n = 0;
                    for (int py = y0; py < y1; py++)
                    for (int px = x0; px < x1; px++)
                    {
                        var c = src.GetPixel(px, py);
                        r += c.Red; g += c.Green; b += c.Blue; n++;
                    }
                    cells[y, x] = n > 0 ? new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n)) : new SKColor(0, 0, 0);
                }
            }
            return cells;
        }

        private static SKColor[,] GradientCells(int gx, int gy)
        {
            var cells = new SKColor[gy, gx];
            for (int y = 0; y < gy; y++)
            for (int x = 0; x < gx; x++)
                cells[y, x] = new SKColor((byte)(x * 255 / Math.Max(1, gx - 1)), (byte)(y * 255 / Math.Max(1, gy - 1)), 128);
            return cells;
        }

        private static List<SKColor> BuildPalette(SKColor[,] cells, int gx, int gy, int maxColors)
        {
            var distinct = new List<SKColor>(gx * gy);
            for (int y = 0; y < gy; y++)
            for (int x = 0; x < gx; x++)
                distinct.Add(cells[y, x]);

            if (distinct.Count <= maxColors) return distinct.Distinct().ToList();

            var boxes = new List<List<SKColor>> { distinct };
            while (boxes.Count < maxColors)
            {
                var box = boxes.OrderByDescending(ChannelRange).FirstOrDefault();
                if (box == null || box.Count < 2) break;
                boxes.Remove(box);
                var (lo, hi) = SplitBox(box);
                boxes.Add(lo);
                if (hi.Count > 0) boxes.Add(hi);
            }
            return boxes.Where(b => b.Count > 0).Select(Average).ToList();
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

        // ---- SVG preview (maps prstGeom -> SVG elements) ----

        public static string RenderSvg(List<ComposedShape> shapes, double widthIn, double heightIn)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""100%"" height=""100%"" viewBox=""0 0 {widthIn * 96} {heightIn * 96}"">");
            sb.Append($@"<rect width=""100%"" height=""100%"" fill=""#ffffff""/>");
            foreach (var s in shapes)
            {
                sb.Append(SvgShape(s));
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string SvgShape(ComposedShape s)
        {
            double x = s.X * 96, y = s.Y * 96, w = s.W * 96, h = s.H * 96;
            double cx = x + w / 2, cy = y + h / 2;
            string fill = "#" + s.Fill;
            string transform = s.Rot != 0 ? $" transform=\"rotate({s.Rot} {cx} {cy})\"" : "";
            if (s.Prst == "line")
            {
                // prstGeom "line" draws corner-to-corner; match that in SVG.
                return $"<line x1=\"{x:F1}\" y1=\"{y:F1}\" x2=\"{x + w:F1}\" y2=\"{y + h:F1}\" stroke=\"{fill}\" stroke-width=\"{(s.H * 96 * 0.18):F1}\"{transform}/>";
            }
            return s.Prst switch
            {
                "ellipse" or "circle" => $"<ellipse cx=\"{cx:F1}\" cy=\"{cy:F1}\" rx=\"{w / 2:F1}\" ry=\"{h / 2:F1}\" fill=\"{fill}\"{transform}/>",
                "roundrect" => $"<rect x=\"{x:F1}\" y=\"{y:F1}\" width=\"{w:F1}\" height=\"{h:F1}\" rx=\"{(w * 0.16):F1}\" fill=\"{fill}\"{transform}/>",
                "diamond" => $"<polygon points=\"{cx:F1},{y:F1} {x + w:F1},{cy:F1} {cx:F1},{y + h:F1} {x:F1},{cy:F1}\" fill=\"{fill}\"{transform}/>",
                "triangle" => $"<polygon points=\"{cx:F1},{y:F1} {x + w:F1},{y + h:F1} {x:F1},{y + h:F1}\" fill=\"{fill}\"{transform}/>",
                "hexagon" => $"<polygon points=\"{x + w * 0.25:F1},{y:F1} {x + w * 0.75:F1},{y:F1} {x + w:F1},{cy:F1} {x + w * 0.75:F1},{y + h:F1} {x + w * 0.25:F1},{y + h:F1} {x:F1},{cy:F1}\" fill=\"{fill}\"{transform}/>",
                "parallelogram" => $"<polygon points=\"{x + w * 0.25:F1},{y:F1} {x + w:F1},{y:F1} {x + w * 0.75:F1},{y + h:F1} {x:F1},{y + h:F1}\" fill=\"{fill}\"{transform}/>",
                "chevron" => $"<polygon points=\"{x:F1},{y:F1} {x + w * 0.65:F1},{y:F1} {x + w:F1},{cy:F1} {x + w * 0.65:F1},{y + h:F1} {x:F1},{y + h:F1} {x + w * 0.35:F1},{cy:F1}\" fill=\"{fill}\"{transform}/>",
                _ => $"<rect x=\"{x:F1}\" y=\"{y:F1}\" width=\"{w:F1}\" height=\"{h:F1}\" fill=\"{fill}\"{transform}/>"
            };
        }
    }
}
