using System.Runtime.InteropServices;
using MarkSmith.Services;
using SkiaSharp;

namespace MarkSmith.Ocr;

/// <summary>
/// Tesseract 5 (Apache-2.0) through its C API, in-process. The native library is the one the
/// "Tesseract" NuGet package ships on Windows (tesseract50.dll), or the system libtesseract on
/// Linux and macOS. Language data comes from ocr-models/tessdata (tessdata_best English), or the
/// system's tessdata when that folder is missing.
/// </summary>
public sealed class TesseractOcrProvider : IOcrProvider
{
    private const string Lib = "tesseract";
    private static readonly object Gate = new();
    private static bool _resolverSet;

    public TesseractOcrProvider(string language = "eng") => Language = language;

    public string Language { get; }

    public string EngineName => "Tesseract 5";

    public bool IsAvailable
    {
        get
        {
            try
            {
                EnsureResolver();
                return TessdataPath() is not null && NativeLibrary.TryLoad(Lib, typeof(TesseractOcrProvider).Assembly, null, out _);
            }
            catch { return false; }
        }
    }

    public Task<OcrPageResult> RecognizeAsync(SKBitmap bitmap) => Task.Run(() => Recognize(bitmap));

    public OcrPageResult Recognize(SKBitmap bitmap)
    {
        EnsureResolver();
        var tessdata = TessdataPath() ?? throw new InvalidOperationException("Tesseract's language data isn't installed (ocr-models/tessdata).");
        var gray = OcrGeometry.ToGray(bitmap);
        IntPtr api = TessBaseAPICreate();
        try
        {
            if (TessBaseAPIInit3(api, tessdata, Language) != 0)
                throw new InvalidOperationException($"Tesseract couldn't load the \"{Language}\" language data.");
            // PSM 3: fully automatic page segmentation (Tesseract's default for documents).
            TessBaseAPISetPageSegMode(api, 3);
            TessBaseAPISetImage(api, gray, bitmap.Width, bitmap.Height, 1, bitmap.Width);
            TessBaseAPISetSourceResolution(api, 300);
            IntPtr tsvPtr = TessBaseAPIGetTsvText(api, 0);
            string tsv = Marshal.PtrToStringUTF8(tsvPtr) ?? "";
            TessDeleteText(tsvPtr);
            return FromTsv(tsv, bitmap.Width, bitmap.Height);
        }
        finally
        {
            TessBaseAPIEnd(api);
            TessBaseAPIDelete(api);
        }
    }

    /// <summary>Tesseract's TSV (level, page, block, paragraph, line, word, left, top, width,
    /// height, conf, text) as lines in Tesseract's own reading order.</summary>
    public static OcrPageResult FromTsv(string tsv, float width, float height)
    {
        var groups = new Dictionary<(int, int, int), List<OcrWord>>();
        var order = new List<(int, int, int)>();
        foreach (var row in tsv.Split('\n'))
        {
            var f = row.Split('\t');
            if (f.Length < 12 || f[0] != "5") continue;
            var text = f[11].Trim();
            if (text.Length == 0) continue;
            var key = (int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]));
            if (!groups.TryGetValue(key, out var words)) { groups[key] = words = new List<OcrWord>(); order.Add(key); }
            words.Add(new OcrWord(text, float.Parse(f[6]), float.Parse(f[7]), float.Parse(f[8]), float.Parse(f[9])));
        }
        var lines = order.Select(k =>
        {
            var w = groups[k];
            float top = w.Min(x => x.Y), bottom = w.Max(x => x.Y + x.Height);
            return new OcrLine(string.Join(" ", w.Select(x => x.Text)), w, top, bottom - top);
        }).ToList();
        return new OcrPageResult(lines, width, height);
    }

    private static string? TessdataPath()
    {
        var bundled = OcrModelStore.PathOf(OcrModelStore.TessdataFolder);
        if (File.Exists(Path.Combine(bundled, "eng.traineddata"))) return bundled;
        foreach (var dir in new[] { "/usr/share/tesseract-ocr/5/tessdata", "/usr/share/tesseract-ocr/4.00/tessdata", "/usr/share/tessdata", "/opt/homebrew/share/tessdata", "/usr/local/share/tessdata" })
            if (File.Exists(Path.Combine(dir, "eng.traineddata"))) return dir;
        return null;
    }

    private static void EnsureResolver()
    {
        lock (Gate)
        {
            if (_resolverSet) return;
            _resolverSet = true;
            NativeLibrary.SetDllImportResolver(typeof(TesseractOcrProvider).Assembly, (name, asm, path) =>
            {
                if (name != Lib) return IntPtr.Zero;
                string arch = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? "x86" : "x64";
                var candidates = OperatingSystem.IsWindows()
                    ? new[] { Path.Combine(AppContext.BaseDirectory, arch, "tesseract50.dll"), Path.Combine(AppContext.BaseDirectory, "tesseract50.dll"), "tesseract50", "libtesseract-5" }
                    : OperatingSystem.IsMacOS()
                        ? new[] { "libtesseract.5.dylib", "/opt/homebrew/lib/libtesseract.dylib", "/usr/local/lib/libtesseract.dylib" }
                        : new[] { "libtesseract.so.5", "libtesseract.so" };
                foreach (var c in candidates)
                    if (NativeLibrary.TryLoad(c, out var handle)) return handle;
                return IntPtr.Zero;
            });
        }
    }

    [DllImport(Lib)] private static extern IntPtr TessBaseAPICreate();
    [DllImport(Lib)] private static extern void TessBaseAPIDelete(IntPtr handle);
    [DllImport(Lib)] private static extern void TessBaseAPIEnd(IntPtr handle);
    [DllImport(Lib, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int TessBaseAPIInit3(IntPtr handle, string datapath, string language);
    [DllImport(Lib)] private static extern void TessBaseAPISetPageSegMode(IntPtr handle, int mode);
    [DllImport(Lib)] private static extern void TessBaseAPISetImage(IntPtr handle, byte[] imagedata, int width, int height, int bytesPerPixel, int bytesPerLine);
    [DllImport(Lib)] private static extern void TessBaseAPISetSourceResolution(IntPtr handle, int ppi);
    [DllImport(Lib)] private static extern IntPtr TessBaseAPIGetTsvText(IntPtr handle, int pageNumber);
    [DllImport(Lib)] private static extern void TessDeleteText(IntPtr text);
}
