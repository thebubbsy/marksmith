using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace MarkSmith.Views;

/// <summary>
/// Launch intro: plays the branded video (Assets/LaunchVideo.mp4) before the main window appears.
/// Click to skip, auto-closes on MediaEnded, and has a hard 12s timeout so a missing or corrupt
/// asset can never delay the app. The skip flag lives in Settings (SkipLaunchVideo).
/// </summary>
public sealed partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Title = "MarkSmith";

        try
        {
            var uri = new Uri("ms-appx:///Assets/LaunchVideo.mp4");
            Player.Source = MediaSource.CreateFromUri(uri);
            Player.MediaPlayer.MediaEnded += (_, _) => Close();
            Player.MediaPlayer.MediaFailed += (_, _) => Close(); // corrupt/missing asset — never hang
            Player.MediaPlayer.Play();
        }
        catch
        {
            // Any setup failure (e.g. unsupported codec) must not block the app.
            Close();
            return;
        }

        CenterWindow(960, 540);
        _ = AutoCloseAsync();
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e) => Close();

    private async Task AutoCloseAsync()
    {
        await Task.Delay(12_000);
        Close();
    }

    private void CenterWindow(int width, int height)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            if (displayArea is not null)
            {
                var area = displayArea.WorkArea;
                appWindow.Move(new Windows.Graphics.PointInt32(
                    area.X + (area.Width - width) / 2,
                    area.Y + (area.Height - height) / 2));
            }
        }
        catch { /* centering is best-effort */ }
    }

    // (Window.Closed is an event, not overridable — App wires it to show the main window.)
}
