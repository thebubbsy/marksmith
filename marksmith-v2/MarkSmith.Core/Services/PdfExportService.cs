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

    /// <summary>The tallest page a PDF may have: 14,400 points (200 in). Acrobat crops or refuses
    /// anything taller, so a continuous page past it carries on onto a second tall page.</summary>
    public const double MaxPageHeightIn = 200.0;

    /// <summary>
    /// The height of a continuous page whose content is <paramref name="contentHeightPx"/> tall: the
    /// content plus a hair, or, past the PDF limit, the content shared evenly over as many tall pages
    /// as it needs (each repeats the page padding, hence the extra inch or two), rather than one full
    /// page and a second mostly empty one.
    /// </summary>
    public static double ContinuousPageHeightIn(double contentHeightPx)
    {
        var neededIn = (Math.Max(contentHeightPx, 96) + 24) / PxPerInch;
        if (neededIn <= MaxPageHeightIn) return neededIn;
        var pages = Math.Ceiling(neededIn / MaxPageHeightIn);
        return Math.Min(MaxPageHeightIn, neededIn / pages + 2);
    }

    /// <summary>Space between the paper edge and the text at the top and bottom of every page.</summary>
    public const double PageInsetIn = 0.6;

    /// <summary>Height of a header or footer band; the page inset under it shrinks to match.</summary>
    public const double BandHeightIn = 0.45;

    /// <summary>
    /// The paper a paginated PDF prints on. The A4 lock gives true A4 (8.27 x 11.69 in); with the
    /// lock off the page is the chosen width in Letter proportions, the paper Word uses then.
    /// </summary>
    public static (double WidthIn, double HeightIn) PaperSize(AppSettings settings)
    {
        if (settings.A4FixedWidth) return (8.27, 11.69);
        var width = PageWidthPx(settings) / PxPerInch;
        return (width, width * 11.0 / 8.5);
    }

    /// <summary>The page's width in CSS pixels: 794 (A4) while the A4 lock is on, whatever an older
    /// settings file says the width is, so the preview, a continuous PDF and paper all agree.</summary>
    public static int PageWidthPx(AppSettings settings) =>
        settings.A4FixedWidth ? 794 : Math.Clamp(settings.ContentWidth, 400, 2400);

    /// <summary>The page inset under a band: the band's own height counts towards the white space,
    /// so text starts a little further from the paper's edge than without one.</summary>
    public static double InsetUnderBand(bool hasBand) => hasBand ? PageInsetIn - BandHeightIn * 0.8 : PageInsetIn;

    /// <summary>The title {title} prints and the PDF's Title property: the document's own title
    /// (front matter, then first heading), falling back to the file name.</summary>
    public static string DocumentTitle(string? sourceMarkdown, string pdfPath)
    {
        var title = HistoryEntry.ExtractTitle(sourceMarkdown ?? "");
        return string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(pdfPath) : title;
    }

    // `host` must already be ready (EnsureReadyAsync) and its underlying web control parented in a
    // visual tree — nothing will render (and therefore nothing meaningful will print) otherwise, so
    // callers should reuse the visible preview host rather than an unparented one-off instance.
    public async Task ExportAsync(IWebRenderHost host, string html, string pdfPath, AppSettings settings, string? sourceMarkdown = null)
    {
        await host.NavigateToStringAsync(html);

        // Deterministically wait for Mermaid rendering, async image decodes, and layout reflow
        // completion before measuring scrollHeight and printing to PDF, without sleeping. The
        // readiness contract (MutationObserver / img.decode / double-rAF) lives in
        // IWebRenderHost.WaitForExportReadyAsync so every host gets it for free.
        var checkMermaid = settings.MermaidEnabled && html.Contains("mermaid", StringComparison.OrdinalIgnoreCase);
        await host.WaitForExportReadyAsync(checkMermaid);

        var docTitle = DocumentTitle(sourceMarkdown, pdfPath);
        var look = PageLook.Parse(await host.ExecuteScriptAsync(PageLook.Script));

        // Paper can't be clicked: a folded callout (> [!tip]-) or any other closed <details> would
        // print as its title alone, so everything folded prints open.
        await host.ExecuteScriptAsync("document.querySelectorAll('details:not([open])').forEach(d => d.open = true);");

        // Header / footer bands (Settings > PDF): Chromium draws them in the page margin, so a band
        // gets a margin of its own. A single continuous page has no pages to number, so no bands.
        var (headerTpl, footerTpl) = settings.UnlimitedHeight ? ("", "") : BuildHeaderFooter(settings, docTitle, look);

        PdfPageSetup setup;
        string canvasPadding;
        if (settings.UnlimitedHeight)
        {
            // One tall page as wide as the preview's page. The height is measured with the
            // document's print rules applied on screen first (print pads the page differently and
            // wraps long code lines), so the page ends just below the last line: a guess fell short
            // and spilled the end onto a second page, or left a blank band at the bottom.
            var pageWidthPx = PageWidthPx(settings);
            var scrollHeightResult = await host.ExecuteScriptAsync(MeasurePrintHeightScript(pageWidthPx));
            var scrollHeightPx = double.TryParse(scrollHeightResult, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 1000;
            setup = new PdfPageSetup(
                PageWidthIn: pageWidthPx / PxPerInch,
                PageHeightIn: ContinuousPageHeightIn(scrollHeightPx),
                MarginTopIn: 0, MarginBottomIn: 0, MarginLeftIn: 0, MarginRightIn: 0,
                PrintBackgrounds: true);
            canvasPadding = "#canvas { -webkit-box-decoration-break: clone; box-decoration-break: clone; }";
        }
        else
        {
            // Real paper. The page colour runs edge to edge (zero side margins, the theme's colour
            // behind everything), and box-decoration-break repeats the canvas's top and bottom
            // padding on every page, so page 2 onwards no longer starts at the paper's very edge.
            var (pageW, pageH) = PaperSize(settings);
            var hasHeader = headerTpl.Length > 0;
            var hasFooter = footerTpl.Length > 0;
            setup = new PdfPageSetup(
                PageWidthIn: pageW, PageHeightIn: pageH,
                MarginTopIn: hasHeader ? BandHeightIn : 0, MarginBottomIn: hasFooter ? BandHeightIn : 0,
                MarginLeftIn: 0, MarginRightIn: 0,
                PrintBackgrounds: true, HeaderTemplate: headerTpl, FooterTemplate: footerTpl);
            var insetTop = Inches(InsetUnderBand(hasHeader));
            var insetBottom = Inches(InsetUnderBand(hasFooter));
            // A cover page fills the first page exactly: the page area less the text insets.
            canvasPadding =
                $":root {{ --ms-cover-min-height: calc(100vh - {insetTop}in - {insetBottom}in - 2px); }} " +
                $"#canvas {{ padding-top: {insetTop}in !important; padding-bottom: {insetBottom}in !important; " +
                "-webkit-box-decoration-break: clone; box-decoration-break: clone; }";
        }

        // @page mirrors the print setup so the CSS and the print settings agree, and the theme's
        // page colour fills the paper (Chromium paints the root background edge to edge).
        await host.ExecuteScriptAsync(InjectStyle(
            $"@page {{ size: {Inches(setup.PageWidthIn)}in {Inches(setup.PageHeightIn)}in; margin: {Inches(setup.MarginTopIn)}in 0 {Inches(setup.MarginBottomIn)}in 0 !important; }} " +
            $"@media print {{ html {{ background: {look.Background} !important; }} " +
            "html, body { margin: 0 !important; padding: 0 !important; height: auto !important; min-height: 0 !important; -webkit-print-color-adjust: exact !important; print-color-adjust: exact !important; } " +
            canvasPadding + " }"));

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

        // Metadata + lossless source embed (low-key "Marksmith by Matthew Bubb" in file properties,
        // original Markdown tucked into the Info dictionary so a PDF can be re-opened as Markdown).
        // Runs AFTER encryption so the entries are never dropped by the security re-save; when the
        // file is protected we open it with the owner password that was just set (auto-generated
        // when only permissions were restricted). Best-effort: if the printed file can't be opened
        // for modification, ship it as-is rather than fail an otherwise-good export.
        try
        {
            PdfSourceStore.Apply(pdfPath, sourceMarkdown, docTitle, settings, policy?.OwnerPassword);
        }
        catch (PdfSharp.Pdf.IO.PdfReaderException)
        {
            // unreadable/corrupt output — the plain printed PDF still stands
        }
    }

    /// <summary>
    /// A script that copies every <c>@media print</c> rule of the document into a screen
    /// stylesheet, lays the page out at the paper's width and returns its height in pixels: the
    /// height the printed page needs. The copied rules are the print rules, so printing afterwards
    /// lays out the same way.
    /// </summary>
    public static string MeasurePrintHeightScript(int pageWidthPx) => $$"""
        (() => {
            const css = [];
            for (const sheet of document.styleSheets) {
                let rules;
                try { rules = sheet.cssRules; } catch { continue; }
                for (const rule of rules) {
                    if (rule instanceof CSSMediaRule && /\bprint\b/i.test(rule.media.mediaText))
                        for (const inner of rule.cssRules) css.push(inner.cssText);
                }
            }
            css.push('html, body { width: {{pageWidthPx}}px !important; }');
            const style = document.createElement('style');
            style.textContent = css.join('\n');
            document.head.appendChild(style);
            return Math.ceil(document.documentElement.scrollHeight);
        })();
        """;

    // A script that appends one <style> to the document. The CSS travels as a JSON string, so
    // quotes and backslashes in it (font names) can't break out of the script.
    private static string InjectStyle(string css) =>
        $"(() => {{ const s = document.createElement('style'); s.textContent = {System.Text.Json.JsonSerializer.Serialize(css)}; document.head.appendChild(s); }})();";

    /// <summary>The printed page's colours and font, read from the rendered document, so header and
    /// footer bands match the page whatever the theme, light influence or brand font did to it.</summary>
    public sealed record PageLook(string Background, string Text, string FontFamily)
    {
        public static readonly PageLook Default = new("#ffffff", "#24292f", "\"Segoe UI\", sans-serif");

        internal const string Script = """
            (() => {
                const canvas = document.getElementById('canvas') || document.body;
                const cs = getComputedStyle(canvas), bs = getComputedStyle(document.body);
                const clear = c => !c || c === 'transparent' || /^rgba\(.*,\s*0\)$/.test(c);
                return JSON.stringify({ bg: clear(cs.backgroundColor) ? bs.backgroundColor : cs.backgroundColor, fg: cs.color, font: bs.fontFamily });
            })();
            """;

        /// <summary>Reads <see cref="Script"/>'s result. ExecuteScriptAsync returns the value JSON
        /// encoded, so the JSON arrives as a quoted string; anything unexpected gives the default page.</summary>
        public static PageLook Parse(string? result)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(result)) return Default;
                var json = result.TrimStart().StartsWith('"') ? System.Text.Json.JsonSerializer.Deserialize<string>(result) : result;
                using var doc = System.Text.Json.JsonDocument.Parse(json ?? "{}");
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return Default;
                string Read(string name, string fallback) =>
                    root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                    && v.GetString() is { Length: > 0 } s && SafeCss(s) ? s : fallback;
                var bg = Read("bg", Default.Background);
                if (IsClear(bg)) bg = Default.Background;
                return new PageLook(bg, Read("fg", Default.Text), Read("font", Default.FontFamily));
            }
            catch (System.Text.Json.JsonException)
            {
                return Default;
            }
        }

        private static bool IsClear(string color) =>
            color.Equals("transparent", StringComparison.OrdinalIgnoreCase)
            || (color.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && color.Replace(" ", "").EndsWith(",0)"));

        // The values go into a style attribute and a stylesheet: nothing that ends a declaration or
        // the markup around it.
        private static bool SafeCss(string value) => value.IndexOfAny(['<', '>', ';', '{', '}']) < 0;
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
    public static (string Header, string Footer) BuildHeaderFooter(AppSettings settings, string title, PageLook? look = null, DateTime? date = null)
    {
        var (header, footer, align) = ResolveBands(settings);
        look ??= PageLook.Default;
        // {date} is the reader's short date, filled in here as Settings previews it: Chromium's own
        // date span prints the time as well ("10/10/2026, 11:13").
        var day = System.Net.WebUtility.HtmlEncode((date ?? DateTime.Now).ToString("d", CultureInfo.CurrentCulture));
        string Dated(string band) => band.Replace("{date}", day, StringComparison.Ordinal);
        return (WrapBand(Dated(header), title, align, look), WrapBand(Dated(footer), title, align, look));
    }

    /// <summary>
    /// The header and footer templates that will actually print, after the page-number position
    /// has filled an empty matching band, and their alignment ("left", "center" or "right").
    /// Settings' preview uses this too (<see cref="SettingsPreviews.PdfBands"/>).
    /// </summary>
    public static (string Header, string Footer, string Alignment) ResolveBands(AppSettings settings)
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

        return (header, footer, align);
    }

    // A band fills its margin with the page's own colour (Chromium leaves margins white: white
    // stripes on a dark theme) and sets its text in the page's font and colour, a little quieter.
    // Chromium pads its #header/#footer boxes, so the style resets that to reach the paper's edges.
    private static string WrapBand(string template, string title, string align, PageLook look)
    {
        var body = BuildChromiumTemplate(template, title);
        if (body.Length == 0) return "";
        var justify = align switch { "center" => "center", "right" => "flex-end", _ => "flex-start" };
        var font = System.Net.WebUtility.HtmlEncode(look.FontFamily);
        return "<style>#header, #footer { padding: 0 !important; margin: 0 !important; }</style>" +
               $"<div style=\"box-sizing:border-box; width:100%; height:{Inches(BandHeightIn)}in; margin:0; padding:0 54px; display:flex; align-items:center; justify-content:{justify}; text-align:{align}; " +
               $"background:{look.Background}; color:{look.Text}; font-family:{font}; font-size:12px; -webkit-print-color-adjust:exact; print-color-adjust:exact;\">" +
               $"<span style=\"opacity:0.7; white-space:nowrap; overflow:hidden; text-overflow:ellipsis;\">{body}</span></div>";
    }
}
