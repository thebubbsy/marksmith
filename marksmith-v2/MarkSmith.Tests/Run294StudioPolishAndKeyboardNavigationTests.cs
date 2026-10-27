using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

public class Run294StudioPolishAndKeyboardNavigationTests
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
    public void SuiteHub_StarterTemplates_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SuiteHubView.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""StarterTemplatesPanel""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "StarterTemplatesPanel declaration should exist");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
    }

    [Fact]
    public void SuiteHub_CardsGrid_ActionClusters_HaveXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SuiteHubView.xaml");
        var content = File.ReadAllText(xamlPath);

        for (int i = 1; i <= 6; i++)
        {
            var clusterName = $"Card{i}ActionCluster";
            var match = Regex.Match(content, $@"<StackPanel\s+x:Name=""{clusterName}""[^>]*>", RegexOptions.Singleline);
            Assert.True(match.Success, $"{clusterName} declaration should exist in SuiteHubView.xaml");
            Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
            Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
        }
    }

    [Fact]
    public void SuiteHub_FooterCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "SuiteHubView.xaml");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""HubFooterCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "HubFooterCluster declaration should exist in SuiteHubView.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
    }

    [Fact]
    public void WelcomeTour_PipelineAndFinishClusters_HaveXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "WelcomeTour.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var pipelineMatch = Regex.Match(content, @"<Grid\s+x:Name=""PipelineCardsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(pipelineMatch.Success, "PipelineCardsCluster should exist in WelcomeTour.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", pipelineMatch.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", pipelineMatch.Value);

        var finishMatch = Regex.Match(content, @"<Grid\s+x:Name=""FinishCardsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(finishMatch.Success, "FinishCardsCluster should exist in WelcomeTour.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", finishMatch.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", finishMatch.Value);
    }

    [Fact]
    public void WelcomeTour_TourNavCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "WelcomeTour.xaml");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""TourNavCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "TourNavCluster should exist in WelcomeTour.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
    }

    [Fact]
    public void MermaidStudio_DiagramToolsCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""DiagramToolsCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "DiagramToolsCluster should exist in MermaidDiagramStudioControl.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
    }

    [Fact]
    public void MindMapGalaxy_GalaxyActionCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "MindMap", "MindMapGalaxyWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""GalaxyActionCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "GalaxyActionCluster should exist in MindMapGalaxyWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
    }

    [Fact]
    public void HistoryWindow_TimelineJumpCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "Views", "History", "HistoryWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""TimelineJumpCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "TimelineJumpCluster should exist in HistoryWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
    }

    [Fact]
    public void MainWindow_StatusUtilityCluster_HasXYFocusAndSingleTabStop()
    {
        var xamlPath = Path.Combine(GetSourceRoot(), "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"File exists: {xamlPath}");
        var content = File.ReadAllText(xamlPath);

        var match = Regex.Match(content, @"<StackPanel\s+x:Name=""StatusUtilityCluster""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, "StatusUtilityCluster should exist in MainWindow.xaml");
        Assert.Contains(@"XYFocusKeyboardNavigation=""Enabled""", match.Value);
        Assert.Contains(@"TabFocusNavigation=""Once""", match.Value);
    }
}
