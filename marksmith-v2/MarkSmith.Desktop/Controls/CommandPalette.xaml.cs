using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MarkSmith.Services;
using Windows.System;

namespace MarkSmith.Controls;

/// <summary>
/// One row in the command palette. Shortcut is display-only, read from Core's KeyboardShortcuts
/// (the accelerators live in XAML).
/// </summary>
public sealed record PaletteCommand(string Label, string Category, Func<Task> Run, string Shortcut = "", string Keywords = "")
{
    public Visibility ShortcutVisibility => Shortcut.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // The same picture as the command's button or menu item (Core CommandIcons), so the
    // palette isn't a wall of text. Letterforms (H1, AB) use the text font, like the menus.
    public string Icon => CommandIcons.ForPalette(Label, Category);
    public FontFamily IconFont => CommandIcons.IsGlyph(Icon) ? SymbolFont : LetterFont;
    public double IconSize => CommandIcons.IsGlyph(Icon) ? 16 : 11;
    public Windows.UI.Text.FontWeight IconWeight => CommandIcons.IsGlyph(Icon)
        ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
    private static readonly FontFamily SymbolFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily LetterFont = new("Segoe UI Variable Text, Segoe UI");

    // What a screen reader announces for the row (it read the record's debug text,
    // "PaletteCommand { Label = …, Run = System.Func`1[…] }").
    public override string ToString() => Shortcut.Length > 0 ? $"{Label}, {Category}, {Shortcut}" : $"{Label}, {Category}";
}

/// <summary>
/// The Ctrl+K command palette. It used to be a stock ContentDialog: a "Command palette" title, a
/// full-width Cancel button under the list, opened in the middle of the window over the work, and
/// with nothing typed it listed ~80 commands in one run whose first screen was always the Export
/// rows. It is now a launcher: a card that drops from under the title bar's search button, a
/// borderless search box, the commands you ran last at the top and every other command under its
/// category heading, a footer that says how to drive it, and Esc / a click outside to close.
/// <para>
/// It lives in a <see cref="Popup"/>, not a ContentDialog, so it closes itself whenever a real
/// dialog is about to open (<see cref="HoverPolish.DialogOpening"/>) and swallows the window's
/// keyboard accelerators while open (Ctrl+B in the search box used to bold the document).
/// </para>
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private static CommandPalette? _open;
    private static readonly HashSet<string> RemovedRecentLabels = new(StringComparer.Ordinal);

    private readonly IReadOnlyList<PaletteCommand> _commands;
    private readonly PaletteRecents _recents;
    private readonly TaskCompletionSource<PaletteCommand?> _result = new();
    private Popup? _popup;
    private XamlRoot? _root;
    private Control? _focusBefore;
    private bool _closing;

    public static string FormatCountText(bool searching, int count) =>
        !searching ? (count == 1 ? "1 command" : $"{Math.Max(0, count)} commands")
        : count <= 0 ? ""
        : count == 1 ? "1 match" : $"{count} matches";

    public static string FormatCommandClipboardText(PaletteCommand command) =>
        string.IsNullOrWhiteSpace(command.Shortcut)
            ? command.Label
            : $"{command.Label} ({command.Shortcut})";

    private CommandPalette(IReadOnlyList<PaletteCommand> commands, PaletteRecents recents)
    {
        _commands = commands;
        _recents = recents;
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        // While the palette is up the window's shortcuts belong to it: Ctrl+K closes it and the
        // rest (Ctrl+B, Ctrl+E…) must not act on the document behind it.
        ProcessKeyboardAccelerators += (_, e) => e.Handled = true;
        Scrim.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) };
        Loaded += (_, _) =>
        {
            Scrim.Opacity = 0.6;
            SearchBox.Focus(FocusState.Programmatic);
            HideBuiltInDeleteButton(SearchBox);
        };
    }

    private static void HideBuiltInDeleteButton(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: "DeleteButton" } btn)
            {
                btn.Visibility = Visibility.Collapsed;
                btn.Width = 0;
                btn.MinWidth = 0;
                btn.MaxWidth = 0;
                btn.Opacity = 0;
                btn.IsHitTestVisible = false;
                return;
            }
            HideBuiltInDeleteButton(child);
        }
    }

    /// <summary>True while a palette is showing (a second Ctrl+K closes it instead).</summary>
    public static bool IsOpen => _open is not null;

    /// <summary>Closes an open palette without running anything.</summary>
    public static void CloseOpen() => _open?.Close(null);

    /// <summary>
    /// Shows the palette over <paramref name="root"/> and returns the command the person picked,
    /// or null. The chosen command is recorded as recently used; running it is the caller's job,
    /// after the palette has gone and focus is back where it was.
    /// </summary>
    public static Task<PaletteCommand?> ShowAsync(XamlRoot root, IReadOnlyList<PaletteCommand> commands, PaletteRecents recents)
    {
        if (_open is not null)
        {
            _open.Close(null);
            return Task.FromResult<PaletteCommand?>(null);
        }

        var palette = new CommandPalette(commands, recents);
        palette._root = root;
        // Opened with a click on the title bar's search button, focus was on that button, and after
        // Esc you typed into nothing. A button isn't somewhere to go back to; the editor is.
        palette._focusBefore = FocusManager.GetFocusedElement(root) is Control c and not ButtonBase ? c : null;
        palette.Refresh();

        var popup = new Popup { XamlRoot = root, Child = palette, IsLightDismissEnabled = false };
        palette._popup = popup;
        palette.FitTo(root);
        root.Changed += palette.OnRootChanged;
        HoverPolish.DialogOpening += palette.OnDialogOpening;
        _open = palette;
        popup.IsOpen = true;
        HoverPolish.Track(palette);
        return palette._result.Task;
    }

    private void FitTo(XamlRoot root)
    {
        Width = root.Size.Width;
        Height = root.Size.Height;
        Card.Width = Math.Max(280, Math.Min(600, root.Size.Width - 32));
        // Leave room for the footer and the card's margin on a short window.
        Results.MaxHeight = Math.Max(140, Math.Min(420, root.Size.Height - 200));
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!sender.IsHostVisible) { Close(null); return; }
        FitTo(sender);
    }

    private void OnDialogOpening(ContentDialog _) => Close(null);

    private void Close(PaletteCommand? chosen)
    {
        if (_closing) return;
        _closing = true;
        if (_root is not null) _root.Changed -= OnRootChanged;
        HoverPolish.DialogOpening -= OnDialogOpening;
        if (_popup is not null) _popup.IsOpen = false;
        _open = null;
        if (chosen is not null)
        {
            RemovedRecentLabels.Remove(chosen.Label);
            _recents.Push(chosen.Label);
            if (RemovedRecentLabels.Count > 0) PersistRemainingRecents();
        }
        HoverPolish.RestoreFocus(_root, _focusBefore);
        _result.TrySetResult(chosen);
    }

    // ---- Results ----

    private sealed class Group : List<PaletteCommand>
    {
        public Group(string key, IEnumerable<PaletteCommand> items) : base(items) => Key = key;
        public string Key { get; }
    }

    private bool _searching;

    private IReadOnlyList<string> EffectiveRecentLabels() =>
        _recents.Labels.Where(l => !RemovedRecentLabels.Contains(l)).ToList();

    private bool IsRecent(string label) =>
        !RemovedRecentLabels.Contains(label) && _recents.Labels.Contains(label, StringComparer.Ordinal);

    private void RemoveRecent(string label)
    {
        RemovedRecentLabels.Add(label);
        if (_recents.Labels is IList<string> mutable)
        {
            mutable.Remove(label);
        }
        PersistRemainingRecents();
        Refresh();
        AnnounceFooterStatus("Removed from Recently used");
    }

    private void ClearRecents()
    {
        foreach (var label in _recents.Labels) RemovedRecentLabels.Add(label);
        if (_recents.Labels is IList<string> mutable)
        {
            mutable.Clear();
        }
        PersistRemainingRecents();
        Refresh();
        AnnounceFooterStatus("Cleared Recently used");
    }

    private void PersistRemainingRecents()
    {
        try
        {
            var path = typeof(PaletteRecents)
                .GetField("_path", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(_recents) as string;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(AppPaths.ConfigDir, "palette-recent.json");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(EffectiveRecentLabels()));
        }
        catch { }
    }

    private void AnnounceFooterStatus(string status)
    {
        CountText.Text = status;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CountText, CountText.Text);
    }

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();
        _searching = query.Length > 0;
        if (ClearSearchButton is not null)
            ClearSearchButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var sections = CommandPaletteSections.Build(_commands, query, c => c.Label, c => c.Category, c => c.Keywords, EffectiveRecentLabels());
        var count = sections.Sum(s => s.Items.Count);

        if (_searching)
        {
            // One ranked run: no headings, and each row says which category it came from.
            Results.ItemsSource = sections.Count == 0 ? new List<PaletteCommand>() : sections[0].Items.ToList();
        }
        else
        {
            var source = new CollectionViewSource
            {
                IsSourceGrouped = true,
                Source = sections.Select(s => new Group(s.Header, s.Items)).ToList(),
            };
            Results.ItemsSource = source.View;
        }

        if (count > 0)
        {
            Results.SelectedIndex = 0;
            if (Results.SelectedItem is PaletteCommand selectedCmd)
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Results, $"Commands — {selectedCmd}");
            }
        }
        else
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Results, "Commands");
        }
        Results.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatches.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;
        NoMatchesTitle.Text = $"Nothing matches \u201C{query}\u201D";
        CountText.Text = FormatCountText(_searching, count);

        if (count == 0)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NoMatches, NoMatchesTitle.Text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(NoMatches, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        }
        else
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CountText, CountText.Text);
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        HideBuiltInDeleteButton(SearchBox);
        Refresh();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus(FocusState.Programmatic);
    }

    // The label is filled here rather than bound, so the letters the query matched can be bold:
    // with abbreviations ("exppdf") it wasn't obvious why a row was listed. The category tag only
    // shows in search results; with nothing typed the row already sits under its heading.
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.InRecycleQueue)
        {
            if (e.ItemContainer is FrameworkElement fe) ToolTipService.SetToolTip(fe, null);
            return;
        }
        if (e.Item is not PaletteCommand c) return;
        if (e.ItemContainer is FrameworkElement container)
        {
            var tip = string.IsNullOrWhiteSpace(c.Shortcut)
                ? $"{c.Label} ({c.Category})"
                : $"{c.Label} ({c.Category}) · {c.Shortcut}";
            ToolTipService.SetToolTip(container, tip);
        }
        if (e.ItemContainer.ContentTemplateRoot is not Grid { Children.Count: > 2 } row) return;
        if (row.Children[1] is TextBlock text)
        {
            text.Inlines.Clear();
            var at = 0;
            foreach (var (start, length) in CommandSearch.Highlights(c.Label, SearchBox.Text))
            {
                if (start > at) text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = c.Label[at..start] });
                text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = c.Label.Substring(start, length),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    // Accent as well as weight: SemiBold alone next to Regular was too quiet to
                    // see at a glance which letters matched.
                    Foreground = ThemeBrush.For(text, "AccentTextFillColorPrimaryBrush"),
                });
                at = start + length;
            }
            if (at < c.Label.Length) text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = c.Label[at..] });
        }
        row.Children[2].Visibility = _searching ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteCommand c) Close(c);
    }

    private void OnResultsRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = FindClickedCommand(e.OriginalSource as DependencyObject) ?? Results.SelectedItem as PaletteCommand;
        if (item is null) return;
        Results.SelectedItem = item;
        e.Handled = true;
        ShowCommandContextMenu(item, Results, e.GetPosition(Results));
    }

    private static PaletteCommand? FindClickedCommand(DependencyObject? source)
    {
        for (var d = source; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListViewItem { Content: PaletteCommand cmd }) return cmd;
            if (d is FrameworkElement { DataContext: PaletteCommand dc }) return dc;
        }
        return null;
    }

    private void ShowCommandContextMenu(PaletteCommand cmd, FrameworkElement anchor, Windows.Foundation.Point? position)
    {
        var menu = new MenuFlyout();

        var runItem = new MenuFlyoutItem
        {
            Text = "Run command (Enter)",
            Icon = new FontIcon { Glyph = "\uE768" }
        };
        runItem.Click += (_, _) => Close(cmd);
        menu.Items.Add(runItem);

        var copyNameItem = new MenuFlyoutItem
        {
            Text = "Copy command name",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyNameItem.Click += async (_, _) =>
        {
            var err = await ClipboardWriter.TrySetTextAsync(cmd.Label);
            AnnounceFooterStatus(err is null ? "Copied command name" : $"Copy failed: {err}");
            SearchBox.Focus(FocusState.Programmatic);
        };
        menu.Items.Add(copyNameItem);

        if (!string.IsNullOrWhiteSpace(cmd.Shortcut))
        {
            var copyShortcutItem = new MenuFlyoutItem
            {
                Text = $"Copy keyboard shortcut ({cmd.Shortcut})",
                Icon = new FontIcon { Glyph = "\uE765" }
            };
            copyShortcutItem.Click += async (_, _) =>
            {
                var err = await ClipboardWriter.TrySetTextAsync(cmd.Shortcut);
                AnnounceFooterStatus(err is null ? $"Copied shortcut {cmd.Shortcut}" : $"Copy failed: {err}");
                SearchBox.Focus(FocusState.Programmatic);
            };
            menu.Items.Add(copyShortcutItem);
        }

        if (IsRecent(cmd.Label))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var removeRecentItem = new MenuFlyoutItem
            {
                Text = "Remove from Recently used (Ctrl+Delete)",
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            removeRecentItem.Click += (_, _) =>
            {
                RemoveRecent(cmd.Label);
                SearchBox.Focus(FocusState.Programmatic);
            };
            menu.Items.Add(removeRecentItem);

            if (EffectiveRecentLabels().Count > 1)
            {
                var clearRecentsItem = new MenuFlyoutItem
                {
                    Text = "Clear Recently used",
                    Icon = new FontIcon { Glyph = "\uE894" }
                };
                clearRecentsItem.Click += (_, _) =>
                {
                    ClearRecents();
                    SearchBox.Focus(FocusState.Programmatic);
                };
                menu.Items.Add(clearRecentsItem);
            }
        }

        if (position.HasValue)
            menu.ShowAt(anchor, position.Value);
        else if (Results.ContainerFromItem(cmd) is FrameworkElement container)
            menu.ShowAt(container);
        else
            menu.ShowAt(anchor);
    }

    private void OnSearchBoxRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var runItem = new MenuFlyoutItem
        {
            Text = "Run selected command (Enter)",
            Icon = new FontIcon { Glyph = "\uE768" }
        };
        runItem.Click += (_, _) =>
        {
            if (Results.SelectedItem is PaletteCommand c) Close(c);
        };
        menu.Items.Add(runItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = "Copy selected command (Ctrl+C)",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) =>
        {
            if (Results.SelectedItem is PaletteCommand c)
            {
                await CopySelectedCommandAsync(c);
            }
        };
        menu.Items.Add(copyItem);

        var clearItem = new MenuFlyoutItem
        {
            Text = "Clear search query (Esc)",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        clearItem.Click += (_, _) =>
        {
            SearchBox.Text = "";
            SearchBox.Focus(FocusState.Programmatic);
        };
        menu.Items.Add(clearItem);

        var pasteItem = new MenuFlyoutItem
        {
            Text = "Paste into search (Ctrl+V)",
            Icon = new FontIcon { Glyph = "\uE77F" }
        };
        pasteItem.Click += async (_, _) =>
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    var text = await content.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        SearchBox.Text = text;
                        SearchBox.SelectionStart = SearchBox.Text.Length;
                    }
                }
            }
            catch { }
        };
        menu.Items.Add(pasteItem);

        if (sender is FrameworkElement fe)
            menu.ShowAt(fe, e.GetPosition(fe));
        else
            menu.ShowAt(SearchBox);
    }

    // ---- Keyboard ----

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.Application || (shift && !ctrl && e.Key == VirtualKey.F10))
        {
            if (Results.SelectedItem is PaletteCommand contextCmd)
            {
                ShowCommandContextMenu(contextCmd, Results, null);
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (SearchBox.Text.Length > 0)
                {
                    SearchBox.Text = "";
                }
                else
                {
                    Close(null);
                }
                break;
            case VirtualKey.K when ctrl:
                Close(null);
                break;
            case VirtualKey.C when ctrl && SearchBox.SelectionLength == 0 && Results.SelectedItem is PaletteCommand copyCmd:
                _ = CopySelectedCommandAsync(copyCmd);
                break;
            case VirtualKey.Delete when ctrl && Results.SelectedItem is PaletteCommand recCmd && IsRecent(recCmd.Label):
                RemoveRecent(recCmd.Label);
                break;
            case VirtualKey.Enter:
                if (Results.SelectedItem is PaletteCommand c) Close(c);
                else if (NoMatches.Visibility == Visibility.Visible) OnClearSearchClick(this, new RoutedEventArgs());
                break;
            case VirtualKey.Home when ctrl:
                Move(-9999);
                break;
            case VirtualKey.End when ctrl:
                Move(9999);
                break;
            case VirtualKey.Down:
                Move(1);
                break;
            case VirtualKey.Up:
                Move(-1);
                break;
            case VirtualKey.PageDown:
                Move(8);
                break;
            case VirtualKey.PageUp:
                Move(-8);
                break;
            case VirtualKey.Tab:
                if (NoMatches.Visibility == Visibility.Visible && _root is not null)
                {
                    var focused = FocusManager.GetFocusedElement(_root);
                    if (ReferenceEquals(focused, ResetSearchButton))
                        SearchBox.Focus(FocusState.Keyboard);
                    else
                        ResetSearchButton.Focus(FocusState.Keyboard);
                }
                else
                {
                    // The list is driven from the search box; tabbing into it only lost the caret.
                    SearchBox.Focus(FocusState.Keyboard);
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private async Task CopySelectedCommandAsync(PaletteCommand cmd)
    {
        var err = await ClipboardWriter.TrySetTextAsync(FormatCommandClipboardText(cmd));
        AnnounceFooterStatus(err is null ? "Copied command" : $"Copy failed: {err}");
    }

    private void Move(int by)
    {
        var count = Results.Items.Count;
        if (count == 0) return;
        var index = Results.SelectedIndex < 0 ? 0 : Results.SelectedIndex + by;
        // Arrows wrap around (Up on the first row is the last row); paging stops at the ends.
        index = Math.Abs(by) == 1 ? (index % count + count) % count : Math.Clamp(index, 0, count - 1);
        Results.SelectedIndex = index;
        if (Results.SelectedItem is PaletteCommand selectedCmd)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Results, $"Commands — {selectedCmd}");
        }
        if (index == 0 && !_searching)
        {
            // ScrollIntoView stops at the first row and leaves its heading cut off.
            FindScrollViewer(Results)?.ChangeView(null, 0, null, true);
        }
        else
        {
            Results.ScrollIntoView(Results.SelectedItem);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private void OnScrimPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        Close(null);
    }

    private void OnEscClick(object sender, RoutedEventArgs e) => Close(null);
}
