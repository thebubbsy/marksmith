using System.Text;

namespace MarkSmith.Core.Preview;

/// <summary>
/// Word-style pagination for the HTML preview, used when DOCX is the default output format.
///
/// The preview renders as real Word pages (Letter, 1" margins, Word typography, per-page footers
/// with "Page N of M"), and page breaks follow Word's rules: the engine REASONS about the elements
/// around each break instead of overlaying them —
///
///   • Paragraphs and code blocks SPLIT at line boundaries and flow onto the next page (Word does
///     this with body text and long code listings).
///   • Keep-together blocks (mermaid/plugin diagrams, shape canvases, tables, figures, images) are
///     NEVER split: if one doesn't fit the remaining space it moves WHOLE to the next page.
///   • A keep-together block taller than a full page is SCALED to fit the content box (Word shrinks
///     oversized diagrams) — never clipped, never overlapped.
///   • Headings keep with the paragraph that follows them (keep-with-next).
///
/// Pagination is two-pass: this service estimates heights with Word metrics and packs deterministically
/// (unit-tested); the injected script then re-measures the REAL rendered heights, moves overflow
/// blocks whole to the next page, and scales any remaining oversized diagrams — so breaks land within
/// a line or two of Word with no element ever straddling or covering another.
/// </summary>
public static class WordLikePageService
{
    // ── Word geometry (US Letter, Word defaults) ──────────────────────────────────────────────
    public const int PageWidthPx = 816;     // 8.5in @96dpi
    public const int PageHeightPx = 1056;   // 11in @96dpi
    public const int MarginPx = 96;         // 1in
    public const int ContentWidthPx = PageWidthPx - 2 * MarginPx;   // 624
    public const int ContentHeightPx = PageHeightPx - 2 * MarginPx; // 864

    public sealed record Page(string Html, int Number, int Total);
    public sealed record Pagination(List<Page> Pages);

    // ── Block splitting: top-level elements of the canvas inner HTML ─────────────────────────
    /// <summary>Plain wrapper <c>&lt;div&gt;</c> containers (section-like) that Word flows across
    /// pages: their block-level children are emitted as top-level blocks instead of the wrapper
    /// being treated as one unsplittable keep-together lump. Diagram/shape/TOC/attribution
    /// wrappers are NOT transparent — they stay whole.</summary>
    public static bool IsTransparentWrapper(string block)
    {
        string t = block.TrimStart();
        if (!t.StartsWith("<div", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsKeepTogether(block)) return false;
        if (t.Contains("toc", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("attribution", StringComparison.OrdinalIgnoreCase)) return false;
        // Only recurse when the div actually contains block-level children to flow.
        return t.Contains("<table", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("<div", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("<ul", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("<ol", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("<pre", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("<figure", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("<img", StringComparison.OrdinalIgnoreCase);
    }

    public static List<string> SplitTopLevelBlocks(string html)
    {
        var blocks = new List<string>();
        int i = 0, n = html.Length;
        while (i < n)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0) break;
            if (lt > i && !string.IsNullOrWhiteSpace(html[i..lt]))
                blocks.Add("<p>" + html[i..lt] + "</p>"); // loose text (shouldn't happen from Markdig)

            int depth = 0, j = lt;
            while (j < n)
            {
                int nextLt = html.IndexOf('<', j);
                if (nextLt < 0) break;
                int gt = html.IndexOf('>', nextLt);
                if (gt < 0) break;
                string tag = html[(nextLt + 1)..gt].Trim();
                if (tag.StartsWith("!--"))
                {
                    // Comments carry no layout: skip (never emitted as blocks).
                    int close = html.IndexOf("-->", nextLt + 4);
                    j = close >= 0 ? close + 3 : gt + 1;
                    if (depth == 0) { i = j; lt = j; }
                    continue;
                }
                if (tag.StartsWith("!")) { j = gt + 1; continue; }
                if (tag.StartsWith("/"))
                {
                    depth--;
                    j = gt + 1;
                    if (depth == 0)
                    {
                        string block = html[lt..(gt + 1)];
                        // Transparent wrapper: flow its children as top-level blocks (Word does).
                        if (IsTransparentWrapper(block))
                        {
                            int openEnd = block.IndexOf('>');
                            string inner = block[(openEnd + 1)..];
                            if (inner.EndsWith("</div>", StringComparison.OrdinalIgnoreCase))
                                inner = inner[..^"</div>".Length];
                            blocks.AddRange(SplitTopLevelBlocks(inner));
                        }
                        else
                        {
                            blocks.Add(block);
                        }
                        break;
                    }
                }
                else
                {
                    // Self-closing tags (<hr />, <br />, <img … />) must NOT open a level; a
                    // self-closing tag at depth 0 is its own top-level block.
                    bool selfClosing = tag.TrimEnd().EndsWith("/");
                    if (selfClosing)
                    {
                        j = gt + 1;
                        if (depth == 0) { blocks.Add(html[lt..(gt + 1)]); break; }
                    }
                    else
                    {
                        depth++;
                        j = gt + 1;
                    }
                }
            }
            if (depth != 0 && j >= n) { blocks.Add(html[lt..]); break; }
            i = depth == 0 && j < n ? j : n;
            if (blocks.Count > 0 && blocks[^1] == html[lt..Math.Min(j, n)] && j >= n) break;
        }
        return blocks.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
    }

    // ── Height estimation with Word metrics (the JS pass re-measures) ────────────────────────
    private static readonly double BodyFontPx = 11.0 * 96.0 / 72.0;  // 14.667
    private static readonly double BodyLinePx = BodyFontPx * 1.08;   // 15.84
    private const double AvgCharEm = 0.50;

    private static int CharsPerLine(double fontSizePx) =>
        Math.Max(10, (int)(ContentWidthPx / (AvgCharEm * fontSizePx)));

    public static int EstimateHeight(string block)
    {
        string b = block.TrimStart();
        if (b.StartsWith("<h1", StringComparison.OrdinalIgnoreCase)) return (int)(14 * 1.2 + 16 * 1.2 + 14);
        if (b.StartsWith("<h2", StringComparison.OrdinalIgnoreCase)) return (int)(10 + 16 * 1.2 + 12);
        if (b.StartsWith("<h3", StringComparison.OrdinalIgnoreCase)) return (int)(8 + 14 * 1.2 + 10);
        if (b.StartsWith("<h4", StringComparison.OrdinalIgnoreCase)) return (int)(6 + 12 * 1.2 + 8);
        if (b.StartsWith("<h5", StringComparison.OrdinalIgnoreCase)) return (int)(4 + 11 * 1.2 + 6);
        if (b.StartsWith("<h6", StringComparison.OrdinalIgnoreCase)) return (int)(4 + 11 * 1.2 + 6);
        if (b.StartsWith("<hr", StringComparison.OrdinalIgnoreCase)) return 22;
        if (b.StartsWith("<table", StringComparison.OrdinalIgnoreCase))
            return Math.Max(40, CountOccurrences(b, "<tr") * 26 + 24);
        if (b.StartsWith("<pre", StringComparison.OrdinalIgnoreCase))
        {
            // white-space: pre — height is NEWLINES, not wrapped width. 9.5pt mono ≈ 12.67px,
            // line-height 1.08 ≈ 13.68px, padding 8pt + margin 8pt.
            string text = StripTags(b);
            int lines = 1;
            foreach (char c in text) if (c == '\n') lines++;
            return (int)(lines * 13.7) + 26;
        }
        if (b.StartsWith("<img", StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith("<figure", StringComparison.OrdinalIgnoreCase))
        {
            int h = ExtractIntAttr(b, "height");
            return h > 0 ? h + 12 : 220;
        }
        if (b.StartsWith("<div", StringComparison.OrdinalIgnoreCase))
        {
            // Diagrams / shape canvases: sum the explicit heights of nested <svg>/<img> fragments.
            int h = ExtractIntAttr(b, "height");
            if (h <= 0)
            {
                int sum = 0;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    b, @"<(?:svg|img)[^>]*?height[^0-9]*([0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    sum += int.Parse(m.Groups[1].Value);
                }
                if (sum > 0) return sum + 14 * CountOccurrences(b, "<svg");
            }
            return h > 0 ? h : 240;
        }
        if (b.StartsWith("<ul", StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith("<ol", StringComparison.OrdinalIgnoreCase))
            return Math.Max(20, CountOccurrences(b, "<li") * (int)BodyLinePx + 8);
        if (b.StartsWith("<blockquote", StringComparison.OrdinalIgnoreCase))
            return (int)(EstimateTextHeight(StripTags(b)) * 1.06) + 8;
        return EstimateTextHeight(StripTags(b));
    }

    private static int EstimateTextHeight(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (int)BodyLinePx;
        int cpl = CharsPerLine(BodyFontPx);
        int lines = Math.Max(1, (int)Math.Ceiling(text.Length / (double)cpl));
        return (int)(lines * BodyLinePx) + 8; // trailing 8pt space-after
    }

    private static string StripTags(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");

    private static int ExtractIntAttr(string html, string attr)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html, attr + @"\s*=\s*""?(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static int CountOccurrences(string s, string needle)
    {
        int c = 0, idx = 0;
        while ((idx = s.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase)) >= 0) { c++; idx += needle.Length; }
        return c;
    }

    private static bool IsHeading(string b) =>
        b.TrimStart().StartsWith("<h1", StringComparison.OrdinalIgnoreCase) ||
        b.TrimStart().StartsWith("<h2", StringComparison.OrdinalIgnoreCase) ||
        b.TrimStart().StartsWith("<h3", StringComparison.OrdinalIgnoreCase) ||
        b.TrimStart().StartsWith("<h4", StringComparison.OrdinalIgnoreCase) ||
        b.TrimStart().StartsWith("<h5", StringComparison.OrdinalIgnoreCase) ||
        b.TrimStart().StartsWith("<h6", StringComparison.OrdinalIgnoreCase);

    /// <summary>Blocks Word never splits mid-element: a diagram, shape canvas, table, figure or
    /// image that doesn't fit the remaining space moves WHOLE to the next page.</summary>
    public static bool IsKeepTogether(string b)
    {
        string t = b.TrimStart();
        return t.StartsWith("<table", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<figure", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<img", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<div class=\"mermaid", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<div class=\"plugin-diagram", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<div class=\"shape", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("plugin-diagram", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("data-diagram-type", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Blocks that can split at line/row boundaries and flow across pages (Word body
    /// text, code listings, list items).</summary>
    public static bool IsSplittable(string b)
    {
        string t = b.TrimStart();
        return t.StartsWith("<p", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<pre", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<ul", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<ol", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<blockquote", StringComparison.OrdinalIgnoreCase);
    }

    // ── The packer ────────────────────────────────────────────────────────────────────────────
    public static Pagination Paginate(List<string> blocks)
    {
        var pages = new List<Page>();
        var current = new List<string>();
        int used = 0;

        for (int i = 0; i < blocks.Count; i++)
        {
            string b = blocks[i];
            int h = EstimateHeight(b);
            bool keep = IsKeepTogether(b);
            bool heading = IsHeading(b);

            if (used + h <= ContentHeightPx)
            {
                // Keep-with-next: a heading that would be the last block of a page moves with the
                // following block (Word's keepNext on heading styles).
                if (heading && i + 1 < blocks.Count && used + h + EstimateHeight(blocks[i + 1]) > ContentHeightPx)
                {
                    pages.Add(MakePage(current));
                    current.Clear(); used = 0;
                    current.Add(b); used = h;
                }
                else
                {
                    current.Add(b); used += h;
                }
                continue;
            }

            // Doesn't fit the remaining space.
            if (keep || h > ContentHeightPx && !IsSplittable(b))
            {
                // Keep-together (or a single non-splittable oversized block): move WHOLE to the
                // next page. An oversized diagram is scaled by the repack/CSS, never clipped.
                if (current.Count > 0) { pages.Add(MakePage(current)); current.Clear(); used = 0; }
                current.Add(b); used = h;
            }
            else if (IsSplittable(b))
            {
                foreach (var chunk in SplitBlock(b, ContentHeightPx - used, h))
                {
                    if (used + EstimateHeight(chunk) <= ContentHeightPx || current.Count == 0 && used == 0)
                    {
                        current.Add(chunk); used += EstimateHeight(chunk);
                    }
                    else
                    {
                        if (current.Count > 0) pages.Add(MakePage(current));
                        current.Clear(); used = 0;
                        current.Add(chunk); used = EstimateHeight(chunk);
                    }
                }
            }
            else
            {
                // Plain overflow fallback: move whole.
                if (current.Count > 0) { pages.Add(MakePage(current)); current.Clear(); used = 0; }
                current.Add(b); used = h;
            }
        }
        if (current.Count > 0) pages.Add(MakePage(current));

        // Renumber footers (the HTML still says "Page 0 of 0" until now).
        var result = pages.Select((p, idx) =>
        {
            int num = idx + 1, total = pages.Count;
            string html = p.Html.Replace(
                "<div class=\"wp-footer\">Page 0 of 0</div>",
                $"<div class=\"wp-footer\">Page {num} of {total}</div>");
            return new Page(html, num, total);
        }).ToList();
        return new Pagination(result);
    }

    private static List<string> SplitBlock(string block, int remaining, int totalH)
    {
        string t = block.TrimStart();
        if (t.StartsWith("<pre", StringComparison.OrdinalIgnoreCase)) return SplitCodeBlock(block, remaining);
        if (t.StartsWith("<ul", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<ol", StringComparison.OrdinalIgnoreCase))
            return SplitList(block, remaining);
        return SplitParagraph(block, remaining);
    }

    private static List<string> SplitCodeBlock(string block, int remaining)
    {
        int linesBudget = Math.Max(2, remaining / 14);
        var m = System.Text.RegularExpressions.Regex.Match(block, @"^(<pre[^>]*>)(<code[^>]*>)(.*?)(</code>)(</pre>)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success) return new List<string> { block };
        string openPre = m.Groups[1].Value, openCode = m.Groups[2].Value;
        string text = m.Groups[3].Value, closeCode = m.Groups[4].Value, closePre = m.Groups[5].Value;

        int idx = -1, line = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                if (line >= linesBudget) { idx = i; break; }
            }
        }
        if (idx < 0) return new List<string> { block };

        string a = text[..idx], b = text[(idx + 1)..];
        var list = new List<string>();
        if (a.Length > 0) list.Add(openPre + openCode + a + closeCode + closePre);
        if (b.Length > 0) list.Add(openPre + openCode + b + closeCode + closePre);
        return list.Count > 0 ? list : new List<string> { block };
    }

    private static List<string> SplitList(string block, int remaining)
    {
        // Split a <ul>/<ol> at an <li> boundary so list items flow whole across pages.
        int budget = Math.Max(1, remaining / (int)BodyLinePx);
        string open = block[..(block.IndexOf('>') + 1)];
        string close = "</" + (block.TrimStart().StartsWith("<ul", StringComparison.OrdinalIgnoreCase) ? "ul" : "ol") + ">";
        string inner = block[(block.IndexOf('>') + 1)..];
        if (inner.EndsWith(close, StringComparison.OrdinalIgnoreCase)) inner = inner[..^close.Length];

        var items = new List<string>();
        int idx = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(inner, "<li[^>]*>.*?</li>", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            items.Add(m.Value);
            idx += m.Value.Length;
        }
        if (items.Count < 2) return new List<string> { block };

        var a = new List<string>(); var b = new List<string>();
        int count = 0;
        bool first = true;
        foreach (var it in items)
        {
            (first ? a : b).Add(it);
            if (first) { count++; if (count >= budget) first = false; }
        }
        var list = new List<string>();
        if (a.Count > 0) list.Add(open + string.Concat(a) + close);
        if (b.Count > 0) list.Add(open + string.Concat(b) + close);
        return list.Count > 0 ? list : new List<string> { block };
    }

    private static List<string> SplitParagraph(string block, int remaining)
    {
        string inner = StripTags(block);
        int cpl = CharsPerLine(BodyFontPx);
        int firstLines = Math.Max(2, remaining / (int)BodyLinePx);
        int cut = Math.Min(inner.Length, firstLines * cpl);
        if (cut <= 0) return new List<string> { block };
        int sp = inner.LastIndexOf(' ', Math.Min(cut, inner.Length - 1));
        if (sp > cut / 2) cut = sp;
        string a = inner[..cut].TrimEnd(), b = inner[cut..].TrimStart();
        var list = new List<string>();
        if (a.Length > 0) list.Add("<p>" + a + "</p>");
        if (b.Length > 0) list.Add("<p>" + b + "</p>");
        return list.Count > 0 ? list : new List<string> { block };
    }

    private static Page MakePage(List<string> blocks)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"wp-page\" data-wp-page=\"true\"><div class=\"wp-page-inner\">");
        sb.Append("<div class=\"wp-header\"></div>");
        sb.Append("<div class=\"wp-content\">");
        foreach (var b in blocks) sb.Append(b);
        sb.Append("</div>");
        sb.Append("<div class=\"wp-footer\">Page 0 of 0</div>");
        sb.Append("</div></div>");
        return new Page(sb.ToString(), 0, 0);
    }

    // ── Full-document transform ───────────────────────────────────────────────────────────────
    /// <summary>Paged-view CSS, coloured by the SELECTED THEME (page background/text/headings come
    /// from the theme, exactly like the DOCX export) — the content typography is inherited from the
    /// base preview styles, so the pages look like our premium Markdown experience, not a generic
    /// white Word page.</summary>
    public static string BuildCss(string pageBg, string textHex, string borderHex) => $$"""
        /* Word-style pagination (WordLikePageService) — theme-coloured */
        body.wp-paged { background: #4f4f55; }
        body.wp-paged #canvas { padding: 28px 0 !important; width: auto !important; min-width: 0 !important;
                                max-width: none !important; margin: 0 !important; background: transparent !important;
                                box-shadow: none !important; border: none !important; border-radius: 0 !important;
                                display: flex; flex-direction: column; align-items: center; }
        .wp-page { width: 816px; min-width: 816px; height: 1056px; margin: 0 0 28px; background: {{pageBg}}; color: {{textHex}};
                   box-shadow: 0 2px 12px rgba(0,0,0,.45); position: relative; overflow: hidden; }
        .wp-page-inner { position: relative; width: 100%; height: 100%; box-sizing: border-box; }
        .wp-content { position: absolute; top: 96px; left: 96px; right: 96px; bottom: 96px; overflow: hidden; }
        .wp-header { position: absolute; top: 48px; left: 96px; right: 96px; height: 30px; overflow: hidden;
                     color: {{textHex}}; opacity: .6; font-size: 12px; }
        .wp-footer { position: absolute; bottom: 42px; left: 96px; right: 96px; height: 24px; overflow: hidden;
                     text-align: center; color: {{textHex}}; opacity: .6; font-size: 12px; }
        .wp-content h1, .wp-content h2, .wp-content h3, .wp-content h4, .wp-content h5, .wp-content h6 { break-after: avoid; page-break-after: avoid; }
        /* Keep-together diagrams never overlap the next page: they scale to fit the content box
           (Word shrinks oversized diagrams), so a break can never cut one in half. */
        .wp-content svg, .wp-content img { max-width: 100%; max-height: 100%; width: auto; height: auto; }
        .wp-content .mermaid, .wp-content .plugin-diagram,
        .wp-content div[class*="shape"] { max-width: 100%; }
        """;

    public const string RepackScript = """
        <script>
        (function(){
          function keepTogether(el){
            if(!el || el.nodeType !== 1) return false;
            var c = el.className || '', t = (el.tagName||'').toUpperCase();
            return t === 'TABLE' || t === 'IMG' || t === 'FIGURE' || t === 'SVG' ||
                   c.indexOf('mermaid') >= 0 || c.indexOf('plugin-diagram') >= 0 || c.indexOf('shape') >= 0;
          }
          function repack(){
            var pages=[].slice.call(document.querySelectorAll('.wp-page'));
            if(pages.length<2) return;
            for(var pass=0;pass<4;pass++){
              var changed=false;
              // Bottom-up: push overflow whole blocks to the next page (Word never splits diagrams/tables).
              for(var i=pages.length-1;i>0;i--){
                var c=pages[i-1].querySelector('.wp-content'), c2=pages[i].querySelector('.wp-content');
                var guard=0;
                while(c.scrollHeight>c.clientHeight+2 && guard++<80){
                  var kids=[].slice.call(c.children);
                  var last=kids[kids.length-1];
                  if(!last) break;
                  var moving=[last];
                  if(/^H[1-6]$/.test(last.tagName) && last.nextElementSibling) moving.push(last.nextElementSibling);
                  moving.forEach(function(el){ c.removeChild(el); c2.insertBefore(el, c2.firstChild); });
                  changed=true;
                }
              }
              if(!changed) break;
            }
            // Content still overflowing the LAST page flows onto a freshly created page (Word
            // never clips — it adds a page), then footers are renumbered.
            var guardPages=0;
            while(guardPages++<20){
              var all=[].slice.call(document.querySelectorAll('.wp-page'));
              var last=all[all.length-1];
              var lc=last.querySelector('.wp-content');
              if(lc.scrollHeight<=lc.clientHeight+2) break;
              var np=last.cloneNode(true);
              np.querySelector('.wp-content').innerHTML='';
              last.parentNode.insertBefore(np,last.nextSibling);
              var npc=np.querySelector('.wp-content');
              var g=0;
              while(lc.scrollHeight>lc.clientHeight+2 && g++<60){
                var kids=[].slice.call(lc.children);
                var kid=kids[kids.length-1];
                if(!kid) break;
                lc.removeChild(kid);
                npc.insertBefore(kid,npc.firstChild);
              }
            }
            var total=[].slice.call(document.querySelectorAll('.wp-page'));
            total.forEach(function(p,i){ var f=p.querySelector('.wp-footer'); if(f) f.textContent='Page '+(i+1)+' of '+total.length; });
            // Scale any keep-together block still taller than its page (oversized diagram) to fit
            // — amend the element instead of clipping it.
            document.querySelectorAll('.wp-page .wp-content > *').forEach(function(el){
              var content=el.parentElement; if(!content) return;
              var maxH=content.clientHeight;
              if(el.scrollHeight>maxH+2 && keepTogether(el)){
                var s=maxH/el.scrollHeight;
                el.style.height=maxH+'px';
                el.style.overflow='hidden';
                el.style.transformOrigin='top left';
                el.style.transform='scale('+s.toFixed(4)+')';
                el.style.width=(el.scrollWidth*s)+'px';
              }
            });
          }
          function settle(){ requestAnimationFrame(function(){ requestAnimationFrame(repack); }); }
          if(document.readyState==='complete') settle();
          else window.addEventListener('load', settle);
          if(document.fonts && document.fonts.ready) document.fonts.ready.then(settle);
          // Mermaid / async images land LATE — keep re-measuring until the layout is stable so a
          // diagram that renders after the first pass can never leave an element straddling a
          // page break or clipped under one.
          var stableRuns = 0;
          var stabilityTimer = setInterval(function(){
            var before = document.body.scrollHeight;
            repack();
            stableRuns = (document.body.scrollHeight === before) ? stableRuns + 1 : 0;
            if (stableRuns >= 2 || stableRuns > 24) clearInterval(stabilityTimer);
          }, 1200);
        })();
        </script>
        """;

    /// <summary>Transforms a fully-rendered preview document into the Word-style paged layout,
    /// colouring the pages with the SELECTED THEME (page background/text from the theme — the
    /// preview shows what our DOCX export makes Word look like). Returns the document unchanged
    /// when the canvas markers are absent.</summary>
    public static string BuildPagedDocument(string fullHtml, string pageBg, string textHex, string borderHex)
    {
        const string startMarker = "<!--ms-canvas-start-->";
        const string endMarker = "<!--ms-canvas-end-->";
        int s = fullHtml.IndexOf(startMarker, StringComparison.Ordinal);
        int e = fullHtml.IndexOf(endMarker, StringComparison.Ordinal);
        if (s < 0 || e < 0 || e <= s) return fullHtml;

        int innerStart = s + startMarker.Length;
        string inner = fullHtml[innerStart..e];
        var blocks = SplitTopLevelBlocks(inner);
        if (blocks.Count == 0) return fullHtml;

        var pagination = Paginate(blocks);
        var sb = new StringBuilder();
        foreach (var page in pagination.Pages) sb.Append(page.Html);

        string paged = fullHtml[..innerStart] + sb + fullHtml[e..];
        paged = paged.Replace("<body class=\"", "<body class=\"wp-paged ", StringComparison.Ordinal);
        int styleEnd = paged.LastIndexOf("</style>", StringComparison.Ordinal);
        if (styleEnd >= 0) paged = paged.Insert(styleEnd, "\n" + BuildCss(pageBg, textHex, borderHex));
        int bodyEnd = paged.LastIndexOf("</body>", StringComparison.Ordinal);
        if (bodyEnd >= 0) paged = paged.Insert(bodyEnd, RepackScript);
        return paged;
    }
}
