using SkiaSharp;

namespace MarkSmith.Ocr.MarkSmith;

/// <summary>A page ready to read: straightened, at a workable size, thresholded and segmented.</summary>
public sealed class MsPage
{
    public required BinaryImage Binary { get; init; }
    public required List<TextLine> Lines { get; init; }
    /// <summary>How much the page was enlarged (1 = not at all); coordinates are in enlarged pixels.</summary>
    public double Scale { get; init; } = 1;
    public double Skew { get; init; }

    /// <summary>Letters are enlarged until a typical one is at least this tall: below ~24 px the
    /// strokes are too thin to threshold cleanly (150 dpi scans, 8 pt print).</summary>
    public const float MinTypicalHeight = 26;

    /// <summary>
    /// The one preparation path MarkSmith OCR uses, both when reading and when making training data:
    /// deskew, a first threshold to measure the print, enlarge small print, threshold again, segment.
    /// </summary>
    public static MsPage Prepare(SKBitmap image, bool deskew = true)
    {
        double skew = 0;
        var straight = deskew ? OcrPreprocess.Deskew(image, out skew) : image;
        try
        {
            var gray = OcrGeometry.ToGray(straight);
            var first = OcrPreprocess.Binarize(gray, straight.Width, straight.Height);
            float typical = MsSegmenter.TypicalHeight(MsSegmenter.Blobs(first));
            double scale = 1;
            var bin = first;
            if (typical > 0 && typical < MinTypicalHeight)
            {
                scale = Math.Min(3.0, MinTypicalHeight / typical);
                using var big = OcrGeometry.Resize(straight, (int)Math.Round(straight.Width * scale), (int)Math.Round(straight.Height * scale));
                bin = OcrPreprocess.Binarize(OcrGeometry.ToGray(big), big.Width, big.Height);
            }
            var lines = MsSegmenter.Lines(bin, out _);
            return new MsPage { Binary = bin, Lines = lines, Scale = scale, Skew = skew };
        }
        finally
        {
            if (!ReferenceEquals(straight, image)) straight.Dispose();
        }
    }
}
