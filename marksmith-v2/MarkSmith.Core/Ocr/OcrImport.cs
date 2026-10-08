using SkiaSharp;

namespace MarkSmith.Ocr;

/// <summary>A picture of text read into Markdown, and by which engine.</summary>
public sealed record OcrImportResult(string Markdown, string Engine, bool FellBack);

/// <summary>Reads a picture (PNG, JPEG, BMP, GIF, WebP) of a page into Markdown with the engine
/// Settings picked: decode, straighten, recognise, lay out as paragraphs, headings and lists.</summary>
public static class OcrImport
{
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static Task<OcrImportResult> ImageToMarkdownAsync(string path, string? engineId = null) => Task.Run(async () =>
    {
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)} isn't a picture MarkSmith can open.");
        var engine = OcrEngines.Create(engineId ?? AppServices.Settings.Current.OcrEngine, out bool fellBack);
        var straight = OcrPreprocess.Deskew(bitmap, out _);
        try
        {
            var page = await engine.RecognizeAsync(straight);
            return new OcrImportResult(OcrMarkdown.FromPage(page), engine.EngineName, fellBack);
        }
        finally
        {
            if (!ReferenceEquals(straight, bitmap)) straight.Dispose();
        }
    });
}
