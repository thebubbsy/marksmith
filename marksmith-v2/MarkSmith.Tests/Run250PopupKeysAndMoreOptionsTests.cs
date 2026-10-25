using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #250: a menu's keys route through the ancestors of the element it opened from, so window-level
// key handlers saw keys pressed inside an open menu. History's Down moved the timeline instead of the
// menu and S / F2 / Delete acted on the version behind it; SmartArt's Delete deleted the selected node;
// Mind Map's Ctrl+Z / D / L acted behind it; Suite Hub's 1-6 launched studios; OCR's Esc in a flyout
// discarded the studio. Each handler now returns first when focus is outside its root
// (PopupKeys.FocusIsOutside). TextBoxMenu's text menu also gains "More options", because Tab selects a
// box's text and Shift+F10 then shows the text menu instead of the box's options.
public class Run250PopupKeysAndMoreOptionsTests
{
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

    [Fact]
    public void PopupKeys_IsTrueOnlyWhenFocusSitsOutsideTheRoot()
    {
        var body = Method(Read(Path.Combine("Services", "PopupKeys.cs")), "FocusIsOutside");
        Assert.Contains("FocusManager.GetFocusedElement(xamlRoot)", body);
        Assert.Contains("VisualTreeHelper.GetParent(cur)", body);
        Assert.Contains("ReferenceEquals(cur, root)) return false", body);
        // No root or no focus: the handler keeps working as before.
        Assert.Contains("root?.XamlRoot is not { } xamlRoot) return false", body);
        Assert.Contains("is not DependencyObject focused) return false", body);
    }

    [Theory]
    [InlineData("Views/History/HistoryWindow.xaml.cs", "OnRootPreviewKeyDown", "RootGrid")]
    [InlineData("Views/History/HistoryWindow.xaml.cs", "OnRootKeyDown", "RootGrid")]
    [InlineData("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs", "OnRootGridKeyDown", "RootGrid")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "OnRootKeyDown", "RootGrid")]
    [InlineData("Views/SuiteHubView.xaml.cs", "OnHubKeyDown", "this")]
    [InlineData("Views/Ocr/OcrInspectionStudioWindow.xaml.cs", "OnRootKeyDown", "RootGrid")]
    public void WindowKeyHandlers_LeaveKeysPressedInsideAnOpenMenuAlone(string file, string handler, string root)
    {
        var body = Method(Read(file.Replace('/', Path.DirectorySeparatorChar)), handler);
        var guard = body.IndexOf($"if (PopupKeys.FocusIsOutside({root})) return;", StringComparison.Ordinal);
        Assert.True(guard >= 0, $"{handler} has no popup guard");
        // Before any key is acted on, Esc and the Notification check included.
        var firstKey = body.IndexOf("e.Key", StringComparison.Ordinal);
        Assert.True(firstKey < 0 || guard < firstKey, $"{handler} reads a key before the guard");
    }

    [Fact]
    public void SuiteHubKeyHandler_StillSeesHandledKeys_SoTheGuardMatters()
    {
        Assert.Contains("AddHandler(KeyDownEvent, new KeyEventHandler(OnHubKeyDown), true);",
            Read(Path.Combine("Views", "SuiteHubView.xaml.cs")));
    }

    [Fact]
    public void TextBoxMenu_TextMenuOffersMoreOptions()
    {
        var code = Read(Path.Combine("Services", "TextBoxMenu.cs"));
        var attach = Method(code, "Attach");
        Assert.Contains("bar.Opening += (_, _) => AddMoreOptions(bar, box, optionsMenu);", attach);

        var add = Method(code, "AddMoreOptions");
        // Only the box the menu is opening on, and once per opening.
        Assert.Contains("ReferenceEquals(bar.Target, box)) return;", add);
        Assert.Contains("Tag: MoreOptionsTag }) return;", add);
        Assert.Contains("Label = \"More options\"", add);
        Assert.Contains("bar.SecondaryCommands.Add(more);", add);
        // The text menu closes first, then the options menu opens on the same box.
        var hide = add.IndexOf("bar.Hide();", StringComparison.Ordinal);
        var show = add.IndexOf("DispatcherQueue.TryEnqueue(() => optionsMenu.ShowAt(box))", StringComparison.Ordinal);
        Assert.True(hide >= 0 && show > hide);
    }
    [Fact]
    public void HistoryWindow_AsksWhereFocusIsOnlyOnceItHasAXamlRoot()
    {
        var code = Read(Path.Combine("Views", "History", "HistoryWindow.xaml.cs"));
        // The first load runs from the constructor; GetFocusedElement(null) threw into the load's catch.
        Assert.Contains("RootGrid.XamlRoot is { } root ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root)", code);
        Assert.DoesNotContain("GetFocusedElement(RootGrid.XamlRoot)", code);
        Assert.Contains("FocusedElement() is not DependencyObject focused) return;", Method(code, "HandFocusOffCollapsingDiffTools"));
    }
}
