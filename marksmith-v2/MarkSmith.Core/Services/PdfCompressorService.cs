using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace MarkSmith.Services;

/// <summary>Compression intensity for the PDF size reducer (D6).</summary>
public enum CompressionPreset { Web, Email }

/// <summary>Outcome of a compression pass: the reduced PDF, sizes, and derived metrics.</summary>
public sealed record PdfCompressionResult(
    byte[] CompressedPdf,
    long OriginalSize,
    long CompressedSize,
    int PagesProcessed,
    int ImagesProcessed)
{
    public double SavingsPercent => OriginalSize > 0 ? (OriginalSize - CompressedSize) / (double)OriginalSize * 100.0 : 0;
    public bool WasReduced => CompressedSize < OriginalSize;
}

/// <summary>Pre-compression analysis of a PDF: page/image inventory + human-readable sizes.</summary>
public sealed record PdfAnalysisResult(long TotalSize, int PageCount, int ImageCount, long ImageSize)
{
    public string HumanTotalSize => Human(TotalSize);
    public string HumanImageSize => Human(ImageSize);

    private static string Human(long bytes)
    {
        if (bytes >= 1024 * 1024) return (bytes / 1048576.0).ToString("0.0") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return bytes + " B";
    }
}

/// <summary>
/// High-throughput PDF size compressor (backlog D6): re-serializes a PDF through PDFsharp so all
/// streams are re-compressed (flate) and the package is rebuilt cleanly. Post-processes generated
/// PDFs for email sharing.
/// </summary>
public static class PdfCompressorService
{
    /// <summary>Re-compresses a PDF in memory and applies the preset's image policy. The output is
    /// a fresh, optimized package — every stream is rewritten with the default flate compressor, so
    /// bloated image filters and uncompressed data from upstream tools shrink without touching
    /// visual fidelity. Beyond re-compression, the preset governs how aggressively embedded JPEGs
    /// are downsampled: Web targets small files (800px longest edge, quality 70), Email keeps more
    /// detail (1200px, quality 85).</summary>
    public static PdfCompressionResult Compress(Stream pdf, CompressionPreset preset)
    {
        if (pdf is null) throw new ArgumentNullException(nameof(pdf));
        if (pdf.Length == 0) throw new ArgumentException("The PDF stream is empty.", nameof(pdf));
        var originalSize = pdf.Length;
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);

        // D6 image downsampler: the preset governs how aggressively embedded JPEGs are scaled
        // down. Web is the "email this quickly" tier (smaller files), Email keeps more detail.
        (int maxDim, int quality) = preset switch
        {
            CompressionPreset.Web => (800, 70),
            CompressionPreset.Email => (1200, 85),
            _ => (1200, 85),
        };
        DownsampleImages(document, maxDim, quality);

        var (images, imageBytes) = CountImages(document);

        // A fresh PdfDocument re-serializes every page with default (flate) compression.
        using var output = new PdfDocument();
        foreach (var page in document.Pages)
        {
            output.AddPage(page);
        }

        var ms = new MemoryStream();
        output.Save(ms, false);
        var compressed = ms.ToArray();
        return new PdfCompressionResult(compressed, originalSize, compressed.Length, document.PageCount, images);
    }

    /// <summary>Best-effort JPEG XObject downsampler: any embedded DeviceRGB DCTDecode image whose
    /// longest edge exceeds <paramref name="maxDim"/> is decoded with SkiaSharp, resized to fit,
    /// re-encoded as JPEG at <paramref name="quality"/>, and written back into its XObject.
    /// Only plain-JPEG images are touched (anything with exotic filters, color spaces, or
    /// decode parameters is left alone); a re-encode that isn't smaller is discarded. A malformed
    /// image tree never aborts the pass — it just skips.</summary>
    private static void DownsampleImages(PdfDocument document, int maxDim, int quality)
    {
        try
        {
            foreach (var page in document.Pages)
            {
                var resources = page.Elements?.GetDictionary("/Resources");
                var xobjects = resources?.Elements?.GetDictionary("/XObject");
                if (xobjects is null) continue;
                foreach (var item in xobjects.Elements)
                {
                    // PDFsharp 6.x yields PdfReference values here — resolve by key (GetDictionary
                    // dereferences) instead of casting the raw value.
                    if (xobjects.Elements.GetDictionary(item.Key) is not PdfDictionary dict) continue;
                    if (!(dict.Elements?.GetString("/Subtype") ?? "").Contains("Image", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!(dict.Elements?.GetString("/Filter") ?? "").Contains("DCTDecode", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(dict.Elements?.GetString("/ColorSpace"), "/DeviceRGB", StringComparison.Ordinal)) continue;
                    int width = dict.Elements?.GetInteger("/Width") ?? 0;
                    int height = dict.Elements?.GetInteger("/Height") ?? 0;
                    if (width <= 0 || height <= 0 || Math.Max(width, height) <= maxDim) continue;

                    var stream = dict.Stream;
                    if (stream is null || stream.Length <= 0) continue;
                    var jpeg = stream.Value; // raw (filtered) bytes — for DCTDecode that IS the JPEG

                    using var bitmap = SkiaSharp.SKBitmap.Decode(jpeg);
                    if (bitmap is null) continue;
                    float scale = (float)maxDim / Math.Max(bitmap.Width, bitmap.Height);
                    int newWidth = Math.Max(1, (int)(bitmap.Width * scale));
                    int newHeight = Math.Max(1, (int)(bitmap.Height * scale));
                    using var resized = bitmap.Resize(new SkiaSharp.SKImageInfo(newWidth, newHeight), SkiaSharp.SKFilterQuality.Medium);
                    if (resized is null) continue;
                    using var image = SkiaSharp.SKImage.FromBitmap(resized);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, quality);
                    var bytes = data?.ToArray();
                    if (bytes is null || bytes.Length >= jpeg.Length) continue; // not worth it — keep original

                    stream.Value = bytes;
                    dict.Elements?.SetInteger("/Width", newWidth);
                    dict.Elements?.SetInteger("/Height", newHeight);
                }
            }
        }
        catch { /* a malformed resource tree must never abort compression */ }
    }

    /// <summary>Analyzes a PDF without rewriting it: page count, embedded image count, and the
    /// total bytes those image streams occupy (the dominant bloat source in generated docs).</summary>
    public static PdfAnalysisResult Analyze(Stream pdf)
    {
        var total = pdf.Length;
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);

        var (imageCount, imageBytes) = CountImages(document);
        return new PdfAnalysisResult(total, document.PageCount, imageCount, imageBytes);
    }

    /// <summary>Counts embedded image XObjects and their stream bytes (best-effort — PDFsharp 6.x
    /// hides references, so a malformed tree or exotic structure simply yields zero).</summary>
    private static (int count, long bytes) CountImages(PdfDocument document)
    {
        int count = 0;
        long bytes = 0;
        try
        {
            foreach (var page in document.Pages)
            {
                var resources = page.Elements?.GetDictionary("/Resources");
                var xobjects = resources?.Elements?.GetDictionary("/XObject");
                if (xobjects is null) continue;
                foreach (var item in xobjects.Elements)
                {
                    // PDFsharp 6.x yields PdfReference values here — resolve by key (GetDictionary
                    // dereferences) instead of casting the raw value.
                    if (xobjects.Elements.GetDictionary(item.Key) is not PdfDictionary dict) continue;
                    if (!(dict.Elements?.GetString("/Subtype") ?? "").Contains("Image", StringComparison.OrdinalIgnoreCase)) continue;
                    count++;
                    try { bytes += dict.Stream?.Length ?? 0; } catch { /* best-effort */ }
                }
            }
        }
        catch { /* a malformed resource tree must never abort analysis */ }
        return (count, bytes);
    }
}
