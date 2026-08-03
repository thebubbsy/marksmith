using System.Text.RegularExpressions;

namespace MarkSmith.Services;

// ISS-002: explicit `::emoji_name::` double-colon shortcode support. Markdig's UseEmojiAndSmiley
// already converts the single-colon `:rocket:` form in body text (and leaves code blocks alone);
// this adds the double-colon trigger some AI outputs emit. Only names present in the map are
// replaced, so prose such as `std::vector::foo` or an unknown `::notanemoji::` passes through
// untouched. Runs on non-code lines via DialectNormalizer, so fenced code is preserved.
public static partial class EmojiReplacer
{
    private static readonly Dictionary<string, string> EmojiMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rocket"] = "🚀", ["smile"] = "😄", ["heart"] = "❤️", ["fire"] = "🔥",
        ["party"] = "🎉", ["bulb"] = "💡", ["zap"] = "⚡", ["globe"] = "🌐",
    };

    [GeneratedRegex(@"::([a-zA-Z0-9_+-]+)::")]
    private static partial Regex DoubleColonEmojiRe();

    public static string ReplaceDoubleColonShortcodes(string markdown)
    {
        return DoubleColonEmojiRe().Replace(markdown, m =>
        {
            var key = m.Groups[1].Value;
            return EmojiMap.TryGetValue(key, out var emoji) ? emoji : m.Value;
        });
    }

    // Single-match variant for callers (e.g. DialectNormalizer) that replace outside inline-code
    // spans themselves: returns the emoji for a known shortcode key, otherwise the supplied
    // fallback (the original matched text) so unknown shortcodes stay literal.
    public static string ReplaceShortcode(string key, string fallback) =>
        EmojiMap.TryGetValue(key, out var emoji) ? emoji : fallback;
}
