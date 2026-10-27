using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run307StudioPolishAndKeyboardNavigationTests
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

    private static string ExtractXmlBlock(string xml, string tagName, string xName)
    {
        var startPattern = $@"<{tagName}\s+[^>]*x:Name=""{xName}""[^>]*>";
        var match = Regex.Match(xml, startPattern, RegexOptions.Singleline);
        if (!match.Success) return "";
        int startIndex = match.Index;
        int depth = 0;
        int i = startIndex;
        while (i < xml.Length)
        {
            if (xml.Substring(i).StartsWith($"<{tagName}"))
            {
                depth++;
                i += tagName.Length + 1;
            }
            else if (xml.Substring(i).StartsWith($"</{tagName}>"))
            {
                depth--;
                if (depth == 0)
                {
                    int endIndex = i + tagName.Length + 3;
                    return xml.Substring(startIndex, endIndex - startIndex);
                }
                i += tagName.Length + 3;
            }
            else
            {
                i++;
            }
        }
        return "";
    }

    [Fact]
    public void MermaidStudio_DiagramCanvasLayoutToolsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""DiagramCanvasLayoutToolsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "DiagramCanvasLayoutToolsCluster declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Canvas layout and alignment tools""", match.Value);

        // Verify it encloses AutoLayoutButton, GridSnapToggle, DeleteSelectedButton, AlignButton
        var block = ExtractXmlBlock(content, "StackPanel", "DiagramCanvasLayoutToolsCluster");
        Assert.NotEmpty(block);
        Assert.Contains("AutoLayoutButton", block);
        Assert.Contains("GridSnapToggle", block);
        Assert.Contains("DeleteSelectedButton", block);
        Assert.Contains("AlignButton", block);
    }

    [Fact]
    public void MermaidCanvas_QuickAddHoverContainer_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidCanvasControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""QuickAddHoverContainer""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "QuickAddHoverContainer declaration should exist in MermaidCanvasControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Quick-add connected shapes""", match.Value);
    }

    [Fact]
    public void MermaidCanvas_InlineEditActionBar_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidCanvasControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""InlineEditActionBar""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "InlineEditActionBar declaration should exist in MermaidCanvasControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Inline edit actions""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_GalaxyNodeEditingCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""GalaxyNodeEditingCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GalaxyNodeEditingCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Node editing tools""", match.Value);

        // Verify it encloses AddChildToolButton, AddSiblingToolButton, LinkToolButton, DeleteToolButton
        var block = ExtractXmlBlock(content, "StackPanel", "GalaxyNodeEditingCluster");
        Assert.NotEmpty(block);
        Assert.Contains("AddChildToolButton", block);
        Assert.Contains("AddSiblingToolButton", block);
        Assert.Contains("LinkToolButton", block);
        Assert.Contains("DeleteToolButton", block);
    }

    [Fact]
    public void MindMapGalaxy_GalaxyUndoRedoCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""GalaxyUndoRedoCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GalaxyUndoRedoCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Undo and redo map changes""", match.Value);

        // Verify it encloses UndoToolButton and RedoToolButton
        var block = ExtractXmlBlock(content, "StackPanel", "GalaxyUndoRedoCluster");
        Assert.NotEmpty(block);
        Assert.Contains("UndoToolButton", block);
        Assert.Contains("RedoToolButton", block);
    }

    [Fact]
    public void ShapeStudio_MultiSelectionBadge_HasAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Border\s+x:Name=""MultiSelectionBadge""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "MultiSelectionBadge declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Multi-selection summary badge""", match.Value);
    }

    [Fact]
    public void ShapeStudio_CurrentLayerPosition_HasAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Border\s+Grid\.Column=""2""\s+Background=""\{ThemeResource CardBackgroundFillColorDefaultBrush\}""[^>]*AutomationProperties\.Name=""Current layer position""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "Current layer position border should have accessible name in ShapeDesignStudioWindow.xaml");
    }

    [Fact]
    public void ShapeStudio_MultiAlignCluster_HasXYFocusAndSingleTabStop()
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
    public void SmartArtStudio_PreviewZoomControlCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""PreviewZoomControlCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PreviewZoomControlCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Preview zoom controls""", match.Value);
    }
}
