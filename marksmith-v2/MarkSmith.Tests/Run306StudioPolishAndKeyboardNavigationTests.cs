using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run306StudioPolishAndKeyboardNavigationTests
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
    public void ShapeStudio_CanvasUndoRedoCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""CanvasUndoRedoCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CanvasUndoRedoCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Undo and redo canvas actions""", match.Value);
    }

    [Fact]
    public void ShapeStudio_CanvasAlignDistributeCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""CanvasAlignDistributeCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CanvasAlignDistributeCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Align and distribute shapes""", match.Value);
    }

    [Fact]
    public void ShapeStudio_CanvasZOrderArrangeCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""CanvasZOrderArrangeCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CanvasZOrderArrangeCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Arrange and rotate shapes""", match.Value);
    }

    [Fact]
    public void ShapeStudio_CanvasItemActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""CanvasItemActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CanvasItemActionsCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Duplicate and delete shapes""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_OutlineAddItemsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""OutlineAddItemsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OutlineAddItemsCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Add outline items""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_OutlineItemOperationsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""OutlineItemOperationsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OutlineItemOperationsCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Outline item operations""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_OutlineHierarchyCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""OutlineHierarchyCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OutlineHierarchyCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Outline item hierarchy and level""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_OutlineRowActionsCluster_HasNameAndXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel[^>]*x:Name=""OutlineRowActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OutlineRowActionsCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Outline row actions""", match.Value);
    }

    [Fact]
    public void MermaidStudio_MermaidUndoRedoCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""MermaidUndoRedoCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "MermaidUndoRedoCluster declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Undo and redo diagram changes""", match.Value);
    }

    [Fact]
    public void MermaidStudio_DiagramLayoutRoutingCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""DiagramLayoutRoutingCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "DiagramLayoutRoutingCluster declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Diagram layout direction and connector routing""", match.Value);
    }

    [Fact]
    public void MermaidStudio_SyncToMarkdownButton_HasAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Button\s+x:Name=""SyncToMarkdownButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "SyncToMarkdownButton declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Sync to Markdown""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_SearchStepperButtons_HaveAccessibleNames()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var prevMatch = Regex.Match(content, @"<Button\s+x:Name=""SearchPrevMatchButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(prevMatch.Success, "SearchPrevMatchButton declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Previous match""", prevMatch.Value);

        var nextMatch = Regex.Match(content, @"<Button\s+x:Name=""SearchNextMatchButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(nextMatch.Success, "SearchNextMatchButton declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Next match""", nextMatch.Value);

        var clearMatch = Regex.Match(content, @"<Button\s+x:Name=""SearchClearButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(clearMatch.Success, "SearchClearButton declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Clear search""", clearMatch.Value);
    }

    [Fact]
    public void MindMapGalaxy_AllTagsPill_HasAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Button\s+x:Name=""AllTagsPill""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "AllTagsPill declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""All tags""", match.Value);
    }

    [Fact]
    public void MainWindow_StatusOutputLinks_HasAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""StatusOutputLinks""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "StatusOutputLinks declaration should exist in MainWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Exported file actions""", match.Value);
    }

    [Fact]
    public void MainWindow_CursorPosButton_HasAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<HyperlinkButton\s+x:Name=""CursorPosButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CursorPosButton declaration should exist in MainWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Cursor position""", match.Value);
    }
}
