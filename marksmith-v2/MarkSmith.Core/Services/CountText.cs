namespace MarkSmith.Services;

// A count with the right noun: "1 table", "3 tables". Status lines and copy reports used to say
// "3 table(s)" and "1 image(s)", which reads like a template rather than a sentence.
public static class CountText
{
    // Formats the number with group separators ("1,204 lines"). plural defaults to singular + "s".
    public static string Of(int count, string singular, string? plural = null) =>
        $"{count:N0} {Noun(count, singular, plural)}";

    // Just the noun for a count, for sentences that place the number themselves.
    public static string Noun(int count, string singular, string? plural = null) =>
        count == 1 ? singular : plural ?? singular + "s";

    // An added / removed pair, each with its own noun: "+1 word / -3 words". History's diff pill
    // said "+1 / -0 words", one plural for two counts.
    public static string Delta(int added, int removed, string singular, string? plural = null) =>
        $"+{Of(added, singular, plural)} / -{Of(removed, singular, plural)}";

    // The same pair without a side that is zero: "+11 words", "-3 words", "+2 words / -1 word",
    // "no word changes". History's word pill read "+11 words / -0 words" for a first version.
    public static string Change(int added, int removed, string singular, string? plural = null) =>
        (added, removed) switch
        {
            (0, 0) => $"no {singular} changes",
            (_, 0) => $"+{Of(added, singular, plural)}",
            (0, _) => $"-{Of(removed, singular, plural)}",
            _ => Delta(added, removed, singular, plural),
        };
}
