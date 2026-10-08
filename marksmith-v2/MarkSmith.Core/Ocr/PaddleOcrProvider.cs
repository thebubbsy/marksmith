using MarkSmith.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace MarkSmith.Ocr;

/// <summary>
/// PaddleOCR PP-OCRv5 (Apache-2.0), run in-process through ONNX Runtime: a DB text-line detector,
/// then a CTC recogniser on each detected line. Pre- and post-processing follow the official
/// pipeline (inference.yml): BGR input, ImageNet normalisation for detection, a 0.3 probability
/// threshold, a 0.6 box score, unclip ratio 1.5; recognition at height 48, (x/255 − 0.5)/0.5.
/// </summary>
public sealed class PaddleOcrProvider : IOcrProvider, IDisposable
{
    private readonly Lazy<(InferenceSession Det, InferenceSession Rec, string[] Dict)?> _models;
    private readonly string _recModel;

    /// <param name="latin">The Latin recogniser (accented European letters) instead of English.</param>
    public PaddleOcrProvider(bool latin = false)
    {
        _recModel = latin ? OcrModelStore.PaddleRecognitionLatin : OcrModelStore.PaddleRecognitionEnglish;
        _models = new Lazy<(InferenceSession, InferenceSession, string[])?>(Load, isThreadSafe: true);
    }

    public string EngineName => "PaddleOCR (PP-OCRv5)";

    public bool IsAvailable => OcrModelStore.Has(OcrModelStore.PaddleDetection) && OcrModelStore.Has(_recModel);

    /// <summary>Longest side the detector sees. PaddleOCR's default (960) loses small print on a
    /// full A4 page; 2048 keeps 9 pt text at 300 dpi.</summary>
    public int DetectionMaxSide { get; init; } = 2048;

    private (InferenceSession, InferenceSession, string[])? Load()
    {
        if (!IsAvailable) return null;
        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        opts.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount);
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        var det = new InferenceSession(OcrModelStore.PathOf(OcrModelStore.PaddleDetection), opts);
        var rec = new InferenceSession(OcrModelStore.PathOf(_recModel), opts);
        var dict = ReadDictionary(OcrModelStore.PathOf(Path.ChangeExtension(_recModel, ".yml")));
        return (det, rec, dict);
    }

    public Task<OcrPageResult> RecognizeAsync(SKBitmap bitmap) => Task.Run(() => Recognize(bitmap));

    public OcrPageResult Recognize(SKBitmap bitmap)
    {
        var models = _models.Value ?? throw new InvalidOperationException("PaddleOCR's model files aren't installed (ocr-models folder).");
        using var bgra = OcrGeometry.ToBgra(bitmap);
        var boxes = Detect(models.Item1, bgra);
        var lines = new List<(RotatedBox Box, string Text, float Score)>();
        // Recognise in batches of similar width (less padding, same as PaddleOCR's sort by ratio).
        var order = boxes.OrderBy(b => b.Width / Math.Max(1f, b.Height)).ToList();
        const int batch = 8;
        for (int i = 0; i < order.Count; i += batch)
        {
            var group = order.Skip(i).Take(batch).ToList();
            var crops = group.Select(b => OcrGeometry.CropRotated(bgra, b)).ToList();
            try
            {
                var texts = RecognizeLines(models.Item2, models.Item3, crops);
                for (int k = 0; k < group.Count; k++)
                    if (!string.IsNullOrWhiteSpace(texts[k].Text)) lines.Add((group[k], texts[k].Text, texts[k].Score));
            }
            finally { foreach (var c in crops) c.Dispose(); }
        }
        return OcrLayout.ToPageResult(lines.Select(l => (l.Box.Bounds, l.Text)).ToList(), bitmap.Width, bitmap.Height);
    }

    // ---- detection ----

    private List<RotatedBox> Detect(InferenceSession det, SKBitmap img)
    {
        int w = img.Width, h = img.Height;
        double scale = Math.Min(1.0, DetectionMaxSide / (double)Math.Max(w, h));
        if (Math.Min(w, h) * scale < 64) scale = 64.0 / Math.Min(w, h);
        int rw = Math.Max(32, (int)Math.Round(w * scale / 32) * 32), rh = Math.Max(32, (int)Math.Round(h * scale / 32) * 32);
        using var resized = OcrGeometry.Resize(img, rw, rh);

        var input = new DenseTensor<float>(new[] { 1, 3, rh, rw });
        var px = resized.GetPixelSpan();
        float[] mean = { 0.485f, 0.456f, 0.406f }, std = { 0.229f, 0.224f, 0.225f };
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int p = (y * rw + x) * 4;
                // BGR order, as PaddleOCR decodes; the normalisation constants apply per channel index.
                input[0, 0, y, x] = (px[p] / 255f - mean[0]) / std[0];
                input[0, 1, y, x] = (px[p + 1] / 255f - mean[1]) / std[1];
                input[0, 2, y, x] = (px[p + 2] / 255f - mean[2]) / std[2];
            }

        using var results = det.Run(new[] { NamedOnnxValue.CreateFromTensor(det.InputMetadata.Keys.First(), input) });
        var prob = results.First().AsTensor<float>();
        int ph = prob.Dimensions[2], pw = prob.Dimensions[3];
        var probs = new float[ph * pw];
        var mask = new bool[ph * pw];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                float v = prob[0, 0, y, x];
                probs[y * pw + x] = v;
                mask[y * pw + x] = v > 0.3f;
            }

        var boxes = new List<RotatedBox>();
        double sx = w / (double)pw, sy = h / (double)ph;
        foreach (var region in OcrGeometry.ConnectedComponents(mask, pw, ph, minPixels: 4))
        {
            double score = region.Average(i => probs[i]);
            if (score < 0.6) continue;
            var pts = region.Select(i => new SKPoint(i % pw, i / pw)).ToList();
            var r = OcrGeometry.MinAreaRect(pts);
            if (Math.Min(r.Width, r.Height) < 2) continue;
            // Unclip: grow by area × ratio / perimeter on every side (the DB paper's offset).
            double area = (r.Width + 1) * (r.Height + 1), perim = 2 * (r.Width + 1 + r.Height + 1);
            float d = (float)(area * 1.5 / perim);
            var grown = new RotatedBox(r.CenterX, r.CenterY, r.Width + 1 + 2 * d, r.Height + 1 + 2 * d, r.Angle);
            var mapped = new RotatedBox((float)(grown.CenterX * sx), (float)(grown.CenterY * sy), (float)(grown.Width * sx), (float)(grown.Height * sy), grown.Angle);
            if (mapped.Height < 4) continue;
            boxes.Add(mapped);
        }
        return boxes;
    }

    // ---- recognition ----

    private static List<(string Text, float Score)> RecognizeLines(InferenceSession rec, string[] dict, List<SKBitmap> crops)
    {
        const int H = 48;
        var widths = crops.Select(c => Math.Clamp((int)Math.Ceiling(H * c.Width / (double)Math.Max(1, c.Height)), 16, 3200)).ToList();
        int maxW = Math.Max(320, widths.Max());
        var input = new DenseTensor<float>(new[] { crops.Count, 3, H, maxW });
        for (int n = 0; n < crops.Count; n++)
        {
            using var r = OcrGeometry.Resize(crops[n], widths[n], H);
            var px = r.GetPixelSpan();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < widths[n]; x++)
                {
                    int p = (y * widths[n] + x) * 4;
                    input[n, 0, y, x] = (px[p] / 255f - 0.5f) / 0.5f;
                    input[n, 1, y, x] = (px[p + 1] / 255f - 0.5f) / 0.5f;
                    input[n, 2, y, x] = (px[p + 2] / 255f - 0.5f) / 0.5f;
                }
            // Padding stays 0, i.e. mid grey after normalisation, as PaddleOCR pads.
        }
        using var results = rec.Run(new[] { NamedOnnxValue.CreateFromTensor(rec.InputMetadata.Keys.First(), input) });
        var outp = results.First().AsTensor<float>();
        int T = outp.Dimensions[1], C = outp.Dimensions[2];
        bool hasSpace = C == dict.Length + 2;
        var texts = new List<(string, float)>();
        for (int n = 0; n < crops.Count; n++)
        {
            var sb = new System.Text.StringBuilder();
            int prev = -1;
            double conf = 0; int count = 0;
            for (int t = 0; t < T; t++)
            {
                int best = 0; float bestV = float.MinValue;
                for (int c = 0; c < C; c++)
                {
                    float v = outp[n, t, c];
                    if (v > bestV) { bestV = v; best = c; }
                }
                if (best != 0 && best != prev)
                {
                    int idx = best - 1;
                    if (idx < dict.Length) sb.Append(dict[idx]);
                    else if (hasSpace) sb.Append(' ');
                    conf += bestV; count++;
                }
                prev = best;
            }
            texts.Add((sb.ToString().Trim(), count == 0 ? 0 : (float)(conf / count)));
        }
        return texts;
    }

    /// <summary>The recogniser's character list from its inference.yml (PostProcess.character_dict).</summary>
    public static string[] ReadDictionary(string ymlPath)
    {
        var chars = new List<string>();
        bool inDict = false;
        foreach (var raw in File.ReadLines(ymlPath))
        {
            if (!inDict)
            {
                if (raw.Trim() == "character_dict:") inDict = true;
                continue;
            }
            var t = raw.TrimStart();
            if (!t.StartsWith("- ")) break;
            var v = t[2..];
            if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') v = v[1..^1].Replace("''", "'");
            else if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = System.Text.RegularExpressions.Regex.Unescape(v[1..^1]);
            chars.Add(v);
        }
        return chars.ToArray();
    }

    public void Dispose()
    {
        if (_models.IsValueCreated && _models.Value is { } m) { m.Item1.Dispose(); m.Item2.Dispose(); }
    }
}
