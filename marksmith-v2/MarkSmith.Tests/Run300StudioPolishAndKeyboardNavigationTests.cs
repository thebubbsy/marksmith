using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run300StudioPolishAndKeyboardNavigationTests
{
    private static string GetSourceRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "marksmith-v2", "MarkSmith.Desktop");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate MarkSmith.Desktop directory.");
    }

    [Fact]
    public void SettingsView_CloudSyncActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""CloudSyncActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CloudSyncActionsCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Cloud sync actions""", match.Value);
    }

    [Fact]
    public void SettingsView_CopyPromptActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""CopyPromptActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CopyPromptActionsCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Copy prompt actions""", match.Value);
    }

    [Fact]
    public void SettingsView_HouseStyleActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""HouseStyleActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "HouseStyleActionsCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""House style actions""", match.Value);
    }

    [Fact]
    public void SettingsView_LicenseActivationCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""LicenseActivationCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LicenseActivationCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""License activation actions""", match.Value);
    }

    [Fact]
    public void OcrOverlay_TelemetryCluster_HasAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Controls", "OcrExperienceOverlay.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""OcrTelemetryCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OcrTelemetryCluster declaration should exist in OcrExperienceOverlay.xaml");
        Assert.Contains(@"AutomationProperties.Name=""OCR telemetry counters""", match.Value);
    }

    [Fact]
    public void OcrOverlay_HeaderActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Controls", "OcrExperienceOverlay.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""OcrOverlayHeaderActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OcrOverlayHeaderActionsCluster declaration should exist in OcrExperienceOverlay.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""OCR overlay actions""", match.Value);
    }

    [Fact]
    public void OcrOverlay_FinalMarkdownActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Controls", "OcrExperienceOverlay.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""FinalMarkdownActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "FinalMarkdownActionsCluster declaration should exist in OcrExperienceOverlay.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Final markdown actions""", match.Value);
    }

    [Fact]
    public void MermaidStudio_StylePresetButtons_HaveExplicitAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        Assert.Contains(@"x:Name=""PresetCatppuccin"" Tag=""Catppuccin Slate"" Click=""OnPalettePresetClick""", content);
        Assert.Contains(@"AutomationProperties.Name=""Catppuccin Slate style preset""", content);

        Assert.Contains(@"x:Name=""PresetNord"" Tag=""Nord Ocean"" Click=""OnPalettePresetClick""", content);
        Assert.Contains(@"AutomationProperties.Name=""Nord Ocean style preset""", content);

        Assert.Contains(@"x:Name=""PresetEmerald"" Tag=""Emerald Corporate"" Click=""OnPalettePresetClick""", content);
        Assert.Contains(@"AutomationProperties.Name=""Emerald Corporate style preset""", content);

        Assert.Contains(@"x:Name=""PresetMono"" Tag=""Monochrome Print"" Click=""OnPalettePresetClick""", content);
        Assert.Contains(@"AutomationProperties.Name=""Monochrome Print style preset""", content);
    }

    [Fact]
    public void MermaidStudio_CanvasSummaryButton_HasAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Button\s+x:Name=""CanvasSummaryButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CanvasSummaryButton declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Diagram canvas summary""", match.Value);
    }

    [Fact]
    public void SuiteHubView_StarterTemplates_HaveExplicitAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SuiteHubView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        Assert.Contains(@"x:Name=""TemplateMeetingNotesBtn""", content);
        Assert.Contains(@"AutomationProperties.Name=""Meeting Notes template""", content);

        Assert.Contains(@"x:Name=""TemplateTechSpecBtn""", content);
        Assert.Contains(@"AutomationProperties.Name=""Technical Spec template""", content);

        Assert.Contains(@"x:Name=""TemplateSmartArtBtn""", content);
        Assert.Contains(@"AutomationProperties.Name=""SmartArt Showcase template""", content);

        Assert.Contains(@"x:Name=""TemplateMindMapBtn""", content);
        Assert.Contains(@"AutomationProperties.Name=""MindMap Note template""", content);

        Assert.Contains(@"x:Name=""TemplateAdrBtn""", content);
        Assert.Contains(@"AutomationProperties.Name=""ADR Spec template""", content);

        Assert.Contains(@"x:Name=""TemplateReleaseNotesBtn""", content);
        Assert.Contains(@"AutomationProperties.Name=""Release Notes template""", content);

        Assert.Contains(@"x:Name=""TemplatePostmortemBtn""", content);
        Assert.Contains(@"AutomationProperties.Name=""Incident Postmortem template""", content);

        Assert.Contains(@"x:Name=""TemplateRfcBtn""", content);
        Assert.Contains(@"AutomationProperties.Name=""RFC Proposal template""", content);

        Assert.Contains(@"x:Name=""TemplatePrdBtn""", content);
        Assert.Contains(@"AutomationProperties.Name=""PRD Document template""", content);

        Assert.Contains(@"x:Name=""TemplateApiDocBtn""", content);
        Assert.Contains(@"AutomationProperties.Name=""REST API Spec template""", content);
    }
}
