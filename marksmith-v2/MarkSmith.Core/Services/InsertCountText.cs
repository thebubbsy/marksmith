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
}
