using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run296StudioPolishAndKeyboardNavigationTests
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
    public void ShapeDesignStudio_MorphShapesCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""MorphShapesCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "MorphShapesCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Quick morph shapes""", match.Value);
    }

    [Fact]
    public void ShapeDesignStudio_LayerActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""LayerActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LayerActionsCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Layer ordering actions""", match.Value);
    }

    [Fact]
    public void ShapeDesignStudio_TransformActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""TransformActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "TransformActionsCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Transform and flip actions""", match.Value);
    }

    [Fact]
    public void ShapeDesignStudio_MultiAlignCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""MultiAlignCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "MultiAlignCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Multi-selection alignment ribbon""", match.Value);
    }

    [Fact]
    public void MermaidDiagramStudio_DiagramStylePresetsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""DiagramStylePresetsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "DiagramStylePresetsCluster declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Style presets""", match.Value);
    }

    [Fact]
    public void SmartArtDesignStudio_OutlineToolbar_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ui:WrapRow\s+x:Name=""OutlineToolbar""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OutlineToolbar declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Outline actions toolbar""", match.Value);
    }

    [Fact]
    public void SmartArtDesignStudio_OutlineMatchStepperCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""OutlineMatchStepperCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OutlineMatchStepperCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Search match navigation""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_LinkActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""LinkActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LinkActionsCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Link actions""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_GalaxyStatusCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""GalaxyStatusCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GalaxyStatusCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Galaxy status counts""", match.Value);
    }

    [Fact]
    public void MainWindow_EditingClustersPanel_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""EditingClustersPanel""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "EditingClustersPanel declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Editing tool clusters""", match.Value);
    }
}
