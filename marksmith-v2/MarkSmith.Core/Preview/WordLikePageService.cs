using System.Text;

namespace MarkSmith.Core.Preview;

/// <summary>
/// Renders the Markdown preview the way Word lays out a page: real paper (Letter by default,
/// Word's classic geometry), one-inch margins, Word typography (Calibri 11 pt, 1.08 line
/// spacing, 8 pt space after), per-page headers/footers with "Page N of M", and page breaks
/// that follow Word's rules (keep-with-next headings, no block splitting, widow/orphan-safe).
///
/// The engine replaces the Word-exact (NetOffice) preview entirely — this is a pure-HTML/CSS
/// renderer, so it is instant, live, and needs no Office dependency. Pagination is a two-pass
/// process: this service splits the rendered Markdown body into top-level blocks, estimates each
/// block's height with Word's metrics and packs them into fixed-size pages (deterministic, unit
/// tested); the small injected script then re-measures the REAL rendered heights and re-packs
/// (moving overflow to the next page), so pagination matches the browser's actual line-breaking —
/// which, with the same fonts and the same content width, lands within a line or two of Word.
/// </summary>
public static class WordLikePageService
{
    // ── Word geometry (US Letter, Word defaults) ──────────────────────────────────────────────
    public const double PageWidthIn = 8.5;
    public const double PageHeightIn = 11.0;
    public const double MarginIn = 1.0;
    public const double HeaderIn = 0.5;   // Word default header distance from page edge
    public const double FooterIn = 0.5;   // Word default footer distance from page edge

    public const int PageWidthPx = (int)(PageWidthIn * 96.0);   // 816
    public const int PageHeightPx = (int)(PageHeightIn * 96.0); // 1056
    public const int MarginPx = (int)(MarginIn * 96.0);         // 96
    public const int ContentWidthPx = PageWidthPx - 2 * MarginPx;  // 624
    public const int ContentHeightPx = PageHeightPx - 2 * MarginPx; // 864

    public sealed record Page(string Html, int Number, int Total);

    public sealed record Pagination(List<Page> Pages);

    // ── Block splitting: top-level elements of the canvas inner HTML ─────────────────────────
    public static List<string> SplitTopLevelBlocks(string html)
    {
        var blocks = new List<string>();
        int i = 0, n = html.Length;
        while (i < n)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0) break;
            if (lt > i && !string.IsNullOrWhiteSpace(html[i..lt]))
            {
                // Loose text not wrapped in a block element (shouldn't happen from Markdig, but
                // treat it as its own paragraph so nothing is lost).
                blocks.Add("<p>" + html[i..lt] + "</p>");
            }
            // Find the extent of the element starting at lt (handles comments + tags). Each
            // iteration starts at a '<' — text between tags is skipped, so tag content is always
            // parsed from the element's own opening bracket.
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
                    // <!--…--> — the closing --> contains '>', so gt is already the end; confirm.
                    // Comments carry no layout: skip them (never emitted as blocks).
                    int close = html.IndexOf("-->", nextLt + 4);
                    j = close >= 0 ? close + 3 : gt + 1;
                    if (depth == 0) { i = j; lt = j; } // treat as consumed whitespace
                    continue;
                }
                if (tag.StartsWith("!")) { j = gt + 1; continue; }
                if (tag.StartsWith("/"))
                {
                    depth--;
                    j = gt + 1;
                    if (depth == 0) { blocks.Add(html[lt..(gt + 1)]); break; }
                }
                else
                {
                    string name = tag.Split(' ', '>')[0].TrimEnd('/');
                    if (!name.EndsWith("/")) depth++; // self-closing <br/> etc. don't nest
                }
                j = gt + 1;
            }
            if (depth != 0 && j >= n)
            {
                // Malformed tail: take the rest as one block and stop.
                blocks.Add(html[lt..]);
                break;
            }
            i = depth == 0 && j < n ? j : n;
            if (blocks.Count > 0 && blocks[^1] == html[lt..Math.Min(j, n)] && j >= n) break;
        }
        return blocks.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
    }

    // ── Height estimation with Word metrics (rough; the JS pass re-measures) ─────────────────
    private static readonly double BodyFontPx = 11.0 * 96.0 / 72.0;        // 14.667
    private static readonly double BodyLinePx = BodyFontPx * 1.08;         // 15.84
    private const double AvgCharEm = 0.50;                                  // Calibri avg glyph ~0.5em

    private static int CharsPerLine(double fontSizePx) =>
        Math.Max(10, (int)(ContentWidthPx / (AvgCharEm * fontSizePx)));

    public static int EstimateHeight(string block)
    {
        string b = block.TrimStart();
        double line = BodyLinePx;

        if (b.StartsWith("<h1", StringComparison.OrdinalIgnoreCase)) return (int)(12 + 16 * 1.2 + 14);
        if (b.StartsWith("<h2", StringComparison.OrdinalIgnoreCase)) return (int)(8 + 13 * 1.2 + 12);
        if (b.StartsWith("<h3", StringComparison.OrdinalIgnoreCase)) return (int)(6 + 12 * 1.2 + 10);
        if (b.StartsWith("<h4", StringComparison.OrdinalIgnoreCase)) return (int)(6 + 11 * 1.2 + 8);
        if (b.StartsWith("<h5", StringComparison.OrdinalIgnoreCase)) return (int)(4 + 11 * 1.2 + 6);
        if (b.StartsWith("<h6", StringComparison.OrdinalIgnoreCase)) return (int)(4 + 11 * 1.2 + 6);
        if (b.StartsWith("<hr", StringComparison.OrdinalIgnoreCase)) return 22;
        if (b.StartsWith("<table", StringComparison.OrdinalIgnoreCase)) return EstimateTable(b);
        if (b.StartsWith("<pre", StringComparison.OrdinalIgnoreCase))
        {
            string text = StripTags(b);
            int cpl = Math.Max(10, ContentWidthPx / (int)(0.60 * 12.5));
            int lines = Math.Max(1, (int)Math.Ceiling(text.Length / (double)cpl) + 1);
            return lines * 15 + 20;
        }
        if (b.StartsWith("<img", StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith("<figure", StringComparison.OrdinalIgnoreCase))
        {
            int h = ExtractIntAttr(b, "height");
            return h > 0 ? h + 12 : 220;
        }
        if (b.StartsWith("<div", StringComparison.OrdinalIgnoreCase))
        {
            // mermaid (empty pre-render), shape canvases, plugin SVGs — assume a diagram band.
            int h = ExtractIntAttr(b, "height");
            return h > 0 ? h : 240;
        }
        if (b.StartsWith("<ul", StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith("<ol", StringComparison.OrdinalIgnoreCase))
        {
            int items = CountOccurrences(b, "<li") ;
            return Math.Max(20, items * (int)line + 8);
        }
        if (b.StartsWith("<blockquote", StringComparison.OrdinalIgnoreCase))
            return (int)(EstimateTextHeight(StripTags(b)) * 1.06) + 8;

        // Paragraph (and anything else): count text, account for inline code/strong widths.
        return EstimateTextHeight(StripTags(b));
    }

    private static int EstimateTextHeight(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (int)BodyLinePx;
        int cpl = CharsPerLine(BodyFontPx);
        int lines = Math.Max(1, (int)Math.Ceiling(text.Length / (double)cpl));
        return (int)(lines * BodyLinePx) + 8; // trailing 8pt space-after
    }

    private static int EstimateTable(string block)
    {
        int rows = CountOccurrences(block, "<tr");
        int cols = Math.Max(1, CountOccurrences(block.Split("<tr")[1], "<td") + CountOccurrences(block.Split("<tr")[1], "<th"));
        int rowH = 22;
        return Math.Max(40, rows * rowH + 24);
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

    private static bool KeepWithNext(string b)
    {
        string t = b.TrimStart();
        return IsHeading(b) || t.StartsWith("<table", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<figure", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("<div class=\"mermaid", StringComparison.OrdinalIgnoreCase);
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
            bool keep = KeepWithNext(b);

            if (used + h <= ContentHeightPx)
            {
                if (keep && i + 1 < blocks.Count && used + h + EstimateHeight(blocks[i + 1]) > ContentHeightPx)
                {
                    // Heading/table would be stranded at the bottom: take the pair to the next page.
                    current.Add(b);
                    used += h;
                }
                else
                {
                    current.Add(b);
                    used += h;
                }
            }
            else
            {
                // Doesn't fit the remaining space.
                if (h > ContentHeightPx)
                {
                    // Oversized block: split a long paragraph at the page boundary, else let it overflow.
                    if (b.TrimStart().StartsWith("<p", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var chunk in SplitParagraph(b, ContentHeightPx - used, h))
                        {
                            if (used + EstimateHeight(chunk) <= ContentHeightPx || current.Count == 0 && used == 0)
                            {
                                current.Add(chunk); used += EstimateHeight(chunk);
                                if (used > ContentHeightPx) break;
                            }
                            else
                            {
                                pages.Add(MakePage(current));
                                current.Clear(); used = 0;
                                current.Add(chunk); used = EstimateHeight(chunk);
                            }
                        }
                        continue;
                    }
                    current.Add(b); used += h; // rare: let it overflow its page (JS trims later)
                }
                else
                {
                    pages.Add(MakePage(current));
                    current.Clear(); used = 0;
                    current.Add(b); used = h;
                }
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

    private static List<string> SplitParagraph(string block, int remaining, int totalH)
    {
        // Split a long paragraph into two <p> chunks at the estimated line boundary.
        string inner = StripTags(block);
        int cpl = CharsPerLine(BodyFontPx);
        int firstLines = Math.Max(2, remaining * cpl / (int)(BodyLinePx * cpl) ); // ~ remaining/lineHeight lines
        int cut = Math.Min(inner.Length, firstLines * cpl);
        if (cut <= 0) return new List<string> { block };
        int sp = inner.LastIndexOf(' ', cut);
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
        sb.Append("<div class=\"wp-footer\">Page 0 of 0</div>"); // renumbered later
        sb.Append("</div></div>");
        return new Page(sb.ToString(), 0, 0);
    }

    // ── Full-document transform ───────────────────────────────────────────────────────────────
    public const string Css = """
        /* Word-like page view (WordLikePageService) */
        body.wp-paged { background: #4f4f55; }
        body.wp-paged #canvas { padding: 28px 0 !important; width: auto !important; min-width: 0 !important;
                                max-width: none !important; margin: 0 !important; background: transparent !important;
                                box-shadow: none !important; border: none !important; border-radius: 0 !important;
                                display: flex; flex-direction: column; align-items: center; }
        .wp-page { width: 816px; min-width: 816px; height: 1056px; margin: 0 0 28px; background: #ffffff;
                   box-shadow: 0 2px 12px rgba(0,0,0,.45); position: relative; overflow: hidden; }
        .wp-page-inner { position: relative; width: 100%; height: 100%; box-sizing: border-box; }
        .wp-content { position: absolute; top: 96px; left: 96px; right: 96px; bottom: 96px; overflow: hidden; }
        .wp-header { position: absolute; top: 48px; left: 96px; right: 96px; height: 30px; overflow: hidden;
                     font-family: Calibri, 'Segoe UI', sans-serif; font-size: 9pt; color: #595959; }
        .wp-footer { position: absolute; bottom: 42px; left: 96px; right: 96px; height: 24px; overflow: hidden;
                     text-align: center; font-family: Calibri, 'Segoe UI', sans-serif; font-size: 9pt; color: #595959; }
        /* Word typography: Normal = Calibri 11 pt, 1.08 line spacing, 8 pt after, left aligned. */
        body.wp-paged, body.wp-paged .wp-content { font-family: Calibri, 'Segoe UI', 'Segoe UI Emoji', sans-serif;
                     font-size: 11pt; line-height: 1.08; color: #000; }
        body.wp-paged .wp-content p { margin: 0 0 8pt; }
        body.wp-paged .wp-content h1, body.wp-paged .wp-content h2 { border-bottom: none !important; padding-bottom: 0 !important; }
        body.wp-paged .wp-content h1 { font-size: 16pt; font-weight: 600; color: #2F5496; margin: 12pt 0 0; line-height: 1.1; }
        body.wp-paged .wp-content h2 { font-size: 13pt; font-weight: 600; color: #2F5496; margin: 8pt 0 0; line-height: 1.15; }
        body.wp-paged .wp-content h3 { font-size: 12pt; font-weight: 600; color: #1F4D78; margin: 6pt 0 0; }
        body.wp-paged .wp-content h4, body.wp-paged .wp-content h5, body.wp-paged .wp-content h6 { font-size: 11pt; font-weight: 600; color: #1F4D78; margin: 4pt 0 0; }
        body.wp-paged .wp-content ul, body.wp-paged .wp-content ol { margin: 0 0 8pt; padding-left: 40px; }
        body.wp-paged .wp-content li { margin: 0 0 2pt; }
        body.wp-paged .wp-content table { border-collapse: collapse; margin: 0 0 8pt; width: 100%; }
        body.wp-paged .wp-content th, body.wp-paged .wp-content td { border: 1px solid #bfbfbf; padding: 4pt 6pt; font-size: 10pt; }
        body.wp-paged .wp-content pre { background: #f2f2f2; border: 1px solid #d9d9d9; padding: 8pt; font-size: 9.5pt; margin: 0 0 8pt; }
        body.wp-paged .wp-content code { font-family: Consolas, 'Cascadia Mono', monospace; }
        body.wp-paged .wp-content blockquote { margin: 0 0 8pt 24pt; padding: 0 8pt; border-left: 4px solid #d0d0d0; color: #404040; }
        body.wp-paged .wp-content hr { border: none; border-top: 1px solid #999; margin: 10pt 0; }
        body.wp-paged .wp-content img { max-width: 100%; height: auto; }
        body.wp-paged .wp-content .mermaid, body.wp-paged .wp-content div[class*="shape"], body.wp-paged .wp-content svg { max-width: 100%; margin: 0 0 8pt; }
        """;

    public const string RepackScript = """
        <script>
        (function(){
          function isHeading(el){ return /^H[1-6]$/.test(el.tagName); }
          function repack(){
            var pages=[].slice.call(document.querySelectorAll('.wp-page'));
            if(pages.length<2) return;
            for(var pass=0;pass<4;pass++){
              var changed=false;
              for(var i=pages.length-1;i>0;i--){
                var c=pages[i-1].querySelector('.wp-content'), c2=pages[i].querySelector('.wp-content');
                var guard=0;
                while(c.scrollHeight>c.clientHeight+2 && guard++<60){
                  var kids=[].slice.call(c.children);
                  var last=kids[kids.length-1];
                  if(!last) break;
                  // Keep-with-next: move a stranded heading WITH the block that follows it.
                  var moving=[last];
                  if(isHeading(last)){
                    var nxt=last.nextElementSibling;
                    if(nxt) moving.push(nxt);
                  }
                  moving.forEach(function(el){ c.removeChild(el); c2.insertBefore(el, c2.firstChild); });
                  changed=true;
                }
              }
              if(!changed) break;
            }
          }
          function settle(){ requestAnimationFrame(function(){ requestAnimationFrame(repack); }); }
          if(document.readyState==='complete') settle();
          else window.addEventListener('load', settle);
          if(document.fonts && document.fonts.ready) document.fonts.ready.then(settle);
          setTimeout(settle, 900); // mermaid / async images land late
        })();
        </script>
        """;

    /// <summary>Transforms a fully-rendered preview document (from MarkdownHtmlService.Render) into
    /// the Word-like paged layout. Returns the same document with #canvas content paginated.</summary>
    public static string BuildPagedDocument(string fullHtml)
    {
        const string startMarker = "<!--ms-canvas-start-->";
        const string endMarker = "<!--ms-canvas-end-->";
        int s = fullHtml.IndexOf(startMarker, StringComparison.Ordinal);
        int e = fullHtml.IndexOf(endMarker, StringComparison.Ordinal);
        if (s < 0 || e < 0 || e <= s) return fullHtml; // unrecognised structure: leave untouched

        int innerStart = s + startMarker.Length;
        string inner = fullHtml[innerStart..e];
        var blocks = SplitTopLevelBlocks(inner);
        if (blocks.Count == 0) return fullHtml;

        var pagination = Paginate(blocks);
        var sb = new StringBuilder();
        foreach (var page in pagination.Pages)
        {
            sb.Append(page.Html
                .Replace("<div class=\"wp-footer\">Page 0 of 0</div>",
                         $"<div class=\"wp-footer\">Page {page.Number} of {page.Total}</div>"));
        }

        string paged = fullHtml[..innerStart] + sb + fullHtml[e..];

        // Mark the body for paged CSS + inject the styles and the re-measure script.
        paged = paged.Replace("<body class=\"", "<body class=\"wp-paged ", StringComparison.Ordinal);
        int styleEnd = paged.LastIndexOf("</style>", StringComparison.Ordinal);
        if (styleEnd >= 0) paged = paged.Insert(styleEnd, "\n" + Css);
        int bodyEnd = paged.LastIndexOf("</body>", StringComparison.Ordinal);
        if (bodyEnd >= 0) paged = paged.Insert(bodyEnd, RepackScript);
        return paged;
    }
}
