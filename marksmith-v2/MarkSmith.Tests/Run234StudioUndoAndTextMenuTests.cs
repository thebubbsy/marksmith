using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #234: Diagram Studio's Ctrl+Z / Ctrl+Y undo typing inside its text boxes (they undid a canvas
// step), its Undo / Redo buttons keep keyboard focus in the toolbar when they disable, and its Code
// pane menu opens from the keyboard with only honest shortcuts. The outline filter box and the OCR
// studio's Markdown preview get the same TextBoxMenu treatment, and Step 1's recent-files list
// opens its menu from Shift+F10.
public class Run234StudioUndoAndTextMenuTests
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
        var m = Regex.Match(code, @"(?:void|Task) " + Regex.Escape(name) + @"\(");
        var start = m.Success ? m.Index : -1;
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

    private static string? AcceleratorFor(string code, string label)
    {
        var m = Regex.Match(code,
            "Text = \"" + Regex.Escape(label) + "\",\\s*(?:Icon = new FontIcon \\{ Glyph = \"[^\"]*\" \\},\\s*)?(?:KeyboardAcceleratorTextOverride = \"(?<keys>[^\"]*)\")?");
        Assert.True(m.Success, $"menu item '{label}' not found");
        return m.Groups["keys"].Success ? m.Groups["keys"].Value : null;
    }

    private static string Studio() => Read("Views", "Mermaid", "MermaidDiagramStudioControl.xaml.cs");

    [Theory]
    [InlineData("OnUndoAcceleratorInvoked")]
    [InlineData("OnRedoAcceleratorInvoked")]
    public void Studio_undo_keys_leave_text_boxes_their_own_undo(string handler)
    {
        // Accelerators run before the focused TextBox sees the key, so without the guard Ctrl+Z in
        // the Code pane or a label box undid a canvas step and the typing stayed.
        var body = Method(Studio(), handler);
        var guard = body.IndexOf("if (IsEditingText()) return;", StringComparison.Ordinal);
        var handled = body.IndexOf("args.Handled = true;", StringComparison.Ordinal);
        Assert.True(guard >= 0, $"{handler} has no text-box guard");
        Assert.True(guard < handled);
    }

    [Fact]
    public void Undo_and_redo_hand_focus_on_when_their_button_disables()
    {
        var code = Studio();
        var body = Method(code, "UndoRedo");
        // The focus holder is captured before the step, and checked after the binding disabled it.
        Assert.True(body.IndexOf("FocusState != FocusState.Unfocused", StringComparison.Ordinal)
                    < body.IndexOf("ViewModel.Undo()", StringComparison.Ordinal));
        Assert.Contains("DispatcherQueue.TryEnqueue(", body);
        Assert.Contains("if (holder.IsEnabled) return;", body);
        Assert.Contains("next.Focus(FocusState.Keyboard)", body);
        Assert.Contains("_canvas.FocusCanvas()", body);

        // Toolbar clicks, the shortcuts and the toolbar menu all go through it.
        Assert.Contains("OnUndoClick(object sender, RoutedEventArgs e) => UndoRedo(redo: false);", code);
        Assert.Contains("OnRedoClick(object sender, RoutedEventArgs e) => UndoRedo(redo: true);", code);
        var menu = Method(code, "ShowUndoRedoMenu");
        Assert.Contains("UndoRedo(redo: false, fe as Control)", menu);
        Assert.Contains("UndoRedo(redo: true, fe as Control)", menu);
        Assert.DoesNotContain("ViewModel?.Undo()", code);
        Assert.DoesNotContain("ViewModel?.Redo()", code);
    }

    [Theory]
    [InlineData(@"Views\Mermaid\MermaidDiagramStudioControl.xaml", "CodeEditorTextBox", "FillCodeEditorMenu", "OnCodeEditorTextBoxRightTapped")]
    [InlineData("MainWindow.xaml", "TocFilterBox", "FillTocFilterMenu", "OnTocFilterRightTapped")]
    [InlineData(@"Views\Ocr\OcrInspectionStudioWindow.xaml", "MarkdownPreviewTextBox", "FillMarkdownPreviewMenu", "OnMarkdownPreviewRightTapped")]
    public void Text_boxes_open_their_menu_from_the_keyboard(string xamlPath, string box, string fill, string oldHandler)
    {
        var parts = xamlPath.Split('\\');
        var element = Named(Xaml(parts), box);
        Assert.Null(element.Attribute("RightTapped"));
        var tip = (string?)element.Attribute(XName.Get("ToolTipService.ToolTip"));
        Assert.NotNull(tip);
        Assert.Contains("Right-click or Shift+F10 for options", tip);

        var code = Read(parts[..^1].Append(parts[^1] + ".cs").ToArray());
        Assert.Contains($"TextBoxMenu.Attach({box}, {fill});", code);
        Assert.DoesNotContain($"void {oldHandler}(", code);

        var body = Method(code, fill);
        Assert.DoesNotContain("ShowAt(", body);
        Assert.DoesNotContain("new MenuFlyout()", body);
        Assert.DoesNotContain("e.Handled", body);
        Assert.DoesNotContain("SelectedText", body);
    }

    [Fact]
    public void Code_pane_menu_shows_only_shortcuts_that_work_there()
    {
        var body = Method(Studio(), "FillCodeEditorMenu");
        // Ctrl+Enter runs OnSyncToMarkdownClick (the document write), not a canvas sync.
        Assert.Equal("Ctrl+Enter", AcceleratorFor(body, "Write diagram to document"));
        Assert.Contains("writeItem.Click += (_, _) => OnSyncToMarkdownClick(", body);
        Assert.Null(AcceleratorFor(body, "Update canvas now"));
        // Ctrl+C / Ctrl+V act on the selection and caret, not the whole source.
        Assert.Null(AcceleratorFor(body, "Copy all source"));
        Assert.Null(AcceleratorFor(body, "Replace source with clipboard"));
        Assert.Equal("Ctrl+A", AcceleratorFor(body, "Select all"));
        Assert.DoesNotContain("\"Sync to canvas\"", body);
        Assert.DoesNotContain("\"Paste source\"", body);
    }

    [Theory]
    [InlineData("OnCopySvgAcceleratorInvoked")]
    [InlineData("OnExportSvgAcceleratorInvoked")]
    public void Svg_shortcuts_listed_in_the_code_menu_work_from_the_code_pane(string handler)
    {
        var code = Studio();
        Assert.Contains("if (IsEditingTextOutsideCode()) return;", Method(code, handler));
        Assert.Contains("IsEditingText() && CodeEditorTextBox.FocusState == FocusState.Unfocused", code);
        var menu = Method(code, "FillCodeEditorMenu");
        Assert.Equal("Ctrl+Shift+C", AcceleratorFor(menu, "Copy diagram as SVG"));
    }

    [Fact]
    public void Replacing_the_source_from_the_clipboard_reports_what_happened()
    {
        var body = Method(Studio(), "ReplaceCodeFromClipboardAsync");
        Assert.DoesNotContain("catch { }", body);
        Assert.Contains("Couldn't read the clipboard", body);
        Assert.Contains("The clipboard has no text to paste", body);
        Assert.Contains("SyncCanvasFromEditor(text,", body);
        var sync = Method(Studio(), "SyncCanvasFromEditor");
        Assert.Contains("ShowCodeStatus(", sync);
        var status = Method(Studio(), "ShowCodeStatus");
        Assert.Contains("AnnounceLiveRegion(CodeStatusText, text, assertive);", status);
    }

    [Fact]
    public void Code_pane_tooltip_says_what_Ctrl_Enter_does()
    {
        var tip = (string?)Named(Xaml("Views", "Mermaid", "MermaidDiagramStudioControl.xaml"), "CodeEditorTextBox")
            .Attribute(XName.Get("ToolTipService.ToolTip"));
        Assert.Contains("Ctrl+Enter writes it to the document", tip);
        Assert.DoesNotContain("Ctrl+Enter to sync", tip);
    }

    [Fact]
    public void Ocr_preview_menu_drops_the_Esc_hint_it_cannot_honour()
    {
        var code = Read("Views", "Ocr", "OcrInspectionStudioWindow.xaml.cs");
        var body = Method(code, "FillMarkdownPreviewMenu");
        Assert.Null(AcceleratorFor(body, "Cancel & Discard"));
        // The global shortcuts it lists run before OnRootKeyDown's text-box bail-out.
        var keys = Method(code, "OnRootKeyDown");
        Assert.True(keys.IndexOf("VirtualKey.C && ctrl && shift", StringComparison.Ordinal)
                    < keys.IndexOf("if (inputFocused) return;", StringComparison.Ordinal));
        Assert.Equal("Ctrl+Shift+C", AcceleratorFor(body, "Copy Markdown preview"));
    }

    [Fact]
    public void Recent_files_list_opens_its_menu_from_Shift_F10()
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.Contains("(MarkdownFilesCombo, OnMarkdownFilesComboRightTapped),", code);
        var body = Method(code, "OnMarkdownFilesComboRightTapped");
        Assert.True(body.IndexOf("MarkdownFilesCombo.IsDropDownOpen) return;", StringComparison.Ordinal)
                    < body.IndexOf("e.Handled = true;", StringComparison.Ordinal));
        var tip = (string?)Named(Xaml("MainWindow.xaml"), "MarkdownFilesCombo").Attribute(XName.Get("ToolTipService.ToolTip"));
        Assert.Contains("Right-click or Shift+F10 for options", tip);
    }
}
