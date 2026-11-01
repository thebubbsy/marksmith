using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
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
/// With <c>typeAheadOpens</c>, a letter or digit on the closed box opens the list too, and the
/// key goes on to the open list's type-ahead, which highlights the match without picking it.
/// Typing "b" on the closed recent-files box used to open the first file starting with b.
/// </para>
/// </summary>
public static class ComboArrowOpens
{
    public static void Attach(ComboBox combo, bool typeAheadOpens = false)
    {
        combo.PreviewKeyDown += (_, e) =>
        {
            if (combo.IsDropDownOpen) return;
            if (OpensList(e.Key))
            {
                e.Handled = true;
                combo.IsDropDownOpen = true;
            }
            else if (typeAheadOpens && IsTypeAheadKey(e.Key) && !ModifierDown())
            {
                // Not handled: the now-open list takes the letter as type-ahead.
                combo.IsDropDownOpen = true;
            }
        };
    }

    internal static bool OpensList(VirtualKey key) =>
        key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End
            or VirtualKey.PageUp or VirtualKey.PageDown;

    internal static bool IsTypeAheadKey(VirtualKey key) =>
        key is >= VirtualKey.A and <= VirtualKey.Z
            or >= VirtualKey.Number0 and <= VirtualKey.Number9
            or >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9;

    // Ctrl+V, Alt+letter (access keys) and the like are shortcuts, not type-ahead.
    private static bool ModifierDown() =>
        IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftWindows);

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
