using System.Text;
using System.Text.RegularExpressions;

namespace MdToPdf.Services;

// Rewrites the 2026-era Markdown dialect extensions — the syntax AI agents and modern tools
// (Obsidian, Notion exports, MkDocs/Docusaurus docs, GitHub) emit beyond CommonMark — into forms
// the core pipeline (Markdig + the HTML/DOCX exporters) can render properly, instead of leaking
// raw syntax at the reader:
//
//   [[Page]] / [[page|alias]]      wiki-links      -> styled <span class="wikilink">
//   #tag (inline, not headings)    hashtags        -> styled <span class="md-tag">
//   ```python title="train.py"    fence titles    -> caption line + clean ```python fence
//   ```ts:api.ts                   fence file tag  -> caption line + clean ```ts fence
//   === "Tab name" + indent       MkDocs tabs     -> bold labeled section + dedented body
//   <!-- pagebreak --> / \pagebreak / \newpage     -> <div class="page-break"></div>
//   table row glued to next text line              -> blank line inserted (Markdig, unlike
//                                                     GitHub, rejects the whole table otherwise)
//
// Like AdmonitionNormalizer, this runs on the raw Markdown before Markdig in BOTH the HTML/PDF and
// DOCX paths, and everything skips fenced-code regions so syntax examples stay literal.
public static class DialectNormalizer
{
    private static readonly Regex WikiLink = new(@"\[\[([^\[\]|\n]+)(?:\|([^\[\]\n]+))?\]\]", RegexOptions.Compiled);
    // #tag: after whitespace/line start, a letter first (never matches "# heading" — that has a
    // space after #), no URLs (#fragment follows non-space), and never a hex color — "#FF5733" in
    // prose is a color someone is talking about, not a tag (the lookahead rejects 3/4/6/8-digit
    // pure-hex tokens; a real tag like #q3-plan has non-hex characters and passes).
    private static readonly Regex HashTag = new(@"(?<=^|\s)#(?![0-9a-fA-F]{3,8}\b(?![\w/-]))([a-zA-Z][\w/-]{1,49})\b", RegexOptions.Compiled);
    private static readonly Regex FenceTitle = new("^(`{3,}|~{3,})\\s*([\\w+#-]+)?(?::([\\w./\\\\ -]+))?((?:\\s+\\w+=(?:\"[^\"]*\"|\\S+))*)\\s*$", RegexOptions.Compiled);
    private static readonly Regex TitleAttr = new("title=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex TabHeader = new("^===\\s+\"([^\"]+)\"\\s*$", RegexOptions.Compiled);
    private static readonly Regex PageBreak = new(@"^\s*(<!--\s*page\s*-?break\s*-->|\\pagebreak|\\newpage|<div[^>]*page-break-after[^>]*>\s*(</div>)?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InlineCode = new("`[^`\n]+`", RegexOptions.Compiled);

    public static string Apply(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        var lines = markdown.Split('\n');
        var output = new List<string>(lines.Length + 16);
        bool inCode = false;
        string? fenceMarker = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // ---- fenced code regions: pass through untouched (but rewrite the OPENING line's
            // title/filename annotation into a caption before it) ----
            if (!inCode)
            {
                var fm = FenceTitle.Match(trimmed);
                if (fm.Success && (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")))
                {
                    inCode = true;
                    fenceMarker = fm.Groups[1].Value[..3];
                    var lang = fm.Groups[2].Value;
                    var fileTag = fm.Groups[3].Value;                     // ```ts:api.ts form
                    var titleAttr = TitleAttr.Match(fm.Groups[4].Value);   // title="train.py" form
                    var caption = titleAttr.Success ? titleAttr.Groups[1].Value
                        : fileTag.Length > 0 ? fileTag.Trim() : null;

                    if (caption is not null)
                    {
                        // Caption as a raw-HTML line; blank line after so the HTML block doesn't
                        // swallow the fence itself (HTML blocks run until a blank line).
                        output.Add($"<div class=\"code-title\">{System.Net.WebUtility.HtmlEncode(caption)}</div>");
                        output.Add("");
                        output.Add($"{fenceMarker}{lang}"); // clean fence: language only
                        continue;
                    }
                    output.Add(line);
                    continue;
                }
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inCode = true;
                    fenceMarker = trimmed[..3];
                    output.Add(line);
                    continue;
                }
            }
            else
            {
                if (fenceMarker is not null && trimmed.StartsWith(fenceMarker)) { inCode = false; fenceMarker = null; }
                output.Add(line);
                continue;
            }

            // ---- page break markers -> a div both exporters understand (HTML: visible marker in
            // preview + page-break-after for print; DOCX: a real w:br type=page) ----
            if (PageBreak.IsMatch(line))
            {
                output.Add("<div class=\"page-break\"></div>");
                continue;
            }

            // ---- MkDocs content tabs: === "Name" + 4-space-indented body -> bold label line +
            // dedented body (sequential sections — a static document can't switch tabs) ----
            var tab = TabHeader.Match(line);
            if (tab.Success)
            {
                if (output.Count > 0 && output[^1].Length > 0) output.Add("");
                output.Add($"<div class=\"tab-label\">{System.Net.WebUtility.HtmlEncode(tab.Groups[1].Value)}</div>");
                output.Add("");
                int j = i + 1;
                while (j < lines.Length)
                {
                    if (lines[j].Length == 0) { output.Add(""); j++; continue; }
                    if (lines[j].StartsWith("    ") || lines[j].StartsWith("\t"))
                    {
                        output.Add(lines[j].StartsWith("\t") ? lines[j][1..] : lines[j][4..]);
                        j++;
                        continue;
                    }
                    break;
                }
                i = j - 1;
                continue;
            }

            // ---- wiki-links and #tags (inline, code-span-guarded) ----
            line = ReplaceOutsideInlineCode(line, WikiLink, m =>
            {
                var target = m.Groups[1].Value.Trim();
                var alias = m.Groups[2].Success ? m.Groups[2].Value.Trim() : target;
                return $"<span class=\"wikilink\">{System.Net.WebUtility.HtmlEncode(alias)}</span>";
            });
            line = ReplaceOutsideInlineCode(line, HashTag, m =>
                $"<span class=\"md-tag\">#{m.Groups[1].Value}</span>");

            // ---- table tolerance: Markdig (unlike GitHub) rejects a whole pipe table when a
            // non-table text line is glued directly under the last row — separate them ----
            output.Add(line);
            if (trimmed.StartsWith('|') && i + 1 < lines.Length)
            {
                var next = lines[i + 1].TrimStart();
                if (next.Length > 0 && !next.StartsWith('|'))
                {
                    // only when we're actually in a table (previous line was a row too)
                    if (output.Count >= 2 && output[^2].TrimStart().StartsWith('|'))
                        output.Add("");
                }
            }
        }

        return string.Join("\n", output);
    }

    // Applies `rx` replacements to the parts of a single line NOT inside `inline code` spans.
    private static string ReplaceOutsideInlineCode(string line, Regex rx, MatchEvaluator evaluator)
    {
        if (!rx.IsMatch(line)) return line;
        var codeSpans = InlineCode.Matches(line);
        if (codeSpans.Count == 0) return rx.Replace(line, evaluator);

        var sb = new StringBuilder(line.Length + 32);
        int pos = 0;
        foreach (Match code in codeSpans)
        {
            sb.Append(rx.Replace(line[pos..code.Index], evaluator));
            sb.Append(code.Value);
            pos = code.Index + code.Length;
        }
        sb.Append(rx.Replace(line[pos..], evaluator));
        return sb.ToString();
    }
}
