using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #292 (Studio polish, keyboard shortcut isolation, and XY-navigation clusters):
// 1. Mermaid Canvas: ZoomControlCluster groups canvas overlay zoom controls with XYFocusKeyboardNavigation="Enabled"
//    and TabFocusNavigation="Once", enabling fluid arrow navigation across zoom operations in 1 Tab stop.
// 2. Mermaid Diagram Studio: DiagramActionCluster groups Export SVG, Copy SVG, and Sync to Markdown with
//    XYFocusKeyboardNavigation="Enabled" and TabFocusNavigation="Once".
// 3. Mermaid Diagram Studio: OnCopyAcceleratorInvoked (Ctrl+C), OnPasteAcceleratorInvoked (Ctrl+V), and
//    OnRenameAcceleratorInvoked (F2) are gated with ChoiceKeys.FocusIsInChoiceControl(XamlRoot, CanvasContainer),
//    preventing accidental canvas mutations or canvas copies when interacting with choice controls.
// 4. Mermaid Node Palette: Palette items context menu offers "Copy shape name" (Ctrl+C), and OnPaletteListViewKeyDown
//    contextually copies the focused shape's name with polite live announcement on Ctrl+C.
// 5. MindMap Galaxy: OnTagPillsPreviewKeyDown returns focus to GalaxyCanvas on Escape when SelectedTagFilter is null,
//    preventing Escape in the tag strip from accidentally clearing active search or focus mode.
// 6. OCR Studio: PageNavigationCluster and CtaActionCluster feature XYFocusKeyboardNavigation="Enabled" and
//    TabFocusNavigation="Once", enabling smooth arrow navigation across page steppers and action buttons in single Tab stops.
// 7. Main Window: StatusSecondaryCopyPills features XYFocusKeyboardNavigation="Enabled" and TabFocusNavigation="Once",
//    allowing arrow traversal across secondary status copy buttons while reducing tab navigation to 1 single stop.
public class Run292StudioPolishAndKeyboardNavigationTests
{
    private const string MermaidCanvasXaml = "MarkSmith.Desktop/Views/Mermaid/MermaidCanvasControl.xaml";
    private const string MermaidStudioXaml = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml";
    private const string MermaidStudioCs = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml.cs";
    private const string NodePaletteCs = "MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml.cs";
    private const string MindMapCs = "MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml.cs";
    private const string OcrStudioXaml = "MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml";
    private const string MainWindowXaml = "MarkSmith.Desktop/MainWindow.xaml";

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

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

    private static XElement Named(string xamlFile, string name) =>
        XDocument.Parse(Read(xamlFile)).Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"missing: {start}");
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"missing: {end}");
        return text.Substring(a, b - a);
    }

    [Fact]
    public void MermaidCanvas_ZoomControlCluster_HasXYFocusAndSingleTabStop()
    {
        var cluster = Named(MermaidCanvasXaml, "ZoomControlCluster");
        Assert.Equal("Enabled", (string?)cluster.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cluster.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void MermaidStudio_DiagramActionCluster_HasXYFocusAndSingleTabStop()
    {
        var cluster = Named(MermaidStudioXaml, "DiagramActionCluster");
        Assert.Equal("Enabled", (string?)cluster.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cluster.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void MermaidStudio_CopyPasteRename_GatedByChoiceKeys()
    {
        var cs = Read(MermaidStudioCs);
        var copyBody = Between(cs, "private void OnCopyAcceleratorInvoked", "private void OnCopySvgAcceleratorInvoked");
        var pasteBody = Between(cs, "private void OnPasteAcceleratorInvoked", "private void OnSelectAllAcceleratorInvoked");
        var renameBody = Between(cs, "private void OnRenameAcceleratorInvoked", "private void OnFindShapesAcceleratorInvoked");

        Assert.Contains("if (ChoiceKeys.FocusIsInChoiceControl(XamlRoot, CanvasContainer)) return;", copyBody);
        Assert.Contains("if (ChoiceKeys.FocusIsInChoiceControl(XamlRoot, CanvasContainer)) return;", pasteBody);
        Assert.Contains("if (ChoiceKeys.FocusIsInChoiceControl(XamlRoot, CanvasContainer)) return;", renameBody);
    }

    [Fact]
    public void NodePalette_ShowContextMenu_And_KeyDown_SupportCopyShapeName()
    {
        var cs = Read(NodePaletteCs);
        var menuBody = Between(cs, "private void ShowPaletteItemContextMenu", "private void OnPaletteListViewKeyDown");
        var keyBody = Between(cs, "private void OnPaletteListViewKeyDown", "private void OnCategoryPillClick");

        Assert.Contains("Text = \"Copy shape name\"", menuBody);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+C\"", menuBody);
        Assert.Contains("ClipboardWriter.TrySetTextAsync(item.DisplayName", menuBody);

        Assert.Contains("if (ctrl && !shift && e.Key == Windows.System.VirtualKey.C)", keyBody);
        Assert.Contains("ClipboardWriter.TrySetTextAsync(item.DisplayName", keyBody);
        Assert.Contains("LiveAnnounce.RaiseIfChanged(PaletteCountText", keyBody);
    }

    [Fact]
    public void MindMapGalaxy_TagPillsEscape_ReturnsFocusToCanvasWhenFilterNull()
    {
        var cs = Read(MindMapCs);
        var body = Between(cs, "private void OnTagPillsPreviewKeyDown(object sender, KeyRoutedEventArgs e)", "private readonly HashSet<Button> _tagPills");

        Assert.Contains("if (ViewModel.SelectedTagFilter is not null)", body);
        Assert.Contains("AllTagsPill?.Focus(FocusState.Programmatic);", body);
        Assert.Contains("GalaxyCanvas.Focus(FocusState.Programmatic);", body);
        Assert.Contains("e.Handled = true;", body);
    }

    [Fact]
    public void OcrStudio_Clusters_HaveXYFocusAndSingleTabStop()
    {
        var pageNav = Named(OcrStudioXaml, "PageNavigationCluster");
        Assert.Equal("Enabled", (string?)pageNav.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)pageNav.Attribute("TabFocusNavigation"));

        var cta = Named(OcrStudioXaml, "CtaActionCluster");
        Assert.Equal("Enabled", (string?)cta.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cta.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void MainWindow_StatusSecondaryCopyPills_HasXYFocusAndSingleTabStop()
    {
        var panel = Named(MainWindowXaml, "StatusSecondaryCopyPills");
        Assert.Equal("Enabled", (string?)panel.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)panel.Attribute("TabFocusNavigation"));
    }
}
