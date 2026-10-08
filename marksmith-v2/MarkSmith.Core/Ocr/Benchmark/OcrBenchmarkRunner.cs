using MarkSmith.Services;

namespace MarkSmith.Ocr.Benchmark;

/// <summary>One engine's score on one benchmark page.</summary>
public sealed record OcrBenchmarkScore(string Engine, double Accuracy, long Milliseconds, string Text);

/// <summary>Runs an engine on a benchmark page the way the app reads a scan: straighten, then
/// recognise, then score against the page's text.</summary>
public static class OcrBenchmarkRunner
{
    public static OcrBenchmarkScore Run(IOcrProvider engine, SynthPage page)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var straight = OcrPreprocess.Deskew(page.Image, out _);
        try
        {
            var result = engine.RecognizeAsync(straight).GetAwaiter().GetResult();
            return new OcrBenchmarkScore(engine.EngineName, OcrAccuracy.CharacterAccuracy(page.Text, result.PlainText), sw.ElapsedMilliseconds, result.PlainText);
        }
        finally
        {
            if (!ReferenceEquals(straight, page.Image)) straight.Dispose();
        }
    }
}
