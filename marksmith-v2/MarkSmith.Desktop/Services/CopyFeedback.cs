using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Services;

// Copy, then swap the button's icon for a green tick for a moment: the only feedback a copy gets
// inside a dialog, where the main window's status bar is hidden. Shared by Settings, Suite Hub, and
// the extension setup guide so every Copy button in a dialog answers the same way.
public static class CopyFeedback
{
    // Success: a green tick. Failure (the clipboard is busy or blocked): an amber warning with the
    // reason in the button's tooltip, instead of the click silently doing nothing.
    public static async void CopyWithTick(string text, FontIcon icon)
    {
        if (string.IsNullOrEmpty(text)) return;
        var error = await ClipboardWriter.TrySetTextAsync(text);
        ApplyIconTick(icon, FindButton(icon), error);
    }

    public static void CopyWithTick(Button button, FontIcon icon, string text)
        => CopyWithTick(text, icon);

    public static async void CopyWithTick(Button button, string text, string copiedLabel = "\u2713 Copied")
    {
        if (string.IsNullOrEmpty(text)) return;
        var error = await ClipboardWriter.TrySetTextAsync(text);
        ApplyButtonTick(button, error, copiedLabel);
    }

    public static void ApplyButtonTick(Button button, string? error, string copiedLabel = "\u2713 Copied")
    {
        if (button.Content is string currentText)
        {
            if (button.Tag is not string originalText)
            {
                originalText = currentText;
                button.Tag = originalText;
            }
            button.Content = error is null ? copiedLabel : "\u26A0 Busy";
            var timer = button.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(error is null ? 1400 : 3000);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                button.Content = originalText;
                button.Tag = null;
            };
            timer.Start();
        }
        else if (button.Content is FontIcon fontIcon)
        {
            ApplyIconTick(fontIcon, button, error);
        }
        else if (button.Content is Panel panel)
        {
            var icon = panel.Children.OfType<FontIcon>().FirstOrDefault();
            if (icon is not null)
            {
                ApplyIconTick(icon, button, error);
            }
        }
    }

    public static void ApplyIconTick(FontIcon icon, ButtonBase? host, string? error)
    {
        // A second click while the tick shows must not record the tick as the icon to restore.
        if (icon.Tag is not string original)
        {
            original = icon.Glyph;
            icon.Tag = original;
        }
        icon.Glyph = error is null ? "\uE73E" : "\uE7BA"; // CheckMark / Warning
        ThemeBrush.Set(icon, FontIcon.ForegroundProperty,
            error is null ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        var tooltip = host is null ? null : ToolTipService.GetToolTip(host);
        if (host is not null && error is not null) ToolTipService.SetToolTip(host, $"Nothing was copied. {error}");
        var timer = icon.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(error is null ? 1400 : 4000);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            icon.Glyph = original;
            icon.Tag = null;
            icon.ClearValue(FontIcon.ForegroundProperty);
            if (host is not null && error is not null) ToolTipService.SetToolTip(host, tooltip);
        };
        timer.Start();
    }

    private static ButtonBase? FindButton(DependencyObject element)
    {
        for (var d = VisualTreeHelper.GetParent(element); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase button) return button;
        return null;
    }
}
