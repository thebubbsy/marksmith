using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #230: the double-click-only actions in Extension setup, Settings ▸ About and the OCR footer
// become buttons the keyboard can reach (with their menus on Shift+F10), their menus show only
// shortcuts that exist, in the accelerator column, and copies say whether they worked.
public class Run230KeyboardReachAndHonestMenusTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string DesktopDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MarkSmith.Desktop");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { DesktopDir() }.Concat(parts).ToArray()));

    private static XDocument Xaml(params string[] parts) => XDocument.Load(Path.Combine(new[] { DesktopDir() }.Concat(parts).ToArray()));

    private static string ExtCode => Read("Views", "ExtensionSetupView.xaml.cs");
    private static XDocument ExtXaml => Xaml("Views", "ExtensionSetupView.xaml");
    private static string SettingsCode => Read("Views", "SettingsView.xaml.cs");
    private static XDocument SettingsXaml => Xaml("Views", "SettingsView.xaml");
    private static string OcrCode => Read("Views", "Ocr", "OcrInspectionStudioWindow.xaml.cs");
    private static XDocument OcrXaml => Xaml("Views", "Ocr", "OcrInspectionStudioWindow.xaml");
    private static string SuiteCode => Read("Views", "SuiteHubView.xaml.cs");

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found");
        var open = code.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code.Substring(start, i - start + 1);
        }
        throw new InvalidOperationException($"{signature} has no closing brace");
    }

    private static string BridgeTable(string code)
    {
        var start = code.IndexOf("KeyboardContextMenu.BridgeAll(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return code.Substring(start, code.IndexOf(");", start, StringComparison.Ordinal) - start);
    }

    // Menu labels that spell a shortcut in brackets, e.g. Text = "Copy (Ctrl+C)".
    private static readonly Regex HintInLabel = new(@"Text = ""[^""]*\((Ctrl|Esc|Shift|Alt|Left|Right|Enter)[^""]*\)""");

    [Fact]
    public void ExtensionSetup_StepBadges_AreKeyboardButtons()
    {
        var xaml = ExtXaml;
        var bridge = BridgeTable(ExtCode);
        for (int n = 1; n <= 3; n++)
        {
            var badge = Named(xaml, $"Step{n}Badge");
            Assert.Equal("Button", badge.Name.LocalName);
            Assert.Equal("{StaticResource StepBadgeButtonStyle}", (string?)badge.Attribute("Style"));
            Assert.Equal($"OnStep{n}BadgeClick", (string?)badge.Attribute("Click"));
            Assert.Null(badge.Attribute("Tapped"));
            Assert.Contains("Shift+F10", (string)badge.Attributes().First(a => a.Name.LocalName == "ToolTipService.ToolTip"));
            Assert.Contains($"(Step{n}Badge, OnStepBadgeRightTapped)", bridge);
        }
        Assert.DoesNotContain("BadgeBorder", ExtCode);
        Assert.DoesNotContain("BadgeTapped", ExtCode);
    }

    [Fact]
    public void ExtensionSetup_StatusCard_HasACheckNowButtonInsteadOfADoubleClick()
    {
        var xaml = ExtXaml;
        var code = ExtCode;
        var card = Named(xaml, "StatusCard");
        Assert.Null(card.Attribute("DoubleTapped"));
        Assert.DoesNotContain("Double-click", (string)card.Attributes().First(a => a.Name.LocalName == "ToolTipService.ToolTip"));
        Assert.DoesNotContain("OnStatusCardDoubleTapped", code);

        var check = Named(xaml, "CheckNowButton");
        Assert.Equal("Button", check.Name.LocalName);
        Assert.Equal("OnCheckNowClick", (string?)check.Attribute("Click"));
        Assert.True(check.Ancestors().Contains(card), "the button lives in the status card");
        Assert.Contains("(CheckNowButton, OnStatusCardRightTapped)", BridgeTable(code));
        Assert.Contains("Refresh(force: true)", Method(code, "void OnCheckNowClick("));
    }

    [Fact]
    public void ExtensionSetup_FocusMovesOnlyToControlsThatCanTakeIt()
    {
        var code = ExtCode;
        // A Border can't take focus: Step 2 used to focus the status card and do nothing.
        Assert.DoesNotContain("StatusCard.Focus(", code);
        Assert.Contains("CheckNowButton.Focus(", Method(code, "void OnStep2BadgeClick("));
        // The folder controls collapse when the extension isn't bundled; focus falls back.
        var fallback = Method(code, "void FocusFolderOrFallback(");
        Assert.Contains("_folder is not null", fallback);
        Assert.Contains("CheckNowButton.Focus(", fallback);
        Assert.DoesNotContain("CopyFolderButton ?? (Control)FolderPathBox", code);
        // Turn on collapses once the API is on: its focus goes to the always-visible check button.
        Assert.Contains("CheckNowButton.Focus(", Method(code, "void Refresh("));
        Assert.Contains("CheckNowButton.Focus(", Method(code, "void OnTurnOnClick("));
    }

    [Fact]
    public void ExtensionSetup_Menus_ShowOnlyRealShortcuts_InTheAcceleratorColumn()
    {
        var code = ExtCode;
        Assert.DoesNotMatch(HintInLabel, code);
        // Ctrl+C was advertised for the status copy but never bound.
        Assert.DoesNotContain("KeyboardAcceleratorTextOverride = \"Ctrl+C\"", code);
        var keys = Method(code, "void OnSetupKeyDown(");
        foreach (var (shown, key) in new[] { ("Ctrl+R", "VirtualKey.R"), ("Ctrl+O", "VirtualKey.O"), ("Ctrl+Shift+C", "VirtualKey.C") })
        {
            Assert.Contains($"KeyboardAcceleratorTextOverride = \"{shown}\"", code);
            Assert.Contains(key, keys);
        }
        // The status copy now answers with a tick on the check button.
        Assert.Contains("CopyFeedback.CopyWithTick(StatusText.Text, CheckNowIcon)", Method(code, "void OnStatusCardRightTapped("));
    }

    [Fact]
    public void Settings_VersionLine_IsAButtonThatOpensTheAboutMenu()
    {
        var xaml = SettingsXaml;
        var button = Named(xaml, "VersionButton");
        Assert.Equal("Button", button.Name.LocalName);
        Assert.Equal("OnVersionButtonClick", (string?)button.Attribute("Click"));
        Assert.Equal("OnAboutHeaderRightTapped", (string?)button.Attribute("RightTapped"));
        var text = Named(xaml, "VersionText");
        Assert.Equal(button, text.Parent);
        Assert.Null(text.Attribute("DoubleTapped"));
        Assert.Contains("(VersionButton, OnAboutHeaderRightTapped)", BridgeTable(SettingsCode));
        Assert.DoesNotContain("OnVersionTextDoubleTapped", SettingsCode);
        Assert.Contains("AutomationProperties.SetName(VersionButton,", SettingsCode);
    }

    [Fact]
    public void Settings_EveryCopyVersionDetails_CopiesTheSameText_AndReportsFailure()
    {
        var code = SettingsCode;
        var copy = Method(code, "void CopyVersionDetails(");
        Assert.Contains("var error = await ClipboardWriter.TrySetTextAsync(text);", copy);
        Assert.Contains("InfoBarSeverity.Warning", copy);
        Assert.Contains("CopyVersionDetails()", Method(code, "void OnVersionButtonClick("));
        Assert.Contains("copyVersionItem.Click += (_, _) => CopyVersionDetails();", Method(code, "void OnAboutHeaderRightTapped("));
        Assert.Contains("copyVersionItem.Click += (_, _) => CopyVersionDetails();", Method(code, "void OnCheckButtonRightTapped("));
    }

    [Fact]
    public void Settings_PdfPositionMenu_ShowsTheCurrentChoice()
    {
        var code = SettingsCode;
        var menu = Method(code, "void OnPdfPagePreviewRightTapped(");
        Assert.Contains("new RadioMenuFlyoutItem", menu);
        Assert.Contains("IsChecked = string.Equals(currentPosition, value", menu);
        Assert.Contains("foreach (var (value, label) in PdfPageNumberPositions)", menu);

        // The menu's values are exactly the Page numbers combo's tags.
        var combo = SettingsXaml.Descendants().Single(e => e.Name.LocalName == "ComboBox"
            && (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "AutomationProperties.Name") == "Page number position");
        var tags = combo.Elements().Select(i => (string?)i.Attribute("Tag")).OrderBy(t => t).ToArray();
        var table = code.Substring(code.IndexOf("PdfPageNumberPositions =", StringComparison.Ordinal));
        table = table.Substring(0, table.IndexOf("};", StringComparison.Ordinal));
        var values = Regex.Matches(table, @"\(""(\w+)"", ""[^""]+""\)").Select(m => m.Groups[1].Value).OrderBy(v => v).ToArray();
        Assert.Equal(tags, values);
    }

    [Fact]
    public void Ocr_SessionMetrics_IsAButton_AndTheFooterHasNoDoubleClick()
    {
        var xaml = OcrXaml;
        var footer = Named(xaml, "FooterBarGrid");
        Assert.Null(footer.Attribute("DoubleTapped"));
        Assert.DoesNotContain("Double-click", (string)footer.Attributes().First(a => a.Name.LocalName == "ToolTipService.ToolTip"));
        var button = Named(xaml, "SessionMetricsButton");
        Assert.Equal("Button", button.Name.LocalName);
        Assert.Equal("OnSessionMetricsButtonClick", (string?)button.Attribute("Click"));
        Assert.Equal(button, Named(xaml, "SessionMetricsText").Parent);
        Assert.Contains("(SessionMetricsButton, OnFooterBarRightTapped)", BridgeTable(OcrCode));
        Assert.DoesNotContain("OnFooterBarDoubleTapped", OcrCode);
    }

    [Fact]
    public void Ocr_CopyNote_NeverRestoresAStaleStatus()
    {
        var copy = Method(OcrCode, "void CopyFooterText(");
        // Restores only if the note is still showing, and never captures its own note as "previous".
        Assert.Contains("if (FooterStatusText.Text != note || !ReferenceEquals(_copyNote, note)) return;", copy);
        Assert.Contains("if (_copyNote is null || FooterStatusText.Text != _copyNote)", copy);
        Assert.Contains("SetFooterStatusText(", copy);
        Assert.Contains("Couldn't copy", copy);
        var menu = Method(OcrCode, "void OnFooterBarRightTapped(");
        Assert.Contains("CopyFooterText(SessionMetricsText.Text", menu);
    }

    [Fact]
    public void Ocr_Menus_ShowShortcutsInTheAcceleratorColumn_AndCopyPageCopiesThePage()
    {
        var code = OcrCode;
        Assert.DoesNotMatch(HintInLabel, code);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+Enter\"", code);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Esc\"", code);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+Shift+R\"", code);
        Assert.DoesNotContain("KeyboardAcceleratorTextOverride = \"Left\",", code);

        var sheet = Method(code, "void OnPageSheetRightTapped(");
        var copyPage = sheet.Substring(sheet.IndexOf("var copyPageItem", StringComparison.Ordinal));
        copyPage = copyPage.Substring(0, copyPage.IndexOf("menu.Items.Add(copyPageItem);", StringComparison.Ordinal));
        // Run #266 hoisted the page's Markdown into pageMd (it also drives IsEnabled).
        Assert.Contains("var pageMd = _session.CurrentPage?.AssembledPageMarkdown", sheet);
        Assert.Contains("pageMd", copyPage);
        Assert.DoesNotContain("OnCopyMarkdownClick", copyPage);
        // Ctrl+Shift+C copies the preview, so it's only shown when the preview is the page.
        Assert.Contains("_markdownScopeIsDocument ? \"\" : \"Ctrl+Shift+C\"", copyPage);
    }

    [Fact]
    public void SuiteHub_RecentStripDoubleClick_IgnoresItsOwnControls()
    {
        var handler = Method(SuiteCode, "void OnRecentDocsStripDoubleTapped(");
        Assert.Contains("d is ButtonBase or TextBox or AutoSuggestBox", handler);
        Assert.True(handler.IndexOf("return;", StringComparison.Ordinal) < handler.IndexOf("OnRefreshRecentDocsClick", StringComparison.Ordinal));
    }
}
