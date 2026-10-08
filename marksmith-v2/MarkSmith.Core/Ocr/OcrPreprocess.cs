using SkiaSharp;

namespace MarkSmith.Ocr;

/// <summary>A black-and-white page: true = ink.</summary>
public sealed class BinaryImage
{
    public BinaryImage(int width, int height, bool[] ink) { Width = width; Height = height; Ink = ink; }
    public int Width { get; }
    public int Height { get; }
    public bool[] Ink { get; }
    public bool this[int x, int y] => Ink[y * Width + x];
}

/// <summary>
/// Page clean-up shared by every engine (and the first stage of MarkSmith OCR): grey, flatten
/// uneven lighting, adaptive (Sauvola) threshold, deskew.
/// </summary>
public static class OcrPreprocess
{
    /// <summary>
    /// Straightens a skewed scan. Every engine gets the straightened page: a few degrees of tilt on
    /// a long line makes neighbouring lines overlap, and both Tesseract and PaddleOCR lose them.
    /// Returns the input itself when it is already straight (|angle| &lt; 0.15°).
    /// </summary>
    public static SKBitmap Deskew(SKBitmap page, out double angle)
    {
        // The angle is measured on a copy at most 1600 px on its long side: a full A4 scan at
        // 300 dpi is 8.7 million pixels, and the tilt is just as clear at a third of the size.
        double shrink = Math.Min(1.0, 1600.0 / Math.Max(page.Width, page.Height));
        using var small = shrink < 1 ? OcrGeometry.Resize(page, (int)(page.Width * shrink), (int)(page.Height * shrink)) : null;
        var probe = small ?? page;
        var gray = OcrGeometry.ToGray(probe);
        var bin = Binarize(gray, probe.Width, probe.Height);
        angle = EstimateSkew(bin);
        if (Math.Abs(angle) < 0.15) return page;
        return Rotate(page, angle);
    }

    public static SKBitmap Rotate(SKBitmap src, double degrees)
    {
        double rad = Math.Abs(degrees) * Math.PI / 180;
        int w = (int)Math.Ceiling(src.Width * Math.Cos(rad) + src.Height * Math.Sin(rad));
        int h = (int)Math.Ceiling(src.Width * Math.Sin(rad) + src.Height * Math.Cos(rad));
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees((float)degrees);
        canvas.Translate(-src.Width / 2f, -src.Height / 2f);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(src, 0, 0, paint);
        return dst;
    }

    /// <summary>
    /// Sauvola's adaptive threshold on a background-flattened page: each pixel is ink when it is
    /// darker than mean·(1 + k·(σ/R − 1)) over a window around it, which copes with shading,
    /// faded ink and grey paper where one global threshold fails.
    /// </summary>
    public static BinaryImage Binarize(byte[] gray, int width, int height, int window = 0, double k = 0.25)
    {
        var flat = FlattenBackground(gray, width, height);
        if (window <= 0) window = Math.Clamp(Math.Min(width, height) / 12, 25, 91) | 1;
        int r = window / 2;
        // Integral images of the value and its square.
        var sum = new double[(width + 1) * (height + 1)];
        var sq = new double[(width + 1) * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            double rowSum = 0, rowSq = 0;
            for (int x = 0; x < width; x++)
            {
                double v = flat[y * width + x];
                rowSum += v; rowSq += v * v;
                int i = (y + 1) * (width + 1) + x + 1;
                sum[i] = sum[i - width - 1] + rowSum;
                sq[i] = sq[i - width - 1] + rowSq;
            }
        }
        var ink = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(height - 1, y + r);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(width - 1, x + r);
                double n = (x1 - x0 + 1) * (y1 - y0 + 1);
                int a = y0 * (width + 1) + x0, b = y0 * (width + 1) + x1 + 1, c = (y1 + 1) * (width + 1) + x0, d = (y1 + 1) * (width + 1) + x1 + 1;
                double m = (sum[d] - sum[b] - sum[c] + sum[a]) / n;
                double s2 = (sq[d] - sq[b] - sq[c] + sq[a]) / n - m * m;
                double sd = Math.Sqrt(Math.Max(0, s2));
                double t = m * (1 + k * (sd / 128 - 1));
                byte v = flat[y * width + x];
                // A floor on contrast keeps flat paper (σ≈0) from turning into ink speckle.
                ink[y * width + x] = v < t && v < 235 && (m - v) > 12;
            }
        }
        return new BinaryImage(width, height, ink);
    }

    /// <summary>Divides out the paper's own brightness (estimated by a coarse max filter), so
    /// shading and grey paper come out white and the ink keeps its contrast.</summary>
    public static byte[] FlattenBackground(byte[] gray, int width, int height)
    {
        const int cell = 32;
        int cw = (width + cell - 1) / cell, ch = (height + cell - 1) / cell;
        var bg = new double[cw * ch];
        for (int cy = 0; cy < ch; cy++)
            for (int cx = 0; cx < cw; cx++)
            {
                // A high percentile of the cell, not its max, so a speck of glare doesn't count.
                var hist = new int[256];
                int count = 0;
                for (int y = cy * cell; y < Math.Min(height, cy * cell + cell); y++)
                    for (int x = cx * cell; x < Math.Min(width, cx * cell + cell); x++) { hist[gray[y * width + x]]++; count++; }
                int target = (int)(count * 0.9), acc = 0, v = 255;
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= target) { v = i; break; } }
                bg[cy * cw + cx] = v;
            }
        // Cells that are mostly ink (a big dark heading) borrow from their neighbours.
        var smooth = new double[bg.Length];
        for (int cy = 0; cy < ch; cy++)
            for (int cx = 0; cx < cw; cx++)
            {
                double best = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx >= 0 && ny >= 0 && nx < cw && ny < ch) best = Math.Max(best, bg[ny * cw + nx]);
                    }
                smooth[cy * cw + cx] = Math.Max(best, 40);
            }
        var outp = new byte[gray.Length];
        for (int y = 0; y < height; y++)
        {
            double fy = Math.Clamp((y + 0.5) / cell - 0.5, 0, ch - 1);
            int y0 = (int)fy, y1 = Math.Min(ch - 1, y0 + 1);
            double ty = fy - y0;
            for (int x = 0; x < width; x++)
            {
                double fx = Math.Clamp((x + 0.5) / cell - 0.5, 0, cw - 1);
                int x0 = (int)fx, x1 = Math.Min(cw - 1, x0 + 1);
                double tx = fx - x0;
                double b = smooth[y0 * cw + x0] * (1 - tx) * (1 - ty) + smooth[y0 * cw + x1] * tx * (1 - ty)
                         + smooth[y1 * cw + x0] * (1 - tx) * ty + smooth[y1 * cw + x1] * tx * ty;
                outp[y * width + x] = (byte)Math.Clamp(gray[y * width + x] * 255.0 / b, 0, 255);
            }
        }
        return outp;
    }

    /// <summary>
    /// The rotation that levels the page's lines, in degrees, found by projection profiles:
    /// the angle at which ink rows are most sharply peaked. A coarse ±6° search, then a fine one.
    /// </summary>
    public static double EstimateSkew(BinaryImage bin)
    {
        // Sample ink pixels (at most ~150k) to keep this fast on a full page.
        var pts = new List<(int X, int Y)>();
        int total = bin.Ink.Count(v => v);
        int step = Math.Max(1, total / 150_000);
        int seen = 0;
        for (int i = 0; i < bin.Ink.Length; i++)
            if (bin.Ink[i] && seen++ % step == 0) pts.Add((i % bin.Width, i / bin.Width));
        if (pts.Count < 50) return 0;

        double Score(double deg)
        {
            double rad = deg * Math.PI / 180, sin = Math.Sin(rad), cos = Math.Cos(rad);
            int bins = bin.Height + bin.Width;
            var hist = new int[bins * 2];
            foreach (var (x, y) in pts)
            {
                int b = (int)(y * cos + x * sin) + bins;
                if (b >= 0 && b < hist.Length) hist[b]++;
            }
            double s = 0;
            for (int i = 1; i < hist.Length; i++) { double d = hist[i] - hist[i - 1]; s += d * d; }
            return s;
        }

        double best = 0, bestScore = Score(0);
        for (double a = -6; a <= 6.001; a += 0.3)
        {
            double sc = Score(a);
            if (sc > bestScore) { bestScore = sc; best = a; }
        }
        double centre = best;
        for (double a = centre - 0.3; a <= centre + 0.3001; a += 0.05)
        {
            double sc = Score(a);
            if (sc > bestScore) { bestScore = sc; best = a; }
        }
        // The rotation (degrees, Skia's clockwise-positive) that levels the lines.
        return best;
    }
}
