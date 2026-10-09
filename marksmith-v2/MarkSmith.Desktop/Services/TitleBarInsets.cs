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

/// <summary>
/// Colours the system caption buttons (minimise, maximise, close) of a custom title bar to match
/// the theme of the content under them. Windows draws them in the Windows theme, so a window
/// whose content has its own theme (Document Galaxy's light maps, a forced test theme) got white
/// glyphs on a white bar or black on black. Re-applied whenever the content's theme changes.
/// </summary>
public static class CaptionButtons
{
    public static void Follow(Window window, FrameworkElement themeSource)
    {
        void Apply()
        {
            var bar = window.AppWindow.TitleBar;
            bool dark = themeSource.ActualTheme == ElementTheme.Dark;
            byte ink = dark ? (byte)0xFF : (byte)0x00;
            Windows.UI.Color Ink(byte alpha) => Windows.UI.Color.FromArgb(alpha, ink, ink, ink);

            bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            bar.ButtonForegroundColor = Ink(dark ? (byte)0xFF : (byte)0xE4);
            bar.ButtonInactiveForegroundColor = Ink(dark ? (byte)0x5D : (byte)0x5C);
            bar.ButtonHoverForegroundColor = Ink(dark ? (byte)0xFF : (byte)0xE4);
            bar.ButtonHoverBackgroundColor = Ink(dark ? (byte)0x15 : (byte)0x0F);
            bar.ButtonPressedForegroundColor = Ink(dark ? (byte)0xC5 : (byte)0x9E);
            bar.ButtonPressedBackgroundColor = Ink(dark ? (byte)0x0B : (byte)0x09);
        }

        Apply();
        themeSource.ActualThemeChanged += (_, _) => Apply();
    }
}
