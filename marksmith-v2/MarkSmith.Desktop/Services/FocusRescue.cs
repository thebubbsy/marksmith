using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Services;

/// <summary>
/// Toolbar buttons that disable or hide themselves as a result of being pressed (Undo after the
/// last step, Move up once the item reaches the top, Delete once the selection is gone, a link
/// inspector that closes when its link is deleted) used to drop keyboard focus out of the
/// toolbar: WinUI sends it to the window's first tab stop. When a member of a group loses focus
/// that way, focus moves to the next still-available member (then the previous one), else to the
/// fallback (the canvas or list the toolbar works on).
/// </summary>
public static class FocusRescue
{
    public static void Attach(Func<UIElement?>? fallback, params Control?[] group)
    {
        var members = group.Where(c => c is not null).Cast<Control>().ToArray();
        foreach (var member in members)
        {
            // Pointer users get no focus rectangle on the new target; keyboard users keep theirs.
            var how = FocusState.Keyboard;
            member.GotFocus += (s, _) =>
                how = ((Control)s).FocusState == FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard;
            member.LostFocus += (s, _) =>
            {
                var me = (Control)s;
                // Deferred: Undo disabling itself and Redo enabling arrive as separate changes,
                // and WinUI moves focus on after LostFocus.
                me.DispatcherQueue.TryEnqueue(() =>
                {
                    if (IsAvailable(me) || me.XamlRoot is null || !FocusWasLost(me)) return;
                    var target = NextAvailable(members, me);
                    if (target is not null && target.Focus(how)) return;
                    fallback?.Invoke()?.Focus(how);
                });
            };
        }
    }

    // Nothing holds focus, or WinUI has thrown it to the window's first tab stop. Focus the press
    // moved somewhere on purpose (a dialog, a flyout, the canvas, the next field) is left alone.
    private static bool FocusWasLost(Control me)
    {
        var focused = FocusManager.GetFocusedElement(me.XamlRoot);
        if (focused is null || ReferenceEquals(focused, me)) return true;
        if (focused is Control { IsEnabled: false }) return true;
        return me.XamlRoot.Content is DependencyObject content
            && ReferenceEquals(focused, FocusManager.FindFirstFocusableElement(content));
    }

    internal static Control? NextAvailable(Control[] members, Control me)
    {
        var i = Array.IndexOf(members, me);
        for (var k = i + 1; k < members.Length; k++)
            if (IsAvailable(members[k])) return members[k];
        for (var k = i - 1; k >= 0; k--)
            if (IsAvailable(members[k])) return members[k];
        return null;
    }

    /// <summary>
    /// A button that disables itself while its work runs (Rescan, Check for updates, Remove
    /// license, Activate) threw keyboard focus to the window's first tab stop for the whole wait,
    /// which in Settings also scrolled the page back to the top. Call before disabling it: focus
    /// waits on the first of <paramref name="parkOn"/> that takes it, and
    /// <see cref="Parked.Return"/> brings it back once the button is enabled again, unless the
    /// user has moved on in the meantime.
    /// </summary>
    public static Parked Park(Control button, params Control?[] parkOn)
    {
        var state = button.FocusState;
        if (state == FocusState.Unfocused) return default;
        var how = state == FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard;
        foreach (var spot in parkOn)
            if (spot is not null && IsAvailable(spot) && spot.Focus(how)) return new Parked(button, spot, how);
        return default;
    }

    public readonly record struct Parked(Control? Button, Control? Spot, FocusState How)
    {
        /// <summary>The button held focus when it was parked.</summary>
        public bool HadFocus => Spot is not null;

        /// <summary>
        /// Focus is still on the parking spot (or was lost): the user hasn't moved on, so the work
        /// finishing may place it. False when the button never had focus.
        /// </summary>
        public bool Waiting
        {
            get
            {
                if (Button is null || Spot is null || Button.XamlRoot is null) return false;
                var focused = FocusManager.GetFocusedElement(Button.XamlRoot);
                return ReferenceEquals(focused, Spot) || FocusWasLost(Button);
            }
        }

        public void Return()
        {
            if (Button is null || !IsAvailable(Button)) return;
            if (Waiting) Button.Focus(How);
        }
    }

    /// <summary>
    /// Focus is still on <paramref name="control"/>, or was lost (nothing holds it, or WinUI threw
    /// it to the window's first tab stop): the user hasn't moved on since.
    /// </summary>
    public static bool StillOn(Control control) => control.XamlRoot is not null && FocusWasLost(control);

    // Enabled, a tab stop, and shown: neither it nor any ancestor is collapsed.
    private static bool IsAvailable(Control c)
    {
        if (!c.IsEnabled || !c.IsTabStop || c.XamlRoot is null) return false;
        for (DependencyObject? d = c; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }
}
