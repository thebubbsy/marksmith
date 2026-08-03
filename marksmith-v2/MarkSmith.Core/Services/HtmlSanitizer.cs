using System.Text.RegularExpressions;

namespace MarkSmith.Services;

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

    // A single HTML tag, quote-aware: `<`, a tag-name lead char, then runs of non-`>`/non-quote
    // characters OR whole quoted attribute values, up to the closing `>`. Unlike `<[^>]+>`, this
    // does NOT end the tag at a `>` sitting inside a quoted attribute value (e.g. alt="a>b"), which
    // is exactly the hole that let an `onerror=` after such an attribute escape handler stripping.
    [GeneratedRegex("<[a-zA-Z/!][^>\"']*(?:\"[^\"]*\"|'[^']*'|[^>\"'])*>", RegexOptions.Singleline)]
    private static partial Regex TagAware();

    // on*="…" / on*='…' / on*=bare inside a tag. Applied only within a correctly-delimited tag.
    [GeneratedRegex(@"[\s/]on\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlers();

    // A url-bearing attribute (href/src, optionally xlink:/xml: prefixed), capturing name+`=`
    // (group 1) and the value (group 2: double-quoted, single-quoted, or bare). Whether the value
    // is a javascript: URL is decided in code (IsJavascriptUrl), because a single regex can't both
    // respect the value's own delimiter and skip a foreign inner quote — the two-quote-styles bug.
    [GeneratedRegex(@"([\s/](?:xlink:|xml:)?(?:href|src)\s*=\s*)(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlAttr();

    // A URL value is a javascript: link if, after HTML-decoding and removing ALL whitespace/control
    // characters, it begins with "javascript:". Removing whitespace/control chars catches the
    // standard obfuscations (java\tscript:, "  javascript:", and — because we decode first — entity
    // forms like java&#115;cript:).
    private static bool IsJavascriptUrl(string decoded)
    {
        var compact = new string(decoded.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
        return compact.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase);
    }

    public static string Apply(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        html = ScriptBlocks().Replace(html, "");
        html = EmbedElements().Replace(html, "");
        // Process handlers / js-urls tag-by-tag using a QUOTE-AWARE tag matcher, so neither a `>`
        // inside an attribute value nor a foreign quote inside a value can defeat the stripping.
        html = TagAware().Replace(html, m =>
        {
            var tag = m.Value;
            if (tag.Contains("on", StringComparison.OrdinalIgnoreCase))
                tag = EventHandlers().Replace(tag, "");
            tag = UrlAttr().Replace(tag, am =>
            {
                var value = am.Groups[2].Value;
                var inner = value.Length >= 2 && (value[0] == '"' || value[0] == '\'')
                    ? value[1..^1] : value;
                var decoded = System.Net.WebUtility.HtmlDecode(inner);
                return IsJavascriptUrl(decoded) ? am.Groups[1].Value + "\"#\"" : am.Value;
            });
            return tag;
        });
        return html;
    }
}
