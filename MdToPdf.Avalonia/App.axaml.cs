using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MdToPdf.Avalonia.Hosting;
using MdToPdf.Avalonia.Views;

namespace MdToPdf.Avalonia;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private bool _exitRequested;
    private LocalAssetServer? _assetServer;

    // Application isn't part of the visual tree, so x:Name codegen doesn't reach TrayIcon —
    // fetch it via the TrayIcon.Icons attached-property list set in App.axaml instead.
    internal TrayIcon? AppTrayIcon
    {
        get
        {
            var icons = TrayIcon.GetIcons(this);
            return (icons != null && icons.Count > 0) ? icons[0] : null;
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppServices.License.Load(); // resolve Free / Trial / Pro before any UI reads entitlements

            // Must start before anything renders HTML — every render page's <script>/<link> URLs
            // are built from MdToPdf.Services.WebAssets.Base at call time.
            _assetServer = new LocalAssetServer(Path.Combine(AppContext.BaseDirectory, "Assets", "web"));
            _assetServer.Start();
            MdToPdf.Services.WebAssets.Base = _assetServer.BaseUrl;

            var window = new MainWindow { DataContext = AppServices.ViewModel };
            AppServices.ViewModel.Host = window;
            AppServices.ViewModel.Prompts = window;
            _mainWindow = window;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _assetServer?.Dispose();

            // Mirrors the WinUI build: closing the window with "Keep running in system tray" on
            // hides it instead of exiting (watchers/API would keep running here too, once ported).
            window.Closing += (_, e) =>
            {
                if (AppServices.ViewModel.MinimizeToTray && !_exitRequested)
                {
                    e.Cancel = true;
                    window.Hide();
                    if (AppTrayIcon is { } tray) tray.IsVisible = true;
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayShowClick(object? sender, EventArgs e)
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.Activate();
        if (AppTrayIcon is { } tray) tray.IsVisible = false;
    }

    private void OnTrayExitClick(object? sender, EventArgs e)
    {
        _exitRequested = true;
        if (AppTrayIcon is { } tray) tray.IsVisible = false;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
