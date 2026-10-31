using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #311:
// - Settings ▸ Presets: Delete asks first (there is no undo), and hands keyboard focus to Save
//   before the delete disables it (it fell to the window's first tab stop);
// - SmartArt and Shape Studio colour-scheme flyouts open on the scheme in use, scrolled into view,
//   with a focus ring when opened from the keyboard;
// - Mermaid Studio: the node Fill / Border buttons say the colour in use (tooltip and help text),
//   and their colour pickers are titled with the colour they change and its hex value.
public class Run311PresetDeleteAndStudioColourFlyoutTests
{
    private const string Main = "MarkSmith.Desktop/MainWindow.xaml.cs";
    private const string MainXaml = "MarkSmith.Desktop/MainWindow.xaml";
    private const string Helper = "MarkSmith.Desktop/Services/FlyoutListFocus.cs";
    private const string SmartArt = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string Shape = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string ShapeXaml = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml";
    private const string SmartArtXaml = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml";
    private const string Mermaid = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml";
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

    private static XElement Named(string relative, string name) =>
        XDocument.Parse(Read(relative)).Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string Method(string text, string signature)
    {
        int a = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(a >= 0, $"missing: {signature}");
        int b = text.IndexOf("\n    }\n", a, StringComparison.Ordinal);
        Assert.True(b > a, $"no end for: {signature}");
        return text.Substring(a, b - a);
    }

    [Fact]
    public void PresetButtons_AreNamed_AndDeleteSaysItAsks()
    {
        Assert.Equal("OnSavePresetClick", (string?)Named(MainXaml, "SavePresetButton").Attribute("Click"));
        var delete = Named(MainXaml, "DeletePresetButton");
        Assert.Equal("OnDeletePresetClick", (string?)delete.Attribute("Click"));
        Assert.Equal("{Binding HasActivePreset}", (string?)delete.Attribute("IsEnabled"));
        Assert.Contains("after asking", (string?)delete.Attribute("ToolTipService.ToolTip"));
    }

    [Fact]
    public void DeletePreset_AsksBeforeDeleting()
    {
        var body = Method(Read(Main), "private async void OnDeletePresetClick(");
        int ask = body.IndexOf("ConfirmFlyout.AskAsync(DeletePresetButton", StringComparison.Ordinal);
        int delete = body.IndexOf("ViewModel.DeletePreset(preset)", StringComparison.Ordinal);
        Assert.True(ask >= 0 && delete > ask, "the delete must wait for the confirmation");
        // A cancelled ask returns before deleting.
        Assert.Contains("\"Delete\"))\n            return;", body);
        // The preset could have gone while the bubble was open.
        Assert.Contains("ViewModel.Presets.Contains(preset)", body);
    }

    [Fact]
    public void DeletePreset_MovesFocusToSave_BeforeTheDeleteDisablesTheButton()
    {
        var body = Method(Read(Main), "private async void OnDeletePresetClick(");
        // Read before the bubble opens: by the time it closes the button's state has changed.
        int held = body.IndexOf("var held = DeletePresetButton.FocusState;", StringComparison.Ordinal);
        int ask = body.IndexOf("ConfirmFlyout.AskAsync", StringComparison.Ordinal);
        int save = body.IndexOf("SavePresetButton.Focus(", StringComparison.Ordinal);
        int delete = body.IndexOf("ViewModel.DeletePreset(preset)", StringComparison.Ordinal);
        Assert.True(held >= 0 && held < ask, "focus state must be captured before asking");
        Assert.True(save > ask && save < delete, "focus must move before the delete disables the button");
        // Only when the button had focus; a pointer press gets no ring.
        Assert.Contains("held != FocusState.Unfocused", body);
        Assert.Contains("held == FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard", body);
    }

    [Fact]
    public void FlyoutListFocus_ReadsTheOpenerOnOpening_AndFocusesAfterOpened()
    {
        var src = Read(Helper);
        Assert.Contains("flyout.Opening += (_, _) => how = RingIfKeyboard(opener);", src);
        Assert.Contains("flyout.Opened += (_, _) =>", src);
        Assert.Contains("list.DispatcherQueue.TryEnqueue(() => FocusCurrent(list, how))", src);
        Assert.Contains("opener.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic", src);
    }

    [Fact]
    public void FlyoutListFocus_FocusesTheSelectedRow_ScrolledIntoView()
    {
        var src = Read(Helper);
        var body = src.Substring(src.IndexOf("internal static bool FocusCurrent(", StringComparison.Ordinal));
        Assert.Contains("list.SelectedItem ?? (list.Items.Count > 0 ? list.Items[0] : null)", body);
        int scroll = body.IndexOf("list.ScrollIntoView(item);", StringComparison.Ordinal);
        int focus = body.IndexOf("row.Focus(how)", StringComparison.Ordinal);
        Assert.True(scroll >= 0 && focus > scroll);
        // A flyout closed again before the enqueued call ran must not steal focus.
        Assert.Contains("if (list.XamlRoot is null) return false;", body);
    }

    [Theory]
    [InlineData(SmartArt, SmartArtXaml)]
    [InlineData(Shape, ShapeXaml)]
    public void StudioPaletteFlyouts_UseFlyoutListFocus(string code, string xaml)
    {
        Assert.Contains("FlyoutListFocus.Attach(PaletteFlyout, PaletteList, PaletteButton);", Read(code));
        // The list really lives in that flyout.
        var flyout = Named(xaml, "PaletteFlyout");
        Assert.Contains(flyout.Descendants(), e => (string?)e.Attribute(X + "Name") == "PaletteList");
    }

    [Theory]
    [InlineData("NodeFillColorButton", "Node fill color", "FillColor", "Node fill colour")]
    [InlineData("NodeBorderColorButton", "Node border color", "StrokeColor", "Node border colour")]
    public void MermaidNodeColourButtons_SayTheColourInUse(string button, string name, string prop, string pickerTitle)
    {
        var el = Named(Mermaid, button);
        Assert.Equal(name, el.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
        Assert.Equal($"{{Binding SelectedNode.{prop}}}", el.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.HelpText").Value);
        // No static tooltip attribute any more: the element tooltip carries the bound hex.
        Assert.DoesNotContain(el.Attributes(), a => a.Name.LocalName == "ToolTipService.ToolTip");
        var tip = el.Elements().Single(e => e.Name.LocalName == "ToolTipService.ToolTip");
        Assert.Contains(tip.Descendants(), r => r.Name.LocalName == "Run" && (string?)r.Attribute("Text") == $"{{Binding SelectedNode.{prop}}}");

        var flyout = el.Descendants().Single(e => e.Name.LocalName == "Flyout");
        var picker = flyout.Descendants().Single(e => e.Name.LocalName == "ColorPicker");
        Assert.Equal(pickerTitle, picker.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
        Assert.Contains(flyout.Descendants(), t => t.Name.LocalName == "TextBlock" && (string?)t.Attribute("Text") == pickerTitle);
        Assert.Contains(flyout.Descendants(), t => t.Name.LocalName == "TextBlock" && (string?)t.Attribute("Text") == $"{{Binding SelectedNode.{prop}}}");
    }
}
