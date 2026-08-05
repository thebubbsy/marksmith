using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public static partial class EmojiShortcodeService
{
    private static readonly Dictionary<string, string> EmojiMap = new(System.StringComparer.OrdinalIgnoreCase)
    {
        { "rocket", "🚀" },
        { "smile", "😄" },
        { "sparkles", "✨" },
        { "check", "✅" },
        { "warning", "⚠️" },
        { "fire", "🔥" },
        { "tada", "🎉" },
        { "heart", "❤️" },
        { "thumbsup", "👍" },
        { "bulb", "💡" },
        { "mag", "🔍" },
        { "zap", "⚡" }
    };

    [GeneratedRegex(@"::([a-z0-9_+-]+)::", RegexOptions.IgnoreCase)]
    private static partial Regex ShortcodeRe();

    public static string Expand(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown;

        return ShortcodeRe().Replace(markdown, match =>
        {
            var shortcode = match.Groups[1].Value;
            if (EmojiMap.TryGetValue(shortcode, out var emoji))
            {
                return emoji;
            }
            return match.Value; // Keep unchanged if not found
        });
    }
}