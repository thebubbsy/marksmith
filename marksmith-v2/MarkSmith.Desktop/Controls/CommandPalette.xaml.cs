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

    private readonly IReadOnlyList<PaletteCommand> _commands;
    private readonly PaletteRecents _recents;
    private readonly TaskCompletionSource<PaletteCommand?> _result = new();
    private Popup? _popup;
    private XamlRoot? _root;
    private Control? _focusBefore;
    private bool _closing;

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
        };
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
        if (chosen is not null) _recents.Push(chosen.Label);
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

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();
        _searching = query.Length > 0;
        var sections = CommandPaletteSections.Build(_commands, query, c => c.Label, c => c.Category, c => c.Keywords, _recents.Labels);
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

        if (count > 0) Results.SelectedIndex = 0;
        Results.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatches.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;
        NoMatchesTitle.Text = $"Nothing matches \u201C{query}\u201D";
        CountText.Text = !_searching ? $"{count} commands"
            : count == 0 ? "" // the empty state already says so
            : count == 1 ? "1 match" : $"{count} matches";
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => Refresh();

    // The label is filled here rather than bound, so the letters the query matched can be bold:
    // with abbreviations ("exppdf") it wasn't obvious why a row was listed. The category tag only
    // shows in search results; with nothing typed the row already sits under its heading.
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.InRecycleQueue || e.Item is not PaletteCommand c) return;
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

    // ---- Keyboard ----

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Close(null);
                break;
            case VirtualKey.K when ctrl:
                Close(null);
                break;
            case VirtualKey.Enter:
                if (Results.SelectedItem is PaletteCommand c) Close(c);
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
                // The list is driven from the search box; tabbing into it only lost the caret.
                SearchBox.Focus(FocusState.Keyboard);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Move(int by)
    {
        var count = Results.Items.Count;
        if (count == 0) return;
        var index = Results.SelectedIndex < 0 ? 0 : Results.SelectedIndex + by;
        // Arrows wrap around (Up on the first row is the last row); paging stops at the ends.
        index = Math.Abs(by) == 1 ? (index % count + count) % count : Math.Clamp(index, 0, count - 1);
        Results.SelectedIndex = index;
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
