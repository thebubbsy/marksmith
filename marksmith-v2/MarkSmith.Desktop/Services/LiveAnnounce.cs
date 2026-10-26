using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MarkSmith.Services;

/// <summary>
/// Status lines that change while the user works ("Exported to …", "3 of 12", "Moved 2 shapes").
/// Setting <c>AutomationProperties.LiveSetting</c> alone does nothing in WinUI: the element's peer
/// has to raise <see cref="AutomationEvents.LiveRegionChanged"/> each time, so screen readers
/// never heard the main window's status message, the Find bar's match count, or the studios'
/// status bars. <see cref="Track"/> raises it whenever the watched text changes, coalescing a
/// burst (a drag, typing in Find) into one announcement of the final text.
/// </summary>
public static class LiveAnnounce
{
    /// <summary>Quiet time before a changed text is announced.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private static readonly ConditionalWeakTable<UIElement, DispatcherQueueTimer> Timers = new();

    /// <summary>
    /// Announces <paramref name="source"/>'s text whenever it changes. <paramref name="announcer"/>
    /// is the element screen readers know (a button wrapping the text); it defaults to the text.
    /// </summary>
    public static void Track(TextBlock source, UIElement? announcer = null)
    {
        var target = announcer ?? source;
        AutomationProperties.SetLiveSetting(target, AutomationLiveSetting.Polite);
        source.RegisterPropertyChangedCallback(TextBlock.TextProperty, (s, _) =>
        {
            if (s is TextBlock { Text: { Length: > 0 } text } && !string.IsNullOrWhiteSpace(text))
                Schedule(target);
        });
    }

    private static void Schedule(UIElement target)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        if (!Timers.TryGetValue(target, out var timer))
        {
            var queue = target.DispatcherQueue;
            if (queue is null) return;
            timer = queue.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = Settle;
            timer.Tick += (_, _) => Raise(target);
            Timers.AddOrUpdate(target, timer);
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>Raises LiveRegionChanged on <paramref name="element"/> now.</summary>
    public static void Raise(UIElement element)
    {
        if (element.Visibility != Visibility.Visible || element.XamlRoot is null) return;
        (FrameworkElementAutomationPeer.FromElement(element)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(element))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
