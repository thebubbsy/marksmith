using MarkSmith.Ocr.MarkSmith;
using MarkSmith.Services;

namespace MarkSmith.Ocr;

/// <summary>An OCR engine the user can pick, and whether it can run here.</summary>
public sealed record OcrEngineInfo(string Id, string Name, string Description, bool IsAvailable, string? Unavailable);

/// <summary>
/// The OCR engines MarkSmith can use and the one Settings › Import picks:
/// MarkSmith OCR (ours, always built in), PaddleOCR PP-OCRv5 (bundled models), Windows' own OCR
/// (Windows 10/11 language packs) and Tesseract 5 (bundled data and library on Windows).
/// </summary>
public static class OcrEngines
{
    public const string Auto = "auto", MarkSmithId = "marksmith", Paddle = "paddle", Windows = "windows", Tesseract = "tesseract";

    /// <summary>Set by the desktop app: Windows.Media.Ocr only exists there.</summary>
    public static Func<IOcrProvider?>? WindowsFactory { get; set; }

    // One instance per engine for the app's lifetime: PaddleOCR loads ~13 MB of models into ONNX
    // Runtime sessions, which shouldn't happen again on every import.
    private static readonly Lazy<PaddleOcrProvider> PaddleEnglish = new(() => new PaddleOcrProvider(latin: false));
    private static readonly Lazy<PaddleOcrProvider> PaddleLatin = new(() => new PaddleOcrProvider(latin: true));
    private static readonly Lazy<TesseractOcrProvider> TesseractEngine = new(() => new TesseractOcrProvider());
    private static readonly Lazy<MarkSmithOcrProvider> Ours = new(() => new MarkSmithOcrProvider());
    private static readonly Lazy<IOcrProvider?> WindowsEngine = new(SafeWindows);

    private static PaddleOcrProvider PaddleFor() => IsEnglish() ? PaddleEnglish.Value : PaddleLatin.Value;

    public static IReadOnlyList<OcrEngineInfo> List()
    {
        var paddle = PaddleFor();
        var tess = TesseractEngine.Value;
        var win = WindowsEngine.Value;
        var ours = Ours.Value;
        return new[]
        {
            new OcrEngineInfo(Auto, "Automatic", "The most accurate engine available: PaddleOCR when installed, otherwise MarkSmith OCR.", true, null),
            new OcrEngineInfo(MarkSmithId, "MarkSmith OCR", "Built by MarkSmith from the ground up in C#. Always available, fully offline.", ours.IsAvailable, ours.IsAvailable ? null : "Its network is missing from this build."),
            new OcrEngineInfo(Paddle, "PaddleOCR (PP-OCRv5)", "Open-source deep-learning OCR, the most accurate on printed documents. Bundled, offline.", paddle.IsAvailable, paddle.IsAvailable ? null : "Its model files aren't installed (ocr-models)."),
            new OcrEngineInfo(Windows, "Windows OCR", "The OCR built into Windows 10 and 11, using your installed languages.", win?.IsAvailable == true,
                win is null ? "Only available in the Windows app."
                : win.IsAvailable ? null
                : "Windows has no OCR language installed. Add one under Settings > Time & language > Language & region (Optional features: Optical character recognition)."),
            new OcrEngineInfo(Tesseract, "Tesseract 5", "The classic open-source OCR engine (LSTM, best English data). Bundled, offline.", tess.IsAvailable, tess.IsAvailable ? null : "Its library or language data isn't installed."),
        };
    }

    /// <summary>The engine for an id (a Settings value), falling back to MarkSmith OCR when the
    /// chosen one can't run here; <paramref name="fellBack"/> says so.</summary>
    public static IOcrProvider Create(string? id, out bool fellBack)
    {
        fellBack = false;
        IOcrProvider? chosen = (id ?? Auto).ToLowerInvariant() switch
        {
            MarkSmithId => Ours.Value,
            Paddle => PaddleFor(),
            Tesseract => TesseractEngine.Value,
            Windows => WindowsEngine.Value,
            _ => null,
        };
        if (chosen is null && (id ?? Auto).Equals(Auto, StringComparison.OrdinalIgnoreCase))
        {
            var paddle = PaddleFor();
            return paddle.IsAvailable ? paddle : Ours.Value;
        }
        if (chosen is null || !chosen.IsAvailable)
        {
            fellBack = true;
            return Ours.Value;
        }
        return chosen;
    }

    public static IOcrProvider Create(string? id) => Create(id, out _);

    private static IOcrProvider? SafeWindows()
    {
        try { return WindowsFactory?.Invoke(); } catch { return null; }
    }

    private static bool IsEnglish()
    {
        var lang = AppServices.Settings.Current.ContentLanguage;
        return string.IsNullOrWhiteSpace(lang) || lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    }
}
