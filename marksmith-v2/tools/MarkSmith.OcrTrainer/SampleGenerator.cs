using MarkSmith.Ocr.Benchmark;
using MarkSmith.Ocr.MarkSmith;
using SkiaSharp;

namespace MarkSmith.OcrTrainer;

/// <summary>One training example: the network's input and the right answer.</summary>
public sealed record Sample(byte[] Image, float[] Features, int Label);

/// <summary>
/// Makes training samples the way the app will see letters: print a random line in a random
/// font, size and resolution, degrade it like a scan, run MarkSmith OCR's own preparation and
/// segmentation on it, then label each letter candidate from where the font says each glyph is.
/// </summary>
public sealed class SampleGenerator
{
    /// <summary>Fonts the network learns from. The benchmark's held-out fonts (FreeSerif, FreeSans,
    /// Bitstream Charter, Courier 10 Pitch) and FreeMono, Courier's near twin, are never used.
    /// The second group are open-licence (OFL) text families from Google Fonts, installed for
    /// training only (npm @fontsource packages, converted to TTF); any that aren't installed on
    /// the machine running the trainer are skipped.</summary>
    public static readonly string[] Families = new[]
    {
        "Liberation Serif", "Liberation Sans", "Liberation Mono", "Carlito", "Caladea",
        "DejaVu Sans", "DejaVu Serif", "DejaVu Sans Mono", "Inter",
        "Alegreya", "Cormorant Garamond", "Courier Prime", "Crimson Text", "EB Garamond", "Fira Mono", "Fira Sans",
        "Gelasio", "IBM Plex Mono", "IBM Plex Sans", "IBM Plex Serif", "Inconsolata", "Karla", "Lato",
        "Libre Baskerville", "Libre Franklin", "Lora", "Merriweather", "Noticia Text", "Noto Serif", "Nunito Sans",
        "Open Sans", "PT Sans", "PT Serif", "Roboto", "Roboto Mono", "Roboto Slab", "Source Sans 3",
        "Source Serif 4", "Space Mono", "Spectral", "Work Sans",
    }.Where(Installed).ToArray();

    private static bool Installed(string family)
    {
        using var tf = SkiaSharp.SKTypeface.FromFamilyName(family);
        return tf is not null && string.Equals(tf.FamilyName, family, StringComparison.OrdinalIgnoreCase);
    }

    private readonly string[] _words;
    private readonly Dictionary<string, int> _classIndex;

    public SampleGenerator(string[] words)
    {
        _words = words;
        _classIndex = MsGlyph.Charset.Select((c, i) => (c, i)).ToDictionary(t => t.c, t => t.i);
    }

    public int Touching, Unmatched;

    public List<Sample> Line(Random rng)
    {
        var text = RandomText(rng);
        var family = Families[rng.Next(Families.Length)];
        var weight = rng.NextDouble() < 0.25 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var slant = rng.NextDouble() < 0.2 ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        double pt = Math.Exp(Lerp(rng, Math.Log(7), Math.Log(28)));
        int dpi = new[] { 150, 200, 240, 300, 300, 300, 360, 400 }[rng.Next(8)];
        float px = (float)(pt * dpi / 72);
        if (px < 9) px = 9;

        var tf = Typeface(family, weight, slant);
        byte ink = (byte)(rng.NextDouble() < 0.15 ? rng.Next(60, 150) : rng.Next(0, 40));
        byte paper = (byte)(rng.NextDouble() < 0.2 ? rng.Next(205, 245) : 255);
        using var paint = new SKPaint { Typeface = tf, TextSize = px, IsAntialias = true, SubpixelText = true, Color = new SKColor(ink, ink, ink) };

        var glyphIds = paint.GetGlyphs(text);
        var positions = paint.GetGlyphPositions(text);
        paint.GetGlyphWidths(text, out var bounds);
        if (glyphIds.Length != text.Length || positions.Length != text.Length) return new List<Sample>(); // surrogates etc.

        float stretch = (float)Lerp(rng, 0.86, 1.14);
        float shear = slant == SKFontStyleSlant.Upright && rng.NextDouble() < 0.15 ? (float)Lerp(rng, -0.22, 0.05) : 0;
        float margin = px * 1.5f;
        float advance = paint.MeasureText(text);
        int w = (int)(advance * stretch + 2 * margin + Math.Abs(shear) * px * 2), h = (int)(px * 3);
        float baseline = px * 1.9f;
        var matrix = SKMatrix.CreateTranslation(margin + Math.Max(0, -shear) * px * 2, baseline);
        matrix = matrix.PreConcat(SKMatrix.CreateScale(stretch, 1)).PreConcat(SKMatrix.CreateSkew(shear, 0));

        // Tight tracking on some lines, so letters touch the way they do in compact serif type
        // (FreeSerif, Garamond, condensed headings): those touching pairs teach the network to
        // say "not one letter", and their halves teach it to read a split.
        float tracking = rng.NextDouble() < 0.3 ? (float)Lerp(rng, 0.86, 0.97) : 1f;
        if (tracking < 1)
            for (int i = 0; i < positions.Length; i++) positions[i] = new SKPoint(positions[i].X * tracking, positions[i].Y);
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(paper, paper, paper));
            canvas.SetMatrix(matrix);
            if (tracking < 1)
                for (int i = 0; i < text.Length; i++) canvas.DrawText(text[i].ToString(), positions[i].X, 0, paint);
            else
                canvas.DrawText(text, 0, 0, paint);
        }
        var style = new SynthStyle
        {
            Blur = rng.NextDouble() < 0.5 ? (float)Lerp(rng, 0.2, 1.3) : 0,
            Noise = rng.NextDouble() < 0.5 ? (float)Lerp(rng, 3, 26) : 0,
            Jpeg = rng.NextDouble() < 0.2 ? rng.Next(18, 70) : 0,
            Speckle = rng.NextDouble() < 0.15 ? (float)Lerp(rng, 0.0003, 0.002) : 0,
            Shading = (byte)(rng.NextDouble() < 0.2 ? rng.Next(20, 90) : 0),
            Seed = rng.Next(),
        };
        bmp = OcrSynth.Degrade(bmp, style);

        // Where each glyph's ink is, in image pixels.
        var boxes = new List<(float L, float R, string Ch)>();
        for (int i = 0; i < glyphIds.Length; i++)
        {
            string ch = text[i].ToString();
            if (char.IsWhiteSpace(text[i]) || bounds[i].Width <= 0) continue;
            var r = bounds[i];
            r.Offset(positions[i].X, positions[i].Y);
            var mapped = matrix.MapRect(r);
            boxes.Add((mapped.Left, mapped.Right, ch));
        }

        var samples = new List<Sample>();
        using (bmp)
        {
            var page = MsPage.Prepare(bmp, deskew: false);
            float sc = (float)page.Scale;
            foreach (var line in page.Lines)
                foreach (var g in line.Glyphs)
                {
                    float gl = g.Left / sc, gr = (g.Right + 1) / sc, gw = gr - gl;
                    var hits = boxes.Select(b => (b, Ov: Math.Min(b.R, gr) - Math.Max(b.L, gl)))
                                    .Where(t => t.Ov > 0).ToList();
                    string? label = null;
                    var full = hits.Where(t => t.Ov >= 0.6f * (t.b.R - t.b.L) && t.Ov >= 0.6f * gw).ToList();
                    var covering = hits.Where(t => t.Ov >= 0.6f * (t.b.R - t.b.L)).OrderBy(t => t.b.L).ToList();
                    if (covering.Count >= 2)
                    {
                        // Letters touching: as a whole, "not one letter" (class 0); then each piece,
                        // cut where one letter's ink ends and the next begins, as its own letter.
                        Touching++;
                        samples.Add(new Sample(Quantize(MsGlyph.Image(g, page.Binary.Width)), MsGlyph.Features(g, line), 0));
                        int cutStart = g.Left;
                        for (int k = 0; k < covering.Count; k++)
                        {
                            int cutEnd = k + 1 < covering.Count
                                ? (int)Math.Round((covering[k].b.R + covering[k + 1].b.L) / 2 * sc)
                                : g.Right;
                            cutEnd = Math.Clamp(cutEnd, cutStart + 1, g.Right);
                            if (_classIndex.TryGetValue(covering[k].b.Ch, out int pieceLabel) && cutEnd - cutStart >= 2)
                            {
                                var (pTop, pBottom, pink) = MsGlyph.Piece(g, page.Binary.Width, cutStart, cutEnd);
                                if (pink > 0)
                                    samples.Add(new Sample(Quantize(MsGlyph.Image(g, page.Binary.Width, cutStart, cutEnd)),
                                        MsGlyph.Features(g, line, pTop, pBottom, cutStart, cutEnd, pink), pieceLabel));
                            }
                            cutStart = cutEnd + 1;
                            if (cutStart >= g.Right) break;
                        }
                        continue;
                    }
                    if (full.Count == 1) label = full[0].b.Ch;
                    else if (hits.Count == 1 && hits[0].Ov >= 0.8f * gw)
                    {
                        // One mark of a two-mark glyph: the ticks of a double quote read as single quotes.
                        label = hits[0].b.Ch switch { "\"" => "'", "“" => "‘", "”" => "’", "…" => ".", _ => null };
                    }
                    else if (hits.Count == 0 && rng.NextDouble() < 0.5) label = ""; // a speck: "not a letter"
                    if (label is null || !_classIndex.TryGetValue(label, out int idx)) { Unmatched++; continue; }
                    samples.Add(new Sample(Quantize(MsGlyph.Image(g, page.Binary.Width)), MsGlyph.Features(g, line), idx));
                }
        }
        return samples;
    }

    // Font lookup goes through fontconfig on Linux, which isn't thread-safe: parallel lookups
    // deadlocked the generator. Look each face up once, under a lock.
    private static readonly Dictionary<(string, SKFontStyleWeight, SKFontStyleSlant), SKTypeface> Faces = new();

    private static SKTypeface Typeface(string family, SKFontStyleWeight weight, SKFontStyleSlant slant)
    {
        lock (Faces)
        {
            if (!Faces.TryGetValue((family, weight, slant), out var tf))
                Faces[(family, weight, slant)] = tf = OcrSynth.Typeface(new SynthStyle { Family = family, Weight = weight, Slant = slant });
            return tf;
        }
    }

    private static byte[] Quantize(float[] img)
    {
        var b = new byte[img.Length];
        for (int i = 0; i < img.Length; i++) b[i] = (byte)Math.Clamp(img[i] * 255 + 0.5, 0, 255);
        return b;
    }

    private static double Lerp(Random rng, double a, double b) => a + (b - a) * rng.NextDouble();

    private static readonly string Punct = ".,;:!?'\"()-";

    private string RandomText(Random rng)
    {
        var sb = new System.Text.StringBuilder();
        int target = rng.Next(18, 60);
        double mode = rng.NextDouble();
        while (sb.Length < target)
        {
            if (sb.Length > 0) sb.Append(' ');
            double r = rng.NextDouble();
            if (mode < 0.2 || r < 0.12) sb.Append(RandomChars(rng));
            else if (r < 0.25) sb.Append(RandomNumber(rng));
            else sb.Append(RandomWord(rng));
        }
        return sb.ToString();
    }

    private string RandomWord(Random rng)
    {
        var w = _words[rng.Next(_words.Length)];
        double c = rng.NextDouble();
        if (c < 0.22) w = char.ToUpperInvariant(w[0]) + w[1..];
        else if (c < 0.32) w = w.ToUpperInvariant();
        double p = rng.NextDouble();
        if (p < 0.12) w += ",";
        else if (p < 0.2) w += ".";
        else if (p < 0.23) w += new[] { ";", ":", "!", "?" }[rng.Next(4)];
        else if (p < 0.27) w = "(" + w + ")";
        else if (p < 0.31) w = (rng.NextDouble() < 0.5 ? "\"" + w + "\"" : "“" + w + "”");
        else if (p < 0.34) w += "'s";
        else if (p < 0.36) w += "’s";
        else if (p < 0.39) w += "-" + _words[rng.Next(_words.Length)];
        return w;
    }

    private static string RandomNumber(Random rng) => rng.Next(9) switch
    {
        0 => $"${rng.Next(1, 9999)}.{rng.Next(100):00}",
        1 => $"{rng.Next(1, 31):00}/{rng.Next(1, 13):00}/{rng.Next(1990, 2031)}",
        2 => $"{rng.Next(0, 100)}%",
        3 => $"+1 ({rng.Next(200, 999)}) {rng.Next(100, 999)}-{rng.Next(1000, 9999)}",
        4 => $"#{rng.Next(1, 99999)}",
        5 => $"{rng.Next(1, 999)}.{rng.Next(0, 99)}",
        6 => $"user{rng.Next(1, 99)}@example.{(rng.Next(2) == 0 ? "com" : "org")}",
        7 => $"https://www.site{rng.Next(1, 99)}.com/{(char)('a' + rng.Next(26))}{rng.Next(10)}",
        _ => rng.Next(0, 100000).ToString(),
    };

    private static string RandomChars(Random rng)
    {
        int len = rng.Next(1, 7);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < len; i++) sb.Append(MsGlyph.Charset[1 + rng.Next(MsGlyph.Charset.Length - 1)]);
        return sb.ToString();
    }
}
