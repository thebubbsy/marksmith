using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Models;

namespace MarkSmith.Services;

// Personalizes Markdown *structure* rather than just cleaning it: shift every heading up or down a
// level, and rewrite bold / italic emphasis. AI output isn't badly formatted — it's uniformly
// formatted — so this lets you restyle it into your own voice. All transforms run only OUTSIDE
// code (fenced blocks and inline spans), so `**ptr` in a code sample and a `# comment` stay literal.
public static partial class FormattingService
{
    public const int Keep = 0;
    public const int Remove = 1;
    public const int ToItalic = 2;   // bold only

    // Splits into alternating prose / code segments; captured code sits at odd indices.
    [GeneratedRegex(@"(```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\n]*`)")]
    private static partial Regex CodeSplit();

    [GeneratedRegex(@"(?m)^(#{1,6})(\s)")] private static partial Regex Heading();
    [GeneratedRegex(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Singleline)] private static partial Regex Bold();
    [GeneratedRegex(@"(?<!\*)\*(?!\s)(.+?)(?<!\s)\*(?!\*)")] private static partial Regex ItalicStar();
    [GeneratedRegex(@"(?<![A-Za-z0-9_])_(?!_)(.+?)(?<!_)_(?![A-Za-z0-9_])")] private static partial Regex ItalicUnderscore();

    public static string Apply(string markdown, AppSettings s)
    {
        if (s.HeadingShift == 0 && s.BoldMode == Keep && s.ItalicMode == Keep) return markdown;

        var parts = CodeSplit().Split(markdown);
        var sb = new StringBuilder(markdown.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1) { sb.Append(parts[i]); continue; } // code segment — untouched
            sb.Append(TransformProse(parts[i], s));
        }
        return sb.ToString();
    }

    private static string TransformProse(string text, AppSettings s)
    {
        if (s.HeadingShift != 0)
        {
            text = Heading().Replace(text, m =>
            {
                var level = Math.Clamp(m.Groups[1].Value.Length + s.HeadingShift, 1, 6);
                return new string('#', level) + m.Groups[2].Value;
            });
        }

        // Bold first — turning bold→italic can create emphasis the italic pass then acts on,
        // which is the intended, composable behaviour. EXCEPTION: BoldMode=ToItalic + ItalicMode=Remove
        // is self-defeating (bold→italic→strip deletes all bold content), so we collapse it to
        // "remove bold" — the user's combined intent is "no bold, no italic".
        var effectiveBold = (s.BoldMode == ToItalic && s.ItalicMode == Remove) ? Remove : s.BoldMode;
        if (effectiveBold == Remove)
            text = Bold().Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        else if (effectiveBold == ToItalic)
            text = Bold().Replace(text, m => "*" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "*");

        if (s.ItalicMode == Remove)
        {
            text = ItalicStar().Replace(text, "$1");
            text = ItalicUnderscore().Replace(text, "$1");
        }

        return text;
    }
}
