using System.Globalization;
using MdToPdf.Models;

namespace MdToPdf.Services;

// Port of generate_pdf_core() from md_to_pdf_tui.py, driving whatever Chromium-based print
// pipeline the host UI provides via IWebRenderHost (WebView2's PrintToPdfAsync on Windows; an
// equivalent on other platforms) instead of Playwright's page.pdf(). All three ultimately drive
// the same Chromium print-to-PDF machinery, so the same margin math and mermaid-wait polling work
// unchanged across platforms — only the primitives (navigate/execute-script/print) differ per host.
public sealed class PdfExportService
{
    private const double PxPerInch = 96.0;

    // `host` must already be ready (EnsureReadyAsync) and its underlying web control parented in a
    // visual tree — nothing will render (and therefore nothing meaningful will print) otherwise, so
    // callers should reuse the visible preview host rather than an unparented one-off instance.
    public async Task ExportAsync(IWebRenderHost host, string html, string pdfPath, AppSettings settings)
    {
        await host.NavigateToStringAsync(html);

        // Give Mermaid (if present) a moment to finish rendering before we print, same "smart wait"
        // idea as the Python app's page.wait_for_function polling loop, simplified to a poll here.
        if (settings.MermaidEnabled && html.Contains("mermaid", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < 20; i++)
            {
                var done = await host.ExecuteScriptAsync("""
                    (() => {
                        const all = document.querySelectorAll('.mermaid');
                        const processed = document.querySelectorAll('.mermaid[data-processed="true"]');
                        return all.length === 0 || processed.length === all.length;
                    })()
                    """);
                if (done == "true") break;
                await Task.Delay(250);
            }
            await Task.Delay(300); // layout settle buffer, mirrors the Python app's 500ms buffer
        }

        PdfPageSetup setup;
        if (settings.UnlimitedHeight)
        {
            // Continuous/no-page-breaks mode is meant to be an exact export of the on-screen
            // preview — one tall page, not a real paper size — so the PDF's page width must equal
            // the content's actual rendered width, not some other value. #canvas in
            // MarkdownHtmlService.cs is `max-width: {settings.ContentWidth}px` with
            // box-sizing:border-box (padding included in that width) inside a zero-padding body,
            // so the page must be exactly ContentWidth px wide to fill edge-to-edge with no blank
            // margin. This used to be hardcoded to 800/1200px based on the unrelated A4FixedWidth
            // toggle, completely ignoring ContentWidth — whenever a user's Page Width setting
            // wasn't exactly 800 or 1200, the mismatch produced a narrow content column inside a
            // too-wide (or too-narrow) page.
            var pageWidthPx = settings.ContentWidth;
            var scrollHeightResult = await host.ExecuteScriptAsync("document.body.scrollHeight");
            var scrollHeightPx = double.TryParse(scrollHeightResult, out var h) ? h : 1000;
            setup = new PdfPageSetup(
                PageWidthIn: pageWidthPx / PxPerInch,
                PageHeightIn: (scrollHeightPx + 100) / PxPerInch,
                MarginTopIn: 0, MarginBottomIn: 0, MarginLeftIn: 0, MarginRightIn: 0,
                PrintBackgrounds: true);
        }
        else
        {
            setup = new PdfPageSetup(
                PageWidthIn: 8.27, PageHeightIn: 11.69, // A4
                MarginTopIn: 0.39, MarginBottomIn: 0.39, MarginLeftIn: 0.39, MarginRightIn: 0.39, // ~1cm
                PrintBackgrounds: true);
        }

        // Best-effort fallback for hosts whose native print-settings API can't express page size —
        // WinUI's WebView2 host sets PageWidth/PageHeight/MarginX/ShouldPrintBackgrounds directly
        // (see MdToPdf/MainWindow.xaml.cs's PrintToPdfAsync) and doesn't need this. On Windows the
        // Avalonia host now also sets those natively (it bridges past Avalonia.Controls.WebView's
        // PageWidth/PageHeight-less WebViewPrintSettings straight to the underlying WebView2's own
        // CoreWebView2PrintSettings — see the long comment on
        // MdToPdf.Avalonia/Views/MainWindow.xaml.cs's IWebRenderHost implementation) and doesn't rely
        // on this either. Only Linux (WebKitGTK) and macOS (WKWebView) — which have no such bridge
        // wired up — still depend on this `@page` rule for page size, and it remains unverified
        // (not confirmed broken, just never tested) on those two platforms specifically.
        // print-color-adjust is a separate, well-supported CSS mechanism and should force background
        // colors to print regardless of platform — that part isn't in question, only page `size`.
        await host.ExecuteScriptAsync($$"""
            (() => {
                const style = document.createElement('style');
                style.textContent = `@page { size: {{Inches(setup.PageWidthIn)}}in {{Inches(setup.PageHeightIn)}}in; margin-top: {{Inches(setup.MarginTopIn)}}in; margin-right: {{Inches(setup.MarginRightIn)}}in; margin-bottom: {{Inches(setup.MarginBottomIn)}}in; margin-left: {{Inches(setup.MarginLeftIn)}}in; }
                    html, body { -webkit-print-color-adjust: exact !important; print-color-adjust: exact !important; color-adjust: exact !important; }`;
                document.head.appendChild(style);
            })();
            """);

        var ok = await host.PrintToPdfAsync(pdfPath, setup);
        if (!ok) throw new InvalidOperationException("PDF export failed (the web renderer reported failure).");
    }

    private static string Inches(double value) => value.ToString(CultureInfo.InvariantCulture);
}
