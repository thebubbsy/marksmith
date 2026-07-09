using Avalonia.Controls;
using Avalonia.Interactivity;
using MdToPdf.Models;

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

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
