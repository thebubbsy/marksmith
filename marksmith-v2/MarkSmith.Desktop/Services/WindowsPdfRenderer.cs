using System;
using System.IO;
using SkiaSharp;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MarkSmith.Services;

/// <summary>
/// Draws a PDF page with the PDF engine built into Windows (Windows.Data.Pdf), for OCR on pages
/// whose text can't be read and that aren't a single scanned picture.
/// </summary>
public static class WindowsPdfRenderer
{
    /// <summary>Resolution OCR reads at: 300 dpi is what scanners and OCR engines are tuned for.</summary>
    public const double Dpi = 300;

    private static readonly object Gate = new();
    private static (string Path, PdfDocument Doc)? _last;

    /// <summary>Page <paramref name="pageNumber"/> (1-based) of the PDF at <paramref name="path"/>,
    /// or null if Windows can't open it. Called from the import's background thread.</summary>
    public static SKBitmap? Render(string path, int pageNumber)
    {
        try
        {
            PdfDocument doc;
            lock (Gate)
            {
                if (_last is { } l && string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase)) doc = l.Doc;
                else
                {
                    var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
                    doc = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
                    _last = (path, doc);
                }
            }
            if (pageNumber < 1 || pageNumber > doc.PageCount) return null;
            using var page = doc.GetPage((uint)(pageNumber - 1));
            using var stream = new InMemoryRandomAccessStream();
            // Page size is in device-independent pixels (1/96 inch).
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Round(page.Size.Width * Dpi / 96),
                DestinationHeight = (uint)Math.Round(page.Size.Height * Dpi / 96),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };
            page.RenderToStreamAsync(stream, options).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);
            using var net = stream.AsStreamForRead();
            return SKBitmap.Decode(net);
        }
        catch
        {
            return null;
        }
    }
}
