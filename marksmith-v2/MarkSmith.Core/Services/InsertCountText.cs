namespace MarkSmith.Services;

// The count badge and the "add more" hint of the one-entry-per-line Insert dialogs (workflow,
// timeline, tabs, data grid). The data grid's first line is its header, but the badge counted it
// as a row ("3 rows" for a header and two rows) and the hint asked for "at least 2 rows" when it
// meant a header and one row.
public static class InsertCountText
{
    public static string Lines(int lines, string noun, bool headerFirst = false)
    {
        if (!headerFirst) return CountText.Of(lines, noun);
        if (lines == 0) return CountText.Of(0, noun);
        return $"Header + {CountText.Of(lines - 1, noun)}";
    }

    // The box's help text: the count (from Lines), the shape a line takes, and the key that inserts
    // from a box where Enter only starts a new line. The rule was in the heading and the red hint
    // alone, and Ctrl+Enter nowhere a keyboard user could reach.
    public static string BoxHelp(string count, string noun, string? lineHint = null, bool headerFirst = false)
    {
        var rule = lineHint?.Trim().TrimEnd('.') is { Length: > 0 } hint
            ? char.ToUpperInvariant(hint[0]) + hint[1..]
            : headerFirst
                ? $"Header line first, then one {noun} per line, values separated by commas"
                : $"One {noun} per line";
        return $"{count}. {rule}. Ctrl+Enter inserts.";
    }

    public static string Shortfall(int minimum, string noun, bool headerFirst = false)
    {
        if (headerFirst)
        {
            var rows = System.Math.Max(1, minimum - 1);
            return rows == 1
                ? $"Add a header line and at least one {noun} under it."
                : $"Add a header line and at least {rows} {CountText.Noun(rows, noun)} under it.";
        }
        return minimum == 1 ? $"Add at least one {noun}." : $"Add at least {minimum} {CountText.Noun(minimum, noun)}.";
    }
    // More entries than the block can show: the preview draws at most 16 tabs and dropped the
    // rest without a word, so the dialog inserted tabs nobody could ever open.
    public static string Excess(int maximum, int count, string noun) =>
        $"At most {CountText.Of(maximum, noun)} fit; remove {CountText.Of(count - maximum, noun)}.";

    // Help text for the dialogs' number boxes (table size, column count, canvas and map size).
    // Their range was only in the heading and the keys that change the value nowhere at all.
    public static string NumberHelp(double minimum, double maximum, double smallChange = 1, double largeChange = 5) =>
        $"From {minimum:0} to {maximum:0}. Up and Down change it by {smallChange:0}, Page Up and Page Down by {largeChange:0}. Ctrl+Enter inserts.";

    // A citation id is cited as [@id], and every citation pattern in the app reads letters,
    // digits, '-' and '_' only, so "smith 2024" or "smith.2024" made an entry nothing could cite.
    public static string? CitationIdProblem(string? id)
    {
        foreach (var c in id ?? "")
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_') continue;
            var what = char.IsWhiteSpace(c) ? "a space" : $"“{c}”";
            return $"A citation id can only use letters, digits, - and _ (this one has {what}).";
        }
        return null;
    }

    // A backtick in the info string stops a ``` line being a fence at all, so the block (and
    // everything after it) rendered as plain text.
    public static string? CodeLanguageProblem(string? language) =>
        (language ?? "").Contains('`') ? "A language name can't contain a backtick (`)." : null;

    // The video dialog picks the provider from the pasted link; changing it afterwards made a
    // card with the wrong name and colour ("Vimeo · youtube.com"). Only providers the dialog
    // offers are checked, so a link it can't name still inserts as before.
    public static string? EmbedProviderProblem(string? picked, string? url, IEnumerable<string> offered)
    {
        var detected = ContainerBlockParsers.DetectProvider(url ?? "");
        if (detected is null || string.Equals(detected, picked, StringComparison.OrdinalIgnoreCase)
            || !offered.Contains(detected, StringComparer.OrdinalIgnoreCase)) return null;
        var name = ContainerBlockParsers.ProviderDisplayName(detected);
        return $"That's a {name} link, but {ContainerBlockParsers.ProviderDisplayName(picked ?? "")} is picked; pick {name}.";
    }
}
