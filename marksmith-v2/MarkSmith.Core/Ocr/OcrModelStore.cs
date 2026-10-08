namespace MarkSmith.Ocr;

/// <summary>
/// Where the third-party OCR model files live: the <c>ocr-models</c> folder next to the app (the
/// build copies them there, see build/OcrModels.targets), or the folder named by the
/// <c>MARKSMITH_OCR_MODELS</c> environment variable.
/// </summary>
public static class OcrModelStore
{
    public const string PaddleDetection = "paddle-det.onnx";
    public const string PaddleRecognitionEnglish = "paddle-rec-en.onnx";
    public const string PaddleRecognitionLatin = "paddle-rec-latin.onnx";
    public const string TessdataFolder = "tessdata";

    /// <summary>Overrides the folder (tests and the CLI point it somewhere else).</summary>
    public static string? OverrideDirectory { get; set; }

    public static string Directory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OverrideDirectory)) return OverrideDirectory!;
            var env = Environment.GetEnvironmentVariable("MARKSMITH_OCR_MODELS");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(AppContext.BaseDirectory, "ocr-models");
        }
    }

    public static string PathOf(string file) => Path.Combine(Directory, file);

    public static bool Has(string file) => File.Exists(PathOf(file));
}
