namespace MarkSmith.Services;

/// <summary>
/// List type-ahead: which row a typed prefix lands on. Typing "be" goes to the first row starting
/// with "be"; typing one letter again and again ("b", "b") steps through the rows starting with it.
/// </summary>
public static class TypeAheadSearch
{
    /// <summary>Keys further apart than this start a new prefix.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// The prefix after typing <paramref name="key"/>: added to <paramref name="prefix"/> when it
    /// came within <see cref="Pause"/> of the last key, otherwise a fresh one-letter prefix.
    /// </summary>
    public static string Extend(string prefix, char key, TimeSpan sinceLastKey) =>
        sinceLastKey > Pause ? key.ToString() : prefix + key;

    /// <summary>
    /// The index of the row <paramref name="prefix"/> lands on, or -1. <paramref name="current"/> is
    /// the highlighted row (-1 for none). A one-letter prefix, or one letter repeated, looks from
    /// the row after it so repeated presses cycle; a longer prefix may stay on the current row.
    /// Matching ignores case. Null or empty names never match.
    /// </summary>
    public static int Find(IReadOnlyList<string?> names, int current, string prefix)
    {
        if (names.Count == 0 || string.IsNullOrEmpty(prefix)) return -1;
        var cycling = prefix.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(prefix[0]));
        var search = cycling ? prefix[..1] : prefix;
        var start = current < 0 || current >= names.Count ? 0 : cycling ? current + 1 : current;
        for (var i = 0; i < names.Count; i++)
        {
            var index = (start + i) % names.Count;
            if (names[index] is { Length: > 0 } name && name.StartsWith(search, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return -1;
    }
}
