using SkiaSharp;

namespace MarkSmith.Ocr;

/// <summary>A rotated rectangle: centre, size and angle in degrees (clockwise, screen axes).</summary>
public readonly record struct RotatedBox(float CenterX, float CenterY, float Width, float Height, float Angle)
{
    public SKRect Bounds
    {
        get
        {
            var c = Corners();
            float minX = c.Min(p => p.X), maxX = c.Max(p => p.X), minY = c.Min(p => p.Y), maxY = c.Max(p => p.Y);
            return new SKRect(minX, minY, maxX, maxY);
        }
    }

    public SKPoint[] Corners()
    {
        double rad = Angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        float hw = Width / 2, hh = Height / 2;
        float cx = CenterX, cy = CenterY;
        var local = new[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) };
        return local.Select(p => new SKPoint((float)(cx + p.Item1 * cos - p.Item2 * sin), (float)(cy + p.Item1 * sin + p.Item2 * cos))).ToArray();
    }
}

/// <summary>Image and geometry helpers the OCR engines share.</summary>
public static class OcrGeometry
{
    /// <summary>8-bit luminance of every pixel, row-major.</summary>
    public static byte[] ToGray(SKBitmap bitmap)
    {
        using var src = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        var bmp = src ?? bitmap;
        int w = bmp.Width, h = bmp.Height;
        var gray = new byte[w * h];
        var span = bmp.GetPixelSpan();
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            // BGRA; alpha composited over white so transparent PNGs read as paper.
            int a = span[p + 3];
            int lum = (span[p] * 29 + span[p + 1] * 150 + span[p + 2] * 77) >> 8;
            gray[i] = (byte)((lum * a + 255 * (255 - a)) / 255);
        }
        return gray;
    }

    /// <summary>A copy of the bitmap as 32-bit BGRA (what the ONNX preprocessing reads).</summary>
    public static SKBitmap ToBgra(SKBitmap bitmap)
    {
        var dst = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(bitmap, 0, 0);
        return dst;
    }

    public static SKBitmap Resize(SKBitmap src, int width, int height)
    {
        var dst = new SKBitmap(new SKImageInfo(Math.Max(1, width), Math.Max(1, height), SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(src, new SKRect(0, 0, src.Width, src.Height), new SKRect(0, 0, dst.Width, dst.Height), paint);
        return dst;
    }

    /// <summary>Cuts a rotated box out of the image as an upright bitmap (width along the box's
    /// long side), the way PaddleOCR crops a text line before recognising it.</summary>
    public static SKBitmap CropRotated(SKBitmap src, RotatedBox box)
    {
        int w = Math.Max(1, (int)Math.Round(box.Width)), h = Math.Max(1, (int)Math.Round(box.Height));
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(-box.Angle);
        canvas.Translate(-box.CenterX, -box.CenterY);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(src, 0, 0, paint);
        return dst;
    }

    /// <summary>Labels 8-connected foreground regions. Returns each region's pixel indices.</summary>
    public static List<List<int>> ConnectedComponents(bool[] mask, int width, int height, int minPixels = 1)
    {
        var labels = new int[mask.Length];
        var regions = new List<List<int>>();
        var stack = new Stack<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] != 0) continue;
            var pixels = new List<int>();
            int label = regions.Count + 1;
            labels[start] = label;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                pixels.Add(idx);
                int x = idx % width, y = idx / width;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= width || (dx == 0 && dy == 0)) continue;
                        int n = ny * width + nx;
                        if (mask[n] && labels[n] == 0) { labels[n] = label; stack.Push(n); }
                    }
                }
            }
            if (pixels.Count >= minPixels) regions.Add(pixels);
        }
        return regions;
    }

    /// <summary>Convex hull (Andrew's monotone chain), counter-clockwise.</summary>
    public static List<SKPoint> ConvexHull(IEnumerable<SKPoint> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        static float Cross(SKPoint o, SKPoint a, SKPoint b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<SKPoint>();
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>The smallest-area rectangle around a set of points (one side lies on a hull
    /// edge), normalised so Width is the longer side and the angle is within ±45°.</summary>
    public static RotatedBox MinAreaRect(IReadOnlyList<SKPoint> points)
    {
        var hull = ConvexHull(points);
        if (hull.Count == 0) return default;
        if (hull.Count < 3)
        {
            float minX = hull.Min(p => p.X), maxX = hull.Max(p => p.X), minY = hull.Min(p => p.Y), maxY = hull.Max(p => p.Y);
            return new RotatedBox((minX + maxX) / 2, (minY + maxY) / 2, maxX - minX + 1, maxY - minY + 1, 0);
        }
        RotatedBox best = default;
        double bestArea = double.MaxValue;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var p in hull)
            {
                double u = p.X * cos + p.Y * sin, v = -p.X * sin + p.Y * cos;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            double area = (maxU - minU) * (maxV - minV);
            if (area < bestArea)
            {
                bestArea = area;
                double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
                float cx = (float)(cu * cos - cv * sin), cy = (float)(cu * sin + cv * cos);
                best = new RotatedBox(cx, cy, (float)(maxU - minU), (float)(maxV - minV), (float)(angle * 180 / Math.PI));
            }
        }
        return Normalize(best);
    }

    /// <summary>Width the long side, angle in (-45°, 45°].</summary>
    public static RotatedBox Normalize(RotatedBox b)
    {
        float w = b.Width, h = b.Height, a = b.Angle;
        while (a > 45) { a -= 90; (w, h) = (h, w); }
        while (a <= -45) { a += 90; (w, h) = (h, w); }
        return new RotatedBox(b.CenterX, b.CenterY, w, h, a);
    }
    /// <summary>The bitmap turned clockwise by 90, 180 or 270 degrees.</summary>
    public static SKBitmap RotateQuarter(SKBitmap src, int degrees)
    {
        bool swap = degrees is 90 or 270;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.Translate(dst.Width / 2f, dst.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-src.Width / 2f, -src.Height / 2f);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    /// <summary>
    /// Decodes an image file the right way up: phone photos store the picture as the sensor saw
    /// it and say in EXIF which way to turn it, which a plain decode ignores.
    /// </summary>
    public static SKBitmap? DecodeUpright(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;
        var bmp = SKBitmap.Decode(codec);
        if (bmp is null) return null;
        int turn = codec.EncodedOrigin switch
        {
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0,
        };
        // Mirrored origins (rare outside selfies) are read as they are.
        if (turn == 0) return bmp;
        var upright = RotateQuarter(bmp, turn);
        bmp.Dispose();
        return upright;
    }
}
