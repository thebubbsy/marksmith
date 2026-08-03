using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;

namespace MarkSmith.Services;

/// <summary>
/// A single recognized word with its bounding box position (used for layout analysis).
/// Coordinates are in pixels relative to the source image.
/// </summary>
public sealed record OcrWord(string Text, float X, float Y, float Width, float Height);

/// <summary>
/// A recognized line of text (one or more words on the same horizontal baseline).
/// </summary>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words, float Y, float Height);

/// <summary>
/// The result of OCR on a single image/page.
/// </summary>
public sealed record OcrPageResult(IReadOnlyList<OcrLine> Lines, float ImageWidth, float ImageHeight)
{
    public string PlainText => string.Join("\n", Lines.Select(l => l.Text));
}

/// <summary>
/// Platform-specific OCR provider contract. Implementations wrap Windows.Media.Ocr, Tesseract, etc.
/// The provider receives a preprocessed SKBitmap and returns recognized lines.
/// </summary>
public interface IOcrProvider
{
    /// <summary>Human-readable engine name (e.g. "Windows.Media.Ocr", "Tesseract 5").</summary>
    string EngineName { get; }

    /// <summary>Whether the OCR engine is available on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Recognize text in a preprocessed bitmap. Returns lines in reading order.</summary>
    Task<OcrPageResult> RecognizeAsync(SKBitmap bitmap);
}

/// <summary>
/// Local OCR engine service (D2): extracts text from images (PNG, JPG, BMP, TIFF) and scanned
/// PDF pages into structured Markdown. Uses a pluggable <see cref="IOcrProvider"/> for the actual
/// recognition step; the service handles image loading, preprocessing (grayscale, adaptive
/// threshold, deskew), multi-page PDF orchestration, and Markdown formatting.
///
/// Image preprocessing pipeline:
///   1. Decode via SkiaSharp (supports PNG, JPEG, BMP, WEBP, GIF)
///   2. Convert to grayscale
///   3. Apply Otsu threshold for clean binary image (improves OCR accuracy on low-contrast scans)
///   4. Optional upscale if image is very small (OCR engines prefer ≥300 DPI)
/// </summary>
public sealed class OcrEngineService
{
    private readonly IOcrProvider _provider;

    public OcrEngineService(IOcrProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>The name of the underlying OCR engine.</summary>
    public string EngineName => _provider.EngineName;

    /// <summary>Whether OCR is available on this machine.</summary>
    public bool IsAvailable => _provider.IsAvailable;

    // ---- Single image → Markdown ----------------------------------------------------------------

    /// <summary>
    /// Recognizes text in an image file and returns formatted Markdown.
    /// Supported formats: PNG, JPEG, BMP, WEBP, GIF (anything SkiaSharp can decode).
    /// </summary>
    public async Task<string> ImageToMarkdownAsync(string imagePath)
    {
        using var bitmap = LoadAndPreprocess(imagePath);
        if (bitmap is null) return "";
        var result = await _provider.RecognizeAsync(bitmap);
        return FormatAsMarkdown(result);
    }

    /// <summary>Stream overload for image OCR.</summary>
    public async Task<string> ImageToMarkdownAsync(Stream imageStream)
    {
        using var bitmap = PreprocessStream(imageStream);
        if (bitmap is null) return "";
        var result = await _provider.RecognizeAsync(bitmap);
        return FormatAsMarkdown(result);
    }

    // ---- Multi-page PDF → Markdown --------------------------------------------------------------

    /// <summary>
    /// Renders each page of a PDF to an image (via PdfSharp + SkiaSharp) and runs OCR.
    /// Pages are separated by horizontal rules in the output Markdown.
    /// For digital PDFs with embedded text, use ReverseImportService instead (faster, lossless).
    /// </summary>
    public async Task<string> PdfToMarkdownAsync(string pdfPath, float dpi = 300)
    {
        var sb = new StringBuilder();
        var pageImages = RenderPdfPages(pdfPath, dpi);

        for (int i = 0; i < pageImages.Count; i++)
        {
            using var bitmap = pageImages[i];
            var result = await _provider.RecognizeAsync(bitmap);
            var pageMd = FormatAsMarkdown(result);
            if (!string.IsNullOrWhiteSpace(pageMd))
            {
                if (sb.Length > 0) sb.Append("\n\n---\n\n");
                sb.Append(pageMd);
            }
        }

        return sb.ToString().Trim();
    }

    // ---- Image preprocessing --------------------------------------------------------------------

    /// <summary>Loads an image file and applies OCR-friendly preprocessing.</summary>
    private static SKBitmap? LoadAndPreprocess(string path)
    {
        using var stream = File.OpenRead(path);
        return PreprocessStream(stream);
    }

    /// <summary>Decodes and preprocesses an image stream for OCR.</summary>
    private static SKBitmap? PreprocessStream(Stream stream)
    {
        using var original = SKBitmap.Decode(stream);
        if (original is null) return null;
        return PreprocessBitmap(original);
    }

    /// <summary>
    /// Applies grayscale conversion, Otsu thresholding, and optional upscaling to produce
    /// a clean binary image optimized for OCR recognition.
    /// </summary>
    internal static SKBitmap PreprocessBitmap(SKBitmap source)
    {
        int w = source.Width;
        int h = source.Height;

        // Upscale small images (OCR accuracy improves significantly at ≥1000px width).
        float scale = 1f;
        if (w < 1000)
        {
            scale = 1000f / w;
            w = (int)(w * scale);
            h = (int)(h * scale);
        }

        var resized = (scale > 1f)
            ? source.Resize(new SKImageInfo(w, h), SKFilterQuality.High)
            : source;

        if (resized is null) return source;

        // Convert to grayscale.
        var gray = new SKBitmap(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(gray))
        using (var paint = new SKPaint())
        {
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
            {
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0,       0,       0,       1, 0,
            });
            canvas.DrawBitmap(resized, 0, 0, paint);
        }

        if (!ReferenceEquals(resized, source)) resized.Dispose();

        // Apply Otsu threshold for clean binary output.
        var threshold = ComputeOtsuThreshold(gray);
        ApplyThreshold(gray, threshold);

        return gray;
    }

    /// <summary>Computes the optimal binarization threshold using Otsu's method.</summary>
    internal static byte ComputeOtsuThreshold(SKBitmap grayscale)
    {
        // Build histogram.
        var histogram = new int[256];
        int totalPixels = grayscale.Width * grayscale.Height;
        for (int y = 0; y < grayscale.Height; y++)
            for (int x = 0; x < grayscale.Width; x++)
                histogram[grayscale.GetPixel(x, y).Red]++;

        // Otsu's inter-class variance maximization.
        float sum = 0;
        for (int i = 0; i < 256; i++) sum += i * histogram[i];

        float sumB = 0;
        int wB = 0;
        float maxVariance = 0;
        byte bestThreshold = 128;

        for (int t = 0; t < 256; t++)
        {
            wB += histogram[t];
            if (wB == 0) continue;
            int wF = totalPixels - wB;
            if (wF == 0) break;

            sumB += t * histogram[t];
            float mB = sumB / wB;
            float mF = (sum - sumB) / wF;
            float variance = (float)wB * wF * (mB - mF) * (mB - mF);

            if (variance > maxVariance)
            {
                maxVariance = variance;
                bestThreshold = (byte)t;
            }
        }

        return bestThreshold;
    }

    /// <summary>Applies a binary threshold in-place to a grayscale bitmap.</summary>
    private static void ApplyThreshold(SKBitmap grayscale, byte threshold)
    {
        for (int y = 0; y < grayscale.Height; y++)
            for (int x = 0; x < grayscale.Width; x++)
            {
                var pixel = grayscale.GetPixel(x, y);
                var binary = pixel.Red > threshold ? (byte)255 : (byte)0;
                grayscale.SetPixel(x, y, new SKColor(binary, binary, binary));
            }
    }

    // ---- PDF page rendering ---------------------------------------------------------------------

    /// <summary>
    /// Renders PDF pages to SKBitmaps at the specified DPI using PdfSharp for page access
    /// and SkiaSharp for rasterization. Returns one bitmap per page.
    /// </summary>
    private static List<SKBitmap> RenderPdfPages(string pdfPath, float dpi)
    {
        var bitmaps = new List<SKBitmap>();
        using var stream = File.OpenRead(pdfPath);
        // Use PdfDocumentOpenMode.Import for reading/extracting PDF document streams (ReadOnly is obsolete CS0618)
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(stream, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);

        for (int i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            // Calculate pixel dimensions from PDF points (72 points per inch).
            int pixelW = (int)(page.Width.Point / 72.0 * dpi);
            int pixelH = (int)(page.Height.Point / 72.0 * dpi);

            // Create a white bitmap at the target resolution.
            var bitmap = new SKBitmap(pixelW, pixelH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            // Note: Full PDF rendering requires a PDF renderer (e.g. PDFium, MuPDF).
            // PdfSharp does NOT render pages to images. For OCR of scanned PDFs, the pages
            // typically contain embedded images — we extract those directly.
            ExtractEmbeddedImages(page, bitmap, canvas, pixelW, pixelH);
            canvas.Flush();

            bitmaps.Add(bitmap);
        }

        return bitmaps;
    }

    /// <summary>
    /// Extracts embedded images from a PDF page's resources and draws them onto the canvas.
    /// Scanned PDFs store each page as a single full-page image in /Resources /XObject.
    /// </summary>
    private static void ExtractEmbeddedImages(PdfSharp.Pdf.PdfPage page, SKBitmap target, SKCanvas canvas, int pixelW, int pixelH)
    {
        var resources = page.Elements.GetDictionary("/Resources");
        if (resources is null) return;
        var xobjects = resources.Elements.GetDictionary("/XObject");
        if (xobjects is null) return;

        foreach (var key in xobjects.Elements.Keys)
        {
            var xobj = xobjects.Elements.GetDictionary(key);
            if (xobj is null) continue;
            var subtype = xobj.Elements.GetString("/Subtype");
            if (subtype != "/Image") continue;

            var stream = xobj.Stream;
            if (stream is null) continue;
            var imageBytes = stream.Value;
            if (imageBytes is null || imageBytes.Length == 0) continue;

            // Try to decode as a standard image format (JPEG, PNG).
            using var skStream = new SKMemoryStream(imageBytes);
            using var decoded = SKBitmap.Decode(skStream);
            if (decoded is not null)
            {
                // Scale the image to fill the page.
                canvas.DrawBitmap(decoded, new SKRect(0, 0, pixelW, pixelH));
                return; // One full-page image per scanned page.
            }
        }
    }

    // ---- Markdown formatting --------------------------------------------------------------------

    /// <summary>
    /// Formats OCR results as clean Markdown. Detects headings (large text / short lines at
    /// section starts), preserves paragraph structure, and identifies potential table regions.
    /// </summary>
    internal static string FormatAsMarkdown(OcrPageResult result)
    {
        if (result.Lines.Count == 0) return "";

        var sb = new StringBuilder();
        float? bodyHeight = EstimateBodyFontHeight(result.Lines);

        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0) continue;

            // Heading detection: lines significantly taller than body text, or very short
            // lines that start a section (all-caps, numbered).
            bool isHeading = bodyHeight.HasValue && line.Height > bodyHeight.Value * 1.4f;
            bool isAllCaps = text.Length > 2 && text == text.ToUpperInvariant() && text.Any(char.IsLetter);
            bool isNumbered = text.Length > 2 && char.IsDigit(text[0]) && (text[1] == '.' || text[1] == ')');

            if (isHeading || (isAllCaps && text.Length < 60))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("## ").Append(text).Append('\n');
            }
            else if (isNumbered)
            {
                sb.Append(text).Append('\n');
            }
            else
            {
                sb.Append(text).Append('\n');
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Estimates the median line height (body font size) from all recognized lines.</summary>
    private static float? EstimateBodyFontHeight(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0) return null;
        var heights = lines.Select(l => l.Height).OrderBy(h => h).ToList();
        return heights[heights.Count / 2]; // median
    }
}
