using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using MarkSmith.Services;
using MarkSmith.ViewModels.History;
using Xunit;

namespace MarkSmith.Tests;

// Run #237: Version History's menus promised F5 for Refresh but no key handler existed; F5 now
// refreshes, and every Refresh (F5, the menus, double-clicking the Documents header) stays on the
// document being viewed and says what it found. The main window's three pane splitters, Settings'
// page list and the extension guide link open their right-click menus from Shift+F10 / the Menu
// key, and Settings' page menu shows the real Ctrl+1 to Ctrl+7 shortcut.
public class Run237RefreshKeyAndSplitterMenusTests : IDisposable
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_run237_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

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

    private static string HistoryCode => Read("Views", "History", "HistoryWindow.xaml.cs");

    // ── Version History: F5 ───────────────────────────────────────────────

    [Fact]
    public void History_menus_that_show_F5_have_a_key_behind_them()
    {
        var code = HistoryCode;
        Assert.Contains("KeyboardAcceleratorTextOverride = \"F5\"", code);
        var keyDown = Method(code, "OnRootKeyDown");
        Assert.Matches(@"e\.Key == VirtualKey\.F5 && !isCtrl && !isAlt\)\s*\{[^}]*RefreshHistoryAsync\(\)", keyDown);
        // Ignored while a dialog is up, like every other window key.
        Assert.True(keyDown.IndexOf("if (_dialogOpen) return;") < keyDown.IndexOf("VirtualKey.F5"));
    }

    [Fact]
    public void History_every_refresh_path_shares_one_method()
    {
        var code = HistoryCode;
        // The version menu, the Documents-header menu and the header's double-click.
        Assert.Equal(2, Regex.Matches(code, @"refresh\.Click \+= async \(_, _\) => await RefreshHistoryAsync\(\);").Count);
        Assert.Contains("RefreshHistoryAsync()", Method(code, "OnDocumentsHeaderDoubleTapped"));
        Assert.DoesNotContain("LoadCommand", Method(code, "OnDocumentsHeaderDoubleTapped"));
        Assert.DoesNotContain("LoadCommand.ExecuteAsync(null)", Method(code, "OnDocumentsHeaderRightTapped"));

        var refresh = Method(code, "RefreshHistoryAsync");
        Assert.Contains("if (_vm.IsLoading) return;", refresh);
        Assert.Contains("_vm.ShowFileAsync(file.Summary.FilePath)", refresh);
        // A failed read shows its own error; the confirmation must not paper over it.
        Assert.Contains("if (!_vm.HasLoadError)", refresh);
        Assert.Contains("_vm.ShowNotice(", refresh);
    }

    [Fact]
    public void History_documents_header_tooltip_names_F5()
    {
        var header = Named(Xaml("Views", "History", "HistoryWindow.xaml"), "DocumentsHeaderBorder");
        Assert.Contains("F5", header.Attributes().Single(a => a.Name.LocalName == "ToolTipService.ToolTip").Value);
    }

    [Fact]
    public async Task Refreshing_the_viewed_document_stays_on_it_and_picks_up_new_versions()
    {
        var service = new VersionHistoryService(Path.Combine(_dir, "history"));
        const string editorFile = @"C:\docs\r237-editor.md";
        const string otherFile = @"C:\docs\r237-other.md";
        await service.CaptureAsync(editorFile, "editor one", "opened");
        await service.CaptureAsync(otherFile, "other one", "opened");
        // The store normalises paths, so match on the file name.
        static bool IsOther(FileSummaryViewModel f) => f.Summary.FilePath.EndsWith("r237-other.md", StringComparison.OrdinalIgnoreCase);

        var vm = new HistoryWindowViewModel(_ => "<html/>", _ => Task.FromResult(true), service, initialFilePath: editorFile);
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.SelectFileAsync(vm.Files.Single(f => IsOther(f)));
        Assert.True(IsOther(vm.SelectedFile!));

        await service.CaptureAsync(otherFile, "other two", "edited");
        // What RefreshHistoryAsync runs when a document is showing.
        await vm.ShowFileAsync(vm.SelectedFile.Summary.FilePath);

        Assert.True(IsOther(vm.SelectedFile!));
        Assert.Equal("2 versions", vm.SelectedFile.VersionCountLabel);
        Assert.False(vm.HasLoadError);
    }

    // ── Main window splitters ─────────────────────────────────────────────

    [Theory]
    [InlineData("LeftSplitter", "OnLeftSplitterRightTapped")]
    [InlineData("RightSplitter", "OnRightSplitterRightTapped")]
    [InlineData("SplitViewSplitter", "OnSplitViewSplitterRightTapped")]
    public void Splitter_preset_menus_open_from_the_keyboard(string name, string handler)
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.Contains($"({name}, {handler}),", code);

        var splitter = Named(Xaml("MainWindow.xaml"), name);
        Assert.Equal(handler, (string?)splitter.Attribute("RightTapped"));
        var tip = splitter.Attributes().Single(a => a.Name.LocalName == "ToolTipService.ToolTip").Value;
        Assert.Contains("Shift+F10", tip);
        Assert.Contains("arrow keys", tip);
        // "Double-tap" is touch talk; the tooltip is read by mouse users.
        Assert.DoesNotContain("Double-tap", tip);

        // The menu anchors to the splitter for a keyboard request instead of the window corner.
        Assert.Contains("KeyboardContextMenu.PointIn(e, ", Method(code, handler));
    }

    // ── Settings page list ────────────────────────────────────────────────

    [Fact]
    public void Settings_page_list_menu_opens_from_the_keyboard()
    {
        var code = Read("Views", "SettingsView.xaml.cs");
        Assert.Matches(@"foreach \(var navItem in Nav\.MenuItems\.OfType<NavigationViewItem>\(\)\)\s*KeyboardContextMenu\.Bridge\(navItem, OnNavItemRightTapped\);", code);

        var items = Xaml("Views", "SettingsView.xaml").Descendants().Where(e => e.Name.LocalName == "NavigationViewItem").ToList();
        Assert.Equal(7, items.Count);
        Assert.All(items, i => Assert.Equal("OnNavItemRightTapped", (string?)i.Attribute("RightTapped")));
    }

    [Fact]
    public void Settings_page_menu_shows_the_real_page_shortcut()
    {
        var code = Read("Views", "SettingsView.xaml.cs");
        var menu = Method(code, "OnNavItemRightTapped");
        Assert.Contains("var pageIndex = Nav.MenuItems.IndexOf(item);", menu);
        Assert.Contains("pageIndex is >= 0 and < 7 ? $\"Ctrl+{pageIndex + 1}\" : \"\"", menu);
        // The shortcut it names is the one OnSettingsKeyDown handles (Ctrl or Alt + 1..7).
        Assert.Contains("e.Key is >= Windows.System.VirtualKey.Number1 and <= Windows.System.VirtualKey.Number7", Method(code, "OnSettingsKeyDown"));
        Assert.Contains("KeyboardContextMenu.PointIn(e, item)", menu);
    }

    // ── Extension setup guide link ────────────────────────────────────────

    [Fact]
    public void Extension_guide_link_menu_opens_from_the_keyboard()
    {
        var link = Named(Xaml("Views", "ExtensionSetupView.xaml"), "GuideHyperlink");
        Assert.Equal("HyperlinkButton", link.Name.LocalName);
        Assert.Equal("OnGuideHyperlinkRightTapped", (string?)link.Attribute("RightTapped"));
        Assert.Contains("Shift+F10", link.Attributes().Single(a => a.Name.LocalName == "ToolTipService.ToolTip").Value);

        var code = Read("Views", "ExtensionSetupView.xaml.cs");
        Assert.Contains("(GuideHyperlink, OnGuideHyperlinkRightTapped),", code);
        Assert.Contains("KeyboardContextMenu.PointIn(e, target)", Method(code, "OnGuideHyperlinkRightTapped"));
    }
}
