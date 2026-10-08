using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Ocr;
using MarkSmith.Ocr.Benchmark;
using MarkSmith.Ocr.MarkSmith;
using MarkSmith.Services;
using Xunit;
using Xunit.Abstractions;

namespace MarkSmith.Core.Tests.Ocr;

/// <summary>
/// The OCR benchmark: 20 pages (OcrBenchmarkCorpus) read by MarkSmith OCR and by every other engine
/// that can run on this machine: PaddleOCR PP-OCRv5 and Tesseract 5 (bundled models), and Windows
/// OCR when the tests run inside the Windows app's process. On each page MarkSmith OCR must reach
/// at least 90% of the best engine's character accuracy. With no other engine installed it must
/// reach 90% accuracy outright.
/// </summary>
public class OcrBenchmarkTests
{
    private readonly ITestOutputHelper _out;
    public OcrBenchmarkTests(ITestOutputHelper output) => _out = output;

    private static readonly Lazy<List<IOcrProvider>> Others = new(() =>
    {
        var list = new List<IOcrProvider>();
        foreach (var e in new IOcrProvider?[] { new PaddleOcrProvider(), new TesseractOcrProvider(), SafeWindows() })
            if (e is not null && e.IsAvailable) list.Add(e);
        return list;
    });

    private static IOcrProvider? SafeWindows()
    {
        try { return OcrEngines.WindowsFactory?.Invoke(); } catch { return null; }
    }

    public static IEnumerable<object[]> Cases() => OcrBenchmarkCorpus.Cases.Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(Cases))]
    public void MarkSmith_OCR_reads_at_least_90_percent_as_well_as_the_best_engine(string id) =>
        Compare(OcrBenchmarkCorpus.Cases.Single(x => x.Id == id));

    private void Compare(OcrBenchmarkCase c)
    {
        using var page = c.Make();

        var ours = new MarkSmithOcrProvider();
        Assert.True(ours.IsAvailable, "MarkSmith OCR's network is missing from this build.");
        var mine = OcrBenchmarkRunner.Run(ours, page);
        var others = Others.Value.Select(e => OcrBenchmarkRunner.Run(e, page)).ToList();

        _out.WriteLine($"{c.Id}: {c.Description}");
        foreach (var s in others.Prepend(mine))
            _out.WriteLine($"  {s.Engine,-22} {s.Accuracy,8:P2}  {s.Milliseconds,6} ms");
        _out.WriteLine("  MarkSmith OCR read:\n    " + mine.Text.Replace("\n", "\n    "));

        double best = others.Count == 0 ? 1.0 : others.Max(o => o.Accuracy);
        double ratio = best <= 0 ? 1 : mine.Accuracy / best;
        Assert.True(ratio >= 0.9,
            $"{c.Id}: MarkSmith OCR {mine.Accuracy:P2} is {ratio:P1} of the best ({best:P2}); needs at least 90%.");
    }
}
