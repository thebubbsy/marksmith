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

    /// <param name="observer">Watches the read as it happens (the desktop app's reading animation).</param>
    public static Task<OcrImportResult> ImageToMarkdownAsync(string path, string? engineId = null, IOcrObserver? observer = null) => Task.Run(async () =>
    {
        using var bitmap = OcrGeometry.DecodeUpright(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)} isn't a picture MarkSmith can open.");
        var engine = OcrEngines.Create(engineId ?? AppServices.Settings.Current.OcrEngine, out bool fellBack);
        var straight = OcrPreprocess.Deskew(bitmap, out _);
        try
        {
            OcrTrace.NotifyStarted(observer, straight, 1, 0, 1, engine.EngineName);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var page = await engine.RecognizeAsync(straight);
            string fullPath = Path.GetFullPath(path);
            string mediaDir = Path.Combine(
                Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
                Path.GetFileNameWithoutExtension(fullPath) + "_media");
            var extracted = OcrVisualElements.ExtractPage(straight, page, new OcrVisualOptions
            {
                PageNumber = 1,
                MediaDirectory = mediaDir,
                MediaLink = Path.GetFileName(mediaDir),
            });
            OcrTrace.NotifyRead(observer, 1, page, clock.Elapsed, extracted.Blocks);
            return new OcrImportResult(extracted.Markdown, engine.EngineName, fellBack);
        }
        finally
        {
            if (!ReferenceEquals(straight, bitmap)) straight.Dispose();
        }
    });
}
