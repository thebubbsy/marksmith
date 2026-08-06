using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using MarkSmith.Plugins;

namespace MarkSmith.Views;

public sealed partial class SettingsView : UserControl
{
    // Raised after a plugin is installed or removed so the host (MainWindow) can re-render the live
    // preview — a freshly installed diagram engine changes what the current document can render.
    public event Action? PluginsChanged;

    public SettingsView()
    {
        InitializeComponent();
        DataContext = App.ViewModel;
        VersionText.Text = $"Version {App.Updates.CurrentVersion}";
        RefreshLicenseUi();
        BuildPluginCards();
        App.License.Changed += OnLicenseChanged;
    }

    private void RefreshLicenseUi()
    {
        var ed = App.License.State.Edition;
        // "Remove license" only makes sense for an actual activated Pro key.
        DeactivateButton.Visibility = ed == Models.Edition.Pro ? Visibility.Visible : Visibility.Collapsed;
        // "Start trial" is available to Free users who haven't spent their one export.
        StartTrialButton.Visibility = ed == Models.Edition.Free ? Visibility.Visible : Visibility.Collapsed;
        // Always surface the resolved state (Free / Trial — ONE export remaining / Pro).
        LicenseStatus.Text = App.License.State.Status ?? "Free";
        LicenseStatus.Visibility = Visibility.Visible;
    }

    private void OnStartTrialClick(object sender, RoutedEventArgs e)
    {
        var (ok, message) = App.License.StartTrial();
        LicenseStatus.Text = message;
        LicenseStatus.Visibility = Visibility.Visible;
        RefreshLicenseUi();
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

    // Keep the License page live whenever the state changes (trial started/consumed, key
    // activated/removed, reset to free).
    private void OnLicenseChanged()
    {
        var dq = DispatcherQueue;
        if (dq is null) { RefreshLicenseUi(); return; }
        dq.TryEnqueue(RefreshLicenseUi);
    }

    // Cloud Storage Sync (Task 9): re-detect the local cloud-drive sync folders and refresh the picker.
    private void OnRescanCloud(object sender, RoutedEventArgs e) => App.ViewModel.RefreshCloudProviders();

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        CheckRing.IsActive = true;
        UpdateStatus.Visibility = Visibility.Visible;
        UpdateStatus.Text = "Checking…";
        DownloadLink.Visibility = Visibility.Collapsed;

        var result = await App.Updates.CheckAsync();

        UpdateStatus.Text = result.Message;
        if (result.UpdateAvailable)
        {
            App.ViewModel.IsUpdateAvailable = true;
            App.ViewModel.LatestUpdateTag = result.LatestTag;
            App.ViewModel.UpdateDownloadUrl = result.DownloadUrl;
            App.ViewModel.UpdateStatusText = result.Message;

            if (!string.IsNullOrEmpty(result.ReleaseUrl))
            {
                DownloadLink.NavigateUri = new Uri(result.ReleaseUrl);
                DownloadLink.Visibility = Visibility.Visible;
            }
        }

        CheckRing.IsActive = false;
        CheckButton.IsEnabled = true;
    }

    // ---- Plugins tab ----
    // One card per registered plugin (built-ins + any plugin.json dropped into
    // %LOCALAPPDATA%\MarkSmith\Plugins\<id>\), generated in code rather than a DataTemplate so this
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
        // Green success tick shown once download hits 100% — animated in by ShowTick, replacing the
        // "Downloading… 100%" spinner/text so completion reads as a clear, finished state.
        var tick = new FontIcon
        {
            Glyph = "", // CheckMark
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43)),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };
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
            tick.Visibility = Visibility.Collapsed;
            tick.Opacity = 0;
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

            var ok = false;
            try
            {
                await plugin.InstallAsync(progress, CancellationToken.None);
                status.Text = "Downloading… 100%";
                ok = true;
            }
            catch (Exception ex)
            {
                status.Text = $"Install failed: {ex.Message}";
            }

            ring.IsActive = false;
            installButton.IsEnabled = true;
            Refresh();

            if (ok)
            {
                status.Text = "Done — installed.";
                ShowTick(tick);
                // A new engine can change what the current document renders — refresh the preview.
                PluginsChanged?.Invoke();
            }
        };

        removeButton.Click += (_, _) =>
        {
            try
            {
                plugin.Uninstall();
                tick.Visibility = Visibility.Collapsed;
                status.Text = "Removed.";
            }
            catch (Exception ex)
            {
                status.Text = $"Remove failed: {ex.Message}";
            }
            Refresh();
            PluginsChanged?.Invoke();
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
        buttons.Children.Add(tick);
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

    // Pop the success tick in: fade + a slight overshoot scale so completion feels like a positive
    // "done", not a control quietly toggling visibility. RenderTransform scale + Opacity are both
    // composition-independent animations, so this stays smooth without EnableDependentAnimation.
    private static void ShowTick(FontIcon icon)
    {
        var scale = new ScaleTransform { ScaleX = 0.4, ScaleY = 0.4 };
        icon.RenderTransform = scale;
        icon.Visibility = Visibility.Visible;

        var ease = new BackEase { Amplitude = 0.7, EasingMode = EasingMode.EaseOut };
        var sb = new Storyboard();

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(200)) };
        Storyboard.SetTarget(fade, icon);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var grow = new DoubleAnimation
            {
                From = 0.4, To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(340)),
                EasingFunction = ease,
            };
            Storyboard.SetTarget(grow, scale);
            Storyboard.SetTargetProperty(grow, axis);
            sb.Children.Add(grow);
        }

        sb.Begin();
    }

    // House-style template import: pick a .dotx; the ViewModel parses it locally, shows the prompt
    // as a copyable fallback, and enqueues it for the extension. The AI reply comes back through
    // the command channel and is applied by the heartbeat poll in MainWindow.
    private void OnApplyHouseStyleJsonClick(object sender, RoutedEventArgs e)
        => App.ViewModel.ApplyHouseStyleThemeJson(App.ViewModel.HouseStyleJsonResult);

    private async void OnImportDotxClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".dotx");
        picker.FileTypeFilter.Add(".docx");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            App.ViewModel.BeginHouseStyleImport(file.Path);
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title = "Template Error",
                Content = $"Could not parse the template:\n{ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
        }
    }
}

