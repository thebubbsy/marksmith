namespace MarkSmith.Services;

// Mermaid's flowchart stylesheet draws edge-label backgrounds at half opacity
// (".edgeLabel rect { opacity: 0.5 }", and the HTML-label equivalent), so the connector shows
// straight through "yes" / "no" and reads as struck-out text. Every Mermaid init that draws for a
// person (the preview, the diagram viewer, export rasters, the email preview, Diagram Studio)
// passes this as its themeCSS so labels sit on a solid patch of the page colour.
public static class MermaidLabelStyle
{
    /// <summary>A JavaScript string literal, ready to drop in as <c>themeCSS: …</c>.</summary>
    public static string ThemeCss(string background)
    {
        var bg = Sanitize(background);
        var css = $".edgeLabel rect{{opacity:1!important;fill:{bg}!important}}"
                + $".edgeLabel,.edgeLabel p,.edgeLabel span,.labelBkg{{background-color:{bg}!important;opacity:1!important}}";
        return System.Text.Json.JsonSerializer.Serialize(css);
    }

    // Theme colours come from user-editable theme files; keep anything that could close the rule.
    private static string Sanitize(string value)
    {
        var v = (value ?? "").Trim();
        return v.Length == 0 || v.IndexOfAny(new[] { '{', '}', ';', '<', '>', '"', '\'' }) >= 0 ? "#ffffff" : v;
    }
}
