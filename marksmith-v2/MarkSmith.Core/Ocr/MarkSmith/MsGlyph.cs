namespace MarkSmith.Ocr.MarkSmith;

/// <summary>Turns a letter candidate into the network's input: a 32×32 image and 8 measurements.</summary>
public static class MsGlyph
{
    /// <summary>What MarkSmith OCR can read. Index 0 is "not one letter": a speck or smudge, or two
    /// or more letters touching (which the recogniser then splits).</summary>
    public static readonly string[] Charset = BuildCharset();

    private static string[] BuildCharset()
    {
        var list = new List<string> { "" };
        for (char c = '!'; c <= '~'; c++) list.Add(c.ToString());
        list.AddRange(new[] { "‘", "’", "“", "”", "–", "—", "•", "€", "£", "©", "®", "°", "…" });
        return list.ToArray();
    }

    /// <summary>The candidate's ink, scaled to fit 28 px (keeping its shape) and centred in 32×32.
    /// Only the candidate's own blobs are drawn, so a neighbour's serif never leaks in.</summary>
    public static float[] Image(GlyphCandidate g, int imageWidth, int left = -1, int right = -1)
    {
        int l = left >= 0 ? left : g.Left, r = right >= 0 ? right : g.Right;
        int top = g.Top, bottom = g.Bottom;
        int w = r - l + 1, h = bottom - top + 1;
        // Rasterise the blobs' pixels into a local bitmap (clipped to [l, r] for split pieces).
        var local = new float[w * h];
        int tTop = int.MaxValue, tBottom = -1;
        foreach (var b in g.Blobs)
            foreach (var i in b.Pixels)
            {
                int x = i % imageWidth - l, y = i / imageWidth - top;
                if (x < 0 || x >= w) continue;
                local[y * w + x] = 1;
                if (y < tTop) tTop = y;
                if (y > tBottom) tBottom = y;
            }
        if (tBottom < 0) return new float[MsGlyphNet.Size * MsGlyphNet.Size];
        // A split piece is cropped vertically to its own ink.
        int oy = tTop, hh = tBottom - tTop + 1;
        return Fit(local, w, h, oy, hh);
    }

    private static float[] Fit(float[] local, int w, int fullH, int oy, int h)
    {
        const int S = MsGlyphNet.Size, Box = 28;
        var dst = new float[S * S];
        double scale = Box / (double)Math.Max(w, h);
        double outW = w * scale, outH = h * scale;
        double ox = (S - outW) / 2, oyDst = (S - outH) / 2;
        // Area sampling: each destination pixel averages the source pixels it covers.
        for (int dy = 0; dy < S; dy++)
        {
            double sy0 = (dy - oyDst) / scale, sy1 = (dy + 1 - oyDst) / scale;
            if (sy1 <= 0 || sy0 >= h) continue;
            for (int dx = 0; dx < S; dx++)
            {
                double sx0 = (dx - ox) / scale, sx1 = (dx + 1 - ox) / scale;
                if (sx1 <= 0 || sx0 >= w) continue;
                double acc = 0, area = 0;
                for (int sy = Math.Max(0, (int)Math.Floor(sy0)); sy < Math.Min(h, (int)Math.Ceiling(sy1)); sy++)
                {
                    double cy = Math.Min(sy + 1, sy1) - Math.Max(sy, sy0);
                    for (int sx = Math.Max(0, (int)Math.Floor(sx0)); sx < Math.Min(w, (int)Math.Ceiling(sx1)); sx++)
                    {
                        double cx = Math.Min(sx + 1, sx1) - Math.Max(sx, sx0);
                        acc += local[(sy + oy) * w + sx] * cx * cy;
                        area += cx * cy;
                    }
                }
                dst[dy * S + dx] = area > 0 ? (float)(acc / ((sx1 - sx0) * (sy1 - sy0))) : 0;
            }
        }
        return dst;
    }

    /// <summary>
    /// Where the letter sits on its line, in units of the line's cap height: top and bottom
    /// against the cap line and baseline, its height and width, the line's x-height ratio, the
    /// letter's top against the x-height line, its ink density and how many pieces it has.
    /// </summary>
    public static float[] Features(GlyphCandidate g, TextLine line, int top, int bottom, int left, int right, int inkPixels)
    {
        float cap = line.CapHeight;
        int w = right - left + 1, h = bottom - top + 1;
        static float C(float v) => Math.Clamp(v, -3f, 3f);
        return new[]
        {
            C((top - line.CapTop) / cap),
            C((bottom + 1 - line.Baseline) / cap),
            C(h / cap),
            C(w / cap),
            C(line.XHeight / cap),
            C((top - line.XTop) / cap),
            C(inkPixels / (float)Math.Max(1, w * h)),
            C(g.Blobs.Count / 3f),
        };
    }

    /// <summary>The ink extent of the candidate between columns <paramref name="left"/> and
    /// <paramref name="right"/> (a piece of a split): top, bottom and pixel count.</summary>
    public static (int Top, int Bottom, int Ink) Piece(GlyphCandidate g, int imageWidth, int left, int right)
    {
        int top = int.MaxValue, bottom = -1, ink = 0;
        foreach (var b in g.Blobs)
            foreach (var i in b.Pixels)
            {
                int x = i % imageWidth;
                if (x < left || x > right) continue;
                int y = i / imageWidth;
                ink++;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        return bottom < 0 ? (g.Top, g.Top, 0) : (top, bottom, ink);
    }

    public static float[] Features(GlyphCandidate g, TextLine line) =>
        Features(g, line, g.Top, g.Bottom, g.Left, g.Right, g.Blobs.Sum(b => b.Pixels.Count));
}
