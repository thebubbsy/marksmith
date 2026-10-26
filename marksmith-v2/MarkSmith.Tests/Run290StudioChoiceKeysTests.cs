using System;
using System.IO;
using MarkSmith.ViewModels.Mermaid;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;

namespace MarkSmith.Tests;

// Run #290:
// - Shape Studio: Delete / Backspace and Ctrl+A in the preset list, shape palette, colour schemes
//   or a slider no longer delete (or select) the canvas shapes; the Shapes list keeps both;
// - Diagram Studio: Delete / Backspace / Ctrl+A in the palette list, category pills, combo boxes
//   or sliders leave the canvas selection alone (the canvas itself still deletes);
// - SmartArt Studio: Delete / Backspace on a layout, colour scheme or quick layout no longer
//   deletes the selected outline item;
// - Shape Studio and Diagram Studio say "Nothing to delete" instead of doing nothing;
// - Shape Studio's Select all reads right at one, and explains an empty canvas or traced line art.
public class Run290StudioChoiceKeysTests
{
    private const string ChoiceKeys = "MarkSmith.Desktop/Services/ChoiceKeys.cs";
    private const string ShapeStudio = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string Diagram = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml.cs";
    private const string SmartArt = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";

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

    // ---- the shared check ----

    [Fact]
    public void ChoiceKeys_CoversListsCombosSlidersSwitchesAndPillGroups()
    {
        var src = Read(ChoiceKeys);
        foreach (var kind in new[] { "ListViewBase", "SelectorItem", "ComboBox", "Slider", "ToggleSwitch", "XYFocusKeyboardNavigationMode.Enabled" })
            Assert.Contains(kind, src);
    }

    [Fact]
    public void ChoiceKeys_StopsAtTheSelectionHost_BeforeMatchingAList()
    {
        var src = Read(ChoiceKeys);
        int host = src.IndexOf("ReferenceEquals(cur, selectionHost)) return false;", StringComparison.Ordinal);
        int list = src.IndexOf("cur is ListViewBase", StringComparison.Ordinal);
        Assert.True(host > 0 && list > host, "the selection host must be checked before the list match");
    }

    // ---- Shape Studio ----

    [Fact]
    public void ShapeStudio_ShapesListIsTheSelectionHost()
    {
        var keys = Between(Read(ShapeStudio), "private void OnRootPreviewKeyDown", "private static bool IsDown(");
        Assert.Contains("bool inChoice = ChoiceKeys.FocusIsInChoiceControl(this.RootGrid.XamlRoot, ShapesList);", keys);
    }

    [Fact]
    public void ShapeStudio_DeleteAndSelectAll_SkipChoiceControls()
    {
        var keys = Between(Read(ShapeStudio), "private void OnRootPreviewKeyDown", "private static bool IsDown(");
        Assert.Contains("case VirtualKey.A when ctrl && !inChoice:", keys);
        Assert.Contains("case (VirtualKey.Delete or VirtualKey.Back) when !inChoice && ViewModel.HasSelection:", keys);
        Assert.DoesNotContain("case VirtualKey.A when ctrl:", keys);
        // The check is computed before the switch that uses it.
        Assert.True(keys.IndexOf("bool inChoice", StringComparison.Ordinal) < keys.IndexOf("switch (e.Key)", StringComparison.Ordinal));
    }

    [Fact]
    public void ShapeStudio_DeleteWithNothingSelected_SaysSo()
    {
        var keys = Between(Read(ShapeStudio), "private void OnRootPreviewKeyDown", "private static bool IsDown(");
        Assert.Contains("case VirtualKey.Delete when !inChoice && !ViewModel.HasSelection:", keys);
        Assert.Contains("Nothing to delete: the canvas is empty.", keys);
        Assert.Contains("Nothing to delete: select a shape on the canvas first.", keys);
    }

    [Fact]
    public void ShapeStudio_SelectAll_OnEmptyCanvas_ExplainsWhy()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        vm.SelectAll();

        Assert.Equal("Nothing to select: add a shape or pick a preset first.", vm.StatusMessage);
    }

    [Fact]
    public void ShapeStudio_SelectAll_ReadsRightAtOneAndMany()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();
        vm.AddShapeAt("rect", 0, 0, 50, 40, "0078D4", "Only");
        vm.SelectAll();
        Assert.Equal("Selected the only shape.", vm.StatusMessage);

        vm.AddShapeAt("rect", 0, 60, 50, 40, "0078D4", "Second");
        vm.SelectAll();
        Assert.Equal("Selected all 2 shapes.", vm.StatusMessage);
    }

    [Fact]
    public void ShapeStudio_SelectAll_OnTracedLineArt_PointsToTheShapesList()
    {
        var src = Read("MarkSmith.Core/ViewModels/ShapeStudio/ShapeDesignStudioViewModel.cs");
        var body = Between(src, "public void SelectAll()", "public void ClearSelection()");
        Assert.Contains("if (IsDense)", body);
        Assert.Contains("pick a line in the Shapes list", body);
        Assert.DoesNotContain("if (Shapes.Count == 0 || IsDense) return;", body);
    }

    // ---- Diagram Studio ----

    [Theory]
    [InlineData("private void OnDeleteAcceleratorInvoked", "private void OnDuplicateAcceleratorInvoked")]
    [InlineData("private void OnSelectAllAcceleratorInvoked", "private void OnRenameAcceleratorInvoked")]
    public void DiagramStudio_DeleteAndSelectAll_SkipChoiceControls(string start, string end)
    {
        var body = Between(Read(Diagram), start, end);
        const string guard = "if (ChoiceKeys.FocusIsInChoiceControl(XamlRoot, CanvasContainer)) return;";
        Assert.Contains(guard, body);
        Assert.True(body.IndexOf(guard, StringComparison.Ordinal) < body.IndexOf("args.Handled = true;", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagramStudio_DeleteWithNothingSelected_SaysSo()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMarkdown(string.Join(Environment.NewLine, "```mermaid", "flowchart TD", "    A[Start] --> B[End]", "```"), 0);
        vm.SelectedNodes.Clear();
        vm.SelectedNode = null;
        vm.SelectedConnector = null;

        vm.DeleteSelected();

        Assert.Equal("Nothing to delete: select a shape or connector on the canvas first.", vm.StatusText);
        Assert.Equal(2, vm.Nodes.Count);
    }

    [Fact]
    public void DiagramStudio_DeleteOnEmptyCanvas_SaysItIsEmpty()
    {
        var vm = new MermaidStudioViewModel();
        vm.Nodes.Clear();
        vm.SelectedNodes.Clear();
        vm.SelectedNode = null;
        vm.SelectedConnector = null;

        vm.DeleteSelected();

        Assert.Equal("Nothing to delete: the canvas is empty.", vm.StatusText);
    }

    // ---- SmartArt Studio ----

    [Fact]
    public void SmartArt_RootDelete_SkipsChoiceControls()
    {
        var src = Read(SmartArt);
        var root = Between(src, "private void OnRootGridKeyDown", "private void OnMarkdownTextBoxDoubleTapped");
        Assert.Contains("else if ((e.Key is VirtualKey.Delete or VirtualKey.Back) && !ChoiceKeys.FocusIsInChoiceControl(RootGrid.XamlRoot))", root);
        Assert.DoesNotContain("else if (e.Key is VirtualKey.Delete or VirtualKey.Back) { ViewModel.DeleteSelectedCommand", root);
    }
}
