using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #236: Settings' PDF page preview is a button (Enter or a click moves the page number,
// Shift+F10 opens its menu). The Insert dialogs select a number box's sample on open, leave Enter
// to an open dropdown, announce their problem hint and open one menu on the Inserts card. The
// SmartArt dialog's Examples button, layout gallery and indent buttons open their menus from the
// keyboard, and Suite Hub's health badge copies only what it knows.
public class Run236PdfPreviewAndInsertDialogTests
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
        Assert.True(m.Success, $"{name} not found");
        var open = code.IndexOf('{', m.Index);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code.Substring(m.Index, i - m.Index + 1);
        }
        throw new InvalidOperationException($"{name} has no closing brace");
    }

    private static string InsertCode => Read("Views", "InsertDialogControls.cs");

    [Fact]
    public void Settings_pdf_preview_is_a_button_with_one_action_per_click()
    {
        var xaml = Xaml("Views", "SettingsView.xaml");
        var button = Named(xaml, "PdfPagePreviewButton");
        Assert.Equal("Button", button.Name.LocalName);
        Assert.Equal("OnPdfPagePreviewClick", (string?)button.Attribute("Click"));
        Assert.Equal("OnPdfPagePreviewRightTapped", (string?)button.Attribute("RightTapped"));
        Assert.Contains("Shift+F10", (string?)button.Attribute("ToolTipService.ToolTip") ?? "");
        Assert.DoesNotContain("Double-click", (string?)button.Attribute("ToolTipService.ToolTip") ?? "");

        // The page itself is a plain picture inside the button now.
        var page = Named(xaml, "PdfPagePreview");
        Assert.Same(button, page.Parent);
        Assert.Null(page.Attribute("DoubleTapped"));
        Assert.Null(page.Attribute("RightTapped"));

        var code = Read("Views", "SettingsView.xaml.cs");
        Assert.DoesNotContain("OnPdfPagePreviewDoubleTapped", code);
        Assert.Contains("(PdfPagePreviewButton, OnPdfPagePreviewRightTapped)", code);
        Assert.Contains("menu.ShowAt(PdfPagePreviewButton, KeyboardContextMenu.PointIn(e, PdfPagePreviewButton));",
            Method(code, "OnPdfPagePreviewRightTapped"));
        // The spoken name follows the focusable button, not the picture inside it.
        Assert.Contains("AutomationProperties.SetName(PdfPagePreviewButton,", Method(code, "RenderPdfBands"));
    }

    [Fact]
    public void Insert_dialog_selects_the_sample_inside_number_boxes_and_combo_boxes()
    {
        var focus = Method(InsertCode, "FocusFirstField");
        Assert.Contains("first as TextBox ?? InnerTextBox(first)", focus);
        Assert.Contains("box.SelectAll()", focus);
        Assert.Contains("private static TextBox? InnerTextBox(DependencyObject parent)", InsertCode);
    }

    [Fact]
    public void Insert_dialog_leaves_enter_to_an_open_dropdown()
    {
        Assert.Contains("AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnBodyPreviewKeyDown), true);", InsertCode);
        var preview = Method(InsertCode, "OnBodyPreviewKeyDown");
        Assert.Contains("IsDropDownOpen: true", preview);

        // The commit path checks the flag before it looks at anything else about Enter.
        var keyDown = Method(InsertCode, "OnBodyKeyDown");
        var enter = keyDown.IndexOf("if (e.Key != VirtualKey.Enter) return;", StringComparison.Ordinal);
        var guard = keyDown.IndexOf("if (_enterClosesDropDown)", StringComparison.Ordinal);
        var commit = keyDown.IndexOf("RequestCommit();", StringComparison.Ordinal);
        Assert.True(enter >= 0 && guard > enter && commit > guard);
    }

    [Fact]
    public void Insert_dialog_problem_hint_is_announced()
    {
        var refresh = Method(InsertCode, "Refresh");
        // Run #284: through LiveAnnounce, which creates the peer the raw FromElement call lacked.
        Assert.Contains("LiveAnnounce.RaiseIfChanged(_problem, problem)", refresh);
    }

    [Fact]
    public void Insert_dialog_card_has_one_menu_for_mouse_and_keyboard()
    {
        Assert.DoesNotContain(".RightTapped +=", InsertCode);
        Assert.DoesNotContain("ShowSnippetContextMenu", InsertCode);
        Assert.Contains("ContextFlyout = previewContextFlyout,", InsertCode);
        Assert.Contains("PopulateSnippetContextMenu(previewContextFlyout);", InsertCode);
    }

    [Fact]
    public void SmartArt_insert_menus_open_from_the_keyboard()
    {
        var code = Read("Views", "SmartArtInsertControl.cs");
        Assert.Contains("KeyboardContextMenu.Bridge(examplesButton, OnExamplesButtonRightTapped);", code);
        Assert.Contains("KeyboardContextMenu.Bridge(_gallery, OnGalleryRightTapped);", code);
        Assert.Contains("KeyboardContextMenu.Bridge(button, showMenu);", code);
        // A keyboard request has no pointer, so GetPosition would put the menu in the corner.
        Assert.DoesNotContain("e.GetPosition(", code);
    }

    [Fact]
    public void Suite_hub_health_copy_claims_only_what_it_knows()
    {
        var code = Read("Views", "SuiteHubView.xaml.cs");
        var click = Method(code, "OnHealthStatusClick");
        Assert.DoesNotContain("All Studios Ready", click);
        Assert.DoesNotContain("3.0.0", click);
        var menu = Method(code, "OnHealthStatusRightTapped");
        Assert.DoesNotContain("Engine: Ready", menu);
        Assert.DoesNotContain("\"3.0.0\"", menu);
        Assert.Contains("RuntimeInformation.ProcessArchitecture", menu);
    }
}
