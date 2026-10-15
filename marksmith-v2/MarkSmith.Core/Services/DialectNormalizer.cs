using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

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
public static partial class DialectNormalizer
{
    [GeneratedRegex(@"\[\[([^\[\]|\n]+)(?:\|([^\[\]\n]+))?\]\]")]
    private static partial Regex WikiLink();

    // #tag: after whitespace/line start, a letter first (never matches "# heading" — that has a
    // space after #), no URLs (#fragment follows non-space), and never a hex color — "#FF5733" in
    // prose is a color someone is talking about, not a tag (the lookahead rejects 3/4/6/8-digit
    // pure-hex tokens; a real tag like #q3-plan has non-hex characters and passes).
    [GeneratedRegex(@"(?<=^|\s)#(?![0-9a-fA-F]{3,8}\b(?![\w/-]))([a-zA-Z][\w/-]{1,49})\b")]
    private static partial Regex HashTag();

    // ISS-002: ::emoji_name:: double-colon shortcode (single-colon :emoji: is Markdig's job). The
    // map lookup in EmojiReplacer ignores unknown names, so std::vector::foo passes through.
    [GeneratedRegex(@"::([a-zA-Z0-9_+-]+)::")]
    private static partial Regex DoubleColonEmoji();

    [GeneratedRegex("^(`{3,}|~{3,})\\s*([\\w+#-]+)?(?::([\\w./\\\\ -]+))?((?:\\s+\\w+=(?:\"[^\"]*\"|\\S+))*)\\s*$")]
    private static partial Regex FenceTitle();

    [GeneratedRegex("title=\"([^\"]*)\"")]
    private static partial Regex TitleAttr();

    [GeneratedRegex("^===\\s+\"([^\"]+)\"\\s*$")]
    private static partial Regex TabHeader();

    [GeneratedRegex(@"^\s*(<!--\s*page\s*-?break\s*-->|\\pagebreak|\\newpage|<div[^>]*page-break-after[^>]*>\s*(</div>)?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageBreak();

    [GeneratedRegex(@"(`+)[^\n]*?\1")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"</?[a-zA-Z!][^>]*>|<!--.*?-->")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^(\s*):\s+(.*)$")]
    private static partial Regex DefinitionList();

    [GeneratedRegex(@"^\[\^[^\]\s]+\]:")]
    private static partial Regex FootnoteDefinition();

    [GeneratedRegex(@"\{==(?<text>(?:(?!==\}).)+)==\}\{>>(?<comment>(?:(?!<<\}).)*)<<\}")]
    private static partial Regex CriticHlComment();

    [GeneratedRegex(@"\{~~(?!=)((?:(?!~>|~~\}).)+)\~>~?((?:(?!~~\}).)+)\~~\}")]
    private static partial Regex CriticSub();

    [GeneratedRegex(@"\{(--|~~)((?:(?!--\}|~~\}).)+)\1\}")]
    private static partial Regex CriticDel();

    [GeneratedRegex(@"\{\+\+((?:(?!\+\+\}).)+)\+\+\}")]
    private static partial Regex CriticIns();

    [GeneratedRegex(@"\{==((?:(?!==\}).)+)==\}")]
    private static partial Regex CriticHl();

    [GeneratedRegex(@"\{>>(?<comment>(?:(?!<<\}).)*)<<\}")]
    private static partial Regex CriticComment();

    [GeneratedRegex(@"\^\[(?!(?:index|\^))\s*(?<author>[^:\]\n]+?)(?:\s*\((?<date>[^\)]+)\))?:\s*(?:[""“](?<comment>(?:[^""”\\]|\\.)*?)[""”]|(?<comment>[^\]\n]+))\s*\]")]
    private static partial Regex ReviewerComment();

    // Pandoc inline footnote `^[text]` (one level of nested brackets, for links). Runs after the
    // reviewer-comment and index forms, which share the `^[` opener.
    [GeneratedRegex(@"\^\[(?<text>[^\[\]\n]*(?:\[[^\[\]\n]*\][^\[\]\n]*)*)\]")]
    private static partial Regex InlineFootnote();

    [GeneratedRegex(@"\^\[index:\s*(?:[""“](?<entry>(?:[^""”\\]|\\.)*?)[""”]|(?<entry>[^\]\n]+))\s*\]")]
    private static partial Regex IndexAnchor();

    [GeneratedRegex(@"\[dropdown:\s*(?<options>[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex DropdownControl();

    [GeneratedRegex(@"\[date(?::\s*(?<date>[^\]]+))?\]", RegexOptions.IgnoreCase)]
    private static partial Regex DateControl();

    [GeneratedRegex(@"\[text(?::\s*(?:[""“](?<ph>(?:[^""”\\]|\\.)*?)[""”]|(?<ph>[^\]]+)))?\]", RegexOptions.IgnoreCase)]
    private static partial Regex TextControl();

    // A GFM task-list item ("- [x] Ship it", "1. [ ] Draft"): the list marker must survive the
    // checkbox conversion below, or the item stops being a list item and Markdown folds it into
    // the previous bullet as lazy continuation text ("…customers ☑ Ship it ☐ Draft" on one line).
    [GeneratedRegex(@"^(?<lead>[ \t]*(?:[-*+]|\d{1,9}[.)])[ \t]+)\[(?<checked>[ xX])\](?=[ \t]|$)")]
    private static partial Regex TaskListItem();

    [GeneratedRegex(@"(?<=^|[|\s])(?:-\s*)?\[(?<checked>[ xX])\](?=\s|[|]|$)")]
    private static partial Regex CheckboxControl();

    [GeneratedRegex(@"^\|[\s|:\-]+$")]
    private static partial Regex TableDelimiterRegex();

    [GeneratedRegex(@"===\s*""([^""]+)""\r?\n([\s\S]*?)(?=(===\s*""|$))")]
    private static partial Regex TabbedBlockRegex();

    [GeneratedRegex(@"^@(?<author>[^:\[\]\n]+?)\s*\[(?<date>[^\]]+)\]:\s*(?<text>[\s\S]*)$")]
    private static partial Regex CommentMetaAtBracket();

    [GeneratedRegex(@"^(?<author>[^:()\n]+?)\s*\((?<date>[^)]+)\):\s*(?<text>[\s\S]*)$")]
    private static partial Regex CommentMetaParen();

    [GeneratedRegex(@"^(?<author>[a-zA-Z0-9_.\s-]{1,50}):\s*(?<text>[\s\S]*)$")]
    private static partial Regex CommentMetaColon();

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
        bool afterDefinitionList = false;
        var inlineNotes = new List<string>();

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
                if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                {
                    var fm = FenceTitle().Match(trimmed);
                    if (fm.Success)
                    {
                        inCode = true;
                        fenceMarker = fm.Groups[1].Value[..3];
                        var lang = fm.Groups[2].Value;
                        var fileTag = fm.Groups[3].Value;                     // ```ts:api.ts form
                        var titleAttr = TitleAttr().Match(fm.Groups[4].Value);   // title="train.py" form
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
                    }
                    inCode = true;
                    fenceMarker = trimmed[..3];
                    output.Add(line);
                    continue;
                }
            }
            else
            {
                if (fenceMarker is not null && trimmed.StartsWith(fenceMarker, StringComparison.Ordinal)) { inCode = false; fenceMarker = null; }
                output.Add(line);
                continue;
            }

            // ---- page break markers -> a div both exporters understand (HTML: visible marker in
            // preview + page-break-after for print; DOCX: a real w:br type=page) ----
            if ((trimmed.StartsWith('<') || trimmed.StartsWith('\\')) && PageBreak().IsMatch(line))
            {
                output.Add("<div class=\"page-break\"></div>");
                continue;
            }

            // ---- MkDocs content tabs: === "Name" + 4-space-indented body -> bold label line +
            // dedented body (sequential sections — a static document can't switch tabs) ----
            if (line.StartsWith("===", StringComparison.Ordinal))
            {
                var tab = TabHeader().Match(line);
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
            }

            // ---- definition lists ----
            // Markdig requires 3+ spaces after the colon. AI often outputs 1 space.
            if (trimmed.StartsWith(':'))
            {
                var dlMatch = DefinitionList().Match(line);
                if (dlMatch.Success)
                {
                    output.Add(dlMatch.Groups[1].Value + ":   " + dlMatch.Groups[2].Value);
                    afterDefinitionList = true;
                    continue;
                }
            }
            // A footnote definition straight after a definition list: Markdig reads it as the next
            // term, then as a link reference definition, so "[^1]" printed raw in every format.
            // An empty comment closes the list first.
            if (afterDefinitionList && FootnoteDefinition().IsMatch(line))
            {
                if (output.Count > 0 && output[^1].Trim().Length > 0) output.Add("");
                output.Add("<!-- -->");
                output.Add("");
            }
            if (trimmed.Length > 0 && trimmed.Length == line.Length) afterDefinitionList = false;

            // ---- ChatGPT-style escaped quotes in table cells — \" leftovers from JSON-to-markdown
            // conversions go back to plain quotes. Table lines ONLY, so prose keeps its literal
            // backslash-quotes verbatim.
            if (trimmed.StartsWith('|'))
                line = line.Replace("\\\"", "\"");

            // ---- wiki-links and #tags (inline, code-span-guarded) ----
            if (line.Contains("[[", StringComparison.Ordinal))
            {
                line = ReplaceOutsideInlineCode(line, WikiLink(), m =>
                {
                    var target = m.Groups[1].Value.Trim();
                    var alias = m.Groups[2].Success ? m.Groups[2].Value.Trim() : target;
                    return $"<span class=\"wikilink\">{System.Net.WebUtility.HtmlEncode(alias)}</span>";
                });
            }
            if (line.Contains('#'))
            {
                line = ReplaceOutsideInlineCode(line, HashTag(), m =>
                    $"<span class=\"md-tag\">#{m.Groups[1].Value}</span>");
            }

            // ---- ISS-002: ::emoji:: double-colon shortcodes (inline-code-guarded; fenced code is
            // already skipped by the inCode pass above) ----
            if (line.Contains("::", StringComparison.Ordinal))
            {
                line = ReplaceOutsideInlineCode(line, DoubleColonEmoji(), m =>
                    EmojiReplacer.ReplaceShortcode(m.Groups[1].Value, m.Value));
            }

            // ---- CriticMarkup / revision markers ----
            if (line.Contains('{'))
            {
                line = ReplaceOutsideInlineCode(line, CriticHlComment(), m =>
                {
                    var hlText = m.Groups["text"].Value;
                    var (author, date, comment) = ParseCommentMetadata(m.Groups["comment"].Value);
                    return $"<mark>{hlText}</mark>" + RenderCommentAnchor(author, date, comment);
                }, protectHtml: false);

                line = ReplaceOutsideInlineCode(line, CriticSub(), m => $"<del>{m.Groups[1].Value}</del><ins>{m.Groups[2].Value}</ins>", protectHtml: false);
                line = ReplaceOutsideInlineCode(line, CriticDel(), m => $"<del>{m.Groups[2].Value}</del>", protectHtml: false);
                line = ReplaceOutsideInlineCode(line, CriticIns(), m => $"<ins>{m.Groups[1].Value}</ins>", protectHtml: false);
                line = ReplaceOutsideInlineCode(line, CriticHl(),  m => $"<mark>{m.Groups[1].Value}</mark>", protectHtml: false);
                line = ReplaceOutsideInlineCode(line, CriticComment(), m =>
                {
                    var (author, date, comment) = ParseCommentMetadata(m.Groups["comment"].Value);
                    return RenderCommentAnchor(author, date, comment);
                }, protectHtml: false);
            }

            // ---- Reviewer comment annotations, Index anchors, & Pandoc inline footnotes (all open with `^[`) ----
            if (line.Contains("^[", StringComparison.Ordinal))
            {
                line = ReplaceOutsideInlineCode(line, ReviewerComment(), m =>
                {
                    var author = m.Groups["author"].Value.Trim();
                    var date = m.Groups["date"].Success ? m.Groups["date"].Value.Trim() : "";
                    var comment = CleanCommentText(m.Groups["comment"].Value);
                    return RenderCommentAnchor(author, date, comment);
                }, protectHtml: false);

                line = ReplaceOutsideInlineCode(line, IndexAnchor(), m =>
                {
                    var entry = m.Groups["entry"].Value.Trim();
                    return $"<span class=\"ms-index-anchor\" data-index=\"{System.Net.WebUtility.HtmlEncode(entry)}\"></span>";
                }, protectHtml: false);

                line = ReplaceOutsideInlineCode(line, InlineFootnote(), m =>
                {
                    var text = m.Groups["text"].Value.Trim();
                    if (text.Length == 0) return m.Value;
                    var label = $"ms-inline-{inlineNotes.Count + 1}";
                    inlineNotes.Add($"[^{label}]: {text}");
                    return $"[^{label}]";
                });
            }

            // ---- Fillable Form Controls (SDT): [dropdown: ...], [date: ...], [text: ...], [x]/[ ] ----
            if (line.Contains('['))
            {
                line = ReplaceOutsideInlineCode(line, DropdownControl(), m =>
                {
                    var options = m.Groups["options"].Value.Split('|').Select(o => o.Trim()).Where(o => !string.IsNullOrEmpty(o)).ToList();
                    var sb = new StringBuilder();
                    sb.Append("<select class=\"ms-form-dropdown\"");
                    sb.Append($" data-options=\"{System.Net.WebUtility.HtmlEncode(string.Join("|", options))}\">");
                    foreach (var opt in options)
                    {
                        sb.Append($"<option value=\"{System.Net.WebUtility.HtmlEncode(opt)}\">{System.Net.WebUtility.HtmlEncode(opt)}</option>");
                    }
                    sb.Append("</select>");
                    return sb.ToString();
                }, protectHtml: false);

                line = ReplaceOutsideInlineCode(line, DateControl(), m =>
                {
                    var d = m.Groups["date"].Success ? m.Groups["date"].Value.Trim() : "";
                    var valAttr = !string.IsNullOrEmpty(d) ? $" value=\"{System.Net.WebUtility.HtmlEncode(d)}\"" : "";
                    return $"<input type=\"date\" class=\"ms-form-date\"{valAttr} />";
                }, protectHtml: false);

                line = ReplaceOutsideInlineCode(line, TextControl(), m =>
                {
                    var ph = m.Groups["ph"].Success ? m.Groups["ph"].Value.Trim() : "";
                    var phAttr = !string.IsNullOrEmpty(ph) ? $" placeholder=\"{System.Net.WebUtility.HtmlEncode(ph)}\" value=\"{System.Net.WebUtility.HtmlEncode(ph)}\"" : "";
                    return $"<input type=\"text\" class=\"ms-form-text\"{phAttr} />";
                }, protectHtml: false);

                line = ReplaceOutsideInlineCode(line, TaskListItem(), m =>
                {
                    bool isChecked = m.Groups["checked"].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
                    return m.Groups["lead"].Value + (isChecked
                        ? "<input type=\"checkbox\" class=\"ms-form-checkbox\" checked />"
                        : "<input type=\"checkbox\" class=\"ms-form-checkbox\" />");
                }, protectHtml: false);

                line = ReplaceOutsideInlineCode(line, CheckboxControl(), m =>
                {
                    bool isChecked = m.Groups["checked"].Value.Trim().Equals("x", StringComparison.OrdinalIgnoreCase);
                    return isChecked ? "<input type=\"checkbox\" class=\"ms-form-checkbox\" checked />" : "<input type=\"checkbox\" class=\"ms-form-checkbox\" />";
                }, protectHtml: false);
            }

            // ---- table delimiter line normalization: e.g. |--:| -> |---:| ----
            if (trimmed.StartsWith('|') && TableDelimiterRegex().IsMatch(trimmed))
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

        // Inline footnotes' definitions, after everything else (not inside an unclosed fence).
        if (inlineNotes.Count > 0 && !inCode)
        {
            while (output.Count > 0 && output[^1].Trim().Length == 0) output.RemoveAt(output.Count - 1);
            output.Add("");
            if (afterDefinitionList) { output.Add("<!-- -->"); output.Add(""); }
            output.AddRange(inlineNotes);
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
        return TabbedBlockRegex().Replace(markdown, m =>
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
    private static string ReplaceOutsideInlineCode(string line, Regex rx, MatchEvaluator evaluator, bool protectHtml = true)
    {
        if (!rx.IsMatch(line)) return line;

        var codeSpans = line.Contains('`') ? InlineCode().Matches(line) : null;
        var htmlSpans = protectHtml && line.Contains('<') ? HtmlTag().Matches(line) : null;
        if ((codeSpans == null || codeSpans.Count == 0) && (htmlSpans == null || htmlSpans.Count == 0))
            return rx.Replace(line, evaluator);

        var protectedRanges = new List<(int Start, int End)>();
        if (codeSpans != null)
        {
            foreach (Match m in codeSpans) protectedRanges.Add((m.Index, m.Index + m.Length));
        }
        if (htmlSpans != null)
        {
            foreach (Match m in htmlSpans) protectedRanges.Add((m.Index, m.Index + m.Length));
        }

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

    private static (string Author, string Date, string Text) ParseCommentMetadata(string raw)
    {
        raw = raw.Trim();
        // Format 1: @Author [Date]: Comment body
        var m1 = CommentMetaAtBracket().Match(raw);
        if (m1.Success)
            return (CleanAuthor(m1.Groups["author"].Value), m1.Groups["date"].Value.Trim(), CleanCommentText(m1.Groups["text"].Value));

        // Format 2: Author (Date): Comment body
        var m2 = CommentMetaParen().Match(raw);
        if (m2.Success)
            return (CleanAuthor(m2.Groups["author"].Value), m2.Groups["date"].Value.Trim(), CleanCommentText(m2.Groups["text"].Value));

        // Format 3: Author: Comment body (where Author has no newlines and looks like an author name)
        var m3 = CommentMetaColon().Match(raw);
        if (m3.Success && !m3.Groups["author"].Value.Contains('\n'))
            return (CleanAuthor(m3.Groups["author"].Value), "", CleanCommentText(m3.Groups["text"].Value));

        // Format 4: Just comment body
        return ("Reviewer", "", CleanCommentText(raw));
    }

    private static string CleanAuthor(string author) => author.Trim().Trim('"', '\'', '@');

    private static string CleanCommentText(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && ((text.StartsWith('"') && text.EndsWith('"')) || (text.StartsWith('“') && text.EndsWith('”'))))
        {
            text = text[1..^1].Trim();
        }
        return text;
    }

    private static string RenderCommentAnchor(string author, string date, string text)
    {
        var dateAttr = !string.IsNullOrWhiteSpace(date) ? $" data-date=\"{System.Net.WebUtility.HtmlEncode(date)}\"" : "";
        var title = !string.IsNullOrWhiteSpace(date)
            ? $"{author} ({date}): {text}"
            : $"{author}: {text}";
        return $"<span class=\"ms-comment-anchor\" data-author=\"{System.Net.WebUtility.HtmlEncode(author)}\"{dateAttr} data-comment=\"{System.Net.WebUtility.HtmlEncode(text)}\"><sup class=\"ms-comment-badge\" title=\"{System.Net.WebUtility.HtmlEncode(title)}\">💬 {System.Net.WebUtility.HtmlEncode(author)}</sup></span>";
    }
}
