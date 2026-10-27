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
    public void OcrInspectionStudio_TelemetryCluster_HasAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Ocr", "OcrInspectionStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""OcrStudioTelemetryCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OcrStudioTelemetryCluster declaration should exist in OcrInspectionStudioWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""OCR studio telemetry counters""", match.Value);
    }
}
