using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #246: the last mouse-only readouts in Shape Studio and Mind Map (selection bounds pill,
// Unsaved badge, node and link counts) are buttons that open their menu on click and from
// Shift+F10, instead of moving shapes, saving, re-selecting or copying on a click or double-click.
// SmartArt's outline stats menu is grouped into submenus, and Mind Map's breadcrumb bar follows
// the map's palette.
public class Run246StudioPillsAndBreadcrumbTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

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

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative));

    private static string ShapeXamlPath => Path.Combine("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
    private static string MindXamlPath => Path.Combine("Views", "MindMap", "MindMapGalaxyWindow.xaml");
    private static string SmartArtXamlPath => Path.Combine("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");

    private static XElement Named(string xamlPath, string name) =>
        XDocument.Parse(Read(xamlPath)).Descendants().Single(e => (string?)e.Attribute(XamlNs + "Name") == name);

    private static string? Attr(XElement e, string localName) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static string Method(string code, string name)
    {
        var decl = Regex.Match(code, $@"^\s*(private|protected|public|internal)\b[^\n;=]*\s{Regex.Escape(name)}\(", RegexOptions.Multiline);
        Assert.True(decl.Success, $"{name} not found");
        var open = code.IndexOf('{', decl.Index);
        var arrow = code.IndexOf("=>", decl.Index, StringComparison.Ordinal);
        if (arrow >= 0 && (open < 0 || arrow < open)) return code[arrow..code.IndexOf(';', arrow)];
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        throw new InvalidOperationException($"{name} is unbalanced");
    }

    private static string BridgeTable(string code)
    {
        var start = code.IndexOf("KeyboardContextMenu.BridgeAll(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return code[start..code.IndexOf(");", start, StringComparison.Ordinal)];
    }

    [Fact]
    public void ShapeStudio_SelectionBoundsPill_IsAButtonWhoseClickOpensTheMenu()
    {
        var pill = Named(ShapeXamlPath, "SelectionBoundsPill");
        Assert.Equal("Button", pill.Name.LocalName);
        Assert.Null(pill.Attribute("Tapped"));
        Assert.Null(pill.Attribute("DoubleTapped"));
        Assert.Equal("OnSelectionBoundsPillRightTapped", (string?)pill.Attribute("RightTapped"));
        Assert.NotNull(Attr(pill, "AutomationProperties.Name"));
        var menu = Named(ShapeXamlPath, "SelectionBoundsFlyout");
        Assert.Equal("Button.Flyout", menu.Parent!.Name.LocalName);
        var tip = Attr(pill, "ToolTipService.ToolTip")!;
        Assert.Contains("Shift+F10", tip);
        Assert.DoesNotContain("double-click", tip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Click to center", tip);
    }

    [Fact]
    public void ShapeStudio_SelectionBoundsPill_ClickNoLongerMovesShapes_AndIsBridged()
    {
        var code = Read(ShapeXamlPath + ".cs");
        Assert.DoesNotContain("OnSelectionBoundsPillTapped", code);
        Assert.DoesNotContain("OnSelectionBoundsPillDoubleTapped", code);
        Assert.Contains("(SelectionBoundsPill, OnSelectionBoundsPillRightTapped)", BridgeTable(code));
        Assert.Contains("KeyboardContextMenu.PointIn(e, SelectionBoundsPill)", Method(code, "OnSelectionBoundsPillRightTapped"));
        Assert.Contains("AutomationProperties.SetName(SelectionBoundsPill, $\"Selection bounds: {SelectionBoundsText.Text}\")",
            Method(code, "UpdateSelectionBoundsPill"));
        // Named from the start, not only after the first selection change.
        Assert.Contains("this.RootGrid.Loaded += (_, _) => UpdateSelectionBoundsPill();", code);
    }

    [Theory]
    [InlineData("UnsavedBadge", "Button", "OnUnsavedBadgeClick", "OnUnsavedBadgeRightTapped")]
    [InlineData("NodesCountButton", "HyperlinkButton", "OnNodesCountClick", "OnNodesCountRightTapped")]
    [InlineData("LinksCountButton", "HyperlinkButton", "OnLinksCountClick", "OnLinksCountRightTapped")]
    public void MindMap_Readouts_AreButtons_ClickOpensTheSameMenu(string name, string type, string click, string rightTapped)
    {
        var el = Named(MindXamlPath, name);
        Assert.Equal(type, el.Name.LocalName);
        Assert.Equal(click, (string?)el.Attribute("Click"));
        Assert.Equal(rightTapped, (string?)el.Attribute("RightTapped"));
        Assert.Null(el.Attribute("DoubleTapped"));
        Assert.NotNull(Attr(el, "AutomationProperties.Name"));
        var tip = Attr(el, "ToolTipService.ToolTip")!;
        Assert.Contains("Shift+F10", tip);
        Assert.DoesNotContain("Double-click", tip, StringComparison.OrdinalIgnoreCase);

        var code = Read(MindXamlPath + ".cs");
        Assert.Contains($"({name}, {rightTapped})", BridgeTable(code));
        Assert.Contains($"KeyboardContextMenu.Invoke({name}, {rightTapped})", Method(code, click));
        Assert.Contains($"KeyboardContextMenu.PointIn(e, {name})", Method(code, rightTapped));
    }

    [Fact]
    public void MindMap_CountReadouts_KeepTheirBoundText_AndNameFollowsIt()
    {
        foreach (var (button, text) in new[] { ("NodesCountButton", "NodesCountText"), ("LinksCountButton", "LinksCountText") })
        {
            var el = Named(MindXamlPath, button);
            Assert.Equal($"{{Binding {text}}}", Attr(el, "AutomationProperties.Name"));
            var inner = el.Elements().Single();
            Assert.Equal(text, (string?)inner.Attribute(XamlNs + "Name"));
            Assert.Equal($"{{Binding {text}}}", (string?)inner.Attribute("Text"));
        }
    }

    [Fact]
    public void MindMap_OldDoubleClickActions_AreGone()
    {
        var code = Read(MindXamlPath + ".cs");
        Assert.DoesNotContain("OnUnsavedBadgeDoubleTapped", code);
        Assert.DoesNotContain("OnNodesCountDoubleTapped", code);
        Assert.DoesNotContain("OnLinksCountDoubleTapped", code);
        Assert.DoesNotContain("_nodeCycleMode", code);
    }

    [Fact]
    public void MindMap_StatusBarDoubleClick_SkipsTheCountButtons()
    {
        var handler = Method(Read(MindXamlPath + ".cs"), "OnStatusBarDoubleTapped");
        var guard = handler.IndexOf("is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return;", StringComparison.Ordinal);
        Assert.True(guard > 0, "a double-click on a count button doesn't also fit the map");
        Assert.True(guard < handler.IndexOf("OnFitToWindowClick", StringComparison.Ordinal));
    }

    [Fact]
    public void MindMap_UnsavedBadge_MovesFocusOffBeforeItIsLost()
    {
        var code = Read(MindXamlPath + ".cs");
        var move = Method(code, "MoveFocusOffUnsavedBadge");
        Assert.Contains("UnsavedBadge.Visibility == Visibility.Visible) return;", move);
        // Only when focus was on the badge (or lost with it): never steal focus from elsewhere.
        Assert.Contains("if (focused is not null && !ReferenceEquals(focused, UnsavedBadge)) return;", move);
        Assert.Contains("GalaxyCanvas.Focus(FocusState.Programmatic);", move);
        Assert.Contains("UnsavedBadge.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => MoveFocusOffUnsavedBadge());", code);
        Assert.Contains("menu.Closed += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, MoveFocusOffUnsavedBadge);",
            Method(code, "OnUnsavedBadgeRightTapped"));
    }

    [Fact]
    public void MindMap_Breadcrumbs_FollowThePalette_AndDoOneThingPerClick()
    {
        var code = Read(MindXamlPath + ".cs");
        var crumbs = Method(code, "UpdateBreadcrumbs");
        Assert.Contains("_breadcrumbBar.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xEE, card.R, card.G, card.B));", crumbs);
        Assert.DoesNotContain("Microsoft.UI.Colors.LightGreen", crumbs);
        Assert.DoesNotContain("Microsoft.UI.Colors.White", crumbs);
        Assert.DoesNotContain("DoubleTapped", crumbs);
        Assert.DoesNotContain("Double-click", crumbs);
        Assert.Contains("AutomationProperties.SetName(focusToggle, focusOn ? \"Focus mode: on\" : \"Focus mode: off\");", crumbs);
        Assert.DoesNotContain("\"Focus (F)\"", crumbs);
        Assert.Contains("UpdateBreadcrumbs();", Method(code, "ApplyPalette"));
    }

    [Fact]
    public void SmartArt_OutlineStatsMenu_IsGroupedIntoSubmenus()
    {
        var menu = Named(SmartArtXamlPath, "OutlineStatsFlyout");
        var top = menu.Elements().ToList();
        var subs = top.Where(e => e.Name.LocalName == "MenuFlyoutSubItem").Select(e => (string?)e.Attribute("Text")).ToList();
        Assert.Equal(new[] { "Copy", "Restructure", "Clean up", "Suggest", "Diagnostics" }, subs);
        // Short enough to fit a 900 px screen: two direct items, five submenus, two separators.
        Assert.True(top.Count <= 10, $"{top.Count} top-level entries");
        // Every action is still in the menu.
        var all = menu.Descendants().Where(e => e.Name.LocalName == "MenuFlyoutItem").Select(e => (string?)e.Attribute("Click")).ToList();
        foreach (var click in new[] { "OnSelectRootNodeClick", "OnCopyOutlineSummaryClick", "OnPromoteAllClick", "OnSortOutlineAscendingClick",
                     "OnTrimTrailingWhitespaceClick", "OnNormalizeIndentationClick", "OnRecommendLayoutsOutlineClick", "OnRecommendIconsOutlineClick" })
        {
            Assert.Contains(click, all);
        }
    }

    [Fact]
    public void SmartArt_DiagnosticsSubmenu_IsNamedForItself()
    {
        var diag = Named(SmartArtXamlPath, "OutlineStatsFlyout").Elements()
            .Single(e => e.Name.LocalName == "MenuFlyoutSubItem" && (string?)e.Attribute("Text") == "Diagnostics");
        // It carried the pill's name and tooltip, so a screen reader announced "Outline stats".
        Assert.Null(Attr(diag, "AutomationProperties.Name"));
        Assert.Null(Attr(diag, "ToolTipService.ToolTip"));
    }
}
