namespace MdToPdf.Services;

// Bundled offline web assets (mermaid, KaTeX, highlight.js) are served to the preview WebView from
// a virtual host mapped in MainWindow.MapAssetHost. Centralize the host + URLs so every render page
// references the same origin and there are no stray CDN calls.
public static class WebAssets
{
    public const string Host = "marksmith.assets";
    public const string Base = "https://" + Host;

    public const string Mermaid = Base + "/mermaid.min.js";
    public const string KatexCss = Base + "/katex.min.css";
    public const string KatexJs = Base + "/katex.min.js";
    public const string KatexAutoRender = Base + "/auto-render.min.js";
    public const string HighlightJs = Base + "/highlight.min.js";
    public static string HighlightCss(bool dark) => $"{Base}/{(dark ? "github-dark" : "github")}.min.css";
}
