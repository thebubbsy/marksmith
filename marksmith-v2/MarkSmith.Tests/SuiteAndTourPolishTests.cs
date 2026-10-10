using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Run #75: Suite Hub SmartArt Studio integration, Welcome Tour focus-preserving card navigation,
/// SettingsCard tap-to-toggle and accessible name inheritance, and CommandPalette live search ergonomics.
/// </summary>
public sealed class SuiteAndTourPolishTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "marksmith.sln")) && !Directory.Exists(Path.Combine(dir, "marksmith-v2")))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return dir ?? AppContext.BaseDirectory;
    }

    private static string DesktopDir() => Path.Combine(RepoRoot(), "marksmith-v2", "MarkSmith.Desktop");

    [Fact]
    public void SuiteHub_Card1_includes_all_three_drawing_studios()
    {
        var xaml = File.ReadAllText(Path.Combine(DesktopDir(), "Views", "SuiteHubView.xaml"));

        // Diagram Studio, Shape Studio, and SmartArt Studio
        Assert.Contains("x:Name=\"Card1DiagramButton\"", xaml);
        Assert.Contains("x:Name=\"Card1ShapeButton\"", xaml);
        Assert.Contains("x:Name=\"Card1SmartArtButton\"", xaml);

        Assert.Contains("Click=\"OnOpenMermaidStudioClick\"", xaml);
        Assert.Contains("Click=\"OnOpenShapeStudioClick\"", xaml);
        Assert.Contains("Click=\"OnOpenSmartArtStudioClick\"", xaml);

        Assert.Contains("AutomationProperties.Name=\"Open SmartArt Studio\"", xaml);
    }

    [Fact]
    public void SuiteHub_declares_and_mainWindow_wires_smartArt_studio_requested_event()
    {
        var hubCode = File.ReadAllText(Path.Combine(DesktopDir(), "Views", "SuiteHubView.xaml.cs"));
        var mainCode = File.ReadAllText(Path.Combine(DesktopDir(), "MainWindow.xaml.cs"));

        Assert.Contains("public event Action? OpenSmartArtStudioRequested;", hubCode);
        Assert.Contains("private void OnOpenSmartArtStudioClick(object sender, RoutedEventArgs e)", hubCode);
        Assert.Contains("OpenSmartArtStudioRequested?.Invoke();", hubCode);

        Assert.Contains("suiteHubView.OpenSmartArtStudioRequested +=", mainCode);
        Assert.Contains("OnOpenSmartArtDesignStudioClick", mainCode);
    }

    [Fact]
    public void SuiteHub_keyboard_accelerators_cover_all_studios()
    {
        var hubCode = File.ReadAllText(Path.Combine(DesktopDir(), "Views", "SuiteHubView.xaml.cs"));

        // Ctrl+Shift+M for Mermaid, Ctrl+Shift+S for Shape, Ctrl+Shift+A for SmartArt
        Assert.Contains("e.Key == VirtualKey.M", hubCode);
        Assert.Contains("OpenMermaidStudioRequested?.Invoke()", hubCode);
        Assert.Contains("e.Key == VirtualKey.S", hubCode);
        Assert.Contains("OpenShapeStudioRequested?.Invoke()", hubCode);
        Assert.Contains("e.Key == VirtualKey.A", hubCode);
        Assert.Contains("OpenSmartArtStudioRequested?.Invoke()", hubCode);
    }

    [Fact]
    public void WelcomeTour_OnTourKeyDown_preserves_card_navigation_on_multi_card_pages()
    {
        var tourCode = File.ReadAllText(Path.Combine(DesktopDir(), "Views", "WelcomeTour.xaml.cs"));

        // Page 6 SampleCard <-> BlankCard arrow navigation
        Assert.Contains("IsInside(focused, BlankCard)", tourCode);
        Assert.Contains("SampleCard.Focus", tourCode);
        Assert.Contains("IsInside(focused, SampleCard)", tourCode);
        Assert.Contains("BlankCard.Focus", tourCode);

        // Page 0 StepCard arrow navigation
        Assert.Contains("FindStepButton(step + 1)", tourCode);
        Assert.Contains("FindStepButton(step - 1)", tourCode);

        // PageDown & PageUp paging
        Assert.Contains("e.Key == Windows.System.VirtualKey.PageDown", tourCode);
        Assert.Contains("e.Key == Windows.System.VirtualKey.PageUp", tourCode);
    }

    [Fact]
    public void SettingsCard_implements_tap_to_toggle_and_name_inheritance()
    {
        var cardCode = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "SettingsCard.xaml.cs"));

        Assert.Contains("Tapped += OnCardTapped;", cardCode);
        Assert.Contains("private void OnCardTapped(object sender, TappedRoutedEventArgs e)", cardCode);
        Assert.Contains("toggle.IsOn = !toggle.IsOn;", cardCode);
        Assert.Contains("check.IsChecked = !(check.IsChecked ?? false);", cardCode);
        Assert.Contains("IsInside(source, ActionPart)", cardCode);
        Assert.Contains("IsInside(source, DetailsPart)", cardCode);

        Assert.Contains("NameUnnamed(Action as DependencyObject);", cardCode);
        Assert.Contains("NameUnnamed(Details as DependencyObject);", cardCode);
    }

    [Fact]
    public void CommandPalette_implements_query_clearing_and_boundary_jumping()
    {
        var paletteCode = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "CommandPalette.xaml.cs"));

        // Escape clears query if non-empty, otherwise closes
        Assert.Contains("if (SearchBox.Text.Length > 0)", paletteCode);
        Assert.Contains("SearchBox.Text = \"\";", paletteCode);

        // Ctrl+Home and Ctrl+End jump to boundaries
        Assert.Contains("case VirtualKey.Home when ctrl:", paletteCode);
        Assert.Contains("Move(-9999);", paletteCode);
        Assert.Contains("case VirtualKey.End when ctrl:", paletteCode);
        Assert.Contains("Move(9999);", paletteCode);

        // Live region updates
        Assert.Contains("AutomationProperties.SetName(NoMatches, NoMatchesTitle.Text);", paletteCode);
        Assert.Contains("AutomationProperties.SetLiveSetting(NoMatches, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);", paletteCode);
        Assert.Contains("AutomationProperties.SetName(CountText, CountText.Text);", paletteCode);
    }
}
