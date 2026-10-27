using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run302StudioPolishAndKeyboardNavigationTests
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
    public void ShapeStudio_CanvasTopToolbarRibbon_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ui:WrapRow\s+x:Name=""CanvasTopToolbarRibbon""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "CanvasTopToolbarRibbon declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Canvas top toolbar ribbon""", match.Value);
    }

    [Fact]
    public void ShapeStudio_ShapesList_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ListView\s+[^>]*x:Name=""ShapesList""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ShapesList declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Shapes on canvas""", match.Value);
    }

    [Fact]
    public void ShapeStudio_PaletteList_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ListView\s+x:Name=""PaletteList""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PaletteList declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Colour schemes""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_LayoutSearchFilterCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""LayoutSearchFilterCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LayoutSearchFilterCluster declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Layout search and filter controls""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_LayoutList_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ListView\s+[^>]*x:Name=""LayoutList""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LayoutList declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Layouts""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_NoMatchesPanel_HasAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""NoMatchesPanel""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "NoMatchesPanel declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""No matching layouts notification""", match.Value);
    }

    [Fact]
    public void SmartArtStudio_PaletteList_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ListView\s+x:Name=""PaletteList""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PaletteList declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""SmartArt colour palettes""", match.Value);
    }

    [Fact]
    public void HistoryWindow_DiffInspectorToolbarRow_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "History", "HistoryWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ui:WrapRow\s+x:Name=""DiffInspectorToolbarRow""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "DiffInspectorToolbarRow declaration should exist in HistoryWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Diff inspector toolbar""", match.Value);
    }

    [Fact]
    public void OcrInspectionStudio_MarkdownPaneHeader_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Ocr", "OcrInspectionStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ui:WrapRow\s+x:Name=""MarkdownPaneHeader""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "MarkdownPaneHeader declaration should exist in OcrInspectionStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Markdown pane header""", match.Value);
    }

    [Fact]
    public void OcrInspectionStudio_SessionMetricsButton_HasAccessibleName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Ocr", "OcrInspectionStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Button\s+x:Name=""SessionMetricsButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "SessionMetricsButton declaration should exist in OcrInspectionStudioWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Session metrics and status summary""", match.Value);
    }

    [Fact]
    public void MermaidStudio_ConnectorEndpointsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""ConnectorEndpointsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ConnectorEndpointsCluster declaration should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Connector endpoint nodes""", match.Value);
    }

    [Fact]
    public void MainWindow_StatusLeftPanel_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""StatusLeftPanel""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "StatusLeftPanel declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Status left panel""", match.Value);
    }
}
