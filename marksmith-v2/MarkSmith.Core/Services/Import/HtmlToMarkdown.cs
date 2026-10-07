using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace MarkSmith.Services.Import;

/// <summary>Options for <see cref="HtmlToMarkdown"/>.</summary>
public sealed class HtmlToMarkdownOptions
{
    /// <summary>Maps an image <c>src</c> to the URL the Markdown should use. Return null to drop
    /// the image (e.g. a <c>cid:</c> that has no matching part). When unset, sources are kept.</summary>
    public Func<string, string?>? ResolveImage { get; init; }

    /// <summary>Unwraps Outlook "Safe Links" (<c>*.safelinks.protection.outlook.com/?url=…</c>)
    /// back to the address the sender actually wrote. On by default.</summary>
    public bool UnwrapSafeLinks { get; init; } = true;
}

/// <summary>Turns real-world HTML — browser pages, Word and Outlook mail bodies, our own exports —
/// into clean, readable Markdown. It walks a spec-compliant DOM (AngleSharp parses exactly what a
/// browser would), so implicit closes, Word's <c>&lt;o:p&gt;</c> runs and conditional comments
/// can't derail it the way regexes do.
///
/// What it does beyond the obvious tag mapping:
/// - Word/Outlook list paragraphs (<c>mso-list:l0 level2</c> with a fake bullet) become real
///   nested lists.
/// - Layout tables (a single column, nested tables, block content in cells — every HTML email's
///   600 px wrapper) are unwrapped; data tables become GFM pipe tables with their alignment.
/// - Bold/italic/monospace applied through inline <c>style</c> (Word's habit) count as such, and
///   adjacent runs of the same formatting merge (<c>&lt;b&gt;Hel&lt;/b&gt;&lt;b&gt;lo&lt;/b&gt;</c>
///   is <c>**Hello**</c>, not <c>**Hel****lo**</c>).
/// - Hidden content (<c>display:none</c>, <c>mso-hide:all</c>, mail preheaders) and tracking
///   pixels are dropped.</summary>
public static class HtmlToMarkdown
{
    public static string Convert(string html, HtmlToMarkdownOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var doc = new HtmlParser().ParseDocument(html);
        return Convert(doc.Body ?? (INode)doc.DocumentElement, options);
    }

    /// <summary>Converts the children of <paramref name="root"/> (an element or a fragment).</summary>
    public static string Convert(INode root, HtmlToMarkdownOptions? options = null)
    {
        var w = new Walker(options ?? new HtmlToMarkdownOptions());
        return Tidy(w.Blocks(root.ChildNodes));
    }

    /// <summary>Plain text (a <c>text/plain</c> mail body, a .txt) as Markdown that reads the way the
    /// sender laid it out: blank-line paragraphs stay paragraphs, short lines (addresses,
    /// signatures) keep their line breaks, and <c>&gt;</c> quotes are already Markdown.</summary>
    public static string FromPlainText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var next = i + 1 < lines.Length ? lines[i + 1].TrimEnd() : "";
            sb.Append(line);
            // A hard break keeps "Kind regards,\nAnn" on two lines; never after a blank line, a
            // quote or list line (Markdown already breaks those), or before a blank line.
            bool keepBreak = line.Length > 0 && next.Length > 0
                && !line.StartsWith('>') && !next.StartsWith('>')
                && !ListLine.IsMatch(next) && !ListLine.IsMatch(line);
            // Entering or leaving a quote gets its own paragraph break, so the quote reads as one.
            bool quoteEdge = line.Length > 0 && next.Length > 0 && line.StartsWith('>') != next.StartsWith('>');
            if (i < lines.Length - 1) sb.Append(keepBreak ? "\\\n" : quoteEdge ? "\n\n" : "\n");
        }
        return Tidy(sb.ToString());
    }

    private static readonly Regex ListLine = new(@"^\s*(?:[-*+]|\d{1,3}[.)])\s", RegexOptions.Compiled);

    // Collapse runs of blank lines, strip trailing spaces, and end with exactly one newline.
    internal static string Tidy(string md)
    {
        md = md.Replace("\r\n", "\n");
        md = Regex.Replace(md, @"[ \t]+\n", "\n");
        md = Regex.Replace(md, @"\n{3,}", "\n\n");
        md = md.Trim('\n', ' ');
        return md.Length == 0 ? "" : md + "\n";
    }

    private sealed class Walker
    {
        private readonly HtmlToMarkdownOptions _o;
        public Walker(HtmlToMarkdownOptions o) => _o = o;

        private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "p", "div", "section", "article", "header", "footer", "main", "aside", "center",
            "address", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "blockquote", "pre",
            "table", "thead", "tbody", "tfoot", "tr", "td", "th", "hr", "dl", "dt", "dd", "figure",
            "figcaption", "details", "summary", "form", "fieldset", "body", "html", "caption",
        };

        private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "head", "title", "nav", "meta", "link", "noscript", "template", "iframe",
            "object", "embed", "svg", "canvas", "button", "select", "textarea", "map", "video", "audio",
        };

        // ---------- blocks ----------

        public string Blocks(INodeList nodes)
        {
            var parts = new List<string>();
            var inline = new List<INode>();

            void FlushInline()
            {
                if (inline.Count == 0) return;
                var p = Paragraph(inline);
                if (p.Length > 0) parts.Add(p);
                inline.Clear();
            }

            var list = nodes.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                if (n is IElement el && IsMsoListParagraph(el))
                {
                    FlushInline();
                    var run = new List<IElement> { el };
                    int j = i + 1;
                    for (; j < list.Count; j++)
                    {
                        if (list[j] is IText t && string.IsNullOrWhiteSpace(t.Data)) continue;
                        if (list[j] is IElement e2 && IsMsoListParagraph(e2)) { run.Add(e2); continue; }
                        break;
                    }
                    parts.Add(MsoList(run));
                    i = j - 1;
                    continue;
                }

                if (n is IElement e && IsBlock(e))
                {
                    FlushInline();
                    var b = Block(e);
                    if (b.Length > 0) parts.Add(b);
                }
                else
                {
                    inline.Add(n);
                }
            }
            FlushInline();
            return string.Join("\n\n", parts);
        }

        private bool IsBlock(IElement e)
        {
            var tag = e.LocalName;
            if (BlockTags.Contains(tag)) return true;
            if (SkipTags.Contains(tag)) return false;
            // A span/font that wraps block content (Word does this) is treated as its content.
            return false;
        }

        private string Block(IElement e)
        {
            if (IsHidden(e)) return "";
            var tag = e.LocalName.ToLowerInvariant();
            switch (tag)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                {
                    var text = InlineText(e.ChildNodes).Replace("\n", " ").Trim();
                    // A heading's own bold is noise: "## **Title**" reads as "## Title".
                    text = StripWrappingBold(text);
                    return text.Length == 0 ? "" : new string('#', tag[1] - '0') + " " + text;
                }
                case "p":
                    return Paragraph(e.ChildNodes);
                case "hr":
                    return "---";
                case "pre":
                    return CodeBlock(e);
                case "blockquote":
                {
                    var inner = Blocks(e.ChildNodes).Trim('\n');
                    if (inner.Length == 0) return "";
                    return string.Join("\n", inner.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));
                }
                case "ul": case "ol":
                    return List(e, 0);
                case "li":
                    // A stray <li> outside a list still reads as a bullet.
                    return "- " + Indent(Blocks(e.ChildNodes).Trim('\n'), 2).TrimStart();
                case "table":
                    return Table(e);
                case "dl":
                    return DefinitionList(e);
                case "details":
                {
                    var summary = e.Children.FirstOrDefault(c => c.LocalName == "summary");
                    var body = Blocks(new NodeListView(e.ChildNodes.Where(c => c != summary)));
                    var title = summary is null ? "Details" : InlineText(summary.ChildNodes).Trim();
                    return $"<details><summary>{Escape(title)}</summary>\n\n{body}\n\n</details>";
                }
                case "figure":
                {
                    var caption = e.Children.FirstOrDefault(c => c.LocalName == "figcaption");
                    var body = Blocks(new NodeListView(e.ChildNodes.Where(c => c != caption)));
                    if (caption is null) return body;
                    var cap = InlineText(caption.ChildNodes).Trim();
                    return cap.Length == 0 ? body : body + "\n\n*" + cap + "*";
                }
                case "caption": case "summary": case "figcaption": case "dt": case "dd":
                    return Paragraph(e.ChildNodes);
                default:
                    // div, section, td, body… — a container: its content as blocks.
                    return Blocks(e.ChildNodes);
            }
        }

        private static string StripWrappingBold(string text)
        {
            if (text.Length > 4 && text.StartsWith("**") && text.EndsWith("**") && text.IndexOf("**", 2) == text.Length - 2)
                return text[2..^2].Trim();
            return text;
        }

        private string Paragraph(IEnumerable<INode> nodes)
        {
            var text = InlineText(nodes).Trim();
            // Lines that are only a hard break (Word's "<p>&nbsp;</p>" spacers) are nothing.
            text = Regex.Replace(text, @"^(\\\n)+|(\\\n)+$", "").Trim();
            if (text.Length == 0) return "";
            return EscapeLineStarts(text);
        }

        private string Paragraph(INodeList nodes) => Paragraph(nodes.AsEnumerable());

        // A paragraph that starts with "# ", "> ", "- ", "1. " would turn into something else.
        private static string EscapeLineStarts(string text)
        {
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (Regex.IsMatch(l, @"^(#{1,6}\s|>|[-+*]\s|=+\s*$|-{3,}\s*$)")) lines[i] = "\\" + l;
                else if (Regex.IsMatch(l, @"^\d{1,9}[.)]\s")) lines[i] = Regex.Replace(l, @"^(\d+)([.)])", "$1\\$2");
            }
            return string.Join("\n", lines);
        }

        private string CodeBlock(IElement pre)
        {
            var code = pre.QuerySelector("code") ?? pre;
            var lang = Language(code) ?? Language(pre) ?? "";
            var text = code.TextContent.Replace("\r\n", "\n").Replace(' ', ' ').TrimEnd('\n', ' ');
            if (text.StartsWith('\n')) text = text[1..];
            var longest = Regex.Matches(text, "`+").Select(m => m.Length).DefaultIfEmpty(0).Max();
            var fence = new string('`', Math.Max(3, longest + 1));
            return $"{fence}{lang}\n{text}\n{fence}";
        }

        private static string? Language(IElement e)
        {
            foreach (var cls in e.ClassList)
            {
                var m = Regex.Match(cls, @"^(?:language|lang)-(.+)$");
                if (m.Success) return m.Groups[1].Value;
            }
            var data = e.GetAttribute("data-lang") ?? e.GetAttribute("lang");
            return string.IsNullOrWhiteSpace(data) ? null : data;
        }

        private string List(IElement list, int depth)
        {
            bool ordered = list.LocalName.Equals("ol", StringComparison.OrdinalIgnoreCase);
            int n = int.TryParse(list.GetAttribute("start"), out var s) ? s : 1;
            var items = new List<string>();
            bool loose = false;
            foreach (var child in list.ChildNodes)
            {
                if (child is IElement li && (li.LocalName == "li"))
                {
                    var marker = ordered ? $"{n++}." : "-";
                    var body = ListItemBody(li, out var multiBlock);
                    loose |= multiBlock;
                    items.Add(marker + " " + Indent(body, marker.Length + 1).TrimStart());
                }
                else if (child is IElement nested && (nested.LocalName is "ul" or "ol"))
                {
                    // Invalid but common: a nested list as a direct child of the list.
                    var inner = List(nested, depth + 1);
                    if (items.Count > 0) items[^1] += "\n" + Indent(inner, ordered ? 3 : 2);
                    else items.Add(inner);
                }
                else if (child is IText t && !string.IsNullOrWhiteSpace(t.Data))
                {
                    items.Add("- " + Escape(Collapse(t.Data).Trim()));
                }
            }
            return string.Join(loose ? "\n\n" : "\n", items);
        }

        private string ListItemBody(IElement li, out bool multiBlock)
        {
            // A task-list checkbox renders inline as "[x] " (AppendElement), so it leads the item.
            var parts = Blocks(li.ChildNodes).Trim('\n');
            multiBlock = Regex.IsMatch(parts, @"\n\n(?![ \t]*([-*+]|\d+\.)\s)");
            // Nested lists hang directly under the item without a blank line between.
            parts = Regex.Replace(parts, @"\n\n(?=([-*+]|\d+\.)\s)", "\n");
            return parts;
        }

        private static string Indent(string text, int width)
        {
            var pad = new string(' ', width);
            return string.Join("\n", text.Split('\n').Select((l, i) => i == 0 || l.Length == 0 ? l : pad + l));
        }

        private string DefinitionList(IElement dl)
        {
            var parts = new List<string>();
            foreach (var c in dl.Children)
            {
                var text = InlineText(c.ChildNodes).Trim();
                if (text.Length == 0) continue;
                parts.Add(c.LocalName == "dt" ? "**" + text + "**" : text);
            }
            return string.Join("\n\n", parts);
        }

        // ---------- Word / Outlook list paragraphs ----------

        private static readonly Regex MsoListStyle = new(@"mso-list\s*:\s*l\d+\s+level(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool IsMsoListParagraph(IElement e) =>
            e.LocalName is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
            && MsoListStyle.IsMatch(e.GetAttribute("style") ?? "");

        private string MsoList(List<IElement> paragraphs)
        {
            var lines = new List<string>();
            var counters = new Dictionary<int, int>();
            var indents = new Dictionary<int, int>(); // level -> column where that level's text starts
            foreach (var p in paragraphs)
            {
                int level = int.Parse(MsoListStyle.Match(p.GetAttribute("style") ?? "").Groups[1].Value, CultureInfo.InvariantCulture);
                level = Math.Clamp(level, 1, 9);
                // Word writes the bullet as real text between <![if !supportLists]> … <![endif]>
                // comments; read it to tell numbers from bullets, then leave it out of the text.
                var (bullet, content) = SplitMsoBullet(p);
                bool ordered = Regex.IsMatch(bullet, @"^\(?[0-9]+[.)]?$|^\(?[a-zA-Z][.)]$|^[ivxlcdm]+[.)]$", RegexOptions.IgnoreCase);
                foreach (var k in counters.Keys.Where(k => k > level).ToList()) { counters.Remove(k); indents.Remove(k); }
                counters[level] = counters.TryGetValue(level, out var c) ? c + 1 : 1;
                var marker = ordered ? counters[level] + "." : "-";
                var text = InlineText(content).Trim();
                if (text.Length == 0) continue;
                // A nested item lines up with its parent's text, whatever the parent's marker width.
                int indent = level > 1 && indents.TryGetValue(level - 1, out var parent) ? parent : 0;
                indents[level] = indent + marker.Length + 1;
                lines.Add(new string(' ', indent) + marker + " " + text);
            }
            return string.Join("\n", lines);
        }

        private static (string Bullet, List<INode> Content) SplitMsoBullet(IElement p)
        {
            var bullet = new StringBuilder();
            var content = new List<INode>();
            bool inBullet = false, sawBullet = false;
            foreach (var n in p.ChildNodes)
            {
                if (n is IComment c)
                {
                    var d = c.Data.Trim();
                    if (d.Contains("supportLists", StringComparison.OrdinalIgnoreCase)) { inBullet = true; sawBullet = true; continue; }
                    if (inBullet && d.Contains("endif", StringComparison.OrdinalIgnoreCase)) { inBullet = false; continue; }
                    continue;
                }
                if (inBullet) { bullet.Append(n.TextContent); continue; }
                content.Add(n);
            }
            // No conditional comments (the HTML was cleaned by a client): the first span holding
            // "·", "o", "§" or "1." followed by spaces is the bullet.
            if (!sawBullet && content.FirstOrDefault(x => x is IElement || (x is IText t && t.Data.Trim().Length > 0)) is IElement first
                && first.LocalName == "span" && Regex.IsMatch(first.TextContent.Replace(' ', ' '), @"^\s*(\S{1,4})\s+$"))
            {
                bullet.Append(first.TextContent);
                content.Remove(first);
            }
            return (bullet.ToString().Replace(' ', ' ').Trim(), content);
        }

        // ---------- tables ----------

        private string Table(IElement table)
        {
            if (IsHidden(table)) return "";
            var rows = Rows(table);
            if (IsLayoutTable(table, rows))
            {
                // Unwrap: every cell's content, in reading order.
                var parts = new List<string>();
                foreach (var row in rows)
                    foreach (var cell in row.Cells)
                    {
                        var b = Blocks(cell.ChildNodes);
                        if (b.Trim().Length > 0) parts.Add(b);
                    }
                return string.Join("\n\n", parts);
            }

            var grid = new List<List<string>>();
            var aligns = new List<string?>();
            foreach (var row in rows)
            {
                var cells = new List<string>();
                foreach (var cell in row.Cells)
                {
                    var text = CellText(cell);
                    cells.Add(text);
                    int span = int.TryParse(cell.GetAttribute("colspan"), out var cs) ? Math.Clamp(cs, 1, 50) : 1;
                    for (int k = 1; k < span; k++) cells.Add("");
                    if (grid.Count == 0 || aligns.Count < cells.Count)
                        while (aligns.Count < cells.Count) aligns.Add(Align(cell));
                }
                if (cells.All(c => c.Length == 0)) continue;
                grid.Add(cells);
            }
            if (grid.Count == 0) return "";
            // Header cells render bold already; Word's explicit <b> there is noise.
            grid[0] = grid[0].Select(StripWrappingBold).ToList();
            int cols = grid.Max(r => r.Count);
            foreach (var r in grid) while (r.Count < cols) r.Add("");
            while (aligns.Count < cols) aligns.Add(null);

            var sb = new StringBuilder();
            sb.Append("| ").Append(string.Join(" | ", grid[0])).Append(" |\n");
            sb.Append('|').Append(string.Join("|", aligns.Take(cols).Select(a => a switch
            {
                "center" => " :---: ",
                "right" => " ---: ",
                "left" => " :--- ",
                _ => " --- ",
            }))).Append("|\n");
            foreach (var r in grid.Skip(1)) sb.Append("| ").Append(string.Join(" | ", r)).Append(" |\n");

            var caption = table.Children.FirstOrDefault(c => c.LocalName == "caption");
            var capText = caption is null ? "" : InlineText(caption.ChildNodes).Trim();
            return (capText.Length > 0 ? "**" + capText + "**\n\n" : "") + sb.ToString().TrimEnd('\n');
        }

        private sealed record Row(List<IElement> Cells);

        private static List<Row> Rows(IElement table)
        {
            var rows = new List<Row>();
            // Direct rows only (thead/tbody/tfoot are transparent); nested tables keep their own.
            foreach (var child in table.Children)
            {
                if (child.LocalName == "tr") rows.Add(new Row(child.Children.Where(c => c.LocalName is "td" or "th").ToList()));
                else if (child.LocalName is "thead" or "tbody" or "tfoot")
                    foreach (var tr in child.Children.Where(c => c.LocalName == "tr"))
                        rows.Add(new Row(tr.Children.Where(c => c.LocalName is "td" or "th").ToList()));
            }
            return rows;
        }

        private static readonly HashSet<string> StructuralInCell = new(StringComparer.OrdinalIgnoreCase)
        { "table", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "blockquote", "pre", "hr", "img" };

        private bool IsLayoutTable(IElement table, List<Row> rows)
        {
            var role = table.GetAttribute("role");
            if (role is "presentation" or "none") return true;
            if (rows.Count == 0) return true;
            int maxCols = rows.Max(r => r.Cells.Count);
            if (maxCols < 2) return true;               // single column: a wrapper, not data
            if (rows.Count < 2) return true;            // one row: side-by-side layout (logo | text)
            foreach (var row in rows)
                foreach (var cell in row.Cells)
                {
                    if (cell.QuerySelectorAll("*").Any(d => StructuralInCell.Contains(d.LocalName) && !IsTrackingPixel(d)))
                        return true;
                    // Several real paragraphs in one cell is prose, not a datum.
                    if (cell.Children.Count(c => c.LocalName is "p" or "div" && c.TextContent.Trim().Length > 0) > 2) return true;
                    if (cell.TextContent.Length > 600) return true;
                }
            return false;
        }

        private string CellText(IElement cell)
        {
            // Paragraphs inside a cell become <br>-separated lines; pipes are escaped.
            var blocks = Blocks(cell.ChildNodes).Trim('\n');
            blocks = Regex.Replace(blocks, @"\\\n", "<br>");
            blocks = Regex.Replace(blocks, @"\n+", "<br>");
            return blocks.Replace("|", "\\|").Trim();
        }

        private static string? Align(IElement cell)
        {
            var a = cell.GetAttribute("align")?.Trim().ToLowerInvariant();
            if (a is "left" or "center" or "right") return a;
            var m = Regex.Match(cell.GetAttribute("style") ?? "", @"text-align\s*:\s*(left|center|right)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            // Word puts the alignment on the cell's paragraph.
            var p = cell.Children.FirstOrDefault(c => c.LocalName is "p" or "div");
            if (p is not null)
            {
                var pa = p.GetAttribute("align")?.Trim().ToLowerInvariant();
                if (pa is "center" or "right") return pa;
                m = Regex.Match(p.GetAttribute("style") ?? "", @"text-align\s*:\s*(center|right)", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            }
            return null;
        }

        // ---------- inline ----------

        private string InlineText(INodeList nodes) => InlineText(nodes.AsEnumerable());

        private string InlineText(IEnumerable<INode> nodes)
        {
            var sb = new StringBuilder();
            AppendInline(sb, nodes);
            var s = sb.ToString();
            // Collapse spaces the element boundaries left behind, but never across a hard break.
            s = Regex.Replace(s, @"[ \t]{2,}", " ");
            s = Regex.Replace(s, @" ?\\\n ?", "\\\n");
            return s;
        }

        // Inline children grouped by formatting, so adjacent same-format runs merge into one span.
        private void AppendInline(StringBuilder sb, IEnumerable<INode> nodes)
        {
            string? groupKey = null;
            var group = new List<INode>();

            void FlushGroup()
            {
                if (group.Count == 0) return;
                if (groupKey is null) foreach (var g in group) AppendNode(sb, g);
                else AppendFormatted(sb, groupKey, group);
                group.Clear();
                groupKey = null;
            }

            foreach (var n in nodes)
            {
                var key = n is IElement e ? FormatKey(e) : null;
                if (key is not null && key == groupKey) { group.Add(n); continue; }
                FlushGroup();
                groupKey = key;
                group.Add(n);
            }
            FlushGroup();
        }

        private void AppendFormatted(StringBuilder sb, string key, List<INode> group)
        {
            var inner = new StringBuilder();
            foreach (var n in group)
            {
                if (n is IElement e) AppendInline(inner, e.ChildNodes);
                else AppendNode(inner, n);
            }
            var text = inner.ToString();
            if (text.Trim().Length == 0) { sb.Append(text.Length > 0 ? " " : ""); return; }

            // Move edge whitespace outside the markers: "**Hello** world", never "**Hello **world".
            var lead = text.Length - text.TrimStart().Length;
            var trail = text.Length - text.TrimEnd().Length;
            var core = text.Trim();
            if (lead > 0) sb.Append(' ');

            if (key.StartsWith("a:", StringComparison.Ordinal))
            {
                var href = key[2..];
                var plain = core.Trim('<', '>');
                if (href == plain || href == "mailto:" + plain) sb.Append('<').Append(plain).Append('>');
                else sb.Append('[').Append(core).Append("](").Append(LinkTarget(href)).Append(')');
            }
            else if (key == "code")
            {
                var raw = string.Concat(group.Select(g => g.TextContent)).Replace(' ', ' ').Trim();
                var longest = Regex.Matches(raw, "`+").Select(m => m.Length).DefaultIfEmpty(0).Max();
                var tick = new string('`', longest + 1);
                var pad = raw.StartsWith('`') || raw.EndsWith('`') ? " " : "";
                sb.Append(tick).Append(pad).Append(raw).Append(pad).Append(tick);
            }
            else
            {
                var (open, close) = key switch
                {
                    "b" => ("**", "**"),
                    "i" => ("*", "*"),
                    "bi" => ("***", "***"),
                    "s" => ("~~", "~~"),
                    "sup" => ("<sup>", "</sup>"),
                    "sub" => ("<sub>", "</sub>"),
                    "mark" => ("==", "=="),
                    _ => ("", ""),
                };
                // Markers can't span a hard break; close and reopen around it.
                sb.Append(open).Append(core.Replace("\\\n", close + "\\\n" + open)).Append(close);
            }
            if (trail > 0) sb.Append(' ');
        }

        private void AppendNode(StringBuilder sb, INode n)
        {
            switch (n)
            {
                case IText t:
                    sb.Append(Escape(Collapse(t.Data)));
                    break;
                case IElement e:
                    AppendElement(sb, e);
                    break;
            }
        }

        private void AppendElement(StringBuilder sb, IElement e)
        {
            if (IsHidden(e)) return;
            var tag = e.LocalName.ToLowerInvariant();
            if (SkipTags.Contains(tag)) return;
            switch (tag)
            {
                case "br":
                    sb.Append("\\\n");
                    return;
                case "img":
                    AppendImage(sb, e);
                    return;
                case "input":
                    if (string.Equals(e.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase))
                        sb.Append(e.HasAttribute("checked") ? "[x] " : "[ ] ");
                    return;
                case "wbr":
                    return;
            }
            if (IsBlock(e))
            {
                // A block inside inline content (e.g. <div> inside <a>): keep its text on a new line.
                var inner = Blocks(e.ChildNodes).Trim();
                if (inner.Length > 0) sb.Append("\\\n").Append(inner.Replace("\n\n", "\\\n")).Append("\\\n");
                return;
            }
            AppendInline(sb, e.ChildNodes);
        }

        private void AppendImage(StringBuilder sb, IElement img)
        {
            if (IsTrackingPixel(img)) return;
            var src = img.GetAttribute("src") ?? "";
            if (src.Length == 0) return;
            var resolved = _o.ResolveImage is null ? src : _o.ResolveImage(src);
            if (string.IsNullOrEmpty(resolved)) return;
            var alt = Collapse(img.GetAttribute("alt") ?? img.GetAttribute("title") ?? "").Trim();
            alt = alt.Replace("[", "\\[").Replace("]", "\\]");
            sb.Append("![").Append(alt).Append("](").Append(LinkTarget(resolved)).Append(')');
        }

        private static string LinkTarget(string url)
        {
            url = url.Trim();
            // Spaces and parentheses would end the link early; angle brackets keep it whole.
            return url.IndexOfAny(new[] { ' ', '(', ')' }) >= 0 ? "<" + url.Replace(">", "%3E") + ">" : url;
        }

        private static bool IsTrackingPixel(IElement e)
        {
            if (e.LocalName != "img") return false;
            static int? Px(string? v) => v is not null && int.TryParse(Regex.Match(v, @"^\s*(\d+)").Groups[1].Value, out var n) ? n : null;
            var w = Px(e.GetAttribute("width"));
            var h = Px(e.GetAttribute("height"));
            var style = e.GetAttribute("style") ?? "";
            var sw = Regex.Match(style, @"(?<![-\w])width\s*:\s*(\d+)px", RegexOptions.IgnoreCase);
            var sh = Regex.Match(style, @"(?<![-\w])height\s*:\s*(\d+)px", RegexOptions.IgnoreCase);
            if (sw.Success) w = int.Parse(sw.Groups[1].Value, CultureInfo.InvariantCulture);
            if (sh.Success) h = int.Parse(sh.Groups[1].Value, CultureInfo.InvariantCulture);
            return (w is <= 2 && h is <= 2) || (w is 0) || (h is 0);
        }

        // The formatting an inline element applies, or null for a transparent wrapper.
        private string? FormatKey(IElement e)
        {
            var tag = e.LocalName.ToLowerInvariant();
            switch (tag)
            {
                case "a":
                {
                    var href = e.GetAttribute("href")?.Trim();
                    if (string.IsNullOrEmpty(href) || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                        return null;
                    if (_o.UnwrapSafeLinks) href = UnwrapSafeLink(href);
                    return "a:" + href;
                }
                case "strong": case "b": return IsItalicStyle(e) ? "bi" : "b";
                case "em": case "i": case "cite": case "dfn": case "var": return IsBoldStyle(e) ? "bi" : "i";
                case "code": case "kbd": case "samp": case "tt": return "code";
                case "s": case "strike": case "del": return "s";
                case "sup": return "sup";
                case "sub": return "sub";
                case "mark": return "mark";
                case "span": case "font":
                {
                    if (IsMonospaceStyle(e)) return "code";
                    bool b = IsBoldStyle(e), i = IsItalicStyle(e);
                    if (b && i) return "bi";
                    if (b) return "b";
                    if (i) return "i";
                    if (Regex.IsMatch(e.GetAttribute("style") ?? "", @"text-decoration[^;]*line-through", RegexOptions.IgnoreCase)) return "s";
                    return null;
                }
                default: return null;
            }
        }

        private static bool IsBoldStyle(IElement e) =>
            Regex.IsMatch(e.GetAttribute("style") ?? "", @"font-weight\s*:\s*(bold|bolder|[6-9]00)", RegexOptions.IgnoreCase);

        private static bool IsItalicStyle(IElement e) =>
            Regex.IsMatch(e.GetAttribute("style") ?? "", @"font-style\s*:\s*italic", RegexOptions.IgnoreCase);

        private static bool IsMonospaceStyle(IElement e)
        {
            var style = e.GetAttribute("style") ?? "";
            var face = e.GetAttribute("face") ?? "";
            return Regex.IsMatch(style + ";" + face, @"(font-family\s*:[^;]*|^|;)\s*['""]?(Consolas|Courier|Courier New|Menlo|Monaco|monospace|Cascadia|Lucida Console)", RegexOptions.IgnoreCase);
        }

        private static bool IsHidden(IElement e)
        {
            var style = e.GetAttribute("style") ?? "";
            if (Regex.IsMatch(style, @"display\s*:\s*none|mso-hide\s*:\s*all|visibility\s*:\s*hidden", RegexOptions.IgnoreCase)) return true;
            if (e.HasAttribute("hidden")) return true;
            // MarkSmith's own export chrome (the "Made with" footer); the contents list is a <nav>,
            // skipped with every other navigation block — the TOC setting rebuilds it on export.
            if (e.ClassList.Contains("mark-footer")) return true;
            // Mail preheaders: tiny, transparent or zero-height text meant only for inbox previews.
            if (Regex.IsMatch(style, @"(max-height|font-size|line-height)\s*:\s*0(px)?\s*(;|$)", RegexOptions.IgnoreCase)
                && Regex.IsMatch(style, @"overflow\s*:\s*hidden|opacity\s*:\s*0|font-size\s*:\s*0", RegexOptions.IgnoreCase)) return true;
            return false;
        }

        internal static string UnwrapSafeLink(string href)
        {
            if (!href.Contains("safelinks.protection.outlook.com", StringComparison.OrdinalIgnoreCase)) return href;
            var m = Regex.Match(href, @"[?&]url=([^&]+)", RegexOptions.IgnoreCase);
            if (!m.Success) return href;
            try { return Uri.UnescapeDataString(m.Groups[1].Value); } catch { return href; }
        }

        // Whitespace as a browser renders it: runs collapse to one space; nbsp counts as a space.
        private static string Collapse(string s) => Regex.Replace(s.Replace(' ', ' '), @"\s+", " ");

        // Escape only what would otherwise become Markdown syntax mid-line.
        private static string Escape(string s)
        {
            if (s.Length == 0) return s;
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': case '`': case '*': case '[': case ']':
                        sb.Append('\\').Append(c);
                        break;
                    case '_':
                        // Intraword underscores (snake_case) are literal in CommonMark already.
                        bool word = i > 0 && char.IsLetterOrDigit(s[i - 1]) && i + 1 < s.Length && char.IsLetterOrDigit(s[i + 1]);
                        sb.Append(word ? "_" : "\\_");
                        break;
                    case '<':
                        sb.Append(i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] is '/' or '!' or '?') ? "\\<" : "<");
                        break;
                    case '|':
                        sb.Append('|'); // escaped only inside table cells (CellText)
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // Lets the walker treat a filtered child sequence like an INodeList.
    private sealed class NodeListView : INodeList
    {
        private readonly List<INode> _nodes;
        public NodeListView(IEnumerable<INode> nodes) => _nodes = nodes.ToList();
        public INode this[int index] => _nodes[index];
        public int Length => _nodes.Count;
        public IEnumerator<INode> GetEnumerator() => _nodes.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _nodes.GetEnumerator();
        public void ToHtml(TextWriter writer, AngleSharp.IMarkupFormatter formatter)
        {
            foreach (var n in _nodes) n.ToHtml(writer, formatter);
        }
    }
}
