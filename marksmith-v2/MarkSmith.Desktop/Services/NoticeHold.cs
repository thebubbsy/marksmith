using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace MarkSmith.Services;

// Keeps a self-closing notice open while someone is reading it: the pointer resting on it, or
// keyboard focus on its close button, pauses the close timer. Leaving restarts the full interval
// rather than closing at once, so moving off a half-read message doesn't snatch it away.
public static class NoticeHold
{
    private sealed class State { public bool Pointer; public bool Focus; }
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    public static void Attach(FrameworkElement notice, DispatcherTimer timer, Func<bool> shouldResume)
    {
        var state = States.GetOrCreateValue(notice);

        void Hold() => timer.Stop();
        void Release()
        {
            if (state.Pointer || state.Focus) return;
            if (shouldResume()) timer.Start();
        }

        notice.PointerEntered += (_, _) => { state.Pointer = true; Hold(); };
        notice.PointerExited += (_, _) => { state.Pointer = false; Release(); };
        notice.PointerCanceled += (_, _) => { state.Pointer = false; Release(); };
        notice.GotFocus += (_, _) => { state.Focus = true; Hold(); };
        notice.LostFocus += (_, _) => { state.Focus = false; Release(); };
        // Closing the bar under the pointer (its X button) may never raise PointerExited; a stale
        // hold would keep every later notice open for good.
        if (notice is Microsoft.UI.Xaml.Controls.InfoBar bar)
            bar.Closed += (_, _) => { state.Pointer = false; state.Focus = false; };
    }

    // True while the pointer or focus is on the notice: a new message shown then must not start a
    // countdown under the reader's pointer. Leaving starts it.
    public static bool IsHeld(FrameworkElement notice) =>
        States.TryGetValue(notice, out var state) && (state.Pointer || state.Focus);
}
