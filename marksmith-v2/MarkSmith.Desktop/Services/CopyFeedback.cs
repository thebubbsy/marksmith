using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Services;

// Copy, then swap the button's icon for a green tick for a moment: the only feedback a copy gets
// inside a dialog, where the main window's status bar is hidden. Shared by Settings and the
// extension setup guide so every Copy button in a dialog answers the same way.
public static class CopyFeedback
{
    public static bool CopyWithTick(string text, FontIcon icon)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch { return false; }

        // A second click while the tick shows must not record the tick as the icon to restore.
        if (icon.Tag is not string original)
        {
            original = icon.Glyph;
            icon.Tag = original;
        }
        icon.Glyph = "\uE73E"; // CheckMark
        ThemeBrush.Set(icon, FontIcon.ForegroundProperty, "SystemFillColorSuccessBrush");
        var timer = icon.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            icon.Glyph = original;
            icon.Tag = null;
            icon.ClearValue(FontIcon.ForegroundProperty);
        };
        timer.Start();
        return true;
    }
}
