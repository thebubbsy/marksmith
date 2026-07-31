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

    // Opener: leading colons or exclamations, a type word, then an optional title
    private static readonly Regex Opener = new(@"^\s*:::+\s*([A-Za-z]+)\s*(?:\[(?<t1>[^\]]*)\]|""(?<t2>[^""]*)""|\s+(?<t3>\S.*?))?\s*$");
    private static readonly Regex PyOpener = new(@"^\s*!!!\s*([A-Za-z]+)(?:\s+""(?<t1>[^""]*)""|\s+(?<t2>\S.*?))?\s*$");
    private static readonly Regex Closer = new(@"^\s*:::+\s*$");

    // Obsidian's foldable-callout variants: `> [!tip]- Title` (collapsed) / `> [!tip]+ Title`
    // (expanded). Markdig's alert parser only recognizes the plain `> [!TIP]` form and leaks the
    // suffixed ones as blockquote text. Rewrite them to a real <details>/<summary> element so the
    // preview gets genuine browser-native collapse behaviour — folded closed by default for `-`,
    // open for `+`, and toggled on click — with the body kept as markdown (blank lines around it
    // so Markdig still parses **bold**, code, math inside). Group 3 captures the fold char.
    private static readonly Regex FoldedCallout = new(@"^(\s*)>\s*\[!([A-Za-z]+)\]([-+])\s*(.*)$");

    private static readonly Regex BlockquoteStartRx = new(@"^\s*>", RegexOptions.Compiled);
    private static readonly Regex StripBlockquoteRx = new(@"^\s*>\s?", RegexOptions.Compiled);

    public static string Apply(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;
        if (!markdown.Contains(":::", StringComparison.Ordinal) && !markdown.Contains("[!", StringComparison.Ordinal) && !markdown.Contains("!!!", StringComparison.Ordinal))
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

            var folded = FoldedCallout.Match(line);
            if (folded.Success)
            {
                var kind = TypeMap.TryGetValue(folded.Groups[2].Value, out var mapped) ? mapped : folded.Groups[2].Value.ToUpperInvariant();
                var startOpen = folded.Groups[3].Value == "+";  // `+` = expanded, `-` = collapsed
                var foldTitle = folded.Groups[4].Value.Trim();

                // Consume the rest of the blockquote as the callout body, stripping the "> " marker.
                var body = new List<string>();
                int j = i + 1;
                while (j < lines.Length && BlockquoteStartRx.IsMatch(lines[j]))
                {
                    body.Add(StripBlockquoteRx.Replace(lines[j], ""));
                    j++;
                }
                i = j - 1; // the for-loop's i++ lands on the first non-blockquote line

                var summary = string.IsNullOrEmpty(foldTitle) ? kind : $"{kind} · {foldTitle}";
                if (outLines.Count > 0 && outLines[^1].Length > 0) outLines.Add("");
                outLines.Add($"<details class=\"md-callout md-callout-{kind.ToLowerInvariant()}\"{(startOpen ? " open" : "")}>");
                outLines.Add($"<summary>{System.Net.WebUtility.HtmlEncode(summary)}</summary>");
                outLines.Add("");                     // blank line → body parsed as markdown, not raw HTML
                foreach (var b in body) outLines.Add(b);
                outLines.Add("");
                outLines.Add("</details>");
                outLines.Add("");
                continue;
            }

            var m = Opener.Match(line);
            var pyM = PyOpener.Match(line);

            if (m.Success && m.Groups[1].Value.Equals("toggle", StringComparison.OrdinalIgnoreCase))
            {
                var title = m.Groups["t1"].Success ? m.Groups["t1"].Value
                    : m.Groups["t2"].Success ? m.Groups["t2"].Value
                    : m.Groups["t3"].Success ? m.Groups["t3"].Value.Trim()
                    : "";
                if (string.IsNullOrWhiteSpace(title)) title = "Toggle";

                var body = new List<string>();
                int j = i + 1;
                bool bodyInCode = false;
                string? bodyFence = null;
                int depth = 0;
                while (j < lines.Length)
                {
                    var bodyLine = lines[j];
                    var bodyTrimmed = bodyLine.TrimStart();
                    if (!bodyInCode && (bodyTrimmed.StartsWith("```", StringComparison.Ordinal) || bodyTrimmed.StartsWith("~~~", StringComparison.Ordinal)))
                    {
                        bodyInCode = true;
                        bodyFence = bodyTrimmed.StartsWith("```", StringComparison.Ordinal) ? "```" : "~~~";
                        body.Add(bodyLine);
                        j++;
                        continue;
                    }
                    if (bodyInCode)
                    {
                        if (bodyFence is not null && bodyTrimmed.StartsWith(bodyFence, StringComparison.Ordinal))
                        {
                            bodyInCode = false;
                            bodyFence = null;
                        }
                        body.Add(bodyLine);
                        j++;
                        continue;
                    }

                    if (Opener.IsMatch(bodyLine))
                    {
                        depth++;
                        body.Add(bodyLine);
                        j++;
                        continue;
                    }

                    if (Closer.IsMatch(bodyLine))
                    {
                        if (depth > 0)
                        {
                            depth--;
                            body.Add(bodyLine);
                            j++;
                            continue;
                        }
                        break;
                    }

                    body.Add(bodyLine);
                    j++;
                }
                i = j;

                if (outLines.Count > 0 && outLines[^1].Length > 0) outLines.Add("");
                outLines.Add("<details>");
                outLines.Add($"<summary>{System.Net.WebUtility.HtmlEncode(title)}</summary>");
                outLines.Add("");
                var innerNormalized = Apply(string.Join("\n", body));
                outLines.Add(innerNormalized);
                outLines.Add("");
                outLines.Add("</details>");
                outLines.Add("");
                continue;
            }

            if ((m.Success && TypeMap.TryGetValue(m.Groups[1].Value, out var alertType)) ||
                (pyM.Success && TypeMap.TryGetValue(pyM.Groups[1].Value, out alertType)))
            {
                var isPy = pyM.Success;
                var match = isPy ? pyM : m;
                var title = match.Groups["t1"].Success ? match.Groups["t1"].Value
                    : match.Groups["t2"].Success ? match.Groups["t2"].Value
                    : match.Groups["t3"].Success ? match.Groups["t3"].Value.Trim()
                    : "";

                var body = new List<string>();
                int j = i + 1;

                if (isPy)
                {
                    // For Python Markdown `!!! note Title`, if subsequent lines are indented by 4 spaces (or 2+ spaces), collect them as body.
                    // If no indented lines follow, treat the title itself (if any) as the body text if there's no quoted title.
                    while (j < lines.Length)
                    {
                        var pyLine = lines[j];
                        if (string.IsNullOrWhiteSpace(pyLine))
                        {
                            // Blank line inside indented block or terminating it
                            if (j + 1 < lines.Length && (lines[j + 1].StartsWith("    ") || lines[j + 1].StartsWith("\t") || lines[j + 1].StartsWith("  ")))
                            {
                                body.Add("");
                                j++;
                                continue;
                            }
                            break;
                        }

                        if (pyLine.StartsWith("    ") || pyLine.StartsWith("\t"))
                        {
                            body.Add(pyLine.StartsWith("\t") ? pyLine[1..] : pyLine[4..]);
                            j++;
                        }
                        else if (pyLine.StartsWith("  "))
                        {
                            body.Add(pyLine[2..]);
                            j++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    
                    // Handle single-line Python admonition: `!!! note Python Markdown style admonitions work too!`
                    if (body.Count == 0 && !string.IsNullOrWhiteSpace(title) && !match.Groups["t1"].Success && !match.Groups["t2"].Success)
                    {
                        body.Add(title);
                        title = "";
                    }

                    i = j - 1;
                }
                else
                {
                    // Gather body up to the closing ::: (or end of document if the author forgot it).
                    bool bodyInCode = false;
                    string? bodyFence = null;
                    while (j < lines.Length)
                    {
                        var bodyLine = lines[j];
                        var bodyTrimmed = bodyLine.TrimStart();
                        if (!bodyInCode && (bodyTrimmed.StartsWith("```", StringComparison.Ordinal) || bodyTrimmed.StartsWith("~~~", StringComparison.Ordinal)))
                        {
                            bodyInCode = true;
                            bodyFence = bodyTrimmed.StartsWith("```", StringComparison.Ordinal) ? "```" : "~~~";
                            body.Add(bodyLine);
                            j++;
                            continue;
                        }
                        if (bodyInCode)
                        {
                            if (bodyFence is not null && bodyTrimmed.StartsWith(bodyFence, StringComparison.Ordinal))
                            {
                                bodyInCode = false;
                                bodyFence = null;
                            }
                            body.Add(bodyLine);
                            j++;
                            continue;
                        }

                        if (Closer.IsMatch(bodyLine)) break;

                        body.Add(bodyLine);
                        j++;
                    }
                    i = j; // the for-loop's i++ then steps past the closing ::: (or lands at EOF)
                }

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
