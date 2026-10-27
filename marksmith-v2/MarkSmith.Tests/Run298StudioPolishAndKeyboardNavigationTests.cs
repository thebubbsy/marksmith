using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run298StudioPolishAndKeyboardNavigationTests
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
    public void MainWindow_TitleBarActions_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""TitleBarActions""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "TitleBarActions declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Title bar actions""", match.Value);
    }

    [Fact]
    public void MainWindow_CenterViewSelector_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<SelectorBar\s+x:Name=""CenterViewSelector""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CenterViewSelector declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Editor view layout""", match.Value);
    }

    [Fact]
    public void MainWindow_StatusViewInfoCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""StatusViewInfoCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "StatusViewInfoCluster declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""View and document status""", match.Value);
    }

    [Fact]
    public void SettingsView_PdfPermissionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""PdfPermissionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PdfPermissionsCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Reader permissions""", match.Value);
    }

    [Fact]
    public void SettingsView_GoogleSignInActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""GoogleSignInActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GoogleSignInActionsCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Google sign-in actions""", match.Value);
    }

    [Fact]
    public void SettingsView_GoogleAccountActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""GoogleAccountActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GoogleAccountActionsCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Google account actions""", match.Value);
    }

    [Fact]
    public void SettingsView_PluginsLinksCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""PluginsLinksCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PluginsLinksCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Plugins footer links""", match.Value);
    }

    [Fact]
    public void SettingsView_AboutCheckPanel_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""AboutCheckPanel""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "AboutCheckPanel declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""About update actions""", match.Value);
    }

    [Fact]
    public void ExtensionSetupView_BrowserPagesPanel_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ExtensionSetupView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""BrowserPagesPanel""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "BrowserPagesPanel declaration should exist in ExtensionSetupView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Browser extensions pages""", match.Value);
    }

    [Fact]
    public void ExtensionSetupView_ExtensionFolderActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ExtensionSetupView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""ExtensionFolderActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ExtensionFolderActionsCluster declaration should exist in ExtensionSetupView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Extension folder actions""", match.Value);
    }

    [Fact]
    public void HistoryWindow_DiffClusters_HaveUiaNames()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "History", "HistoryWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var matchDiffMode = Regex.Match(content, @"<StackPanel\s+x:Name=""DiffModeSegment""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchDiffMode.Success);
        Assert.Contains(@"AutomationProperties.Name=""Diff view modes""", matchDiffMode.Value);

        var matchStepper = Regex.Match(content, @"<StackPanel\s+x:Name=""DiffChangeStepperControls""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchStepper.Success);
        Assert.Contains(@"AutomationProperties.Name=""Change hunk navigation""", matchStepper.Value);

        var matchMeters = Regex.Match(content, @"<StackPanel\s+x:Name=""DiffMetersCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchMeters.Success);
        Assert.Contains(@"AutomationProperties.Name=""Diff meters""", matchMeters.Value);
    }

    [Fact]
    public void OcrInspectionStudio_Clusters_HaveUiaNames()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Ocr", "OcrInspectionStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var matchPageNav = Regex.Match(content, @"<StackPanel\s+x:Name=""PageNavigationCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchPageNav.Success);
        Assert.Contains(@"AutomationProperties.Name=""Page navigation""", matchPageNav.Value);

        var matchCta = Regex.Match(content, @"<StackPanel\s+x:Name=""CtaActionCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchCta.Success);
        Assert.Contains(@"AutomationProperties.Name=""Studio call to action buttons""", matchCta.Value);
    }

    [Fact]
    public void SmartArtDesignStudio_Clusters_HaveUiaNames()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var matchActions = Regex.Match(content, @"<StackPanel\s+x:Name=""SmartArtActionCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchActions.Success);
        Assert.Contains(@"AutomationProperties.Name=""SmartArt studio actions""", matchActions.Value);

        var matchHarmony = Regex.Match(content, @"<StackPanel\s+x:Name=""HarmonyModeSegment""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchHarmony.Success);
        Assert.Contains(@"AutomationProperties.Name=""Palette harmony modes""", matchHarmony.Value);

        var matchZoom = Regex.Match(content, @"<StackPanel\s+x:Name=""PreviewZoomControlCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchZoom.Success);
        Assert.Contains(@"AutomationProperties.Name=""Preview zoom controls""", matchZoom.Value);
    }

    [Fact]
    public void ShapeDesignStudio_Clusters_HaveUiaNames()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var matchActions = Regex.Match(content, @"<StackPanel\s+x:Name=""ShapeStudioActionCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchActions.Success);
        Assert.Contains(@"AutomationProperties.Name=""Shape studio actions""", matchActions.Value);

        var matchStatus = Regex.Match(content, @"<StackPanel\s+x:Name=""ShapeStudioStatusCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchStatus.Success);
        Assert.Contains(@"AutomationProperties.Name=""Shape studio status actions""", matchStatus.Value);
    }

    [Fact]
    public void MermaidDiagramStudio_Clusters_HaveUiaNames()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var matchActions = Regex.Match(content, @"<StackPanel\s+x:Name=""DiagramActionCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchActions.Success);
        Assert.Contains(@"AutomationProperties.Name=""Diagram studio actions""", matchActions.Value);
    }

    [Fact]
    public void MindMapGalaxy_Clusters_HaveUiaNames()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var matchZoom = Regex.Match(content, @"<StackPanel\s+x:Name=""ZoomControlCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchZoom.Success);
        Assert.Contains(@"AutomationProperties.Name=""Galaxy zoom controls""", matchZoom.Value);

        var matchTags = Regex.Match(content, @"<StackPanel\s+x:Name=""GalaxyTagPillsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(matchTags.Success);
        Assert.Contains(@"AutomationProperties.Name=""Filter tag pills""", matchTags.Value);
    }
}
