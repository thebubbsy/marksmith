using System.Text.RegularExpressions;

namespace MdToPdf.Services;

// Replaces em-dashes (—) — the tell-tale AI-writing glyph — with a plainer dash or custom text.
// Code fences and inline code are preserved verbatim, so an em-dash inside a code sample stays
// literal. Horizontal whitespace immediately around the dash is collapsed so "a — b" doesn't
// become "a  -  b". Mirrors the EmojiStripper pattern and is applied at the same pipeline point.
public static partial class DashReplacer
{
    public const int Keep = 0;
    public const int Hyphen = 1;     // —  ->  -
    public const int Spaced = 2;     // —  ->  " - "
    public const int Custom = 3;     // —  ->  user string

    // Group 1 = a code fence or inline-code span (left untouched); otherwise an em-dash with any
    // surrounding spaces/tabs is matched for replacement.
    [GeneratedRegex(@"(```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\n]*`)|[ \t]*—[ \t]*")]
    private static partial Regex EmDashOutsideCode();

    public static string Apply(string markdown, int mode, string? custom)
    {
        if (mode == Keep || string.IsNullOrEmpty(markdown)) return markdown;

        var replacement = mode switch
        {
            Hyphen => "-",
            Spaced => " - ",
            Custom => custom ?? "",   // empty custom = delete the em-dash (and its padding)
            _ => "—",
        };

        return EmDashOutsideCode().Replace(markdown, m =>
            m.Groups[1].Success ? m.Value : replacement);
    }
}
