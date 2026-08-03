using System;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// Task 10 — PdfExportService header/footer engine: token substitution, Chromium template building
// and position-driven header/footer assembly. These are pure string transforms, so they're fully
// unit-testable without a web renderer.
public class PdfExportServiceTests
{
    private static readonly DateTime Date = new(2026, 7, 27);

    // ---- Literal token substitution (Settings preview + tests) ----

    [Fact]
    public void SubstituteTokens_replaces_all_four_tokens()
    {
        var result = PdfExportService.SubstituteTokens(
            "{title} | {date} | {page}/{pages}", "Report", 3, 12, Date);

        Assert.Equal("Report | 2026-07-27 | 3/12", result);
    }

    [Fact]
    public void SubstituteTokens_replaces_pages_before_page_so_neither_corrupts()
    {
        // The classic bug: replacing {page} first turns {pages} into "<n>s". The engine must not.
        var result = PdfExportService.SubstituteTokens("Page {page} of {pages}", "T", 2, 40, Date);

        Assert.Equal("Page 2 of 40", result);
        Assert.DoesNotContain("{", result);
    }

    [Fact]
    public void SubstituteTokens_replaces_repeated_tokens()
    {
        var result = PdfExportService.SubstituteTokens("{page}-{page}-{pages}", "T", 7, 9, Date);

        Assert.Equal("7-7-9", result);
    }

    [Fact]
    public void SubstituteTokens_empty_template_returns_empty()
    {
        Assert.Equal("", PdfExportService.SubstituteTokens("", "T", 1, 1, Date));
        Assert.Equal("", PdfExportService.SubstituteTokens(null!, "T", 1, 1, Date));
    }

    // ---- Chromium template building ----

    [Fact]
    public void BuildChromiumTemplate_emits_special_spans()
    {
        var html = PdfExportService.BuildChromiumTemplate("Page {page} of {pages} — {date}", "T");

        Assert.Contains("<span class=\"pageNumber\"></span>", html);
        Assert.Contains("<span class=\"totalPages\"></span>", html);
        Assert.Contains("<span class=\"date\"></span>", html);
        Assert.DoesNotContain("{page}", html);
        Assert.DoesNotContain("{pages}", html);
    }

    [Fact]
    public void BuildChromiumTemplate_html_encodes_title()
    {
        var html = PdfExportService.BuildChromiumTemplate("{title}", "A & B <C>");

        Assert.Contains("A &amp; B &lt;C&gt;", html);
        Assert.DoesNotContain("<C>", html);
    }

    [Fact]
    public void BuildChromiumTemplate_pages_before_page_no_corruption()
    {
        var html = PdfExportService.BuildChromiumTemplate("{page}{pages}", "T");

        // Exactly one of each span, in order, with no stray "s" from a partial {page} replacement.
        Assert.Equal(
            "<span class=\"pageNumber\"></span><span class=\"totalPages\"></span>",
            html);
    }

    [Fact]
    public void BuildChromiumTemplate_empty_returns_empty()
    {
        Assert.Equal("", PdfExportService.BuildChromiumTemplate("", "T"));
        Assert.Equal("", PdfExportService.BuildChromiumTemplate("   ", "T"));
    }

    // ---- Position-driven header/footer assembly ----

    [Fact]
    public void BuildHeaderFooter_none_position_emits_nothing_by_default()
    {
        var s = new AppSettings { PdfPageNumberPosition = "None" };
        var (header, footer) = PdfExportService.BuildHeaderFooter(s, "Doc");

        Assert.Equal("", header);
        Assert.Equal("", footer);
    }

    [Fact]
    public void BuildHeaderFooter_bottom_right_injects_default_footer()
    {
        var s = new AppSettings { PdfPageNumberPosition = "BottomRight" };
        var (header, footer) = PdfExportService.BuildHeaderFooter(s, "Doc");

        Assert.Equal("", header);
        Assert.Contains("text-align:right", footer);
        Assert.Contains("<span class=\"pageNumber\"></span>", footer);
        Assert.Contains("<span class=\"totalPages\"></span>", footer);
    }

    [Fact]
    public void BuildHeaderFooter_bottom_center_aligns_center()
    {
        var s = new AppSettings { PdfPageNumberPosition = "BottomCenter" };
        var (_, footer) = PdfExportService.BuildHeaderFooter(s, "Doc");

        Assert.Contains("text-align:center", footer);
    }

    [Fact]
    public void BuildHeaderFooter_top_right_injects_default_header()
    {
        var s = new AppSettings { PdfPageNumberPosition = "TopRight" };
        var (header, footer) = PdfExportService.BuildHeaderFooter(s, "Doc");

        Assert.Contains("text-align:right", header);
        Assert.Contains("<span class=\"pageNumber\"></span>", header);
        Assert.Equal("", footer);
    }

    [Fact]
    public void BuildHeaderFooter_explicit_templates_render_even_when_position_none()
    {
        var s = new AppSettings
        {
            PdfPageNumberPosition = "None",
            PdfHeaderTemplate = "{title}",
            PdfFooterTemplate = "{page}/{pages}",
        };
        var (header, footer) = PdfExportService.BuildHeaderFooter(s, "My Doc");

        Assert.Contains("My Doc", header);
        Assert.Contains("<span class=\"pageNumber\"></span>", footer);
        Assert.Contains("<span class=\"totalPages\"></span>", footer);
    }

    [Fact]
    public void BuildHeaderFooter_explicit_footer_wins_over_default_injection()
    {
        var s = new AppSettings
        {
            PdfPageNumberPosition = "BottomRight",
            PdfFooterTemplate = "{title} — {date}",
        };
        var (_, footer) = PdfExportService.BuildHeaderFooter(s, "Notes");

        Assert.Contains("Notes", footer);
        Assert.Contains("<span class=\"date\"></span>", footer);
        // The user's template replaced the default, so no bare pageNumber span was injected.
        Assert.DoesNotContain("pageNumber", footer);
    }
}
