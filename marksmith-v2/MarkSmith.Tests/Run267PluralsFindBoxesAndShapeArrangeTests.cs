using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #267:
// - Status lines and copy reports say "1 table" / "3 tables" (Core CountText) instead of "3 table(s)"
//   across the main window's inventory copies, Version History, Shape Studio and Suite Hub;
// - Version History: Ctrl+Shift+F reaches Find in diff (Ctrl+F used to take it first);
// - SmartArt Studio: the outline's Find node box has a name, help text and tooltip, and its match
//   count is a polite live region with a spoken form;
// - Shape Studio: Center / Make square / Reset rotation explain an empty selection and a no-op instead
//   of recording an undo step that changes nothing.
public class Run267PluralsFindBoxesAndShapeArrangeTests
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
    private const string HistoryCs = "Views/History/HistoryWindow.xaml.cs";
    private const string HistoryXaml = "Views/History/HistoryWindow.xaml";
    private const string ShapeCs = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string SmartArtCs = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string SmartArtXaml = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml";
    private const string SuiteHubCs = "Views/SuiteHubView.xaml.cs";

    // ---- CountText ----

    [Theory]
    [InlineData(0, "table", null, "0 tables")]
    [InlineData(1, "table", null, "1 table")]
    [InlineData(2, "table", null, "2 tables")]
    [InlineData(1, "image source", null, "1 image source")]
    [InlineData(1204, "line", null, "1,204 lines")]
    [InlineData(3, "match", "matches", "3 matches")]
    [InlineData(1, "match", "matches", "1 match")]
    public void CountText_Of_PicksTheNoun(int count, string singular, string? plural, string expected)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(expected, CountText.Of(count, singular, plural));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void CountText_Noun_HasNoNumber()
    {
        Assert.Equal("image", CountText.Noun(1, "image"));
        Assert.Equal("images", CountText.Noun(4, "image"));
        Assert.Equal("children", CountText.Noun(2, "child", "children"));
    }

    // ---- "(s)" sweep ----

    [Theory]
    [InlineData(MainWindowCs)]
    [InlineData(HistoryCs)]
    [InlineData(ShapeCs)]
    [InlineData(SuiteHubCs)]
    public void StatusMessages_HaveNoParenthesisedPlurals(string file)
    {
        // A quoted string containing "word(s)"; "http(s)" is a protocol, not a plural.
        var offenders = Read(file).Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(l => Regex.IsMatch(l, "\"[^\"]*[A-Za-z]\\(s\\)[^\"]*\"") && !l.Contains("http(s)"))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void MainWindow_InventoryCopies_UseCountText()
    {
        var cs = Read(MainWindowCs);
        Assert.Contains("CountText.Of(tables.Count, \"Markdown table\")", cs);
        Assert.Contains("CountText.Of(alerts.Count, \"high-priority alert callout\")", cs);
        Assert.Contains("CountText.Of(images.Count, \"image source\")} (URLs and file paths)", cs);
        Assert.Contains("the dropped {CountText.Noun(images.Count, \"image\")}", cs);
        Assert.Contains("\"The footnote has a matching inline reference and definition.\"", cs);
    }

    [Fact]
    public void HistoryAndSuiteHub_CopyMessages_UseCountText()
    {
        Assert.Contains("CountText.Of(added.Count, \"added line\")", Read(HistoryCs));
        Assert.Contains("CountText.Of(names.Count, \"recent file name\")", Read(SuiteHubCs));
    }

    // ---- Version History: find in diff ----

    [Fact]
    public void History_CtrlF_LeavesCtrlShiftF_ForFindInDiff()
    {
        var cs = Read(HistoryCs);
        var plain = cs.IndexOf("if (isCtrl && !isShift && e.Key == VirtualKey.F)", StringComparison.Ordinal);
        var diff = cs.IndexOf("else if (isCtrl && isShift && e.Key == VirtualKey.F)", StringComparison.Ordinal);
        Assert.True(plain > 0, "Ctrl+F must exclude Shift");
        Assert.True(diff > plain);
        Assert.DoesNotContain("if (isCtrl && e.Key == VirtualKey.F)", cs);
    }

    [Fact]
    public void History_FindInDiffBox_AdvertisesItsShortcut()
    {
        var xaml = Read(HistoryXaml);
        Assert.Contains("PlaceholderText=\"Find in diff…\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"Find in diff (Ctrl+Shift+F)", xaml);
        Assert.DoesNotContain("Find in diff...", xaml);
    }

    // ---- SmartArt: outline find ----

    [Fact]
    public void SmartArt_OutlineFind_IsNamedAndAnnounced()
    {
        var xaml = Read(SmartArtXaml);
        var box = xaml[xaml.IndexOf("x:Name=\"OutlineSearchBox\"", StringComparison.Ordinal)..];
        box = box[..box.IndexOf("/>", StringComparison.Ordinal)];
        Assert.Contains("PlaceholderText=\"Find node…\"", box);
        Assert.Contains("AutomationProperties.Name=\"Find node in outline\"", box);
        Assert.Contains("ToolTipService.ToolTip=", box);

        var label = xaml[xaml.IndexOf("x:Name=\"OutlineMatchCountLabel\"", StringComparison.Ordinal)..];
        label = label[..label.IndexOf("/>", StringComparison.Ordinal)];
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", label);

        var cs = Read(SmartArtCs);
        Assert.Contains("private void SetOutlineMatchCount(string text, string spoken)", cs);
        Assert.Contains("AutomationEvents.LiveRegionChanged", cs);
        Assert.DoesNotContain("OutlineMatchCountLabel.Text = \"0 matches\"", cs);
        Assert.Contains("SetOutlineMatchCount(\"No match\"", cs);
        Assert.Contains("ViewModel.StatusMessage = $\"No node matches “{query}”.\";", cs);
    }

    // ---- Shape Studio: arrange actions ----

    private static string Handler(string cs, string name)
    {
        var start = cs.IndexOf($"private void {name}(", StringComparison.Ordinal);
        Assert.True(start > 0, name);
        var end = cs.IndexOf("\n        private ", start + 10, StringComparison.Ordinal);
        return cs[start..end];
    }

    [Theory]
    [InlineData("OnSelectionCenterCanvasClick", "No shapes on the canvas to center.", "already centered")]
    [InlineData("OnSelectionMakeSquareClick", "Select a shape to make it square.", "already square")]
    [InlineData("OnSelectionResetRotationClick", "Select a shape to reset its rotation.", "isn't rotated")]
    public void ShapeStudio_ArrangeActions_ExplainNoOps_BeforeRecordingUndo(string name, string emptyMessage, string noOpPhrase)
    {
        var body = Handler(Read(ShapeCs), name);
        Assert.Contains(emptyMessage, body);
        var noOp = body.IndexOf(noOpPhrase, StringComparison.Ordinal);
        var undo = body.IndexOf("ViewModel.RecordUndo();", StringComparison.Ordinal);
        Assert.True(noOp > 0 && undo > noOp, "the no-op check must come before RecordUndo");
        Assert.Contains("· Ctrl+Z to undo", body);
        Assert.DoesNotContain("if (sel.Count == 0) return;", body);
    }
}
