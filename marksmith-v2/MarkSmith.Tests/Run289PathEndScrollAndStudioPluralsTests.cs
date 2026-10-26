using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #289:
// 1. Extended TextBoxEndScroll to path and URL text boxes across the main window, settings, and studios:
//    RunningDocPathTextBox, WatchFolderTextBox, BrandLogoPathTextBox, CustomFontPathTextBox,
//    CloudSubfolderBox, and FuseImagePathBox.
// 2. Swept remaining raw interpolation plurals across MindMap Galaxy, Shape Studio, Mermaid Canvas,
//    and TextSearch math diagnostics using canonical CountText.Of sentences.
public class Run289PathEndScrollAndStudioPluralsTests
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

    private const string MainXaml = "MarkSmith.Desktop/MainWindow.xaml";
    private const string MainCs = "MarkSmith.Desktop/MainWindow.xaml.cs";
    private const string SettingsXaml = "MarkSmith.Desktop/Views/SettingsView.xaml";
    private const string SettingsCs = "MarkSmith.Desktop/Views/SettingsView.xaml.cs";
    private const string ShapeStudioCs = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string MindMapCs = "MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml.cs";
    private const string MermaidCanvasCs = "MarkSmith.Desktop/Views/Mermaid/MermaidCanvasControl.xaml.cs";
    private const string TextSearchCs = "MarkSmith.Core/Services/TextSearch.cs";

    [Fact]
    public void MainWindow_PathBoxes_AreDeclaredAndAttachedToEndScroll()
    {
        var xaml = Read(MainXaml);
        Assert.Contains("x:Name=\"RunningDocPathTextBox\"", xaml);
        Assert.Contains("x:Name=\"WatchFolderTextBox\"", xaml);
        Assert.Contains("x:Name=\"BrandLogoPathTextBox\"", xaml);
        Assert.Contains("x:Name=\"CustomFontPathTextBox\"", xaml);

        var cs = Read(MainCs);
        Assert.Contains("TextBoxEndScroll.Attach(RunningDocPathTextBox);", cs);
        Assert.Contains("TextBoxEndScroll.Attach(WatchFolderTextBox);", cs);
        Assert.Contains("TextBoxEndScroll.Attach(BrandLogoPathTextBox);", cs);
        Assert.Contains("TextBoxEndScroll.Attach(CustomFontPathTextBox);", cs);
    }

    [Fact]
    public void SettingsView_CloudSubfolderBox_IsDeclaredAndAttachedToEndScroll()
    {
        var xaml = Read(SettingsXaml);
        Assert.Contains("x:Name=\"CloudSubfolderBox\"", xaml);

        var cs = Read(SettingsCs);
        Assert.Contains("TextBoxEndScroll.Attach(CloudSubfolderBox);", cs);
    }

    [Fact]
    public void ShapeDesignStudio_FuseImagePathBox_IsAttachedToEndScroll()
    {
        var cs = Read(ShapeStudioCs);
        Assert.Contains("TextBoxEndScroll.Attach(FuseImagePathBox);", cs);
    }

    [Theory]
    [InlineData(0, "node", "0 nodes")]
    [InlineData(1, "node", "1 node")]
    [InlineData(2, "node", "2 nodes")]
    [InlineData(1204, "node", "1,204 nodes")]
    [InlineData(1, "shape", "1 shape")]
    [InlineData(15, "shape", "15 shapes")]
    [InlineData(1, "unclosed brace", "1 unclosed brace")]
    [InlineData(3, "unclosed brace", "3 unclosed braces")]
    [InlineData(1, "wikilink", "1 wikilink")]
    [InlineData(4, "wikilink", "4 wikilinks")]
    [InlineData(1, "asset", "1 asset")]
    [InlineData(2, "asset", "2 assets")]
    public void CountText_FormatsNounsAndGroupedNumbers(int count, string noun, string expected)
    {
        Assert.Equal(expected, CountText.Of(count, noun));
    }

    [Fact]
    public void MindMapGalaxy_UsesCountText_ForMultiSelectSummariesAndActions()
    {
        var cs = Read(MindMapCs);
        Assert.Contains("CountText.Of(selected.Count, \"node\")", cs);
        Assert.Contains("CountText.Of(sel.Count, \"node\")", cs);
        Assert.DoesNotContain("PushUndo($\"Align {selected.Count} nodes\")", cs);
        Assert.DoesNotContain("PushUndo($\"Distribute {selected.Count} nodes\")", cs);
        Assert.DoesNotContain("PushUndo($\"Delete {selected.Count} nodes\")", cs);
        Assert.DoesNotContain("StatusMessage = $\"Deleted {selected.Count} nodes\"", cs);
    }

    [Fact]
    public void ShapeDesignStudio_UsesCountText_ForMultiSelectionBadgesAndActions()
    {
        var cs = Read(ShapeStudioCs);
        Assert.Contains("CountText.Of(selected.Count, \"shape\")", cs);
        Assert.Contains("CountText.Of(ViewModel.SelectionCount, \"shape\")", cs);
        Assert.DoesNotContain("Set {targetSpacing} px spacing across {selected.Count} shapes", cs);
        Assert.DoesNotContain("Rotated group of {_initialGroupTransforms.Count} shapes", cs);
        Assert.DoesNotContain("Reset rotation on {selected.Count} shapes", cs);
        Assert.DoesNotContain("Flipped {selected.Count} shapes horizontally", cs);
    }

    [Fact]
    public void MermaidCanvas_UsesCountText_ForDeleteSelected()
    {
        var cs = Read(MermaidCanvasCs);
        Assert.Contains("Delete {CountText.Of(vm.SelectedNodes.Count, \"shape\")}", cs);
        Assert.DoesNotContain("Delete {vm.SelectedNodes.Count} shapes", cs);
    }

    [Fact]
    public void TextSearch_EliminatedBraceParentheses_UsesCountText()
    {
        var cs = Read(TextSearchCs);
        Assert.DoesNotContain("brace(s)", cs);
        Assert.Contains("CountText.Of(unclosed, \"unclosed brace\")", cs);
        Assert.Contains("CountText.Of(unbalancedBraces, \"unbalanced brace\")", cs);
    }
}
