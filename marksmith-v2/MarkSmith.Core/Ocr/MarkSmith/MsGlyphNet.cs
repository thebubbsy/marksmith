using System.Numerics;
using System.Runtime.CompilerServices;

namespace MarkSmith.Ocr.MarkSmith;

/// <summary>
/// MarkSmith OCR's letter recogniser: a small convolutional network, written from scratch with
/// no ML library, that reads one 32×32 letter image plus a few measurements of where the letter
/// sits on its line (which is what tells o from O, c from C and a comma from an apostrophe).
///
/// conv3×3 1→16, ReLU, max-pool 2 → conv3×3 16→32, ReLU, pool → conv3×3 32→64, ReLU, pool
/// → 1024 values + 8 measurements → dense 256, ReLU → dense (one score per character).
///
/// The same code trains it (tools/MarkSmith.OcrTrainer) and runs it in the app, so the two can't
/// drift apart.
/// </summary>
public sealed class MsGlyphNet
{
    public const int Size = 32;
    public const int FeatureCount = 8;
    private static readonly int[] Channels = { 1, 16, 32, 64 };
    public const int Hidden = 256;

    public string[] Classes { get; }
    public int ClassCount => Classes.Length;

    // Parameters: conv weights [oc][ic][3][3] and biases, dense weights [out][in] and biases.
    public readonly float[][] ConvW = new float[3][];
    public readonly float[][] ConvB = new float[3][];
    public float[] Fc1W, Fc1B, Fc2W, Fc2B;

    public int FlatCount => Channels[3] * (Size / 8) * (Size / 8);
    public int Fc1In => FlatCount + FeatureCount;

    public MsGlyphNet(string[] classes, int seed = 1)
    {
        Classes = classes;
        var rng = new Random(seed);
        for (int l = 0; l < 3; l++)
        {
            int fanIn = Channels[l] * 9;
            ConvW[l] = He(rng, Channels[l + 1] * Channels[l] * 9, fanIn);
            ConvB[l] = new float[Channels[l + 1]];
        }
        Fc1W = He(rng, Hidden * Fc1In, Fc1In);
        Fc1B = new float[Hidden];
        Fc2W = He(rng, ClassCount * Hidden, Hidden);
        Fc2B = new float[ClassCount];
    }

    private static float[] He(Random rng, int count, int fanIn)
    {
        var w = new float[count];
        double sd = Math.Sqrt(2.0 / fanIn);
        for (int i = 0; i < count; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            w[i] = (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * sd);
        }
        return w;
    }

    /// <summary>Everything one forward pass keeps, so the trainer can run it backwards.</summary>
    public sealed class Activations
    {
        public readonly float[][] ConvIn = new float[3][];   // input to each conv (after previous pool)
        public readonly float[][] ConvOut = new float[3][];  // after ReLU, before pool
        public readonly int[][] PoolArg = new int[3][];      // which input won each pool cell
        public readonly float[] Fc1In;
        public readonly float[] Hidden;
        public readonly float[] Logits;

        public Activations(MsGlyphNet net)
        {
            int s = Size;
            for (int l = 0; l < 3; l++)
            {
                ConvIn[l] = new float[Channels[l] * s * s];
                ConvOut[l] = new float[Channels[l + 1] * s * s];
                s /= 2;
                PoolArg[l] = new int[Channels[l + 1] * s * s];
            }
            Fc1In = new float[net.Fc1In];
            Hidden = new float[MsGlyphNet.Hidden];
            Logits = new float[net.ClassCount];
        }
    }

    /// <summary>Scores for one letter. <paramref name="image"/> is Size×Size, ink = 1.</summary>
    public float[] Forward(ReadOnlySpan<float> image, ReadOnlySpan<float> features, Activations a)
    {
        image.CopyTo(a.ConvIn[0]);
        int s = Size;
        for (int l = 0; l < 3; l++)
        {
            Conv3x3(a.ConvIn[l], Channels[l], s, ConvW[l], ConvB[l], Channels[l + 1], a.ConvOut[l]);
            var next = l < 2 ? a.ConvIn[l + 1] : a.Fc1In;
            MaxPool(a.ConvOut[l], Channels[l + 1], s, next, a.PoolArg[l]);
            s /= 2;
        }
        features.CopyTo(a.Fc1In.AsSpan(FlatCount));
        Dense(a.Fc1In, Fc1W, Fc1B, a.Hidden, relu: true);
        Dense(a.Hidden, Fc2W, Fc2B, a.Logits, relu: false);
        return a.Logits;
    }

    public static float[] Softmax(ReadOnlySpan<float> logits)
    {
        var p = new float[logits.Length];
        float max = float.MinValue;
        foreach (var v in logits) max = Math.Max(max, v);
        double sum = 0;
        for (int i = 0; i < p.Length; i++) { p[i] = MathF.Exp(logits[i] - max); sum += p[i]; }
        for (int i = 0; i < p.Length; i++) p[i] = (float)(p[i] / sum);
        return p;
    }

    // ---- layers ----

    private static void Conv3x3(float[] input, int inC, int s, float[] w, float[] b, int outC, float[] output)
    {
        int plane = s * s;
        for (int oc = 0; oc < outC; oc++)
        {
            var o = output.AsSpan(oc * plane, plane);
            o.Fill(b[oc]);
            for (int ic = 0; ic < inC; ic++)
            {
                var src = input.AsSpan(ic * plane, plane);
                int wBase = (oc * inC + ic) * 9;
                for (int ky = 0; ky < 3; ky++)
                    for (int kx = 0; kx < 3; kx++)
                    {
                        float wv = w[wBase + ky * 3 + kx];
                        if (wv == 0) continue;
                        int dy = ky - 1, dx = kx - 1;
                        int x0 = Math.Max(0, -dx), x1 = Math.Min(s, s - dx);
                        for (int y = Math.Max(0, -dy); y < Math.Min(s, s - dy); y++)
                            Axpy(wv, src.Slice((y + dy) * s + x0 + dx, x1 - x0), o.Slice(y * s + x0, x1 - x0));
                    }
            }
            for (int i = 0; i < plane; i++) if (o[i] < 0) o[i] = 0;
        }
    }

    private static void MaxPool(float[] input, int c, int s, float[] output, int[] arg)
    {
        int h = s / 2, plane = s * s;
        for (int ch = 0; ch < c; ch++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < h; x++)
                {
                    int i0 = ch * plane + (2 * y) * s + 2 * x;
                    int best = i0;
                    if (input[i0 + 1] > input[best]) best = i0 + 1;
                    if (input[i0 + s] > input[best]) best = i0 + s;
                    if (input[i0 + s + 1] > input[best]) best = i0 + s + 1;
                    int o = ch * h * h + y * h + x;
                    output[o] = input[best];
                    arg[o] = best;
                }
    }

    private static void Dense(float[] input, float[] w, float[] b, float[] output, bool relu)
    {
        int n = input.Length;
        for (int o = 0; o < output.Length; o++)
        {
            float v = b[o] + Dot(w.AsSpan(o * n, n), input);
            output[o] = relu && v < 0 ? 0 : v;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Axpy(float a, ReadOnlySpan<float> x, Span<float> y)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            var va = new Vector<float>(a);
            for (; i <= x.Length - Vector<float>.Count; i += Vector<float>.Count)
                (new Vector<float>(y.Slice(i)) + va * new Vector<float>(x.Slice(i))).CopyTo(y.Slice(i));
        }
        for (; i < x.Length; i++) y[i] += a * x[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0;
        float sum = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
                acc += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
            sum = Vector.Dot(acc, Vector<float>.One);
        }
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    // ---- training: gradients for one sample, added into g ----

    /// <summary>Gradient buffers shaped like the parameters.</summary>
    public sealed class Gradients
    {
        public readonly float[][] ConvW = new float[3][];
        public readonly float[][] ConvB = new float[3][];
        public readonly float[] Fc1W, Fc1B, Fc2W, Fc2B;
        public Gradients(MsGlyphNet n)
        {
            for (int l = 0; l < 3; l++) { ConvW[l] = new float[n.ConvW[l].Length]; ConvB[l] = new float[n.ConvB[l].Length]; }
            Fc1W = new float[n.Fc1W.Length]; Fc1B = new float[n.Fc1B.Length];
            Fc2W = new float[n.Fc2W.Length]; Fc2B = new float[n.Fc2B.Length];
        }
        public IEnumerable<float[]> All() => ConvW.Concat(ConvB).Append(Fc1W).Append(Fc1B).Append(Fc2W).Append(Fc2B);
        public void Clear() { foreach (var a in All()) Array.Clear(a); }
    }

    public IEnumerable<float[]> Parameters() => ConvW.Concat(ConvB).Append(Fc1W).Append(Fc1B).Append(Fc2W).Append(Fc2B);

    /// <summary>Cross-entropy loss for one sample (forward already run into <paramref name="a"/>);
    /// adds its gradients into <paramref name="g"/>.</summary>
    public float Backward(Activations a, int label, Gradients g, float[] scratchHidden, float[] scratchFlat, float[][] scratchConv)
    {
        var p = Softmax(a.Logits);
        float loss = -MathF.Log(Math.Max(1e-7f, p[label]));
        var dLogits = p;
        dLogits[label] -= 1;

        // Dense 2.
        Array.Clear(scratchHidden);
        for (int o = 0; o < ClassCount; o++)
        {
            float d = dLogits[o];
            if (d == 0) continue;
            g.Fc2B[o] += d;
            Axpy(d, a.Hidden, g.Fc2W.AsSpan(o * Hidden, Hidden));
            Axpy(d, Fc2W.AsSpan(o * Hidden, Hidden), scratchHidden);
        }
        for (int i = 0; i < Hidden; i++) if (a.Hidden[i] <= 0) scratchHidden[i] = 0;

        // Dense 1.
        int n = Fc1In;
        Array.Clear(scratchFlat);
        for (int o = 0; o < Hidden; o++)
        {
            float d = scratchHidden[o];
            if (d == 0) continue;
            g.Fc1B[o] += d;
            Axpy(d, a.Fc1In, g.Fc1W.AsSpan(o * n, n));
            Axpy(d, Fc1W.AsSpan(o * n, n), scratchFlat);
        }

        // Conv stack, last to first. scratchConv[l] = gradient w.r.t. ConvOut[l].
        var dPooled = scratchFlat; // first FlatCount entries are the last pool's output gradient
        int s = Size / 4; // size of ConvOut[2]
        for (int l = 2; l >= 0; l--)
        {
            int outC = Channels[l + 1], inC = Channels[l];
            var dOut = scratchConv[l];
            Array.Clear(dOut);
            // Unpool into the winning positions, then the ReLU gate.
            var arg = a.PoolArg[l];
            for (int i = 0; i < arg.Length; i++) dOut[arg[i]] += dPooled[i];
            var outAct = a.ConvOut[l];
            for (int i = 0; i < dOut.Length; i++) if (outAct[i] <= 0) dOut[i] = 0;

            var dIn = l > 0 ? scratchConv[l - 1 + 3] : null; // gradient w.r.t. ConvIn[l] (pool output of l-1)
            if (dIn is not null) Array.Clear(dIn);
            ConvBackward(a.ConvIn[l], inC, s, ConvW[l], dOut, outC, g.ConvW[l], g.ConvB[l], dIn);
            dPooled = dIn!;
            s *= 2;
        }
        return loss;
    }

    private static void ConvBackward(float[] input, int inC, int s, float[] w, float[] dOut, int outC, float[] dW, float[] dB, float[]? dIn)
    {
        int plane = s * s;
        for (int oc = 0; oc < outC; oc++)
        {
            var d = dOut.AsSpan(oc * plane, plane);
            float sum = 0;
            foreach (var v in d) sum += v;
            dB[oc] += sum;
            if (sum == 0 && !AnyNonZero(d)) continue;
            for (int ic = 0; ic < inC; ic++)
            {
                var src = input.AsSpan(ic * plane, plane);
                int wBase = (oc * inC + ic) * 9;
                for (int ky = 0; ky < 3; ky++)
                    for (int kx = 0; kx < 3; kx++)
                    {
                        int dy = ky - 1, dx = kx - 1;
                        int x0 = Math.Max(0, -dx), x1 = Math.Min(s, s - dx);
                        float acc = 0;
                        float wv = w[wBase + ky * 3 + kx];
                        for (int y = Math.Max(0, -dy); y < Math.Min(s, s - dy); y++)
                        {
                            var dRow = d.Slice(y * s + x0, x1 - x0);
                            acc += Dot(dRow, src.Slice((y + dy) * s + x0 + dx, x1 - x0));
                            if (dIn is not null && wv != 0) Axpy(wv, dRow, dIn.AsSpan(ic * plane + (y + dy) * s + x0 + dx, x1 - x0));
                        }
                        dW[wBase + ky * 3 + kx] += acc;
                    }
            }
        }
    }

    private static bool AnyNonZero(ReadOnlySpan<float> v)
    {
        foreach (var x in v) if (x != 0) return true;
        return false;
    }

    /// <summary>Scratch buffers Backward needs, per thread.</summary>
    public (float[] Hidden, float[] Flat, float[][] Conv) BackwardScratch()
    {
        var conv = new float[6][];
        int s = Size;
        for (int l = 0; l < 3; l++) { conv[l] = new float[Channels[l + 1] * s * s]; s /= 2; }
        // dIn for conv l (l = 1, 2) has the shape of ConvIn[l].
        s = Size / 2;
        for (int l = 1; l < 3; l++) { conv[l - 1 + 3] = new float[Channels[l] * s * s]; s /= 2; }
        conv[5] = Array.Empty<float>();
        return (new float[Hidden], new float[Fc1In], conv);
    }

    // ---- weights file ----

    private const string Magic = "MSOCRNET1";

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Classes.Length);
        foreach (var c in Classes) w.Write(c);
        foreach (var p in Parameters())
        {
            w.Write(p.Length);
            // Half precision halves the file; inference loses nothing measurable.
            foreach (var v in p) w.Write((Half)v);
        }
    }

    public static MsGlyphNet Load(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadString() != Magic) throw new InvalidDataException("Not a MarkSmith OCR network.");
        var classes = new string[r.ReadInt32()];
        for (int i = 0; i < classes.Length; i++) classes[i] = r.ReadString();
        var net = new MsGlyphNet(classes);
        foreach (var p in net.Parameters())
        {
            int n = r.ReadInt32();
            if (n != p.Length) throw new InvalidDataException("MarkSmith OCR network shape mismatch.");
            for (int i = 0; i < n; i++) p[i] = (float)r.ReadHalf();
        }
        return net;
    }
}
