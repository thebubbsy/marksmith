using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run308StudioPolishAndKeyboardNavigationTests
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
        var openRegex = new Regex($@"^<{tagName}[\s/>]");
        while (i < xml.Length)
        {
            if (openRegex.IsMatch(xml.Substring(i)))
            {
                depth++;
                i += tagName.Length + 1;
            }
            else if (xml.Substring(i).StartsWith($"</{tagName}>") || Regex.IsMatch(xml.Substring(i), $@"^</{tagName}\s*>"))
            {
                depth--;
                if (depth == 0)
                {
                    int endIndex = xml.IndexOf('>', i) + 1;
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
    public void SmartArtStudio_OutlineEditorHeaderRow_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ui:WrapRow\s+x:Name=""OutlineEditorHeaderRow""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OutlineEditorHeaderRow declaration should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Outline editor header controls""", match.Value);

        var block = ExtractXmlBlock(content, "ui:WrapRow", "OutlineEditorHeaderRow");
        Assert.NotEmpty(block);
        Assert.Contains("EditorTabs", block);
        Assert.Contains("OutlineMetricsCluster", block);
        Assert.Contains("OutlineSearchPill", block);
    }

    [Fact]
    public void SmartArtStudio_ThumbnailBorder_HasAccessibilityViewRaw()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Border[^>]*Background=""\{ThemeResource SmartArtThumbTileBrush\}""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "SmartArtThumbTileBrush border should exist in SmartArtDesignStudioWindow.xaml");
        Assert.Contains(@"AutomationProperties.AccessibilityView=""Raw""", match.Value);
    }

    [Fact]
    public void ShapeStudio_PresetsPanel_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""PresetsPanel""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PresetsPanel declaration should exist in ShapeDesignStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Diagram presets panel""", match.Value);

        var block = ExtractXmlBlock(content, "Grid", "PresetsPanel");
        Assert.NotEmpty(block);
        Assert.Contains("PresetCategoryCombo", block);
        Assert.Contains("PresetList", block);
    }

    [Fact]
    public void ShapeStudio_PresetListThumbnailBorder_HasAccessibilityViewRaw()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ListView\s+[^>]*x:Name=""PresetList""[^>]*>.*?<DataTemplate>.*?<Border[^>]*Background=""\{ThemeResource SubtleFillColorSecondaryBrush\}""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PresetList template should have thumbnail border");
        Assert.Contains(@"AutomationProperties.AccessibilityView=""Raw""", match.Value);
    }

    [Fact]
    public void ShapeStudio_ShowShapeContextMenu_HasLayerOrderSubItemParity()
    {
        var csPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs");
        Assert.True(File.Exists(csPath), $"File exists: {csPath}");
        var content = File.ReadAllText(csPath);

        Assert.Contains(@"Text = ""Layer order""", content);
        Assert.Contains(@"layerSub.Items.Add(ShapeMenuItem(""Bring to front"", ""\uE74A"", () => ViewModel.BringToFrontCommand.Execute(null), ""Ctrl+Shift+]""));", content);
        Assert.Contains(@"layerSub.Items.Add(ShapeMenuItem(""Bring forward"", ""\uE74A"", () => ViewModel.BringForwardCommand.Execute(null), ""Ctrl+]""));", content);
        Assert.Contains(@"layerSub.Items.Add(ShapeMenuItem(""Send backward"", ""\uE74B"", () => ViewModel.SendBackwardCommand.Execute(null), ""Ctrl+[""));", content);
        Assert.Contains(@"layerSub.Items.Add(ShapeMenuItem(""Send to back"", ""\uE74B"", () => ViewModel.SendToBackCommand.Execute(null), ""Ctrl+Shift+[""));", content);
    }

    [Fact]
    public void MermaidStudio_NodePaletteControl_SearchCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "NodePaletteControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""PaletteSearchCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PaletteSearchCluster declaration should exist in NodePaletteControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Search shape palette""", match.Value);

        var block = ExtractXmlBlock(content, "StackPanel", "PaletteSearchCluster");
        Assert.NotEmpty(block);
        Assert.Contains("SearchTextBox", block);
    }

    [Fact]
    public void MermaidStudio_NodePaletteControl_PaletteListView_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "NodePaletteControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<ListView\s+x:Name=""PaletteListView""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PaletteListView declaration should exist in NodePaletteControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Shape palette""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_GalaxyLayoutToolsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""GalaxyLayoutToolsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GalaxyLayoutToolsCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Layout and connection styling tools""", match.Value);

        var block = ExtractXmlBlock(content, "StackPanel", "GalaxyLayoutToolsCluster");
        Assert.NotEmpty(block);
        Assert.Contains("ArrangeDropDownButton", block);
        Assert.Contains("ConnectionStyleDropDownButton", block);
    }

    [Fact]
    public void MindMapGalaxy_GalaxyIOCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""GalaxyIOCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GalaxyIOCluster declaration should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Import, export and save tools""", match.Value);

        var block = ExtractXmlBlock(content, "StackPanel", "GalaxyIOCluster");
        Assert.NotEmpty(block);
        Assert.Contains("ImportFolderButton", block);
        Assert.Contains("ExportDropDownButton", block);
        Assert.Contains("SaveMapButton", block);
    }

    [Fact]
    public void SmartArtStudio_LayoutSearchFilterCluster_MaintainsSingleTabStop()
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
}
