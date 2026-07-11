using System.Text.RegularExpressions;

namespace MdToPdf.Services;

// Neutralizes active content in the RENDERED body before it reaches the preview WebView. Markdown
// legitimately carries raw HTML (tables, <sub>, <details> — we support those on purpose), but AI
// chats and copied web content can also carry <script>, inline event handlers, or javascript:
// URLs, which would otherwise execute inside the preview with access to the document. The preview
// is same-machine/offline so the blast radius is small, but pasted content should never run code.
//
// Scope note: this is a targeted filter for the pasted-content threat model, not a general-purpose
// sanitizer library — it removes the executable vectors (script/iframe/object/embed elements,
// on*= handlers, javascript: URLs) and deliberately leaves all presentational HTML alone.
public static partial class HtmlSanitizer
{
    [GeneratedRegex(@"<script\b[^>]*>[\s\S]*?</script\s*>|<script\b[^>]*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlocks();

    [GeneratedRegex(@"<(iframe|object|embed|frame|frameset)\b[^>]*>[\s\S]*?</\1\s*>|<(iframe|object|embed|frame|frameset)\b[^>]*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedElements();

    // on*="…" / on*='…' / on*=bare inside a tag. Applied only within <...> spans (see Apply) so
    // prose that happens to contain "online=" is never touched.
    [GeneratedRegex(@"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlers();

    [GeneratedRegex(@"\s(href|src)\s*=\s*([""'])\s*javascript:[^""']*\2", RegexOptions.IgnoreCase)]
    private static partial Regex JavascriptUrls();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    public static string Apply(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        html = ScriptBlocks().Replace(html, "");
        html = EmbedElements().Replace(html, "");
        // Strip handlers/js-urls tag-by-tag so the regexes can't reach across tag boundaries.
        html = Tags().Replace(html, m =>
        {
            var tag = m.Value;
            if (tag.Contains("on", StringComparison.OrdinalIgnoreCase)) tag = EventHandlers().Replace(tag, "");
            if (tag.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) tag = JavascriptUrls().Replace(tag, "");
            return tag;
        });
        return html;
    }
}
