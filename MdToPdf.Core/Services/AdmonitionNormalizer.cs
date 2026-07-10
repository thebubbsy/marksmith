using System.Text.RegularExpressions;

namespace MdToPdf.Services;

// Rewrites the ::: fenced-admonition syntax (Docusaurus, Obsidian, GitBook, Nextra, Starlight,
// MkDocs-material's alternative form — all increasingly common in AI-generated Markdown) into the
// GitHub-alert blockquote form the app already styles as a callout box in BOTH the PDF/preview
// (MarkdownHtmlService) and DOCX (DocxExportService) paths via UseAlertBlocks. Reusing that proven
// rendering means a `:::warning` gets the same bordered, tinted, icon'd box as a `> [!WARNING]`,
// with no new per-exporter rendering code.
//
//   :::tip Optional title            > [!TIP]
//   Be careful here.        ─────►    > **Optional title**
//   :::                               > Be careful here.
//
// GitHub alerts only define five kinds (NOTE/TIP/IMPORTANT/WARNING/CAUTION); the much larger set of
// admonition names the various ecosystems use is mapped onto the closest one so nothing renders as
// an unstyled leftover. Only recognized admonition names are touched — an unknown `:::foo` is left
// exactly as-is so a genuine custom container isn't hijacked.
public static class AdmonitionNormalizer
{
    private static readonly Dictionary<string, string> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["note"] = "NOTE", ["info"] = "NOTE", ["abstract"] = "NOTE", ["summary"] = "NOTE",
        ["tldr"] = "NOTE", ["question"] = "NOTE", ["help"] = "NOTE", ["faq"] = "NOTE",
        ["quote"] = "NOTE", ["cite"] = "NOTE", ["example"] = "NOTE", ["seealso"] = "NOTE",
        ["tip"] = "TIP", ["hint"] = "TIP", ["success"] = "TIP", ["check"] = "TIP",
        ["done"] = "TIP", ["important"] = "IMPORTANT",
        ["warning"] = "WARNING", ["caution"] = "WARNING", ["attention"] = "WARNING",
        ["danger"] = "CAUTION", ["error"] = "CAUTION", ["bug"] = "CAUTION",
        ["failure"] = "CAUTION", ["fail"] = "CAUTION", ["missing"] = "CAUTION", ["deprecated"] = "CAUTION",
    };

    // Opener: leading colons, a type word, then an optional title as `[Title]` (Docusaurus 3) or a
    // trailing bare/quoted title (Docusaurus 2 / Obsidian). Closer: a line of only colons.
    private static readonly Regex Opener = new(@"^\s*:::+\s*([A-Za-z]+)\s*(?:\[(?<t1>[^\]]*)\]|""(?<t2>[^""]*)""|\s+(?<t3>\S.*?))?\s*$");
    private static readonly Regex Closer = new(@"^\s*:::+\s*$");

    public static string Apply(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains(":::", StringComparison.Ordinal))
            return markdown;

        var lines = markdown.Split('\n');
        var outLines = new List<string>(lines.Length + 8);
        bool inCode = false;
        string? fence = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Never touch anything inside a fenced code block — a ::: in a code sample is literal.
            if (!inCode && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                inCode = true;
                fence = trimmed.StartsWith("```", StringComparison.Ordinal) ? "```" : "~~~";
                outLines.Add(line);
                continue;
            }
            if (inCode)
            {
                if (fence is not null && trimmed.StartsWith(fence, StringComparison.Ordinal)) { inCode = false; fence = null; }
                outLines.Add(line);
                continue;
            }

            var m = Opener.Match(line);
            if (m.Success && TypeMap.TryGetValue(m.Groups[1].Value, out var alertType))
            {
                var title = m.Groups["t1"].Success ? m.Groups["t1"].Value
                    : m.Groups["t2"].Success ? m.Groups["t2"].Value
                    : m.Groups["t3"].Success ? m.Groups["t3"].Value.Trim()
                    : "";

                // Gather body up to the closing ::: (or end of document if the author forgot it).
                var body = new List<string>();
                int j = i + 1;
                while (j < lines.Length && !Closer.IsMatch(lines[j])) body.Add(lines[j++]);
                i = j; // the for-loop's i++ then steps past the closing ::: (or lands at EOF)

                if (outLines.Count > 0 && outLines[^1].Length > 0) outLines.Add(""); // ensure blockquote starts a new block
                outLines.Add($"> [!{alertType}]");
                if (!string.IsNullOrWhiteSpace(title)) outLines.Add($"> **{title}**");
                foreach (var b in body) outLines.Add(b.Length == 0 ? ">" : "> " + b);
                outLines.Add("");
                continue;
            }

            outLines.Add(line);
        }

        return string.Join("\n", outLines);
    }
}
