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
    // ISS-002: ::emoji_name:: double-colon shortcode (single-colon :emoji: is Markdig's job). The
    // map lookup in EmojiReplacer ignores unknown names, so std::vector::foo passes through.
    private static readonly Regex DoubleColonEmoji = new(@"::([a-zA-Z0-9_+-]+)::", RegexOptions.Compiled);
    private static readonly Regex FenceTitle = new("^(`{3,}|~{3,})\\s*([\\w+#-]+)?(?::([\\w./\\\\ -]+))?((?:\\s+\\w+=(?:\"[^\"]*\"|\\S+))*)\\s*$", RegexOptions.Compiled);
    private static readonly Regex TitleAttr = new("title=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex TabHeader = new("^===\\s+\"([^\"]+)\"\\s*$", RegexOptions.Compiled);
    private static readonly Regex PageBreak = new(@"^\s*(<!--\s*page\s*-?break\s*-->|\\pagebreak|\\newpage|<div[^>]*page-break-after[^>]*>\s*(</div>)?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"(`+)[^\n]*?\1", RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new(@"</?[a-zA-Z!][^>]*>|<!--.*?-->", RegexOptions.Compiled);
    private static readonly Regex DefinitionList = new(@"^(\s*):\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex CriticSub = new(@"\{~~(?!=)((?:(?!~>|~~\}).)+)\~>~?((?:(?!~~\}).)+)\~~\}", RegexOptions.Compiled);
    private static readonly Regex CriticDel = new(@"\{~~((?:(?!~~\}).)+)\~~\}", RegexOptions.Compiled);
    private static readonly Regex CriticIns = new(@"\{\+\+((?:(?!\+\+\}).)+)\+\+\}", RegexOptions.Compiled);
    private static readonly Regex CriticHl  = new(@"\{==((?:(?!==\}).)+)==\}", RegexOptions.Compiled);
    private static readonly Regex CriticComment = new(@"\{>>((?:(?!<<\}).)*)\<<\}", RegexOptions.Compiled);

    public static string Apply(string markdown) => Apply(markdown, -1);

    public static string Apply(string markdown, int dashMode)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        if (dashMode == -1 || dashMode != DashReplacer.Keep)
        {
            markdown = DashReplacer.NormalizeDoubleHyphens(markdown);
        }

        var lines = markdown.Split('\n');
        var output = new List<string>(lines.Length + 16);
        bool inCode = false;
        string? fenceMarker = null;

        bool inTabsBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (!inCode && trimmed.StartsWith(":::tabs", StringComparison.OrdinalIgnoreCase))
            {
                inTabsBlock = true;
                output.Add(line);
                continue;
            }
            if (inTabsBlock)
            {
                if (trimmed == ":::") { inTabsBlock = false; }
                output.Add(line);
                continue;
            }

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

            // ---- definition lists ----
            // Markdig requires 3+ spaces after the colon. AI often outputs 1 space.
            var dlMatch = DefinitionList.Match(line);
            if (dlMatch.Success)
            {
                output.Add(dlMatch.Groups[1].Value + ":   " + dlMatch.Groups[2].Value);
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

            // ---- ISS-002: ::emoji:: double-colon shortcodes (inline-code-guarded; fenced code is
            // already skipped by the inCode pass above) ----
            line = ReplaceOutsideInlineCode(line, DoubleColonEmoji, m =>
                EmojiReplacer.ReplaceShortcode(m.Groups[1].Value, m.Value));

            // ---- CriticMarkup syntax normalization ({++ins++}, {~~del~~}, {==hl==}, {~~old~>~new~~}, {>>comment<<}) ----
            line = ReplaceOutsideInlineCode(line, CriticSub, m => $"<del>{m.Groups[1].Value}</del><ins>{m.Groups[2].Value}</ins>");
            line = ReplaceOutsideInlineCode(line, CriticDel, m => $"<del>{m.Groups[1].Value}</del>");
            line = ReplaceOutsideInlineCode(line, CriticIns, m => $"<ins>{m.Groups[1].Value}</ins>");
            line = ReplaceOutsideInlineCode(line, CriticHl,  m => $"<mark>{m.Groups[1].Value}</mark>");
            line = ReplaceOutsideInlineCode(line, CriticComment, m => "");

            // ---- table delimiter line normalization: e.g. |--:| -> |---:| ----
            if (trimmed.StartsWith('|') && Regex.IsMatch(trimmed, @"^\|[\s|:\-]+$"))
            {
                line = line.Replace("--:", "---:").Replace(":--", ":---");
            }

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

        // ---- clean up orphaned grid borders (+---+) under pipe tables ----
        for (int i = 1; i < output.Count; i++)
        {
            var line = output[i].Trim();
            if (line.StartsWith('+') && line.EndsWith('+') && (line.Contains('-') || line.Contains('=')))
            {
                var prev = output[i - 1].TrimStart();
                if (prev.StartsWith('|'))
                {
                    bool isGridTable = false;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        var pLine = output[j].TrimStart();
                        if (pLine.StartsWith('+')) { isGridTable = true; break; }
                        if (!pLine.StartsWith('|')) { break; }
                    }
                    if (!isGridTable)
                    {
                        output[i] = "";
                    }
                }
            }
        }

        return string.Join("\n", output);
    }

    // ISS-015: rewrite MkDocs-style `=== "Tab"` blocks into an interactive tab group
    // (.md-tab-group / .md-tab-nav / .md-tab-content) for outputs that can host live JS (the HTML
    // preview). Each tab's body is emitted verbatim, so callers that need the body rendered as
    // Markdown should run this on already-rendered fragments or post-process the content divs.
    //
    // NOTE: deliberately NOT wired into Apply(). The default pipeline (preview + PDF + DOCX) uses
    // the sequential bold-label rendering above — that path is covered by Content_tabs_become_labels
    // and works in every output, whereas this interactive form relies on inline onclick handlers
    // that HtmlSanitizer strips. Kept as a building block for a future sanitizer-safe tab strip.
    public static string NormalizeTabbedBlocks(string markdown)
    {
        return Regex.Replace(markdown, "===\\s*\"([^\"]+)\"\\r?\\n([\\s\\S]*?)(?=(===\\s*\"|$))", m =>
        {
            var title = m.Groups[1].Value;
            var content = m.Groups[2].Value.Trim();
            var tabId = "tab-" + Guid.NewGuid().ToString("n")[..6];

            return $"""
                <div class="md-tab-group">
                    <div class="md-tab-nav">
                        <button class="md-tab-link active" onclick="selectMdTab(this, '{tabId}')">{title}</button>
                    </div>
                    <div class="md-tab-content active" id="{tabId}">
                        {content}
                    </div>
                </div>
                """;
        });
    }

    // Applies `rx` replacements to the parts of a single line NOT inside `inline code` spans or HTML tags.
    private static string ReplaceOutsideInlineCode(string line, Regex rx, MatchEvaluator evaluator)
    {
        if (!rx.IsMatch(line)) return line;
        
        var codeSpans = InlineCode.Matches(line);
        var htmlSpans = HtmlTag.Matches(line);
        if (codeSpans.Count == 0 && htmlSpans.Count == 0) return rx.Replace(line, evaluator);

        var protectedRanges = new List<(int Start, int End)>();
        foreach (Match m in codeSpans) protectedRanges.Add((m.Index, m.Index + m.Length));
        foreach (Match m in htmlSpans) protectedRanges.Add((m.Index, m.Index + m.Length));

        protectedRanges.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(int Start, int End)>();
        foreach (var range in protectedRanges)
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
            }
            else
            {
                var last = merged[^1];
                if (range.Start < last.End)
                {
                    merged[^1] = (last.Start, Math.Max(last.End, range.End));
                }
                else
                {
                    merged.Add(range);
                }
            }
        }

        var sb = new StringBuilder(line.Length + 32);
        int pos = 0;
        foreach (var range in merged)
        {
            if (range.Start > pos)
            {
                sb.Append(rx.Replace(line[pos..range.Start], evaluator));
            }
            sb.Append(line[range.Start..range.End]);
            pos = range.End;
        }
        if (pos < line.Length)
        {
            sb.Append(rx.Replace(line[pos..], evaluator));
        }

        return sb.ToString();
    }
}
