namespace MarkSmith.Services;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;

/// <summary>
/// Automatically scrolls single-line path and URL text boxes horizontally to the end
/// while unfocused, so the leaf file name, folder name, or URL endpoint is immediately visible
/// instead of cutting off at the drive root or domain start.
/// When the user focuses the control, natural cursor placement and selection behavior is preserved.
/// </summary>
public static class TextBoxEndScroll
{
    private static readonly ConditionalWeakTable<TextBox, object> Attached = new();

    /// <summary>
    /// Attaches unfocused end-scrolling to a TextBox on Loaded, SizeChanged, LostFocus, and TextChanged.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    public static void Attach(TextBox box)
    {
        if (box is null) return;
        if (Attached.TryGetValue(box, out _)) return;
        Attached.Add(box, true);

        void Scroll() => ScrollToEndIfUnfocused(box);

        box.Loaded += (_, _) => Scroll();
        box.SizeChanged += (_, _) => Scroll();
        box.LostFocus += (_, _) => Scroll();
        box.TextChanged += (_, _) => Scroll();
        Scroll();
    }

    /// <summary>
    /// Scrolls the TextBox's internal ScrollViewer to the end if the control is currently unfocused.
    /// </summary>
    public static void ScrollToEndIfUnfocused(TextBox box)
    {
        if (box is null) return;
        if (box.FocusState != FocusState.Unfocused) return;
        var sv = FindScrollViewer(box);
        if (sv is null || sv.ScrollableWidth <= 0) return;
        sv.ChangeView(sv.ScrollableWidth, null, null, disableAnimation: true);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }
}
