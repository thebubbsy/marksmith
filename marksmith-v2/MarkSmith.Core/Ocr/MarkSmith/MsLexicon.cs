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
        var list = new List<string>
        {
            "centrelink", "medicare", "commonwealth", "australia", "australian",
            "superannuation", "jobseeker", "austudy", "abstudy", "fortnight", "fortnightly",
            "carer", "carers", "parenting", "partnered", "unpartnered", "underpayment", "overpayment"
        };
        string? line;
        while ((line = r.ReadLine()) is not null) list.Add(line);
        return new MsLexicon(list);
    }

    public int Count => _words.Count;

    /// <summary>Whether the word (or its singular/possessive base) is a known word.</summary>
    public bool Contains(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        var w = word.ToLowerInvariant();
        if (_words.Contains(w)) return true;
        foreach (var suffix in new[] { "'s", "’s", "s'", "s’", "s", "es", "ed", "ing", "ly", "er", "ers", "or", "ment", "ments", "tion", "tions" })
            if (w.Length > suffix.Length + 2 && w.EndsWith(suffix, StringComparison.Ordinal) && _words.Contains(w[..^suffix.Length]))
                return true;
        // Hyphenated compounds: every part known.
        if (w.Contains('-')) return w.Split('-').All(p => p.Length == 0 || Contains(p));
        return false;
    }

    private static readonly (string From, string To)[] LookAlikes =
    {
        ("rn", "m"), ("m", "rn"), ("cl", "d"), ("d", "cl"), ("vv", "w"), ("w", "vv"), ("ri", "n"), ("li", "h"), ("ii", "u"), ("in", "m"),
        ("ii", "ll"), ("iii", "ll"), ("Il", "ll"), ("lI", "ll"), ("it", "rt"), ("ait", "art"), ("oit", "ort"), ("uit", "urt"),
        // An o broken open by a photocopy reads as two marks.
        ("C)", "o"), ("c)", "o"), ("()", "o"), ("0", "o"),
    };

    /// <summary>
    /// Glyphs that are drawn alike in some typefaces, whatever the network's confidence: in a
    /// typewriter face l and 1 are the same stroke with a serif, and i/l/I/| differ by a dot or a
    /// pixel. A word only changes through these when the result is a real word.
    /// </summary>
    private static readonly Dictionary<string, string[]> Confusable = new()
    {
        ["1"] = new[] { "l", "I", "i" }, ["l"] = new[] { "i", "I", "1" }, ["i"] = new[] { "l", "r" }, ["I"] = new[] { "l", "i" },
        ["|"] = new[] { "l", "I" }, ["!"] = new[] { "l", "i" }, ["0"] = new[] { "o", "O" },
    };

    /// <summary>The prior a look-alike substitution starts from (it then has to make a real word).</summary>
    private const float ConfusableP = 0.08f;

    public List<ReadChar>? BestMatch(List<ReadChar> core)
    {
        if (core.Count < 2 || core.Count > 24) return null;
        string rawText = string.Concat(core.Select(c => c.Text));
        bool hasLookAlikeSubstring = rawText.Contains("ait", StringComparison.OrdinalIgnoreCase)
            || rawText.Contains("oit", StringComparison.OrdinalIgnoreCase)
            || rawText.Contains("ii", StringComparison.OrdinalIgnoreCase)
            || rawText.Contains("rn", StringComparison.Ordinal);
        // A confidently read word is left alone, unless it holds a glyph that has a twin
        // (pane1, cooi, paitner, wiii): the network can be sure and still wrong about those.
        // Two-letter tokens (ls, lf) are too short to judge from a dictionary.
        if (core.All(c => c.Probability >= 0.9f)
            && (core.Count < 3 || (!hasLookAlikeSubstring && !core.Any(c => Confusable.ContainsKey(c.Text) || c.Text is "(" or ")"))))
            return null;
        double original = core.Sum(c => Math.Log(Math.Max(1e-6, c.Probability)));

        // Beam search over each letter's alternatives.
        var beam = new List<(List<ReadChar> Chars, double Score)> { (new List<ReadChar>(), 0) };
        foreach (var c in core)
        {
            var next = new List<(List<ReadChar>, double)>();
            var options = c.Alternatives.Where(a => a.P >= 0.02f && a.Text.Length == 1 && (char.IsLetter(a.Text[0]) || a.Text == c.Text || a.Text == "'" || a.Text == "-"))
                                        .DefaultIfEmpty((c.Text, c.Probability)).ToList();
            if (Confusable.TryGetValue(c.Text, out var twins))
                foreach (var t in twins)
                    if (!options.Any(o => o.Text == t)) options.Add((t, ConfusableP));
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
        if (best is null || best.Value.Score < original - 6.5) return null;
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
                yield return (text[..i] + to + text[(i + from.Length)..], Math.Log(0.18));
                i = text.IndexOf(from, i + 1, StringComparison.Ordinal);
            }
        }
    }

    private static readonly HashSet<string> GlueWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "you", "your", "yours", "yourself", "of", "to", "in", "for", "and", "with", "from",
        "about", "into", "onto", "upon", "have", "has", "had", "does", "did", "are", "was", "were",
        "will", "would", "could", "should", "when", "where", "what", "which", "while", "that", "this",
        "these", "those", "there", "their", "they", "them", "been", "being", "after", "before",
        "under", "over", "between", "through", "during", "without", "within", "payment", "payments",
        "pension", "allowance", "benefit", "benefits", "partner", "details", "number", "income",
        "address", "person", "people", "service", "services", "customer", "reference", "relationship",
        "separated", "separation", "living", "property", "children", "arrangement", "arrangements",
        "commitment", "financial", "household", "information", "question", "questions", "support",
        "disability", "carer", "parenting", "complete", "completed", "form", "shared", "care",
        "accommodation", "contact", "phone", "date", "birth", "name", "names", "other", "please",
        "provide", "give", "below", "next", "goto", "need", "needs", "tell", "told", "live", "lived"
    };

    private static readonly HashSet<string> ShortValidWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "i", "of", "to", "in", "on", "at", "by", "or", "as", "is", "be", "us", "we", "he",
        "my", "no", "so", "up", "do", "go", "if", "an", "it", "me"
    };

    /// <summary>
    /// Words run together because the space between them was too narrow to see: splits at the
    /// widest real gap, or segments long compound runs of known dictionary words via dynamic programming.
    /// </summary>
    public (List<ReadChar> First, List<ReadChar> Second)? SplitJoined(List<ReadChar> word, float minGap)
    {
        if (word.Count < 4 || !word.All(c => c.Text.Length == 1 && (char.IsLetter(c.Text[0]) || c.Text is "1" or "0" or "|"))) return null;

        // Normalize internal confusable 'I'/'1'/'|' inside lowercase letters before checking/splitting.
        var normalized = NormalizeInternalConfusables(word);
        var text = string.Concat(normalized.Select(c => c.Text));
        if (Contains(text)) return null;

        int widest = -1, widestGap = int.MinValue;
        var gaps = new List<int>();
        for (int i = 1; i < normalized.Count; i++)
        {
            int gap = normalized[i].Left - normalized[i - 1].Right;
            gaps.Add(gap);
            if (gap > widestGap) { widestGap = gap; widest = i; }
        }
        gaps.Sort();
        float median = gaps[gaps.Count / 2];

        bool ValidPart(string w) => w.Length >= 2 ? Contains(w) : w is "a" or "A" or "I";

        // 1. Check all gaps that exceed the real-gap threshold (widest first).
        if (widest >= 1 && widestGap >= minGap && widestGap >= median * 1.8f + 1)
        {
            var realGapIndices = Enumerable.Range(1, normalized.Count - 1)
                .Where(i =>
                {
                    int g = normalized[i].Left - normalized[i - 1].Right;
                    return g >= minGap && g >= median * 1.8f + 1;
                })
                .OrderByDescending(i => normalized[i].Left - normalized[i - 1].Right)
                .ToList();

            foreach (int cut in realGapIndices)
            {
                string a = text[..cut], b = text[cut..];
                if (ValidPart(a) && (ValidPart(b) || TryDpSplit(b, normalized, cut) is not null))
                    return (normalized.GetRange(0, cut), normalized.GetRange(cut, normalized.Count - cut));
            }
        }

        // 2. Check camelCase / PascalCase internal boundary (e.g., "HaveYou", "CarerPayment") where both sides are known.
        for (int i = 2; i <= normalized.Count - 2; i++)
        {
            if (char.IsLower(text[i - 1]) && char.IsUpper(text[i]))
            {
                string a = text[..i], b = text[i..];
                if (ValidPart(a) && (ValidPart(b) || TryDpSplit(b, normalized, i) is not null))
                    return (normalized.GetRange(0, i), normalized.GetRange(i, normalized.Count - i));
            }
        }

        // 3. For long compound runs (>= 10 chars, e.g. "Carerpayment", "Disabilitysupportpension",
        //    "natureofcommitment", "HaveyoutoIdusaboutyourseparation") or glue-word pairs (e.g. "Haveyou",
        //    "ofyour", "Doesthe"), find the optimal DP dictionary segmentation.
        if (normalized.Count >= 10 || HasGlueWordBoundary(text))
        {
            if (TryDpSplit(text, normalized, 0) is { } firstCut && firstCut > 0 && firstCut < normalized.Count)
            {
                return (normalized.GetRange(0, firstCut), normalized.GetRange(firstCut, normalized.Count - firstCut));
            }
        }

        return null;
    }

    private bool HasGlueWordBoundary(string text)
    {
        if (text.Length < 5) return false;
        for (int i = 2; i <= text.Length - 2; i++)
        {
            string a = text[..i], b = text[i..];
            if ((GlueWords.Contains(a) && Contains(b) && b.Length >= 3)
                || (GlueWords.Contains(b) && Contains(a) && a.Length >= 2))
                return true;
        }
        return false;
    }

    private int? TryDpSplit(string subText, List<ReadChar> fullChars, int offset)
    {
        int n = subText.Length;
        if (n < 4) return null;

        // dp[i] = (score, nextIndex, wordCount) for subText[i..n]
        var dp = new (double Score, int Next, int Words)[n + 1];
        for (int i = 0; i <= n; i++) dp[i] = (double.NegativeInfinity, -1, 0);
        dp[n] = (0, n, 0);

        for (int i = n - 1; i >= 0; i--)
        {
            int maxLen = Math.Min(20, n - i);
            for (int len = 1; len <= maxLen; len++)
            {
                int j = i + len;
                if (double.IsNegativeInfinity(dp[j].Score)) continue;
                if (i == 0 && j == n) continue; // must split into at least 2 parts at top level

                string part = subText.Substring(i, len);
                string lower = part.ToLowerInvariant();
                bool isValid = false;
                double wordScore = 0;

                if (len == 1)
                {
                    if (part is "a" or "A" or "I" && n >= 12)
                    {
                        isValid = true;
                        wordScore = -4.5;
                    }
                }
                else if (len == 2)
                {
                    if (ShortValidWords.Contains(lower))
                    {
                        isValid = true;
                        wordScore = GlueWords.Contains(lower) ? 1.5 : -2.2;
                    }
                }
                else if (len == 3)
                {
                    if (_words.Contains(lower))
                    {
                        isValid = true;
                        wordScore = GlueWords.Contains(lower) ? 3.5 : 0.8;
                    }
                }
                else
                {
                    if (_words.Contains(lower) || Contains(lower))
                    {
                        isValid = true;
                        wordScore = len * 1.6 + (GlueWords.Contains(lower) ? 2.5 : 0);
                    }
                }

                if (!isValid) continue;

                // Small bonus when the physical gap at the split boundary is wider than average.
                double gapBonus = 0;
                if (j < n && offset + j < fullChars.Count && offset + j > 0)
                {
                    int gap = fullChars[offset + j].Left - fullChars[offset + j - 1].Right;
                    gapBonus = Math.Clamp(gap * 0.15, -0.5, 1.5);
                }

                double candidateScore = wordScore + gapBonus - 1.8 + dp[j].Score;
                if (candidateScore > dp[i].Score)
                {
                    dp[i] = (candidateScore, j, dp[j].Words + 1);
                }
            }
        }

        if (double.IsNegativeInfinity(dp[0].Score) || dp[0].Next <= 0 || dp[0].Next >= n) return null;
        return dp[0].Next;
    }

    private static List<ReadChar> NormalizeInternalConfusables(List<ReadChar> word)
    {
        if (word.Count < 3) return word;
        bool hasLower = word.Any(c => c.Text.Length == 1 && char.IsLower(c.Text[0]));
        if (!hasLower) return word;
        var result = new List<ReadChar>(word);
        for (int i = 0; i < result.Count; i++)
        {
            string t = result[i].Text;
            if (t is "I" or "1" or "|")
            {
                bool prevLower = i > 0 && result[i - 1].Text.Length == 1 && char.IsLower(result[i - 1].Text[0]);
                bool nextLower = i + 1 < result.Count && result[i + 1].Text.Length == 1 && char.IsLower(result[i + 1].Text[0]);
                if (prevLower && nextLower)
                {
                    result[i] = result[i] with { Text = "l" };
                }
                else if (i > 0 && prevLower && i == result.Count - 1)
                {
                    result[i] = result[i] with { Text = "l" };
                }
            }
        }
        return result;
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
