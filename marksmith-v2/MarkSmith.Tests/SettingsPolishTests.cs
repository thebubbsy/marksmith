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

    [Fact]
    public void SettingsCard_and_OptionRow_define_Normal_PointerOver_Pressed_and_Disabled_visual_states()
    {
        var cardXaml = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "SettingsCard.xaml"));
        var rowXaml = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "OptionRow.xaml"));
        foreach (var xaml in new[] { cardXaml, rowXaml })
        {
            Assert.Contains("x:Name=\"Normal\"", xaml);
            Assert.Contains("x:Name=\"PointerOver\"", xaml);
            Assert.Contains("x:Name=\"Pressed\"", xaml);
            Assert.Contains("x:Name=\"Disabled\"", xaml);
        }

        var cardCode = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "SettingsCard.xaml.cs"));
        var rowCode = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "OptionRow.xaml.cs"));
        Assert.Contains("_pointerPressed ? \"Pressed\"", cardCode);
        Assert.Contains("_pointerPressed ? \"Pressed\"", rowCode);
    }

    [Fact]
    public void SettingsCard_and_OptionRow_support_tapping_to_focus_or_open_ComboBox_NumberBox_and_Details_inputs()
    {
        var cardCode = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "SettingsCard.xaml.cs"));
        var rowCode = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "OptionRow.xaml.cs"));

        Assert.Contains("Action is ComboBox { IsEnabled: true } combo", cardCode);
        Assert.Contains("combo.IsDropDownOpen = !combo.IsDropDownOpen;", cardCode);
        Assert.Contains("Action is NumberBox { IsEnabled: true } numberBox", cardCode);
        Assert.Contains("FindFirstInput(Details as DependencyObject)", cardCode);
        Assert.Contains("AutomationProperties.SetHelpText(this, Description ?? \"\");", cardCode);

        Assert.Contains("case ComboBox { IsEnabled: true } combo:", rowCode);
        Assert.Contains("case NumberBox { IsEnabled: true } numberBox:", rowCode);
        Assert.Contains("FindFirstInput(Details as DependencyObject)", rowCode);
        Assert.Contains("AutomationProperties.SetHelpText(this, Description ?? \"\");", rowCode);
    }

    [Fact]
    public void SettingsCard_wraps_wide_actions_below_header_on_narrow_widths()
    {
        var cardCode = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "SettingsCard.xaml.cs"));
        Assert.Contains("private void UpdateActionPlacement(double width)", cardCode);
        Assert.Contains("var compactSwitch = Action is null or ToggleSwitch or CheckBox;", cardCode);
        Assert.Contains("Grid.SetRow(ActionPart, 1);", cardCode);
        Assert.Contains("Grid.SetRow(ActionPart, 0);", cardCode);
    }

    [Fact]
    public void SettingsView_supports_keyboard_page_cycling_and_direct_page_shortcuts()
    {
        var code = SettingsCode();
        Assert.Contains("private void StepNavPage(int delta)", code);
        Assert.Contains("if (ctrl && e.Key == Windows.System.VirtualKey.Tab)", code);
        Assert.Contains("Windows.System.VirtualKey.PageDown or Windows.System.VirtualKey.PageUp", code);
        Assert.Contains(">= Windows.System.VirtualKey.Number1 and <= Windows.System.VirtualKey.Number7", code);
        Assert.Contains("else if (PluginWarningsBar.IsOpen)", code);
        Assert.Contains("OnPdfUserPasswordKeyDown", code);
        Assert.Contains("OnGoogleClientIdKeyDown", code);
        Assert.Contains("OnGoogleSecretKeyDown", code);
        Assert.Contains("OnHouseStyleReplyKeyDown", code);
    }

    [Fact]
    public void SettingsView_adapts_layout_on_narrow_windows()
    {
        var code = SettingsCode();
        Assert.Contains("private void ApplyResponsiveLayout(double width)", code);
        Assert.Contains("NavigationViewPaneDisplayMode.LeftCompact", code);
        Assert.Contains("Grid.SetRow(PdfPagePreviewPanel, 1);", code);
        Assert.Contains("Grid.SetRow(AboutCheckPanel, 1);", code);
    }

    [Fact]
    public void Every_interactive_control_and_nav_item_in_SettingsView_has_an_accessible_name()
    {
        var xaml = SettingsXaml();
        foreach (var tag in new[] { "General", "Pdf", "Automation", "Google", "License", "Plugins", "About" })
        {
            var navItem = Regex.Match(xaml, $"<NavigationViewItem[^>]*Tag=\"{tag}\"[^>]*>", RegexOptions.Singleline);
            Assert.True(navItem.Success, $"missing NavigationViewItem for {tag}");
            Assert.Contains("AutomationProperties.Name=", navItem.Value);
            Assert.Contains("ToolTipService.ToolTip=", navItem.Value);
        }

        foreach (var controlTag in new[] { "ToggleSwitch", "ComboBox", "TextBox", "PasswordBox", "NumberBox", "CheckBox", "HyperlinkButton" })
        {
            var matches = Regex.Matches(xaml, $"<{controlTag}\\s[^>]*>", RegexOptions.Singleline)
                .Select(m => m.Value)
                .ToList();
            Assert.NotEmpty(matches);
            foreach (var m in matches)
            {
                // DownloadLink has Content="Open the releases page" which names itself in UIA
                if (m.Contains("x:Name=\"DownloadLink\"")) continue;
                Assert.True(m.Contains("AutomationProperties.Name="), $"{controlTag} missing AutomationProperties.Name: {m}");
            }
        }
    }

    [Fact]
    public void SettingsView_syncs_live_accessibility_names_and_restores_focus_after_async_actions()
    {
        var code = SettingsCode();
        Assert.Contains("RenderGeneralDynamicNames()", code);
        Assert.Contains("RenderApiStatus()", code);
        Assert.Contains("RenderHouseStyleStatus()", code);
        Assert.Contains("CloudRescanStatusText", code);
        Assert.Contains("if (!HoverPolish.AnimationsEnabled)", code);
        Assert.Contains("CheckRing.Visibility = Visibility.Visible;", code);
        Assert.Contains("CheckRing.Visibility = Visibility.Collapsed;", code);
        Assert.Contains("parked.Return();", code); // run #279: focus returns only if Check had it
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

    private static string SettingsCode() =>
        File.ReadAllText(Path.Combine(DesktopDir(), "Views", "SettingsView.xaml.cs"));

    private static string DesktopDir([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "MarkSmith.Desktop");

    private static string CoreDir([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "MarkSmith.Core");
}
