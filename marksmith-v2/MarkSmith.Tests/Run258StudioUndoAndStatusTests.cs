using System;
using System.IO;
using System.Linq;
using MarkSmith.ViewModels.ShapeStudio;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Tests;

// Run #258 (run #257's Next up items 2 and 3):
// - SmartArt Studio's outline commands (promote / demote / flatten / reverse / sort / trim / standardise /
//   normalise indentation, the starter templates, paste, structure by delimiter, balance depth, normalise
//   matrix quadrants) assigned MarkdownText directly, so Ctrl+Z couldn't bring the old outline back;
// - seven of those edits also copied their summary, silently replacing the user's clipboard;
// - ReportEdit (Shape Studio, the Galaxy) replaced the VM's "✓ … · Ctrl+Z to undo" line with the bare summary;
// - Shape Studio's status texts used the old menu names ("Distribute evenly (Horizontal)", "Distributed uniform
//   spacing", "…with equal spacing" for the left-edge command), and two distribute paths failed silently.
public class Run258StudioUndoAndStatusTests
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

    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = code.IndexOf("\n        }\n", start, StringComparison.Ordinal);
        return code[start..end];
    }

    private const string SmartArtWindow = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string ShapeWindow = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string GalaxyWindow = "Views/MindMap/MindMapGalaxyWindow.xaml.cs";

    [Fact]
    public void ReplaceOutline_IsOneUndoableStep()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = "- B\n- A";
        var before = vm.MarkdownText;

        Assert.True(vm.ReplaceOutline("- A\n- B"));
        Assert.Equal("- A\n- B", vm.MarkdownText);
        Assert.True(vm.CanUndo);

        vm.Undo();
        Assert.Equal(before, vm.MarkdownText);
    }

    [Fact]
    public void ReplaceOutline_WithTheSameText_PushesNothing()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = "- Only";
        bool couldUndo = vm.CanUndo;

        Assert.False(vm.ReplaceOutline("- Only"));
        Assert.Equal(couldUndo, vm.CanUndo);
    }

    // Found live: the Markdown TextBox stores Enter as a bare \r, and Sort A to Z turned a typed
    // "- Banana / - Apple / - Cherry" into the single line "- Banana- Apple- Cherry".
    [Fact]
    public void TypedOutline_WithBareCarriageReturns_SortsLineByLine()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = "- Banana\r- Apple\r- Cherry";

        Assert.Equal("- Banana\n- Apple\n- Cherry", vm.OutlineText);
        Assert.True(vm.ReplaceOutline(SmartArtDesignStudioViewModel.SortOutlineAlphabetically(vm.OutlineText, ascending: true)));
        Assert.Equal(new[] { "- Apple", "- Banana", "- Cherry" }, vm.OutlineText.Split('\n'));

        vm.Undo();
        Assert.Equal("- Banana\r- Apple\r- Cherry", vm.MarkdownText);
    }

    [Fact]
    public void ReplaceOutline_IgnoresLineBreakStyle_WhenNothingElseChanged()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = "- A\r- B";
        bool couldUndo = vm.CanUndo;
        Assert.False(vm.ReplaceOutline("- A\n- B"));
        Assert.False(vm.ReplaceOutline("- A\r\n- B"));
        Assert.Equal(couldUndo, vm.CanUndo);
    }

    [Fact]
    public void BalanceOutlineNestingDepth_SeesEveryTypedLine()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = "- Root\r  - Child\r    - Grandchild\r      - Too deep";
        var (_, adjusted, _) = vm.BalanceOutlineNestingDepth(maxDepth: 2, mergeIntoParent: false);
        Assert.True(adjusted > 0);
        Assert.Equal(4, vm.OutlineText.Split('\n').Length);
    }

    [Fact]
    public void SmartArtWindow_ReadsTheOutlineThroughOutlineText()
    {
        var code = Read(SmartArtWindow);
        Assert.DoesNotContain("ViewModel.MarkdownText ?? \"\"", code);
        // Run #268 routed nine outline commands through OutlineForCommand(), which reads OutlineText once.
        Assert.True(code.Split("ViewModel.OutlineText").Length - 1 + code.Split("OutlineForCommand()").Length - 1 >= 20);
    }

    [Fact]
    public void BalanceOutlineNestingDepth_CanBeUndone()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = "- Root\n  - Child\n    - Grandchild\n      - Too deep";
        var before = vm.MarkdownText;

        var (_, adjusted, _) = vm.BalanceOutlineNestingDepth(maxDepth: 2, mergeIntoParent: false);
        Assert.True(adjusted > 0);
        Assert.NotEqual(before, vm.MarkdownText);

        vm.Undo();
        Assert.Equal(before, vm.MarkdownText);
    }

    [Fact]
    public void SmartArtViewModel_QuadrantNormalisation_GoesThroughReplaceOutline()
    {
        var dir = new DirectoryInfo(DesktopDir()).Parent!.FullName;
        var code = File.ReadAllText(Path.Combine(dir, "MarkSmith.Core", "ViewModels", "SmartArtStudio", "SmartArtDesignStudioViewModel.cs")).Replace("\r\n", "\n");
        var start = code.IndexOf("public MatrixQuadrantNormalizationResult NormalizeMatrixQuadrantHeaders()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = code[start..code.IndexOf("\n    }\n", start, StringComparison.Ordinal)];
        Assert.Contains("ReplaceOutline(result.NormalizedMarkdown)", body);
        Assert.DoesNotContain("MarkdownText = ", body);
    }

    [Fact]
    public void SmartArtWindow_NeverAssignsTheOutlineDirectly()
    {
        var code = Read(SmartArtWindow);
        Assert.DoesNotContain("ViewModel.MarkdownText = ", code);
        Assert.True(code.Split("ViewModel.ReplaceOutline(").Length - 1 >= 20);
    }

    [Theory]
    [InlineData("OnReverseHierarchyOutlineClick")]
    [InlineData("OnDeduplicateOutlineClick")]
    [InlineData("OnSplitLongOutlineClick")]
    [InlineData("OnAutoNumberOutlineClick")]
    [InlineData("OnStructureByDelimiterOutlineClick")]
    [InlineData("OnBalanceOutlineDepthClick")]
    [InlineData("OnNormalizeMatrixQuadrantsClick")]
    public void SmartArtOutlineEdits_ReportWithoutTouchingTheClipboard(string handler)
    {
        var body = Method(Read(SmartArtWindow), $"void {handler}(");
        Assert.Contains("ReportEdit(", body);
        Assert.DoesNotContain("CopyAndReport(", body);
        Assert.DoesNotContain("Clipboard", body);
    }

    [Fact]
    public void SmartArtFlatten_SaysSoWhenTheOutlineIsAlreadyFlat()
    {
        var body = Method(Read(SmartArtWindow), "private void OnFlattenAllClick(");
        Assert.Contains("if (!ViewModel.ReplaceOutline(", body);
        Assert.Contains("already at the root level", body);
    }

    [Theory]
    [InlineData(SmartArtWindow)]
    [InlineData(ShapeWindow)]
    [InlineData(GalaxyWindow)]
    public void ReportEdit_KeepsTheViewModelsRicherLine(string file)
    {
        var body = Method(Read(file), "private void ReportEdit(string? summary)");
        Assert.DoesNotContain("Clipboard", body);
        var keep = body.IndexOf("ViewModel.StatusMessage?.Contains(line, StringComparison.Ordinal) == true) return;", StringComparison.Ordinal);
        var set = body.IndexOf("ViewModel.StatusMessage = ", StringComparison.Ordinal);
        Assert.True(keep >= 0 && set > keep);
    }

    [Fact]
    public void EqualGapsSummary_UsesTheMenuWording()
    {
        Assert.Equal("Equal gaps between shapes (vertical): needs at least 3 shapes (2 found)",
            ShapeDesignStudioViewModel.FormatDistributionSummary(0, 2, DistributionAxis.Vertical));
        Assert.Equal("Equal gaps between shapes (horizontal): moved 2 shapes of 4 (12.5px gaps)",
            ShapeDesignStudioViewModel.FormatDistributionSummary(2, 4, DistributionAxis.Horizontal, 12.5));
    }

    [Fact]
    public void WholePixelGaps_NamesTheCommand_WhenThereAreTooFewShapes()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();
        vm.AddShapeAt("rect", 0, 0, 50, 50, "0078D4", "A");
        vm.AddShapeAt("rect", 100, 0, 50, 50, "0078D4", "B");
        var (_, modified, _, summary) = vm.DistributeSpacingUniformly(DistributionAxis.Horizontal);
        Assert.Equal(0, modified);
        Assert.Equal("Equal gaps in whole pixels (horizontal): needs at least 3 shapes (2 found)", summary);
    }

    [Fact]
    public void SpaceLeftEdges_ReportsWhatItDid_WithTheUndoHint()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();
        vm.AddShapeAt("rect", 0, 0, 40, 40, "0078D4", "A");
        vm.AddShapeAt("rect", 30, 0, 40, 40, "0078D4", "B");
        vm.AddShapeAt("rect", 200, 0, 40, 40, "0078D4", "C");
        vm.SelectAll();
        vm.DistributeHorizontal();
        Assert.Equal(100, vm.Shapes.OrderBy(s => s.X).ElementAt(1).X);
        Assert.Equal("Spaced left edges evenly across 3 shapes · Ctrl+Z to undo", vm.StatusMessage);
    }

    [Fact]
    public void CanvasDistribute_WithTooFewShapes_SaysWhy()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();
        vm.AddShapeAt("rect", 0, 0, 40, 40, "0078D4", "A");
        vm.DistributeDiagramOnCanvas(vertical: false);
        Assert.Equal("Distribute needs at least 3 shapes on the canvas (1 found)", vm.StatusMessage);
    }

    [Theory]
    [InlineData("OnSelectionDistributeHorizontallyClick", "left edges")]
    [InlineData("OnSelectionDistributeVerticallyClick", "top edges")]
    public void SelectionMenuEdgeSpacing_NoLongerClaimsEqualSpacing(string handler, string edges)
    {
        var body = Method(Read(ShapeWindow), $"private void {handler}(");
        Assert.DoesNotContain("with equal spacing", body);
        var guard = body.IndexOf("if (ViewModel.SelectionCount < 3)", StringComparison.Ordinal);
        var run = body.IndexOf("ViewModel.Distribute", StringComparison.Ordinal);
        Assert.True(guard >= 0 && run > guard);
        Assert.Contains($"to space their {edges} evenly", body);
    }

    [Fact]
    public void ShapeContextMenus_NameTheEdgeSpacingCommandsLikeTheSelectionMenu()
    {
        var code = Read(ShapeWindow);
        Assert.DoesNotContain("ShapeMenuItem(\"Distribute horizontally\"", code);
        Assert.DoesNotContain("ShapeMenuItem(\"Distribute vertically\"", code);
        Assert.Equal(4, code.Split("ShapeMenuItem(\"Space left edges evenly\", \"\\uE8AB\"").Length - 1);
        Assert.Equal(4, code.Split("ShapeMenuItem(\"Space top edges evenly\", \"\\uE8CB\"").Length - 1);
    }
}
