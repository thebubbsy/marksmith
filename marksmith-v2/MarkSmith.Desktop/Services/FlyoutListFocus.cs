using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MarkSmith.Services;

/// <summary>
/// A flyout whose main content is a pick list (the studios' colour-scheme flyouts) opened with
/// focus on the list's first row and no focus ring, and a selection further down the list could
/// sit out of view. On open this scrolls the current choice into view and focuses its row, with a
/// keyboard focus ring when the opener was pressed from the keyboard (Enter, Space, Alt+Down).
/// A mouse open still draws no ring.
/// </summary>
public static class FlyoutListFocus
{
    public static void Attach(FlyoutBase flyout, ListViewBase list, Control opener)
    {
        // Read before the flyout takes focus: by Opened the opener has already lost it.
        var how = FocusState.Programmatic;
        flyout.Opening += (_, _) => how = RingIfKeyboard(opener);
        flyout.Opened += (_, _) =>
            // Enqueued: the flyout focuses its first element on open, and an Opening handler may
            // have just rebuilt the list's items.
            list.DispatcherQueue.TryEnqueue(() => FocusCurrent(list, how));
    }

    internal static FocusState RingIfKeyboard(Control opener) =>
        opener.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;

    internal static bool FocusCurrent(ListViewBase list, FocusState how)
    {
        if (list.XamlRoot is null) return false; // closed again before this ran
        var item = list.SelectedItem ?? (list.Items.Count > 0 ? list.Items[0] : null);
        if (item is null) return false;
        list.ScrollIntoView(item);
        list.UpdateLayout();
        return list.ContainerFromItem(item) is Control row && row.Focus(how);
    }
}
