using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run299StudioPolishAndKeyboardNavigationTests
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
    public void MainWindow_EditorStripTools_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""EditorStripTools""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "EditorStripTools declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Editor strip tools""", match.Value);
    }

    [Fact]
    public void MainWindow_PreviewZoomControlsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""PreviewZoomControlsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PreviewZoomControlsCluster declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Preview zoom controls""", match.Value);
    }

    [Fact]
    public void MainWindow_PortalControlsRow_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""PortalControlsRow""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "PortalControlsRow declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Looking Glass portal dials""", match.Value);
    }

    [Fact]
    public void MainWindow_StyleHeaderToolsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""StyleHeaderToolsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "StyleHeaderToolsCluster declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Style and export header tools""", match.Value);
    }

    [Fact]
    public void MainWindow_FileActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""FileActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "FileActionsCluster declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Selected file actions""", match.Value);
    }

    [Fact]
    public void MainWindow_OutputFolderActionsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""OutputFolderActionsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "OutputFolderActionsCluster declaration should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        Assert.Contains(@"AutomationProperties.Name=""Output folder actions""", match.Value);
    }

    [Fact]
    public void MainWindow_RescanButton_HasAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<HyperlinkButton\s+x:Name=""RescanButton""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "RescanButton declaration should exist in MainWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Rescan Markdown files""", match.Value);
    }

    [Fact]
    public void MainWindow_StatusPrimaryCopyPills_HasAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""StatusPrimaryCopyPills""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "StatusPrimaryCopyPills declaration should exist in MainWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Primary copy actions""", match.Value);
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
    }

    [Fact]
    public void MainWindow_StatusSecondaryCopyPills_HasAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""StatusSecondaryCopyPills""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "StatusSecondaryCopyPills declaration should exist in MainWindow.xaml");
        Assert.Contains(@"AutomationProperties.Name=""Secondary copy actions""", match.Value);
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
    }

    [Fact]
    public void ShapeStudio_MultiAlignClusterButtons_HaveAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        Assert.Contains(@"<Button x:Name=""MultiAlignLeftBtn"" AutomationProperties.Name=""Align left edges""", content);
        Assert.Contains(@"<Button x:Name=""MultiAlignCenterBtn"" AutomationProperties.Name=""Align horizontal center""", content);
        Assert.Contains(@"<Button x:Name=""MultiAlignRightBtn"" AutomationProperties.Name=""Align right edges""", content);
        Assert.Contains(@"<Button x:Name=""MultiAlignTopBtn"" AutomationProperties.Name=""Align top edges""", content);
        Assert.Contains(@"<Button x:Name=""MultiAlignMiddleBtn"" AutomationProperties.Name=""Align vertical middle""", content);
        Assert.Contains(@"<Button x:Name=""MultiAlignBottomBtn"" AutomationProperties.Name=""Align bottom edges""", content);
        Assert.Contains(@"<Button x:Name=""MultiDistributeHBtn"" AutomationProperties.Name=""Distribute horizontally""", content);
        Assert.Contains(@"<Button x:Name=""MultiDistributeVBtn"" AutomationProperties.Name=""Distribute vertically""", content);
    }

    [Fact]
    public void ShapeStudio_MorphShapesClusterButtons_HaveAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        Assert.Contains(@"<Button x:Name=""MorphRectBtn"" AutomationProperties.Name=""Convert to rectangle""", content);
        Assert.Contains(@"<Button x:Name=""MorphRoundRectBtn"" AutomationProperties.Name=""Convert to rounded rectangle""", content);
        Assert.Contains(@"<Button x:Name=""MorphEllipseBtn"" AutomationProperties.Name=""Convert to ellipse""", content);
        Assert.Contains(@"<Button x:Name=""MorphDiamondBtn"" AutomationProperties.Name=""Convert to decision diamond""", content);
        Assert.Contains(@"<Button x:Name=""MorphHexagonBtn"" AutomationProperties.Name=""Convert to hexagon""", content);
        Assert.Contains(@"<Button Grid.Column=""2"" x:Name=""CornerRadiusToggleBtn"" AutomationProperties.Name=""Toggle corner radius""", content);
    }

    [Fact]
    public void ShapeStudio_LayerActionsClusterButtons_HaveAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        Assert.Contains(@"<Button x:Name=""InspectorBringToFrontBtn"" AutomationProperties.Name=""Bring to front""", content);
        Assert.Contains(@"<Button x:Name=""InspectorBringForwardBtn"" AutomationProperties.Name=""Bring forward""", content);
        Assert.Contains(@"<Button x:Name=""InspectorSendBackwardBtn"" AutomationProperties.Name=""Send backward""", content);
        Assert.Contains(@"<Button x:Name=""InspectorSendToBackBtn"" AutomationProperties.Name=""Send to back""", content);
    }

    [Fact]
    public void ShapeStudio_TransformActionsClusterButtons_HaveAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        Assert.Contains(@"<Button x:Name=""InspectorFlipHBtn"" AutomationProperties.Name=""Flip horizontal""", content);
        Assert.Contains(@"<Button x:Name=""InspectorFlipVBtn"" AutomationProperties.Name=""Flip vertical""", content);
        Assert.Contains(@"<Button x:Name=""InspectorRotateCwBtn"" AutomationProperties.Name=""Rotate 90 degrees clockwise""", content);
        Assert.Contains(@"<Button x:Name=""InspectorRotateCcwBtn"" AutomationProperties.Name=""Rotate 90 degrees counter-clockwise""", content);
        Assert.Contains(@"<Button Grid.Column=""2"" x:Name=""InspectorSquareAspectBtn"" AutomationProperties.Name=""Make square 1:1 aspect ratio""", content);
    }

    [Fact]
    public void SettingsView_Nav_HasAutomationPropertiesName()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SettingsView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        Assert.Contains(@"<NavigationView x:Name=""Nav"" AutomationProperties.Name=""Settings navigation""", content);
    }
}
