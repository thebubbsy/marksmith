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

        var pageWidthPx = settings.A4FixedWidth ? 800 : 1200;
        PdfPageSetup setup;
        if (settings.UnlimitedHeight)
        {
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

        // Belt-and-suspenders page sizing: WebView2's native PrintSettings (page width/height,
        // margins, "print backgrounds") cover WinUI, but Avalonia.Controls.WebView's
        // WebViewPrintSettings exposes none of those — only Orientation and integer margins — so
        // there's no native lever there at all. An injected @page rule is honored by every
        // Chromium/WebKit print pipeline these hosts wrap (WebView2, WKWebView, WebKitGTK/WPE),
        // so it's the one part of PdfPageSetup guaranteed to reach the printed page regardless of
        // what the native API on a given platform actually exposes.
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
