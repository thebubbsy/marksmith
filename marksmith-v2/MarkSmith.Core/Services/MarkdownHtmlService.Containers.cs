using System.Globalization;
using System.Net;
using System.Text;
using MarkSmith.Models;
using Markdig;

namespace MarkSmith.Services;

// Preview half of the Insert-menu containers that used to be DOCX-only: :::tabs, :::datagrid,
// :::references, :::embed and :::ai-context. The DOCX exporter has drawn all five since they
// shipped, but the HTML pipeline (live preview, PDF, HTML export) had no handler, so a block
// inserted from the Insert menu previewed as run-on text ("=== Tab 1 Content 1 === Tab 2…",
// "label,value Q1,10…") or — for :::embed — vanished entirely. Same lift/placeholder model as
// :::chart: the block is swapped for an HTML comment before Markdig, then for markup we build
// ourselves after the sanitize step, so nothing from the document is injected unescaped.
public sealed partial class MarkdownHtmlService
{
    private static readonly string[] ContainerNames = { "tabs", "datagrid", "references", "embed", "ai-context" };

    /// <summary>Lifts every supported container out of <paramref name="markdown"/>, returning the
    /// rewritten text; <paramref name="blocks"/> holds the HTML for each <c>&lt;!--MSBLOCK:n--&gt;</c>.
    /// Line-based rather than a regex because <c>:::tabs</c> may nest <c>:::tab</c> containers, each
    /// with its own closer; code fences are skipped wholesale.</summary>
    internal static string LiftContainerBlocks(string markdown, ThemeDefinition theme, out List<string> blocks)
    {
        blocks = new List<string>();
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains(":::", StringComparison.Ordinal)) return markdown;

        var lines = markdown.Split('\n');
        var output = new StringBuilder(markdown.Length);
        string? fence = null;
        var tabsCssEmitted = false;
        var citations = new Dictionary<string, (string Anchor, string Label)>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (fence is null && (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")))
                fence = trimmed[..3];
            else if (fence is not null && trimmed.StartsWith(fence))
                fence = null;
            else if (fence is null && ContainerName(trimmed) is { } name && FindCloser(lines, i) is var close && close > i)
            {
                var inner = string.Join("\n", lines[(i + 1)..close]).Replace("\r", "");
                var html = name switch
                {
                    "tabs" => RenderTabsHtml(inner, theme, blocks.Count, ref tabsCssEmitted),
                    "datagrid" => RenderDatagridHtml(inner, theme),
                    "references" => RenderReferencesHtml(inner, theme, UnderHeading(output), citations),
                    "embed" => RenderEmbedHtml(trimmed, inner, theme),
                    _ => RenderAiContextHtml(inner, theme),
                };
                // Hints (a block that can't render yet) say how to fix it, styled like a callout.
                blocks.Add(html.Replace("class=\"ms-block-hint\"",
                    $"class=\"ms-block-hint\" style=\"margin:1rem 0;padding:.6rem .9rem;border:1px dashed {Enc(theme.Border)};border-radius:8px;color:{Enc(theme.Line)};font-size:.9em\""));
                output.Append($"\n<!--MSBLOCK:{blocks.Count - 1}-->\n\n");
                i = close;
                continue;
            }

            output.Append(line);
            if (i < lines.Length - 1) output.Append('\n');
        }
        return citations.Count == 0 ? output.ToString() : LinkCitations(output.ToString(), citations);
    }

    // True when the last non-blank line written so far is a Markdown heading, i.e. the author has
    // already titled the block ("## References") and a second "Bibliography" heading would stack.
    private static bool UnderHeading(StringBuilder output)
    {
        var text = output.ToString().TrimEnd();
        var last = text[(text.LastIndexOf('\n') + 1)..].TrimStart();
        return last.StartsWith('#');
    }

    private static readonly System.Text.RegularExpressions.Regex CitationClusterRe =
        new(@"\[@([\w\-]+(?:\s*;\s*@[\w\-]+)*)\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// For exports with no anchors to link to (Word): every <c>[@key]</c> a <c>:::references</c>
    /// block defines becomes plain "(Knuth, 1984)" text, the same label the preview links.
    /// </summary>
    /// <remarks>Also marks a block the author already headed ("## References" right above it)
    /// with <c>titled="true"</c>, so Word adds no second "Bibliography" heading: its renderer
    /// draws into a fresh body and can't see what came before.</remarks>
    internal static string CiteReferencesAsText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains(":::references", StringComparison.OrdinalIgnoreCase)) return markdown;
        var refs = new Dictionary<string, (string Anchor, string Label)>(StringComparer.OrdinalIgnoreCase);
        var lines = markdown.Split('\n');
        string? fence = null;
        var lastText = "";
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (fence is null && (t.StartsWith("```") || t.StartsWith("~~~"))) { fence = t[..3]; lastText = t; continue; }
            if (fence is not null) { if (t.StartsWith(fence)) fence = null; continue; }
            if (ContainerName(t) != "references" || FindCloser(lines, i) is not (var close and > 0))
            {
                if (t.Length > 0) lastText = t;
                continue;
            }
            if (lastText.StartsWith('#') && !t.Contains("titled=", StringComparison.OrdinalIgnoreCase))
                lines[i] = lines[i].TrimEnd('\r', ' ') + " titled=\"true\"" + (lines[i].EndsWith('\r') ? "\r" : "");
            foreach (var r in ContainerBlockParsers.ParseReferences(string.Join("\n", lines[(i + 1)..close]).Replace("\r", "")))
                if (r.Id.Length > 0) refs.TryAdd(r.Id, ("", CitationLabel(r)));
            lastText = ":::";
            i = close;
        }
        var marked = string.Join('\n', lines);
        return refs.Count == 0 || !marked.Contains("[@", StringComparison.Ordinal) ? marked : LinkCitations(marked, refs, linked: false);
    }

    // "[@knuth1984]" -> "[(Knuth, 1984)](#ref-knuth1984)" for every key a :::references block
    // defines, outside fenced code. Keys the document never defines are left as written.
    private static string LinkCitations(string markdown, Dictionary<string, (string Anchor, string Label)> refs, bool linked = true)
    {
        var lines = markdown.Split('\n');
        string? fence = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (fence is null && (t.StartsWith("```") || t.StartsWith("~~~"))) { fence = t[..3]; continue; }
            if (fence is not null) { if (t.StartsWith(fence)) fence = null; continue; }
            if (!lines[i].Contains("[@", StringComparison.Ordinal)) continue;
            lines[i] = CitationClusterRe.Replace(lines[i], m =>
            {
                var keys = m.Groups[1].Value.Split(';').Select(k => k.Trim().TrimStart('@')).ToList();
                if (!keys.All(refs.ContainsKey)) return m.Value;
                if (!linked) return "(" + string.Join("; ", keys.Select(k => refs[k].Label)) + ")";
                if (keys.Count == 1) return $"[({refs[keys[0]].Label})](#{refs[keys[0]].Anchor})";
                return "(" + string.Join("; ", keys.Select(k => $"[{refs[k].Label}](#{refs[k].Anchor})")) + ")";
            });
        }
        return string.Join('\n', lines);
    }

    // "Donald E. Knuth" -> "Knuth"; "Lamport, Leslie" -> "Lamport"; two authors "A & B", more "A et al.".
    private static string CitationLabel(ContainerBlockParsers.Reference r)
    {
        static string Surname(string name)
        {
            name = name.Trim();
            if (name.Contains(',')) return name[..name.IndexOf(',')].Trim();
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? name : parts[^1];
        }
        var authors = System.Text.RegularExpressions.Regex.Split(r.Author, @"\s+and\s+|\s*;\s*|\s*&\s*")
            .Where(a => a.Trim().Length > 0).Select(Surname).ToList();
        var who = authors.Count switch
        {
            0 => r.Title.Length > 0 ? r.Title : r.Id,
            1 => authors[0],
            2 => $"{authors[0]} & {authors[1]}",
            _ => $"{authors[0]} et al.",
        };
        return $"{who}, {(r.Year.Length > 0 ? r.Year : "n.d.")}";
    }

    private static string? ContainerName(string trimmedLine)
    {
        if (!trimmedLine.StartsWith(":::", StringComparison.Ordinal)) return null;
        foreach (var n in ContainerNames)
        {
            if (!trimmedLine.AsSpan(3).StartsWith(n, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = trimmedLine.Length > 3 + n.Length ? trimmedLine[3 + n.Length] : ' ';
            if (char.IsWhiteSpace(rest)) return n;
        }
        return null;
    }

    // Index of the ":::" that closes the container opened on line `open`, or -1. Named ":::x"
    // lines open a nested level; fenced code inside the block never counts.
    private static int FindCloser(string[] lines, int open)
    {
        var depth = 1;
        string? fence = null;
        for (int j = open + 1; j < lines.Length; j++)
        {
            var t = lines[j].Trim();
            if (fence is null && (t.StartsWith("```") || t.StartsWith("~~~"))) { fence = t[..3]; continue; }
            if (fence is not null) { if (t.StartsWith(fence)) fence = null; continue; }
            if (t == ":::") { if (--depth == 0) return j; }
            else if (t.Length > 3 && t.StartsWith(":::", StringComparison.Ordinal) && char.IsLetter(t[3])) depth++;
        }
        return -1;
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s ?? "");

    private static string Accent(ThemeDefinition theme) =>
        PickLinkColor(theme.Primary, theme.Background, ThemeDefinition.IsLight(theme.Background));

    // Inner Markdown of a tab panel: same treatment as a :::columns cell.
    private static string RenderFragment(string md) =>
        HtmlSanitizer.Apply(Markdown.ToHtml(DialectNormalizer.Apply(md.Trim()), Pipeline).Trim());

    private const string TabsCss =
        "<style>" +
        ".ms-tabs{margin:1.25rem 0;border:1px solid var(--ms-bd);border-radius:10px;overflow:hidden;background:rgba(125,125,125,.06)}" +
        ".ms-tabs>input{position:absolute;opacity:0;pointer-events:none}" +
        ".ms-tabs>label{display:inline-block;padding:.55rem 1.05rem;cursor:pointer;font-weight:500;color:var(--ms-mute);border-bottom:2px solid transparent;transition:color .15s,border-color .15s,background-color .15s;user-select:none}" +
        ".ms-tabs>label:hover{color:var(--ms-text);background:rgba(125,125,125,.10)}" +
        ".ms-tabs>input:checked+label{color:var(--ms-acc);border-bottom-color:var(--ms-acc)}" +
        ".ms-tabs>input:focus-visible+label{outline:2px solid var(--ms-acc);outline-offset:-2px}" +
        ".ms-tab-panels{border-top:1px solid var(--ms-bd);background:var(--ms-bg);padding:.15rem 1.1rem}" +
        ".ms-tab-panel{display:none}.ms-tab-title{display:none}" +
        "{NTH}" +
        "@media print{.ms-tabs{background:none}.ms-tabs>label{display:none}.ms-tab-panels{border-top:0}" +
        ".ms-tab-panel{display:block!important}.ms-tab-title{display:block;font-weight:600;margin:.9rem 0 .2rem}}" +
        "</style>";

    private static string RenderTabsHtml(string inner, ThemeDefinition theme, int blockIndex, ref bool cssEmitted)
    {
        var tabs = ContainerBlockParsers.ParseTabs(inner);
        if (tabs.Count == 0)
            return "<div class=\"ms-block-hint\">Tabs: add a <code>=== Tab title</code> line before each tab's content.</div>";

        var sb = new StringBuilder();
        if (!cssEmitted)
        {
            // Panel N shows while radio N is checked; generated for the most tabs a group can hold.
            var nth = new StringBuilder();
            for (int n = 1; n <= 16; n++)
                nth.Append($".ms-tabs>input:nth-of-type({n}):checked~.ms-tab-panels>.ms-tab-panel:nth-of-type({n}){{display:block}}");
            sb.Append(TabsCss.Replace("{NTH}", nth.ToString()));
            cssEmitted = true;
        }

        var group = $"ms-tabs-{blockIndex}";
        sb.Append($"<div class=\"ms-tabs\" style=\"--ms-bd:{Enc(theme.Border)};--ms-acc:{Enc(Accent(theme))};--ms-text:{Enc(theme.Text)};--ms-mute:{Enc(theme.Line)};--ms-bg:{Enc(theme.Background)}\">");
        var shown = Math.Min(tabs.Count, 16);
        for (int t = 0; t < shown; t++)
        {
            var id = $"{group}-{t}";
            sb.Append($"<input type=\"radio\" name=\"{group}\" id=\"{id}\"{(t == 0 ? " checked" : "")}>")
              .Append($"<label for=\"{id}\">{Enc(tabs[t].Title)}</label>");
        }
        sb.Append("<div class=\"ms-tab-panels\">");
        for (int t = 0; t < shown; t++)
        {
            var body = string.IsNullOrWhiteSpace(tabs[t].Content)
                ? $"<p style=\"color:{Enc(theme.Line)};font-style:italic\">Empty tab — write its content under the <code>=== {Enc(tabs[t].Title)}</code> line.</p>"
                : RenderFragment(tabs[t].Content);
            sb.Append($"<div class=\"ms-tab-panel\"><div class=\"ms-tab-title\">{Enc(tabs[t].Title)}</div>{body}</div>");
        }
        return sb.Append("</div></div>").ToString();
    }

    private static bool LooksNumeric(string s) => ContainerBlockParsers.LooksNumeric(s);

    private static string RenderDatagridHtml(string inner, ThemeDefinition theme)
    {
        var rows = ContainerBlockParsers.ParseDatagrid(inner);
        if (rows.Count == 0)
            return "<div class=\"ms-block-hint\">Data grid: first line is the column headers, then one comma-separated row per line.</div>";

        // Header colours follow the DOCX exporter (theme primary), with the text colour picked for
        // contrast instead of a fixed white that vanished on light-primary themes (GitHub Dark).
        var headBg = theme.Primary;
        var headFg = ContrastGuard.GetContrastRatio("#ffffff", headBg) >= ContrastGuard.GetContrastRatio("#111111", headBg) ? "#ffffff" : "#111111";
        var cols = rows[0].Length;
        var numeric = new bool[cols];
        for (int c = 0; c < cols; c++)
            numeric[c] = rows.Count > 1 && rows.Skip(1).All(r => r[c].Length == 0 || LooksNumeric(r[c])) && rows.Skip(1).Any(r => r[c].Length > 0);

        var sb = new StringBuilder("<div class=\"ms-datagrid-wrap\" style=\"overflow-x:auto;margin:1.2rem 0\"><table class=\"ms-datagrid\" style=\"margin:0\"><thead><tr>");
        for (int c = 0; c < cols; c++)
            sb.Append($"<th style=\"background:{Enc(headBg)};color:{Enc(headFg)};text-align:{(numeric[c] ? "right" : "left")}\">{Enc(rows[0][c])}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var r in rows.Skip(1))
        {
            sb.Append("<tr>");
            for (int c = 0; c < cols; c++)
                sb.Append($"<td{(numeric[c] ? " style=\"text-align:right;font-variant-numeric:tabular-nums\"" : "")}>{Enc(r[c])}</td>");
            sb.Append("</tr>");
        }
        return sb.Append("</tbody></table></div>").ToString();
    }

    private static string RenderReferencesHtml(string inner, ThemeDefinition theme, bool underHeading,
        Dictionary<string, (string Anchor, string Label)> citations)
    {
        var refs = ContainerBlockParsers.ParseReferences(inner);
        if (refs.Count == 0)
            return "<div class=\"ms-block-hint\">Bibliography: start each entry with an <code>@id</code> line, then <code>author:</code>, <code>title:</code>, <code>year:</code>.</div>";

        // Same heading the DOCX export writes above its BIBLIOGRAPHY field, unless the author
        // already put one ("## References") right above the block.
        var sb = new StringBuilder("<section class=\"ms-references\">" + (underHeading ? "" : "<h2>Bibliography</h2>") + "<ol style=\"padding-left:1.6em\">");
        foreach (var r in refs)
        {
            var anchor = "ref-" + new string(r.Id.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
            if (r.Id.Length > 0) citations.TryAdd(r.Id, (anchor, CitationLabel(r)));
            var idAttr = r.Id.Length > 0 ? $" id=\"{Enc(anchor)}\"" : "";
            sb.Append($"<li{idAttr} style=\"margin:.35rem 0;line-height:1.5\">");
            if (r.Author.Length > 0) sb.Append(Enc(r.Author)).Append(' ');
            if (r.Year.Length > 0) sb.Append('(').Append(Enc(r.Year)).Append("). ");
            else if (r.Author.Length > 0) sb.Append("(n.d.). ");
            if (r.Title.Length > 0) sb.Append("<em>").Append(Enc(r.Title)).Append("</em>. ");
            if (r.Journal.Length > 0) sb.Append(Enc(r.Journal)).Append(". ");
            if (IsWebUrl(r.Url)) sb.Append($"<a href=\"{Enc(r.Url)}\">{Enc(r.Url)}</a> ");
            sb.Append("</li>");
        }
        return sb.Append("</ol></section>").ToString();
    }

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string RenderEmbedHtml(string markerLine, string inner, ThemeDefinition theme)
    {
        var attrs = ContainerBlockParsers.ParseAttributes(markerLine);
        var src = attrs.GetValueOrDefault("src", "").Trim();
        var provider = attrs.GetValueOrDefault("provider", "");
        if (provider.Length == 0) provider = ContainerBlockParsers.DetectProvider(src) ?? "";
        var name = ContainerBlockParsers.ProviderDisplayName(provider);
        var caption = inner.Trim();

        if (!IsWebUrl(src))
        {
            var why = src.Length == 0 ? "no <code>src</code> URL" : $"<code>{Enc(src)}</code> isn't a web address";
            var label = provider.Length == 0 ? "Embed" : name + " embed";
            return $"<div class=\"ms-block-hint\">{Enc(label)}: {why}. Use <code>src=\"https://…\"</code>.</div>";
        }

        var brand = provider.ToLowerInvariant() switch
        {
            "youtube" => "#e62117",
            "vimeo" => "#17a2d8",
            "loom" => "#625df5",
            "codepen" => "#47cf73",
            "bilibili" => "#00a1d6",
            "figma" => "#a259ff",
            _ => Accent(theme),
        };
        var host = Uri.TryCreate(src, UriKind.Absolute, out var u) ? u.Host : src;

        // A document can't play video (PDF/print/DOCX all link out), so the preview shows the same
        // honest thing: a card that opens the video, not a blank iframe.
        return "<a class=\"ms-embed\" href=\"" + Enc(src) + "\" style=\"display:flex;align-items:center;gap:14px;margin:1.2rem 0;padding:14px 16px;" +
               $"border:1px solid {Enc(theme.Border)};border-radius:10px;background:rgba(125,125,125,.06);text-decoration:none;color:{Enc(theme.Text)}\">" +
               $"<span style=\"flex:none;width:44px;height:44px;border-radius:50%;background:{brand};display:flex;align-items:center;justify-content:center\">" +
               "<svg width=\"18\" height=\"18\" viewBox=\"0 0 18 18\" aria-hidden=\"true\"><path d=\"M6 3.5v11l9-5.5z\" fill=\"#fff\"/></svg></span>" +
               "<span style=\"min-width:0\">" +
               $"<span style=\"display:block;font-weight:600\">{Enc(caption.Length > 0 ? caption : name + " video")}</span>" +
               $"<span style=\"display:block;font-size:.85em;color:{Enc(theme.Line)};overflow:hidden;text-overflow:ellipsis;white-space:nowrap\">{Enc(name)} · {Enc(host)}</span>" +
               "</span></a>";
    }

    private static string RenderAiContextHtml(string inner, ThemeDefinition theme)
    {
        var pairs = ContainerBlockParsers.ParseKeyValues(inner);
        if (pairs.Count == 0)
            return "<div class=\"ms-block-hint\">AI context: add <code>key: value</code> lines such as <code>model:</code> and <code>timestamp:</code>.</div>";

        var sb = new StringBuilder(
            $"<aside class=\"ms-ai-context\" style=\"margin:1.2rem 0;padding:.7rem 1rem;border-left:3px solid {Enc(Accent(theme))};border-radius:6px;background:rgba(125,125,125,.07);font-size:.9em\">" +
            $"<div style=\"font-size:.75em;font-weight:600;letter-spacing:.06em;text-transform:uppercase;color:{Enc(theme.Line)}\">AI context</div>" +
            "<dl style=\"display:grid;grid-template-columns:max-content 1fr;gap:.15rem 1rem;margin:.4rem 0 0\">");
        foreach (var (k, v) in pairs)
            sb.Append($"<dt style=\"color:{Enc(theme.Line)};font-family:ui-monospace,Consolas,monospace\">{Enc(k)}</dt>")
              .Append($"<dd style=\"margin:0;font-family:ui-monospace,Consolas,monospace;overflow-wrap:anywhere\">{Enc(v)}</dd>");
        return sb.Append("</dl></aside>").ToString();
    }
}
