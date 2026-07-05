namespace MdToPdf.Services;

// NOT IMPLEMENTED — element-level screenshots (one PNG per <div class="mermaid"> diagram, for the
// PNG gallery mode and for splicing diagrams into a DOCX) are the one piece of the Python app's
// Playwright pipeline that has no direct WebView2 equivalent:
//
//   Playwright:  await page.locator(".mermaid").screenshot(path=...)   // one call, clips to the element
//   WebView2:    no element-screenshot API. Two ways to get there:
//
//   1) CoreWebView2.CapturePreviewAsync(...) captures the *whole visible viewport* to an image
//      stream, not a single element. You'd size the WebView2 to exactly the diagram's bounding
//      box (read via ExecuteScriptAsync("JSON.stringify(el.getBoundingClientRect())")) before
//      capturing, then crop. Works, but means resizing/scrolling per diagram.
//   2) Drop to the Chrome DevTools Protocol directly via
//      CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", ...) with a `clip`
//      rect — this is what Playwright itself does under the hood, so it's the closer analog.
//      Requires wiring up the raw CDP JSON request/response, which WebView2 exposes but doesn't
//      wrap in a typed API the way it does for printing.
//
// Whichever you pick, the "wait until Mermaid has finished rendering" polling loop already lives
// in PdfExportService.ExportAsync and can be extracted/reused here.
public sealed class MermaidRenderService
{
    public Task<byte[]> CaptureDiagramAsync(Microsoft.UI.Xaml.Controls.WebView2 webView, int diagramIndex) =>
        throw new NotImplementedException(
            "Mermaid element screenshots are a stubbed design decision — see the comment at the top of MermaidRenderService.cs.");
}
