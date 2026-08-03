using System.Globalization;
using MarkSmith.Models;

namespace MarkSmith.Services;

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

        // Deterministically wait for Mermaid rendering, async image decodes, and layout reflow
        // completion before measuring scrollHeight and printing to PDF, without sleeping. The
        // readiness contract (MutationObserver / img.decode / double-rAF) lives in
        // IWebRenderHost.WaitForExportReadyAsync so every host gets it for free.
        var checkMermaid = settings.MermaidEnabled && html.Contains("mermaid", StringComparison.OrdinalIgnoreCase);
        await host.WaitForExportReadyAsync(checkMermaid);

        PdfPageSetup setup;
        if (settings.UnlimitedHeight)
        {
            // Continuous/no-page-breaks mode is meant to be an exact export of the on-screen
            // preview — one tall page, not a real paper size — so the PDF's page width must equal
            // the content's actual rendered width, not some other value. #canvas in
            // MarkdownHtmlService.cs is `max-width: {settings.ContentWidth}px` with
            // box-sizing:border-box (padding included in that width) inside a zero-padding body,
            // so the page must be exactly ContentWidth px wide to fill edge-to-edge with no blank
            // margin. Calculates layout dimensions dynamically from configured page setup parameters.
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
            // Paginated mode: page width follows ContentWidth so the HTML canvas (laid out at
            // ContentWidth px in MarkdownHtmlService) maps edge-to-edge onto the printed page.
            // Height stays A4-proportional; the previous hardcoded 8.27in clipped wider content.
            var pageW = settings.ContentWidth / PxPerInch;
            var pageH = pageW * (11.69 / 8.27); // maintain A4 aspect ratio
            setup = new PdfPageSetup(
                PageWidthIn: pageW, PageHeightIn: pageH,
                MarginTopIn: 0, MarginBottomIn: 0, MarginLeftIn: 0, MarginRightIn: 0, // Edge-to-edge theme background
                PrintBackgrounds: true);
        }

        // Header / footer engine (Task 10): build the Chromium template pair and, when a band is
        // present, reserve top/bottom margin space for it (Chromium draws the header/footer inside the
        // margin box, so a zero-margin page would clip them). Off by default — existing edge-to-edge
        // exports keep margin 0 and no header/footer until the user opts in via Settings.
        // Skipped in UnlimitedHeight mode: the output is a single continuous page, so per-page
        // header/footer chrome is semantically void (would render once at the very top/bottom of a
        // giant scroll and contradict the zero-margin edge-to-edge contract of that mode).
        var docTitle = Path.GetFileNameWithoutExtension(pdfPath);
        var (headerTpl, footerTpl) = settings.UnlimitedHeight ? ("", "") : BuildHeaderFooter(settings, docTitle);
        if (headerTpl.Length > 0) setup = setup with { MarginTopIn = Math.Max(setup.MarginTopIn, 0.4), HeaderTemplate = headerTpl };
        if (footerTpl.Length > 0) setup = setup with { MarginBottomIn = Math.Max(setup.MarginBottomIn, 0.4), FooterTemplate = footerTpl };

        // @page CSS mirrors the print margins (0 for edge-to-edge theme backgrounds, or the reserved
        // header/footer space) and forces exact background color rendering so the theme fills the page.
        await host.ExecuteScriptAsync($$"""
            (() => {
                const style = document.createElement('style');
                style.textContent = `@page { size: {{Inches(setup.PageWidthIn)}}in {{Inches(setup.PageHeightIn)}}in; margin: {{Inches(setup.MarginTopIn)}}in {{Inches(setup.MarginRightIn)}}in {{Inches(setup.MarginBottomIn)}}in {{Inches(setup.MarginLeftIn)}}in !important; }
                    html, body { margin: 0 !important; padding: 0 !important; width: 100% !important; height: 100% !important; -webkit-print-color-adjust: exact !important; print-color-adjust: exact !important; color-adjust: exact !important; }`;
                document.head.appendChild(style);
            })();
            """);

        var ok = await host.PrintToPdfAsync(pdfPath, setup);
        if (!ok) throw new InvalidOperationException("PDF export failed (the web renderer reported failure).");

        // Password protection / access control (Task 18): Chromium emits an unprotected PDF, so when
        // protection is configured we post-process the written file with the standard security handler.
        // When the user restricts permissions but leaves both passwords blank, an owner password is
        // auto-generated so the restrictions are actually enforced (the PDF still opens freely — no
        // user password — but printing/copying/modifying are blocked until the owner password is given).
        var policy = PdfSecurityService.BuildPolicy(settings);
        if (policy != null && policy.IsProtected)
            PdfSecurityService.ApplyToFile(pdfPath, policy);
    }

    private static string Inches(double value) => value.ToString(CultureInfo.InvariantCulture);

    // ---- Header / footer engine (Task 10) ----

    // Substitutes the four template tokens with literal values — used for the Settings live preview
    // and unit tests. {pages} is replaced before {page} so the shorter token can't corrupt the longer
    // one ("Page {page} of {pages}" must become "Page 3 of 12", never "Page 3 of <n>s").
    public static string SubstituteTokens(string template, string title, int page, int pages, DateTime date)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return template
            .Replace("{title}", title)
            .Replace("{pages}", pages.ToString(CultureInfo.InvariantCulture))
            .Replace("{page}", page.ToString(CultureInfo.InvariantCulture))
            .Replace("{date}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    // Converts a user template into a Chromium header/footer HTML fragment. {page}/{pages}/{date}
    // become Chromium's special auto-substituting spans (filled per page at print time); {title}
    // becomes the HTML-escaped literal document title. Returns "" for an empty template so Chromium
    // simply skips that band.
    public static string BuildChromiumTemplate(string template, string title)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        return template
            .Replace("{pages}", "<span class=\"totalPages\"></span>")
            .Replace("{page}", "<span class=\"pageNumber\"></span>")
            .Replace("{date}", "<span class=\"date\"></span>")
            .Replace("{title}", System.Net.WebUtility.HtmlEncode(title));
    }

    // Builds the (header, footer) Chromium template pair from settings + the chosen page-number
    // position. A non-"None" position with an empty matching band injects a default "Page {page} of
    // {pages}"; alignment (left/center/right) follows the position. Explicit templates always render.
    public static (string Header, string Footer) BuildHeaderFooter(AppSettings settings, string title)
    {
        var pos = (settings.PdfPageNumberPosition ?? "None").Trim();
        var header = settings.PdfHeaderTemplate ?? "";
        var footer = settings.PdfFooterTemplate ?? "";
        var enabled = !string.Equals(pos, "None", StringComparison.OrdinalIgnoreCase);

        if (enabled)
        {
            var top = pos.StartsWith("Top", StringComparison.OrdinalIgnoreCase);
            if (top && string.IsNullOrWhiteSpace(header)) header = "Page {page} of {pages}";
            if (!top && string.IsNullOrWhiteSpace(footer)) footer = "Page {page} of {pages}";
        }

        var align = pos.EndsWith("Center", StringComparison.OrdinalIgnoreCase) ? "center"
                  : pos.EndsWith("Right", StringComparison.OrdinalIgnoreCase) ? "right"
                  : "left";

        return (WrapBand(header, title, align), WrapBand(footer, title, align));
    }

    private static string WrapBand(string template, string title, string align)
    {
        var body = BuildChromiumTemplate(template, title);
        return body.Length == 0
            ? ""
            : $"<div style=\"font-size:9px; text-align:{align}; width:100%; padding:0 0.3in;\">{body}</div>";
    }
}
