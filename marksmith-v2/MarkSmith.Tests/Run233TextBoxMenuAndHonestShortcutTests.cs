using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #233: every text box with its own options menu (Find, Replace, History search, the node
// palette search, the two path boxes) opens it from Shift+F10 / the Menu key as well as a
// right-click, and keeps the stock Cut / Copy / Paste menu over a selection. The menus outside the
// editor window show only shortcuts that really work, in the accelerator column.
public class Run233TextBoxMenuAndHonestShortcutTests
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

    private static string?[] AcceleratorsFor(string code, string label)
    {
        var matches = Regex.Matches(code,
            "Text = \"" + Regex.Escape(label) + "\",\\s*(?:KeyboardAcceleratorTextOverride = \"(?<keys>[^\"]*)\")?");
        Assert.NotEmpty(matches);
        return matches.Select(m => m.Groups["keys"].Success ? m.Groups["keys"].Value : null).ToArray();
    }

    [Fact]
    public void TextBoxMenu_swaps_the_flyout_on_selection_and_rebuilds_on_open()
    {
        var code = Read("Services", "TextBoxMenu.cs");
        var attach = Method(code, "Attach");
        // The stock menu is captured before the swap; a code-built box gets a fresh text menu.
        Assert.Contains("var textMenu = box.ContextFlyout ?? new TextCommandBarFlyout();", attach);
        Assert.Contains("string.IsNullOrEmpty(box.SelectedText) ? optionsMenu : textMenu", attach);
        Assert.Contains("box.SelectionChanged +=", attach);
        Assert.Contains("optionsMenu.Items.Clear();", attach);
        // No key or pointer handlers: Shift+F10 reaches a TextBox as a context-menu request.
        Assert.DoesNotContain("KeyDown", attach);
        Assert.DoesNotContain("RightTapped", attach);
    }

    [Theory]
    [InlineData("MainWindow.xaml", "FindTextBox", "FillFindTextBoxMenu")]
    [InlineData("MainWindow.xaml", "ReplaceTextBox", "FillReplaceTextBoxMenu")]
    [InlineData("MainWindow.xaml", "InputFileTextBox", "FillInputFileMenu")]
    [InlineData("MainWindow.xaml", "OutputFolderTextBox", "FillOutputFolderMenu")]
    [InlineData(@"Views\History\HistoryWindow.xaml", "SearchBox", "FillSearchBoxMenu")]
    [InlineData(@"Views\Mermaid\NodePaletteControl.xaml", "SearchTextBox", "FillSearchTextBoxMenu")]
    [InlineData(@"Controls\CommandPalette.xaml", "SearchBox", "FillSearchBoxMenu")]
    public void Text_boxes_open_their_menu_from_the_keyboard(string xamlPath, string box, string fill)
    {
        var parts = xamlPath.Split('\\');
        var element = Named(Xaml(parts), box);
        Assert.Null(element.Attribute("RightTapped"));
        Assert.Null(element.Attribute("ContextRequested"));

        var code = Read(parts[..^1].Append(parts[^1] + ".cs").ToArray());
        Assert.Contains($"TextBoxMenu.Attach({box}, {fill});", code);
        Assert.DoesNotContain($"void On{box}RightTapped(", code);

        // The fill methods only add items; TextBoxMenu owns the flyout and shows it.
        var body = Method(code, fill);
        Assert.DoesNotContain("ShowAt(", body);
        Assert.DoesNotContain("new MenuFlyout()", body);
        Assert.DoesNotContain("e.Handled", body);
    }

    [Theory]
    [InlineData("MainWindow.xaml", "ReplaceTextBox")]
    [InlineData(@"Views\History\HistoryWindow.xaml", "SearchBox")]
    [InlineData(@"Views\Mermaid\NodePaletteControl.xaml", "SearchTextBox")]
    [InlineData("MainWindow.xaml", "FindTextBox")]
    [InlineData(@"Controls\CommandPalette.xaml", "SearchBox")]
    public void Text_box_tooltips_mention_Shift_F10(string xamlPath, string box)
    {
        var tip = (string?)Named(Xaml(xamlPath.Split('\\')), box).Attribute(XName.Get("ToolTipService.ToolTip"));
        Assert.NotNull(tip);
        Assert.Contains("Right-click or Shift+F10 for options", tip);
    }

    [Fact]
    public void Palette_leaves_Shift_F10_in_the_search_box_to_the_box()
    {
        // Found live: the palette's PreviewKeyDown showed the command menu, and the box's own
        // context-menu request then replaced it with the stock text menu.
        var code = Read("Controls", "CommandPalette.xaml.cs");
        var keys = Method(code, "OnPreviewKeyDown");
        var bail = keys.IndexOf("ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), SearchBox)) return;", StringComparison.Ordinal);
        Assert.True(bail >= 0);
        Assert.True(bail < keys.IndexOf("ShowCommandContextMenu(contextCmd", StringComparison.Ordinal));
        // Run / Copy need a highlighted command.
        var fill = Method(code, "FillSearchBoxMenu");
        Assert.Equal(2, Regex.Matches(fill, "IsEnabled = hasCommand,").Count);
    }

    [Fact]
    public void Find_menu_shows_Ctrl_H_in_the_accelerator_column()
    {
        var body = Method(Read("MainWindow.xaml.cs"), "FillFindTextBoxMenu");
        Assert.Contains("? \"Hide replace\" : \"Show replace\",", body);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+H\"", body);
        Assert.DoesNotContain("(Ctrl+H)", body);
        // Ctrl+H is a real window accelerator.
        Assert.Contains("Modifiers=\"Control\" Key=\"H\"", Read("MainWindow.xaml"));
    }

    [Theory]
    [InlineData(@"Views\WelcomeTour.xaml.cs")]
    [InlineData(@"Controls\CommandPalette.xaml.cs")]
    [InlineData(@"Views\SmartArtInsertControl.cs")]
    [InlineData(@"Controls\OcrExperienceOverlay.xaml.cs")]
    [InlineData(@"Controls\ExtensionTip.xaml.cs")]
    [InlineData(@"Controls\ExtensionHintBar.xaml.cs")]
    [InlineData(@"Views\SplashWindow.xaml.cs")]
    [InlineData(@"Views\InsertDialogControls.cs")]
    [InlineData(@"Views\ImageInsertControl.cs")]
    [InlineData(@"Views\History\HistoryWindow.xaml.cs")]
    [InlineData(@"Views\Mermaid\NodePaletteControl.xaml.cs")]
    [InlineData(@"Controls\OptionRow.xaml.cs")]
    public void Menus_keep_shortcuts_out_of_their_labels(string file)
    {
        var code = Read(file.Split('\\'));
        var bracketed = Regex.Matches(code,
                "Text = [^;\\r\\n]*\"[^\"]*\\((?:Ctrl|Alt|Shift|F[0-9]+|Esc|Enter|Delete|Space|Home|End|Right arrow|Left arrow)[^\"]*\\)\"")
            .Select(m => m.Value).ToArray();
        Assert.Empty(bracketed);
    }

    [Theory]
    [InlineData(@"Views\WelcomeTour.xaml.cs", "Skip tour", "Esc")]
    [InlineData(@"Views\WelcomeTour.xaml.cs", "Start with my own document", "Enter")]
    [InlineData(@"Controls\CommandPalette.xaml.cs", "Remove from Recently used", "Ctrl+Delete")]
    [InlineData(@"Controls\CommandPalette.xaml.cs", "Copy selected command", "Ctrl+C")]
    [InlineData(@"Controls\CommandPalette.xaml.cs", "Run selected command", "Enter")]
    [InlineData(@"Views\SmartArtInsertControl.cs", "Indent line", "Alt+Shift+Right")]
    [InlineData(@"Views\SmartArtInsertControl.cs", "Outdent line", "Alt+Shift+Left")]
    [InlineData(@"Controls\OcrExperienceOverlay.xaml.cs", "Skip to editor", "Esc")]
    [InlineData(@"Controls\OcrExperienceOverlay.xaml.cs", "Copy assembled Markdown", "Ctrl+C")]
    [InlineData(@"Controls\ExtensionTip.xaml.cs", "Dismiss tip", "Esc")]
    [InlineData(@"Controls\ExtensionHintBar.xaml.cs", "Dismiss hint", "Esc")]
    [InlineData(@"Views\SplashWindow.xaml.cs", "Replay intro video", "F5")]
    [InlineData(@"Views\SplashWindow.xaml.cs", "Continue to MarkSmith", "Space")]
    [InlineData(@"Views\WelcomeTour.xaml.cs", "Next step", "Right")]
    [InlineData(@"Views\WelcomeTour.xaml.cs", "Previous step", "Left")]
    [InlineData(@"Views\WelcomeTour.xaml.cs", "Jump to first step", "Home")]
    [InlineData(@"Views\WelcomeTour.xaml.cs", "Jump to last step", "End")]
    [InlineData(@"Views\InsertDialogControls.cs", "Insert into document", "Ctrl+Enter")]
    [InlineData(@"Views\History\HistoryWindow.xaml.cs", "Clear search filter", "Esc")]
    public void Real_shortcuts_sit_in_the_accelerator_column(string file, string label, string keys)
    {
        // A label can appear in several menus; the one opened where the key works shows it.
        Assert.Contains(keys, AcceleratorsFor(Read(file.Split('\\')), label));
    }

    [Theory]
    // Nothing binds Ctrl+Enter on the tour's last page.
    [InlineData(@"Views\WelcomeTour.xaml.cs", "Open the sample document")]
    // Ctrl+C in the outline box copies the selection, not the whole outline.
    [InlineData(@"Views\SmartArtInsertControl.cs", "Copy outline text")]
    public void False_hints_are_gone(string file, string label)
    {
        Assert.All(AcceleratorsFor(Read(file.Split('\\')), label), Assert.Null);
    }

    [Fact]
    public void The_shortcuts_behind_the_moved_hints_are_bound()
    {
        Assert.Contains("VirtualKey.Delete when ctrl", Read("Controls", "CommandPalette.xaml.cs"));
        Assert.Contains("e.Key is not (VirtualKey.Right or VirtualKey.Left)", Read("Views", "SmartArtInsertControl.cs"));
        Assert.Contains("VirtualKey.F5", Read("Views", "SplashWindow.xaml.cs"));
        var overlay = Method(Read("Controls", "OcrExperienceOverlay.xaml.cs"), "OnOverlayKeyDown");
        Assert.Contains("VirtualKey.Escape", overlay);
        Assert.Contains("VirtualKey.C", overlay);
        Assert.Contains("VirtualKey.Escape", Method(Read("Views", "WelcomeTour.xaml.cs"), "OnTourKeyDown"));
    }
}
