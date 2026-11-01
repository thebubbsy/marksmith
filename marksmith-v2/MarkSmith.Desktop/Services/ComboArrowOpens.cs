using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace MarkSmith.Services;

/// <summary>
/// A closed ComboBox changes its selection on Up/Down (and Home/End/PageUp/PageDown), and some
/// combo boxes act the moment their selection changes. Down on the closed recent-files box opened
/// the next file and tucked the panel away; on the AI-agent folder presets it overwrote the watch
/// folder; on the OCR reclassify boxes it reclassified the element once per key. For those boxes
/// these keys open the list instead, where arrows only move the highlight and Enter picks.
/// <para>
/// Type-ahead has the same problem, open or closed: a letter selects the first matching row, so
/// typing "b" on the recent-files box opened the first file starting with b. With
/// <c>typeAheadText</c> (the text a row is known by), letters and digits open the list and move
/// only the highlight there (<see cref="TypeAheadSearch"/>); Enter still picks.
/// </para>
/// </summary>
public static class ComboArrowOpens
{
    private sealed class TypeAheadState
    {
        public string Prefix = "";
        public DateTime At = DateTime.MinValue;
    }

    private static readonly ConditionalWeakTable<ComboBox, TypeAheadState> States = new();

    public static void Attach(ComboBox combo, Func<object, string?>? typeAheadText = null)
    {
        bool TryTypeAhead(KeyRoutedEventArgs e)
        {
            if (typeAheadText is null || KeyChar(e.Key) is not { } ch || ModifierDown()) return false;
            e.Handled = true;
            TypeAhead(combo, ch, typeAheadText);
            return true;
        }

        if (typeAheadText is not null)
        {
            // Keys in the open list go to its popup, which isn't on the combo's own key route.
            combo.DropDownOpened += (_, _) =>
            {
                if (FindPopup(combo)?.Child is UIElement list && Hooked.TryAdd(list, Marker))
                    list.PreviewKeyDown += (_, e) => TryTypeAhead(e);
            };
        }

        combo.PreviewKeyDown += (_, e) =>
        {
            if (TryTypeAhead(e)) return;
            if (combo.IsDropDownOpen) return;
            if (OpensList(e.Key))
            {
                e.Handled = true;
                combo.IsDropDownOpen = true;
            }
        };
    }

    private static readonly object Marker = new();
    private static readonly ConditionalWeakTable<UIElement, object> Hooked = new();

    private static Popup? FindPopup(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Popup popup) return popup;
            if (FindPopup(child) is { } found) return found;
        }
        return null;
    }

    internal static bool OpensList(VirtualKey key) =>
        key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End
            or VirtualKey.PageUp or VirtualKey.PageDown;

    internal static char? KeyChar(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => (char)('a' + (key - VirtualKey.A)),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => (char)('0' + (key - VirtualKey.Number0)),
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => (char)('0' + (key - VirtualKey.NumberPad0)),
        _ => null,
    };

    private static void TypeAhead(ComboBox combo, char ch, Func<object, string?> text)
    {
        var state = States.GetOrCreateValue(combo);
        var now = DateTime.UtcNow;
        state.Prefix = TypeAheadSearch.Extend(state.Prefix, ch, now - state.At);
        state.At = now;
        var prefix = state.Prefix;

        if (combo.IsDropDownOpen)
        {
            HighlightMatch(combo, prefix, text);
            return;
        }
        // The list focuses its selected row as it opens; move the highlight after that.
        void Opened(object? sender, object args)
        {
            combo.DropDownOpened -= Opened;
            combo.DispatcherQueue.TryEnqueue(() => HighlightMatch(combo, prefix, text));
        }
        combo.DropDownOpened += Opened;
        combo.IsDropDownOpen = true;
    }

    private static void HighlightMatch(ComboBox combo, string prefix, Func<object, string?> text)
    {
        if (!combo.IsDropDownOpen || combo.XamlRoot is null) return;
        var names = combo.Items.Select(text).ToList();
        var current = FocusManager.GetFocusedElement(combo.XamlRoot) is ComboBoxItem focused
            ? combo.IndexFromContainer(focused)
            : combo.SelectedIndex;
        var index = TypeAheadSearch.Find(names, current, prefix);
        if (index >= 0 && combo.ContainerFromIndex(index) is ComboBoxItem row)
            row.Focus(FocusState.Keyboard);
    }

    // Ctrl+V, Alt+letter (access keys) and the like are shortcuts, not type-ahead.
    private static bool ModifierDown() =>
        IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftWindows);

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
