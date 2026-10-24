using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #247: Mind Map's title is a button (the title bar used to swallow its right-click, and the
// keyboard couldn't reach its menu), the breadcrumb bar moves below the guided-tour banner, the
// map search box keeps Cut / Copy / Paste and opens its options from Shift+F10, SmartArt's layout
// count is a button, and every bridged studio control's tooltip says Shift+F10 as well as right-click.
public class Run247TitleSearchAndShiftF10HintsTests
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

    private static string ShapeStem => Path.Combine("Views", "ShapeStudio", "ShapeDesignStudioWindow");
    private static string MindStem => Path.Combine("Views", "MindMap", "MindMapGalaxyWindow");
    private static string SmartArtStem => Path.Combine("Views", "SmartArtStudio", "SmartArtDesignStudioWindow");

    private static XElement Named(string stem, string name) =>
        XDocument.Parse(Read(stem + ".xaml")).Descendants().Single(e => (string?)e.Attribute(XamlNs + "Name") == name);

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
    public void MindMap_Title_IsAButtonWhoseClickAndShiftF10OpenTheMenu()
    {
        var button = Named(MindStem, "MapTitleButton");
        Assert.Equal("Button", button.Name.LocalName);
        Assert.Equal("OnMapTitleButtonClick", (string?)button.Attribute("Click"));
        Assert.Equal("OnMapTitleButtonRightTapped", (string?)button.Attribute("RightTapped"));
        Assert.Null(button.Attribute("DoubleTapped"));
        Assert.Equal("{Binding Title}", Attr(button, "AutomationProperties.Name"));
        Assert.Contains("Shift+F10", Attr(button, "ToolTipService.ToolTip"));

        var title = Named(MindStem, "MapTitleText");
        Assert.Equal("MapTitleButton", (string?)title.Parent!.Attribute(XamlNs + "Name"));
        Assert.Null(title.Attribute("DoubleTapped"));
        Assert.Null(title.Attribute("RightTapped"));

        var code = Read(MindStem + ".xaml.cs");
        Assert.Contains("(MapTitleButton, OnMapTitleButtonRightTapped)", BridgeTable(code));
        Assert.Contains("KeyboardContextMenu.Invoke(MapTitleButton, OnMapTitleButtonRightTapped)", Method(code, "OnMapTitleButtonClick"));
        Assert.Contains("KeyboardContextMenu.PointIn(e, MapTitleButton)", Method(code, "OnMapTitleButtonRightTapped"));
        Assert.DoesNotContain("OnMapTitleTextDoubleTapped", code);
    }

    [Fact]
    public void MindMap_BreadcrumbBar_DropsBelowTheTopOverlayWhenTheyWouldOverlap()
    {
        var banner = Named(MindStem, "TourBanner");
        Assert.Equal("TopOverlay", (string?)banner.Parent!.Attribute(XamlNs + "Name"));
        Assert.Equal("{Binding IsTutorialActive, Converter={StaticResource BoolToVis}}", (string?)banner.Attribute("Visibility"));

        var code = Read(MindStem + ".xaml.cs");
        var place = Method(code, "PlaceBreadcrumbBar");
        Assert.Contains("TopOverlay.TransformToVisual(CanvasContainer)", place);
        Assert.Contains("overlayLeft < 16 + _breadcrumbBar.ActualWidth + 8", place);
        Assert.Contains("top += TopOverlay.ActualHeight + 8", place);
        Assert.Contains("_breadcrumbBar.Margin = new Thickness(16, top, 0, 0)", place);
        foreach (var source in new[] { "_breadcrumbBar", "TopOverlay", "CanvasContainer" })
        {
            Assert.Contains($"{source}.SizeChanged += (_, _) => PlaceBreadcrumbBar();", code);
        }
    }

    [Fact]
    public void MindMap_SearchBox_UsesTextBoxMenuInsteadOfARightTapHandler()
    {
        var box = Named(MindStem, "SearchBox");
        Assert.Null(box.Attribute("RightTapped"));
        Assert.Equal("OnSearchBoxLoaded", (string?)box.Attribute("Loaded"));
        Assert.Contains("Right-click or Shift+F10 for options", Attr(box, "ToolTipService.ToolTip"));

        var code = Read(MindStem + ".xaml.cs");
        Assert.DoesNotContain("OnSearchBoxRightTapped", code);
        var loaded = Method(code, "OnSearchBoxLoaded");
        Assert.Contains("FindDescendant<TextBox>(SearchBox)", loaded);
        Assert.Contains("TextBoxMenu.Attach(box, FillSearchBoxMenu)", loaded);
        Assert.Contains("_searchBoxMenuAttached = true", loaded);

        var fill = Method(code, "FillSearchBoxMenu");
        Assert.DoesNotContain("ShowAt(", fill);
        Assert.DoesNotContain("new MenuFlyout()", fill);
        foreach (var item in new[] { "Next match", "Previous match", "Copy search query", "Clear search", "Return to galaxy map" })
        {
            Assert.Contains($"\"{item}\"", fill);
        }
    }

    [Fact]
    public void SmartArt_LayoutCount_IsAButtonInsteadOfADoubleClickReadout()
    {
        var button = Named(SmartArtStem, "LayoutCountButton");
        Assert.Equal("HyperlinkButton", button.Name.LocalName);
        Assert.Equal("OnLayoutCountButtonClick", (string?)button.Attribute("Click"));
        Assert.Equal("OnLayoutCountButtonRightTapped", (string?)button.Attribute("RightTapped"));
        Assert.Equal("{Binding LayoutCountText}", Attr(button, "AutomationProperties.Name"));
        var tip = Attr(button, "ToolTipService.ToolTip")!;
        Assert.Contains("Shift+F10", tip);
        Assert.DoesNotContain("Double-click", tip, StringComparison.OrdinalIgnoreCase);

        // The label keeps its live region (the code announces count changes through it).
        var label = Named(SmartArtStem, "LayoutCountLabel");
        Assert.Equal("LayoutCountButton", (string?)label.Parent!.Attribute(XamlNs + "Name"));
        Assert.Equal("Polite", Attr(label, "AutomationProperties.LiveSetting"));
        Assert.Null(label.Attribute("DoubleTapped"));
        Assert.Null(label.Attribute("RightTapped"));

        var code = Read(SmartArtStem + ".xaml.cs");
        Assert.DoesNotContain("OnLayoutCountLabelDoubleTapped", code);
        Assert.Contains("(LayoutCountButton, OnLayoutCountButtonRightTapped)", BridgeTable(code));
        Assert.Contains("KeyboardContextMenu.Invoke(LayoutCountButton, OnLayoutCountButtonRightTapped)", Method(code, "OnLayoutCountButtonClick"));
        Assert.Contains("KeyboardContextMenu.PointIn(e, LayoutCountButton)", Method(code, "OnLayoutCountButtonRightTapped"));
    }

    public static TheoryData<string> Studios => new()
    {
        Path.Combine("Views", "ShapeStudio", "ShapeDesignStudioWindow"),
        Path.Combine("Views", "MindMap", "MindMapGalaxyWindow"),
        Path.Combine("Views", "SmartArtStudio", "SmartArtDesignStudioWindow"),
    };

    [Theory]
    [MemberData(nameof(Studios))]
    public void BridgedControls_TooltipsMentionShiftF10WheneverTheyMentionRightClick(string stem)
    {
        var names = Regex.Matches(BridgeTable(Read(stem + ".xaml.cs")), @"\((\w+),\s*On\w+\)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        Assert.NotEmpty(names);

        var checkedCount = 0;
        foreach (var element in XDocument.Parse(Read(stem + ".xaml")).Descendants())
        {
            if ((string?)element.Attribute(XamlNs + "Name") is not { } name || !names.Contains(name)) continue;
            var tip = Attr(element, "ToolTipService.ToolTip");
            if (tip is null || tip.StartsWith("{", StringComparison.Ordinal)) continue;
            if (!tip.Contains("right-click", StringComparison.OrdinalIgnoreCase)) continue;
            checkedCount++;
            Assert.True(tip.Contains("Shift+F10", StringComparison.Ordinal), $"{name}: \"{tip}\" names right-click but not Shift+F10");
        }
        Assert.True(checkedCount >= 10, $"only {checkedCount} bridged tooltips found in {stem}");
    }

    [Fact]
    public void ShapeStudio_MultiSelectionBadge_NameIsTheReadoutNotMouseInstructions()
    {
        var code = Read(ShapeStem + ".xaml.cs");
        var body = Method(code, "UpdateMultiSelectionAdorner");
        Assert.Contains("AutomationProperties.SetName(MultiSelectionBadge, $\"Selection: {badgeSummary}\")", body);
        Assert.DoesNotContain("SetName(MultiSelectionBadge, fullTip)", body);
        Assert.Contains("ToolTipService.SetToolTip(MultiSelectionBadge, fullTip)", body);
    }
}
