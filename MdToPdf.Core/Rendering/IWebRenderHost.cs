namespace MdToPdf.Services;

// Page geometry for a PDF print job, expressed platform-neutrally (inches, matching the print
// settings both WebView2 and other Chromium-based print pipelines use). Each UI project's
// IWebRenderHost implementation translates this into its own web engine's native print-settings type.
public sealed record PdfPageSetup(
    double PageWidthIn, double PageHeightIn,
    double MarginTopIn, double MarginBottomIn, double MarginLeftIn, double MarginRightIn,
    bool PrintBackgrounds);

// The portable seam between the Core engine and whatever embeddable web renderer a UI project
// uses (WebView2 on Windows, an Avalonia webview package on Mac/Linux, or a headless-Chromium
// fallback). Everything that used to reach directly into a WinUI WebView2 control — PDF export,
// the mermaid PNG/geometry harvesters — is written in Core against these four primitives instead,
// so that logic (the mermaid-render-completion polling, the JS harvesting scripts, the print
// margin math) is verified once and reused on every platform. Only the primitives themselves are
// implemented per platform, typically as a thin wrapper a few lines long.
public interface IWebRenderHost
{
    // True once the underlying web engine is initialized and ready to navigate/execute script.
    // Implementations should lazily initialize on first call rather than assume it's ready.
    Task<bool> EnsureReadyAsync();

    // Navigates to an in-memory HTML document (never a URL) and waits for navigation to complete.
    Task NavigateToStringAsync(string html);

    // Runs JavaScript in the current document and returns its JSON-serialized result as a string
    // (matching CoreWebView2.ExecuteScriptAsync's contract) — the caller deserializes as needed.
    Task<string?> ExecuteScriptAsync(string javaScript);

    // Prints the currently-loaded document to a PDF file. Returns false (not throws) on failure,
    // matching WebView2's PrintToPdfAsync so callers can produce a consistent error message.
    Task<bool> PrintToPdfAsync(string outputPath, PdfPageSetup setup);

    // Brackets a mermaid harvest batch (MermaidHarvestService navigates the host to a throwaway
    // render page and polls it for several seconds). Hosts that also drive a live-updating preview
    // on the same control use these to pause their own auto-refresh during the batch and restore
    // it afterward; a host with no such concern can no-op both.
    Task BeginHarvestAsync();
    Task EndHarvestAsync();
}

// UI-framework-specific prompts the Core ViewModel needs but can't render itself (native dialogs
// differ completely between WinUI and Avalonia). Kept separate from IWebRenderHost because a host
// without a dialog system (e.g. a future headless/CLI mode) can still implement rendering only.
public interface IUiPrompts
{
    // Large-diagram prompt: 1 = keep exact layout (Web Layout), 2 = reflow to fit the page.
    Task<int> AskOversizedDiagramModeAsync();
    
    // Ask the user to resolve an ambiguous construct in the markdown.
    Task<MdToPdf.Models.RenderOption?> ShowAmbiguityResolverDialogAsync(MdToPdf.Models.AmbiguityCase ambiguity);
}
