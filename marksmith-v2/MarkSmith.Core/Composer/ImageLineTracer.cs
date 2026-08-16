using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace MarkSmith.Core.Composer
{
    /// <summary>How the image is turned into line items.</summary>
    public enum LineTraceMode
    {
        /// <summary>Hatch engraving: horizontal ink lines whose thickness tracks local darkness.</summary>
        Engraved,

        /// <summary>Hard silhouette: solid bands in dark regions.</summary>
        Silhouette,

        /// <summary>Edge tracing: contour lines at luminance gradients.</summary>
        Edges,

        /// <summary>Full-width scanlines across each row.</summary>
        Scanlines,

        /// <summary>Dual-angle 45° cross-hatching: intersecting etching strokes that build shadow depth.</summary>
        CrossHatch,

        /// <summary>Topographic flow: undulating sinusoidal contour waves modulated by luminance.</summary>
        TopographicWaves,

        /// <summary>Directional 30° calligraphic chisel-tip ink strokes.</summary>
        Calligraphic
    }

    /// <summary>Options for <see cref="ImageLineTracer.TraceLines"/>.</summary>
    public class LineTraceOptions
    {
        /// <summary>Number of horizontal scanline rows (density). 16..16384.</summary>
        public int Rows { get; set; } = 480;

        public LineTraceMode Mode { get; set; } = LineTraceMode.Engraved;

        /// <summary>Sample each line's color from the image; false = monochrome ink.</summary>
        public bool UseColor { get; set; } = true;

        /// <summary>Luminance (0..255) below which a pixel is "ink" (Engraved/Silhouette/CrossHatch).</summary>
        public double InkThreshold { get; set; } = 205;

        /// <summary>Gradient magnitude above which a pixel is an edge (Edges mode).</summary>
        public double EdgeThreshold { get; set; } = 40;

        /// <summary>Ignore ink runs shorter than this fraction of the image width (speckle filter).</summary>
        public double MinRunFraction { get; set; } = 0.004;

        /// <summary>Ceiling for a single line's stroke thickness, in points.</summary>
        public double MaxThicknessPt { get; set; } = 8;

        /// <summary>Ceiling on total line items — keeps the .docx/.dotx practical in Word.</summary>
        public int MaxLines { get; set; } = 65_536;

        /// <summary>Canvas width in inches (aspect-corrected height derived from the image).</summary>
        public double CanvasWidthInches { get; set; } = ImageShapeComposer.DefaultCanvasWidthInches;
    }

    /// <summary>
    /// Turns a raster image into a dense, high-fidelity set of line items — the "MLShape"
    /// tracing engine. Each line is one <see cref="ComposedShape"/> with a PathPoints
    /// polyline (the same custGeom stroke mechanism the Word curve tracer uses), so every line is
    /// individually selectable on the studio canvas, renders as a native Word line in
    /// .docx/.dotx export, and shows as an SVG <path> in the HTML preview.
    /// </summary>
    public static class ImageLineTracer
    {
        public const int MinRows = 16;
        public const int MaxRows = 16384;

        /// <summary>Working width cap — dense tracings stay fast; the canvas is 6.2" wide.</summary>
        private const int MaxWorkingWidth = 1280;

        /// <summary>Every traced run is the same straight mid-line (0..100 local space) — share
        /// ONE list instance across up to 65k lines instead of allocating one per line.</summary>
        private static readonly List<(double X, double Y)> StraightLinePoints = new() { (0, 50), (100, 50) };
        private static readonly List<(double X, double Y)> DiagForwardPoints = new() { (0, 100), (100, 0) };
        private static readonly List<(double X, double Y)> DiagBackwardPoints = new() { (0, 0), (100, 100) };
        private static readonly List<(double X, double Y)> CalligraphicPoints = new() { (0, 75), (100, 25) };

        public static List<ComposedShape> TraceLines(string imagePath, LineTraceOptions? options = null)
        {
            var opt = options ?? new LineTraceOptions();
            int rows = Math.Clamp(opt.Rows, MinRows, MaxRows);

            double imgW, imgH;
            int pxW, pxH;
            SKColor[] pixels;
            try
            {
                using var src = SKBitmap.Decode(imagePath);
                if (src == null || src.Width <= 0 || src.Height <= 0) throw new InvalidDataException("decode failed");
                imgW = src.Width;
                imgH = src.Height;
                using var bmp = Downscale(src, MaxWorkingWidth);
                pxW = bmp.Width;
                pxH = bmp.Height;
                pixels = bmp.Pixels;
            }
            catch
            {
                imgW = 32; imgH = 32; pxW = 32; pxH = 32;
                pixels = new SKColor[pxW * pxH];
                for (int y = 0; y < pxH; y++)
                for (int x = 0; x < pxW; x++)
                    pixels[y * pxW + x] = new SKColor((byte)(x * 255 / Math.Max(1, pxW - 1)), (byte)(y * 255 / Math.Max(1, pxH - 1)), 128);
            }

            double totalW = opt.CanvasWidthInches;
            double totalH = totalW * (imgH / imgW);
            double rowH = totalH / rows;
            double cellW = totalW / pxW;
            double rowHpt = rowH * 72.0;
            int minRunPx = Math.Max(1, (int)Math.Round(opt.MinRunFraction * pxW));

            if (opt.Mode == LineTraceMode.TopographicWaves)
            {
                return EmitTopographicWaves(pixels, pxW, pxH, rows, opt, totalW, totalH, rowH);
            }
            if (opt.Mode == LineTraceMode.CrossHatch)
            {
                return EmitCrossHatch(pixels, pxW, pxH, rows, opt, totalW, totalH, rowH, rowHpt, cellW, minRunPx);
            }
            if (opt.Mode == LineTraceMode.Calligraphic)
            {
                return EmitCalligraphic(pixels, pxW, pxH, rows, opt, totalW, totalH, rowH, rowHpt, cellW, minRunPx);
            }

            int lineCount = CountRuns(pixels, pxW, pxH, rows, opt, minRunPx);
            int emitRows = rows;
            if (lineCount > opt.MaxLines)
            {
                emitRows = Math.Max(MinRows, (int)Math.Round(rows * (double)opt.MaxLines / lineCount));
            }

            return EmitLines(pixels, pxW, pxH, rows, emitRows, opt, minRunPx, rowH, rowHpt, cellW);
        }

        private static List<ComposedShape> EmitCrossHatch(SKColor[] px, int pxW, int pxH, int rows,
            LineTraceOptions opt, double totalW, double totalH, double rowH, double rowHpt, double cellW, int minRunPx)
        {
            var result = new List<ComposedShape>();
            var luma = new double[pxW];
            double inkThreshold = Math.Clamp(opt.InkThreshold, 0, 255);
            string inkHex = opt.UseColor ? "" : "181818";
            int step = Math.Max(1, pxW / Math.Max(8, rows));

            // Forward hatch lines (/)
            for (int y = 0; y < rows; y++)
            {
                if (result.Count >= opt.MaxLines) break;
                int py = Math.Min(pxH - 1, y * pxH / rows);
                LoadLuma(px, pxW, py, luma);
                double bandY = y * rowH;

                for (int x = 0; x < pxW - step; x += step)
                {
                    double lum = luma[x];
                    if (lum > inkThreshold) continue;
                    double darkness = 1 - lum / 255.0;
                    double thickPt = Math.Clamp(0.4 + darkness * rowHpt * 1.6, 0.3, opt.MaxThicknessPt);
                    double thickIn = thickPt / 72.0;

                    var c = px[py * pxW + x];
                    string fill = inkHex.Length > 0 ? inkHex : $"{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

                    result.Add(new ComposedShape
                    {
                        Prst = "sketch",
                        X = x * cellW,
                        Y = bandY,
                        W = step * cellW * 1.4,
                        H = Math.Max(thickIn, rowH),
                        Fill = fill,
                        PathPoints = DiagForwardPoints,
                        StrokeWidthPt = thickPt
                    });

                    // Cross hatch in shadows (darkness > 0.45)
                    if (darkness > 0.45 && result.Count < opt.MaxLines)
                    {
                        result.Add(new ComposedShape
                        {
                            Prst = "sketch",
                            X = x * cellW,
                            Y = bandY,
                            W = step * cellW * 1.4,
                            H = Math.Max(thickIn, rowH),
                            Fill = fill,
                            PathPoints = DiagBackwardPoints,
                            StrokeWidthPt = thickPt * 0.85
                        });
                    }
                }
            }
            return result;
        }

        private static List<ComposedShape> EmitCalligraphic(SKColor[] px, int pxW, int pxH, int rows,
            LineTraceOptions opt, double totalW, double totalH, double rowH, double rowHpt, double cellW, int minRunPx)
        {
            var result = new List<ComposedShape>();
            var luma = new double[pxW];
            double inkThreshold = Math.Clamp(opt.InkThreshold, 0, 255);
            string inkHex = opt.UseColor ? "" : "181818";
            int step = Math.Max(2, pxW / Math.Max(8, rows / 2));

            for (int y = 0; y < rows; y++)
            {
                if (result.Count >= opt.MaxLines) break;
                int py = Math.Min(pxH - 1, y * pxH / rows);
                LoadLuma(px, pxW, py, luma);
                double bandY = y * rowH;

                for (int x = 0; x < pxW - step; x += step)
                {
                    double lum = luma[x];
                    if (lum > inkThreshold) continue;
                    double darkness = 1 - lum / 255.0;
                    double thickPt = Math.Clamp(0.6 + darkness * rowHpt * 2.2, 0.4, opt.MaxThicknessPt);
                    double thickIn = thickPt / 72.0;

                    var c = px[py * pxW + x];
                    string fill = inkHex.Length > 0 ? inkHex : $"{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

                    result.Add(new ComposedShape
                    {
                        Prst = "sketch",
                        X = x * cellW,
                        Y = bandY,
                        W = step * cellW * 1.25,
                        H = Math.Max(thickIn, rowH * 1.2),
                        Fill = fill,
                        PathPoints = CalligraphicPoints,
                        StrokeWidthPt = thickPt
                    });
                }
            }
            return result;
        }

        private static List<ComposedShape> EmitTopographicWaves(SKColor[] px, int pxW, int pxH, int rows,
            LineTraceOptions opt, double totalW, double totalH, double rowH)
        {
            var result = new List<ComposedShape>();
            int segments = Math.Clamp(rows / 2, 16, 120);
            double segW = totalW / segments;
            string inkHex = opt.UseColor ? "" : "181818";

            for (int y = 0; y < rows; y++)
            {
                if (result.Count >= opt.MaxLines) break;
                int py = Math.Min(pxH - 1, y * pxH / rows);
                double bandY = y * rowH;
                double phase = y * 0.85;

                for (int s = 0; s < segments; s++)
                {
                    int pxX = Math.Min(pxW - 1, s * pxW / segments);
                    var c = px[py * pxW + pxX];
                    double lum = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                    if (lum > 248) continue; // paper

                    double darkness = 1 - lum / 255.0;
                    double strokePt = Math.Clamp(0.5 + darkness * 3.5, 0.4, opt.MaxThicknessPt);
                    double amp = Math.Max(0.04, rowH * (0.8 + darkness * 1.4));

                    var pts = new List<(double X, double Y)>();
                    const int subSteps = 6;
                    for (int i = 0; i <= subSteps; i++)
                    {
                        double t = i / (double)subSteps;
                        double pxPos = t * 100;
                        double wave = Math.Sin(t * Math.PI * 2 + phase + s * 0.7);
                        double pyPos = 50 + 40 * wave * darkness;
                        pts.Add((pxPos, pyPos));
                    }

                    string fill = inkHex.Length > 0 ? inkHex : $"{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
                    result.Add(new ComposedShape
                    {
                        Prst = "sketch",
                        X = s * segW,
                        Y = bandY + (rowH - amp) / 2,
                        W = segW * 1.02,
                        H = amp,
                        Fill = fill,
                        PathPoints = pts,
                        StrokeWidthPt = strokePt
                    });
                }
            }
            return result;
        }

        // ---- pass 1: count ink runs (feeds the adaptive cap) ----

        private static int CountRuns(SKColor[] px, int pxW, int pxH, int rows, LineTraceOptions opt, int minRunPx)
        {
            int total = 0;
            var luma = new double[pxW];
            var prev = new double[pxW];
            var next = new double[pxW];
            bool edges = opt.Mode == LineTraceMode.Edges;
            for (int y = 0; y < rows; y++)
            {
                int py = Math.Min(pxH - 1, y * pxH / rows);
                LoadLuma(px, pxW, py, luma);
                if (edges)
                {
                    LoadLuma(px, pxW, Math.Max(0, py - 1), prev);
                    LoadLuma(px, pxW, Math.Min(pxH - 1, py + 1), next);
                }
                total += CountRow(luma, prev, next, pxW, opt, minRunPx, edges);
            }
            return total;
        }

        private static int CountRow(double[] luma, double[] prev, double[] next, int pxW,
            LineTraceOptions opt, int minRunPx, bool edges)
        {
            int runs = 0, run = 0;
            for (int x = 0; x <= pxW; x++)
            {
                bool ink = x < pxW && IsInk(luma, prev, next, x, opt, edges);
                if (ink)
                {
                    run++;
                }
                else if (run >= minRunPx)
                {
                    runs++;
                    run = 0;
                }
                else
                {
                    run = 0;
                }
            }
            return runs;
        }

        // ---- pass 2: emit one line item per run ----

        private static List<ComposedShape> EmitLines(SKColor[] px, int pxW, int pxH, int rows, int emitRows,
            LineTraceOptions opt, int minRunPx, double rowH, double rowHpt, double cellW)
        {
            var result = new List<ComposedShape>();
            var luma = new double[pxW];
            var prev = new double[pxW];
            var next = new double[pxW];
            bool edges = opt.Mode == LineTraceMode.Edges;
            bool scanlines = opt.Mode == LineTraceMode.Scanlines;
            bool silhouette = opt.Mode == LineTraceMode.Silhouette;
            double inkThreshold = Math.Clamp(opt.InkThreshold, 0, 255);
            string inkHex = opt.UseColor ? "" : "181818";

            // When emitRows < rows (cap engaged), sample rows EVENLY across the image so no region
            // is dropped; each sampled row maps to a distinct source row (emitRows <= rows).
            for (int r = 0; r < emitRows; r++)
            {
                if (result.Count >= opt.MaxLines) break;
                int y = r * rows / emitRows;
                double bandY = y * rowH;
                int py = Math.Min(pxH - 1, y * pxH / rows);
                LoadLuma(px, pxW, py, luma);
                if (edges)
                {
                    LoadLuma(px, pxW, Math.Max(0, py - 1), prev);
                    LoadLuma(px, pxW, Math.Min(pxH - 1, py + 1), next);
                }

                if (scanlines)
                {
                    EmitScanline(result, px, pxW, py, luma, bandY, rowH, rowHpt, inkThreshold, opt, inkHex);
                    continue;
                }

                int x = 0;
                while (x < pxW)
                {
                    while (x < pxW && !IsInk(luma, prev, next, x, opt, edges)) x++;
                    int start = x;
                    while (x < pxW && IsInk(luma, prev, next, x, opt, edges)) x++;
                    int runLen = x - start;
                    if (runLen >= minRunPx)
                    {
                        EmitRun(result, px, pxW, py, luma, start, runLen, bandY, rowH, rowHpt,
                            inkThreshold, silhouette, opt, inkHex, cellW);
                    }
                }
            }
            return result;
        }

        private static bool IsInk(double[] luma, double[] prev, double[] next, int x, LineTraceOptions opt, bool edges)
        {
            if (edges)
            {
                int xl = Math.Max(0, x - 1), xr = Math.Min(luma.Length - 1, x + 1);
                double g = Math.Abs(next[x] - prev[x]) + Math.Abs(luma[xr] - luma[xl]);
                return g > opt.EdgeThreshold;
            }
            return luma[x] < opt.InkThreshold;
        }

        private static void EmitRun(List<ComposedShape> result, SKColor[] px, int pxW, int py, double[] luma,
            int start, int runLen, double bandY, double rowH, double rowHpt,
            double inkThreshold, bool silhouette, LineTraceOptions opt, string inkHex, double cellW)
        {
            int rowBase = py * pxW;
            long r = 0, g = 0, b = 0;
            double lum = 0;
            for (int i = 0; i < runLen; i++)
            {
                var c = px[rowBase + start + i];
                r += c.Red; g += c.Green; b += c.Blue;
                lum += luma[start + i];
            }
            int n = Math.Max(1, runLen);
            double darkness = Math.Clamp((inkThreshold - lum / n) / Math.Max(1, inkThreshold), 0, 1);
            if (silhouette) darkness = 1; // solid band across the shape body

            // Thickness is capped at the row band height so a line never spills into the adjacent
            // band — non-overlap holds at ANY density (this is what keeps dense traces readable).
            double bandCap = Math.Min(opt.MaxThicknessPt, rowHpt);
            double thicknessPt = silhouette
                ? rowHpt
                : Math.Clamp(0.4 + darkness * rowHpt * 1.4, Math.Min(0.3, rowHpt), bandCap);
            double thicknessIn = thicknessPt / 72.0;

            string fill = inkHex.Length > 0
                ? inkHex
                : $"{((byte)(r / n)):X2}{((byte)(g / n)):X2}{((byte)(b / n)):X2}";

            // One straight line item spanning the run (0..100 local space, vertically centred).
            result.Add(new ComposedShape
            {
                Prst = "sketch",
                X = start * cellW,
                Y = bandY + Math.Max(0, (rowH - thicknessIn) / 2),
                W = runLen * cellW,
                H = thicknessIn,
                Fill = fill,
                PathPoints = StraightLinePoints,
                StrokeWidthPt = thicknessPt
            });
        }

        private static void EmitScanline(List<ComposedShape> result, SKColor[] px, int pxW, int py, double[] luma,
            double bandY, double rowH, double rowHpt, double inkThreshold, LineTraceOptions opt, string inkHex)
        {
            int rowBase = py * pxW;
            long r = 0, g = 0, b = 0;
            double lum = 0;
            for (int x = 0; x < pxW; x++)
            {
                var c = px[rowBase + x];
                r += c.Red; g += c.Green; b += c.Blue;
                lum += luma[x];
            }
            int n = Math.Max(1, pxW);
            double avgLum = lum / n;
            if (avgLum > 245) return; // paper
            double darkness = Math.Clamp(1 - avgLum / 255.0, 0, 1);
            double bandCap = Math.Min(opt.MaxThicknessPt, rowHpt);
            double thicknessPt = Math.Clamp(0.15 + darkness * rowHpt * 0.95, Math.Min(0.1, rowHpt), bandCap);
            double thicknessIn = thicknessPt / 72.0;

            string fill = inkHex.Length > 0
                ? inkHex
                : $"{((byte)(r / n)):X2}{((byte)(g / n)):X2}{((byte)(b / n)):X2}";

            result.Add(new ComposedShape
            {
                Prst = "sketch",
                X = 0,
                Y = bandY + Math.Max(0, (rowH - thicknessIn) / 2),
                W = opt.CanvasWidthInches,
                H = thicknessIn,
                Fill = fill,
                PathPoints = StraightLinePoints,
                StrokeWidthPt = thicknessPt
            });
        }

        private static void LoadLuma(SKColor[] px, int pxW, int py, double[] luma)
        {
            int row = py * pxW;
            for (int x = 0; x < pxW; x++)
            {
                var c = px[row + x];
                luma[x] = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            }
        }

        // ---- preview ----

        /// <summary>Rasterizes a traced composition to a PNG for the studio canvas and thumbnails.
        /// Capped at <paramref name="previewCap"/> lines for the raster pass only — export and the
        /// markdown/SVG paths always carry the FULL line set. When over the cap, lines are sampled
        /// EVENLY (every Nth) so the preview still shows the whole picture, not just the top.</summary>
        public static byte[]? RenderPreviewPng(List<ComposedShape> shapes, double widthIn, double heightIn,
            int previewCap = 24000)
        {
            if (shapes.Count == 0) return null;
            List<ComposedShape> limited;
            if (shapes.Count <= previewCap)
            {
                limited = shapes;
            }
            else
            {
                int stride = (int)Math.Ceiling(shapes.Count / (double)previewCap);
                limited = new List<ComposedShape>(previewCap);
                for (int i = 0; i < shapes.Count; i += stride)
                    limited.Add(shapes[i]);
            }
            string svg = ImageShapeComposer.RenderSvg(limited, widthIn, heightIn);
            return MarkSmith.Services.SvgRasterizer.ToPng(svg, scale: 2.0);
        }

        private static SKBitmap Downscale(SKBitmap src, int maxWidth)
        {
            if (src.Width <= maxWidth) return src.Copy();
            int w = maxWidth;
            int h = Math.Max(1, (int)Math.Round(src.Height * (maxWidth / (double)src.Width)));
            var dst = new SKBitmap(new SKImageInfo(w, h));
            using (var canvas = new SKCanvas(dst))
            {
                canvas.DrawBitmap(src, new SKRect(0, 0, w, h));
            }
            return dst;
        }
    }
}
