using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #290:
// 1. Shape Studio: exempt lists, sliders, combo boxes and pill groups from Delete, Backspace, Ctrl+A,
//    Ctrl+D and reorder shortcuts in the window KeyDown. Canvas selection/deletion shortcuts only fire
//    when the canvas is focused or for layer deletion in ShapesList.
// 2. Shape Studio: PaletteGrid (shape tools) is an arrow-navigated grid (XYFocusKeyboardNavigation="Enabled")
//    with a single Tab stop (TabFocusNavigation="Once").
// 3. Shape Studio: PresetList diagram presets is a single Tab stop (TabFocusNavigation="Once").
// 4. MindMap Galaxy: tag filter pills container is an arrow-navigated strip (XYFocusKeyboardNavigation="Enabled")
//    with a single Tab stop (TabFocusNavigation="Once").
// 5. MindMap Galaxy: Ctrl+D duplicate node requires canvasFocused, preventing accidental node duplication
//    when adjusting outside controls.
// 6. SmartArt Studio: QuickLayoutCarouselList horizontal layout carousel is a single Tab stop (TabFocusNavigation="Once").
public class Run290KeyboardIsolationAndNavigationTests
{
    private const string ShapeStudioCs = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string ShapeStudioXaml = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml";
    private const string MindMapCs = "MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml.cs";
    private const string MindMapXaml = "MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml";
    private const string SmartArtXaml = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml";

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
    public void ShapeStudio_FocusOwnsStudioKeys_GuardsSlidersListsCombosAndPills()
    {
        var cs = Read(ShapeStudioCs);
        var method = Between(cs, "private bool FocusOwnsStudioKeys(DependencyObject? focused, bool allowShapesList = false)", "private static bool IsInsideNumberBox");
        Assert.Contains("IsCanvasFocused(focused)", method);
        Assert.Contains("allowShapesList && IsWithin(focused, ShapesList)", method);
        Assert.Contains("Slider", method);
        Assert.Contains("ListViewItem", method);
        Assert.Contains("ListViewBase", method);
        Assert.Contains("ComboBox", method);
        Assert.Contains("ComboBoxItem", method);
        Assert.Contains("XYFocusKeyboardNavigationMode.Enabled", method);
    }

    [Fact]
    public void ShapeStudio_KeyDown_ExemptsStudioKeys_FromDeleteBackspaceAndCtrlA()
    {
        var cs = Read(ShapeStudioCs);
        var preview = Between(cs, "private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)", "private static bool IsDown(VirtualKey key)");

        // Ctrl+A is protected
        Assert.Contains("case VirtualKey.A when ctrl:\n                    if (FocusOwnsStudioKeys(focused as DependencyObject)) return;", preview);

        // Ctrl+D is protected
        Assert.Contains("case VirtualKey.D when ctrl && ViewModel.HasSelection:\n                    if (FocusOwnsStudioKeys(focused as DependencyObject)) return;", preview);

        // Delete/Back is protected with ShapesList allowance
        Assert.Contains("case (VirtualKey.Delete or VirtualKey.Back) when ViewModel.HasSelection:\n                    if (FocusOwnsStudioKeys(focused as DependencyObject, allowShapesList: true)) return;", preview);

        // Reordering keys are protected
        Assert.Contains("case (VirtualKey)221 when ctrl && shift && ViewModel.HasSelection: // Ctrl+Shift+]\n                    if (FocusOwnsStudioKeys(focused as DependencyObject)) return;", preview);
        Assert.Contains("case (VirtualKey)219 when ctrl && shift && ViewModel.HasSelection: // Ctrl+Shift+[\n                    if (FocusOwnsStudioKeys(focused as DependencyObject)) return;", preview);
    }

    [Fact]
    public void ShapeStudio_PaletteGrid_HasXYFocusAndSingleTabStop()
    {
        var grid = Named(ShapeStudioXaml, "PaletteGrid");
        Assert.Equal("Enabled", (string?)grid.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)grid.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void ShapeStudio_PresetList_HasSingleTabStop()
    {
        var list = Named(ShapeStudioXaml, "PresetList");
        Assert.Equal("Once", (string?)list.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void MindMapGalaxy_TagPills_HasXYFocusAndSingleTabStop()
    {
        var xaml = Read(MindMapXaml);
        Assert.Contains("XYFocusKeyboardNavigation=\"Enabled\" TabFocusNavigation=\"Once\"", xaml);
    }

    [Fact]
    public void MindMapGalaxy_CtrlD_RequiresCanvasFocused()
    {
        var cs = Read(MindMapCs);
        Assert.Contains("case Windows.System.VirtualKey.D when ctrl && canvasFocused:", cs);
    }

    [Fact]
    public void SmartArtStudio_QuickLayoutCarousel_HasSingleTabStop()
    {
        var list = Named(SmartArtXaml, "QuickLayoutCarouselList");
        Assert.Equal("Once", (string?)list.Attribute("TabFocusNavigation"));
    }
}
