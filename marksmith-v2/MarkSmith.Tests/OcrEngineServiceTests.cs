using System.Collections.Generic;
using System.Threading.Tasks;
using MarkSmith.Services;
using SkiaSharp;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Unit tests for OcrEngineService (D2) — preprocessing pipeline, Markdown formatting,
/// and provider integration via a mock OCR engine.
/// </summary>
public class OcrEngineServiceTests
{
    // ---- Mock provider for testing ---------------------------------------------------------------

    private sealed class MockOcrProvider : IOcrProvider
    {
        private readonly OcrPageResult _result;
        public MockOcrProvider(OcrPageResult result) => _result = result;
        public string EngineName => "MockOCR";
        public bool IsAvailable => true;
        public Task<OcrPageResult> RecognizeAsync(SKBitmap bitmap) => Task.FromResult(_result);
    }

    private static OcrPageResult MakeResult(params (string text, float height)[] lines)
    {
        var ocrLines = new List<OcrLine>();
        float y = 0;
        foreach (var (text, height) in lines)
        {
            var words = new List<OcrWord> { new(text, 0, y, text.Length * 8, height) };
            ocrLines.Add(new OcrLine(text, words, y, height));
            y += height + 4;
        }
        return new OcrPageResult(ocrLines, 800, y);
    }

    // ---- Markdown formatting ---------------------------------------------------------------------

    [Fact]
    public void FormatAsMarkdown_plain_lines_produces_paragraphs()
    {
        var result = MakeResult(("Hello world", 12f), ("Second line", 12f));
        var md = OcrEngineService.FormatAsMarkdown(result);

        Assert.Contains("Hello world", md);
        Assert.Contains("Second line", md);
        Assert.DoesNotContain("##", md); // no headings
    }

    [Fact]
    public void FormatAsMarkdown_detects_headings_by_height()
    {
        // First line is much taller → heading.
        var result = MakeResult(("CHAPTER 1", 24f), ("Body text here", 12f), ("More body", 12f));
        var md = OcrEngineService.FormatAsMarkdown(result);

        Assert.Contains("## CHAPTER 1", md);
        Assert.Contains("Body text here", md);
    }

    [Fact]
    public void FormatAsMarkdown_detects_allcaps_as_heading()
    {
        var result = MakeResult(("INTRODUCTION", 12f), ("Some normal text", 12f));
        var md = OcrEngineService.FormatAsMarkdown(result);

        Assert.Contains("## INTRODUCTION", md);
    }

    [Fact]
    public void FormatAsMarkdown_empty_result_returns_empty()
    {
        var result = new OcrPageResult(new List<OcrLine>(), 800, 600);
        Assert.Equal("", OcrEngineService.FormatAsMarkdown(result));
    }

    // ---- Image preprocessing ---------------------------------------------------------------------

    [Fact]
    public void PreprocessBitmap_converts_to_grayscale_binary()
    {
        // Create a simple 100x50 color bitmap.
        using var source = new SKBitmap(100, 50, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(new SKColor(200, 100, 50)); // orange-ish
        }

        using var processed = OcrEngineService.PreprocessBitmap(source);

        // After preprocessing, all pixels should be either black or white (binary).
        var pixel = processed.GetPixel(50, 25);
        Assert.True(pixel.Red == 0 || pixel.Red == 255,
            $"Expected binary pixel, got R={pixel.Red}");
        // Grayscale means R == G == B.
        Assert.Equal(pixel.Red, pixel.Green);
        Assert.Equal(pixel.Green, pixel.Blue);
    }

    [Fact]
    public void PreprocessBitmap_upscales_small_images()
    {
        using var tiny = new SKBitmap(100, 50, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(tiny))
            canvas.Clear(SKColors.White);

        using var processed = OcrEngineService.PreprocessBitmap(tiny);

        // Should be upscaled to at least 1000px wide.
        Assert.True(processed.Width >= 1000, $"Expected width >= 1000, got {processed.Width}");
    }

    [Fact]
    public void ComputeOtsuThreshold_bimodal_image()
    {
        // Create a bimodal image using Rgba8888 (SetPixel works reliably on this format).
        using var bmp = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
                bmp.SetPixel(x, y, x < 50 ? new SKColor(30, 30, 30) : new SKColor(220, 220, 220));

        var threshold = OcrEngineService.ComputeOtsuThreshold(bmp);

        // For a perfectly bimodal image (30 and 220), any threshold in [30,219] is optimal.
        // The algorithm picks the first maximum, which is at the lower mode boundary.
        Assert.InRange(threshold, (byte)30, (byte)219);
    }

    // ---- Service integration with mock -----------------------------------------------------------

    [Fact]
    public async Task ImageToMarkdown_uses_provider_and_formats()
    {
        var mockResult = MakeResult(("Test Title", 20f), ("Body paragraph", 12f));
        var provider = new MockOcrProvider(mockResult);
        var service = new OcrEngineService(provider);

        Assert.Equal("MockOCR", service.EngineName);
        Assert.True(service.IsAvailable);

        // Create a simple test image.
        using var bitmap = new SKBitmap(200, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.White);

        // Encode to PNG stream for the service.
        using var image = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new System.IO.MemoryStream();
        image.SaveTo(stream);
        stream.Position = 0;

        var md = await service.ImageToMarkdownAsync(stream);
        Assert.Contains("Test Title", md);
        Assert.Contains("Body paragraph", md);
    }

    [Fact]
    public void OcrPageResult_PlainText_joins_lines()
    {
        var result = MakeResult(("Line one", 12f), ("Line two", 12f));
        Assert.Equal("Line one\nLine two", result.PlainText);
    }
}
