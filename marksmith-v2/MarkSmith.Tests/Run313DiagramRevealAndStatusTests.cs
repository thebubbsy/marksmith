using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MarkSmith.Core.Mermaid.Routing;
using MarkSmith.ViewModels;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

// Run #313:
// - Diagram Studio scrolls a quick-added, Tab-selected or pasted shape into view (it could land
//   half past the edge of the canvas with the view left where it was);
// - naming a quick-added shape in the Properties panel replaces "Type to name it." on the status line;
// - a right-click quick-add of a Decision/Circle/… is one undo step (Ctrl+Z only reshaped it before);
// - Shape palette rows are named once, on the row (the card inside exposed the name again as a Group);
// - Alt+drag duplicate's status uses the short name, and counts (status line, status-bar summary,
//   code sync, minimap) say "1 shape, 2 connectors" rather than nodes and edges;
// - the main window's detected-source badge says "1 fix" and leaves "0 fixes" off.
public class Run313DiagramRevealAndStatusTests
{
    private const string CanvasCode = "MarkSmith.Desktop/Views/Mermaid/MermaidCanvasControl.xaml.cs";
    private const string PaletteXaml = "MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml";
    private const string PaletteCode = "MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml.cs";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

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

    private static MermaidStudioViewModel FlowchartVm()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMarkdown("```mermaid\nflowchart TD\n    A[Start] -->|go| B{Check input}\n```", 0);
        return vm;
    }

    // ---- Reveal ----

    [Theory]
    [InlineData(0, 800, 100, 150, 40)]   // well inside
    [InlineData(200, 800, 240, 150, 40)] // exactly at the margin
    public void Reveal_LeavesAShapeInViewWhereItIs(double offset, double viewport, double start, double length, double margin)
    {
        Assert.Null(ViewportReveal.OffsetToReveal(offset, viewport, start, length, margin));
    }

    [Fact]
    public void Reveal_ScrollsJustFarEnough_PastTheFarEdge()
    {
        // Shape at 900–1050 in an 800-wide view from 0: its right edge plus the margin meets the view's end.
        Assert.Equal(290, ViewportReveal.OffsetToReveal(0, 800, 900, 150, 40));
    }

    [Fact]
    public void Reveal_ScrollsBack_ForAShapeBeforeTheView()
    {
        Assert.Equal(60, ViewportReveal.OffsetToReveal(500, 800, 100, 150, 40));
        Assert.Equal(0, ViewportReveal.OffsetToReveal(500, 800, 10, 150, 40)); // never below zero
    }

    [Fact]
    public void Reveal_AShapeBiggerThanTheView_LinesUpItsStart()
    {
        Assert.Equal(960, ViewportReveal.OffsetToReveal(0, 300, 1000, 400, 40));
    }

    [Fact]
    public void Reveal_NoViewportYet_DoesNothing()
    {
        Assert.Null(ViewportReveal.OffsetToReveal(0, 0, 1000, 100, 40));
    }

    [Fact]
    public void QuickAdd_Tab_AndPaste_AskForTheShapeToBeRevealed()
    {
        var vm = FlowchartVm();
        var revealed = new List<DiagramNodeViewModel>();
        vm.RevealNodeRequested += (_, n) => revealed.Add(n);

        var added = vm.QuickAddNode(vm.Nodes[0], "Right");
        Assert.Same(added, revealed.Last());

        var cycled = vm.CycleSelection();
        Assert.Same(cycled, revealed.Last());

        vm.CopySelected();
        vm.PasteClipboard();
        Assert.Same(vm.SelectedNode, revealed.Last());
        Assert.Equal(3, revealed.Count);
    }

    [Fact]
    public void PointerSelection_DoesNotScroll()
    {
        var vm = FlowchartVm();
        int raised = 0;
        vm.RevealNodeRequested += (_, _) => raised++;
        vm.SelectNode(vm.Nodes[1]);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Canvas_ScrollsToTheRequestedShape_AndUnhooksOnDataContextSwap()
    {
        var code = Read(CanvasCode);
        Assert.Contains("_hintVm.RevealNodeRequested -= OnRevealNodeRequested;", code);
        Assert.Contains("_hintVm.RevealNodeRequested += OnRevealNodeRequested;", code);
        Assert.Contains("ViewportReveal.OffsetToReveal(", code);
        Assert.Contains("CanvasScrollViewer.ChangeView(x, y, null);", code);
        // The band under the minimap is hidden, as in Fit.
        var reveal = code[code.IndexOf("public void RevealNode(", StringComparison.Ordinal)..];
        Assert.Contains("MinimapControl.Visibility == Visibility.Visible", reveal[..reveal.IndexOf("\n    }\n", StringComparison.Ordinal)]);
    }

    // ---- "Type to name it." ----

    [Fact]
    public void NamingInThePropertiesPanel_ReplacesTheNamePrompt_KeystrokeByKeystroke()
    {
        var vm = FlowchartVm();
        var added = vm.QuickAddNode(vm.Nodes[1], "Right");
        Assert.EndsWith("Type to name it.", vm.StatusText);

        added.LabelText = "S";
        Assert.Equal("Renamed shape to 'S'.", vm.StatusText);
        added.LabelText = "Ship it";
        Assert.Equal("Renamed shape to 'Ship it'.", vm.StatusText);
    }

    [Fact]
    public void NamePrompt_LeavesALaterStatusAlone()
    {
        var vm = FlowchartVm();
        var added = vm.QuickAddNode(vm.Nodes[1], "Right");
        vm.StatusText = "Exported diagram.";
        added.LabelText = "Ship it";
        Assert.Equal("Exported diagram.", vm.StatusText);
    }

    [Fact]
    public void NamePrompt_IgnoresLabelsSetUnderSuspendedTracking()
    {
        // The canvas's inline rename and the palette's "Connect to" set the label themselves and
        // write their own status line.
        var vm = FlowchartVm();
        var added = vm.QuickAddNode(vm.Nodes[1], "Right");
        var prompt = vm.StatusText;
        using (vm.SuspendEditTracking()) added.LabelText = "Decision";
        Assert.Equal(prompt, vm.StatusText);
    }

    [Fact]
    public void NamePrompt_FollowsOnlyTheShapeItWasFor()
    {
        var vm = FlowchartVm();
        vm.QuickAddNode(vm.Nodes[1], "Right");
        var prompt = vm.StatusText;
        vm.Nodes[0].LabelText = "Other"; // not selected, not prompted
        Assert.Equal(prompt, vm.StatusText);
    }

    // ---- One undo step for a right-click quick-add ----

    [Fact]
    public void QuickAddThenReshape_UnderSuspendedTracking_UndoesInOneStep()
    {
        var vm = FlowchartVm();
        int before = vm.Nodes.Count;
        var added = vm.QuickAddNode(vm.Nodes[1], "Right");
        using (vm.SuspendEditTracking())
        {
            added.Shape = "Rhombus";
            added.LabelText = "Decision";
        }
        vm.Undo();
        Assert.Equal(before, vm.Nodes.Count);
    }

    [Fact]
    public void QuickAddMenu_SetsShapeAndLabel_InsideTheQuickAddsUndoStep()
    {
        var code = Read(CanvasCode);
        var spawn = code[code.IndexOf("void SpawnShape(", StringComparison.Ordinal)..];
        spawn = spawn[..spawn.IndexOf("StartNodeInPlaceEdit(newNode);", StringComparison.Ordinal)];
        var suspend = spawn.IndexOf("using var _ = ViewModel.SuspendEditTracking();", StringComparison.Ordinal);
        Assert.True(suspend > spawn.IndexOf("ViewModel.QuickAddNode(nodeVM, dirTag)", StringComparison.Ordinal));
        Assert.True(suspend < spawn.IndexOf("newNode.Shape = shapeType;", StringComparison.Ordinal));
    }

    // ---- Palette rows ----

    [Fact]
    public void PaletteRows_AreNamedOnTheRow_NotOnTheCardInside()
    {
        var doc = XDocument.Parse(Read(PaletteXaml));
        var list = doc.Descendants(P + "ListView").Single(e => (string?)e.Attribute(X + "Name") == "PaletteListView");
        Assert.Equal("OnPaletteContainerContentChanging", (string?)list.Attribute("ContainerContentChanging"));
        var card = list.Descendants(P + "DataTemplate").Single().Elements().First();
        Assert.Null(card.Attribute("AutomationProperties.Name"));

        var code = Read(PaletteCode);
        Assert.Contains("AutomationProperties.SetName(args.ItemContainer, item.DisplayName);", code);
        Assert.Contains("AutomationProperties.SetHelpText(args.ItemContainer,", code);
        Assert.Contains("if (args.InRecycleQueue || args.Item is not ViewModels.Mermaid.MermaidPaletteItem item) return;", code);
    }

    // ---- Status wording ----

    [Theory]
    [InlineData(1, 0, "1 shape, 0 connectors")]
    [InlineData(2, 1, "2 shapes, 1 connector")]
    [InlineData(0, 0, "0 shapes, 0 connectors")]
    public void ShapeCount_IsSingularForOne(int shapes, int connectors, string expected)
    {
        Assert.Equal(expected, MermaidStudioViewModel.ShapeCount(shapes, connectors));
    }

    [Fact]
    public void LoadedDiagramStatus_SaysShapesAndConnectors()
    {
        var vm = FlowchartVm();
        Assert.Contains("(2 shapes, 1 connector)", vm.StatusText);
        Assert.DoesNotContain("nodes", vm.StatusText);
    }

    [Fact]
    public void StudioStatusLines_NoLongerSayNodesOrEdges()
    {
        var code = Read("MarkSmith.Core/ViewModels/Mermaid/MermaidStudioViewModel.cs");
        Assert.DoesNotContain("nodes, {Connectors.Count} edges", code);
        Assert.DoesNotContain("Pasted {created.Count} nodes", code);
        Assert.DoesNotContain("Deleted {nodesToDelete.Count} nodes", code);
        Assert.DoesNotContain("Selected all {SelectedNodes.Count} nodes", code);
        Assert.DoesNotContain("two nodes to align", code);
    }

    [Fact]
    public void StatusBarSummary_CodeSyncAndMinimap_CountShapesAndConnectors()
    {
        var studio = Read("MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml.cs");
        Assert.Contains("CountText.Of(vm.Nodes.Count, \"shape\"), CountText.Of(vm.Connectors.Count, \"connector\")", studio);
        Assert.Contains("CountText.Of(ViewModel.Nodes.Count, \"shape\")}, {CountText.Of(ViewModel.Connectors.Count, \"connector\")", studio);
        Assert.DoesNotContain("\"edge\")", studio);
        Assert.Contains("CountText.Of(vm.Nodes.Count, \"shape\")", Read("MarkSmith.Desktop/Views/Mermaid/MermaidMinimapControl.cs"));
    }

    [Fact]
    public void AltDragDuplicate_StatusUsesTheShortName()
    {
        var vm = FlowchartVm();
        var node = vm.Nodes[1];
        node.LabelText = "Check input\rsecond line";
        vm.DuplicateSingleNodeForDrag(node);
        Assert.Equal("Duplicated 'Check input' — drag to place.", vm.StatusText);
    }

    [Fact]
    public void Pasting_SeveralShapes_SaysShapes()
    {
        var vm = FlowchartVm();
        vm.SelectAll();
        vm.CopySelected();
        vm.PasteClipboard();
        Assert.Equal("Pasted 2 shapes.", vm.StatusText);
    }

    // ---- Detected-source badge ----

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, " · 1 fix")]
    [InlineData(3, " · 3 fixes")]
    public void FixesSuffix_IsSingularForOne_AndBlankForNone(int n, string expected)
    {
        Assert.Equal(expected, MainViewModel.FixesSuffix(n));
    }

    [Fact]
    public void DetectedSourceBadge_UsesTheFixesHelper()
    {
        var code = Read("MarkSmith.Core/ViewModels/MainViewModel.cs");
        Assert.DoesNotContain("AppliedFixes.Count} fixes", code);
        Assert.Contains("applied {FixCount(classification.AppliedFixes.Count)}.", code);
    }
}
