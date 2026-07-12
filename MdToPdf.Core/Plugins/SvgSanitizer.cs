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
    private static readonly Regex EventHandlerAttr = new("\\son[a-zA-Z]+\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // href / xlink:href whose value (after optional whitespace/entities) begins with javascript:.
    private static readonly Regex JavascriptHref = new(
        "\\s(?:xlink:)?href\\s*=\\s*(\"\\s*javascript:[^\"]*\"|'\\s*javascript:[^']*'|\\s*javascript:[^\\s>]+)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Sanitize(string svg)
    {
        svg = ScriptEl.Replace(svg, "");
        svg = ForeignObjectEl.Replace(svg, "");
        svg = EventHandlerAttr.Replace(svg, "");
        svg = JavascriptHref.Replace(svg, "");
        return svg;
    }
}
