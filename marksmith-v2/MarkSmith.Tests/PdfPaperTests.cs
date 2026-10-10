using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// PDF export on paper (routine run #59): true paper sizes, text insets repeated on every page,
/// header/footer bands in the page's own colours, a continuous page that ends at the content and
/// respects the PDF page-height limit, and the print rules that keep code, tables, covers and
/// folded callouts whole. Rendered PDFs were checked page by page; these pin the decisions.
/// </summary>
public class PdfPaperTests
{
    private static readonly ThemeDefinition Light = new("Light", "#ffffff", "#111111", "#222222", "#f4f4f4", "#d9d9d9", "#0078d4", "#e8f4fd", "#bfbfbf");

    // ---- Paper ----

    [Fact]
    public void A4_lock_prints_true_A4_whatever_the_stored_width()
    {
        var s = new AppSettings { A4FixedWidth = true, ContentWidth = 800 };
        Assert.Equal((8.27, 11.69), PdfExportService.PaperSize(s));
        Assert.Equal(794, PdfExportService.PageWidthPx(s));
    }

    [Fact]
    public void Without_the_lock_the_page_is_the_chosen_width_in_Letter_proportions()
    {
        var s = new AppSettings { A4FixedWidth = false, ContentWidth = 816 };
        var (w, h) = PdfExportService.PaperSize(s);
        Assert.Equal(8.5, w, 3);
        Assert.Equal(11.0, h, 3);
    }

    [Fact]
    public void Page_width_is_clamped_to_the_options_range()
    {
        Assert.Equal(400, PdfExportService.PageWidthPx(new AppSettings { A4FixedWidth = false, ContentWidth = 10 }));
        Assert.Equal(2400, PdfExportService.PageWidthPx(new AppSettings { A4FixedWidth = false, ContentWidth = 9000 }));
    }

    // ---- Continuous page ----

    [Fact]
    public void Continuous_page_ends_just_below_the_content()
    {
        var h = PdfExportService.ContinuousPageHeightIn(960);
        Assert.InRange(h, 10.0, 10.3);
    }

    [Fact]
    public void Continuous_page_never_passes_the_PDF_limit_and_shares_long_content_evenly()
    {
        // 300 in of content: two pages of about 150 in, not 200 in and a mostly empty 200 in.
        var h = PdfExportService.ContinuousPageHeightIn(300 * 96);
        Assert.InRange(h, 150.0, 155.0);
        Assert.True(PdfExportService.ContinuousPageHeightIn(5000 * 96) <= PdfExportService.MaxPageHeightIn);
    }

    [Fact]
    public void Measuring_script_lays_the_page_out_with_its_print_rules_at_the_paper_width()
    {
        var js = PdfExportService.MeasurePrintHeightScript(794);
        Assert.Contains("CSSMediaRule", js);
        Assert.Contains(@"/\bprint\b/i", js);
        Assert.Contains("width: 794px", js);
        Assert.Contains("scrollHeight", js);
    }

    // ---- Title and bands ----

    [Fact]
    public void Title_token_prints_the_documents_title_not_the_file_name()
    {
        Assert.Equal("Quarterly Review", PdfExportService.DocumentTitle("# Quarterly Review\n\nBody", @"C:\out\2026-10-10 Quarterly Review (pdf).pdf"));
        Assert.Equal("From front matter", PdfExportService.DocumentTitle("---\ntitle: From front matter\n---\n# Heading", @"C:\out\x.pdf"));
        Assert.Equal("notes", PdfExportService.DocumentTitle("", @"C:\out\notes.pdf"));
        Assert.Equal("notes", PdfExportService.DocumentTitle(null, @"C:\out\notes.pdf"));
    }

    [Fact]
    public void Bands_take_the_pages_colours_and_font_and_reach_the_edges()
    {
        var s = new AppSettings { PdfPageNumberPosition = "BottomCenter" };
        var look = new PdfExportService.PageLook("rgb(13, 17, 23)", "rgb(230, 237, 243)", "\"Inter\", sans-serif");
        var (header, footer) = PdfExportService.BuildHeaderFooter(s, "Doc", look);

        Assert.Equal("", header);
        Assert.Contains("background:rgb(13, 17, 23)", footer);
        Assert.Contains("color:rgb(230, 237, 243)", footer);
        Assert.Contains("font-family:&quot;Inter&quot;, sans-serif", footer);
        Assert.Contains("#header, #footer { padding: 0 !important;", footer);
        Assert.Contains("font-size:12px", footer);
    }

    [Theory]
    [InlineData("\"{\\\"bg\\\":\\\"rgb(1, 2, 3)\\\",\\\"fg\\\":\\\"rgb(4, 5, 6)\\\",\\\"font\\\":\\\"Segoe UI\\\"}\"", "rgb(1, 2, 3)", "rgb(4, 5, 6)")]
    [InlineData("\"{\\\"bg\\\":\\\"rgba(0, 0, 0, 0)\\\",\\\"fg\\\":\\\"rgb(4, 5, 6)\\\"}\"", "#ffffff", "rgb(4, 5, 6)")]
    [InlineData("\"{\\\"bg\\\":\\\"red;}</style><script>\\\",\\\"fg\\\":\\\"blue\\\"}\"", "#ffffff", "blue")]
    [InlineData("null", "#ffffff", "#24292f")]
    [InlineData("not json", "#ffffff", "#24292f")]
    public void Page_look_reads_the_script_result_and_falls_back_safely(string result, string bg, string fg)
    {
        var look = PdfExportService.PageLook.Parse(result);
        Assert.Equal(bg, look.Background);
        Assert.Equal(fg, look.Text);
    }

    // ---- The export itself, against a recording host ----

    [Fact]
    public async Task Paginated_export_prints_A4_with_insets_repeated_on_every_page()
    {
        var host = new RecordingHost();
        var path = Path.Combine(Path.GetTempPath(), $"ms-paper-{Guid.NewGuid():N}.pdf");
        try
        {
            var s = new AppSettings { UnlimitedHeight = false, A4FixedWidth = true };
            await new PdfExportService().ExportAsync(host, "<html><body><div id=\"canvas\">x</div></body></html>", path, s, "# T");

            Assert.NotNull(host.Setup);
            Assert.Equal(8.27, host.Setup!.PageWidthIn);
            Assert.Equal(11.69, host.Setup.PageHeightIn);
            Assert.Equal(0, host.Setup.MarginTopIn);
            var css = string.Join("\n", host.Scripts);
            Assert.Contains("box-decoration-break: clone", css);
            Assert.Contains("padding-top: 0.6in", css);
            Assert.Contains("--ms-cover-min-height", css);
            // Folded callouts print open.
            Assert.Contains("details:not([open])", css);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_footer_band_gets_its_own_margin_and_the_inset_under_it_shrinks()
    {
        var host = new RecordingHost();
        var path = Path.Combine(Path.GetTempPath(), $"ms-paper-{Guid.NewGuid():N}.pdf");
        try
        {
            var s = new AppSettings { UnlimitedHeight = false, PdfPageNumberPosition = "BottomRight" };
            await new PdfExportService().ExportAsync(host, "<html></html>", path, s, "# T");

            Assert.Equal(PdfExportService.BandHeightIn, host.Setup!.MarginBottomIn);
            Assert.Equal(0, host.Setup.MarginTopIn);
            Assert.Contains("pageNumber", host.Setup.FooterTemplate);
            var css = string.Join("\n", host.Scripts);
            Assert.Contains($"padding-bottom: {PdfExportService.InsetUnderBand(true).ToString(System.Globalization.CultureInfo.InvariantCulture)}in", css);
            // The page's own @page margin, not a stylesheet's zero, decides the band's room.
            Assert.Contains("margin: 0in 0 0.45in 0 !important", css);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Continuous_export_is_one_page_measured_with_the_print_rules()
    {
        var host = new RecordingHost { MeasuredHeight = "4800" };
        var path = Path.Combine(Path.GetTempPath(), $"ms-paper-{Guid.NewGuid():N}.pdf");
        try
        {
            var s = new AppSettings { UnlimitedHeight = true, A4FixedWidth = true, PdfPageNumberPosition = "BottomRight" };
            await new PdfExportService().ExportAsync(host, "<html></html>", path, s, "# T");

            Assert.Equal(794 / 96.0, host.Setup!.PageWidthIn, 3);
            Assert.Equal(PdfExportService.ContinuousPageHeightIn(4800), host.Setup.PageHeightIn, 3);
            Assert.Equal("", host.Setup.FooterTemplate); // nothing to number on one page
        }
        finally { File.Delete(path); }
    }

    // ---- The HTML the PDF prints ----

    [Fact]
    public void Print_rules_wrap_code_keep_headings_with_their_text_and_hide_preview_furniture()
    {
        var html = AppServices.MarkdownHtml.Render("# Hi\n\n```\ncode\n```", new AppSettings { IncludeToc = false }, Light);
        Assert.Contains("pre { white-space: pre-wrap !important;", html);
        Assert.Contains("h1, h2, h3, h4, h5, h6 { break-after: avoid;", html);
        Assert.Contains(".page-break-gap, #overflow-banner { display: none !important; }", html);
        Assert.DoesNotContain("@page { margin: 0 !important; }", html);
    }

    [Fact]
    public void Table_cells_keep_words_whole()
    {
        var html = AppServices.MarkdownHtml.Render("| a | b |\n|---|---|\n| 1 | 2 |", new AppSettings { IncludeToc = false }, Light);
        Assert.DoesNotContain("overflow-wrap: anywhere; word-break: break-word;", html);
        Assert.Contains("td a, td code, th a, th code { overflow-wrap: anywhere; }", html);
    }

    [Fact]
    public void Contents_follow_a_cover_page_instead_of_printing_above_it()
    {
        const string md = ":::cover-page\ntitle: \"Guide\"\nauthor: \"Team\"\n:::\n\n# Guide\n\n## Part one\n\nText.";
        var html = AppServices.MarkdownHtml.Render(md, new AppSettings { IncludeToc = true }, Light);
        var cover = html.IndexOf("<div class=\"cover-page", StringComparison.Ordinal);
        var toc = html.IndexOf("<nav id=\"toc\">", StringComparison.Ordinal);
        Assert.True(cover >= 0 && toc > cover, "the contents box should come after the cover page");
    }

    [Fact]
    public void Without_a_cover_the_contents_come_first()
    {
        var html = AppServices.MarkdownHtml.Render("# Guide\n\n## Part one\n\nText.", new AppSettings { IncludeToc = true }, Light);
        Assert.True(html.IndexOf("<nav id=\"toc\">", StringComparison.Ordinal) < html.IndexOf("<h1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_watermark_after_a_cover_page_keeps_its_text_and_opacity()
    {
        const string md = ":::cover-page\ntitle: \"Guide\"\n:::\n\n:::watermark \"DRAFT\" opacity=0.10\n\n# Guide";
        var html = AppServices.MarkdownHtml.Render(md, new AppSettings(), Light);
        Assert.Contains("<div class=\"mk-watermark-text\">DRAFT</div>", html);
        Assert.Contains("--wm-opacity: 0.10", html);
        Assert.DoesNotContain(":::watermark", html);
    }

    [Fact]
    public void Page_border_draws_a_frame_only_when_on()
    {
        var on = AppServices.MarkdownHtml.Render("# Hi", new AppSettings { PageBorder = true }, Light);
        var off = AppServices.MarkdownHtml.Render("# Hi", new AppSettings { PageBorder = false }, Light);
        Assert.Contains("<div class=\"ms-page-frame\"", on);
        Assert.DoesNotContain("<div class=\"ms-page-frame\"", off);
    }

    [Fact]
    public void A_cover_page_fills_one_printed_page_but_never_forces_a_break_on_a_continuous_page()
    {
        const string md = ":::cover-page\ntitle: \"Guide\"\n:::\n\n# Guide";
        var paged = AppServices.MarkdownHtml.Render(md, new AppSettings { UnlimitedHeight = false }, Light);
        var continuous = AppServices.MarkdownHtml.Render(md, new AppSettings { UnlimitedHeight = true }, Light);
        Assert.Contains("min-height: var(--ms-cover-min-height, 800px);", paged);
        Assert.Contains(".cover-page + .page-break { display: none; }", paged);
        Assert.Contains("margin: 0; page-break-after: always; break-after: page;", paged);
        Assert.DoesNotContain("margin: 0; page-break-after: always; break-after: page;", continuous);
    }

    private sealed class RecordingHost : IWebRenderHost
    {
        public List<string> Scripts { get; } = new();
        public PdfPageSetup? Setup { get; private set; }
        public string MeasuredHeight { get; init; } = "1000";

        public Task<bool> EnsureReadyAsync() => Task.FromResult(true);
        public Task NavigateToStringAsync(string html) => Task.CompletedTask;

        public Task<string?> ExecuteScriptAsync(string javaScript)
        {
            Scripts.Add(javaScript);
            if (javaScript.Contains("CSSMediaRule")) return Task.FromResult<string?>(MeasuredHeight);
            if (javaScript.Contains("getComputedStyle(canvas)"))
                return Task.FromResult<string?>("\"{\\\"bg\\\":\\\"rgb(255, 255, 255)\\\",\\\"fg\\\":\\\"rgb(0, 0, 0)\\\",\\\"font\\\":\\\"Segoe UI\\\"}\"");
            return Task.FromResult<string?>("null");
        }

        public Task<bool> PrintToPdfAsync(string outputPath, PdfPageSetup setup)
        {
            Setup = setup;
            using var doc = new PdfSharp.Pdf.PdfDocument();
            doc.AddPage();
            doc.Save(outputPath);
            return Task.FromResult(true);
        }

        public Task BeginHarvestAsync() => Task.CompletedTask;
        public Task EndHarvestAsync() => Task.CompletedTask;
    }
}
