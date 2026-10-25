using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;

namespace MarkSmith.Tests;

// Run #249: TextBoxMenu reaches the TextBox inside a NumberBox / AutoSuggestBox (AttachInner), so
// Shape Studio's position, size and line-weight boxes and SmartArt's layout search keep Cut / Copy /
// Paste over a selection and open their options from Shift+F10. Shape Studio's fill hex box,
// SmartArt's Markdown and outline rename boxes and Mind Map's icon box move onto TextBoxMenu too,
// and Shape Studio's rotation readout is a button. Shape Studio's window keys also leave an open
// menu alone: Esc used to clear the selection behind it and Delete removed the selected shapes.
public class Run249NumberBoxAndStudioTextMenusTests
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

    private static string Stem(string studio) => studio switch
    {
        "Shape" => Path.Combine("Views", "ShapeStudio", "ShapeDesignStudioWindow"),
        "MindMap" => Path.Combine("Views", "MindMap", "MindMapGalaxyWindow"),
        _ => Path.Combine("Views", "SmartArtStudio", "SmartArtDesignStudioWindow"),
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

    private static void AssertFillOnly(string code, string fill)
    {
        var body = Method(code, fill);
        // TextBoxMenu owns and shows the flyout; a fill only adds items.
        Assert.DoesNotContain(".ShowAt(", body);
        Assert.DoesNotContain("new MenuFlyout()", body);
        Assert.DoesNotContain("e.Handled", body);
    }

    [Fact]
    public void AttachInner_FindsTheTemplateTextBoxNowOrOnLoad()
    {
        var code = Read(Path.Combine("Services", "TextBoxMenu.cs"));
        var inner = Method(code, "AttachInner");
        Assert.Contains("public static void AttachInner(Control host, Action<MenuFlyout> fill)", code);
        Assert.Contains("host.ApplyTemplate();", inner);
        Assert.Contains("host.Loaded +=", inner);
        Assert.Contains("Attach(inner, fill)", inner);
        // Still no key or pointer handlers: Shift+F10 reaches the inner TextBox as a context request.
        Assert.DoesNotContain("KeyDown", inner);
        Assert.DoesNotContain("RightTapped", inner);
    }

    [Fact]
    public void Attach_IsIdempotent_SoARecycledTemplateBoxKeepsItsTextMenu()
    {
        var code = Read(Path.Combine("Services", "TextBoxMenu.cs"));
        var attach = Method(code, "Attach");
        Assert.Contains("ConditionalWeakTable<TextBox, object>", code);
        // The guard runs before the stock menu is captured; a second capture would take the options menu.
        var guard = attach.IndexOf("Attached.TryGetValue(box, out _)", StringComparison.Ordinal);
        var capture = attach.IndexOf("var textMenu = box.ContextFlyout", StringComparison.Ordinal);
        Assert.True(guard >= 0 && capture > guard);
    }

    [Theory]
    [InlineData("Shape", "PositionXNumberBox", "NumberBox", "FillPositionNumberBoxMenu", "OnPositionNumberBoxRightTapped")]
    [InlineData("Shape", "PositionYNumberBox", "NumberBox", "FillPositionNumberBoxMenu", "OnPositionNumberBoxRightTapped")]
    [InlineData("Shape", "SizeWidthNumberBox", "NumberBox", "FillSizeNumberBoxMenu", "OnSizeNumberBoxRightTapped")]
    [InlineData("Shape", "SizeHeightNumberBox", "NumberBox", "FillSizeNumberBoxMenu", "OnSizeNumberBoxRightTapped")]
    [InlineData("Shape", "StrokeWeightNumberBox", "NumberBox", "FillStrokeWeightMenu", "OnStrokeWeightRightTapped")]
    [InlineData("SmartArt", "LayoutSearch", "AutoSuggestBox", "FillLayoutSearchMenu", "OnLayoutSearchRightTapped")]
    public void TemplatedTextControl_UsesAttachInnerInsteadOfARightTapHandler(string studio, string name, string type, string fill, string oldHandler)
    {
        var stem = Stem(studio);
        var box = Named(stem, name);
        Assert.Equal(type, box.Name.LocalName);
        Assert.Null(box.Attribute("RightTapped"));
        Assert.Contains("Right-click or Shift+F10", Attr(box, "ToolTipService.ToolTip"));

        var code = Read(stem + ".xaml.cs");
        Assert.Contains($"TextBoxMenu.AttachInner({name}, {fill});", code);
        Assert.DoesNotContain(oldHandler, code);
        Assert.Matches(@"private void " + fill + @"\(MenuFlyout \w+\)", code);
        AssertFillOnly(code, fill);
    }

    [Theory]
    [InlineData("Shape", "FillHexBox", "FillFillHexBoxMenu", "OnFillHexBoxRightTapped")]
    [InlineData("SmartArt", "MarkdownTextBox", "FillMarkdownTextBoxMenu", "OnMarkdownTextBoxRightTapped")]
    [InlineData("MindMap", "NodeIconBox", "FillNodeIconMenu", "RightTapped=\"OnNodeIconBoxRightTapped\"")]
    public void StudioTextBox_UsesTextBoxMenuInsteadOfARightTapHandler(string studio, string name, string fill, string gone)
    {
        var stem = Stem(studio);
        var box = Named(stem, name);
        Assert.Equal("TextBox", box.Name.LocalName);
        Assert.Null(box.Attribute("RightTapped"));
        var tip = Attr(box, "ToolTipService.ToolTip") ?? "";
        Assert.Contains("Shift+F10", tip);

        var code = Read(stem + ".xaml.cs");
        Assert.Contains($"TextBoxMenu.Attach({name}, {fill});", code);
        if (gone.StartsWith("RightTapped=", StringComparison.Ordinal))
            Assert.DoesNotContain(gone, box.ToString());
        else
            Assert.DoesNotContain(gone, code);
        AssertFillOnly(code, fill);
    }

    [Fact]
    public void ShapeStudio_FillHexAndSwatch_ShareOneMenuBuilder()
    {
        var code = Read(Stem("Shape") + ".xaml.cs");
        Assert.Contains("FillFillQuickPaletteMenu(flyout, shape);", Method(code, "ShowFillQuickPaletteMenu"));
        Assert.Contains("FillFillQuickPaletteMenu(flyout, shape)", Method(code, "FillFillHexBoxMenu"));
        var fill = Method(code, "FillFillQuickPaletteMenu");
        // Its "Pick colour…" item opens the colour picker flyout; the menu itself is shown by the callers.
        Assert.DoesNotContain("flyout.ShowAt(", fill);
        Assert.DoesNotContain("new MenuFlyout()", fill);
    }

    [Fact]
    public void MindMap_IconPickerButton_StillShowsTheSameMenu()
    {
        var code = Read(Stem("MindMap") + ".xaml.cs");
        var show = Method(code, "ShowNodeIconPickerFlyout");
        Assert.Contains("FillNodeIconMenu(menu);", show);
        // No node selected: the fill adds nothing and the picker shows no empty menu.
        Assert.Contains("if (menu.Items.Count > 0) menu.ShowAt(target, pos);", show);
        Assert.Contains("(NodeIconPickerButton, OnNodeIconBoxRightTapped)", BridgeTable(code));
    }

    [Fact]
    public void SmartArt_RenameBox_GetsItsMenuOnLoadAndTheRowLeavesItAlone()
    {
        var stem = Stem("SmartArt");
        var xaml = Read(stem + ".xaml");
        Assert.DoesNotContain("OnRenameTextBoxRightTapped", xaml);
        Assert.Contains("Loaded=\"OnRenameTextBoxLoaded\"", xaml);
        Assert.Contains("Right-click or Shift+F10 for casing options", xaml);

        var code = Read(stem + ".xaml.cs");
        Assert.Contains("TextBoxMenu.Attach(tb, menu => FillRenameTextBoxMenu(tb, menu));", Method(code, "OnRenameTextBoxLoaded"));
        Assert.Matches(@"private void FillRenameTextBoxMenu\(TextBox tb, MenuFlyout menu\)", code);
        AssertFillOnly(code, "FillRenameTextBoxMenu");
        // The row and the outline area would otherwise handle the right-click and replace the box's menu.
        foreach (var handler in new[] { "OnRowRightTapped", "OnOutlineAreaRightTapped" })
        {
            var body = Method(code, handler);
            var guard = body.IndexOf("IsWithinTextBox(source)) return;", StringComparison.Ordinal);
            var show = body.IndexOf("Show", guard < 0 ? 0 : guard, StringComparison.Ordinal);
            Assert.True(guard >= 0, handler);
            Assert.True(show > guard, handler);
        }
    }

    [Fact]
    public void ShapeStudio_RotationReadout_IsAButtonForTheAnglePresets()
    {
        var stem = Stem("Shape");
        var button = Named(stem, "RotationReadoutButton");
        Assert.Equal("HyperlinkButton", button.Name.LocalName);
        Assert.Equal("OnRotationReadoutButtonClick", Attr(button, "Click"));
        Assert.Equal("OnResetRotationButtonRightTapped", Attr(button, "RightTapped"));
        // One action per gesture: the readout no longer also resets on double-click.
        Assert.Null(Attr(button, "DoubleTapped"));
        Assert.Contains("Shift+F10", Attr(button, "ToolTipService.ToolTip"));
        Assert.NotNull(Attr(button, "AutomationProperties.HelpText"));

        var text = Named(stem, "RotationDegreeText");
        Assert.Equal("TextBlock", text.Name.LocalName);
        Assert.Equal("RotationReadoutButton", (string?)text.Parent?.Attribute(XamlNs + "Name"));
        Assert.Null(Attr(text, "RightTapped"));
        Assert.Null(Attr(text, "DoubleTapped"));

        var code = Read(stem + ".xaml.cs");
        Assert.Contains("KeyboardContextMenu.Invoke(RotationReadoutButton, OnResetRotationButtonRightTapped)", Method(code, "OnRotationReadoutButtonClick"));
        Assert.Contains("(RotationReadoutButton, OnResetRotationButtonRightTapped)", BridgeTable(code));
        // The shared preset menu anchors under the control for a keyboard request.
        Assert.Contains("KeyboardContextMenu.PointIn(e, target)", Method(code, "OnResetRotationButtonRightTapped"));
    }

    [Fact]
    public void RotationLabel_NamesTheReadoutAndFollowsTheAngle()
    {
        var shape = new ShapeCanvasItemViewModel { Prst = "rect", Rotation = 45 };
        Assert.Equal("Rotation 45°", shape.RotationLabel);
        string? changed = null;
        shape.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShapeCanvasItemViewModel.RotationLabel)) changed = shape.RotationLabel; };
        shape.Rotation = -90;
        Assert.Equal("Rotation -90°", changed);

        var button = Named(Stem("Shape"), "RotationReadoutButton");
        Assert.StartsWith("{Binding SelectedShape.RotationLabel", Attr(button, "AutomationProperties.Name"));
    }

    [Fact]
    public void ShapeStudio_WindowKeys_LeaveAnOpenMenuOrFlyoutAlone()
    {
        var code = Read(Stem("Shape") + ".xaml.cs");
        var handler = Method(code, "OnRootPreviewKeyDown");
        var guard = handler.IndexOf("if (focused is DependencyObject f && !IsWithinRootGrid(f)) return;", StringComparison.Ordinal);
        Assert.True(guard >= 0);
        // Before any shortcut: Ctrl+F, the text-box Esc, Ctrl+C / V, Delete and the Esc that clears the selection.
        foreach (var later in new[] { "ctrl && e.Key == VirtualKey.F", "VirtualKey.Escape && focused is TextBox", "OnCopyMarkdownClick",
                                      "case (VirtualKey.Delete or VirtualKey.Back) when ViewModel.HasSelection", "case VirtualKey.Escape when ViewModel.HasSelection" })
        {
            var at = handler.IndexOf(later, StringComparison.Ordinal);
            Assert.True(at > guard, later);
        }
        var within = Method(code, "IsWithinRootGrid");
        Assert.Contains("VisualTreeHelper.GetParent(obj)", within);
        Assert.Contains("ReferenceEquals(obj, RootGrid)", within);
    }
}
