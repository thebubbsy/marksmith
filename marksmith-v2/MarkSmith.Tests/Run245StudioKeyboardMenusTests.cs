using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #245: Shape Studio and Mind Map buttons whose double-click did a second, different thing
// (align then distribute, rotate three times, copy subtree Markdown behind the export menu, delete a
// second link) now do one thing per click. Their right-click menus open from Shift+F10 and the Menu
// key, and Shape Studio's window-level Shift+F10 no longer replaces a toolbar control's own menu
// with the canvas menu. SmartArt's outline stats / text volume pills are real buttons.
public class Run245StudioKeyboardMenusTests
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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { DesktopDir() }.Concat(parts).ToArray()));

    private static string ShapeXamlPath => Path.Combine("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml");
    private static string MindXamlPath => Path.Combine("Views", "MindMap", "MindMapGalaxyWindow.xaml");
    private static string SmartArtXamlPath => Path.Combine("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml");

    private static XDocument Xaml(string relative) => XDocument.Parse(Read(relative));
    private static string Code(string relative) => Read(relative + ".cs");

    private static string Method(string code, string name)
    {
        var decl = Regex.Match(code, $@"^\s*(private|protected|public|internal)\b[^\n;=]*\s{Regex.Escape(name)}\(", RegexOptions.Multiline);
        Assert.True(decl.Success, $"{name} not found");
        var open = code.IndexOf('{', decl.Index);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        throw new InvalidOperationException($"{name} is unbalanced");
    }

    private static readonly string[] ClickyTags = { "Button", "DropDownButton", "ToggleButton", "SplitButton" };

    private static string BridgeTable(string code)
    {
        var start = code.IndexOf("KeyboardContextMenu.BridgeAll(", StringComparison.Ordinal);
        Assert.True(start >= 0, "window bridges its right-click menus");
        return code[start..code.IndexOf(");", start, StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData("ShapeStudio")]
    [InlineData("MindMap")]
    public void ButtonsThatClick_HaveNoSecondDoubleClickAction(string studio)
    {
        var path = studio == "ShapeStudio" ? ShapeXamlPath : MindXamlPath;
        var offenders = Xaml(path).Descendants()
            .Where(e => ClickyTags.Contains(e.Name.LocalName) && e.Attribute("DoubleTapped") is not null)
            .Select(e => (string?)e.Attribute(XamlNs + "Name") ?? (string?)e.Attribute("DoubleTapped") ?? "?")
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void RemovedDoubleClickHandlers_AreGone()
    {
        var shape = Code(ShapeXamlPath);
        foreach (var gone in new[] { "OnAlignLeftDoubleTapped", "OnDistributeVerticalDoubleTapped", "OnRotateClockwiseDoubleTapped", "OnGridSnapButtonDoubleTapped", "OnCenterCanvasButtonDoubleTapped", "OnDistributeCanvasButtonDoubleTapped", "OnZoomButtonDoubleTapped" })
        {
            Assert.DoesNotContain(gone, shape);
        }
        var mind = Code(MindXamlPath);
        foreach (var gone in new[] { "OnExportDropDownDoubleTapped", "OnDeleteLinkButtonDoubleTapped", "OnArrangeDropDownDoubleTapped", "OnTagFilterButtonDoubleTapped", "OnCenterOnRootDoubleTapped" })
        {
            Assert.DoesNotContain(gone, mind);
        }
    }

    [Theory]
    [InlineData("ShapeStudio")]
    [InlineData("MindMap")]
    public void ButtonTooltips_NoLongerPromiseADoubleClick(string studio)
    {
        var path = studio == "ShapeStudio" ? ShapeXamlPath : MindXamlPath;
        var offenders = Xaml(path).Descendants()
            .Where(e => ClickyTags.Contains(e.Name.LocalName))
            .Select(e => (Name: (string?)e.Attribute(XamlNs + "Name"), Tip: (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "ToolTipService.ToolTip") ?? ""))
            // The linked-file button's tooltip describes the node's double-click, not its own.
            .Where(t => t.Tip.Contains("double-click", StringComparison.OrdinalIgnoreCase) && !t.Tip.Contains("double-click the node", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name ?? t.Tip)
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void MindMap_CenterOnRootAt100_MovedIntoItsMenu()
    {
        var menu = Method(Code(MindXamlPath), "OnCenterOnRootRightTapped");
        Assert.Contains("\"Center on root at 100%\"", menu);
        Assert.Contains("ViewModel.ZoomLevel = 1.0; CenterOnRoot();", menu);
        // The shortcut sits in the accelerator column, not in the label.
        Assert.Contains("Text = \"Reset zoom to 100%\", KeyboardAcceleratorTextOverride = \"Ctrl+1\"", menu);
        Assert.DoesNotContain("(100% · Ctrl+1)", Code(MindXamlPath));
    }

    [Fact]
    public void MindMap_ConnectionStyleTooltip_SaysWhatRightClickDoes()
    {
        var button = Xaml(MindXamlPath).Descendants().Single(e => (string?)e.Attribute(XamlNs + "Name") == "ConnectionStyleDropDownButton");
        var tip = (string)button.Attributes().First(a => a.Name.LocalName == "ToolTipService.ToolTip");
        Assert.Contains("Right-click to switch between solid, dashed and dotted lines", tip);
        // Its right-click cycles the dash style; Shift+F10 must not silently restyle every link.
        Assert.DoesNotContain("ConnectionStyleDropDownButton", BridgeTable(Code(MindXamlPath)));
    }

    [Theory]
    [InlineData("ShapeStudio")]
    [InlineData("MindMap")]
    public void EveryNamedFocusableControlWithAMenu_IsBridged(string studio)
    {
        var path = studio == "ShapeStudio" ? ShapeXamlPath : MindXamlPath;
        var table = BridgeTable(Code(path));
        var focusable = new[] { "Button", "DropDownButton", "ToggleButton", "ComboBox", "Slider", "ListView", "GridView" };
        var missing = Xaml(path).Descendants()
            .Where(e => focusable.Contains(e.Name.LocalName) && e.Attribute("RightTapped") is not null && e.Attribute(XamlNs + "Name") is not null)
            .Select(e => ((string)e.Attribute(XamlNs + "Name")!, (string)e.Attribute("RightTapped")!))
            .Where(p => p.Item1 != "ConnectionStyleDropDownButton")
            .Where(p => !table.Contains($"({p.Item1}, {p.Item2})"))
            .ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    [Fact]
    public void ShapeStudio_ToolStripButtons_AreNamedSoTheyCanBeBridged()
    {
        var unnamed = Xaml(ShapeXamlPath).Descendants()
            .Where(e => e.Name.LocalName == "Button" && e.Attribute("RightTapped") is not null && e.Attribute(XamlNs + "Name") is null)
            .Select(e => (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "AutomationProperties.Name") ?? "?")
            .ToList();
        Assert.True(unnamed.Count == 0, string.Join(", ", unnamed));
        var table = BridgeTable(Code(ShapeXamlPath));
        foreach (var name in new[] { "ToolUndoButton", "RotateLeftButton", "RotateRightButton", "DuplicateToolButton", "DeleteSelectionToolButton" })
        {
            Assert.Contains($"({name}, On", table);
        }
    }

    [Fact]
    public void MindMap_TagPillsAndFocusToggle_OpenTheirMenusFromTheKeyboard()
    {
        var code = Code(MindXamlPath);
        Assert.Contains("KeyboardContextMenu.BridgeOnLoad(sender, OnDynamicTagPillRightTapped);", Method(code, "OnTagPillLoaded"));
        Assert.Contains("KeyboardContextMenu.PointIn(e, btn)", Method(code, "OnDynamicTagPillRightTapped"));
        var table = BridgeTable(code);
        foreach (var name in new[] { "FocusModeToggle", "PreviewOpenButton", "ExportDropDownButton", "DeleteLinkButton", "NodeProgressSlider" })
        {
            Assert.Contains($"({name}, On", table);
        }
    }

    [Theory]
    [InlineData("ShapeStudio", "OnAlignButtonRightTapped")]
    [InlineData("ShapeStudio", "OnFillSwatchButtonRightTapped")]
    [InlineData("ShapeStudio", "OnFuseLineDensityRightTapped")]
    [InlineData("MindMap", "OnZoomResetRightTapped")]
    [InlineData("MindMap", "OnCenterOnRootRightTapped")]
    [InlineData("MindMap", "OnNodeIconBoxRightTapped")]
    public void BridgedHandlers_AnchorKeyboardMenusUnderTheControl(string studio, string handler)
    {
        var body = Method(Code(studio == "ShapeStudio" ? ShapeXamlPath : MindXamlPath), handler);
        Assert.DoesNotContain("e.GetPosition(", body);
    }

    [Fact]
    public void ShapeStudio_WindowShiftF10_LeavesBridgedControlsTheirOwnMenu()
    {
        var handler = Method(Code(ShapeXamlPath), "OnRootPreviewKeyDown");
        Assert.Contains("case VirtualKey.Application when !KeyboardContextMenu.IsFromBridgedControl(e):", handler);
        Assert.Contains("case VirtualKey.F10 when shift && !KeyboardContextMenu.IsFromBridgedControl(e):", handler);
    }

    [Fact]
    public void ShapeStudio_OpenComboList_KeepsItsKeys()
    {
        var handler = Method(Code(ShapeXamlPath), "OnRootPreviewKeyDown");
        var guard = handler.IndexOf("if (focused is ComboBoxItem || focused is ComboBox { IsDropDownOpen: true }) return;", StringComparison.Ordinal);
        Assert.True(guard > 0, "an open list keeps Esc and the arrow keys");
        Assert.True(guard < handler.IndexOf("case VirtualKey.Escape when ViewModel.HasSelection:", StringComparison.Ordinal), "the guard runs before Esc clears the selection");
        Assert.Contains("if (PresetCategoryCombo.IsDropDownOpen) PresetCategoryCombo.IsDropDownOpen = false;", Method(Code(ShapeXamlPath), "OnPresetCategoryComboRightTapped"));
    }

    [Theory]
    [InlineData("OutlineStatsPill", "OutlineStatsFlyout", "OnOutlineStatsPillRightTapped")]
    [InlineData("OutlineMetricsPill", "OutlineMetricsFlyout", "OnOutlineMetricsPillRightTapped")]
    public void SmartArt_OutlinePills_AreButtons(string pill, string flyout, string handler)
    {
        var element = Xaml(SmartArtXamlPath).Descendants().Single(e => (string?)e.Attribute(XamlNs + "Name") == pill);
        Assert.Equal("Button", element.Name.LocalName);
        Assert.Null(element.Attribute("Tapped"));
        Assert.Null(element.Attribute("DoubleTapped"));
        Assert.NotNull(element.Attributes().FirstOrDefault(a => a.Name.LocalName == "AutomationProperties.Name"));
        // Click opens the menu (Button.Flyout); a double-click no longer copies behind it.
        var menu = element.Descendants().Single(e => (string?)e.Attribute(XamlNs + "Name") == flyout);
        Assert.Equal("Button.Flyout", menu.Parent!.Name.LocalName);
        var tip = (string)element.Attributes().First(a => a.Name.LocalName == "ToolTipService.ToolTip");
        Assert.DoesNotContain("double-click", tip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tap ", tip);

        var code = Code(SmartArtXamlPath);
        Assert.Contains($"({pill}, {handler})", BridgeTable(code));
        Assert.Contains($"KeyboardContextMenu.PointIn(e, {pill})", Method(code, handler));
        Assert.Contains($"AutomationProperties.SetName({pill}, ", Method(code, "UpdateOutlineStats"));
        Assert.DoesNotContain("Tap to view", code);
    }
}
