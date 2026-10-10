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

    /// <summary>
    /// Extra <c>themeVariables</c> entries (each preceded by a comma) that colour charts. The
    /// "base" theme derives pie slices from primaryColor, which every Marksmith init sets to the
    /// page background, so pies drew as white wedges with blank legend swatches. The palette is
    /// the one the native Word charts use (<see cref="Mermaid.MermaidChartsRenderer.BuildPalette"/>),
    /// so a chart looks the same in the preview, the PDF, the slides and the Word file.
    /// </summary>
    /// <param name="background">What the diagram sits on (the preview's code-coloured card, else
    /// the page). An xychart paints its own background, white unless told.</param>
    public static string ChartVariables(Models.ThemeDefinition theme, string? background = null)
    {
        var palette = Mermaid.MermaidChartsRenderer.BuildPalette(theme);
        var bg = Sanitize(background ?? theme.Background);
        var dark = !Models.ThemeDefinition.IsLight(bg);
        var sb = new System.Text.StringBuilder();
        string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        for (int i = 0; i < 12; i++) sb.Append($", pie{i + 1}: {J(palette[i % palette.Length])}");
        var text = Sanitize(theme.Text);
        sb.Append($", pieStrokeColor: {J(bg)}, pieStrokeWidth: \"2px\", pieOuterStrokeWidth: \"1px\"");
        sb.Append($", pieOuterStrokeColor: {J(Sanitize(theme.Line))}, pieOpacity: \"1\"");
        // Slices are mid-tone on light pages and lighter on dark ones (BuildPalette), so the
        // percentage reads in white on the first and near-black on the second.
        sb.Append($", pieSectionTextColor: {J(dark ? "#111111" : "#FFFFFF")}");
        sb.Append($", pieTitleTextColor: {J(text)}, pieLegendTextColor: {J(text)}");
        var line = J(Sanitize(theme.Line));
        sb.Append($", xyChart: {{ backgroundColor: {J(bg)}, titleColor: {J(text)}, plotColorPalette: {J(string.Join(",", palette))}");
        sb.Append($", xAxisLabelColor: {J(text)}, xAxisTitleColor: {J(text)}, xAxisTickColor: {line}, xAxisLineColor: {line}");
        sb.Append($", yAxisLabelColor: {J(text)}, yAxisTitleColor: {J(text)}, yAxisTickColor: {line}, yAxisLineColor: {line} }}");
        return sb.ToString();
    }

    // Theme colours come from user-editable theme files; keep anything that could close the rule.
    private static string Sanitize(string value)
    {
        var v = (value ?? "").Trim();
        return v.Length == 0 || v.IndexOfAny(new[] { '{', '}', ';', '<', '>', '"', '\'' }) >= 0 ? "#ffffff" : v;
    }
}
