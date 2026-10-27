using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run305StudioPolishAndKeyboardNavigationTests
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
    public void MindMapGalaxy_LinkedFileCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""LinkedFileCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LinkedFileCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Linked file""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_LinkedFileBox_HasNameAndAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<TextBox\s+x:Name=""LinkedFileBox""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LinkedFileBox declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Linked file""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_NodeTypeCombo_HasNameAndAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ComboBox\s+x:Name=""NodeTypeCombo""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "NodeTypeCombo declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Node type""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_NodeIconCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""NodeIconCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "NodeIconCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Node icon""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_NodePaletteCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""NodePaletteCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "NodePaletteCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Node color palette""", match.Value);
    }

    [Fact]
    public void ShapeStudio_ShapeTypeCombo_HasNameAndAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ComboBox\s+x:Name=""ShapeTypeCombo""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ShapeTypeCombo declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Shape type""", match.Value);
    }

    [Fact]
    public void ShapeStudio_QuickMorphCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""QuickMorphCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "QuickMorphCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Quick morph ribbon""", match.Value);
    }

    [Fact]
    public void ShapeStudio_TransformRibbonCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""TransformRibbonCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "TransformRibbonCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Transform and aspect ratio ribbon""", match.Value);
    }

    [Fact]
    public void MermaidStudio_MermaidStatusBarCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""MermaidStatusBarCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "MermaidStatusBarCluster declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Mermaid status bar""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_OutlineSearchBoxCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""OutlineSearchBoxCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OutlineSearchBoxCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Outline find and match stepper""", match.Value);
    }

    [Fact]
    public void Settings_GoogleClientCredentialsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""GoogleClientCredentialsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GoogleClientCredentialsCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Google OAuth credentials""", match.Value);
    }

    [Fact]
    public void Settings_LicenseKeyEntryCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""LicenseKeyEntryCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LicenseKeyEntryCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""License key entry""", match.Value);
    }

    [Fact]
    public void Settings_CloudProviderCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""CloudProviderCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CloudProviderCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Cloud storage provider""", match.Value);
    }

    [Fact]
    public void History_TimelineSearchCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "History", "HistoryWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""TimelineSearchCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "TimelineSearchCluster declaration should exist in HistoryWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Timeline search and match""", match.Value);
    }
}
