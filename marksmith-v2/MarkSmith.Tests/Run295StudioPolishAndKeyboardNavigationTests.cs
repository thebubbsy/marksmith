using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run295StudioPolishAndKeyboardNavigationTests
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
    public void HistoryWindow_HistoryFilterCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "History", "HistoryWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""HistoryFilterCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "HistoryFilterCluster declaration should exist in HistoryWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Timeline filters""", match.Value);
    }

    [Fact]
    public void HistoryWindow_DiffMatchStepperCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "History", "HistoryWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""DiffMatchStepperCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "DiffMatchStepperCluster declaration should exist in HistoryWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Search match navigation""", match.Value);
    }

    [Fact]
    public void HistoryWindow_DiffViewOptionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "History", "HistoryWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""DiffViewOptionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "DiffViewOptionsCluster declaration should exist in HistoryWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Diff display options""", match.Value);
    }

    [Fact]
    public void OcrInspectionStudio_ReclassifyActionCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Ocr", "OcrInspectionStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<Grid\s+x:Name=""ReclassifyActionCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "ReclassifyActionCluster declaration should exist in OcrInspectionStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Reclassification actions""", match.Value);
    }

    [Fact]
    public void OcrInspectionStudio_MarkdownPaneActions_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Ocr", "OcrInspectionStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""MarkdownPaneActions""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "MarkdownPaneActions declaration should exist in OcrInspectionStudioWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Markdown preview actions""", match.Value);
    }

    [Fact]
    public void OcrInspectionStudio_Escape_DeselectsSelectedElementBeforeDiscard()
    {
        var csPath = Path.Combine(GetSourceRoot(), "Views", "Ocr", "OcrInspectionStudioWindow.xaml.cs");
        Assert.True(File.Exists(csPath), $"File exists: {csPath}");
        var content = File.ReadAllText(csPath);

        Assert.Contains("ClearElementSelection();", content);
        Assert.Contains("Deselected element", content);
    }

    [Fact]
    public void OcrInspectionStudio_ReclassifyShortcuts_AnnounceWhenNoSelection()
    {
        var csPath = Path.Combine(GetSourceRoot(), "Views", "Ocr", "OcrInspectionStudioWindow.xaml.cs");
        Assert.True(File.Exists(csPath), $"File exists: {csPath}");
        var content = File.ReadAllText(csPath);

        Assert.Contains("Select an element first to reclassify it as", content);
        Assert.Contains("No element selected to reset. Select an element on the canvas or press Up/Down first.", content);
        Assert.Contains("is already using its original", content);
    }

    [Fact]
    public void SettingsView_LicenseActionCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""LicenseActionCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "LicenseActionCluster declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""License actions""", match.Value);
    }

    [Fact]
    public void SettingsView_AboutLinks_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""AboutLinks""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "AboutLinks declaration should exist in SettingsView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Related links""", match.Value);
    }
}
