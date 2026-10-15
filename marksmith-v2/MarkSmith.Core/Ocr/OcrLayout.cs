using MarkSmith.Services;
using SkiaSharp;

namespace MarkSmith.Ocr;

/// <summary>
/// Puts recognised text pieces into reading order, the same way for every engine: an XY-cut splits
/// the page at wide vertical gutters (columns) and horizontal gaps (blocks), then each block's pieces
/// are grouped into lines by vertical overlap and read left to right.
/// </summary>
public static class OcrLayout
{
    public static OcrPageResult ToPageResult(IReadOnlyList<(SKRect Box, string Text)> pieces, float width, float height)
    {
        var ordered = new List<OcrLine>();
        foreach (var block in XyCut(pieces.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList(), width))
            ordered.AddRange(Lines(block));
        return new OcrPageResult(ordered, width, height);
    }

    /// <summary>Blocks of pieces in reading order.</summary>
    public static List<List<(SKRect Box, string Text)>> XyCut(List<(SKRect Box, string Text)> pieces, float pageWidth)
    {
        var result = new List<List<(SKRect, string)>>();
        Cut(pieces, pageWidth, result, depth: 0);
        return result;
    }

    private static void Cut(List<(SKRect Box, string Text)> pieces, float pageWidth, List<List<(SKRect, string)>> output, int depth)
    {
        if (pieces.Count == 0) return;
        if (depth > 24 || pieces.Count == 1) { output.Add(pieces); return; }

        float medianH = Median(pieces.Select(p => p.Box.Height));
        float minVGap = Math.Max(medianH * 1.2f, pageWidth * 0.015f);
        var hGap = LargestGap(pieces.Select(p => (p.Box.Top, p.Box.Bottom)).ToList());
        var vGap = LargestGap(pieces.Select(p => (p.Box.Left, p.Box.Right)).ToList());

        bool canV = vGap.Size > minVGap;
        bool canH = hGap.Size > medianH * 0.8f;

        bool isTrueColumnSplit = false;
        bool isSidebarSplit = false;
        if (canV)
        {
            var left = pieces.Where(p => p.Box.MidX < vGap.At).ToList();
            var right = pieces.Where(p => p.Box.MidX >= vGap.At).ToList();
            if (left.Count > 0 && right.Count > 0)
            {
                float leftW = left.Max(p => p.Box.Right) - left.Min(p => p.Box.Left);
                float rightW = right.Max(p => p.Box.Right) - right.Min(p => p.Box.Left);
                if (leftW < medianH * 3.2f && left.Count <= 2)
                {
                    canV = false;
                }
                else if (leftW >= pageWidth * 0.22f && rightW >= pageWidth * 0.22f)
                {
                    isTrueColumnSplit = true;
                }
                else if (leftW < pageWidth * 0.22f && rightW > leftW * 1.6f)
                {
                    isSidebarSplit = true;
                }
            }
        }

        // If a spanning header/footer blocks a central 2-column gutter, split horizontally at the header/footer gap first.
        if (!isTrueColumnSplit && pieces.Count >= 4)
        {
            var hGaps = AllGaps(pieces.Select(p => (p.Box.Top, p.Box.Bottom)).ToList(), medianH * 0.75f);
            foreach (var hg in hGaps)
            {
                var topPart = pieces.Where(p => p.Box.MidY < hg.At).ToList();
                var botPart = pieces.Where(p => p.Box.MidY >= hg.At).ToList();
                if (topPart.Count == 0 || botPart.Count == 0) continue;
                if (HasColumnSplit(botPart, minVGap, pageWidth) || HasColumnSplit(topPart, minVGap, pageWidth))
                {
                    Cut(topPart, pageWidth, output, depth + 1);
                    Cut(botPart, pageWidth, output, depth + 1);
                    return;
                }
            }
        }

        if (isSidebarSplit && canH && hGap.Size > medianH * 0.9f)
        {
            Cut(pieces.Where(p => p.Box.MidY < hGap.At).ToList(), pageWidth, output, depth + 1);
            Cut(pieces.Where(p => p.Box.MidY >= hGap.At).ToList(), pageWidth, output, depth + 1);
            return;
        }

        // Columns win when both exist: a page's two columns share every horizontal band.
        if (canV && (isTrueColumnSplit || !canH || vGap.Size > medianH * 2))
        {
            Cut(pieces.Where(p => p.Box.MidX < vGap.At).ToList(), pageWidth, output, depth + 1);
            Cut(pieces.Where(p => p.Box.MidX >= vGap.At).ToList(), pageWidth, output, depth + 1);
            return;
        }
        if (canH)
        {
            Cut(pieces.Where(p => p.Box.MidY < hGap.At).ToList(), pageWidth, output, depth + 1);
            Cut(pieces.Where(p => p.Box.MidY >= hGap.At).ToList(), pageWidth, output, depth + 1);
            return;
        }
        output.Add(pieces);
    }

    private static bool HasColumnSplit(List<(SKRect Box, string Text)> pieces, float minVGap, float pageWidth)
    {
        if (pieces.Count < 2) return false;
        var vGap = LargestGap(pieces.Select(p => (p.Box.Left, p.Box.Right)).ToList());
        if (vGap.Size <= minVGap) return false;
        var left = pieces.Where(p => p.Box.MidX < vGap.At).ToList();
        var right = pieces.Where(p => p.Box.MidX >= vGap.At).ToList();
        if (left.Count == 0 || right.Count == 0) return false;
        float leftW = left.Max(p => p.Box.Right) - left.Min(p => p.Box.Left);
        float rightW = right.Max(p => p.Box.Right) - right.Min(p => p.Box.Left);
        return leftW >= pageWidth * 0.22f && rightW >= pageWidth * 0.22f;
    }

    private static List<(float Size, float At)> AllGaps(List<(float Start, float End)> spans, float minSize)
    {
        var sorted = spans.OrderBy(s => s.Start).ToList();
        var gaps = new List<(float Size, float At)>();
        if (sorted.Count == 0) return gaps;
        float reach = sorted[0].End;
        foreach (var (start, end) in sorted.Skip(1))
        {
            if (start > reach && start - reach >= minSize)
                gaps.Add((start - reach, (start + reach) / 2));
            reach = Math.Max(reach, end);
        }
        return gaps.OrderByDescending(g => g.Size).ToList();
    }

    /// <summary>The widest empty stretch between intervals, and where it is.</summary>
    private static (float Size, float At) LargestGap(List<(float Start, float End)> spans)
    {
        var sorted = spans.OrderBy(s => s.Start).ToList();
        float reach = sorted[0].End, bestSize = 0, bestAt = 0;
        foreach (var (start, end) in sorted.Skip(1))
        {
            if (start > reach && start - reach > bestSize) { bestSize = start - reach; bestAt = (start + reach) / 2; }
            reach = Math.Max(reach, end);
        }
        return (bestSize, bestAt);
    }

    /// <summary>A block's pieces grouped into lines (vertical overlap), top to bottom.</summary>
    public static List<OcrLine> Lines(IReadOnlyList<(SKRect Box, string Text)> block)
    {
        var lines = new List<List<(SKRect Box, string Text)>>();
        foreach (var p in block.OrderBy(p => p.Box.MidY))
        {
            var line = lines.FirstOrDefault(l =>
            {
                float top = l.Min(q => q.Box.Top), bottom = l.Max(q => q.Box.Bottom);
                float overlap = Math.Min(bottom, p.Box.Bottom) - Math.Max(top, p.Box.Top);
                return overlap > 0.5f * Math.Min(bottom - top, p.Box.Height);
            });
            if (line is null) lines.Add(new List<(SKRect, string)> { p });
            else line.Add(p);
        }
        return lines
            .OrderBy(l => l.Min(q => q.Box.Top))
            .Select(l =>
            {
                var parts = l.OrderBy(q => q.Box.Left).ToList();
                var words = parts.SelectMany(q => SplitWords(q.Box, q.Text)).ToList();
                float top = parts.Min(q => q.Box.Top), bottom = parts.Max(q => q.Box.Bottom);
                return new OcrLine(string.Join(" ", parts.Select(q => q.Text.Trim())), words, top, bottom - top);
            })
            .ToList();
    }

    /// <summary>Word boxes for a piece that only has a box for the whole run of text, spread in
    /// proportion to each word's length.</summary>
    private static IEnumerable<OcrWord> SplitWords(SKRect box, string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int total = Math.Max(1, text.Length);
        float x = box.Left;
        foreach (var w in words)
        {
            float width = box.Width * w.Length / total;
            yield return new OcrWord(w, x, box.Top, width, box.Height);
            x += width + box.Width / total;
        }
    }

    public static float Median(IEnumerable<float> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }
}
