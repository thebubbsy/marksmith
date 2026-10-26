using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #291 (Studio shortcut isolation, XY navigation, and contextual clipboard sweep):
// 1. History Window: S (star), F2 (rename), and Delete (delete version) check !inChoice, preventing accidental
//    version modifications when focus is in DiffModeSegment, DiffChangeStepperControls, or other choice controls.
// 2. History Window: DiffChangeStepperControls has XYFocusKeyboardNavigation="Enabled" and TabFocusNavigation="Once",
//    allowing arrow traversal across hunk stepping controls in a single Tab stop.
// 3. SmartArt Studio: Ctrl+C in LayoutList or QuickLayoutCarouselList copies the selected/focused layout's name
//    (and reports "Copied layout name \"...\" to clipboard.") instead of copying outline markdown.
// 4. SmartArt Studio: Ctrl+D (duplicate node) and F2 (rename node) check !inChoice (ChoiceKeys.FocusIsInChoiceControl(RootGrid.XamlRoot)),
//    protecting outline items from mutating while browsing layouts or color schemes.
// 5. MindMap Galaxy: canvasFocused incorporates !inChoice (ChoiceKeys.FocusIsInChoiceControl), ensuring single-letter
//    commands (N, F, Space, Enter) and canvas keys never trigger when focus is in choice controls.
// 6. MindMap Galaxy: ZoomControlCluster groups header zoom controls with XYFocusKeyboardNavigation="Enabled" and
//    TabFocusNavigation="Once", reducing header tabbing by 4 stops with smooth arrow cycling.
public class Run291HistorySmartArtAndGalaxyPolishTests
{
    private const string HistoryCs = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml.cs";
    private const string HistoryXaml = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml";
    private const string SmartArtCs = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string MindMapCs = "MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml.cs";
    private const string MindMapXaml = "MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml";

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
    public void History_WindowShortcuts_GatedByInChoice()
    {
        var cs = Read(HistoryCs);
        var body = Between(cs, "private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)", "private void OnSearchBoxKeyDown");

        Assert.Contains("bool inChoice = ChoiceKeys.FocusIsInChoiceControl(RootGrid.XamlRoot);", body);
        Assert.Contains("else if (!inTextBox && !inFileList && !inChoice && !isCtrl && e.Key == VirtualKey.S && (focusedVersion ?? _vm.Selected) is { } toStar)", body);
        Assert.Contains("else if (!inTextBox && !inFileList && !inChoice && e.Key == VirtualKey.F2 && (focusedVersion ?? _vm.Selected) is { } toRename)", body);
        Assert.Contains("else if (!inTextBox && !inFileList && !inChoice && e.Key == VirtualKey.Delete && (focusedVersion ?? _vm.Selected) is { } toDelete)", body);
    }

    [Fact]
    public void History_DiffChangeStepperControls_HasXYFocusAndSingleTabStop()
    {
        var panel = Named(HistoryXaml, "DiffChangeStepperControls");
        Assert.Equal("Enabled", (string?)panel.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)panel.Attribute("TabFocusNavigation"));
    }

    [Fact]
    public void SmartArt_GalleryCtrlC_CopiesLayoutName()
    {
        var cs = Read(SmartArtCs);
        var rootKeyDown = Between(cs, "private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs e)", "private void OnMarkdownTextBoxDoubleTapped");
        var listKeyDown = Between(cs, "private void OnLayoutListKeyDown(object sender, KeyRoutedEventArgs e)", "private void OnLayoutTilePointerEntered");

        Assert.Contains("Copied layout name \\\"{layout.DisplayName}\\\" to clipboard.", rootKeyDown);
        Assert.Contains("if (ctrlDown && !shiftDown && e.Key == VirtualKey.C)", listKeyDown);
        Assert.Contains("Copied layout name \\\"{layoutName}\\\" to clipboard.", listKeyDown);
    }

    [Fact]
    public void SmartArt_DuplicateAndRename_GatedByInChoice()
    {
        var cs = Read(SmartArtCs);
        var body = Between(cs, "private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs e)", "private void OnMarkdownTextBoxDoubleTapped");

        Assert.Contains("else if (ctrl && e.Key == VirtualKey.D && !inChoice)", body);
        Assert.Contains("else if (e.Key == VirtualKey.F2 && !inChoice)", body);
    }

    [Fact]
    public void MindMapGalaxy_CanvasFocused_IncludesInChoiceGuard()
    {
        var cs = Read(MindMapCs);
        var body = Between(cs, "private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)", "switch (e.Key)");

        Assert.Contains("bool inChoice = ChoiceKeys.FocusIsInChoiceControl(this.Content.XamlRoot);", body);
        Assert.Contains("bool canvasFocused = (focused is null || ReferenceEquals(focused, GalaxyCanvas)) && !inChoice;", body);
    }

    [Fact]
    public void MindMapGalaxy_ZoomControlCluster_HasXYFocusAndSingleTabStop()
    {
        var cluster = Named(MindMapXaml, "ZoomControlCluster");
        Assert.Equal("Enabled", (string?)cluster.Attribute("XYFocusKeyboardNavigation"));
        Assert.Equal("Once", (string?)cluster.Attribute("TabFocusNavigation"));
    }
}
