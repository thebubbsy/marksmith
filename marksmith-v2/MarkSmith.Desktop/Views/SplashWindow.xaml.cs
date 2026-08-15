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
        ConfigureFramelessWindow();

        try
        {
            var splashPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "splash_video.mp4");
            var launchPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "LaunchVideo.mp4");
            var videoPath = System.IO.File.Exists(splashPath) ? splashPath : launchPath;
            var uri = new Uri(videoPath);

            Player.Source = MediaSource.CreateFromUri(uri);
            Player.MediaPlayer.MediaEnded += (_, _) => SafeClose();
            Player.MediaPlayer.MediaFailed += (_, _) => SafeClose();
            Player.MediaPlayer.MediaOpened += (_, _) =>
            {
                try { Player.MediaPlayer.PlaybackSession.PlaybackRate = 2.5; } catch { }
            };

            Player.MediaPlayer.Play();
            try { Player.MediaPlayer.PlaybackSession.PlaybackRate = 2.5; } catch { }
        }
        catch
        {
            DispatcherQueue.TryEnqueue(SafeClose);
            return;
        }

        CenterWindow(960, 540);
        _ = AutoCloseAsync();
    }

    private void ConfigureFramelessWindow()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }
        }
        catch { }
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e) => SafeClose();

    private async Task AutoCloseAsync()
    {
        await Task.Delay(12_000);
        SafeClose();
    }

    private void SafeClose()
    {
        if (_closed) return;
        _closed = true;
        if (DispatcherQueue.HasThreadAccess)
        {
            Close();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Close);
        }
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
        catch { }
    }
}
