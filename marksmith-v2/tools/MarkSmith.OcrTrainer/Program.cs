using System.Collections.Concurrent;
using System.Diagnostics;
using MarkSmith.Ocr.MarkSmith;
using MarkSmith.OcrTrainer;

Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

// Usage: [lines=12000] [epochs=8] [out=MarkSmith.Core/Ocr/MarkSmith/glyphnet.bin] [words=/usr/share/dict/american-english]
int lines = args.Length > 0 && args[0] != "probe" ? int.Parse(args[0]) : 12000;
int epochs = args.Length > 1 ? int.Parse(args[1]) : 8;
string outPath = args.Length > 2 ? args[2] : Path.Combine("MarkSmith.Core", "Ocr", "MarkSmith", "glyphnet.bin");
string wordsPath = args.Length > 3 ? args[3] : "/usr/share/dict/american-english";

if (args.Length > 0 && args[0] == "probe")
{
    var w0 = File.ReadAllLines(wordsPath).Where(w => w.Length >= 2 && w.All(char.IsLetter) && w.All(c => c < 128)).ToArray();
    var g0 = new SampleGenerator(w0);
    for (int i = int.Parse(args[1]); i < int.Parse(args[1]) + int.Parse(args[2]); i++)
    {
        var t0 = Stopwatch.GetTimestamp();
        g0.Line(new Random(1000 + i));
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (ms > 500) Console.WriteLine($"line {i}: {ms:0} ms");
    }
    return;
}
var words = File.ReadAllLines(wordsPath).Where(w => w.Length >= 2 && w.All(char.IsLetter) && w.All(c => c < 128)).ToArray();
Console.WriteLine($"{words.Length} words, {lines} lines, {epochs} epochs → {outPath}");
Console.WriteLine($"{SampleGenerator.Families.Length} font families: {string.Join(", ", SampleGenerator.Families)}");

// ---- data ----
var sw = Stopwatch.StartNew();
var bag = new ConcurrentBag<List<Sample>>();
var gen = new SampleGenerator(words);
// One thread: Skia's text rendering and fontconfig contend badly across threads (a parallel run
// was many times slower than this, and once deadlocked). Training below is parallel.
Parallel.For(0, lines, new ParallelOptions { MaxDegreeOfParallelism = 1 }, i =>
{
    var rng = new Random(1000 + i);
    try { bag.Add(gen.Line(rng)); } catch (Exception ex) { Console.WriteLine($"line {i}: {ex.Message}"); }
});
var all = bag.SelectMany(s => s).ToList();
var shuffle = new Random(7);
all = all.OrderBy(_ => shuffle.Next()).ToList();
int valCount = Math.Max(1000, all.Count / 25);
var val = all.Take(valCount).ToList();
var train = all.Skip(valCount).ToList();
Console.WriteLine($"{all.Count} samples ({train.Count} train, {val.Count} validation) in {sw.Elapsed.TotalSeconds:0}s; touching skipped {gen.Touching}, unmatched {gen.Unmatched}");
var counts = new int[MsGlyph.Charset.Length];
foreach (var s in all) counts[s.Label]++;
Console.WriteLine("rarest: " + string.Join(" ", counts.Select((c, i) => (c, i)).OrderBy(t => t.c).Take(12).Select(t => $"'{MsGlyph.Charset[t.i]}'={t.c}")));

// ---- training ----
var net = new MsGlyphNet(MsGlyph.Charset, seed: 3);
var adamM = net.Parameters().Select(p => new float[p.Length]).ToArray();
var adamV = net.Parameters().Select(p => new float[p.Length]).ToArray();
int threads = Environment.ProcessorCount;
var grads = Enumerable.Range(0, threads).Select(_ => new MsGlyphNet.Gradients(net)).ToArray();
var acts = Enumerable.Range(0, threads).Select(_ => new MsGlyphNet.Activations(net)).ToArray();
var scratch = Enumerable.Range(0, threads).Select(_ => net.BackwardScratch()).ToArray();
var images = Enumerable.Range(0, threads).Select(_ => new float[MsGlyphNet.Size * MsGlyphNet.Size]).ToArray();
const int batch = 128;
double baseLr = 2e-3, beta1 = 0.9, beta2 = 0.999, eps = 1e-8, wd = 1e-5;
long step = 0;
int totalSteps = epochs * (train.Count / batch);

for (int epoch = 0; epoch < epochs; epoch++)
{
    var order = Enumerable.Range(0, train.Count).OrderBy(_ => shuffle.Next()).ToArray();
    double lossSum = 0; int lossN = 0;
    sw.Restart();
    for (int b0 = 0; b0 + batch <= order.Length; b0 += batch)
    {
        foreach (var g in grads) g.Clear();
        var losses = new double[threads];
        Parallel.For(0, threads, t =>
        {
            var rng = new Random((int)(step * 31 + t));
            for (int k = t; k < batch; k += threads)
            {
                var s = train[order[b0 + k]];
                Augment(s.Image, images[t], rng);
                net.Forward(images[t], s.Features, acts[t]);
                losses[t] += net.Backward(acts[t], s.Label, grads[t], scratch[t].Hidden, scratch[t].Flat, scratch[t].Conv);
            }
        });
        lossSum += losses.Sum(); lossN += batch;

        // Adam with cosine decay.
        step++;
        double lr = baseLr * 0.5 * (1 + Math.Cos(Math.PI * Math.Min(1.0, step / (double)totalSteps)));
        double b1t = 1 - Math.Pow(beta1, step), b2t = 1 - Math.Pow(beta2, step);
        var ps = net.Parameters().ToArray();
        var gs = grads.Select(g => g.All().ToArray()).ToArray();
        Parallel.For(0, ps.Length, pi =>
        {
            var p = ps[pi]; var m = adamM[pi]; var v = adamV[pi];
            for (int i = 0; i < p.Length; i++)
            {
                double gsum = 0;
                for (int t = 0; t < threads; t++) gsum += gs[t][pi][i];
                double g = gsum / batch + wd * p[i];
                m[i] = (float)(beta1 * m[i] + (1 - beta1) * g);
                v[i] = (float)(beta2 * v[i] + (1 - beta2) * g * g);
                p[i] -= (float)(lr * (m[i] / b1t) / (Math.Sqrt(v[i] / b2t) + eps));
            }
        });
        if (step % 500 == 0) Console.WriteLine($"  step {step}: loss {lossSum / lossN:0.000} lr {lr:0.00000}");
    }
    double acc = Evaluate(net, val, out var confusions);
    Console.WriteLine($"epoch {epoch + 1}: loss {lossSum / Math.Max(1, lossN):0.0000}, validation accuracy {acc:P2}, {sw.Elapsed.TotalSeconds:0}s");
    Console.WriteLine("  top confusions: " + string.Join(", ", confusions.Take(12)));
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    using (var f = File.Create(outPath)) net.Save(f);
}

static void Augment(byte[] src, float[] dst, Random rng)
{
    // Small shifts (±1 px) so the network doesn't rely on exact centring.
    int dx = rng.Next(-1, 2), dy = rng.Next(-1, 2);
    const int S = MsGlyphNet.Size;
    Array.Clear(dst);
    for (int y = 0; y < S; y++)
    {
        int sy = y - dy;
        if (sy < 0 || sy >= S) continue;
        for (int x = 0; x < S; x++)
        {
            int sx = x - dx;
            if (sx < 0 || sx >= S) continue;
            dst[y * S + x] = src[sy * S + sx] / 255f;
        }
    }
}

static double Evaluate(MsGlyphNet net, List<Sample> val, out List<string> confusions)
{
    int threads = Environment.ProcessorCount;
    var correct = new int[threads];
    var conf = new ConcurrentDictionary<string, int>();
    Parallel.For(0, threads, t =>
    {
        var a = new MsGlyphNet.Activations(net);
        var img = new float[MsGlyphNet.Size * MsGlyphNet.Size];
        for (int i = t; i < val.Count; i += threads)
        {
            var s = val[i];
            for (int k = 0; k < img.Length; k++) img[k] = s.Image[k] / 255f;
            var logits = net.Forward(img, s.Features, a);
            int best = 0;
            for (int c = 1; c < logits.Length; c++) if (logits[c] > logits[best]) best = c;
            if (best == s.Label) correct[t]++;
            else conf.AddOrUpdate($"{MsGlyph.Charset[s.Label]}→{MsGlyph.Charset[best]}", 1, (_, n) => n + 1);
        }
    });
    confusions = conf.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}").ToList();
    return correct.Sum() / (double)val.Count;
}
