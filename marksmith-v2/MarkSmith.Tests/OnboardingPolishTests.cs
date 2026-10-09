using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;
using Phase = MarkSmith.Services.BrowserExtensionPackage.ConnectionPhase;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Run #55: first run, done properly. The welcome tour, the tips after it, the sample document and
/// "Get the extension" were walked on a fresh config; these pin what that found.
/// </summary>
public sealed class OnboardingPolishTests
{
    // ---- The bundled browser extension ----

    [Fact]
    public void The_bundled_extension_is_found_beside_the_app()
    {
        var app = TempDir();
        var folder = Directory.CreateDirectory(Path.Combine(app, BrowserExtensionPackage.FolderName)).FullName;
        File.WriteAllText(Path.Combine(folder, "manifest.json"), """{ "manifest_version": 3, "version": "3.4.0" }""");

        Assert.Equal(folder, BrowserExtensionPackage.Locate(app));
        Assert.Equal("3.4.0", BrowserExtensionPackage.Version(folder));
    }

    [Fact]
    public void A_folder_without_a_manifest_is_not_an_extension()
    {
        var app = TempDir();
        Directory.CreateDirectory(Path.Combine(app, BrowserExtensionPackage.FolderName));
        Assert.Null(BrowserExtensionPackage.Locate(app));
        Assert.Null(BrowserExtensionPackage.Locate(""));
    }

    [Fact]
    public void A_broken_manifest_has_no_version_rather_than_throwing()
    {
        var folder = TempDir();
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{ not json");
        Assert.Null(BrowserExtensionPackage.Version(folder));
        Assert.Null(BrowserExtensionPackage.Version(Path.Combine(folder, "missing")));
    }

    [Fact]
    public void The_build_ships_the_repo_extension_without_its_tests()
    {
        var csproj = File.ReadAllText(Path.Combine(DesktopDir(), "MarkSmith.Desktop.csproj"));
        Assert.Contains($"Link=\"{BrowserExtensionPackage.FolderName}\\%(RecursiveDir)", csproj);
        Assert.Contains(@"..\..\extension\tests\**", csproj);
        // The folder the item points at is the real extension.
        Assert.True(File.Exists(Path.Combine(DesktopDir(), "..", "..", "extension", "manifest.json")));
    }

    [Fact]
    public void Get_the_extension_opens_the_setup_guide_not_a_source_folder()
    {
        // Every entry point used to open github.com/.../tree/main/extension, which an installed
        // copy of MarkSmith can't load. Only the guide's "full guide" link may still go there.
        foreach (var file in new[] { "Controls/ExtensionTip.xaml.cs", "Controls/ExtensionHintBar.xaml.cs", "Views/SuiteHubView.xaml.cs" })
        {
            var code = File.ReadAllText(Path.Combine(DesktopDir(), file));
            Assert.DoesNotContain("tree/main/extension", code);
        }
        var main = File.ReadAllText(Path.Combine(DesktopDir(), "MainWindow.xaml"));
        Assert.Equal(2, Regex.Matches(main, "GetExtensionRequested=\"OnGetExtensionRequested\"").Count);
    }

    // ---- The guide's connection status ----

    [Theory]
    [InlineData(false, false, false, Phase.ApiOff)]
    [InlineData(true, false, false, Phase.PortBusy)]
    [InlineData(true, false, true, Phase.PortBusy)]
    [InlineData(true, true, false, Phase.Waiting)]
    [InlineData(true, true, true, Phase.Connected)]
    public void The_status_follows_the_setting_the_server_and_the_extension(bool enabled, bool running, bool connected, Phase expected) =>
        Assert.Equal(expected, BrowserExtensionPackage.Phase(enabled, running, connected));

    [Fact]
    public void A_busy_port_says_which_port_and_where_to_change_it()
    {
        var text = BrowserExtensionPackage.Describe(Phase.PortBusy, 48000);
        Assert.Contains("48000", text);
        Assert.Contains("Settings ▸ Automation", text);
        Assert.Contains("47821", BrowserExtensionPackage.Describe(Phase.Waiting, 47821));
    }

    // ---- Tour copy ----

    [Fact]
    public void Pro_plan_copy_names_every_pro_feature()
    {
        foreach (var words in new[] { "Word", "PowerPoint", "batch conversion", "folder watching", "clipboard automation", "auto-export" })
            Assert.Contains(words, ProGate.ProPlanAdds);
        Assert.DoesNotContain("branding", ProGate.ProPlanAdds, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_tour_takes_its_plan_copy_from_ProGate()
    {
        var xaml = TourXaml();
        Assert.Contains("x:Name=\"PlanText\"", xaml);
        Assert.DoesNotContain("automation and branding", xaml);
        Assert.DoesNotContain("cross-platform", xaml);
        Assert.Contains("ProGate.ProPlanAdds", File.ReadAllText(Path.Combine(DesktopDir(), "Views", "WelcomeTour.xaml.cs")));
    }

    [Fact]
    public void The_tour_ends_in_two_choices_not_a_checkbox()
    {
        var xaml = TourXaml();
        Assert.DoesNotContain("LoadSampleCheck", xaml);
        Assert.Contains("x:Name=\"SampleCard\"", xaml);
        Assert.Contains("x:Name=\"BlankCard\"", xaml);
    }

    [Fact]
    public void Tour_paths_use_the_apps_separator()
    {
        Assert.DoesNotMatch(new Regex("Settings →"), TourXaml());
    }

    [Fact]
    public void Teaching_tips_carry_no_emoji()
    {
        // TeachingTip draws an emoji in its subtitle as a blurred blob.
        var code = File.ReadAllText(Path.Combine(DesktopDir(), "MainWindow.xaml.cs"));
        foreach (Match call in Regex.Matches(code, @"ShowMoreMenuTip\(\s*""(?<t>[^""]*)"",\s*""(?<s>[^""]*)""\)"))
        {
            var text = call.Groups["t"].Value + call.Groups["s"].Value;
            Assert.DoesNotContain(text, c => c == '☕' || char.IsSurrogate(c));
        }
        Assert.Equal(2, Regex.Matches(code, @"ShowMoreMenuTip\(\s*""").Count);
    }

    private static string TourXaml() => File.ReadAllText(Path.Combine(DesktopDir(), "Views", "WelcomeTour.xaml"));

    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ms-onboarding-" + Guid.NewGuid().ToString("N"))).FullName;

    private static string DesktopDir([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "MarkSmith.Desktop");
}
