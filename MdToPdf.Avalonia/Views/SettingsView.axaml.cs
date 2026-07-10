using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using MdToPdf.Models;
using MdToPdf.Plugins;

namespace MdToPdf.Avalonia.Views;

// Cross-platform port of MdToPdf/Views/SettingsView.xaml (WinUI). Hosted as the Content of an
// FAContentDialog by MainWindow.OnSettingsClick, mirroring how the WinUI build hosts this same
// UserControl inside a ContentDialog. Shares AppServices (License/Updates/Plugins/ViewModel) with
// the WinUI build via MdToPdf.Core, so activation, trial state, update checks, and plugin
// install/remove behave identically.
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext = AppServices.ViewModel;
        VersionText.Text = $"Version {AppServices.Updates.CurrentVersion}";
        RefreshLicenseUi();
        BuildPluginCards();

        // Keep the "Remove license" button in sync if entitlement state changes while this dialog
        // is open (e.g. a trial expiring), same intent as the WinUI SettingsView's license refresh.
        AppServices.License.Changed += OnLicenseChanged;
        DetachedFromVisualTree += (_, _) => AppServices.License.Changed -= OnLicenseChanged;
    }

    private void OnLicenseChanged() => RefreshLicenseUi();

    private void RefreshLicenseUi()
    {
        // "Remove license" only makes sense for an actual activated Pro key (not a trial).
        DeactivateButton.IsVisible = AppServices.License.State.Edition == Edition.Pro;
    }

    private async void OnActivateLicense(object? sender, RoutedEventArgs e)
    {
        ActivateButton.IsEnabled = false;
        var (ok, message) = await AppServices.License.ActivateAsync(KeyBox.Text);
        LicenseStatus.Text = message;
        LicenseStatus.IsVisible = true;
        if (ok) KeyBox.Text = "";
        RefreshLicenseUi();
        ActivateButton.IsEnabled = true;
    }

    private void OnBuyPro(object? sender, RoutedEventArgs e)
    {
        // WinUI's SettingsView uses Windows.System.Launcher.LaunchUriAsync (WinRT-only); the
        // cross-platform equivalent already used elsewhere in this build (MainWindow.OnUpgradeClick)
        // is Process.Start with UseShellExecute, which opens the URL in the OS default browser.
        try { OpenUrl(Services.LicenseService.StoreUrl); } catch { /* no browser / bad uri — ignore */ }
    }

    private void OnDeactivateLicense(object? sender, RoutedEventArgs e)
    {
        AppServices.License.Deactivate();
        LicenseStatus.Text = "License removed from this device.";
        LicenseStatus.IsVisible = true;
        RefreshLicenseUi();
    }

    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        CheckRing.IsActive = true;
        UpdateStatus.IsVisible = true;
        UpdateStatus.Text = "Checking…";
        DownloadLink.IsVisible = false;

        var result = await AppServices.Updates.CheckAsync();

        UpdateStatus.Text = result.Message;
        if (result.UpdateAvailable && !string.IsNullOrEmpty(result.ReleaseUrl))
        {
            DownloadLink.NavigateUri = new Uri(result.ReleaseUrl);
            DownloadLink.IsVisible = true;
        }

        CheckRing.IsActive = false;
        CheckButton.IsEnabled = true;
    }

    // ---- Plugins tab ----
    // One card per registered plugin (built-ins + any plugin.json dropped into
    // %LOCALAPPDATA%\MdToPdf\Plugins\<id>\), generated in code rather than a DataTemplate so this
    // and the WinUI SettingsView share one obvious pattern for the install/remove/progress wiring.

    private void BuildPluginCards()
    {
        PluginsPanel.Children.Clear();
        foreach (var plugin in AppServices.Plugins.All)
            PluginsPanel.Children.Add(BuildPluginCard(plugin));

        if (AppServices.Plugins.LoadWarnings.Count > 0)
        {
            PluginWarnings.Text = "Some plugin folders were skipped:\n" + string.Join("\n", AppServices.Plugins.LoadWarnings);
            PluginWarnings.IsVisible = true;
        }
    }

    private Control BuildPluginCard(IMarksmithPlugin plugin)
    {
        var title = new TextBlock { Text = plugin.Name, FontWeight = global::Avalonia.Media.FontWeight.SemiBold };
        var description = new TextBlock
        {
            Text = plugin.Description,
            Opacity = 0.7, FontSize = 12,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
        };
        var fences = plugin is IDiagramPlugin diagram
            ? new TextBlock
            {
                Text = "Code blocks: " + string.Join(", ", diagram.FenceLanguages.Select(l => "```" + l)),
                Opacity = 0.55, FontSize = 11,
            }
            : null;

        var status = new SelectableTextBlock { Text = "", Opacity = 0.7, FontSize = 12, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
        var ring = new FAProgressRing { IsActive = false, Width = 18, Height = 18 };
        var installButton = new Button { Content = "Install" };
        var removeButton = new Button { Content = "Remove" };

        void Refresh()
        {
            var installed = plugin.State == PluginInstallState.Installed;
            installButton.IsVisible = !installed;
            removeButton.IsVisible = installed;
            status.Text = installed ? "Installed." : status.Text;
        }

        installButton.Click += async (_, _) =>
        {
            installButton.IsEnabled = false;
            ring.IsActive = true;
            status.Text = "Downloading…";

            // Install downloads tens of MB in a tight loop that reports progress far faster than
            // the UI needs — throttle to whole-percent updates so this doesn't flood the dispatcher.
            var lastPercent = -1;
            var progress = new Progress<double>(p =>
            {
                var percent = (int)(p * 100);
                if (percent == lastPercent) return;
                lastPercent = percent;
                Dispatcher.UIThread.Post(() => status.Text = $"Downloading… {percent}%");
            });

            try
            {
                await plugin.InstallAsync(progress, CancellationToken.None);
                status.Text = "Installed.";
            }
            catch (Exception ex)
            {
                status.Text = $"Install failed: {ex.Message}";
            }

            ring.IsActive = false;
            installButton.IsEnabled = true;
            Refresh();
        };

        removeButton.Click += (_, _) =>
        {
            plugin.Uninstall();
            status.Text = "Removed.";
            Refresh();
        };

        Refresh();

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(description);
        if (fences != null) text.Children.Add(fences);
        text.Children.Add(status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top, Margin = new global::Avalonia.Thickness(12, 0, 0, 0),
        };
        buttons.Children.Add(ring);
        buttons.Children.Add(installButton);
        buttons.Children.Add(removeButton);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        // Border chrome (corner radius / stroke brush) comes from the "plugin-card" style in
        // SettingsView.axaml so the DynamicResource stays declared in XAML where it belongs.
        var card = new Border { Padding = new global::Avalonia.Thickness(16, 12), Child = grid };
        card.Classes.Add("plugin-card");
        return card;
    }

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
