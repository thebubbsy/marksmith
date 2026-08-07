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
    /// <summary>Re-compresses a PDF in memory. The output is a fresh, optimized package — every
    /// stream is rewritten with the default flate compressor, so bloated image filters and
    /// uncompressed data from upstream tools shrink without touching visual fidelity.
    /// <paramref name="preset"/> is currently reserved: both presets re-compress identically;
    /// image downsampling (the D6 follow-on) will key off it (Web/Email will then differ).</summary>
    public static PdfCompressionResult Compress(Stream pdf, CompressionPreset preset)
    {
        if (pdf is null) throw new ArgumentNullException(nameof(pdf));
        if (pdf.Length == 0) throw new ArgumentException("The PDF stream is empty.", nameof(pdf));
        var originalSize = pdf.Length;
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var (images, _) = CountImages(document);

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

    /// <summary>Analyzes a PDF without rewriting it: page count, embedded image count, and the
    /// total bytes those image streams occupy (the dominant bloat source in generated docs).</summary>
    public static PdfAnalysisResult Analyze(Stream pdf)
    {
        var total = pdf.Length;
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.ReadOnly);

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
                    if (item.Value is PdfDictionary dict &&
                        (dict.Elements?.GetString("/Subtype") ?? "").Contains("Image", StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                        try { bytes += dict.Stream?.Length ?? 0; } catch { /* best-effort */ }
                    }
                }
            }
        }
        catch { /* a malformed resource tree must never abort analysis */ }
        return (count, bytes);
    }
}
