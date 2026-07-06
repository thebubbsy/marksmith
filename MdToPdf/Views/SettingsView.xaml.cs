using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MdToPdf.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext = App.ViewModel;
        VersionText.Text = $"Version {App.Updates.CurrentVersion}";
        RefreshLicenseUi();
    }

    private void RefreshLicenseUi()
    {
        // "Remove license" only makes sense for an actual activated Pro key (not a trial).
        DeactivateButton.Visibility = App.License.State.Edition == Models.Edition.Pro
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnActivateLicense(object sender, RoutedEventArgs e)
    {
        ActivateButton.IsEnabled = false;
        var (ok, message) = await App.License.ActivateAsync(KeyBox.Text);
        LicenseStatus.Text = message;
        LicenseStatus.Visibility = Visibility.Visible;
        if (ok) KeyBox.Text = "";
        RefreshLicenseUi();
        ActivateButton.IsEnabled = true;
    }

    private async void OnBuyPro(object sender, RoutedEventArgs e)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(Services.LicenseService.StoreUrl)); }
        catch { /* no browser / bad uri — ignore */ }
    }

    private void OnDeactivateLicense(object sender, RoutedEventArgs e)
    {
        App.License.Deactivate();
        LicenseStatus.Text = "License removed from this device.";
        LicenseStatus.Visibility = Visibility.Visible;
        RefreshLicenseUi();
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        CheckRing.IsActive = true;
        UpdateStatus.Visibility = Visibility.Visible;
        UpdateStatus.Text = "Checking…";
        DownloadLink.Visibility = Visibility.Collapsed;

        var result = await App.Updates.CheckAsync();

        UpdateStatus.Text = result.Message;
        if (result.UpdateAvailable && !string.IsNullOrEmpty(result.ReleaseUrl))
        {
            DownloadLink.NavigateUri = new Uri(result.ReleaseUrl);
            DownloadLink.Visibility = Visibility.Visible;
        }

        CheckRing.IsActive = false;
        CheckButton.IsEnabled = true;
    }
}
