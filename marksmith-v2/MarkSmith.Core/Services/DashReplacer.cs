using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

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

    // Group 1 = protected spans (HTML comments, HTML tags/attributes, autolinks, inline code,
    // LaTeX math, Markdown link URLs, bare URLs, table delimiters);
    // otherwise a double-hyphen (?<!-)--(?!-) is matched for replacement with native em-dash (—).
    [GeneratedRegex(@"(<!--[\s\S]*?-->|</?[a-zA-Z!?:][^>]*>|`+[^`\n]*?`+|\$\$[\s\S]*?\$\$|\$(?!\s)[^\$\n]*?\S\$|\]\([^)]*\)|https?://[^\s<>""'\(\)]+|(?m)^\s*\|?[\s|:-]+\|?\s*$)|(?<!-)--(?!-)")]
    private static partial Regex DoubleHyphenRegex();

    public static string NormalizeDoubleHyphens(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        markdown = TextNormalizer.Newlines(markdown);
        // Fast-path: the transform only fires on a bare -- (not ---), so skip the
        // split/process/join entirely when the document has no double-hyphen at all.
        if (!markdown.Contains("--", StringComparison.Ordinal)) return markdown;

        var lines = markdown.Split('\n');
        var result = new List<string>(lines.Length);

        bool inCode = false;
        char fenceChar = '\0';
        int fenceLength = 0;

        List<string> proseLines = new();

        void FlushProse()
        {
            if (proseLines.Count == 0) return;

            string proseText = string.Join("\n", proseLines);
            proseLines.Clear();

            string processed = DoubleHyphenRegex().Replace(proseText, m =>
                m.Groups[1].Success ? m.Value : "—");

            result.AddRange(processed.Split('\n'));
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (!inCode)
            {
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    fenceChar = trimmed[0];
                    int count = 0;
                    while (count < trimmed.Length && trimmed[count] == fenceChar)
                    {
                        count++;
                    }

                    if (count >= 3)
                    {
                        FlushProse();
                        inCode = true;
                        fenceLength = count;
                        result.Add(line);
                        continue;
                    }
                }

                if (line.StartsWith("    ") || line.StartsWith("\t"))
                {
                    FlushProse();
                    result.Add(line);
                    continue;
                }

                proseLines.Add(line);
            }
            else
            {
                result.Add(line);

                if (trimmed.StartsWith(new string(fenceChar, fenceLength)))
                {
                    inCode = false;
                    fenceChar = '\0';
                    fenceLength = 0;
                }
            }
        }

        FlushProse();

        return string.Join("\n", result);
    }

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


