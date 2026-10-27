using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #293 (Shape Studio action/status clusters, F2 and preset copy isolation, SmartArt preview zoom
// and action clusters, MainWindow primary copy pills cluster, status output cluster, and Theme Builder XY navigation):
// 1. Shape Studio: ShapeStudioActionCluster groups Clear, Export, and Insert into document with
//    XYFocusKeyboardNavigation="Enabled" and TabFocusNavigation="Once".
// 2. Shape Studio: ShapeStudioStatusCluster groups Selection summary and Zoom with
//    XYFocusKeyboardNavigation="Enabled" and TabFocusNavigation="Once".
// 3. Shape Studio: F2 canvas label editor shortcut is gated by !inChoice, preventing accidental canvas edits
//    while in choice controls; OnRootPreviewKeyDown contextually copies focused preset name on Ctrl+C;
//    OnPresetListRightTapped provides a dedicated "Copy preset name" menu item with Ctrl+C accelerator.
// 4. SmartArt Studio: PreviewZoomControlCluster groups zoom out, reset, in, and fit with
//    XYFocusKeyboardNavigation="Enabled" and TabFocusNavigation="Once".
// 5. SmartArt Studio: SmartArtActionCluster groups Undo, Redo, and Insert into document with
//    XYFocusKeyboardNavigation="Enabled" and TabFocusNavigation="Once".
// 6. Main Window: StatusPrimaryCopyPills groups Copy MD, Copy TOC, and Copy Links with
//    XYFocusKeyboardNavigation="Enabled" and TabFocusNavigation="Once".
// 7. Main Window: StatusOutputLinks features XYFocusKeyboardNavigation="Enabled" and
//    TabFocusNavigation="Once", allowing fluid arrow cycling between Open and Show in folder in 1 Tab stop.
// 8. Main Window: Theme Builder OnCreateThemeClick configures rows with
//    XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled and TabFocusNavigation = KeyboardNavigationMode.Cycle.
public class Run293StudioPolishAndKeyboardNavigationTests
{
    private const string ShapeStudioXaml = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml";
    private const string ShapeStudioCs = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string SmartArtStudioXaml = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml";
    private const string MainWindowXaml = "MarkSmith.Desktop/MainWindow.xaml";
    private const string MainWindowCs = "MarkSmith.Desktop/MainWindow.xaml.cs";

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
    public void ShapeStudio_ActionCluster_HasXYFocusAndSingleTabStop()
    {
        var cluster = Named(ShapeStudioXaml, "ShapeStudioActionCluster");
        Assert.Equal("Enabled", (string?)cluster.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cluster.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void ShapeStudio_StatusCluster_HasXYFocusAndSingleTabStop()
    {
        var cluster = Named(ShapeStudioXaml, "ShapeStudioStatusCluster");
        Assert.Equal("Enabled", (string?)cluster.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cluster.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void ShapeStudio_F2_And_CtrlC_GatedAndContextual()
    {
        var cs = Read(ShapeStudioCs);
        Assert.Contains("case VirtualKey.F2 when !inChoice && ViewModel.SelectedShape is { IsLine: false }:", cs);

        var copyBody = Between(cs, "if (ctrl && e.Key == VirtualKey.C && !shift)", "if (ctrl && e.Key == VirtualKey.V && !shift)");
        Assert.Contains("if (FocusedPresetItem() is { } preset)", copyBody);
        Assert.Contains("CopyAndReport(preset.Name", copyBody);
        Assert.Contains("if (inChoice) return;", copyBody);

        var menuBody = Between(cs, "private void OnPresetListRightTapped", "private void OnPaletteGridDoubleTapped");
        Assert.Contains("ShapeMenuItem(\"Copy preset name\"", menuBody);
        Assert.Contains("\"Ctrl+C\"", menuBody);
    }

    [Fact]
    public void SmartArtStudio_PreviewZoomCluster_HasXYFocusAndSingleTabStop()
    {
        var cluster = Named(SmartArtStudioXaml, "PreviewZoomControlCluster");
        Assert.Equal("Enabled", (string?)cluster.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cluster.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void SmartArtStudio_ActionCluster_HasXYFocusAndSingleTabStop()
    {
        var cluster = Named(SmartArtStudioXaml, "SmartArtActionCluster");
        Assert.Equal("Enabled", (string?)cluster.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cluster.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void MainWindow_StatusPrimaryCopyPills_HasXYFocusAndSingleTabStop()
    {
        var cluster = Named(MainWindowXaml, "StatusPrimaryCopyPills");
        Assert.Equal("Enabled", (string?)cluster.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cluster.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void MainWindow_StatusOutputLinks_HasXYFocusAndSingleTabStop()
    {
        var cluster = Named(MainWindowXaml, "StatusOutputLinks");
        Assert.Equal("Enabled", (string?)cluster.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cluster.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void MainWindow_ThemeBuilder_Rows_HasXYFocusNavigation()
    {
        var cs = Read(MainWindowCs);
        var body = Between(cs, "private async void OnCreateThemeClick", "RefreshThemeNames(select: name);");
        Assert.Contains("XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled", body);
        Assert.Contains("TabFocusNavigation = KeyboardNavigationMode.Cycle", body);
    }
}
