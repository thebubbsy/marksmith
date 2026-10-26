using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MarkSmith.ViewModels.MindMap;
using MarkSmith.ViewModels.ShapeStudio;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Tests;

// Run #262 (run #261's Next up items 1 and 2):
// - Status bar: Folds the Shortcuts pill and its divider into "More" when the window width is
//   constrained (< 1480px / secondary copy pills folded), freeing ~110px of room;
// - Status bar: Guarantees ellipsis trimming (StatusMsgText.MaxWidth capped to actualRemaining) so
//   status messages are never clipped mid-character at column edges;
// - Status bar: "More" flyout includes "Keyboard shortcuts cheatsheet (F1)" when folded;
// - MindMap Galaxy: Deduplicates and disambiguates cycle audits so AuditGraphFeedbackArcSetItem executes
//   ViewModel.AuditGraphDagFeedbackLoopsAndScc() with Tarjan SCC analysis instead of duplicate cycle analysis;
// - Shape Studio: Disambiguates duplicate Z-order audits between 2D bounding-box spatial occlusion vs layer compaction;
// - All Studios: ReportEdit in SmartArt, Shape Studio, and MindMap Galaxy normalizes bare \r and CRLF
//   so multi-line summaries don't leak carriage returns or multiple lines into the status bar;
// - MindMap Galaxy: Preview Excerpt cleanly detects paragraph breaks with bare \r\r as well as \n\n;
// - Shape Studio: Distribution spacing badge double-tap warns when < 3 shapes are selected.
public class Run262StudioPolishAndStatusBarTests
{
    private static string DesktopDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MarkSmith.Desktop");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative)).Replace("\r\n", "\n");

    private const string MainWindowCs = "MainWindow.xaml.cs";
    private const string MainWindowXaml = "MainWindow.xaml";
    private const string SmartArtWindow = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string ShapeWindow = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string ShapeXaml = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml";
    private const string GalaxyWindow = "Views/MindMap/MindMapGalaxyWindow.xaml.cs";
    private const string GalaxyXaml = "Views/MindMap/MindMapGalaxyWindow.xaml";

    [Fact]
    public void StatusBar_FoldsShortcutsPillAndDivider_AndCapsMaxWidthToActualRemaining()
    {
        var xaml = Read(MainWindowXaml);
        Assert.Contains("x:Name=\"StatusShortcutsDivider\"", xaml);
        Assert.Contains("x:Name=\"StatusShortcutsButton\"", xaml);

        var cs = Read(MainWindowCs);
        var overflowIdx = cs.IndexOf("private void UpdateStatusBarOverflow()", StringComparison.Ordinal);
        Assert.True(overflowIdx >= 0);
        var overflowEnd = cs.IndexOf("private static bool IsDescendantOf(", overflowIdx, StringComparison.Ordinal);
        var overflowBody = cs.Substring(overflowIdx, overflowEnd - overflowIdx);

        Assert.Contains("StatusShortcutsButton.Visibility = Visibility.Collapsed;", overflowBody);
        Assert.Contains("StatusShortcutsDivider.Visibility = Visibility.Collapsed;", overflowBody);
        Assert.Contains("actualRemaining = Math.Max(0, available - StatusRightPanel.DesiredSize.Width - otherLeft - 12);", overflowBody);
        Assert.Contains("StatusMsgText.MaxWidth = Math.Min(maxW, actualRemaining);", overflowBody);

        var moreIdx = cs.IndexOf("private void OnStatusMoreCopyButtonClick", StringComparison.Ordinal);
        Assert.True(moreIdx >= 0);
        var moreBody = cs.Substring(moreIdx, 1500);
        Assert.Contains("StatusShortcutsButton?.Visibility == Visibility.Collapsed", moreBody);
        Assert.Contains("Keyboard shortcuts cheatsheet (F1)", moreBody);
    }

    [Theory]
    [InlineData(SmartArtWindow)]
    [InlineData(ShapeWindow)]
    [InlineData(GalaxyWindow)]
    public void ReportEdit_NormalizesLineBreaks_AndSplitsOnFirstLine(string windowFile)
    {
        var code = Read(windowFile);
        var reportEditIdx = code.IndexOf("private void ReportEdit(string? summary)", StringComparison.Ordinal);
        Assert.True(reportEditIdx >= 0);
        var reportEditBody = code.Substring(reportEditIdx, 400);
        Assert.Contains("Replace('\\r', '\\n')", reportEditBody);
        Assert.Contains("Split('\\n')[0]", reportEditBody);
    }

    [Fact]
    public void MindMapExcerpt_DetectsParagraphBreaks_WithBareCarriageReturns()
    {
        var vm = new MindMapStudioViewModel();
        var method = typeof(MindMapStudioViewModel).GetMethod("Excerpt", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        string firstPara = new string('A', 800);
        string secondPara = new string('B', 800);
        string text = $"{firstPara}\r\r{secondPara}";

        var result = (string)method!.Invoke(null, new object[] { text, 1500 })!;
        Assert.Contains("open the document to read the rest", result);
        Assert.StartsWith(firstPara, result);
        Assert.DoesNotContain("BBBB", result);
    }

    [Fact]
    public void ShapeStudio_BadgeDoubleTap_GuardsUnderThreeShapes()
    {
        var code = Read(ShapeWindow);
        var badgeDoubleTapIdx = code.IndexOf("badge.DoubleTapped +=", StringComparison.Ordinal);
        Assert.True(badgeDoubleTapIdx >= 0);
        var body = code.Substring(badgeDoubleTapIdx, 400);
        Assert.Contains("selected.Count < 3", body);
        Assert.Contains("Select at least 3 shapes to space their edges evenly.", body);
    }

    [Fact]
    public void ShapeStudio_ZOrderAudits_HaveDistinctTitles()
    {
        var xaml = Read(ShapeXaml);
        Assert.Contains("SelectionAuditLayeringZOrderOcclusionItem", xaml);
        Assert.Contains("SelectionAuditShapeZOrderStackingItem", xaml);
        Assert.Contains("SelectionAuditZOrderOcclusionItem", xaml);

        // Titles are distinct and descriptive
        Assert.Contains("Audit diagram shape 2D bounding-box occlusion &amp; depth", xaml);
        Assert.Contains("Audit diagram Z-index stacking &amp; layer compaction", xaml);
    }

    [Fact]
    public void MindMapGalaxy_FeedbackArcSet_CallsDagFeedbackLoopsAndScc()
    {
        var cs = Read(GalaxyWindow);
        var handlerIdx = cs.IndexOf("OnAuditGraphFeedbackArcSetClick", StringComparison.Ordinal);
        Assert.True(handlerIdx >= 0);
        var body = cs.Substring(handlerIdx, 300);
        Assert.Contains("ViewModel.AuditGraphDagFeedbackLoopsAndScc()", body);

        var xaml = Read(GalaxyXaml);
        Assert.Contains("AuditGraphFeedbackArcSetItem", xaml);
        Assert.Contains("Audit graph DAG feedback loops &amp; strongly connected components", xaml);
    }

    [Fact]
    public void MindMapGalaxy_BipartiteAudits_HaveCleanLabels()
    {
        var xaml = Read(GalaxyXaml);
        Assert.Contains("AuditGraphBipartitenessOddCyclesItem", xaml);
        Assert.Contains("Audit graph bipartiteness &amp; 2-colorability compliance", xaml);
    }
}
