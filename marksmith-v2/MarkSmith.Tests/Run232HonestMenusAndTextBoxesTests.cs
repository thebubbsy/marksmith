using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #232: the editor window's menus show only shortcuts that really work, in the accelerator
// column, and its text boxes behave like text boxes: a double-click selects a word, and the
// custom path menus open from Shift+F10 / the Menu key as well as a right-click.
public class Run232HonestMenusAndTextBoxesTests
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

    // The accelerator text of the menu item whose Text is exactly `label` (null when it has none).
    private static string?[] AcceleratorsFor(string code, string label)
    {
        var matches = Regex.Matches(code,
            "Text = \"" + Regex.Escape(label) + "\",\\s*(?:KeyboardAcceleratorTextOverride = \"(?<keys>[^\"]*)\")?");
        Assert.NotEmpty(matches);
        return matches.Select(m => m.Groups["keys"].Success ? m.Groups["keys"].Value : null).ToArray();
    }

    [Fact]
    public void MainWindow_menu_text_no_longer_spells_shortcuts_in_brackets()
    {
        var bracketed = Regex.Matches(MainCode,
            "Text = \"[^\"]*\\((?:Ctrl|Shift|Alt|F\\d|Enter|Delete|Esc)[^\"]*\\)\"");
        Assert.Empty(bracketed.Select(m => m.Value));
    }

    [Fact]
    public void Real_shortcuts_moved_to_the_accelerator_column()
    {
        Assert.All(AcceleratorsFor(MainCode, "Settings"), k => Assert.Equal("Ctrl+,", k));
        Assert.All(AcceleratorsFor(MainCode, "Keyboard shortcuts"), k => Assert.Equal("F1", k));
        Assert.Contains("Ctrl+G", AcceleratorsFor(MainCode, "Go to line…"));
        Assert.Contains("Alt+Z", AcceleratorsFor(MainCode, "Word wrap"));
        Assert.Contains("Shift+Enter", AcceleratorsFor(MainCode, "Find previous match"));
        Assert.Contains("Alt+C", AcceleratorsFor(MainCode, "Match case"));
        Assert.Contains("Ctrl+Shift+C", AcceleratorsFor(MainCode, "Copy Markdown link"));
        Assert.Contains("Delete", AcceleratorsFor(MainCode, "Remove from list"));
        Assert.Contains("Ctrl+O", AcceleratorsFor(MainCode, "Open a document…"));
    }

    [Fact]
    public void View_menus_do_not_claim_ctrl_1_to_3_which_apply_headings()
    {
        // Ctrl+1–4 are the editor's heading accelerators, not the Code / Split / Preview views.
        var headingKeys = MainXaml.Descendants()
            .Where(e => e.Name.LocalName == "KeyboardAccelerator"
                        && (string?)e.Attribute("Modifiers") == "Control"
                        && (string?)e.Attribute("Invoked") == "OnFormatAcceleratorInvoked")
            .Select(e => (string?)e.Attribute("Key"))
            .ToList();
        Assert.Contains("Number1", headingKeys);
        Assert.DoesNotMatch("KeyboardAcceleratorTextOverride = \"Ctrl\\+[123]\"", MainCode);
        foreach (var label in new[] { "Code view", "Split view", "Preview view" })
        {
            Assert.All(AcceleratorsFor(MainCode, label), Assert.Null);
        }
    }

    [Theory]
    [InlineData("Open in File Explorer")]          // Ctrl+E generates a PDF
    [InlineData("Show in File Explorer")]          // Ctrl+O opens a document (except in export history)
    [InlineData("Copy folder path")]               // Ctrl+C copies the selection only
    [InlineData("Copy full path")]                 // nothing binds Ctrl+Shift+C there
    [InlineData("Choose different folder…")]       // Ctrl+O opens a document
    [InlineData("Choose output folder…")]
    [InlineData("Close replace")]                  // Ctrl+H opens Replace
    [InlineData("Copy replacement text")]
    [InlineData("Cancel export in progress")]      // Esc isn't bound to cancel
    [InlineData("Code view")]
    public void False_shortcut_hints_are_gone(string label)
    {
        var keys = AcceleratorsFor(MainCode, label);
        if (label == "Show in File Explorer")
        {
            // The export-history list really does handle Ctrl+O; the file and pin menus don't.
            Assert.Single(keys, k => k == "Ctrl+O");
            Assert.Contains(null, keys);
            Assert.Contains("VirtualKey.O && ctrl && !shift", Method(MainCode, "OnHistoryListKeyDown"));
        }
        else
        {
            Assert.All(keys, Assert.Null);
        }
    }

    [Theory]
    [InlineData("InputFileTextBox", "FillInputFileMenu")]
    [InlineData("OutputFolderTextBox", "FillOutputFolderMenu")]
    public void Path_boxes_keep_double_click_and_open_their_menu_from_the_keyboard(string box, string fill)
    {
        // Shift+F10 reaches a TextBox as a context-menu request that opens its ContextFlyout, so
        // no pointer or key handler is used: the box's ContextFlyout is swapped instead.
        var element = Named(MainXaml, box);
        foreach (var evt in new[] { "DoubleTapped", "RightTapped", "PreviewKeyDown", "ContextRequested" })
        {
            Assert.Null(element.Attribute(evt));
        }
        // Run #233 moved the swap into Services/TextBoxMenu so other search boxes share it
        // (Run233TextBoxMenuAndHonestShortcutTests covers the helper itself).
        Assert.Contains($"TextBoxMenu.Attach({box}, {fill});", MainCode);
        Assert.DoesNotContain($"void On{box}DoubleTapped(", MainCode);
        Assert.DoesNotContain($"void On{box}RightTapped(", MainCode);

        var body = Method(MainCode, fill);
        Assert.DoesNotContain("ShowAt(", body);
        Assert.DoesNotContain("new MenuFlyout()", body);
    }

    [Fact]
    public void Output_folder_tooltip_no_longer_promises_a_double_click()
    {
        var tip = (string?)Named(MainXaml, "OutputFolderTextBox").Attribute(XName.Get("ToolTipService.ToolTip"));
        Assert.NotNull(tip);
        Assert.DoesNotContain("Double-click", tip);
        Assert.Contains("Shift+F10", tip);
    }

    [Fact]
    public void History_and_palette_search_boxes_select_a_word_on_double_click()
    {
        var history = Named(Xaml("Views", "History", "HistoryWindow.xaml"), "SearchBox");
        Assert.Null(history.Attribute("DoubleTapped"));
        Assert.DoesNotContain("OnSearchBoxDoubleTapped", Read("Views", "History", "HistoryWindow.xaml.cs"));

        var palette = Named(Xaml("Views", "Mermaid", "NodePaletteControl.xaml"), "SearchTextBox");
        Assert.Null(palette.Attribute("DoubleTapped"));
        Assert.DoesNotContain("Double-click", (string?)palette.Attribute(XName.Get("ToolTipService.ToolTip")) ?? "");
        Assert.DoesNotContain("OnSearchTextBoxDoubleTapped", Read("Views", "Mermaid", "NodePaletteControl.xaml.cs"));
    }

    [Fact]
    public void Settings_pdf_preview_cycle_follows_the_position_menu_order()
    {
        var body = Method(Read("Views", "SettingsView.xaml.cs"), "OnPdfPagePreviewClick");
        Assert.Contains("PdfPageNumberPositions", body);
        Assert.DoesNotContain("\"BottomCenter\"", body);
        Assert.DoesNotContain("switch", body);
    }
}
