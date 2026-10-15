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
        // Also exclude thin horizontal/vertical form border rules and 1D vertical barcode clusters.
        float minArea = Math.Max(3, typical * typical * 0.012f);
        var marks = blobs.Where(b =>
            b.Pixels.Count >= minArea
            && b.Height < typical * 8
            && b.Width < typical * 40
            && !(b.Width > typical * 3.2f && b.Height <= Math.Max(5f, typical * 0.25f))
            && !(b.Height > typical * 2.6f && b.Width <= Math.Max(5f, typical * 0.22f))).ToList();
        RemoveBarcodeClusters(marks, typical);
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

    /// <summary>Removes 1D vertical barcode runs (12+ closely-spaced tall vertical bars sharing the same top/bottom band).</summary>
    private static void RemoveBarcodeClusters(List<InkBlob> marks, float typical)
    {
        var bars = marks.Where(b =>
            b.Height >= typical * 1.25f
            && b.Width <= Math.Max(7f, b.Height * 0.34f)
            && b.Pixels.Count >= b.Width * b.Height * 0.50f)
            .OrderBy(b => b.Left)
            .ToList();
        if (bars.Count < 12) return;

        var barcodeRects = new List<(int Left, int Top, int Right, int Bottom)>();
        var visited = new bool[bars.Count];
        for (int i = 0; i < bars.Count; i++)
        {
            if (visited[i]) continue;
            var cluster = new List<InkBlob> { bars[i] };
            visited[i] = true;
            int lastRight = bars[i].Right;
            float refTop = bars[i].Top, refBottom = bars[i].Bottom;
            for (int j = i + 1; j < bars.Count; j++)
            {
                if (visited[j]) continue;
                var cand = bars[j];
                if (cand.Left - lastRight > typical * 1.1f) break;
                if (Math.Abs(cand.Top - refTop) <= typical * 0.45f && Math.Abs(cand.Bottom - refBottom) <= typical * 0.45f)
                {
                    cluster.Add(cand);
                    visited[j] = true;
                    lastRight = Math.Max(lastRight, cand.Right);
                }
            }
            if (cluster.Count >= 12)
            {
                barcodeRects.Add((
                    cluster.Min(c => c.Left) - 2,
                    cluster.Min(c => c.Top) - 2,
                    cluster.Max(c => c.Right) + 2,
                    cluster.Max(c => c.Bottom) + 2));
            }
        }
        if (barcodeRects.Count > 0)
        {
            marks.RemoveAll(m => barcodeRects.Any(r =>
                m.Left >= r.Left && m.Right <= r.Right && m.Top >= r.Top && m.Bottom <= r.Bottom));
        }
    }

    /// <summary>XY-cut over blob boxes: split at the widest vertical gutter (columns) or
    /// horizontal gap (blocks), recursively; returns blocks in reading order.</summary>
    private static List<List<InkBlob>> Blocks(List<InkBlob> blobs, float typical)
    {
        var blocks = new List<List<InkBlob>>();
        if (blobs.Count == 0) return blocks;
        // A column gutter must be clearly wider than this text's own word spaces. In monospaced
        // type the spaces of two lines can line up into a channel as wide as a gutter of
        // proportional text, and cutting there scrambled the reading order.
        float gutter = Math.Max(typical * 1.55f, WordSpace(blobs, typical) * 1.9f);
        int pageMinX = blobs.Min(b => b.Left);
        int pageMaxX = blobs.Max(b => b.Right);
        float pageSpanW = Math.Max(1f, pageMaxX - pageMinX);
        CutBlocks(blobs, typical, gutter, pageMinX, pageSpanW, blocks, 0);
        return blocks;
    }

    /// <summary>The page's word space: the 90th percentile of the gaps between each letter and the
    /// next one along its line (gaps wider than four letter heights aren't spaces).</summary>
    public static float WordSpace(List<InkBlob> blobs, float typical)
    {
        var letters = blobs.Where(b => b.Height >= typical * 0.4f).OrderBy(b => b.Left).ToList();
        var gaps = new List<float>();
        for (int i = 0; i < letters.Count; i++)
        {
            var a = letters[i];
            int best = int.MaxValue;
            for (int j = i + 1; j < letters.Count && letters[j].Left <= a.Right + typical * 4; j++)
            {
                var b = letters[j];
                if (b.Left <= a.Right) continue;
                int overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                if (overlap < Math.Min(a.Height, b.Height) * 0.5f) continue;
                best = Math.Min(best, b.Left - a.Right - 1);
            }
            if (best != int.MaxValue) gaps.Add(best);
        }
        if (gaps.Count < 10) return 0;
        gaps.Sort();
        return gaps[(int)(gaps.Count * 0.9)];
    }

    private static void CutBlocks(List<InkBlob> blobs, float typical, float gutter, int pageMinX, float pageSpanW, List<List<InkBlob>> output, int depth)
    {
        if (blobs.Count == 0) return;
        if (depth > 30 || blobs.Count < 3) { output.Add(blobs); return; }

        var v = Gap(blobs.Select(b => (b.Left, b.Right + 1)));
        var h = Gap(blobs.Select(b => (b.Top, b.Bottom + 1)));
        bool canV = v.Size > gutter;
        bool canH = h.Size > typical * 1.15f;

        bool isTrueColumnSplit = false;
        bool isSidebarSplit = false;
        bool isTinyMarginPrefix = false;

        if (canV)
        {
            var left = blobs.Where(b => b.CenterX < v.At).ToList();
            var right = blobs.Where(b => b.CenterX >= v.At).ToList();
            if (left.Count > 0 && right.Count > 0)
            {
                float leftW = left.Max(b => b.Right) - left.Min(b => b.Left) + 1;
                float rightW = right.Max(b => b.Right) - right.Min(b => b.Left) + 1;
                float leftH = left.Max(b => b.Bottom) - left.Min(b => b.Top) + 1;
                float rightH = right.Max(b => b.Bottom) - right.Min(b => b.Top) + 1;

                // Never split off a tiny left question number or checkbox prefix (e.g. "13", "[ ]") from its line.
                if (leftW < typical * 3.8f && (left.Count <= 4 || leftH < typical * 2.4f))
                {
                    isTinyMarginPrefix = true;
                    canV = false;
                }
                else if (leftW >= pageSpanW * 0.25f && rightW >= pageSpanW * 0.25f
                    && left.Count >= 4 && right.Count >= 4
                    && leftH >= typical * 2.2f && rightH >= typical * 2.2f)
                {
                    isTrueColumnSplit = true;
                }
                else if (leftW < pageSpanW * 0.25f && rightW > leftW * 1.6f)
                {
                    isSidebarSplit = true;
                }
            }
        }

        // If a spanning header or footer currently blocks a true 2-column vertical split in the body,
        // find the horizontal gap that separates the spanning header/footer from the 2-column body first.
        if (!isTrueColumnSplit && blobs.Count >= 12)
        {
            var hGaps = AllGaps(blobs.Select(b => (b.Top, b.Bottom + 1)), typical * 0.95f);
            foreach (var hg in hGaps)
            {
                var topPart = blobs.Where(b => b.CenterY < hg.At).ToList();
                var botPart = blobs.Where(b => b.CenterY >= hg.At).ToList();
                if (topPart.Count == 0 || botPart.Count == 0) continue;
                if (HasTrueColumnSplit(botPart, gutter, pageSpanW, typical) || HasTrueColumnSplit(topPart, gutter, pageSpanW, typical))
                {
                    CutBlocks(topPart, typical, gutter, pageMinX, pageSpanW, output, depth + 1);
                    CutBlocks(botPart, typical, gutter, pageMinX, pageSpanW, output, depth + 1);
                    return;
                }
            }
        }

        // For a left-sidebar heading layout (like Pages 1-2 of form.pdf), cut horizontally at section gaps
        // FIRST so each left-sidebar heading stays paired with its right-hand paragraphs.
        if (isSidebarSplit && canH && h.Size > typical * 1.05f)
        {
            CutBlocks(blobs.Where(b => b.CenterY < h.At).ToList(), typical, gutter, pageMinX, pageSpanW, output, depth + 1);
            CutBlocks(blobs.Where(b => b.CenterY >= h.At).ToList(), typical, gutter, pageMinX, pageSpanW, output, depth + 1);
            return;
        }

        // For a true 2-column split (or a wide unambiguous vertical gutter), cut vertically BEFORE horizontal cuts.
        if (!isTinyMarginPrefix && canV && (isTrueColumnSplit || !canH || v.Size > Math.Max(typical * 2.5f, gutter)))
        {
            CutBlocks(blobs.Where(b => b.CenterX < v.At).ToList(), typical, gutter, pageMinX, pageSpanW, output, depth + 1);
            CutBlocks(blobs.Where(b => b.CenterX >= v.At).ToList(), typical, gutter, pageMinX, pageSpanW, output, depth + 1);
        }
        else if (canH)
        {
            CutBlocks(blobs.Where(b => b.CenterY < h.At).ToList(), typical, gutter, pageMinX, pageSpanW, output, depth + 1);
            CutBlocks(blobs.Where(b => b.CenterY >= h.At).ToList(), typical, gutter, pageMinX, pageSpanW, output, depth + 1);
        }
        else output.Add(blobs);
    }

    private static bool HasTrueColumnSplit(List<InkBlob> blobs, float gutter, float pageSpanW, float typical)
    {
        if (blobs.Count < 8) return false;
        var v = Gap(blobs.Select(b => (b.Left, b.Right + 1)));
        if (v.Size <= gutter) return false;
        var left = blobs.Where(b => b.CenterX < v.At).ToList();
        var right = blobs.Where(b => b.CenterX >= v.At).ToList();
        if (left.Count < 4 || right.Count < 4) return false;
        float leftW = left.Max(b => b.Right) - left.Min(b => b.Left) + 1;
        float rightW = right.Max(b => b.Right) - right.Min(b => b.Left) + 1;
        float leftH = left.Max(b => b.Bottom) - left.Min(b => b.Top) + 1;
        float rightH = right.Max(b => b.Bottom) - right.Min(b => b.Top) + 1;
        return leftW >= pageSpanW * 0.25f && rightW >= pageSpanW * 0.25f
            && leftH >= typical * 2.2f && rightH >= typical * 2.2f;
    }

    private static List<(float Size, float At)> AllGaps(IEnumerable<(int Start, int End)> spans, float minSize)
    {
        var sorted = spans.OrderBy(s => s.Start).ToList();
        var gaps = new List<(float Size, float At)>();
        if (sorted.Count == 0) return gaps;
        int reach = sorted[0].End;
        foreach (var (start, end) in sorted.Skip(1))
        {
            if (start > reach && start - reach >= minSize)
                gaps.Add((start - reach, (start + reach) / 2f));
            reach = Math.Max(reach, end);
        }
        return gaps.OrderByDescending(g => g.Size).ToList();
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
            if (best >= 0 && bestScore >= 0.38)
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
        // Also merge interleaved fragments on the same vertical band where individual member blobs
        // sit inside the horizontal gaps of the host line (e.g. capitals/apostrophes split from lowercase).
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
            few.Members.Count <= 4 && few.CenterY >= host.MinTop - typical * 0.2f && few.CenterY <= host.MaxBottom + typical * 0.2f
            && few.CenterX >= host.Left - typical * 1.5f && few.CenterX <= host.Right + typical * 1.5f;
        if (Stray(b, a, typical) || Stray(a, b, typical)) return true;
        float overlap = Math.Min(a.BandBottom, b.BandBottom) - Math.Max(a.BandTop, b.BandTop);
        float span = Math.Min(a.BandBottom - a.BandTop, b.BandBottom - b.BandTop);
        bool bandOverlap = span > 0 && overlap >= span * 0.42f;
        bool fullVerticalOverlap = Math.Abs(a.CenterY - b.CenterY) <= typical * 0.55f
            && Math.Min(a.MaxBottom, b.MaxBottom) - Math.Max(a.MinTop, b.MinTop) >= Math.Min(a.MaxBottom - a.MinTop, b.MaxBottom - b.MinTop) * 0.45f;
        if (!bandOverlap && !fullVerticalOverlap) return false;
        int xOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (xOverlap <= typical) return true;

        // Check if the two fragments are interleaved letters of the same line (their overall bounding boxes
        // overlap horizontally, but their individual member blobs sit in each other's horizontal gaps).
        var smaller = a.Members.Count <= b.Members.Count ? a : b;
        var larger = ReferenceEquals(smaller, a) ? b : a;
        int collidingBlobs = 0;
        foreach (var sm in smaller.Members)
        {
            bool collides = larger.Members.Any(lg =>
                Math.Min(sm.Right, lg.Right) - Math.Max(sm.Left, lg.Left) > Math.Min(sm.Width, lg.Width) * 0.35f);
            if (collides) collidingBlobs++;
        }
        return collidingBlobs <= smaller.Members.Count * 0.25f;
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
