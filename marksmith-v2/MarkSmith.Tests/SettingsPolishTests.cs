using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Run #53: Settings, done properly. The previews under a setting show what it will produce, go
/// through the export's own code, and the rows that depend on another setting say so.
/// </summary>
public sealed class SettingsPolishTests
{
    private static readonly DateTime Day = new(2026, 7, 24, 9, 5, 0);

    // ---- File name ----

    [Fact]
    public void File_name_preview_uses_the_template_and_the_default_format()
    {
        Assert.Equal("2026-07-24 My Report.pdf", SettingsPreviews.FileName("{date} {title}", "pdf", Day));
        Assert.Equal("My Report (docx).docx", SettingsPreviews.FileName("{title} ({format})", "docx", Day));
        Assert.Equal("My Report 09-05-00.eml", SettingsPreviews.FileName("{title} {time}", "email", Day));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_template_names_the_file_after_the_title(string? template) =>
        Assert.Equal("My Report.pdf", SettingsPreviews.FileName(template, "pdf", Day));

    [Fact]
    public void An_unknown_format_previews_as_pdf_like_the_export() =>
        Assert.Equal("My Report.pdf", SettingsPreviews.FileName("{title}", "html", Day));

    [Fact]
    public void File_name_preview_strips_characters_windows_refuses()
    {
        var name = SettingsPreviews.FileName("{title}: draft?", "pdf", Day);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('?', name);
        Assert.EndsWith(".pdf", name);
    }

    // ---- PDF header and footer ----

    [Fact]
    public void A_header_with_page_numbers_off_still_shows()
    {
        // The old one-line preview showed only the page number's band, so this read "(no header/footer)".
        var bands = SettingsPreviews.PdfBands("None", "{title}", "", Day);
        Assert.Equal("My Report", bands.Header);
        Assert.Equal("", bands.Footer);
        Assert.False(bands.IsEmpty);
    }

    [Fact]
    public void Nothing_set_is_an_empty_page() =>
        Assert.True(SettingsPreviews.PdfBands("None", "", "  ", Day).IsEmpty);

    [Theory]
    [InlineData("BottomRight", "", "Page 2 of 10", "right")]
    [InlineData("BottomCenter", "", "Page 2 of 10", "center")]
    [InlineData("TopRight", "Page 2 of 10", "", "right")]
    [InlineData("None", "", "", "left")]
    public void Page_number_position_fills_its_band_and_sets_the_alignment(string position, string header, string footer, string align)
    {
        var bands = SettingsPreviews.PdfBands(position, "", "", Day);
        Assert.Equal(header, bands.Header);
        Assert.Equal(footer, bands.Footer);
        Assert.Equal(align, bands.Alignment);
    }

    [Fact]
    public void A_typed_footer_wins_over_the_page_number_default()
    {
        var bands = SettingsPreviews.PdfBands("BottomRight", "", "Confidential {page}", Day);
        Assert.Equal("Confidential 2", bands.Footer);
    }

    [Fact]
    public void Date_previews_as_the_readers_short_date_which_is_what_the_pdf_prints()
    {
        var bands = SettingsPreviews.PdfBands("None", "{date}", "{pages}", Day);
        Assert.Equal(Day.ToString("d", CultureInfo.CurrentCulture), bands.Header);
        Assert.Equal("10", bands.Footer);
    }

    [Fact]
    public void Preview_and_export_resolve_the_same_bands()
    {
        var settings = new AppSettings { PdfPageNumberPosition = "TopRight", PdfFooterTemplate = "{title}" };
        var (header, footer, align) = PdfExportService.ResolveBands(settings);
        Assert.Equal("Page {page} of {pages}", header);
        Assert.Equal("{title}", footer);
        Assert.Equal("right", align);

        var (wrappedHeader, wrappedFooter) = PdfExportService.BuildHeaderFooter(settings, "Doc");
        Assert.Contains("pageNumber", wrappedHeader);
        Assert.Contains("text-align:right", wrappedFooter);
    }

    [Fact]
    public void Streaming_address_names_the_real_port() =>
        Assert.Equal("ws://127.0.0.1:51000/api/stream", SettingsPreviews.StreamingEndpoint(51000));

    // ---- Google ----

    [Fact]
    public void Google_status_says_what_to_do_next()
    {
        Assert.Equal("Add your Google Cloud client below to sign in.", MainViewModel.DescribeGoogleAccount(false, false, null));
        Assert.Equal("Not connected", MainViewModel.DescribeGoogleAccount(true, false, null));
        Assert.Equal("Connected as a@b.com. Exports can go straight to Google Docs.", MainViewModel.DescribeGoogleAccount(true, true, "a@b.com"));
        Assert.StartsWith("Connected to Google.", MainViewModel.DescribeGoogleAccount(true, true, ""));
    }

    [Fact]
    public void Google_messages_name_the_real_settings_page()
    {
        foreach (var file in new[] { "ViewModels/MainViewModel.cs", "Services/GoogleAuthService.cs" })
        {
            var text = File.ReadAllText(Path.Combine(CoreDir(), file));
            // Messages only (string literals), not comments.
            Assert.DoesNotMatch(@"""[^""\r\n]*Settings → Google[^""\r\n]*""", text);
            Assert.DoesNotMatch(@"""[^""\r\n]*Settings > Local REST API[^""\r\n]*""", text);
        }
    }

    // ---- The Settings page ----

    [Theory]
    [InlineData("Port", "ApiEnabled")]
    [InlineData("WebSocket streaming", "ApiEnabled")]
    [InlineData("Browser extension pairing", "ApiEnabled")]
    [InlineData("Install updates automatically", "CheckForUpdatesOnStartup")]
    public void Rows_that_depend_on_another_setting_grey_out_with_it(string header, string parent)
    {
        var card = CardOpeningTag(header);
        Assert.Contains($"IsEnabled=\"{{Binding {parent}}}\"", card);
    }

    [Fact]
    public void Settings_has_no_static_example_that_could_drift()
    {
        var xaml = SettingsXaml();
        Assert.DoesNotContain("2026-07-24 My Report.pdf", xaml);
        Assert.DoesNotContain("PdfFooterPreview", xaml);
        Assert.DoesNotContain("ws://127.0.0.1:PORT", xaml);
    }

    [Fact]
    public void Every_settings_row_has_a_description()
    {
        var cards = Regex.Matches(SettingsXaml(), @"<controls:SettingsCard\s[^>]*>", RegexOptions.Singleline)
            .Select(m => m.Value).ToList();
        Assert.NotEmpty(cards);
        Assert.All(cards, c => Assert.Contains("Description=", c));
    }

    private static string CardOpeningTag(string header)
    {
        var match = Regex.Match(SettingsXaml(),
            @"<controls:SettingsCard\s[^>]*Header=""" + Regex.Escape(header) + "\"[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, $"no Settings row called {header}");
        return match.Value;
    }

    private static string SettingsXaml() =>
        File.ReadAllText(Path.Combine(DesktopDir(), "Views", "SettingsView.xaml"));

    private static string DesktopDir([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "MarkSmith.Desktop");

    private static string CoreDir([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "MarkSmith.Core");
}
