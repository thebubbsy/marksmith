using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #231: Diagram Studio's zoom panel, status bar and menus, plus the editor's export links and
// Split-sync toggle. A double-click no longer repeats or undoes what the clicks just did, the
// summary readout is a keyboard button, and menus advertise only the shortcuts that really work.
public class Run231DiagramStudioOneActionTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

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

    private static XDocument Xaml(params string[] parts) => XDocument.Load(Path.Combine(new[] { DesktopDir() }.Concat(parts).ToArray()));

    private static string CanvasCode => Read("Views", "Mermaid", "MermaidCanvasControl.xaml.cs");
    private static XDocument CanvasXaml => Xaml("Views", "Mermaid", "MermaidCanvasControl.xaml");
    private static string StudioCode => Read("Views", "Mermaid", "MermaidDiagramStudioControl.xaml.cs");
    private static XDocument StudioXaml => Xaml("Views", "Mermaid", "MermaidDiagramStudioControl.xaml");
    private static string MainCode => Read("MainWindow.xaml.cs");
    private static XDocument MainXaml => Xaml("MainWindow.xaml");

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string Method(string code, string name)
    {
        var start = code.IndexOf($"void {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} not found");
        var open = code.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code.Substring(start, i - start + 1);
        }
        throw new InvalidOperationException($"{name} has no closing brace");
    }

    private static string BridgeTable(string code)
    {
        var start = code.IndexOf("KeyboardContextMenu.BridgeAll(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return code.Substring(start, code.IndexOf(");", start, StringComparison.Ordinal) - start);
    }

    [Fact]
    public void ZoomPanel_TwoQuickZoomInClicks_DoNotSnapBackTo100()
    {
        var reset = Named(CanvasXaml, "ZoomResetButton");
        Assert.NotNull(reset.Attribute("Click"));
        Assert.Null(reset.Attribute("DoubleTapped"));
        // The panel around the zoom buttons caught their double-taps and reset the zoom.
        var panel = reset.Ancestors().First(a => a.Name.LocalName == "Border");
        Assert.Null(panel.Attribute("DoubleTapped"));
        Assert.DoesNotContain("Double-click", (string?)panel.Attribute("ToolTipService.ToolTip") ?? "");
        Assert.DoesNotContain("OnZoomResetDoubleTapped", CanvasCode);
    }

    [Fact]
    public void ZoomPresets_AreRadioItemsWithTheCurrentLevelChecked()
    {
        var menu = Method(CanvasCode, "OnZoomResetRightTapped");
        Assert.Contains("RadioMenuFlyoutItem", menu);
        Assert.Contains("GroupName = \"MermaidZoomPreset\"", menu);
        Assert.Contains("IsChecked = Math.Abs(currentZoom - factor) < 0.04f", menu);
    }

    [Fact]
    public void StudioMenus_CtrlZeroBelongsTo100Percent_NotFit()
    {
        // Ctrl+0 is bound to zoom reset, so the fit items must not claim it.
        Assert.Contains("Key=\"Number0\" Invoked=\"OnZoomResetAcceleratorInvoked\"", Read("Views", "Mermaid", "MermaidDiagramStudioControl.xaml"));
        Assert.Contains("\"Fit diagram in window\",", StudioCode);
        Assert.Contains("\"Fit whole diagram\",", StudioCode);
        Assert.Matches(new Regex("Text = \"Actual size \\(100%\\)\",\\s*KeyboardAcceleratorTextOverride = \"Ctrl\\+0\""), StudioCode);
        Assert.Matches(new Regex("Text = \"Reset zoom to 100%\",\\s*KeyboardAcceleratorTextOverride = \"Ctrl\\+0\""), CanvasCode);
    }

    [Theory]
    [InlineData("Views/Mermaid/MermaidDiagramStudioControl.xaml.cs")]
    [InlineData("Views/Mermaid/MermaidCanvasControl.xaml.cs")]
    [InlineData("Views/Mermaid/MermaidMinimapControl.cs")]
    [InlineData("Views/Mermaid/NodePaletteControl.xaml.cs")]
    public void DiagramStudioMenus_PutShortcutsInTheAcceleratorColumn(string file)
    {
        var code = Read(file.Split('/'));
        Assert.DoesNotMatch(new Regex("Text = \"[^\"]*\\((Ctrl|Shift|Alt)\\+[^\"]*\\)\""), code);
    }

    [Fact]
    public void SummaryReadout_IsAKeyboardButtonBridgedToTheStatusMenu()
    {
        var button = Named(StudioXaml, "CanvasSummaryButton");
        Assert.Equal("Button", button.Name.LocalName);
        Assert.Equal("OnCanvasSummaryClick", (string?)button.Attribute("Click"));
        Assert.Null(button.Attribute("DoubleTapped"));
        Assert.Contains("Shift+F10", (string?)button.Attribute("ToolTipService.ToolTip") ?? "");
        var text = Named(StudioXaml, "CanvasSummaryText");
        Assert.Equal("CanvasSummaryButton", (string?)text.Parent!.Attribute(X + "Name"));
        Assert.Null(text.Attribute("DoubleTapped"));
        Assert.Contains("(CanvasSummaryButton, OnStatusBarRightTapped)", BridgeTable(StudioCode));
        Assert.Contains("SetName(CanvasSummaryButton, $\"Copy diagram summary, {CanvasSummaryText.Text}\")", StudioCode);
        Assert.DoesNotContain("OnCanvasSummaryDoubleTapped", StudioCode);
    }

    [Fact]
    public void SummaryCopy_ConfirmsOrSaysWhyItFailed()
    {
        var click = Method(StudioCode, "OnCanvasSummaryClick");
        Assert.Contains("string.IsNullOrWhiteSpace(summary)", click);
        Assert.Contains("Copied diagram summary to clipboard.", click);
        Assert.Contains("Couldn't copy the summary", click);
    }

    [Fact]
    public void StatusBarFit_IgnoresDoubleClicksOnItsButtons()
    {
        var fit = Method(StudioCode, "OnStatusBarDoubleTapped");
        Assert.Contains("is ButtonBase) return;", fit);
        Assert.True(fit.IndexOf("ButtonBase", StringComparison.Ordinal) < fit.IndexOf("FitToContent", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportLinks_DoubleClickOnOpen_DoesNotOpenTwiceAndCopy()
    {
        var strip = Named(MainXaml, "StatusOutputLinks");
        Assert.Null(strip.Attribute("DoubleTapped"));
        Assert.DoesNotContain("Double-click", (string?)strip.Attribute("ToolTipService.ToolTip") ?? "");
        Assert.DoesNotContain("OnStatusOutputLinksDoubleTapped", MainCode);
        // The copy actions moved to where the keyboard reaches them: Shift+F10 on either link.
        Assert.Equal("HyperlinkButton", Named(MainXaml, "StatusOpenOutputButton").Name.LocalName);
        Assert.Equal("HyperlinkButton", Named(MainXaml, "StatusShowOutputFolderButton").Name.LocalName);
        // MainWindow bridges from a foreach table rather than BridgeAll.
        var main = MainCode;
        var start = main.IndexOf("foreach (var (pill, handler) in new (UIElement", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var table = main.Substring(start, main.IndexOf("KeyboardContextMenu.Bridge(pill, handler)", start, StringComparison.Ordinal) - start);
        Assert.Contains("(StatusOpenOutputButton, OnStatusOutputLinksRightTapped)", table);
        Assert.Contains("(StatusShowOutputFolderButton, OnStatusOutputLinksRightTapped)", table);
        Assert.Contains("KeyboardContextMenu.PointIn(e, StatusOutputLinks)", Method(MainCode, "OnStatusOutputLinksRightTapped"));
    }

    [Fact]
    public void SplitSyncToggle_DoubleClickNoLongerFlipsSyncOffAndOn()
    {
        var toggle = Named(MainXaml, "SplitSyncToggle");
        Assert.Null(toggle.Attribute("DoubleTapped"));
        Assert.DoesNotContain("Double-click", (string?)toggle.Attribute("ToolTipService.ToolTip") ?? "");
        Assert.DoesNotContain("OnSplitSyncToggleDoubleTapped", MainCode);
        // Aligning lives in the toggle's menu and still says it happened.
        var menu = Method(MainCode, "OnSplitSyncToggleRightTapped");
        Assert.Contains("syncToPreview.Click += (_, _) => AlignPreviewToEditorNow();", menu);
        var align = Method(MainCode, "AlignPreviewToEditorNow");
        Assert.Contains("if (_viewMode != ViewMode.Split) return;", align);
        Assert.Contains("AnnounceStatus(\"Split View aligned", align);
    }
}
