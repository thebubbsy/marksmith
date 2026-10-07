namespace MarkSmith.Services;

/// <summary>
/// Ranks command-palette entries against a query. Plain substring-or-subsequence filtering left
/// the list in definition order, so with ~70 commands a loose subsequence hit listed earlier could
/// sit above the exact command. Better matches now come first: whole label, label prefix, word
/// prefix, substring, all words in any order, keyword, category, then subsequence.
/// </summary>
public static class CommandSearch
{
    /// <summary>Higher is better; null means no match. An empty query matches everything at 0.</summary>
    public static int? Score(string label, string category, string query, string keywords = "")
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return 0;
        label ??= "";

        if (label.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (label.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 900 - Math.Min(label.Length, 99);

        var words = label.Split(new[] { ' ', '(', ')', ':', '.', '-', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(query, StringComparison.OrdinalIgnoreCase))) return 800 - Math.Min(label.Length, 99);
        if (label.Contains(query, StringComparison.OrdinalIgnoreCase)) return 700 - Math.Min(label.Length, 99);

        // Several words in any order: "pdf export" finds "Export PDF".
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var haystack = $"{label} {category} {keywords}";
        if (terms.Length > 1 && terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return 600 - Math.Min(label.Length, 99);

        if (!string.IsNullOrEmpty(keywords) && keywords.Contains(query, StringComparison.OrdinalIgnoreCase)) return 500;
        if (category?.StartsWith(query, StringComparison.OrdinalIgnoreCase) == true) return 400;
        if (IsSubsequence(label, query)) return 100;
        return null;
    }

    /// <summary>The items that match, best first; ties keep their original order.</summary>
    public static List<T> Rank<T>(IEnumerable<T> items, string query, Func<T, string> label, Func<T, string> category, Func<T, string>? keywords = null) =>
        items.Select((item, index) => (item, index, score: Score(label(item), category(item), query, keywords?.Invoke(item) ?? "")))
            .Where(x => x.score is not null)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();

    private static bool IsSubsequence(string text, string query)
    {
        var qi = 0;
        foreach (var ch in text)
        {
            if (qi < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[qi])) qi++;
        }
        return qi == query.Length;
    }
}
