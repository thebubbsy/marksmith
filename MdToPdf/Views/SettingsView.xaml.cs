using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MdToPdf.Plugins;

namespace MdToPdf.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext = App.ViewModel;
        VersionText.Text = $"Version {App.Updates.CurrentVersion}";
        RefreshLicenseUi();
        BuildPluginCards();
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

    // ---- Plugins tab ----
    // One card per registered plugin (built-ins + any plugin.json dropped into
    // %LOCALAPPDATA%\MdToPdf\Plugins\<id>\), generated in code rather than a DataTemplate so this
    // and the Avalonia SettingsView share one obvious pattern for the install/remove/progress wiring.

    private void BuildPluginCards()
    {
        PluginsPanel.Children.Clear();
        foreach (var plugin in App.Plugins.All)
            PluginsPanel.Children.Add(BuildPluginCard(plugin));

        if (App.Plugins.LoadWarnings.Count > 0)
        {
            PluginWarnings.Text = "Some plugin folders were skipped:\n" + string.Join("\n", App.Plugins.LoadWarnings);
            PluginWarnings.Visibility = Visibility.Visible;
        }
    }

    private UIElement BuildPluginCard(IMarksmithPlugin plugin)
    {
        var title = new TextBlock { Text = plugin.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var description = new TextBlock
        {
            Text = plugin.Description,
            Opacity = 0.7, FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var fences = plugin is IDiagramPlugin diagram
            ? new TextBlock
            {
                Text = "Code blocks: " + string.Join(", ", diagram.FenceLanguages.Select(l => "```" + l)),
                Opacity = 0.55, FontSize = 11,
            }
            : null;

        var status = new TextBlock { Text = "", Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var ring = new ProgressRing { IsActive = false, Width = 18, Height = 18 };
        var installButton = new Button { Content = "Install" };
        var removeButton = new Button { Content = "Remove" };

        void Refresh()
        {
            var installed = plugin.State == PluginInstallState.Installed;
            installButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            removeButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
            if (installed && string.IsNullOrEmpty(status.Text)) status.Text = "Installed.";
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
                DispatcherQueue.TryEnqueue(() => status.Text = $"Downloading… {percent}%");
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
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0),
        };
        buttons.Children.Add(ring);
        buttons.Children.Add(installButton);
        buttons.Children.Add(removeButton);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        return new Border
        {
            Style = (Style)Application.Current.Resources["PipelineCardStyle"],
            Padding = new Thickness(16, 12, 16, 12),
            Child = grid,
        };
    }
}
