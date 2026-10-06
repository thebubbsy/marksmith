using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MarkSmith.Services;

/// <summary>
/// Keeps a custom (ExtendsContentIntoTitleBar) title bar's content clear of the system caption
/// buttons. Without this, anything right-aligned in the bar — Shape Studio's Clear / Export
/// buttons, SmartArt Studio's Insert button — renders underneath minimise/maximise/close.
/// </summary>
public static class TitleBarInsets
{
    /// <summary>
    /// Pads <paramref name="titleBar"/> on the right by the width of the caption buttons (plus a
    /// little breathing room), re-measured whenever the bar resizes or moves between monitors with
    /// a different DPI. The bar's existing left/top/bottom padding is preserved.
    /// </summary>
    public static void Reserve(Window window, Grid titleBar, double gap = 12)
    {
        var basePadding = titleBar.Padding;

        void Update()
        {
            if (titleBar.XamlRoot is null) return;
            var scale = titleBar.XamlRoot.RasterizationScale;
            if (scale <= 0) scale = 1;
            var rightInset = window.AppWindow.TitleBar.RightInset / scale;
            titleBar.Padding = new Thickness(basePadding.Left, basePadding.Top, rightInset + gap, basePadding.Bottom);
        }

        titleBar.Loaded += (_, _) =>
        {
            Update();
            titleBar.XamlRoot.Changed += (_, _) => Update();
        };
        titleBar.SizeChanged += (_, _) => Update();
    }
}
