using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MdToPdf.Models;
using MdToPdf.Plugins;

namespace MdToPdf.Avalonia.Views;

// Cross-platform port of MdToPdf/Views/SettingsView.xaml (WinUI). Hosted as the Content of an
// FAContentDialog by MainWindow.OnSettingsClick, mirroring how the WinUI build hosts this same
// UserControl inside a ContentDialog. Shares AppServices (License/Updates/ViewModel) with the WinUI
// build via MdToPdf.Core, so activation, trial state, and update checks behave identically.
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext = AppServices.ViewModel;
        VersionText.Text = $"Version {AppServices.Updates.CurrentVersion}";
        RefreshLicenseUi();
        RefreshPlantUmlUi();

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

    private static IMarksmithPlugin PlantUml => AppServices.Plugins.All.First(p => p.Id == "plantuml");

    private void RefreshPlantUmlUi()
    {
        var installed = PlantUml.State == PluginInstallState.Installed;
        PlantUmlInstallButton.IsVisible = !installed;
        PlantUmlUninstallButton.IsVisible = installed;
        PlantUmlStatus.Text = installed ? "Installed." : "";
    }

    private async void OnInstallPlantUml(object? sender, RoutedEventArgs e)
    {
        PlantUmlInstallButton.IsEnabled = false;
        PlantUmlInstallRing.IsActive = true;
        PlantUmlStatus.Text = "Downloading…";

        // Install downloads ~50MB in a tight loop that reports progress far faster than the UI
        // needs — throttle to whole-percent updates so this doesn't flood the dispatcher.
        var lastPercent = -1;
        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100);
            if (percent == lastPercent) return;
            lastPercent = percent;
            Dispatcher.UIThread.Post(() => PlantUmlStatus.Text = $"Downloading… {percent}%");
        });

        try
        {
            await PlantUml.InstallAsync(progress, CancellationToken.None);
            PlantUmlStatus.Text = "Installed.";
        }
        catch (Exception ex)
        {
            PlantUmlStatus.Text = $"Install failed: {ex.Message}";
        }

        PlantUmlInstallRing.IsActive = false;
        PlantUmlInstallButton.IsEnabled = true;
        RefreshPlantUmlUi();
    }

    private void OnUninstallPlantUml(object? sender, RoutedEventArgs e)
    {
        PlantUml.Uninstall();
        PlantUmlStatus.Text = "Removed.";
        RefreshPlantUmlUi();
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

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
