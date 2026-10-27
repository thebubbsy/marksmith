using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run304StudioPolishAndKeyboardNavigationTests
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
    public void ShapeStudio_ShapePositionCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""ShapePositionCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ShapePositionCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Shape position""", match.Value);
    }

    [Fact]
    public void ShapeStudio_ShapeDimensionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""ShapeDimensionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ShapeDimensionsCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Shape dimensions""", match.Value);
    }

    [Fact]
    public void ShapeStudio_ShapeFillCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""ShapeFillCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ShapeFillCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Shape fill color""", match.Value);
    }

    [Fact]
    public void ShapeStudio_ShapeStrokeWeightCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""ShapeStrokeWeightCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ShapeStrokeWeightCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Line weight""", match.Value);
    }

    [Fact]
    public void ShapeStudio_ShapeRotationCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""ShapeRotationCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ShapeRotationCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Shape rotation""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_CustomPaletteCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""CustomPaletteCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CustomPaletteCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Custom accent palette actions""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_OutlineRowActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+Grid\.Column=""2""\s+Grid\.ColumnSpan=""2""[^>]*AutomationProperties\.Name=""Outline row actions""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "Outline row actions cluster should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
    }

    [Fact]
    public void MermaidStudio_ConnectorStylePropertiesCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""ConnectorStylePropertiesCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ConnectorStylePropertiesCluster declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Connector line and arrow head styles""", match.Value);
    }

    [Fact]
    public void SettingsView_PdfHeaderFooterInputsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""PdfHeaderFooterInputsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PdfHeaderFooterInputsCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""PDF header and footer templates""", match.Value);
    }

    [Fact]
    public void SettingsView_PdfPasswordsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""PdfPasswordsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PdfPasswordsCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""PDF passwords""", match.Value);
    }

    [Fact]
    public void SettingsView_WebDavCredentialsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""WebDavCredentialsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "WebDavCredentialsCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""WebDAV credentials""", match.Value);
    }

    [Fact]
    public void MainWindow_FindCloseButton_HasElementIdAndAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Button\s+x:Name=""FindCloseButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "FindCloseButton declaration should exist in MainWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Close find""", match.Value);
    }

    [Fact]
    public void MindMap_OpenSelectedDocButton_HasElementIdAndAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Button\s+x:Name=""OpenSelectedDocButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OpenSelectedDocButton declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Open in editor""", match.Value);
    }

    [Fact]
    public void MindMap_OpenVersionHistoryButton_HasElementIdAndAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Button\s+x:Name=""OpenVersionHistoryButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OpenVersionHistoryButton declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Version history""", match.Value);
    }
}
