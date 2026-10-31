using System;
using System.IO;
using Xunit;

namespace MarkSmith.Tests;

// Run #309:
// - Settings / side-panel rows: the right-click menu's "run the button" item goes through the
//   button's automation peer, so a Click-wired button (Company template's "Import .dotx…") runs;
//   it used to call Command only and did nothing;
// - rows whose action is a cluster of buttons (Google account, licence, a plugin's Install /
//   Remove) list each visible, enabled button by its accessible name;
// - a Settings card's Shift+F10 / Menu key inside a text box leaves the box's own menu alone;
// - a plugin card has one menu (Install / Remove, Copy plugin ID, Open plugins folder, Copy name)
//   instead of a ContextFlyout beside the card's own;
// - a plugin's status reads "Installed" after the download; a late progress report no longer
//   leaves "Downloading… 100%" over it;
// - the card menu's Turn on / Check items name Space and show the state, like the side panel's;
// - Outline flyout: Down from the filter shows the heading's focus ring, and Up on the first
//   heading returns to the filter.
public class Run309CardMenusAndOutlineKeysTests
{
    private const string Card = "MarkSmith.Desktop/Controls/SettingsCard.xaml.cs";
    private const string Row = "MarkSmith.Desktop/Controls/OptionRow.xaml.cs";
    private const string Items = "MarkSmith.Desktop/Controls/ActionMenuItems.cs";
    private const string Settings = "MarkSmith.Desktop/Views/SettingsView.xaml.cs";
    private const string Main = "MarkSmith.Desktop/MainWindow.xaml.cs";
    private const string MainXaml = "MarkSmith.Desktop/MainWindow.xaml";

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

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"missing: {start}");
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"missing: {end}");
        return text.Substring(a, b - a);
    }

    [Fact]
    public void ActionItems_InvokeThroughTheAutomationPeer_NotCommandOnly()
    {
        var invoke = Between(Read(Items), "private static void Invoke(Button button)", "private static T? FirstOf<T>");
        Assert.Contains("if (!button.IsEnabled || button.Visibility != Visibility.Visible) return;", invoke);
        Assert.Contains("FrameworkElementAutomationPeer.CreatePeerForElement(button) as IInvokeProvider)?.Invoke()", invoke);
        Assert.DoesNotContain("Command?.Execute", Read(Card));
        Assert.DoesNotContain("Command?.Execute", Read(Row));
    }

    [Fact]
    public void ActionItems_ListEveryVisibleEnabledButton_InAClusterToo()
    {
        var buttons = Between(Read(Items), "internal static IEnumerable<Button> Buttons(object? action)", "internal static string Label(");
        Assert.Contains("action is Button { Visibility: Visibility.Visible, IsEnabled: true } button", buttons);
        Assert.Contains("action is Panel { Visibility: Visibility.Visible } panel", buttons);
        Assert.Contains("foreach (var inner in Buttons(child))", buttons);
    }

    [Fact]
    public void ActionItems_LabelPrefersTheAccessibleName_ThenShownText()
    {
        var label = Between(Read(Items), "internal static string Label(Button button)", "private static string? Glyph(");
        int name = label.IndexOf("AutomationProperties.GetName(button)", StringComparison.Ordinal);
        int text = label.IndexOf("button.Content is string s", StringComparison.Ordinal);
        int block = label.IndexOf("FirstOf<TextBlock>(", StringComparison.Ordinal);
        Assert.True(name >= 0 && name < text && text < block, "name, then string content, then a TextBlock");
        Assert.Contains("return \"Run action\";", label);
    }

    [Fact]
    public void CardAndRow_UseTheSharedItems()
    {
        var card = Between(Read(Card), "private void ShowCardContextMenu(", "private static Control? FindFirstInput(");
        Assert.Contains("foreach (var item in ActionMenuItems.For(Action)) menu.Items.Add(item);", card);
        var row = Between(Read(Row), "private void ShowOptionContextMenu(", "private static Control? FindFirstInput(");
        Assert.Contains("foreach (var item in ActionMenuItems.For(Action)) { menu.Items.Add(item); any = true; }", row);
        Assert.Contains("if (any) menu.Items.Add(new MenuFlyoutSeparator());", row);
    }

    [Fact]
    public void Card_MenuKeyInATextBox_LeavesTheBoxMenu()
    {
        var keys = Between(Read(Card), "private void OnCardKeyDown(", "private void ShowCardContextMenu(");
        int guard = keys.IndexOf("if (IsInsideTextInput(XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject))", StringComparison.Ordinal);
        int show = keys.IndexOf("ShowCardContextMenu(null);", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < show, "the text-box check comes before the card menu opens");
    }

    [Fact]
    public void Card_ToggleAndCheckItems_NameSpaceAndShowState()
    {
        var menu = Between(Read(Card), "private void ShowCardContextMenu(", "else if (Action is ComboBox");
        Assert.Equal(2, CountOf(menu, "KeyboardAcceleratorTextOverride = \"Space\","));
        Assert.Contains("Glyph = toggle.IsOn ? \"\\uE73A\" : \"\\uE739\"", menu);
        Assert.Contains("Glyph = check.IsChecked == true ? \"\\uE73A\" : \"\\uE739\"", menu);
    }

    [Fact]
    public void Card_ExtraItemsJoinTheSameMenu_AfterASeparator()
    {
        var source = Read(Card);
        Assert.Contains("public System.Func<System.Collections.Generic.IEnumerable<MenuFlyoutItemBase>>? ExtraMenuItems { get; set; }", source);
        var menu = Between(source, "private void ShowCardContextMenu(", "private static Control? FindFirstInput(");
        Assert.Contains("if (ExtraMenuItems?.Invoke() is { } extra)", menu);
        Assert.Contains("if (first && menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());", menu);
        Assert.True(menu.IndexOf("ExtraMenuItems?.Invoke()", StringComparison.Ordinal) < menu.IndexOf("Copy setting name", StringComparison.Ordinal));
    }

    [Fact]
    public void PluginCard_HasOneMenu_WithCopyIdAndItsShortcut()
    {
        var plugin = Read(Settings);
        Assert.DoesNotContain("card.ContextFlyout = flyout;", plugin);
        var extra = Between(plugin, "card.ExtraMenuItems = () =>", "card.KeyDown += async (s, e) =>");
        Assert.Contains("Text = $\"Copy plugin ID ({plugin.Id})\",", extra);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+C\",", extra);
        Assert.Contains("Text = \"Open plugins folder\",", extra);
        // Ctrl+C and the menu item share one copy path.
        Assert.Equal(2, CountOf(plugin, "await CopyPluginIdAsync();"));
    }

    [Fact]
    public void PluginInstall_LateProgressReports_DoNotOverwriteInstalled()
    {
        var install = Between(Read(Settings), "var progress = new Progress<double>(p =>", "var ok = false;");
        Assert.Contains("if (percent == lastPercent || !installing) return;", install);
        Assert.Contains("DispatcherQueue.TryEnqueue(() => { if (installing) SetStatus($\"Downloading… {percent}%\"); });", install);
    }

    [Fact]
    public void Outline_DownFromFilter_FocusesTheListWithAKeyboardRing()
    {
        var filter = Between(Read(Main), "private void OnTocFilterKeyDown(", "private void OnTocListUpToFilter(");
        Assert.Contains("TocList.Focus(FocusState.Keyboard);", filter);
        Assert.DoesNotContain("TocList.Focus(FocusState.Programmatic);", filter);
    }

    [Fact]
    public void Outline_UpOnTheFirstHeading_ReturnsToTheFilter()
    {
        var main = Read(Main);
        Assert.Contains("TocList.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnTocListUpToFilter), true);", main);
        var up = Between(main, "private void OnTocListUpToFilter(", "private void OnTocListRightTapped(");
        Assert.Contains("if (e.Key != Windows.System.VirtualKey.Up) return;", up);
        Assert.Contains("TocFilterBox.Visibility != Visibility.Visible", up);
        Assert.Contains("!ReferenceEquals(TocList.Items[0], entry)", up);
        Assert.Contains("TocFilterBox.Focus(FocusState.Keyboard);", up);
        Assert.Contains("Up on its first heading comes back", Read(MainXaml));
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
