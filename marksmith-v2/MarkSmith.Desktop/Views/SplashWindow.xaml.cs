using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
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
    private bool _closed;

    public SplashWindow()
    {
        InitializeComponent();
        Title = "MarkSmith";

        try
        {
            var uri = new Uri("ms-appx:///Assets/LaunchVideo.mp4");
            Player.Source = MediaSource.CreateFromUri(uri);
            Player.MediaPlayer.MediaEnded += (_, _) => SafeClose();
            Player.MediaPlayer.MediaFailed += (_, _) => SafeClose(); // corrupt/missing asset — never hang
            Player.MediaPlayer.Play();
        }
        catch
        {
            // Any synchronous setup failure must not block the app — but we must NOT Close() here:
            // App attaches splash.Closed -> ShowMainWindow AFTER this constructor returns, so a
            // synchronous Close would fire with no handler and the main window would never appear.
            // Defer the close to the dispatcher queue (runs after App has wired the continuation).
            DispatcherQueue.TryEnqueue(SafeClose);
            return;
        }

        CenterWindow(960, 540);
        _ = AutoCloseAsync();
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e) => SafeClose();

    private async Task AutoCloseAsync()
    {
        await Task.Delay(12_000);
        SafeClose();
    }

    // Close() is not idempotent; every path funnels through here so a second Close (timeout after
    // the video ended, a click racing the timer) can never throw or double-fire.
    private void SafeClose()
    {
        if (_closed) return;
        _closed = true;
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
}
