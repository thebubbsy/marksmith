using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #248: four studio readouts that were right-click / double-click TextBlocks are buttons
// (Mind Map's search match count, SmartArt's preview title and outline summary, Shape Studio's
// selection summary), and five inspector text boxes use TextBoxMenu, so a selection keeps Cut /
// Copy / Paste and Shift+F10 opens their options.
public class Run248ReadoutButtonsAndTextMenusTests
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

    private static string Stem(string studio) => studio switch
    {
        "Shape" => ShapeStem,
        "MindMap" => MindStem,
        _ => SmartArtStem,
    };

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

    [Theory]
    [InlineData("MindMap", "SearchMatchButton", "SearchMatchLabel", "OnSearchMatchButtonClick", "OnSearchStepperPillRightTapped")]
    [InlineData("SmartArt", "PreviewTitleButton", "PreviewTitleLabel", "OnPreviewTitleButtonClick", "OnPreviewTitleButtonRightTapped")]
    [InlineData("SmartArt", "OutlineSummaryButton", "OutlineSummaryLabel", "OnOutlineSummaryButtonClick", "OnOutlineSummaryButtonRightTapped")]
    [InlineData("Shape", "SelectionSummaryButton", "SelectionSummaryText", "OnSelectionSummaryButtonClick", "OnSelectionSummaryRightTapped")]
    public void Readout_IsAButtonWhoseClickRightClickAndShiftF10OpenTheMenu(string studio, string button, string label, string click, string menu)
    {
        var stem = Stem(studio);
        var b = Named(stem, button);
        Assert.Equal("HyperlinkButton", b.Name.LocalName);
        Assert.Equal(click, (string?)b.Attribute("Click"));
        Assert.Contains("Shift+F10", Attr(b, "ToolTipService.ToolTip"));
        Assert.False(string.IsNullOrEmpty(Attr(b, "AutomationProperties.HelpText")));

        // The readout lives inside the button and no longer carries its own pointer handlers.
        var text = Named(stem, label);
        Assert.Equal(button, (string?)text.Parent!.Attribute(XamlNs + "Name"));
        Assert.Null(text.Attribute("DoubleTapped"));
        Assert.Null(text.Attribute("RightTapped"));
        Assert.Null(text.Attribute("IsHitTestVisible"));

        var code = Read(stem + ".xaml.cs");
        Assert.Contains($"({button}, {menu})", BridgeTable(code));
        Assert.Contains($"KeyboardContextMenu.Invoke({button}, {menu})", Method(code, click));
        Assert.Contains("KeyboardContextMenu.PointIn(e, ", Method(code, menu));
        Assert.DoesNotContain($"e.GetPosition({label})", code);
    }

    [Fact]
    public void Readouts_LostTheirModeDependentDoubleClicks()
    {
        Assert.DoesNotContain("OnSearchMatchLabelDoubleTapped", Read(MindStem + ".xaml.cs"));
        var smartArt = Read(SmartArtStem + ".xaml.cs");
        Assert.DoesNotContain("OnPreviewTitleLabelDoubleTapped", smartArt);
        Assert.DoesNotContain("OnOutlineSummaryLabelDoubleTapped", smartArt);
        Assert.DoesNotContain("OnSelectionSummaryDoubleTapped", Read(ShapeStem + ".xaml.cs"));
    }

    [Theory]
    [InlineData("SmartArt", "OutlineSummaryButton", "OnOutlineSummaryButtonDoubleTapped")]
    [InlineData("Shape", "SelectionSummaryButton", "OnSelectionSummaryButtonDoubleTapped")]
    public void StatusBarButtons_DoNotLetADoubleClickReachTheStatusBarCopy(string studio, string button, string handler)
    {
        // Both sit in a status bar whose DoubleTapped copies the status line.
        var b = Named(Stem(studio), button);
        Assert.Equal("OnStatusBarDoubleTapped", (string?)b.Parent!.Attribute("DoubleTapped"));
        Assert.Equal(handler, (string?)b.Attribute("DoubleTapped"));
        Assert.Contains("e.Handled = true", Method(Read(Stem(studio) + ".xaml.cs"), handler));
    }

    [Fact]
    public void MindMap_SearchStepperPill_ButtonsAllOpenTheSearchMenuFromShiftF10()
    {
        var pill = Named(MindStem, "SearchStepperPill");
        Assert.Equal("OnSearchStepperPillRightTapped", (string?)pill.Attribute("RightTapped"));
        Assert.Contains("Right-click or Shift+F10", Attr(pill, "ToolTipService.ToolTip"));

        var code = Read(MindStem + ".xaml.cs");
        var table = BridgeTable(code);
        foreach (var name in new[] { "SearchPrevMatchButton", "SearchNextMatchButton", "SearchClearButton" })
        {
            Assert.Contains($"({name}, OnSearchStepperPillRightTapped)", table);
            Assert.Contains("Shift+F10", Attr(Named(MindStem, name), "ToolTipService.ToolTip"));
        }
        Assert.Contains("menu.ShowAt(SearchStepperPill, KeyboardContextMenu.PointIn(e, SearchStepperPill))", Method(code, "OnSearchStepperPillRightTapped"));
        Assert.Contains("AutomationProperties.SetName(SearchMatchButton,", Method(code, "SyncAccessibleStatusLabels"));

        // Esc clears the search from anywhere and collapses the pill; focus inside it goes to the canvas.
        Assert.Contains("SearchStepperPill.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => MoveFocusOffSearchStepperPill());", code);
        var move = Method(code, "MoveFocusOffSearchStepperPill");
        Assert.Contains("SearchStepperPill.Visibility == Visibility.Visible) return;", move);
        Assert.Contains("VisualTreeHelper.GetParent(node)", move);
        Assert.Contains("GalaxyCanvas.Focus(FocusState.Programmatic)", move);
    }

    [Fact]
    public void ShapeStudio_SelectionSummaryButton_HidesWhenBlankWithoutDroppingFocus()
    {
        var code = Read(ShapeStem + ".xaml.cs");
        var sync = Method(code, "SyncSelectionSummaryButton");
        Assert.Contains("AutomationProperties.SetName(SelectionSummaryButton, summary)", sync);
        // Focus moves before the collapse, never after.
        var focus = sync.IndexOf("CanvasScroller.Focus(FocusState.Programmatic)", StringComparison.Ordinal);
        var collapse = sync.IndexOf("SelectionSummaryButton.Visibility =", StringComparison.Ordinal);
        Assert.True(focus >= 0 && collapse > focus);
        Assert.Contains("SelectionSummaryButton.FocusState != FocusState.Unfocused", sync);
        Assert.Contains("case nameof(ShapeDesignStudioViewModel.SelectionSummary):\n                    SyncSelectionSummaryButton();", code.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("MindMap", "TitleBox", "FillTitleBoxMenu")]
    [InlineData("MindMap", "TagsBox", "FillTagsBoxMenu")]
    [InlineData("MindMap", "LinkLabelBox", "FillLinkLabelBoxMenu")]
    [InlineData("Shape", "LabelTextBox", "FillLabelTextBoxMenu")]
    [InlineData("Shape", "FuseImagePathBox", "FillFuseImagePathBoxMenu")]
    public void InspectorTextBox_UsesTextBoxMenuInsteadOfARightTapHandler(string studio, string box, string fill)
    {
        var stem = Stem(studio);
        var tb = Named(stem, box);
        Assert.Equal("TextBox", tb.Name.LocalName);
        Assert.Null(tb.Attribute("RightTapped"));
        Assert.Contains("Right-click or Shift+F10", Attr(tb, "ToolTipService.ToolTip"));

        var code = Read(stem + ".xaml.cs");
        Assert.Contains($"TextBoxMenu.Attach({box}, {fill});", code);
        Assert.DoesNotContain($"On{box}RightTapped", code);
        var body = Method(code, fill);
        Assert.Matches(@"private void " + fill + @"\(MenuFlyout \w+\)", code);
        // TextBoxMenu shows the menu; the fill only adds items.
        Assert.DoesNotContain(".ShowAt(", body);
        Assert.DoesNotContain("new MenuFlyout()", body);
    }
}
