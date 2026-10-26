using System;
using System.IO;
using MarkSmith.Services.MindMap;
using Xunit;

namespace MarkSmith.Tests;

// Run #287:
// - Status bar: the "More" pill keeps keyboard focus when the window is resized (the overflow
//   re-measure collapsed it for a moment and focus fell away), and when the pills unfold focus
//   lands on the first pill "More" stood in for;
// - the license banner hands focus to the editor before hiding its focused action button;
// - OCR studio reads out the finished page breakdown once ("Assembled 120 words into 8 blocks.");
// - Mind map: the open-time repair note says "1 broken link", not "1 broken link(s)";
// - Diagram Studio's canvas summary groups large counts like the rest of the app.
public class Run287StatusFocusAndCountWordingTests
{
    private static string RepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoDir(), relative)).Replace("\r\n", "\n");

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"missing: {start}");
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"missing: {end}");
        return text.Substring(a, b - a);
    }

    [Fact]
    public void RepairSummary_UsesSingularForOne()
    {
        var report = new MindMapRepairReport { DroppedDanglingLinks = 1, RepairedNodeIds = 2, BrokenCycles = 1 };
        Assert.Equal("Repaired 1 broken link, 1 parent cycle, 2 duplicate ids.", report.Summarize());
    }

    [Fact]
    public void RepairSummary_PluralsEveryKind()
    {
        var report = new MindMapRepairReport
        {
            DroppedDanglingLinks = 2, DroppedSelfLinks = 3, MergedDuplicateLinks = 4, ClearedDanglingParents = 5,
            BrokenCycles = 6, RepairedChildLists = 7, RepairedNodeIds = 1200, ReassignedRoot = true,
        };
        var text = report.Summarize();
        Assert.DoesNotContain("(s)", text);
        Assert.Equal(
            "Repaired 2 broken links, 3 self-links, 4 duplicate links, 5 orphaned parent refs, 6 parent cycles, " +
            "7 child lists, 1,200 duplicate ids, a missing root.", text);
    }

    [Fact]
    public void RepairSummary_EmptyWithoutRepairs() => Assert.Equal("", new MindMapRepairReport().Summarize());

    [Fact]
    public void RepairSummary_SourceHasNoRawPlurals()
    {
        var body = Between(Read("MarkSmith.Core/Services/MindMap/MindMapGraph.cs"), "public string Summarize()", "public sealed class MindMapInsights");
        Assert.DoesNotContain("(s)", body);
    }

    [Fact]
    public void StatusBarOverflow_MoreKeepsFocusAcrossTheRemeasure()
    {
        var body = Between(Read("MarkSmith.Desktop/MainWindow.xaml.cs"), "private void UpdateStatusBarOverflow()", "private static bool IsDescendantOf(");
        int remember = body.IndexOf("var moreFocus = StatusMoreCopyButton.FocusState;", StringComparison.Ordinal);
        int collapse = body.IndexOf("StatusMoreCopyButton.Visibility = Visibility.Collapsed;", StringComparison.Ordinal);
        Assert.True(remember >= 0 && remember < collapse, "focus must be read before More collapses for the re-measure");
        Assert.Contains("if (moreFocus != FocusState.Unfocused) StatusMoreCopyButton.Focus(moreFocus);", body);
        Assert.Contains("StatusCopyCodeButton.Focus(moreFocus);", body);
    }

    [Fact]
    public void LicenseBanner_ReleasesFocusBeforeHidingTheAction()
    {
        var src = Read("MarkSmith.Desktop/MainWindow.xaml.cs");
        var body = Between(src, "private void UpdateLicenseBanner()", "private void UpdateExportButtonForLicense(");
        int release = body.IndexOf("ReleaseLicenseActionFocus();", StringComparison.Ordinal);
        Assert.True(release >= 0 && release < body.IndexOf("LicenseBanner.IsOpen = false;", StringComparison.Ordinal));
        int second = body.IndexOf("ReleaseLicenseActionFocus();", release + 1, StringComparison.Ordinal);
        Assert.True(second >= 0 && second < body.IndexOf("LicenseActionButton.Visibility = Visibility.Collapsed;", StringComparison.Ordinal));
        var helper = Between(src, "private void ReleaseLicenseActionFocus()", "private void UpdateExportButtonForLicense(");
        Assert.Contains("FocusState.Unfocused) return;", helper);
        Assert.Contains("PasteTextBox.Focus(", helper);
    }

    [Fact]
    public void OcrBreakdown_AnnouncesOnlyTheResult()
    {
        var src = Read("MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml.cs");
        Assert.DoesNotContain("LiveAnnounce.Track(WordStreamText", src);
        int assembled = src.IndexOf("WordStreamText.Text = $\"Assembled ", StringComparison.Ordinal);
        int raise = src.IndexOf("LiveAnnounce.Raise(WordStreamText);", StringComparison.Ordinal);
        Assert.True(assembled >= 0 && raise > assembled, "the result line is raised after it is set");
        Assert.Equal(raise, src.LastIndexOf("LiveAnnounce.Raise(WordStreamText);", StringComparison.Ordinal));
    }

    [Fact]
    public void MermaidCanvasSummary_UsesCountText()
    {
        var src = Read("MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml.cs");
        Assert.DoesNotContain("$\"{n} {one}s\"", src);
        Assert.Contains("CountText.Of(vm.Nodes.Count, \"node\"), CountText.Of(vm.Connectors.Count, \"connector\")", src);
    }
}
