using MdToPdf.Models;
using Microsoft.Web.WebView2.Core;

namespace MdToPdf.Services;

// Port of generate_pdf_core() from md_to_pdf_tui.py, using WebView2's PrintToPdfAsync instead
// of Playwright's page.pdf(). Both ultimately drive the same Chromium print-to-PDF pipeline.
public sealed class PdfExportService
{
    private const double PxPerInch = 96.0;

    // `webView` must already have CoreWebView2 initialized and be parented in a visual tree —
    // WebView2 will not render (and therefore will not produce a meaningful PDF) if it's never
    // been laid out, so callers should reuse the visible preview control rather than an
    // unparented one-off instance.
    public async Task ExportAsync(
        Microsoft.UI.Xaml.Controls.WebView2 webView,
        string html,
        string pdfPath,
        AppSettings settings)
    {
        var core = webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");

        var navigationComplete = new TaskCompletionSource();
        void OnNavigationCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => navigationComplete.TrySetResult();
        core.NavigationCompleted += OnNavigationCompleted;
        try
        {
            core.NavigateToString(html);
            await navigationComplete.Task;
        }
        finally
        {
            core.NavigationCompleted -= OnNavigationCompleted;
        }

        // Give Mermaid (if present) a moment to finish rendering before we print, same "smart wait"
        // idea as the Python app's page.wait_for_function polling loop, simplified to a poll here.
        if (settings.MermaidEnabled && html.Contains("mermaid", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < 20; i++)
            {
                var done = await core.ExecuteScriptAsync("""
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

        var printSettings = core.Environment.CreatePrintSettings();
        printSettings.ShouldPrintBackgrounds = true;
        printSettings.ShouldPrintHeaderAndFooter = false;

        var pageWidthPx = settings.A4FixedWidth ? 800 : 1200;
        printSettings.PageWidth = pageWidthPx / PxPerInch;

        if (settings.UnlimitedHeight)
        {
            var scrollHeightResult = await core.ExecuteScriptAsync("document.body.scrollHeight");
            var scrollHeightPx = double.TryParse(scrollHeightResult, out var h) ? h : 1000;
            printSettings.PageHeight = (scrollHeightPx + 100) / PxPerInch;
            printSettings.MarginTop = printSettings.MarginBottom = printSettings.MarginLeft = printSettings.MarginRight = 0;
        }
        else
        {
            printSettings.PageWidth = 8.27;  // A4
            printSettings.PageHeight = 11.69;
            printSettings.MarginTop = printSettings.MarginBottom = printSettings.MarginLeft = printSettings.MarginRight = 0.39; // ~1cm
        }

        var ok = await core.PrintToPdfAsync(pdfPath, printSettings);
        if (!ok) throw new InvalidOperationException("WebView2 PrintToPdfAsync reported failure.");
    }
}
