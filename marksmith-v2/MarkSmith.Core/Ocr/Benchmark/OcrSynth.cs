using SkiaSharp;

namespace MarkSmith.Ocr.Benchmark;

/// <summary>How a synthetic page is printed and then "scanned".</summary>
public sealed record SynthStyle
{
    public string Family { get; init; } = "Liberation Serif";
    public SKFontStyleWeight Weight { get; init; } = SKFontStyleWeight.Normal;
    public SKFontStyleSlant Slant { get; init; } = SKFontStyleSlant.Upright;
    public float PointSize { get; init; } = 12;
    public int Dpi { get; init; } = 300;
    public float LineSpacing { get; init; } = 1.25f;
    /// <summary>Text width in inches (a 6.5" block is a Letter page's text area).</summary>
    public float WidthInches { get; init; } = 6.5f;
    public float SkewDegrees { get; init; }
    public float Blur { get; init; }
    /// <summary>Std-dev of grey noise added after printing, in 0–255 levels.</summary>
    public float Noise { get; init; }
    /// <summary>JPEG quality to re-encode at (0 = none).</summary>
    public int Jpeg { get; init; }
    /// <summary>Uneven lighting: how much darker (0–255) one corner of the page is than the opposite one.</summary>
    public byte Shading { get; init; }
    /// <summary>Fraction of pixels flipped to black or white specks (dust, toner).</summary>
    public float Speckle { get; init; }
    public byte Ink { get; init; } = 0;
    public byte Paper { get; init; } = 255;
    public int Seed { get; init; } = 1;
}

/// <summary>One block of a synthetic page: a heading or a paragraph, wrapped to the text width.</summary>
public sealed record SynthBlock(string Text, SynthStyle? Style = null, float SpaceAfterLines = 0.6f);

/// <summary>A rendered page and the text that is on it, line by line.</summary>
public sealed record SynthPage(SKBitmap Image, IReadOnlyList<string> Lines) : IDisposable
{
    public string Text => string.Join("\n", Lines);
    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Prints text onto a white page with SkiaSharp and degrades it like a scan (skew, blur, noise,
/// JPEG, low DPI, low contrast). The OCR benchmark and MarkSmith OCR's training data both come
/// from here, so the two see the same kind of page.
/// </summary>
public static class OcrSynth
{
    public static SKTypeface Typeface(SynthStyle s) =>
        SKFontManager.Default.MatchFamily(s.Family, new SKFontStyle(s.Weight, SKFontStyleWidth.Normal, s.Slant))
        ?? SKTypeface.FromFamilyName(s.Family, s.Weight, SKFontStyleWidth.Normal, s.Slant)
        ?? SKTypeface.Default;

    /// <summary>Greedy word wrap at the given width in pixels.</summary>
    public static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        using var measure = new SKPaint { Typeface = font.Typeface, TextSize = font.Size, SubpixelText = true };
        var lines = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && measure.MeasureText(candidate) > maxWidth) { lines.Add(line); line = word; }
                else line = candidate;
            }
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    /// <summary>Single-column page.</summary>
    public static SynthPage Render(IReadOnlyList<SynthBlock> blocks, SynthStyle page) => RenderColumns(new[] { blocks }, page);

    /// <summary>A page of side-by-side columns (each a list of blocks); the expected text reads
    /// the first column top to bottom, then the next.</summary>
    public static SynthPage RenderColumns(IReadOnlyList<IReadOnlyList<SynthBlock>> columns, SynthStyle page)
    {
        float px = page.Dpi / 72f;
        float margin = page.Dpi * 0.5f;
        float gutter = page.Dpi * 0.35f;
        float textWidth = page.WidthInches * page.Dpi;
        float colWidth = (textWidth - gutter * (columns.Count - 1)) / columns.Count;

        // Lay out first to know the height.
        var placed = new List<(string Line, SKFont Font, float X, float Baseline)>();
        var truth = new List<string>();
        float maxBottom = 0;
        for (int c = 0; c < columns.Count; c++)
        {
            float x = margin + c * (colWidth + gutter);
            float y = margin;
            foreach (var block in columns[c])
            {
                var st = block.Style ?? page;
                var font = new SKFont(Typeface(st), st.PointSize * px) { Subpixel = true, Edging = SKFontEdging.Antialias };
                float lineH = st.PointSize * px * st.LineSpacing;
                foreach (var line in Wrap(block.Text, font, colWidth))
                {
                    y += lineH;
                    placed.Add((line, font, x, y - lineH * 0.22f));
                    truth.Add(line);
                }
                y += lineH * block.SpaceAfterLines;
            }
            maxBottom = Math.Max(maxBottom, y);
        }

        int width = (int)(textWidth + 2 * margin), height = (int)(maxBottom + margin);
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(page.Paper, page.Paper, page.Paper));
            if (page.SkewDegrees != 0) canvas.RotateDegrees(page.SkewDegrees, width / 2f, height / 2f);
            using var paint = new SKPaint { Color = new SKColor(page.Ink, page.Ink, page.Ink), IsAntialias = true };
            foreach (var (line, font, x, baseline) in placed)
                canvas.DrawText(line, x, baseline, font, paint);
        }
        foreach (var f in placed.Select(p => p.Font).Distinct()) f.Dispose();

        bmp = Degrade(bmp, page);
        return new SynthPage(bmp, truth);
    }

    public static SKBitmap Degrade(SKBitmap bmp, SynthStyle s)
    {
        if (s.Blur > 0)
        {
            var blurred = new SKBitmap(bmp.Info);
            using (var canvas = new SKCanvas(blurred))
            using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(s.Blur, s.Blur) })
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(bmp, 0, 0, paint);
            }
            bmp.Dispose();
            bmp = blurred;
        }
        if (s.Shading > 0 || s.Speckle > 0)
        {
            var rng = new Random(s.Seed * 31 + 5);
            var p = bmp.GetPixelSpan().ToArray();
            int w = bmp.Width, h = bmp.Height;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    double shade = s.Shading * (x / (double)w + y / (double)h) / 2;
                    for (int k = 0; k < 3; k++) p[i + k] = (byte)Math.Clamp(p[i + k] - shade, 0, 255);
                    if (s.Speckle > 0 && rng.NextDouble() < s.Speckle)
                    {
                        byte v = rng.NextDouble() < 0.5 ? (byte)0 : (byte)255;
                        p[i] = p[i + 1] = p[i + 2] = v;
                    }
                }
            System.Runtime.InteropServices.Marshal.Copy(p, 0, bmp.GetPixels(), p.Length);
        }
        if (s.Noise > 0)
        {
            var rng = new Random(s.Seed);
            var p = bmp.GetPixelSpan().ToArray();
            for (int i = 0; i < p.Length; i += 4)
            {
                // Box-Muller grey noise.
                double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
                double n = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * s.Noise;
                for (int k = 0; k < 3; k++) p[i + k] = (byte)Math.Clamp(p[i + k] + n, 0, 255);
            }
            System.Runtime.InteropServices.Marshal.Copy(p, 0, bmp.GetPixels(), p.Length);
        }
        if (s.Jpeg > 0)
        {
            using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, s.Jpeg);
            var decoded = SKBitmap.Decode(data);
            bmp.Dispose();
            bmp = decoded.Copy(SKColorType.Bgra8888);
            decoded.Dispose();
        }
        return bmp;
    }
}
