using System.Diagnostics;
using MarkSmith.Services;
using SkiaSharp;

namespace MarkSmith.Ocr;

/// <summary>A rectangle as fractions of the page (0..1 on both axes), so a viewer can draw it over
/// any size of preview without knowing the resolution the engine read at.</summary>
public readonly record struct OcrNormRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
}

/// <summary>A page the OCR engine is about to read: a downscaled JPEG of exactly the (straightened)
/// picture the engine is given, so boxes found on it line up with what the user sees.</summary>
public sealed record OcrPagePreview(
    int PageNumber, int PageIndex, int PageCount,
    byte[] Image, int ImageWidth, int ImageHeight, string Engine);

/// <summary>A recognised word: its text, where it sits on the page, and which block it ended up in.</summary>
public sealed record OcrTraceWord(string Text, OcrNormRect Box, int Block);

/// <summary>A Markdown block assembled from the page, in reading order, with the page area it came from.</summary>
public sealed record OcrTraceBlock(int Index, OcrBlockKind Kind, string Markdown, OcrNormRect Box, int WordCount);

/// <summary>Everything the engine found on one page, in reading order.</summary>
public sealed record OcrPageTrace(
    int PageNumber, IReadOnlyList<OcrTraceWord> Words, IReadOnlyList<OcrTraceBlock> Blocks,
    string Markdown, TimeSpan Elapsed)
{
    public int HeadingCount => Blocks.Count(b => b.Kind == OcrBlockKind.Heading);
    public int ListCount => Blocks.Count(b => b.Kind == OcrBlockKind.List);
    public int PictureCount => Blocks.Count(b => b.Kind == OcrBlockKind.Picture);
    public int DiagramCount => Blocks.Count(b => b.Kind is OcrBlockKind.Diagram or OcrBlockKind.ShapeMosaic);
}

/// <summary>
/// Watches OCR as it happens (the desktop app's reading animation). Called on the OCR worker
/// thread: implementations must hand off to their own thread and return straight away. An
/// observer that throws never breaks the import.
/// </summary>
public interface IOcrObserver
{
    /// <summary>The page is straightened and the engine is starting on it.</summary>
    void PageStarted(OcrPagePreview preview);

    /// <summary>The engine has finished the page.</summary>
    void PageRead(OcrPageTrace trace);
}

/// <summary>
/// Deterministic, content-invariant timing schedule for the 4-stage OCR &amp; document import animation.
/// Ensures a single-page import always plays for a calm, readable ~7.1 seconds regardless of whether
/// the document has 1 block or 25 blocks, or 3 words or 1,000 words.
/// </summary>
public readonly record struct OcrPlaybackSchedule(
    int Stage1RemainingMs,
    int Stage2Steps,
    int Stage2StepDelayMs,
    int Stage2HoldMs,
    int Stage3Steps,
    int Stage3StepDelayMs,
    int Stage3HoldMs,
    int Stage4Steps,
    int Stage4StepDelayMs,
    int Stage4HoldMs,
    int FinalSheetHoldMs,
    int FadeOutMs)
{
    /// <summary>Maximum number of pages in a multi-page scanned document that play the full 4-stage visual breakdown before remaining pages fold directly into the final sheet.</summary>
    public const int MaxAnimatedPages = 2;

    public int Stage2TotalMs => Stage2Steps * Stage2StepDelayMs + Stage2HoldMs;
    public int Stage3TotalMs => Stage3Steps * Stage3StepDelayMs + Stage3HoldMs;
    public int Stage4TotalMs => Stage4Steps * Stage4StepDelayMs + Stage4HoldMs;
    public int TotalPageMs => Stage1RemainingMs + Stage2TotalMs + Stage3TotalMs + Stage4TotalMs;
    public int TotalSinglePageSessionMs => TotalPageMs + FinalSheetHoldMs + FadeOutMs;

    public static OcrPlaybackSchedule ForPage(
        int blockCount,
        int wordCount,
        int pageIndex = 0,
        int pageCount = 1,
        int stage1ElapsedMs = 0)
    {
        double timeScale = pageCount <= 1
            ? 1.0
            : (pageIndex == 0 ? 0.85 : 0.60);

        int stage1Target = (int)Math.Round(1150 * timeScale);
        int stage1Remaining = Math.Clamp(stage1Target - Math.Max(0, stage1ElapsedMs), 450, stage1Target);

        int stage2Target = (int)Math.Round(1600 * timeScale);
        int stage2Steps = Math.Clamp(blockCount, 1, 8);
        int stage2StepDelay = stage2Target / (stage2Steps + 1);
        int stage2Hold = stage2Target - (stage2Steps * stage2StepDelay);

        int stage3Target = (int)Math.Round(1600 * timeScale);
        const int stage3Steps = 10;
        int stage3StepDelay = stage3Target / (stage3Steps + 1);
        int stage3Hold = stage3Target - (stage3Steps * stage3StepDelay);

        int stage4Target = (int)Math.Round(1800 * timeScale);
        int stage4Steps = Math.Clamp(blockCount, 1, 8);
        int stage4StepDelay = stage4Target / (stage4Steps + 1);
        int stage4Hold = stage4Target - (stage4Steps * stage4StepDelay);

        return new OcrPlaybackSchedule(
            Stage1RemainingMs: stage1Remaining,
            Stage2Steps: stage2Steps,
            Stage2StepDelayMs: stage2StepDelay,
            Stage2HoldMs: stage2Hold,
            Stage3Steps: stage3Steps,
            Stage3StepDelayMs: stage3StepDelay,
            Stage3HoldMs: stage3Hold,
            Stage4Steps: stage4Steps,
            Stage4StepDelayMs: stage4StepDelay,
            Stage4HoldMs: stage4Hold,
            FinalSheetHoldMs: 950,
            FadeOutMs: 180);
    }
}

/// <summary>Builds what an <see cref="IOcrObserver"/> is shown.</summary>
public static class OcrTrace
{
    /// <summary>Longest side of the preview picture: sharp in the split stage, fast to decode on UI thread.</summary>
    public const int PreviewMaxEdge = 1100;

    /// <summary>A JPEG of the page, no larger than <see cref="PreviewMaxEdge"/> on its long side.</summary>
    public static OcrPagePreview Preview(SKBitmap page, int pageNumber, int pageIndex, int pageCount, string engine)
    {
        int w = Math.Max(1, page.Width), h = Math.Max(1, page.Height);
        double scale = Math.Min(1.0, PreviewMaxEdge / (double)Math.Max(w, h));
        int pw = Math.Max(1, (int)Math.Round(w * scale)), ph = Math.Max(1, (int)Math.Round(h * scale));
        if (pw == w && ph == h && page.ColorType == SKColorType.Bgra8888 && page.AlphaType == SKAlphaType.Opaque)
        {
            using var directImage = SKImage.FromBitmap(page);
            using var directData = directImage.Encode(SKEncodedImageFormat.Jpeg, 84);
            return new OcrPagePreview(pageNumber, pageIndex, pageCount, directData.ToArray(), w, h, engine);
        }
        // Resize always draws onto opaque white BGRA: grey or transparent scans encode cleanly.
        using var small = OcrGeometry.Resize(page, pw, ph);
        using var image = SKImage.FromBitmap(small);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 84);
        return new OcrPagePreview(pageNumber, pageIndex, pageCount, data.ToArray(), w, h, engine);
    }

    /// <summary>The page's words and Markdown blocks in reading order, boxes as page fractions.</summary>
    public static OcrPageTrace Build(
        int pageNumber,
        OcrPageResult page,
        TimeSpan elapsed,
        IReadOnlyList<OcrMarkdownBlock>? extractedBlocks = null)
    {
        float pw = page.ImageWidth > 0 ? page.ImageWidth : 1, ph = page.ImageHeight > 0 ? page.ImageHeight : 1;
        OcrNormRect Norm(float x, float y, float width, float height)
        {
            float l = Math.Clamp(x / pw, 0, 1), t = Math.Clamp(y / ph, 0, 1);
            float r = Math.Clamp((x + width) / pw, 0, 1), b = Math.Clamp((y + height) / ph, 0, 1);
            return new OcrNormRect(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
        }

        var blocks = extractedBlocks ?? OcrMarkdown.Blocks(page);
        var words = new List<OcrTraceWord>();
        var traced = new List<OcrTraceBlock>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            int before = words.Count;
            foreach (var line in block.Lines)
                foreach (var w in line.Words)
                    if (!string.IsNullOrWhiteSpace(w.Text))
                        words.Add(new OcrTraceWord(w.Text, Norm(w.X, w.Y, w.Width, w.Height), i));
            var bounds = block.Bounds;
            var box = bounds.IsEmpty ? default : Norm(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            traced.Add(new OcrTraceBlock(i, block.Kind, block.Markdown, box, words.Count - before));
        }
        var markdown = string.Join("\n\n", blocks.Select(b => b.Markdown)).Trim();
        return new OcrPageTrace(pageNumber, words, traced, markdown, elapsed);
    }

    /// <summary>Ambient observer set by the desktop window so every OCR path (direct import, File > Open,
    /// or drag-and-drop of a scanned PDF) drives the visual OCR experience.</summary>
    public static IOcrObserver? DefaultObserver { get; set; }

    /// <summary>Tells the observer a page is starting; never throws.</summary>
    public static void NotifyStarted(IOcrObserver? observer, SKBitmap page, int pageNumber, int pageIndex, int pageCount, string engine)
    {
        var target = observer ?? DefaultObserver;
        if (target is null) return;
        try { target.PageStarted(Preview(page, pageNumber, pageIndex, pageCount, engine)); }
        catch (Exception ex) { Debug.WriteLine($"OCR observer (start) failed: {ex.Message}"); }
    }

    /// <summary>Tells the observer a page is read; never throws.</summary>
    public static void NotifyRead(
        IOcrObserver? observer,
        int pageNumber,
        OcrPageResult page,
        TimeSpan elapsed,
        IReadOnlyList<OcrMarkdownBlock>? extractedBlocks = null)
    {
        var target = observer ?? DefaultObserver;
        if (target is null) return;
        try { target.PageRead(Build(pageNumber, page, elapsed, extractedBlocks)); }
        catch (Exception ex) { Debug.WriteLine($"OCR observer (read) failed: {ex.Message}"); }
    }

    /// <summary>
    /// Synthesizes a realistic document page preview (<see cref="OcrPagePreview"/>) and pixel-aligned
    /// word/element trace (<see cref="OcrPageTrace"/>) from any imported Markdown document (Word, PDF,
    /// Email, HTML, Spreadsheet, etc.) so the 4-act visual breakdown animation can play for every import.
    /// </summary>
    public static (OcrPagePreview Preview, OcrPageTrace Trace) SynthesizeFromMarkdown(
        string markdown,
        string engineLabel,
        TimeSpan? elapsed = null)
    {
        const int pageW = 960;
        const int pageH = 1320;
        const float marginX = 68f;
        const float contentW = pageW - marginX * 2f;

        OcrNormRect Norm(float x, float y, float width, float height)
        {
            float l = Math.Clamp(x / pageW, 0f, 1f), t = Math.Clamp(y / pageH, 0f, 1f);
            float r = Math.Clamp((x + width) / pageW, 0f, 1f), b = Math.Clamp((y + height) / pageH, 0f, 1f);
            return new OcrNormRect(l, t, Math.Max(0.01f, r - l), Math.Max(0.01f, b - t));
        }

        var parsedBlocks = ParseMarkdownBlocks(markdown);
        var words = new List<OcrTraceWord>();
        var tracedBlocks = new List<OcrTraceBlock>(parsedBlocks.Count);

        using var bmp = new SKBitmap(pageW, pageH, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(252, 253, 255));

        // Subtle document page frame and header rule
        using var borderPaint = new SKPaint { Color = new SKColor(226, 232, 240), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        canvas.DrawRect(new SKRect(20, 20, pageW - 20, pageH - 20), borderPaint);

        using var typefaceSans = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal) ?? SKTypeface.Default;
        using var typefaceBold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var headerPaint = new SKPaint { Color = new SKColor(148, 163, 184), IsAntialias = true, Typeface = typefaceSans, TextSize = 13f };
        canvas.DrawText(string.IsNullOrWhiteSpace(engineLabel) ? "MarkSmith Document Import" : engineLabel, marginX, 48f, headerPaint);
        canvas.DrawLine(marginX, 58f, pageW - marginX, 58f, borderPaint);

        float curY = 82f;
        int maxBlocksToTrace = Math.Min(parsedBlocks.Count, 24);

        for (int i = 0; i < maxBlocksToTrace; i++)
        {
            var (kind, rawBlock) = parsedBlocks[i];
            int wordsBefore = words.Count;
            float blockTop = Math.Min(curY, pageH - 80f);
            float blockLeft = marginX;
            float blockRight = marginX + 180f;

            if (curY < pageH - 90f)
            {
                if (kind == OcrBlockKind.Diagram)
                {
                    float boxH = 112f;
                    var rect = new SKRect(marginX, curY, marginX + contentW, Math.Min(pageH - 36f, curY + boxH));
                    DrawSynthesizedDiagram(canvas, rect, rawBlock, i, words, Norm, typefaceBold);
                    blockLeft = rect.Left;
                    blockRight = rect.Right;
                    curY = rect.Bottom + 22f;
                }
                else if (kind == OcrBlockKind.ShapeMosaic)
                {
                    float boxH = 104f;
                    var rect = new SKRect(marginX, curY, marginX + contentW, Math.Min(pageH - 36f, curY + boxH));
                    DrawSynthesizedMosaic(canvas, rect);
                    blockLeft = rect.Left;
                    blockRight = rect.Right;
                    curY = rect.Bottom + 22f;
                }
                else if (kind == OcrBlockKind.Picture)
                {
                    float boxH = 96f;
                    var rect = new SKRect(marginX, curY, marginX + contentW * 0.72f, Math.Min(pageH - 36f, curY + boxH));
                    DrawSynthesizedPicture(canvas, rect, rawBlock, typefaceSans);
                    blockLeft = rect.Left;
                    blockRight = rect.Right;
                    curY = rect.Bottom + 22f;
                }
                else
                {
                    bool isHeading = kind == OcrBlockKind.Heading;
                    bool isList = kind == OcrBlockKind.List;
                    float fontSize = isHeading ? (rawBlock.StartsWith("# ") ? 26f : 21f) : 16.5f;
                    float lineH = fontSize * 1.42f;
                    using var textPaint = new SKPaint
                    {
                        Color = isHeading ? new SKColor(15, 23, 42) : new SKColor(51, 65, 85),
                        IsAntialias = true,
                        Typeface = isHeading ? typefaceBold : typefaceSans,
                        TextSize = fontSize,
                    };

                    var linesToDraw = PrepareDisplayLines(rawBlock, kind);
                    int maxLines = isHeading ? 2 : 5;
                    foreach (var lineText in linesToDraw.Take(maxLines))
                    {
                        if (curY + lineH > pageH - 40f) break;
                        float x = marginX + (isList ? 18f : 0f);
                        if (isList)
                        {
                            using var bulletPaint = new SKPaint { Color = new SKColor(16, 185, 129), IsAntialias = true, Style = SKPaintStyle.Fill };
                            canvas.DrawCircle(marginX + 6f, curY + fontSize * 0.65f, 3.5f, bulletPaint);
                        }

                        var tokens = lineText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var tok in tokens)
                        {
                            float wWidth = Math.Max(14f, textPaint.MeasureText(tok));
                            if (x + wWidth > marginX + contentW && x > marginX + 24f)
                            {
                                curY += lineH;
                                x = marginX + (isList ? 18f : 0f);
                                if (curY + lineH > pageH - 40f) break;
                            }
                            canvas.DrawText(tok, x, curY + fontSize, textPaint);
                            words.Add(new OcrTraceWord(tok, Norm(x, curY + 2f, wWidth, fontSize + 4f), i));
                            if (x + wWidth > blockRight) blockRight = x + wWidth;
                            x += wWidth + fontSize * 0.34f;
                        }
                        curY += lineH;
                    }
                    curY += isHeading ? 14f : 16f;
                }
            }
            else
            {
                // Off-sheet blocks still contribute their words and block cards to the trace.
                foreach (var tok in ExtractPlainWords(rawBlock).Take(24))
                {
                    words.Add(new OcrTraceWord(tok, Norm(marginX + 12f, pageH - 64f, 48f, 18f), i));
                }
            }

            float blockBottom = Math.Clamp(Math.Max(blockTop + 24f, curY - 14f), blockTop + 20f, pageH - 28f);
            var blockBox = Norm(blockLeft, blockTop, Math.Max(120f, blockRight - blockLeft), blockBottom - blockTop);
            tracedBlocks.Add(new OcrTraceBlock(i, kind, rawBlock, blockBox, words.Count - wordsBefore));
        }

        var preview = Preview(bmp, 1, 0, 1, engineLabel);
        var trace = new OcrPageTrace(
            1,
            words,
            tracedBlocks,
            markdown.Trim(),
            elapsed ?? TimeSpan.FromMilliseconds(42));
        return (preview, trace);
    }

    private static List<(OcrBlockKind Kind, string Markdown)> ParseMarkdownBlocks(string markdown)
    {
        var list = new List<(OcrBlockKind, string)>();
        if (string.IsNullOrWhiteSpace(markdown)) return list;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0) { i++; continue; }

            // Container block :::...
            if (trimmed.StartsWith(":::"))
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(lines[i]);
                bool isMosaic = trimmed.Contains("compact=true", StringComparison.OrdinalIgnoreCase);
                bool isDiag = trimmed.StartsWith(":::shapes", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith(":::smartart", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith(":::workflow", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith(":::chart", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith(":::timeline", StringComparison.OrdinalIgnoreCase);
                i++;
                while (i < lines.Length && !lines[i].Trim().Equals(":::", StringComparison.Ordinal))
                {
                    sb.AppendLine(lines[i]);
                    i++;
                }
                if (i < lines.Length) { sb.AppendLine(lines[i]); i++; }
                var kind = isMosaic ? OcrBlockKind.ShapeMosaic : (isDiag ? OcrBlockKind.Diagram : OcrBlockKind.Paragraph);
                list.Add((kind, sb.ToString().Trim()));
                continue;
            }

            // Fenced code block ```...
            if (trimmed.StartsWith("```"))
            {
                bool isMermaid = trimmed.StartsWith("```mermaid", StringComparison.OrdinalIgnoreCase);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(lines[i]);
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    sb.AppendLine(lines[i]);
                    i++;
                }
                if (i < lines.Length) { sb.AppendLine(lines[i]); i++; }
                list.Add((isMermaid ? OcrBlockKind.Diagram : OcrBlockKind.Paragraph, sb.ToString().Trim()));
                continue;
            }

            // Single-line heading
            if (trimmed.StartsWith('#'))
            {
                list.Add((OcrBlockKind.Heading, trimmed));
                i++;
                continue;
            }

            // Standalone Markdown image
            if (trimmed.StartsWith("!["))
            {
                list.Add((OcrBlockKind.Picture, trimmed));
                i++;
                continue;
            }

            // Contiguous paragraph / list / table chunk until next blank line or special block
            var chunk = new List<string>();
            while (i < lines.Length)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith('#') || t.StartsWith(":::") || t.StartsWith("```") || t.StartsWith("!["))
                    break;
                chunk.Add(lines[i]);
                i++;
            }
            if (chunk.Count > 0)
            {
                string first = chunk[0].TrimStart();
                bool isList = first.StartsWith("- ") || first.StartsWith("* ") || first.StartsWith("+ ")
                    || (first.Length > 2 && char.IsDigit(first[0]) && (first[1] == '.' || (char.IsDigit(first[1]) && first[2] == '.')));
                list.Add((isList ? OcrBlockKind.List : OcrBlockKind.Paragraph, string.Join("\n", chunk).Trim()));
            }
        }
        return list;
    }

    private static List<string> PrepareDisplayLines(string rawBlock, OcrBlockKind kind)
    {
        if (kind == OcrBlockKind.Heading)
            return new List<string> { rawBlock.TrimStart('#', ' ').Trim() };
        if (kind == OcrBlockKind.List)
        {
            return rawBlock.Split('\n')
                .Select(l => l.Trim().TrimStart('-', '*', '+', '•').Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }
        return rawBlock.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.All(c => c is '|' or '-' or ':' or ' '))
            .Select(l => l.Trim('|', '>', ' '))
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static IEnumerable<string> ExtractPlainWords(string rawBlock) =>
        rawBlock.Split(new[] { ' ', '\t', '\r', '\n', '|', '#', '*', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length <= 32);

    private static void DrawSynthesizedDiagram(
        SKCanvas canvas, SKRect rect, string rawBlock, int blockIndex,
        List<OcrTraceWord> words, Func<float, float, float, float, OcrNormRect> norm, SKTypeface boldFont)
    {
        using var bg = new SKPaint { Color = new SKColor(245, 243, 255), Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { Color = new SKColor(168, 85, 247), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawRoundRect(rect, 8, 8, bg);
        canvas.DrawRoundRect(rect, 8, 8, stroke);

        var labels = new List<string>();
        foreach (var line in rawBlock.Split('\n'))
        {
            int idx = line.IndexOf("text=\"", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int s = idx + 6, e = line.IndexOf('"', s);
                if (e > s) labels.Add(line[s..e]);
            }
        }
        if (labels.Count == 0)
        {
            labels.Add("Ingest");
            labels.Add("Transform");
            labels.Add("Output");
        }

        int count = Math.Min(3, labels.Count);
        float boxW = Math.Min(170f, (rect.Width - 80f) / count);
        float boxH = 46f;
        float gap = (rect.Width - boxW * count) / (count + 1);
        float y = rect.MidY - boxH * 0.5f;

        using var nodeFill = new SKPaint { Color = new SKColor(37, 99, 235), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var linePaint = new SKPaint { Color = new SKColor(71, 85, 105), Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        using var labelPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Typeface = boldFont, TextSize = 14f };

        for (int n = 0; n < count; n++)
        {
            float x = rect.Left + gap + n * (boxW + gap);
            var nRect = new SKRect(x, y, x + boxW, y + boxH);
            canvas.DrawRoundRect(nRect, 6, 6, nodeFill);
            string lbl = labels[n];
            float tw = Math.Min(boxW - 12f, labelPaint.MeasureText(lbl));
            float tx = nRect.MidX - tw * 0.5f;
            float ty = nRect.MidY + 5f;
            canvas.DrawText(lbl, tx, ty, labelPaint);
            words.Add(new OcrTraceWord(lbl, norm(tx, nRect.MidY - 10f, tw, 20f), blockIndex));

            if (n + 1 < count)
            {
                float x1 = nRect.Right + 4f;
                float x2 = x + boxW + gap - 4f;
                canvas.DrawLine(x1, nRect.MidY, x2, nRect.MidY, linePaint);
            }
        }
    }

    private static void DrawSynthesizedMosaic(SKCanvas canvas, SKRect rect)
    {
        using var bg = new SKPaint { Color = new SKColor(253, 242, 248), Style = SKPaintStyle.Fill };
        using var border = new SKPaint { Color = new SKColor(236, 72, 153), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawRoundRect(rect, 8, 8, bg);
        canvas.DrawRoundRect(rect, 8, 8, border);

        var cols = new[]
        {
            new SKColor(56, 189, 248),
            new SKColor(168, 85, 247),
            new SKColor(236, 72, 153),
            new SKColor(16, 185, 129),
            new SKColor(245, 158, 11),
        };
        int nx = 14, ny = 3;
        float cw = (rect.Width - 28f) / nx;
        float ch = (rect.Height - 24f) / ny;
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        for (int r = 0; r < ny; r++)
        {
            for (int c = 0; c < nx; c++)
            {
                fill.Color = cols[(r * nx + c) % cols.Length];
                float cx = rect.Left + 14f + c * cw;
                float cy = rect.Top + 12f + r * ch;
                var cell = new SKRect(cx + 3f, cy + 3f, cx + cw - 3f, cy + ch - 3f);
                if ((r + c) % 2 == 0) canvas.DrawRoundRect(cell, 4, 4, fill);
                else canvas.DrawOval(cell, fill);
            }
        }
    }

    private static void DrawSynthesizedPicture(SKCanvas canvas, SKRect rect, string rawBlock, SKTypeface font)
    {
        using var bg = new SKPaint { Color = new SKColor(255, 241, 242), Style = SKPaintStyle.Fill };
        using var border = new SKPaint { Color = new SKColor(244, 63, 94), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        using var txt = new SKPaint { Color = new SKColor(190, 18, 60), IsAntialias = true, Typeface = font, TextSize = 14f };
        canvas.DrawRoundRect(rect, 8, 8, bg);
        canvas.DrawRoundRect(rect, 8, 8, border);
        string label = rawBlock.Length > 64 ? rawBlock[..64] + "…" : rawBlock;
        canvas.DrawText("🖼 " + label, rect.Left + 16f, rect.MidY + 5f, txt);
    }
}
