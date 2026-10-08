namespace MarkSmith.Ocr.MarkSmith;

/// <summary>One read character with the network's runner-up guesses and where it was.</summary>
public sealed record ReadChar(string Text, float Probability, int Left, int Right, (string Text, float P)[] Alternatives);

/// <summary>A read line: words, each a list of characters.</summary>
public sealed record ReadLineResult(List<List<ReadChar>> Words, TextLine Line)
{
    public string Text => string.Join(" ", Words.Select(w => string.Concat(w.Select(c => c.Text))));
}

/// <summary>
/// Reads segmented lines with <see cref="MsGlyphNet"/>: classifies each letter candidate, splits
/// candidates that turn out to be letters touching each other, finds the word spaces from the
/// line's own gap pattern, and corrects words against the dictionary using the network's own
/// second guesses (so "rn" vs "m" and l / I / 1 are settled by real words, not by a blind table).
/// </summary>
public sealed class MsRecognizer
{
    private readonly MsGlyphNet _net;
    private readonly MsLexicon? _lexicon;

    public MsRecognizer(MsGlyphNet net, MsLexicon? lexicon)
    {
        _net = net;
        _lexicon = lexicon;
    }

    public ReadLineResult Read(TextLine line, int imageWidth)
    {
        var act = new MsGlyphNet.Activations(_net);
        var chars = new List<ReadChar>();
        foreach (var g in line.Glyphs)
            chars.AddRange(ReadCandidate(g, line, imageWidth, g.Left, g.Right, act, depth: 0));
        chars.RemoveAll(c => c.Text.Length == 0);

        var words = SplitWords(chars, line);
        for (int i = 0; i < words.Count; i++) words[i] = Correct(words[i]);
        return new ReadLineResult(words, line);
    }

    // ---- classification and splitting ----

    private (float[] P, int Best) Classify(GlyphCandidate g, TextLine line, int imageWidth, int left, int right, MsGlyphNet.Activations act)
    {
        float[] image;
        float[] features;
        if (left == g.Left && right == g.Right)
        {
            image = MsGlyph.Image(g, imageWidth);
            features = MsGlyph.Features(g, line);
        }
        else
        {
            var (top, bottom, ink) = MsGlyph.Piece(g, imageWidth, left, right);
            image = MsGlyph.Image(g, imageWidth, left, right);
            features = MsGlyph.Features(g, line, top, bottom, left, right, ink);
        }
        var p = MsGlyphNet.Softmax(_net.Forward(image, features, act));
        int best = 0;
        for (int i = 1; i < p.Length; i++) if (p[i] > p[best]) best = i;
        return (p, best);
    }

    private List<ReadChar> ReadCandidate(GlyphCandidate g, TextLine line, int imageWidth, int left, int right, MsGlyphNet.Activations act, int depth)
    {
        var (p, best) = Classify(g, line, imageWidth, left, right, act);
        var single = new List<ReadChar> { Make(p, best, left, right) };
        float cap = line.CapHeight;
        int width = right - left + 1;
        // Something the network calls "not one letter" (class 0) that is wide enough to be two,
        // or anything wider than most single letters read without real confidence, may be
        // letters touching (rn, ar, th in tight serif type): try splitting it.
        bool notOne = best == 0 && width > cap * 0.45f;
        bool wide = width > cap * 0.85f;
        if (depth >= 3 || (!wide && !notOne) || (best != 0 && p[best] > 0.92f && width < cap * 1.6f)) return single;

        // "Not one letter" as a whole scores as if very unlikely, so any sensible split wins.
        double singleScore = best == 0 ? Math.Log(1e-4) : Math.Log(Math.Max(1e-6, p[best]));
        List<ReadChar>? bestSplit = null;
        double bestScore = singleScore;
        foreach (int cut in CutColumns(g, imageWidth, left, right, (int)Math.Max(2, cap * 0.18f)))
        {
            var leftPart = ReadCandidate(g, line, imageWidth, left, cut, act, depth + 1);
            var rightPart = ReadCandidate(g, line, imageWidth, cut + 1, right, act, depth + 1);
            var parts = leftPart.Concat(rightPart).ToList();
            if (parts.Any(c => c.Text.Length == 0)) continue;
            // Mean log-probability per piece, with a small cost per extra piece so a confident
            // single letter isn't cut in two for nothing.
            double score = parts.Average(c => Math.Log(Math.Max(1e-6, c.Probability))) - 0.12 * (parts.Count - 1);
            if (score > bestScore + 0.25) { bestScore = score; bestSplit = parts; }
        }
        return bestSplit ?? single;
    }

    /// <summary>Columns where the ink is thinnest (where two touching letters join), best first.</summary>
    private static IEnumerable<int> CutColumns(GlyphCandidate g, int imageWidth, int left, int right, int minPiece)
    {
        int w = right - left + 1;
        if (w < minPiece * 2 + 1) yield break;
        var ink = new int[w];
        foreach (var b in g.Blobs)
            foreach (var i in b.Pixels)
            {
                int x = i % imageWidth - left;
                if (x >= 0 && x < w) ink[x]++;
            }
        var candidates = new List<(int X, int Ink)>();
        for (int x = minPiece; x < w - minPiece; x++)
        {
            int v = ink[x];
            if (v <= ink[x - 1] && v <= ink[x + 1]) candidates.Add((x, v));
        }
        foreach (var c in candidates.OrderBy(c => c.Ink).Take(4))
            yield return left + c.X;
    }

    private ReadChar Make(float[] p, int best, int left, int right)
    {
        var alts = p.Select((v, i) => (Text: _net.Classes[i], P: v))
                    .OrderByDescending(t => t.P).Take(4).ToArray();
        return new ReadChar(_net.Classes[best], p[best], left, right, alts);
    }

    // ---- words ----

    private static List<List<ReadChar>> SplitWords(List<ReadChar> chars, TextLine line)
    {
        var words = new List<List<ReadChar>>();
        if (chars.Count == 0) return words;
        float xh = line.XHeight;
        var gaps = new List<float>();
        for (int i = 1; i < chars.Count; i++) gaps.Add((chars[i].Left - chars[i - 1].Right - 1) / xh);
        float threshold = SpaceThreshold(gaps);
        var current = new List<ReadChar> { chars[0] };
        for (int i = 1; i < chars.Count; i++)
        {
            if (gaps[i - 1] > threshold) { words.Add(current); current = new List<ReadChar>(); }
            current.Add(chars[i]);
        }
        words.Add(current);
        return words;
    }

    /// <summary>
    /// The gap (in x-heights) above which there is a space: Otsu's split of the line's gaps into
    /// letter gaps and word gaps, kept within what real type does (0.28–0.8 x-height) so a line
    /// of one word or of evenly spaced code doesn't produce nonsense.
    /// </summary>
    public static float SpaceThreshold(List<float> gaps)
    {
        const float fallback = 0.42f;
        if (gaps.Count < 4) return fallback;
        var sorted = gaps.OrderBy(g => g).ToList();
        double bestVar = -1, bestT = fallback;
        for (int i = 1; i < sorted.Count; i++)
        {
            double t = (sorted[i - 1] + sorted[i]) / 2;
            var a = sorted.Take(i).ToList();
            var b = sorted.Skip(i).ToList();
            double ma = a.Average(), mb = b.Average();
            double v = a.Count * (double)b.Count * (ma - mb) * (ma - mb);
            if (v > bestVar) { bestVar = v; bestT = t; }
        }
        // No clear second cluster (all letter gaps): only treat really wide gaps as spaces.
        double spread = sorted[^1] - sorted[0];
        if (spread < 0.3) return Math.Max(fallback, (float)sorted[^1] + 0.01f);
        return (float)Math.Clamp(bestT, 0.28, 0.8);
    }

    // ---- correction ----

    private static readonly HashSet<char> CasePairs = new("cCoOsSvVwWxXzZuUkKpPyYjJ");

    private List<ReadChar> Correct(List<ReadChar> word)
    {
        // Quote pairs read as two ticks.
        word = MergeQuotes(word);
        var text = string.Concat(word.Select(c => c.Text));

        // Split leading/trailing punctuation off the core.
        int start = 0, end = word.Count;
        while (start < end && !char.IsLetterOrDigit(word[start].Text.FirstOrDefault())) start++;
        while (end > start && !char.IsLetterOrDigit(word[end - 1].Text.FirstOrDefault())) end--;
        if (end - start == 0) return word;
        var core = word.GetRange(start, end - start);
        string coreText = string.Concat(core.Select(c => c.Text));

        int digits = coreText.Count(char.IsDigit), letters = coreText.Count(char.IsLetter);
        if (digits > 0 && digits >= letters)
        {
            // Mostly a number: the letters that look like digits are digits.
            core = core.Select(c => c.Text switch
            {
                "O" or "o" => c with { Text = "0" },
                "l" or "I" or "|" => c with { Text = "1" },
                _ => c,
            }).ToList();
        }
        else if (_lexicon is not null && letters >= 2 && !_lexicon.Contains(coreText))
        {
            var fixedCore = _lexicon.BestMatch(core);
            if (fixedCore is not null) core = fixedCore;
        }

        // Case that real words don't have (pOrt, wHite): the case-twin letters follow their neighbours.
        coreText = string.Concat(core.Select(c => c.Text));
        if (coreText.Length >= 3 && coreText.Any(char.IsLower))
        {
            bool allCapsish = coreText.Count(char.IsUpper) > coreText.Count(char.IsLower);
            for (int i = 1; i < core.Count; i++)
            {
                var t = core[i].Text;
                if (t.Length == 1 && CasePairs.Contains(t[0]) && !allCapsish && char.IsUpper(t[0]))
                    core[i] = core[i] with { Text = t.ToLowerInvariant() };
            }
        }

        var result = word.Take(start).ToList();
        result.AddRange(core);
        result.AddRange(word.Skip(end));
        return result;
    }

    private static List<ReadChar> MergeQuotes(List<ReadChar> w)
    {
        var outp = new List<ReadChar>();
        for (int i = 0; i < w.Count; i++)
        {
            if (i + 1 < w.Count)
            {
                string pair = w[i].Text + w[i + 1].Text;
                string? merged = pair switch { "''" => "\"", "‘‘" => "“", "’’" => "”", _ => null };
                if (merged is not null && w[i + 1].Left - w[i].Right < (w[i].Right - w[i].Left + 1) * 2)
                {
                    outp.Add(w[i] with { Text = merged, Right = w[i + 1].Right });
                    i++;
                    continue;
                }
            }
            outp.Add(w[i]);
        }
        return outp;
    }
}
