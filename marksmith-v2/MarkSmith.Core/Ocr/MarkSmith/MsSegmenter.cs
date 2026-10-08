using SkiaSharp;

namespace MarkSmith.Ocr.MarkSmith;

/// <summary>One connected blob of ink.</summary>
public sealed class InkBlob
{
    public int Left, Top, Right, Bottom; // inclusive
    public List<int> Pixels = new();
    public int Width => Right - Left + 1;
    public int Height => Bottom - Top + 1;
    public float CenterX => (Left + Right) / 2f;
    public float CenterY => (Top + Bottom) / 2f;
}

/// <summary>A letter candidate: one or more blobs read as one character (i and its dot, a colon).</summary>
public sealed class GlyphCandidate
{
    public List<InkBlob> Blobs = new();
    public int Left => Blobs.Min(b => b.Left);
    public int Right => Blobs.Max(b => b.Right);
    public int Top => Blobs.Min(b => b.Top);
    public int Bottom => Blobs.Max(b => b.Bottom);
    public int Width => Right - Left + 1;
    public int Height => Bottom - Top + 1;
}

/// <summary>A text line: its letters left to right and the line's own measurements.</summary>
public sealed class TextLine
{
    public List<GlyphCandidate> Glyphs = new();
    /// <summary>Median bottom of the letters that sit on the line.</summary>
    public float Baseline;
    /// <summary>Top of capitals and ascenders.</summary>
    public float CapTop;
    /// <summary>Top of lower-case letters without ascenders.</summary>
    public float XTop;
    public float CapHeight => Math.Max(2, Baseline - CapTop);
    public float XHeight => Math.Max(2, Baseline - XTop);
    public int Left => Glyphs.Count == 0 ? 0 : Glyphs.Min(g => g.Left);
    public int Right => Glyphs.Count == 0 ? 0 : Glyphs.Max(g => g.Right);
    public int Top => Glyphs.Count == 0 ? 0 : Glyphs.Min(g => g.Top);
    public int Bottom => Glyphs.Count == 0 ? 0 : Glyphs.Max(g => g.Bottom);
}

/// <summary>
/// MarkSmith OCR's page segmentation: ink blobs → blocks (XY-cut, so columns read in order) →
/// lines (blobs that share a vertical band, left to right) → letter candidates (blobs that overlap
/// horizontally, e.g. i and its dot) with each line's baseline, x-height and cap height.
/// </summary>
public static class MsSegmenter
{
    public static List<InkBlob> Blobs(BinaryImage bin)
    {
        var regions = OcrGeometry.ConnectedComponents(bin.Ink, bin.Width, bin.Height);
        var blobs = new List<InkBlob>(regions.Count);
        foreach (var r in regions)
        {
            var b = new InkBlob { Left = int.MaxValue, Top = int.MaxValue, Right = -1, Bottom = -1, Pixels = r };
            foreach (var i in r)
            {
                int x = i % bin.Width, y = i / bin.Width;
                if (x < b.Left) b.Left = x;
                if (x > b.Right) b.Right = x;
                if (y < b.Top) b.Top = y;
                if (y > b.Bottom) b.Bottom = y;
            }
            blobs.Add(b);
        }
        return blobs;
    }

    /// <summary>A robust "typical letter height" for the page: the median height of blobs that
    /// look like letters (not specks, not rules or pictures).</summary>
    public static float TypicalHeight(IReadOnlyList<InkBlob> blobs)
    {
        var hs = blobs.Where(b => b.Height >= 4 && b.Pixels.Count >= 8 && b.Width < b.Height * 6 && b.Height < 400)
                      .Select(b => (float)b.Height).OrderBy(h => h).ToList();
        return hs.Count == 0 ? 0 : hs[hs.Count / 2];
    }

    public static List<TextLine> Lines(BinaryImage bin, out List<InkBlob> blobs)
    {
        blobs = Blobs(bin);
        float typical = TypicalHeight(blobs);
        if (typical <= 0) return new List<TextLine>();

        // Specks smaller than a full stop at this size are noise; huge blobs are rules or pictures.
        float minArea = Math.Max(3, typical * typical * 0.012f);
        var marks = blobs.Where(b => b.Pixels.Count >= minArea && b.Height < typical * 8 && b.Width < typical * 40).ToList();
        var big = marks.Where(b => b.Height >= typical * 0.45f).ToList();
        var small = marks.Where(b => b.Height < typical * 0.45f).ToList();

        var lines = new List<TextLine>();
        foreach (var block in Blocks(big, typical))
            lines.AddRange(LinesInBlock(block, typical));
        AttachSmallMarks(lines, small, typical);
        foreach (var line in lines) Finish(line);
        lines.RemoveAll(l => l.Glyphs.Count == 0);
        return lines;
    }

    /// <summary>XY-cut over blob boxes: split at the widest vertical gutter (columns) or
    /// horizontal gap (blocks), recursively; returns blocks in reading order.</summary>
    private static List<List<InkBlob>> Blocks(List<InkBlob> blobs, float typical)
    {
        var blocks = new List<List<InkBlob>>();
        CutBlocks(blobs, typical, blocks, 0);
        return blocks;
    }

    private static void CutBlocks(List<InkBlob> blobs, float typical, List<List<InkBlob>> output, int depth)
    {
        if (blobs.Count == 0) return;
        if (depth > 30 || blobs.Count < 3) { output.Add(blobs); return; }
        var v = Gap(blobs.Select(b => (b.Left, b.Right + 1)));
        var h = Gap(blobs.Select(b => (b.Top, b.Bottom + 1)));
        // A column gutter is wider than a word space (~0.35 letter heights) by a clear margin.
        bool canV = v.Size > typical * 1.6f;
        bool canH = h.Size > typical * 1.2f;
        if (canV && (!canH || v.Size > typical * 2.5f))
        {
            CutBlocks(blobs.Where(b => b.CenterX < v.At).ToList(), typical, output, depth + 1);
            CutBlocks(blobs.Where(b => b.CenterX >= v.At).ToList(), typical, output, depth + 1);
        }
        else if (canH)
        {
            CutBlocks(blobs.Where(b => b.CenterY < h.At).ToList(), typical, output, depth + 1);
            CutBlocks(blobs.Where(b => b.CenterY >= h.At).ToList(), typical, output, depth + 1);
        }
        else output.Add(blobs);
    }

    private static (float Size, float At) Gap(IEnumerable<(int Start, int End)> spans)
    {
        var sorted = spans.OrderBy(s => s.Start).ToList();
        int reach = sorted[0].End;
        float bestSize = 0, bestAt = 0;
        foreach (var (start, end) in sorted.Skip(1))
        {
            if (start > reach && start - reach > bestSize) { bestSize = start - reach; bestAt = (start + reach) / 2f; }
            reach = Math.Max(reach, end);
        }
        return (bestSize, bestAt);
    }

    /// <summary>Lines in a block: walk blobs left to right and join each to the line whose
    /// vertical band it shares most (a line's band is the median top/bottom of its members, so
    /// ascenders and descenders of neighbouring lines don't merge them).</summary>
    private static List<TextLine> LinesInBlock(List<InkBlob> blobs, float typical)
    {
        var open = new List<(TextLine Line, List<InkBlob> Members, float Top, float Bottom, int Right)>();
        foreach (var b in blobs.OrderBy(b => b.Left))
        {
            int best = -1;
            double bestScore = 0;
            for (int i = 0; i < open.Count; i++)
            {
                var o = open[i];
                float overlap = Math.Min(o.Bottom, b.Bottom + 1) - Math.Max(o.Top, b.Top);
                float span = Math.Min(o.Bottom - o.Top, b.Height);
                if (overlap <= 0 || span <= 0) continue;
                double score = overlap / span;
                // Far to the left of the line's last letter: a different line in another region.
                if (b.Left - o.Right > typical * 12) score *= 0.3;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0 && bestScore >= 0.45)
            {
                var o = open[best];
                o.Members.Add(b);
                var tops = o.Members.Select(m => (float)m.Top).OrderBy(x => x).ToList();
                var bottoms = o.Members.Select(m => (float)m.Bottom + 1).OrderBy(x => x).ToList();
                open[best] = (o.Line, o.Members, tops[tops.Count / 2], bottoms[bottoms.Count / 2], Math.Max(o.Right, b.Right));
            }
            else
            {
                open.Add((new TextLine(), new List<InkBlob> { b }, b.Top, b.Bottom + 1, b.Right));
            }
        }
        // Fragments of one line (a word that started a new band, e.g. after a tall capital or a
        // run of descenders) join the line they sit level with and don't overlap horizontally; a
        // stray few marks (a comma, a descender piece) inside another line's full height join it.
        // Each group's measurements are kept up to date rather than recomputed per pair: a noisy
        // scan can start with thousands of fragments.
        var groups = open.Select(o => new Fragment(o.Members)).OrderBy(g => g.BandTop).ToList();
        for (int i = 0; i < groups.Count; i++)
        {
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int j = i + 1; j < groups.Count; j++)
                {
                    var a = groups[i]; var b = groups[j];
                    if (b.MinTop > a.MaxBottom + typical * 2) break; // sorted by band: nothing below can be level
                    if (!Joinable(a, b, typical)) continue;
                    a.Absorb(b);
                    groups.RemoveAt(j);
                    j--;
                    grew = true;
                }
            }
        }
        var lines = new List<TextLine>();
        foreach (var g in groups.OrderBy(g => g.BandTop))
        {
            var line = new TextLine();
            line.Glyphs.AddRange(g.Members.Select(m => new GlyphCandidate { Blobs = { m } }));
            lines.Add(line);
        }
        return lines;
    }

    private sealed class Fragment
    {
        public readonly List<InkBlob> Members;
        public float BandTop, BandBottom;   // median top / bottom
        public int MinTop, MaxBottom, Left, Right;
        public float CenterX, CenterY;

        public Fragment(List<InkBlob> members) { Members = members; Measure(); }

        public void Absorb(Fragment other) { Members.AddRange(other.Members); Measure(); }

        private void Measure()
        {
            var tops = Members.Select(m => (float)m.Top).OrderBy(x => x).ToList();
            var bottoms = Members.Select(m => (float)m.Bottom + 1).OrderBy(x => x).ToList();
            BandTop = tops[tops.Count / 2];
            BandBottom = bottoms[bottoms.Count / 2];
            MinTop = Members.Min(m => m.Top); MaxBottom = Members.Max(m => m.Bottom);
            Left = Members.Min(m => m.Left); Right = Members.Max(m => m.Right);
            CenterX = Members.Average(m => m.CenterX); CenterY = Members.Average(m => m.CenterY);
        }
    }

    private static bool Joinable(Fragment a, Fragment b, float typical)
    {
        static bool Stray(Fragment few, Fragment host, float typical) =>
            few.Members.Count <= 3 && few.CenterY >= host.MinTop && few.CenterY <= host.MaxBottom
            && few.CenterX >= host.Left - typical && few.CenterX <= host.Right + typical;
        if (Stray(b, a, typical) || Stray(a, b, typical)) return true;
        float overlap = Math.Min(a.BandBottom, b.BandBottom) - Math.Max(a.BandTop, b.BandTop);
        float span = Math.Min(a.BandBottom - a.BandTop, b.BandBottom - b.BandTop);
        if (span <= 0 || overlap < span * 0.5f) return false;
        int xOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        return xOverlap <= typical;
    }

    /// <summary>Dots, commas, accents, hyphens: each goes to the line whose band it is closest to.</summary>
    private static void AttachSmallMarks(List<TextLine> lines, List<InkBlob> small, float typical)
    {
        if (lines.Count == 0) return;
        foreach (var s in small)
        {
            TextLine? best = null;
            double bestD = double.MaxValue;
            foreach (var l in lines)
            {
                if (l.Glyphs.Count == 0) continue;
                if (s.CenterX < l.Left - typical * 2 || s.CenterX > l.Right + typical * 2) continue;
                float top = l.Top, bottom = l.Bottom;
                double d = s.CenterY < top ? top - s.CenterY : s.CenterY > bottom ? s.CenterY - bottom : 0;
                if (d < bestD) { bestD = d; best = l; }
            }
            if (best is not null && bestD < typical * 0.6) best.Glyphs.Add(new GlyphCandidate { Blobs = { s } });
        }
    }

    /// <summary>Orders a line's candidates, merges blobs that stack (i, j, :, ;, =, %, ä) and
    /// measures the line.</summary>
    private static void Finish(TextLine line)
    {
        var sorted = line.Glyphs.SelectMany(g => g.Blobs).OrderBy(b => b.Left).ToList();
        var glyphs = new List<GlyphCandidate>();
        foreach (var b in sorted)
        {
            var last = glyphs.Count > 0 ? glyphs[^1] : null;
            if (last is not null)
            {
                int overlap = Math.Min(last.Right, b.Right) - Math.Max(last.Left, b.Left) + 1;
                int narrower = Math.Min(last.Width, b.Width);
                // Stacked marks share most of the narrower one's width; letters side by side
                // (even touching italics) share little.
                if (overlap >= narrower * 0.55f) { last.Blobs.Add(b); continue; }
            }
            glyphs.Add(new GlyphCandidate { Blobs = { b } });
        }
        line.Glyphs = glyphs;
        Measure(line);
    }

    public static void Measure(TextLine line)
    {
        var big = line.Glyphs.Where(g => g.Height >= (line.Glyphs.Max(x => x.Height)) * 0.4f).ToList();
        if (big.Count == 0) big = line.Glyphs;
        var bottoms = big.Select(g => (float)g.Bottom + 1).OrderBy(x => x).ToList();
        // Baseline: the most common bottom (descenders sit below, so take a central percentile).
        line.Baseline = bottoms[(int)(bottoms.Count * 0.4)];
        var tops = big.Select(g => (float)g.Top).OrderBy(x => x).ToList();
        line.CapTop = tops[(int)(tops.Count * 0.1)];
        // x-height: the typical top of the shorter half of letters.
        var xs = big.Where(g => g.Top > line.CapTop + (line.Baseline - line.CapTop) * 0.2f).Select(g => (float)g.Top).OrderBy(x => x).ToList();
        line.XTop = xs.Count > 0 ? xs[xs.Count / 2] : line.CapTop + (line.Baseline - line.CapTop) * 0.3f;
    }
}
