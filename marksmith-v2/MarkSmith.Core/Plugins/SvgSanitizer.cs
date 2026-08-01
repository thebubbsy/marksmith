using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace MdToPdf.Plugins;

// Plugin SVG output is injected raw into the preview/export HTML (WebView DOM), so a malicious or
// upstream-compromised diagram tool's output is an active-content vector. The earlier version
// stripped only <script>…</script>, which misses every other way SVG can execute JS. This removes
// the full set: script elements, on* event-handler attributes, javascript:/data-with-script URIs,
// and <foreignObject> (arbitrary embedded HTML). Legitimate diagram output (shapes, text, paths,
// <a xlink:href="https://…">) is preserved.
public static class SvgSanitizer
{
    private static readonly Regex ScriptEl = new("<script\\b[^>]*>.*?</script>|<script\\b[^>]*/?>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ForeignObjectEl = new("<foreignObject\\b[^>]*>.*?</foreignObject>|<foreignObject\\b[^>]*/?>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // on<name>= followed by a single-, double-, or unquoted value.
    private static readonly Regex EventHandlerAttr = new("[\\s/]on[a-zA-Z]+\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // href / xlink:href attribute matcher
    private static readonly Regex HrefAttr = new(
        "\\s(?:xlink:)?href\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsJavascriptUrl(string value)
    {
        var inner = value.Length >= 2 && (value[0] == '"' || value[0] == '\'')
            ? value[1..^1] : value;
        var decoded = WebUtility.HtmlDecode(inner);
        var compact = new string(decoded.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
        return compact.StartsWith("javascript:", System.StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("data:text/html", System.StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("vbscript:", System.StringComparison.OrdinalIgnoreCase);
    }

    public static string Sanitize(string svg, string? bgContextHex = null)
    {
        if (string.IsNullOrEmpty(svg)) return svg;
        svg = ScriptEl.Replace(svg, "");
        svg = ForeignObjectEl.Replace(svg, "");
        svg = EventHandlerAttr.Replace(svg, "");
        svg = HrefAttr.Replace(svg, m => IsJavascriptUrl(m.Groups[1].Value) ? "" : m.Value);
        svg = Services.ContrastGuard.EnsureSvgLegibility(svg, bgContextHex);
        return svg;
    }
}
