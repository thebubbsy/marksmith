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
        foreach (var g in MergeBroken(line.Glyphs, line, imageWidth, act))
            chars.AddRange(ReadCandidate(g, line, imageWidth, g.Left, g.Right, act, depth: 0));
        chars.RemoveAll(c => c.Text.Length == 0);
        chars = MergePercent(chars, line);

        var words = SplitWords(chars, line);
        for (int i = 0; i < words.Count; i++) words[i] = Correct(words[i]);
        // Words run together (an italic space too narrow to see): split where both halves are words.
        if (_lexicon is not null)
            for (int i = 0; i < words.Count; i++)
                if (SplitCore(words[i], line) is { } parts)
                {
                    words[i] = parts.First;
                    words.Insert(i + 1, parts.Second);
                    i++;
                }
        return new ReadLineResult(words, line);
    }

    // ---- merging and splitting ----

    /// <summary>
    /// A letter whose thin strokes broke apart when the scan was thresholded (an italic m, a light
    /// serif w) arrives as two or three marks, each read badly on its own. Neighbouring marks
    /// with almost no gap between them, at least one read weakly, are tried together; they're
    /// kept together when the whole reads as one letter clearly better than the pieces do.
    /// </summary>
    private List<GlyphCandidate> MergeBroken(List<GlyphCandidate> glyphs, TextLine line, int imageWidth, MsGlyphNet.Activations act)
    {
        float xh = line.XHeight, cap = line.CapHeight;
        var single = new Dictionary<GlyphCandidate, (float P, int Best)>();
        (float P, int Best) Read(GlyphCandidate g)
        {
            if (!single.TryGetValue(g, out var r))
            {
                var (p, best) = Classify(g, line, imageWidth, g.Left, g.Right, act);
                single[g] = r = (best == 0 ? 1e-4f : p[best], best);
            }
            return r;
        }
        bool Weak(GlyphCandidate g)
        {
            var (p, best) = Read(g);
            return best == 0 || p < 0.7f || SmallMarks.Contains(_net.Classes[best]);
        }

        var result = new List<GlyphCandidate>(glyphs.Count);
        for (int i = 0; i < glyphs.Count; i++)
        {
            GlyphCandidate? merged = null;
            int take = 0;
            for (int k = Math.Min(2, glyphs.Count - 1 - i); k >= 1 && merged is null; k--)
            {
                var group = glyphs.GetRange(i, k + 1);
                bool tight = true;
                for (int j = 1; j < group.Count; j++)
                    if (group[j].Left - group[j - 1].Right > xh * 0.2f) { tight = false; break; }
                if (!tight || !group.Any(Weak)) continue;
                var union = new GlyphCandidate { Blobs = group.SelectMany(x => x.Blobs).ToList() };
                if (union.Width > cap * 1.4f) continue;
                var (pu, bu) = Classify(union, line, imageWidth, union.Left, union.Right, act);
                if (bu == 0 || pu[bu] < 0.85f || SmallMarks.Contains(_net.Classes[bu])) continue;
                double parts = group.Sum(x => Math.Log(Math.Max(1e-6, Read(x).P)));
                if (Math.Log(pu[bu]) > parts + 1.0) { merged = union; take = k; }
            }
            if (merged is null) { result.Add(glyphs[i]); continue; }
            result.Add(merged);
            i += take;
        }
        return result;
    }

    // ---- classification ----

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
            // Commas, dots and quote marks are separate marks of their own; a piece cut out of
            // a letter that reads as one is a fragment (an italic m read as ",,1"). Only the last
            // piece may be one: a letter touching the full stop or comma after it (y, r.).
            if (parts.Take(parts.Count - 1).Any(c => SmallMarks.Contains(c.Text))
                || (SmallMarks.Contains(parts[^1].Text) && parts[^1].Text is not ("," or "." or ";" or ":" or "!" or "?"))) continue;
            // Mean log-probability per piece, with a small cost per extra piece so a confident
            // single letter isn't cut in two for nothing.
            double score = parts.Average(c => Math.Log(Math.Max(1e-6, c.Probability))) - 0.12 * (parts.Count - 1);
            if (score > bestScore + 0.25) { bestScore = score; bestSplit = parts; }
        }
        if (bestSplit is not null) return bestSplit;
        // Something letter-sized that the network called "not one letter" and no split could
        // explain is still most likely a letter: its best letter beats dropping it.
        if (best == 0 && depth == 0 && g.Height >= line.XHeight * 0.6f)
        {
            int second = 1;
            for (int i = 2; i < p.Length; i++) if (p[i] > p[second]) second = i;
            if (p[second] >= 0.05f) return new List<ReadChar> { Make(p, second, left, right) };
        }
        return single;
    }

    private static readonly HashSet<string> SmallMarks = new() { ",", ".", "'", "`", "‘", "’", "\"", "“", "”", ";", ":", "?", "!" };

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
        float xh = line.XHeight, cap = line.CapHeight;
        var gaps = new List<float>();
        for (int i = 1; i < chars.Count; i++) gaps.Add((chars[i].Left - chars[i - 1].Right - 1) / xh);
        float threshold = SpaceThreshold(gaps);
        float? pitch = MonospacePitch(chars);
        var current = new List<ReadChar> { chars[0] };
        for (int i = 1; i < chars.Count; i++)
        {
            if (IsSpace(chars[i - 1], chars[i], gaps[i - 1], threshold, pitch, xh, cap)) { words.Add(current); current = new List<ReadChar>(); }
            current.Add(chars[i]);
        }
        words.Add(current);
        return words;
    }

    private static float Center(ReadChar c) => (c.Left + c.Right) / 2f;

    private static bool IsSpace(ReadChar a, ReadChar b, float gap, float threshold, float? pitch, float xh, float cap)
    {
        // Monospaced type: every letter sits in a cell of the same width, so a space is a whole
        // empty cell between two centres, however narrow the letters (i, l, 1, punctuation) are.
        if (pitch is { } p) return Center(b) - Center(a) > p * 1.5f;
        // Digits are tabular in nearly every font: a narrow 1 leaves a wide gap that isn't a space.
        bool digits = IsDigitish(a.Text) && IsDigitish(b.Text);
        // Digit to digit is ~0.55 em centre to centre (0.76 cap heights); with a space ~0.83 em.
        if (digits) return Center(b) - Center(a) > cap * 1.0f;
        // Closing punctuation hangs on the word before it.
        if (b.Text is "," or "." or ";" or ":" or "!" or "?" or "%" or ")" or "]" or "}" or "’" or "”")
            return gap > Math.Max(threshold, 0.8f);
        return gap > threshold;
    }

    private static bool IsDigitish(string t) => t.Length == 1 && char.IsDigit(t[0]);

    /// <summary>
    /// The cell width of a monospaced line, or null for proportional type: most neighbouring
    /// letters' centres are one cell apart (within 12%), and the rest a whole number of cells.
    /// </summary>
    public static float? MonospacePitch(List<ReadChar> chars)
    {
        if (chars.Count < 12) return null;
        var d = new List<float>();
        for (int i = 1; i < chars.Count; i++) d.Add(Center(chars[i]) - Center(chars[i - 1]));
        var sorted = d.Where(x => x > 0).OrderBy(x => x).ToList();
        if (sorted.Count < 10) return null;
        float pitch = sorted[sorted.Count / 3];
        if (pitch <= 0) return null;
        int onPitch = 0, onGrid = 0;
        foreach (var x in d)
        {
            float cells = x / pitch;
            if (Math.Abs(cells - 1) < 0.12f) onPitch++;
            if (Math.Abs(cells - Math.Round(cells)) < 0.15f && cells > 0.5f) onGrid++;
        }
        return onPitch >= d.Count * 0.6f && onGrid >= d.Count * 0.9f ? pitch : null;
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
        bool realLetter = coreText.Any(ch => char.IsLetter(ch) && "lIiOo".IndexOf(ch) < 0);
        if (digits > 0 && digits >= letters)
        {
            // Mostly a number: the letters that look like digits are digits (l0l is 101). A token
            // with a letter that can't be a digit (10am, 4x4) is left as printed.
            if (!realLetter)
                core = core.Select(c => c.Text switch
                {
                    "O" or "o" => c with { Text = "0" },
                    "l" or "I" or "|" => c with { Text = "1" },
                    _ => c,
                }).ToList();
        }
        else if (_lexicon is not null && letters >= 2)
        {
            // Each run of letters is a word of its own: help@example.org, list[i].Length.
            foreach (var (runStart, runLength) in LetterRuns(core))
            {
                var run = core.GetRange(runStart, runLength);
                if (_lexicon.Contains(string.Concat(run.Select(c => c.Text)))) continue;
                if (_lexicon.BestMatch(run) is not { } fixedRun) continue;
                core.RemoveRange(runStart, runLength);
                core.InsertRange(runStart, fixedRun);
                // A merge or split changed the length: the later runs moved.
                if (fixedRun.Count != runLength) break;
            }
        }

        // Case that real words don't have (pOrt, wHite): the case-twin letters follow their neighbours.
        // Only in a plain word: in code (list.Where, items.Count) the capital is meant.
        coreText = string.Concat(core.Select(c => c.Text));
        if (coreText.Length >= 3 && coreText.Any(char.IsLower) && coreText.All(char.IsLetter))
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

    /// <summary>
    /// A percent sign is three marks (two rings and a slash), and read one by one they come out as
    /// "0/0" or "o/o". Three such marks packed into one letter's width are one %.
    /// </summary>
    private static List<ReadChar> MergePercent(List<ReadChar> chars, TextLine line)
    {
        static bool Ring(string t) => t is "0" or "o" or "O" or "°" or "º";
        float cap = line.CapHeight;
        var result = new List<ReadChar>(chars.Count);
        for (int i = 0; i < chars.Count; i++)
        {
            if (i + 2 < chars.Count && Ring(chars[i].Text) && chars[i + 1].Text is "/" && Ring(chars[i + 2].Text)
                && chars[i + 2].Right - chars[i].Left + 1 <= cap * 1.25f
                && chars[i + 1].Left - chars[i].Right <= cap * 0.15f && chars[i + 2].Left - chars[i + 1].Right <= cap * 0.15f)
            {
                result.Add(new ReadChar("%", Math.Min(chars[i].Probability, chars[i + 2].Probability), chars[i].Left, chars[i + 2].Right, Array.Empty<(string, float)>()));
                i += 2;
                continue;
            }
            result.Add(chars[i]);
        }
        return result;
    }

    // The letters of a word with its trailing punctuation (split at the widest gap of the letters).
    private (List<ReadChar> First, List<ReadChar> Second)? SplitCore(List<ReadChar> word, TextLine line)
    {
        int end = word.Count;
        while (end > 0 && !char.IsLetterOrDigit(word[end - 1].Text.FirstOrDefault())) end--;
        if (end < 4 || _lexicon!.SplitJoined(word.GetRange(0, end), line.XHeight * 0.15f) is not { } split) return null;
        var second = split.Second.Concat(word.Skip(end)).ToList();
        return (split.First, second);
    }

    /// <summary>Runs of letters within a token (digits that look like letters, 1 0 5 |, count when
    /// the run has letters; an apostrophe between letters stays in), each at least two long.</summary>
    private static List<(int Start, int Length)> LetterRuns(List<ReadChar> core)
    {
        static bool Part(string t) => t.Length == 1 && (char.IsLetter(t[0]) || t is "0" or "1" or "5" or "|");
        var runs = new List<(int, int)>();
        int i = 0;
        while (i < core.Count)
        {
            if (!Part(core[i].Text)) { i++; continue; }
            int start = i;
            // An apostrophe between letters stays in (don't); so does a bracket, which is how a
            // broken o reads (C)pen).
            while (i < core.Count && (Part(core[i].Text)
                   || (core[i].Text is "'" or "’" or "(" or ")" && i + 1 < core.Count && Part(core[i + 1].Text) && i > start))) i++;
            int length = i - start;
            var text = string.Concat(core.Skip(start).Take(length).Select(c => c.Text));
            if (length >= 2 && text.Count(char.IsLetter) > text.Count(char.IsDigit)) runs.Add((start, length));
        }
        return runs;
    }

    private static List<ReadChar> MergeQuotes(List<ReadChar> w)
    {
        var outp = new List<ReadChar>();
        for (int i = 0; i < w.Count; i++)
        {
            // A colon read as a full stop beside a colon (its dots slanted apart): one colon.
            if (i + 1 < w.Count && (w[i].Text + w[i + 1].Text) is ".:" or ":." && w[i + 1].Left - w[i].Right < (w[i].Right - w[i].Left + 1) * 2)
            {
                outp.Add(w[i] with { Text = ":", Right = w[i + 1].Right });
                i++;
                continue;
            }
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
