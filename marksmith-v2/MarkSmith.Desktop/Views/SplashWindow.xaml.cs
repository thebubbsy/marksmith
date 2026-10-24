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

    private CancellationTokenSource? _autoCloseCts = new();
    private bool _menuOpen;

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (point.Properties.IsRightButtonPressed)
        {
            // Do not dismiss on right-click; allow RightTapped to show context menu
            return;
        }
        SafeClose();
    }

    private void OnRootRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowSplashContextMenu(e.GetPosition(Root));
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Space or Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            SafeClose();
        }
        else if (e.Key == Windows.System.VirtualKey.F5)
        {
            e.Handled = true;
            ReplayVideo();
        }
        else if (e.Key == Windows.System.VirtualKey.M)
        {
            e.Handled = true;
            ToggleMute();
        }
    }

    private void ShowSplashContextMenu(Windows.Foundation.Point? position)
    {
        _autoCloseCts?.Cancel();
        _autoCloseCts = null;

        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();

        var continueItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Continue to MarkSmith",
            KeyboardAcceleratorTextOverride = "Space",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE76C" }
        };
        continueItem.Click += (_, _) => SafeClose();
        menu.Items.Add(continueItem);

        var replayItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Replay intro video",
            KeyboardAcceleratorTextOverride = "F5",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE72C" }
        };
        replayItem.Click += (_, _) => ReplayVideo();
        menu.Items.Add(replayItem);

        bool isMuted = Player.MediaPlayer.IsMuted;
        var muteItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = isMuted ? "Unmute audio" : "Mute audio",
            KeyboardAcceleratorTextOverride = "M",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = isMuted ? "\uE767" : "\uE74F" }
        };
        muteItem.Click += (_, _) => ToggleMute();
        menu.Items.Add(muteItem);

        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());

        var copyVersionItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = $"Copy version ({App.Updates.CurrentDisplayVersion})",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE8C8" }
        };
        // Copied the bare product name before (no version); the bubble says whether it worked.
        copyVersionItem.Click += (_, _) =>
        {
            var version = $"MarkSmith {App.Updates.CurrentDisplayVersion}";
            MarkSmith.Services.CopyFeedback.CopyWithBubble(Root, version, $"Copied {version}");
        };
        menu.Items.Add(copyVersionItem);

        menu.Opening += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            _ = AutoCloseAsync(6_000);
        };

        if (position.HasValue)
            menu.ShowAt(Root, position.Value);
        else
            menu.ShowAt(Root);
    }

    private void ReplayVideo()
    {
        try
        {
            Player.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
            Player.MediaPlayer.Play();
        }
        catch { }
    }

    private void ToggleMute()
    {
        try
        {
            Player.MediaPlayer.IsMuted = !Player.MediaPlayer.IsMuted;
        }
        catch { }
    }

    private async Task AutoCloseAsync(int delayMs = 12_000)
    {
        _autoCloseCts?.Cancel();
        var cts = new CancellationTokenSource();
        _autoCloseCts = cts;
        try
        {
            await Task.Delay(delayMs, cts.Token);
            if (!_menuOpen) SafeClose();
        }
        catch (OperationCanceledException) { }
    }

    private void SafeClose()
    {
        if (_closed) return;
        _closed = true;
        _autoCloseCts?.Cancel();
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
