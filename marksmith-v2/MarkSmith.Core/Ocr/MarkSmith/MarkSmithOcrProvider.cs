using MarkSmith.Services;
using SkiaSharp;

namespace MarkSmith.Ocr.MarkSmith;

/// <summary>
/// MarkSmith OCR: written from the ground up in C#, no third-party OCR code or models. Page
/// clean-up and deskew (OcrPreprocess), segmentation into blocks, lines and letters (MsSegmenter),
/// a letter network trained by MarkSmith on synthetic print (MsGlyphNet, tools/MarkSmith.OcrTrainer),
/// touching-letter splitting, word spacing and dictionary correction (MsRecognizer, MsLexicon).
/// </summary>
public sealed class MarkSmithOcrProvider : IOcrProvider
{
    private static readonly Lazy<MsGlyphNet?> Net = new(LoadNet, isThreadSafe: true);
    private static readonly Lazy<MsLexicon?> Lexicon = new(MsLexicon.LoadEmbedded, isThreadSafe: true);

    public string EngineName => "MarkSmith OCR";

    public bool IsAvailable => Net.Value is not null;

    /// <summary>Loads a network from a file instead of the built-in one (the trainer's output).</summary>
    public static MsGlyphNet? OverrideNet { get; set; }

    private static MsGlyphNet? LoadNet()
    {
        using var s = typeof(MarkSmithOcrProvider).Assembly.GetManifestResourceStream("MarkSmith.Ocr.MarkSmith.glyphnet.bin");
        return s is null ? null : MsGlyphNet.Load(s);
    }

    public Task<OcrPageResult> RecognizeAsync(SKBitmap bitmap) => Task.Run(() => Recognize(bitmap));

    public OcrPageResult Recognize(SKBitmap bitmap)
    {
        var net = OverrideNet ?? Net.Value ?? throw new InvalidOperationException("MarkSmith OCR's network is missing from this build.");
        var page = MsPage.Prepare(bitmap);
        var recognizer = new MsRecognizer(net, Lexicon.Value);
        var read = new ReadLineResult[page.Lines.Count];
        Parallel.For(0, page.Lines.Count, i => read[i] = recognizer.Read(page.Lines[i], page.Binary.Width));

        float s = (float)page.Scale;
        var lines = new List<OcrLine>();
        foreach (var r in read)
        {
            if (r.Words.Count == 0) continue;
            var words = r.Words.Select(w => new OcrWord(
                string.Concat(w.Select(c => c.Text)),
                w[0].Left / s, r.Line.Top / s, (w[^1].Right - w[0].Left + 1) / s, (r.Line.Bottom - r.Line.Top + 1) / s)).ToList();
            lines.Add(new OcrLine(r.Text, words, r.Line.Top / s, (r.Line.Bottom - r.Line.Top + 1) / s));
        }
        return new OcrPageResult(lines, bitmap.Width, bitmap.Height);
    }
}
