using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using MarkSmith.Services;

namespace MarkSmith.Services;

/// <summary>
/// Windows 10+ OCR provider using the built-in Windows.Media.Ocr engine.
/// Supports 25+ languages out of the box with no additional downloads.
/// Falls back gracefully if no OCR language packs are installed.
/// </summary>
public sealed class WindowsOcrProvider : IOcrProvider
{
    private readonly OcrEngine _engine;

    public WindowsOcrProvider()
    {
        // Try user's profile language first, then fall back to English.
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                  ?? throw new PlatformNotSupportedException("No OCR language packs available on this machine.");
    }

    public string EngineName => "Windows.Media.Ocr";

    public bool IsAvailable => true;

    public async Task<OcrPageResult> RecognizeAsync(SKBitmap bitmap)
    {
        // Convert SKBitmap to SoftwareBitmap for the WinRT OCR API.
        using var image = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new InMemoryRandomAccessStream();
        using (var netStream = ms.AsStreamForWrite())
        {
            image.SaveTo(netStream);
            await netStream.FlushAsync();
        }
        ms.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ms);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var ocrResult = await _engine.RecognizeAsync(softwareBitmap);

        // Map WinRT OCR results to our platform-agnostic model.
        var lines = new List<OcrLine>();
        foreach (var rtLine in ocrResult.Lines)
        {
            var words = rtLine.Words.Select(w =>
            {
                var rect = w.BoundingRect;
                return new OcrWord(w.Text, (float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
            }).ToList();

            // Line Y and height from the first word's bounding box.
            var firstRect = rtLine.Words[0].BoundingRect;
            lines.Add(new OcrLine(rtLine.Text, words, (float)firstRect.Y, (float)firstRect.Height));
        }

        return new OcrPageResult(lines, (float)softwareBitmap.PixelWidth, (float)softwareBitmap.PixelHeight);
    }
}
