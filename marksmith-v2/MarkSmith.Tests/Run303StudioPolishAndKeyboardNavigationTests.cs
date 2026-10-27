using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run303StudioPolishAndKeyboardNavigationTests
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
    public void ShapeStudio_PresetList_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ListView\s+[^>]*x:Name=""PresetList""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PresetList declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Diagram presets""", match.Value);
    }

    [Fact]
    public void ShapeStudio_ImageCardActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""ImageCardActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ImageCardActionsCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Image source and path""", match.Value);
    }

    [Fact]
    public void ShapeStudio_UseImagePathButton_HasElementIdAndAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Button\s+x:Name=""UseImagePathButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "UseImagePathButton declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Use image path""", match.Value);
    }

    [Fact]
    public void ShapeStudio_MosaicControlsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""MosaicControlsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "MosaicControlsCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Shape mosaic settings""", match.Value);
    }

    [Fact]
    public void ShapeStudio_TileShapesCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""TileShapesCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "TileShapesCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Mosaic tile shapes""", match.Value);
    }

    [Fact]
    public void ShapeStudio_LineArtControlsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""LineArtControlsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LineArtControlsCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Traced line art settings""", match.Value);
    }

    [Fact]
    public void ShapeStudio_LineArtDetailStrokeCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""LineArtDetailStrokeCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LineArtDetailStrokeCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Line art detail and stroke""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_QuickLayoutCarouselCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""QuickLayoutCarouselCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "QuickLayoutCarouselCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Quick layout carousel bar""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_QuickLayoutCarouselList_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ListView\s+x:Name=""QuickLayoutCarouselList""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "QuickLayoutCarouselList declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Quick layout carousel""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_SmartArtStatusBarCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""SmartArtStatusBarCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "SmartArtStatusBarCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""SmartArt status bar""", match.Value);
    }

    [Fact]
    public void MermaidStudio_NodePositionCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""NodePositionCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "NodePositionCluster declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Node position""", match.Value);
    }

    [Fact]
    public void MermaidStudio_NodeDimensionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""NodeDimensionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "NodeDimensionsCluster declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Node dimensions""", match.Value);
    }

    [Fact]
    public void MainWindow_ExportActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""ExportActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ExportActionsCluster declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Export document actions""", match.Value);
    }

    [Fact]
    public void HistoryWindow_DiffSearchCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "History", "HistoryWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""DiffSearchCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "DiffSearchCluster declaration should exist in HistoryWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Find in diff cluster""", match.Value);
    }
}
