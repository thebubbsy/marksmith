using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run301StudioPolishAndKeyboardNavigationTests
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
    public void MindMapGalaxy_MapTitleCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""MapTitleCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "MapTitleCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Map title and state""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_GalaxyTopToolsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""GalaxyTopToolsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GalaxyTopToolsCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Galaxy search and zoom tools""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_GalaxyShortcutsButton_DeclaredWithProperties()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<HyperlinkButton\s+x:Name=""GalaxyShortcutsButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GalaxyShortcutsButton declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"Click=""OnGalaxyShortcutsButtonClick""", match.Value);
        Assert.Contains(@"RightTapped=""OnGalaxyShortcutsButtonRightTapped""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Document Galaxy keyboard shortcuts""", match.Value);
        Assert.Contains("Shortcuts · F1", content);
    }

    [Fact]
    public void MindMapGalaxy_ShortcutsFlyoutAndKeyHandlers_Wired()
    {
        var csPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml.cs");
        Assert.True(File.Exists(csPath), $"File exists: {csPath}");
        var content = File.ReadAllText(csPath);

        Assert.Contains("(GalaxyShortcutsButton, OnGalaxyShortcutsButtonRightTapped)", content);
        Assert.Contains("case Windows.System.VirtualKey.F1:", content);
        Assert.Contains("OnGalaxyShortcutsButtonClick(this, new RoutedEventArgs())", content);
        Assert.Contains("void OnGalaxyShortcutsButtonRightTapped(object sender, RightTappedRoutedEventArgs e)", content);
        Assert.Contains("Copy Galaxy shortcuts cheatsheet (Markdown)", content);
    }

    [Fact]
    public void MindMapGalaxy_EmptyMapActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""EmptyMapActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "EmptyMapActionsCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Empty map actions""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_TourBannerActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""TourBannerActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "TourBannerActionsCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Tour banner actions""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_SelectedNodeActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""SelectedNodeActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "SelectedNodeActionsCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Selected node document actions""", match.Value);
    }

    [Fact]
    public void ShapeStudio_ToolStrip_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ui:WrapRow\s+x:Name=""ToolStrip""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ToolStrip WrapRow declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Canvas tools""", match.Value);
    }

    [Fact]
    public void ShapeStudio_CanvasHelpersCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""CanvasHelpersCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CanvasHelpersCluster declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Canvas helpers and geometry""", match.Value);
    }

    [Fact]
    public void MainWindow_EditorStripStatusCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""EditorStripStatusCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "EditorStripStatusCluster declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Editor strip status""", match.Value);
    }

    [Fact]
    public void MainWindow_BottomCommandStripCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""BottomCommandStripCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "BottomCommandStripCluster declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Bottom command strip""", match.Value);
    }

    [Fact]
    public void MainWindow_EditingExpandedPanel_HasXYFocusAndAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""EditingExpandedPanel""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "EditingExpandedPanel declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Expanded editing tools""", match.Value);
    }

    [Fact]
    public void MermaidStudio_NodeColorPickersCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""NodeColorPickersCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "NodeColorPickersCluster declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Node color pickers""", match.Value);
    }

    [Fact]
    public void MermaidStudio_NodeFillAndBorderButtons_Declared()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var fillMatch = Regex.Match(content, @"<Button\s+x:Name=""NodeFillColorButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(fillMatch.Success, "NodeFillColorButton declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Node fill color""", fillMatch.Value);

        var borderMatch = Regex.Match(content, @"<Button\s+x:Name=""NodeBorderColorButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(borderMatch.Success, "NodeBorderColorButton declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Node border color""", borderMatch.Value);
    }

    [Fact]
    public void MermaidCanvas_ZoomControlCluster_HasAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidCanvasControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""ZoomControlCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ZoomControlCluster declaration should exist in MermaidCanvasControl.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Mermaid canvas zoom controls""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_OutlineAddButtons_HaveAccessibleNames()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var childMatch = Regex.Match(content, @"<Button\s+x:Name=""OutlineAddChildButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(childMatch.Success, "OutlineAddChildButton declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Add child item""", childMatch.Value);

        var siblingMatch = Regex.Match(content, @"<Button\s+x:Name=""OutlineAddSiblingButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(siblingMatch.Success, "OutlineAddSiblingButton declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Add sibling item""", siblingMatch.Value);
    }

    [Fact]
    public void HistoryWindow_TimelineSearchMatchCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "History", "HistoryWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""TimelineSearchMatchCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "TimelineSearchMatchCluster declaration should exist in HistoryWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Timeline search match""", match.Value);
    }

    [Fact]
    public void HistoryWindow_DiffSearchPill_HasAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "History", "HistoryWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Border\s+x:Name=""DiffSearchPill""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "DiffSearchPill declaration should exist in HistoryWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Find in diff controls""", match.Value);
    }

    [Fact]
    public void OcrInspectionStudio_TelemetryCluster_HasAutomationPropertiesNameAndXYFocus()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Ocr", "OcrInspectionStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""OcrStudioTelemetryCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OcrStudioTelemetryCluster declaration should exist in OcrInspectionStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""OCR studio telemetry counters""", match.Value);
    }
}
