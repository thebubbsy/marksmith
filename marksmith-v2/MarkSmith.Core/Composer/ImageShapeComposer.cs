using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

        /// <summary>Open polyline (0..100 local space) drawn as a custGeom stroke — curved sketch lines.</summary>
        public List<(double X, double Y)>? PathPoints { get; set; }

        /// <summary>Line thickness in points when PathPoints is set.</summary>
        public double StrokeWidthPt { get; set; } = 1.5;
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

        /// <summary>
        /// Sketch mode: the image is drawn with short, CURVED strokes (native custGeom
        /// polylines drawn with a thick line — the same Word-proven mechanism the Mermaid
        /// curve tracer uses). Each row splits into segments; per segment a wavy open polyline
        /// stroke is emitted whose thickness follows local luminance. Light areas stay paper.
        /// </summary>
        public static List<ComposedShape> ComposeSketch(string imagePath, ShapeComposerOptions? options = null)
        {
            var opt = options ?? new ShapeComposerOptions();
            int rows = Math.Clamp(opt.Grid, 8, 8192);
            int segments = 12;

            double imgW, imgH;
            SKColor[,]? cells = null;
            try
            {
                using var src = SKBitmap.Decode(imagePath);
                if (src == null || src.Width <= 0 || src.Height <= 0) throw new InvalidDataException("decode failed");
                imgW = src.Width;
                imgH = src.Height;
                cells = SampleCells(src, segments, rows);
            }
            catch
            {
                imgW = 32; imgH = 32;
                cells = GradientCells(segments, rows);
            }

            double totalW = DefaultCanvasWidthInches;
            double totalH = totalW * (imgH / imgW);
            double rowH = totalH / rows;
            double segW = totalW / segments;

            var result = new List<ComposedShape>(rows * segments);
            for (int y = 0; y < rows; y++)
            {
                double phase = y * 1.7;
                for (int x = 0; x < segments; x++)
                {
                    var c = cells[y, x];
                    double lum = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                    if (lum > 232) continue; // near-white: paper, no stroke

                    double darkness = 1 - lum / 255.0;
                    double amp = Math.Max(0.02, rowH * (0.6 + darkness * 0.8));
                    double strokePt = 0.6 + darkness * 2.8;

                    // Wavy open polyline across the segment, 0..100 local space.
                    var pts = new List<(double X, double Y)>();
                    const int n = 5;
                    for (int i = 0; i <= n; i++)
                    {
                        double t = i / (double)n;
                        double px = t * 100;
                        double py = 50 + 32 * Math.Sin(t * Math.PI * 2 + phase + x * 1.1);
                        pts.Add((px, py));
                    }

                    result.Add(new ComposedShape
                    {
                        Prst = "sketch",
                        X = x * segW,
                        Y = y * rowH + (rowH - amp) / 2,
                        W = segW,
                        H = amp,
                        Fill = $"{c.Red:X2}{c.Green:X2}{c.Blue:X2}",
                        PathPoints = pts,
                        StrokeWidthPt = strokePt
                    });
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

            // Custom-geometry curved strokes (sketch mode): polyline in 0..100 space.
            if (s.PathPoints is { Count: >= 2 })
            {
                var d = new StringBuilder("M");
                foreach (var p in s.PathPoints)
                {
                    d.Append(' ').Append(p.X.ToString("F1", CultureInfo.InvariantCulture))
                     .Append(' ').Append(p.Y.ToString("F1", CultureInfo.InvariantCulture));
                }
                // stroke-width must be expressed in PATH space: the transform scales the path by
                // (w/100, h/100), and stroke thickness is perpendicular to the line, so it scales
                // by h/100 — NOT w/100. swPath·(h/100) must equal StrokeWidthPt in px, hence the
                // divisor is h. For traced lines H = StrokeWidthPt/72" so swPath lands at exactly
                // 100 — the stroke fills the box, matching Word's absolute a:ln width.
                double swPath = (s.StrokeWidthPt * 96 / 72.0) * 100.0 / Math.Max(1, h);
                // Straight traced lines use butt caps so adjacent runs never bleed into each other.
                string cap = s.PathPoints.Count == 2 ? "butt" : "round";
                return $"<path d=\"{d}\" transform=\"translate({x:F1},{y:F1}) scale({w / 100:F4},{h / 100:F4}){transform}\" fill=\"none\" stroke=\"{fill}\" stroke-width=\"{swPath:F2}\" stroke-linecap=\"{cap}\"/>";
            }
            if (s.Prst == "line")
            {
                // prstGeom "line" draws corner-to-corner; match that in SVG.
                return $"<line x1=\"{x:F1}\" y1=\"{y:F1}\" x2=\"{x + w:F1}\" y2=\"{y + h:F1}\" stroke=\"{fill}\" stroke-width=\"{(s.H * 96 * 0.18):F1}\"{transform}/>";
            }

            // Curved / sketch-vibe presets (approximate but recognizable).
            switch (s.Prst)
            {
                case "heart":
                    return $"<path d=\"M {cx:F1} {y + h * 0.72:F1} C {x:F1} {y + h * 0.30:F1}, {x + w * 0.08:F1} {y:F1}, {cx:F1} {y + h * 0.22:F1} C {x + w * 0.92:F1} {y:F1}, {x + w:F1} {y + h * 0.30:F1}, {cx:F1} {y + h * 0.72:F1} Z\" fill=\"{fill}\"{transform}/>";
                case "moon":
                    return $"<path d=\"M {x + w * 0.78:F1} {y:F1} A {w * 0.5:F1} {h * 0.5:F1} 0 1 1 {x + w * 0.22:F1} {y + h:F1} A {w * 0.42:F1} {h * 0.42:F1} 0 1 0 {x + w * 0.78:F1} {y:F1} Z\" fill=\"{fill}\"{transform}/>";
                case "arc":
                    return $"<path d=\"M {x + w * 0.06:F1} {y + h:F1} A {w * 0.44:F1} {h * 0.44:F1} 0 1 1 {x + w * 0.94:F1} {y + h:F1}\" fill=\"none\" stroke=\"{fill}\" stroke-width=\"{(h * 96 * 0.18):F1}\" stroke-linecap=\"round\"{transform}/>";
                case "circulararrow":
                    return $"<g{transform}><path d=\"M {cx:F1} {y + h * 0.12:F1} A {w * 0.38:F1} {h * 0.38:F1} 0 1 1 {x + w * 0.96:F1} {cy:F1}\" fill=\"none\" stroke=\"{fill}\" stroke-width=\"{(h * 96 * 0.14):F1}\" stroke-linecap=\"round\"/><polygon points=\"{x + w * 0.96:F1},{cy - h * 0.10:F1} {x + w:F1},{cy:F1} {x + w * 0.96:F1},{cy + h * 0.10:F1}\" fill=\"{fill}\"/></g>";
                case "smileyface":
                    return $"<g{transform}><circle cx=\"{cx:F1}\" cy=\"{cy:F1}\" r=\"{Math.Min(w, h) / 2:F1}\" fill=\"{fill}\"/><circle cx=\"{cx - w * 0.18:F1}\" cy=\"{cy - h * 0.12:F1}\" r=\"{Math.Min(w, h) * 0.07:F1}\" fill=\"#ffffff\"/><circle cx=\"{cx + w * 0.18:F1}\" cy=\"{cy - h * 0.12:F1}\" r=\"{Math.Min(w, h) * 0.07:F1}\" fill=\"#ffffff\"/><path d=\"M {cx - w * 0.22:F1} {cy + h * 0.05:F1} Q {cx:F1} {cy + h * 0.30:F1} {cx + w * 0.22:F1} {cy + h * 0.05:F1}\" fill=\"none\" stroke=\"#ffffff\" stroke-width=\"{(h * 96 * 0.06):F1}\" stroke-linecap=\"round\"/></g>";
                case "cloud":
                    return $"<g{transform}><ellipse cx=\"{cx:F1}\" cy=\"{y + h * 0.55:F1}\" rx=\"{w * 0.42:F1}\" ry=\"{h * 0.34:F1}\" fill=\"{fill}\"/><circle cx=\"{x + w * 0.30:F1}\" cy=\"{y + h * 0.40:F1}\" r=\"{h * 0.26:F1}\" fill=\"{fill}\"/><circle cx=\"{x + w * 0.70:F1}\" cy=\"{y + h * 0.40:F1}\" r=\"{h * 0.26:F1}\" fill=\"{fill}\"/></g>";
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
