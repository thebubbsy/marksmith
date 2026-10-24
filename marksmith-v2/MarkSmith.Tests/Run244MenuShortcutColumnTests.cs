using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #244: about 110 studio and editor menu items spelled their shortcut inside the label
// ("Fit all nodes in window (Ctrl+0)"); the shortcut now sits in the menu's accelerator column like
// the rest of the app, and shortcuts that were never bound are gone from the menus. Mind Map's
// Ctrl+Shift+S saves a copy (it did a plain save) and Esc clears the selection it advertises.
// Studio commands whose result is also copied now say so on the status line, and the splash's
// "Copy version" copies the real version.
public class Run244MenuShortcutColumnTests
{
    private static readonly string[] MenuFiles =
    {
        "MainWindow.xaml.cs",
        Path.Combine("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"),
        Path.Combine("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"),
        Path.Combine("Views", "Ocr", "OcrInspectionStudioWindow.xaml.cs"),
        Path.Combine("Views", "SplashWindow.xaml.cs"),
        Path.Combine("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"),
    };

    private static readonly string[] Studios =
    {
        Path.Combine("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"),
        Path.Combine("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"),
        Path.Combine("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"),
    };

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

    // A label that ends in a bracketed shortcut: "Save map (Ctrl+S)", "Rename (F2)", "Mute audio (M)".
    // Diff markers such as "(+)", value lists such as "(0 / 2 / 4 / 6)" and toolbar chips aren't menu shortcuts.
    private const string Key = @"(?:(?:(?:Ctrl|Shift|Alt)\+)+[0-9]|(?:(?:Ctrl|Shift|Alt)\+)*(?:[A-Z]|F\d{1,2}|Del|Delete|Home|End|Esc|Enter|Space|Tab|Insert|\[|\]|Up|Down))";
    private static readonly Regex HintInLabel = new(@"Text\s*=\s*[^;\n]*""[^""]* \(" + Key + @"(?: / " + Key + @")*\)""");

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var f in MenuFiles) data.Add(f);
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void MenuLabels_NoLongerSpellTheirShortcut(string file)
    {
        var offenders = Read(file).Split('\n')
            .Where(l => l.Contains("Text") && !l.Contains("TextBlock") && !l.Contains("ToolTip") && !l.Contains("Placeholder") && HintInLabel.IsMatch(l))
            .Select(l => l.Trim())
            .ToList();
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("MainWindow.xaml.cs", "F11", 3)]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "Ctrl+0", 10)]
    [InlineData("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs", "Ctrl+Shift+C", 9)]
    [InlineData("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs", "F5", 3)]
    public void Shortcuts_MovedIntoTheAcceleratorColumn(string file, string keys, int atLeast)
    {
        var code = Read(file.Replace('/', Path.DirectorySeparatorChar));
        var count = Regex.Matches(code, $"KeyboardAcceleratorTextOverride = \"{Regex.Escape(keys)}\"").Count;
        Assert.True(count >= atLeast, $"{keys}: {count} < {atLeast}");
    }

    [Fact]
    public void ShapeStudio_ZOrderItems_ShowTheirBracketKeysInTheColumn()
    {
        var xaml = Read(Path.Combine("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        foreach (var (label, keys) in new[] { ("Bring forward", "Ctrl+]"), ("Send backward", "Ctrl+["), ("Bring to front", "Ctrl+Shift+]"), ("Send to back", "Ctrl+Shift+[") })
        {
            Assert.Contains($"Text=\"{label}\" KeyboardAcceleratorTextOverride=\"{keys}\"", xaml);
        }
    }

    [Fact]
    public void UnboundShortcuts_AreNotAdvertised()
    {
        var smartArt = Read(Path.Combine("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        // Nothing handles Ctrl+Tab, Ctrl+Shift+Tab or F6 in SmartArt Studio.
        Assert.DoesNotContain("KeyboardAcceleratorTextOverride = \"Ctrl+Tab\"", smartArt);
        Assert.DoesNotContain("KeyboardAcceleratorTextOverride = \"Ctrl+Shift+Tab\"", smartArt);
        Assert.DoesNotContain("KeyboardAcceleratorTextOverride = \"F6\"", smartArt);
        Assert.DoesNotContain("VirtualKey.F6", smartArt);
        Assert.Contains("Text = \"Focus outline editor\",", smartArt);

        // Mind Map adds a child on Tab (or N), never on Insert.
        var mindMap = Read(Path.Combine("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.DoesNotContain("Insert / Tab", mindMap);
        Assert.DoesNotContain("VirtualKey.Insert", mindMap);

        // The editor's Ctrl+C copies the selection, not the gutter's line.
        var main = Read("MainWindow.xaml.cs");
        var copyLine = main.IndexOf("Text = $\"Copy line {label}\",", StringComparison.Ordinal);
        Assert.True(copyLine >= 0);
        var block = main.Substring(copyLine, main.IndexOf('}', copyLine) - copyLine);
        Assert.DoesNotContain("KeyboardAcceleratorTextOverride", block);
    }

    [Fact]
    public void MindMap_CollapseItem_KeepsSpaceInTheColumnWhenItsLabelChanges()
    {
        var code = Read(Path.Combine("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("MenuItem(node.IsCollapsed ? \"Expand subtree\" : \"Collapse subtree\",", code);
        Assert.Contains("\"Space\");", code[code.IndexOf("var toggleCollapseItem = MenuItem(", StringComparison.Ordinal)..]);
        Assert.Contains("toggleCollapseItem.Text = node.IsCollapsed ? \"Expand subtree\" : \"Collapse subtree\";", code);
    }

    [Fact]
    public void MindMap_CtrlShiftS_SavesACopy_AndCtrlS_IsHandledBeforeItsAwait()
    {
        var keys = Method(Read(Path.Combine("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs")), "OnRootKeyDown");
        var saveAs = keys.IndexOf("case Windows.System.VirtualKey.S when ctrl && shift:", StringComparison.Ordinal);
        var save = keys.IndexOf("case Windows.System.VirtualKey.S when ctrl:", StringComparison.Ordinal);
        Assert.True(saveAs >= 0 && save > saveAs, "Ctrl+Shift+S must be matched before the plain Ctrl+S case");
        Assert.Contains("OnSaveAsClick(this, new RoutedEventArgs());", keys[saveAs..save]);

        var plain = keys[save..keys.IndexOf("return;", save, StringComparison.Ordinal)];
        Assert.True(plain.IndexOf("e.Handled = true;", StringComparison.Ordinal) < plain.IndexOf("await ViewModel.SaveAsync();", StringComparison.Ordinal));
    }

    [Fact]
    public void MindMap_Escape_ClearsTheSelectionWhenNothingElseIsActive()
    {
        var keys = Method(Read(Path.Combine("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs")), "OnRootKeyDown");
        var esc = keys[keys.IndexOf("case Windows.System.VirtualKey.Escape:", StringComparison.Ordinal)..];
        var branch = esc[..esc.IndexOf("ViewModel.SearchQuery = \"\";", StringComparison.Ordinal)];
        Assert.Contains("string.IsNullOrEmpty(ViewModel.SearchQuery)", branch);
        Assert.Contains("ViewModel.SelectedTagFilter is null", branch);
        Assert.Contains("!ViewModel.IsFocusModeEnabled", branch);
        Assert.Contains("foreach (var n in ViewModel.Nodes) n.IsSelected = false;", branch);
        Assert.Contains("ViewModel.SelectedNode = null;", branch);
        Assert.Contains("ViewModel.SelectedLink = null;", branch);
    }

    [Theory]
    [InlineData("✓ 3 shapes off the grid", "✓ 3 shapes off the grid · copied to the clipboard")]
    [InlineData("Layer stack: 4 layers.", "Layer stack: 4 layers · copied to the clipboard")]
    [InlineData("Copied angle: 45°", "Copied angle: 45°")]
    [InlineData("✓ Copied outline bullets", "✓ Copied outline bullets")]
    [InlineData("Outline put on the clipboard", "Outline put on the clipboard")]
    [InlineData("  ", "Copied to the clipboard.")]
    [InlineData(null, "Copied to the clipboard.")]
    public void Succeeded_SaysAResultWentToTheClipboard(string? message, string expected)
    {
        Assert.Equal(expected, CopyStatusText.Succeeded(message));
    }

    [Fact]
    public void Succeeded_IsIdempotent()
    {
        var once = CopyStatusText.Succeeded("Hierarchy: 3 levels");
        Assert.Equal(once, CopyStatusText.Succeeded(once));
    }

    public static TheoryData<string> StudioFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Studios) data.Add(f);
        return data;
    }

    [Theory]
    [MemberData(nameof(StudioFiles))]
    public void Studios_CopyAndReport_ReportSuccessThroughSucceeded(string file)
    {
        var body = Method(Read(file), "CopyAndReport");
        Assert.Contains("CopyStatusText.Succeeded(message ?? ViewModel.StatusMessage)", body);
        Assert.DoesNotContain("if (message is not null) ViewModel.StatusMessage = message;", body);
        Assert.Contains("CopyStatusText.Failed(", body);
    }

    [Fact]
    public void Splash_CopyVersion_CopiesTheRealVersion_AndConfirms()
    {
        var code = Read(Path.Combine("Views", "SplashWindow.xaml.cs"));
        Assert.DoesNotContain("TrySetTextAsync(\"MarkSmith Desktop\")", code);
        Assert.Contains("Text = $\"Copy version ({App.Updates.CurrentDisplayVersion})\"", code);
        Assert.Contains("$\"MarkSmith {App.Updates.CurrentDisplayVersion}\"", code);
        Assert.Contains("CopyFeedback.CopyWithBubble(Root, version,", code);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"M\"", code);
    }

    private static readonly System.Xml.Linq.XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static System.Xml.Linq.XDocument SmartArtXaml() =>
        System.Xml.Linq.XDocument.Load(Path.Combine(DesktopDir(), "Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));

    private static string SmartArtCode() => Read(Path.Combine("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));

    // A double-click on a button raises Click twice and then DoubleTapped: Delete removed three
    // items, Duplicate made four copies, the flyout buttons opened their flyout and cycled a setting.
    [Fact]
    public void SmartArt_ButtonsThatClick_HaveNoSecondDoubleClickAction()
    {
        var offenders = SmartArtXaml().Descendants()
            .Where(e => e.Name.LocalName == "Button"
                        && (e.Attribute("Click") is not null || e.Attribute("Command") is not null)
                        && e.Attribute("DoubleTapped") is not null)
            .Select(e => (string?)e.Attribute(XamlNs + "Name") ?? (string?)e.Attribute("Click") ?? "?")
            .Where(n => n is not ("MoveUpButton" or "MoveDownButton" or "PromoteButton" or "DemoteButton"))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));

        var code = SmartArtCode();
        foreach (var gone in new[] { "OnDeleteButtonDoubleTapped", "OnDuplicateButtonDoubleTapped", "OnRowDeleteDoubleTapped", "OnRowAddChildDoubleTapped", "OnExportSvgButtonDoubleTapped", "OnCopySmartArtBlockButtonDoubleTapped" })
        {
            Assert.DoesNotContain(gone, code);
        }
    }

    [Fact]
    public void SmartArt_TooltipsNoLongerPromiseTheRemovedDoubleClicks()
    {
        foreach (var name in new[] { "RenameButton", "DuplicateButton", "DeleteButton", "PaletteButton", "CopyOutlineMarkdownButton", "CopySmartArtBlockButton", "ExportSvgButton", "DiagramBgButton", "NodeGeometryButton", "AspectRatioButton", "ZoomButton" })
        {
            var button = SmartArtXaml().Descendants().Single(e => (string?)e.Attribute(XamlNs + "Name") == name);
            var tip = (string?)button.Attributes().FirstOrDefault(a => a.Name.LocalName == "ToolTipService.ToolTip") ?? "";
            Assert.DoesNotContain("double-click", tip, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SmartArt_ToolbarMenus_OpenFromTheKeyboard()
    {
        var code = SmartArtCode();
        var start = code.IndexOf("KeyboardContextMenu.BridgeAll(", StringComparison.Ordinal);
        Assert.True(start >= 0, "SmartArt Studio bridges its right-click menus");
        var table = code[start..code.IndexOf(");", start, StringComparison.Ordinal)];
        foreach (var name in new[] { "UndoStudioButton", "RedoStudioButton", "InsertIntoDocumentButton", "LayoutCategoryCombo", "EditorTabs", "RenameButton", "DuplicateButton", "DeleteButton", "MoveUpButton", "PromoteButton", "PaletteButton", "CopyOutlineMarkdownButton", "CopySmartArtBlockButton", "ExportSvgButton", "PreviewZoomResetBtn" })
        {
            Assert.Contains($"({name}, On", table);
        }
        // A keyboard request has no pointer: bridged menus open under the control, not at its corner.
        foreach (var handler in new[] { "OnUndoButtonRightTapped", "OnDeleteButtonRightTapped", "OnCopySmartArtBlockButtonRightTapped", "OnEditorTabsRightTapped" })
        {
            var body = Method(code, handler);
            Assert.Contains("KeyboardContextMenu.PointIn(e, ", body);
            Assert.DoesNotContain("e.GetPosition(", body);
        }
    }

    [Fact]
    public void SmartArt_MoveToTopAndBottom_ShowAltHomeAndAltEndInTheColumn()
    {
        var code = SmartArtCode();
        Assert.DoesNotContain("/ double-click)", code);
        Assert.Equal(2, Regex.Matches(code, "Text = \"Move to top\",\\s*KeyboardAcceleratorTextOverride = \"Alt\\+Home\"").Count);
        Assert.Equal(2, Regex.Matches(code, "Text = \"Move to bottom\",\\s*KeyboardAcceleratorTextOverride = \"Alt\\+End\"").Count);
    }
}
