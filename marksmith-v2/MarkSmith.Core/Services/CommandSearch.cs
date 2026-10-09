namespace MarkSmith.Services;

/// <summary>
/// Ranks command-palette entries against a query. Plain substring-or-subsequence filtering left
/// the list in definition order, so with ~70 commands a loose subsequence hit listed earlier could
/// sit above the exact command. Better matches now come first: whole label, label prefix, word
/// prefix, substring, all words in any order, keyword, category, then word-start abbreviations
/// ("ep" or "exppdf" for Export PDF).
/// A letter-by-letter subsequence used to be the last resort, but it matched far too much: "case"
/// found "Copy as email" (C…a-s…e). Abbreviations now have to start each piece at a word start
/// and stay in few pieces, which is how people actually abbreviate.
/// </summary>
public static class CommandSearch
{
    private static readonly char[] WordBreaks = { ' ', '(', ')', ':', '.', '-', '/', '…', ',', '+', '&' };

    /// <summary>Higher is better; null means no match. An empty query matches everything at 0.</summary>
    public static int? Score(string label, string category, string query, string keywords = "") =>
        Analyse(label, category, query, keywords).Score;

    /// <summary>
    /// The parts of <paramref name="label"/> the query matched, as (start, length) pairs in label
    /// order, for bolding in the list. Empty when the match came from keywords or the category.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Highlights(string label, string query) =>
        Analyse(label, "", query, "").Ranges;

    /// <summary>The items that match, best first; ties keep their original order.</summary>
    public static List<T> Rank<T>(IEnumerable<T> items, string query, Func<T, string> label, Func<T, string> category, Func<T, string>? keywords = null) =>
        items.Select((item, index) => (item, index, score: Score(label(item), category(item), query, keywords?.Invoke(item) ?? "")))
            .Where(x => x.score is not null)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();

    private static readonly IReadOnlyList<(int Start, int Length)> NoRanges = Array.Empty<(int, int)>();

    private static (int? Score, IReadOnlyList<(int Start, int Length)> Ranges) Analyse(string label, string category, string query, string keywords)
    {
        query = (query ?? "").Trim();
        label ??= "";
        if (query.Length == 0) return (0, NoRanges);
        var lengthPenalty = Math.Min(label.Length, 99);
        var whole = new[] { (0, label.Length) };

        if (label.Equals(query, StringComparison.OrdinalIgnoreCase)) return (1000, whole);
        if (label.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return (900 - lengthPenalty, new[] { (0, query.Length) });

        var words = Words(label);
        foreach (var (start, text) in words)
        {
            if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return (800 - lengthPenalty, new[] { (start, query.Length) });
        }
        // An initialism ("ep" for Export PDF) says more than the same letters mid-word ("rEPlace").
        var compact = query.Replace(" ", "");
        var pieces = Abbreviation(words, compact);
        if (pieces is not null && pieces.Count > 1 && pieces.All(p => p.Length == 1)) return (750 - lengthPenalty, pieces);
        var at = label.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return (700 - lengthPenalty, new[] { (at, query.Length) });

        // Several words in any order: "pdf export" finds "Export PDF".
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var haystack = $"{label} {category} {keywords}";
        if (terms.Length > 1 && terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return (600 - lengthPenalty, TermRanges(label, terms));

        if (!string.IsNullOrEmpty(keywords) && keywords.Contains(query, StringComparison.OrdinalIgnoreCase)) return (500, NoRanges);
        if (category?.StartsWith(query, StringComparison.OrdinalIgnoreCase) == true) return (400, NoRanges);

        if (pieces is not null) return (300 - pieces.Count * 10 - lengthPenalty / 10, pieces);
        return (null, NoRanges);
    }

    private static List<(int Start, string Text)> Words(string label)
    {
        var words = new List<(int, string)>();
        var i = 0;
        while (i < label.Length)
        {
            while (i < label.Length && Array.IndexOf(WordBreaks, label[i]) >= 0) i++;
            var start = i;
            while (i < label.Length && Array.IndexOf(WordBreaks, label[i]) < 0) i++;
            if (i > start) words.Add((start, label[start..i]));
        }
        return words;
    }

    private static IReadOnlyList<(int Start, int Length)> TermRanges(string label, string[] terms)
    {
        var ranges = new List<(int Start, int Length)>();
        foreach (var term in terms)
        {
            var at = label.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && !ranges.Any(r => at < r.Start + r.Length && r.Start < at + term.Length)) ranges.Add((at, term.Length));
        }
        return ranges.OrderBy(r => r.Start).ToList();
    }

    /// <summary>
    /// Splits the query into pieces that each start a later word of the label: "exppdf" is
    /// "exp" + "pdf", "ep" is "e" + "p". Accepted only as a pure initialism (every piece one
    /// letter) or in few pieces; "case" against "Copy as email" needs c + as + e and is refused.
    /// </summary>
    private static IReadOnlyList<(int Start, int Length)>? Abbreviation(List<(int Start, string Text)> words, string query)
    {
        if (query.Length < 2) return null;
        var pieces = Parse(words, query, 0, 0);
        if (pieces is null) return null;
        var initialism = pieces.All(p => p.Length == 1);
        var fewPieces = pieces.Count <= Math.Max(2, (query.Length + 2) / 3);
        return initialism || fewPieces ? pieces : null;
    }

    private static List<(int Start, int Length)>? Parse(List<(int Start, string Text)> words, string query, int qi, int wi)
    {
        if (qi == query.Length) return new List<(int, int)>();
        for (var w = wi; w < words.Count; w++)
        {
            var text = words[w].Text;
            var max = 0;
            while (max < text.Length && qi + max < query.Length && char.ToLowerInvariant(text[max]) == char.ToLowerInvariant(query[qi + max])) max++;
            // Longest piece first, so "exppdf" reads as exp + pdf rather than e + x….
            for (var k = max; k >= 1; k--)
            {
                var rest = Parse(words, query, qi + k, w + 1);
                if (rest is null) continue;
                rest.Insert(0, (words[w].Start, k));
                return rest;
            }
        }
        return null;
    }
}
