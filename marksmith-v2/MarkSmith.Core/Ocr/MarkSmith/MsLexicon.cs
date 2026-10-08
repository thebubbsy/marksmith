using System.IO.Compression;

namespace MarkSmith.Ocr.MarkSmith;

/// <summary>
/// MarkSmith OCR's word list (SCOWL-derived English, see THIRD-PARTY-NOTICES) and the search that
/// uses it: given a word the network was unsure of, find the most likely real word reachable
/// through the network's own runner-up guesses for each letter, plus the classic look-alike
/// merges and splits (rn↔m, cl↔d, vv↔w).
/// </summary>
public sealed class MsLexicon
{
    private readonly HashSet<string> _words;

    public MsLexicon(IEnumerable<string> words) =>
        _words = new HashSet<string>(words.Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0), StringComparer.Ordinal);

    public static MsLexicon? LoadEmbedded()
    {
        using var s = typeof(MsLexicon).Assembly.GetManifestResourceStream("MarkSmith.Ocr.MarkSmith.words-en.txt.gz");
        if (s is null) return null;
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        var list = new List<string>();
        string? line;
        while ((line = r.ReadLine()) is not null) list.Add(line);
        return new MsLexicon(list);
    }

    public int Count => _words.Count;

    /// <summary>Whether the word (or its singular/possessive base) is a known word.</summary>
    public bool Contains(string word)
    {
        var w = word.ToLowerInvariant();
        if (_words.Contains(w)) return true;
        foreach (var suffix in new[] { "'s", "’s", "s", "es", "ed", "ing", "ly" })
            if (w.Length > suffix.Length + 2 && w.EndsWith(suffix, StringComparison.Ordinal) && _words.Contains(w[..^suffix.Length]))
                return true;
        // Hyphenated compounds: every part known.
        if (w.Contains('-')) return w.Split('-').All(p => p.Length == 0 || _words.Contains(p));
        return false;
    }

    private static readonly (string From, string To)[] LookAlikes =
    {
        ("rn", "m"), ("m", "rn"), ("cl", "d"), ("d", "cl"), ("vv", "w"), ("w", "vv"), ("ri", "n"), ("li", "h"), ("ii", "u"), ("in", "m"),
    };

    /// <summary>
    /// The best dictionary word for <paramref name="core"/>, or null to leave it alone. A word the
    /// network read confidently is never changed: names, codes and words this list doesn't know
    /// stay as they were printed.
    /// </summary>
    public List<ReadChar>? BestMatch(List<ReadChar> core)
    {
        if (core.Count < 2 || core.Count > 24) return null;
        if (core.All(c => c.Probability >= 0.9f)) return null;
        double original = core.Sum(c => Math.Log(Math.Max(1e-6, c.Probability)));

        // Beam search over each letter's alternatives.
        var beam = new List<(List<ReadChar> Chars, double Score)> { (new List<ReadChar>(), 0) };
        foreach (var c in core)
        {
            var next = new List<(List<ReadChar>, double)>();
            var options = c.Alternatives.Where(a => a.P >= 0.02f && a.Text.Length == 1 && (char.IsLetter(a.Text[0]) || a.Text == c.Text || a.Text == "'" || a.Text == "-"))
                                        .DefaultIfEmpty((c.Text, c.Probability));
            foreach (var (chars, score) in beam)
                foreach (var (text, p) in options)
                {
                    var list = new List<ReadChar>(chars) { c with { Text = text, Probability = p } };
                    next.Add((list, score + Math.Log(Math.Max(1e-6, p))));
                }
            beam = next.OrderByDescending(b => b.Item2).Take(64).ToList();
        }

        (List<ReadChar> Chars, double Score)? best = null;
        foreach (var (chars, score) in beam)
        {
            var text = string.Concat(chars.Select(c => c.Text));
            foreach (var (candidate, penalty) in Variants(text))
            {
                if (!Contains(candidate)) continue;
                double s = score + penalty;
                if (best is null || s > best.Value.Score) best = (Rebuild(chars, text, candidate), s);
            }
        }
        // A correction must not be wildly less likely than what the network read.
        if (best is null || best.Value.Score < original - 6) return null;
        return best.Value.Chars;
    }

    private static IEnumerable<(string Text, double Penalty)> Variants(string text)
    {
        yield return (text, 0);
        foreach (var (from, to) in LookAlikes)
        {
            int i = text.IndexOf(from, StringComparison.Ordinal);
            while (i >= 0)
            {
                yield return (text[..i] + to + text[(i + from.Length)..], Math.Log(0.15));
                i = text.IndexOf(from, i + 1, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The corrected characters, keeping the original boxes (spread over a merge/split).</summary>
    private static List<ReadChar> Rebuild(List<ReadChar> chars, string from, string to)
    {
        if (from.Length == to.Length)
            return chars.Select((c, i) => c with { Text = to[i].ToString() }).ToList();
        int left = chars[0].Left, right = chars[^1].Right;
        float width = (right - left + 1) / (float)to.Length;
        return to.Select((ch, i) => new ReadChar(ch.ToString(), 0.5f, left + (int)(i * width), left + (int)((i + 1) * width) - 1, Array.Empty<(string, float)>())).ToList();
    }
}
