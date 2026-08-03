namespace MarkSmith.Services;

// Bundled offline web assets (mermaid, KaTeX, highlight.js) are served to the preview web host from
// a local origin each platform sets up once at startup: the WinUI build maps Host as a WebView2
// virtual host (MainWindow.MapAssetHost); the Avalonia build points Base at a loopback HTTP server
// (MarkSmith.Avalonia/Services/LocalAssetServer.cs) since Avalonia's NativeWebView has no virtual-host
// equivalent. Settable (not const) so each platform can override before the first HTML render.
public static class WebAssets
{
    public static string Host { get; set; } = "marksmith.assets";
    public static string Base { get; set; } = "https://marksmith.assets";

    public static string Mermaid => Base + "/mermaid.min.js";
    public static string KatexCss => Base + "/katex.min.css";
    public static string KatexJs => Base + "/katex.min.js";
    public static string KatexAutoRender => Base + "/auto-render.min.js";
    public static string HighlightJs => Base + "/highlight.min.js";
    public static string HighlightCss(bool dark) => $"{Base}/{(dark ? "github-dark" : "github")}.min.css";
    public static string LiquidFillCss => Base + "/liquid_fill.css";
    public static string MermaidInteropJs => Base + "/mermaid_interop.js";
}
