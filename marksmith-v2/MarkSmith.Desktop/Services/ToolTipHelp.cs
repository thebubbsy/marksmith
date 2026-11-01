using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MarkSmith.Services;

/// <summary>
/// Gives HoverPolish-tracked controls their tooltip's words as help text, so the keys a tooltip names
/// ("Search versions (Ctrl+F)", "Next change hunk (F7)") reach keyboard and screen reader users,
/// who never see a tooltip. A control that already has help text of its own keeps it. Tooltips
/// that change later (set from code or bound) carry the help text with them, but only while the
/// help text is still the one copied here: help text set by code afterwards is never overwritten.
/// </summary>
public static class ToolTipHelp
{
    // The help text this class last copied onto the control, or null when it copied none.
    private static readonly DependencyProperty CopiedProperty = DependencyProperty.RegisterAttached(
        "Copied", typeof(string), typeof(ToolTipHelp), new PropertyMetadata(null));

    private static readonly DependencyProperty WatchedProperty = DependencyProperty.RegisterAttached(
        "Watched", typeof(bool), typeof(ToolTipHelp), new PropertyMetadata(false));

    internal static string? TipText(object? tip) => tip switch
    {
        string s => s,
        ToolTip { Content: string s } => s,
        _ => null,
    };

    /// <summary>Called for every control HoverPolish walks, so each tracked window, dialog and
    /// opened flyout is covered, including content created after it loaded.</summary>
    internal static void Sync(Control control)
    {
        if (TipText(ToolTipService.GetToolTip(control)) is null && control.GetValue(CopiedProperty) is null) return;
        Update(control);
        if ((bool)control.GetValue(WatchedProperty)) return;
        control.SetValue(WatchedProperty, true);
        control.RegisterPropertyChangedCallback(ToolTipService.ToolTipProperty, (d, _) => Update((Control)d));
    }

    private static void Update(Control control)
    {
        var copied = (string?)control.GetValue(CopiedProperty);
        var next = ToolTipHelpRule.HelpFor(TipText(ToolTipService.GetToolTip(control)), AutomationProperties.GetName(control),
            AutomationProperties.GetHelpText(control), copied);
        if (next is null) return;
        AutomationProperties.SetHelpText(control, next);
        control.SetValue(CopiedProperty, next.Length == 0 ? null : next);
    }
}
