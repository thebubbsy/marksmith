using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #235: Suite Hub's version, plan and health badges and its uptime / memory footer pills were
// mouse-only Borders; they are now Buttons that Tab reaches, Enter runs and Shift+F10 opens. The
// cache pill only counts (and prunes) MarkSmith's own temp files, the recent documents menu stops
// advertising Ctrl+O for a command that key does not run, and the Welcome Tour's step badge is a
// button too.
public class Run235SuiteHubBadgesAndTourStepTests
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

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string Method(string code, string name)
    {
        var m = Regex.Match(code, @"(?:void|Task) " + Regex.Escape(name) + @"\(");
        Assert.True(m.Success, $"{name} not found");
        var open = code.IndexOf('{', m.Index);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code.Substring(m.Index, i - m.Index + 1);
        }
        throw new InvalidOperationException($"{name} has no closing brace");
    }

    [Theory]
    [InlineData("VersionBadge", "OnVersionInfoClick", "OnVersionInfoRightTapped")]
    [InlineData("LicenseBadge", "OnVersionInfoClick", "OnVersionInfoRightTapped")]
    [InlineData("HealthStatusBadge", "OnHealthStatusClick", "OnHealthStatusRightTapped")]
    [InlineData("UptimeStoragePill", "OnUptimeStoragePillClick", "OnUptimeStoragePillRightTapped")]
    [InlineData("MemoryUsagePill", "OnMemoryUsagePillClick", "OnMemoryUsagePillRightTapped")]
    public void Suite_hub_readouts_are_buttons_the_keyboard_reaches(string name, string click, string menu)
    {
        var element = Named(Xaml("Views", "SuiteHubView.xaml"), name);
        Assert.Equal("Button", element.Name.LocalName);
        Assert.Equal(click, (string?)element.Attribute("Click"));
        Assert.Equal(menu, (string?)element.Attribute("RightTapped"));
        Assert.Null(element.Attribute("Tapped"));
        Assert.False(string.IsNullOrWhiteSpace((string?)element.Attribute(XName.Get("AutomationProperties.Name"))));
        Assert.Contains("Shift+F10", (string?)element.Attribute(XName.Get("ToolTipService.ToolTip")));

        var code = Read("Views", "SuiteHubView.xaml.cs");
        Assert.Contains($"({name}, {menu})", code);
        Assert.Contains($"void {click}(object sender, RoutedEventArgs e)", code);
        Assert.DoesNotContain(click.Replace("Click", "Tapped") + "(object sender, TappedRoutedEventArgs e)", code);
    }

    [Fact]
    public void Header_badges_wrap_instead_of_squeezing_the_last_one()
    {
        // Live UIA had the health badge 1px wide beside the header's buttons.
        var doc = Xaml("Views", "SuiteHubView.xaml");
        var row = Named(doc, "VersionBadge").Parent!;
        Assert.Equal("WrapRow", row.Name.LocalName);
        Assert.Equal("using:MarkSmith.Controls", row.Name.NamespaceName);
        var names = row.Elements().Select(e => (string?)e.Attribute(X + "Name")).Where(n => n is not null).ToArray();
        Assert.Equal(new[] { "VersionBadge", "LicenseBadge", "HealthStatusBadge" }, names);
    }

    [Fact]
    public void Ctrl_C_in_the_header_card_still_copies_the_version()
    {
        var code = Read("Views", "SuiteHubView.xaml.cs");
        Assert.Contains("OnVersionInfoClick(this, new RoutedEventArgs());", code);
        Assert.DoesNotContain("new TappedRoutedEventArgs()", code);
    }

    [Fact]
    public void Version_and_plan_badges_say_what_they_copy()
    {
        var code = Read("Views", "SuiteHubView.xaml.cs");
        Assert.Contains("AutomationProperties.SetName(VersionBadge, $\"Copy version details ({VersionText.Text})\");", code);
        Assert.Contains("AutomationProperties.SetName(LicenseBadge, $\"Copy plan details ({LicenseText.Text})\");", code);
    }

    [Fact]
    public void Cache_pill_only_touches_MarkSmith_temp_files()
    {
        var code = Read("Views", "SuiteHubView.xaml.cs");
        // ms-* matched other programs' temp files, and a click deleted the ones older than ten minutes.
        Assert.DoesNotContain("\"ms-*\"", code);
        var helper = Regex.Match(code, @"EnumerateOwnTempFiles\(DirectoryInfo tempDir\) =>\s*tempDir\.EnumerateFiles\(""marksmith\*"", SearchOption\.TopDirectoryOnly\);");
        Assert.True(helper.Success);
        Assert.Equal(3, Regex.Matches(code, @"var files = EnumerateOwnTempFiles\(tempDir\);").Count);

        var prune = Method(code, "OnUptimeStoragePillClick");
        Assert.Contains("EnumerateOwnTempFiles(tempDir)", prune);
        Assert.Contains("TimeSpan.FromMinutes(10)", prune);
    }

    [Fact]
    public void Recent_documents_menu_lists_only_shortcuts_that_run_it()
    {
        var xaml = Read("Views", "SuiteHubView.xaml");
        // Ctrl+O in Suite Hub opens the settings folder (OnOpenConfigFolderClick), not the file picker.
        Assert.DoesNotContain("Browse for Markdown file… (Ctrl+O)", xaml);
        Assert.Contains("<MenuFlyoutItem Text=\"Browse for Markdown file…\" Click=\"OnBrowseFileToOpenClick\">", xaml);
        var code = Read("Views", "SuiteHubView.xaml.cs");
        Assert.Matches(@"if \(e\.Key == VirtualKey\.O\)\s*\{\s*OnOpenConfigFolderClick\(", code);

        // A click already opens that menu, so its tooltip no longer sends people to right-click.
        var actions = Named(Xaml("Views", "SuiteHubView.xaml"), "RecentDocsActionBtn");
        Assert.DoesNotContain("Right-click", (string?)actions.Attribute(XName.Get("ToolTipService.ToolTip")));
    }

    [Fact]
    public void Tour_step_badge_is_a_button_with_its_menu_on_the_keyboard()
    {
        var badge = Named(Xaml("Views", "WelcomeTour.xaml"), "StepBadgeButton");
        Assert.Equal("Button", badge.Name.LocalName);
        Assert.Equal("OnStepBadgeClick", (string?)badge.Attribute("Click"));
        Assert.Equal("OnStepBadgeRightTapped", (string?)badge.Attribute("RightTapped"));
        Assert.Null(badge.Attribute("Tapped"));
        Assert.Contains("Shift+F10", (string?)badge.Attribute(XName.Get("ToolTipService.ToolTip")));

        var code = Read("Views", "WelcomeTour.xaml.cs");
        Assert.Contains("(StepBadgeButton, OnStepBadgeRightTapped),", code);
        Assert.Contains("OnStepBadgeClick(object sender, RoutedEventArgs e) => ShowStepBadgeMenu(StepBadgeButton, null);", code);
        Assert.Contains("ShowStepBadgeMenu(StepBadgeButton, KeyboardContextMenu.PointIn(e, StepBadgeButton));", code);
        Assert.DoesNotContain("StepBadgeBorder", code);
    }
}
