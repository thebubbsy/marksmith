using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using MarkSmith.Plugins;
using MarkSmith.Services;

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
        VersionText.Text = $"Version {App.Updates.CurrentDisplayVersion}";
        RefreshLicenseUi();
        BuildPluginCards();
        // Listen until the dialog closes (Detach). Not until Unloaded: the dialog's popup raises
        // Unloaded while Settings is still on screen, which silently cut off every live update
        // after it (the PDF page preview, Google's status, a download in About, license changes).
        App.License.Changed += OnLicenseChanged;
        App.ViewModel.PropertyChanged += OnUpdateStateChanged;
        RenderAboutUpdate();
        RenderPdfBands();
        RenderGoogleState();
        GoogleSecretBox.Password = App.ViewModel.GoogleClientSecret; // masked; pre-fill for convenience
        Nav.SelectedItem = Nav.MenuItems[0];
        HoverPolish.Track(this);
    }

    /// <summary>
    /// Sizes the view to the window it opens in. The dialog adds ~200px of chrome (title, button
    /// row, padding) around this content, so on a short window a fixed height pushed Close off
    /// the bottom. Pages scroll, so shrinking is always safe.
    /// </summary>
    public void FitTo(Windows.Foundation.Size window)
    {
        Root.Width = Math.Clamp(window.Width - 140, 640, 820);
        Root.Height = Math.Clamp(window.Height - 220, 360, 600);
    }

    /// <summary>
    /// Stops listening to the license and the view model. The host calls this once the dialog has
    /// closed: both outlive every Settings instance, and each open used to leave one more dead
    /// view refreshing itself.
    /// </summary>
    public void Detach()
    {
        App.License.Changed -= OnLicenseChanged;
        App.ViewModel.PropertyChanged -= OnUpdateStateChanged;
    }

    /// <summary>Opens Settings on a particular page (General, Pdf, Automation, Google, License, Plugins, About).</summary>
    public void ShowPage(string tag)
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            if ((string)item.Tag == tag) { Nav.SelectedItem = item; return; }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
        ScrollViewer target = tag switch
        {
            "Pdf" => PdfPage,
            "Automation" => AutomationPage,
            "Google" => GooglePage,
            "License" => LicensePage,
            "Plugins" => PluginsPage,
            "About" => AboutPage,
            _ => GeneralPage,
        };
        foreach (var page in Pages.Children.OfType<ScrollViewer>())
            page.Visibility = ReferenceEquals(page, target) ? Visibility.Visible : Visibility.Collapsed;
        PlayPageEntrance(target);
    }

    // The incoming page fades in while rising 12px (the short entrance Windows Settings uses), so
    // switching pages reads as navigation rather than a hard content swap.
    private static void PlayPageEntrance(UIElement page)
    {
        var shift = new TranslateTransform { Y = 12 };
        page.RenderTransform = shift;
        page.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var sb = new Storyboard();
        var fade = new DoubleAnimation { To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(180)) };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var rise = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(260)), EasingFunction = ease };
        Storyboard.SetTarget(rise, shift);
        Storyboard.SetTargetProperty(rise, "Y");
        sb.Children.Add(fade);
        sb.Children.Add(rise);
        sb.Begin();
    }

    private void ShowLicenseStatus(bool ok, string message)
    {
        LicenseStatusBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        LicenseStatusBar.Message = message;
        LicenseStatusBar.IsOpen = !string.IsNullOrEmpty(message);
    }

    // Activate stays disabled until there's something to activate, and Enter in the key box
    // activates: pasting a key and pressing Enter is how most people will do this.
    private void OnKeyBoxTextChanged(object sender, TextChangedEventArgs e) =>
        ActivateButton.IsEnabled = !string.IsNullOrWhiteSpace(KeyBox.Text);

    private void OnKeyBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !ActivateButton.IsEnabled) return;
        e.Handled = true;
        OnActivateLicense(sender, e);
    }

    private void OnGoogleSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MarkSmith.ViewModels.MainViewModel vm)
            vm.GoogleClientSecret = GoogleSecretBox.Password;
    }

    private void RefreshLicenseUi()
    {
        var ed = App.License.State.Edition;
        // "Remove license" only makes sense for an actual activated Pro key.
        DeactivateButton.Visibility = ed == Models.Edition.Pro ? Visibility.Visible : Visibility.Collapsed;
        // "Start trial" is offered to Free users; StartTrial() itself refuses (with the reason) if
        // the 3-export trial is already active or spent.
        StartTrialButton.Visibility = App.License.CanStartTrial ? Visibility.Visible : Visibility.Collapsed;
        // Once Pro is active there is nothing to paste or buy — just the way to hand the seat back.
        var isPro = ed == Models.Edition.Pro;
        KeyEntryRow.Visibility = isPro ? Visibility.Collapsed : Visibility.Visible;
        BuyButton.Visibility = isPro ? Visibility.Collapsed : Visibility.Visible;
        EditionCard.Glyph = isPro ? "\uE735" : "\uE734"; // filled star once unlocked
        // The resolved state (Free / Trial — N exports remaining / Pro) is the card's heading
        // (EditionStatus); LicenseStatusBar is only for the outcome of an action the user just took.
    }

    private void OnStartTrialClick(object sender, RoutedEventArgs e)
    {
        var (ok, message) = App.License.StartTrial();
        ShowLicenseStatus(ok, message);
        RefreshLicenseUi();
    }

    private async void OnActivateLicense(object sender, RoutedEventArgs e)
    {
        ActivateButton.IsEnabled = false;
        KeyBox.IsEnabled = false;
        var (ok, message) = await App.License.ActivateAsync(KeyBox.Text.Trim());
        ShowLicenseStatus(ok, message);
        KeyBox.IsEnabled = true;
        if (ok) KeyBox.Text = "";
        RefreshLicenseUi();
        ActivateButton.IsEnabled = !string.IsNullOrWhiteSpace(KeyBox.Text);
    }

    private async void OnBuyPro(object sender, RoutedEventArgs e)
    {
        if (!Services.LicenseService.IsStoreConfigured)
        {
            ShowLicenseStatus(false, "The online store link isn't configured yet.");
            return;
        }
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(Services.LicenseService.CheckoutUrl(App.License.State.Email))); }
        catch { /* no browser / bad uri — ignore */ }
    }

    private async void OnDeactivateLicense(object sender, RoutedEventArgs e)
    {
        // DeactivateAsync (not Deactivate) so the Lemon Squeezy activation seat is handed back.
        // Forgetting the key locally while the seat stays claimed is how a customer with a
        // 3-machine key runs out of machines they never used.
        DeactivateButton.IsEnabled = false;
        var (ok, message) = await App.License.DeactivateAsync();
        ShowLicenseStatus(ok, message);
        RefreshLicenseUi();
        DeactivateButton.IsEnabled = true;
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
        UpdateStatusBar.IsOpen = false;

        var result = await App.ViewModel.CheckForUpdatesNowAsync();

        CheckRing.IsActive = false;
        CheckButton.IsEnabled = true;
        // Something to install (or already on its way): the update state says it all.
        if (App.ViewModel.UpdatePhase != ViewModels.UpdatePhase.None && result.UpdateAvailable)
        {
            RenderAboutUpdate();
            return;
        }

        _showingUpdate = false;
        UpdateStatusBar.Severity = result.Ok ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
        UpdateStatusBar.Title = result.Ok ? "You're up to date" : "Couldn't check for updates";
        UpdateStatusBar.Message = result.Message;
        UpdateStatusBar.IsClosable = true;
        AboutUpdateAction.Visibility = Visibility.Collapsed;
        AboutUpdateProgress.Visibility = Visibility.Collapsed;
        DownloadLink.Visibility = result.Ok ? Visibility.Collapsed : Visibility.Visible;
        DownloadLink.Content = "Open the releases page";
        DownloadLink.NavigateUri = new Uri(UpdateService.ReleasesUrl);
        UpdateStatusBar.IsOpen = true;
    }

    private void OnUpdateStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(App.ViewModel.UpdatePhase):
                DispatcherQueue.TryEnqueue(RenderAboutUpdate);
                break;
            case nameof(App.ViewModel.PdfBandsPreview):
                DispatcherQueue.TryEnqueue(RenderPdfBands);
                break;
            case nameof(App.ViewModel.GoogleSignInPhase):
            case nameof(App.ViewModel.GoogleDeviceCode):
                DispatcherQueue.TryEnqueue(RenderGoogleState);
                break;
        }
    }

    // ---- PDF page preview ----

    private void RenderPdfBands()
    {
        var bands = App.ViewModel.PdfBandsPreview;
        var align = bands.Alignment switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        PdfPreviewHeader.Text = bands.Header;
        PdfPreviewFooter.Text = bands.Footer;
        PdfPreviewHeader.TextAlignment = align;
        PdfPreviewFooter.TextAlignment = align;
        PdfPreviewCaption.Text = bands.IsEmpty ? "No header or footer" : "Page 2 of 10";
        var spoken = new System.Collections.Generic.List<string>();
        if (bands.Header.Length > 0) spoken.Add($"header \u201C{bands.Header}\u201D");
        if (bands.Footer.Length > 0) spoken.Add($"footer \u201C{bands.Footer}\u201D");
        AutomationProperties.SetName(PdfPagePreview, spoken.Count == 0
            ? "Page preview: no header or footer"
            : "Page preview: " + string.Join(", ", spoken));
    }

    // ---- Google ----

    private void RenderGoogleState()
    {
        var vm = App.ViewModel;
        GoogleStatusBar.Severity = vm.GoogleSignInPhase switch
        {
            ViewModels.GoogleSignInPhase.Connected => InfoBarSeverity.Success,
            ViewModels.GoogleSignInPhase.Failed => InfoBarSeverity.Error,
            ViewModels.GoogleSignInPhase.NotConfigured => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        var hasCode = !string.IsNullOrEmpty(vm.GoogleDeviceCode);
        GoogleCodeText.Text = vm.GoogleDeviceCode;
        GoogleCodePanel.Visibility = hasCode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyGoogleCodeClick(object sender, RoutedEventArgs e) =>
        CopyWithTick(App.ViewModel.GoogleDeviceCode, CopyGoogleCodeIcon);

    private async void OnOpenGoogleVerifyClick(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(App.ViewModel.GoogleVerifyUrl, UriKind.Absolute, out var uri)) return;
        try { await Windows.System.Launcher.LaunchUriAsync(uri); }
        catch { /* no browser: the code and the address are both on screen */ }
    }

    private static void CopyWithTick(string text, FontIcon icon) => CopyFeedback.CopyWithTick(text, icon);

    // ---- Automation ----

    // Clearing the port box gives NaN, which the int binding silently drops while the box stays
    // empty. Put the port that's actually in use back instead.
    private void OnApiPortValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) sender.Value = App.ViewModel.ApiPort;
    }

    // True while the status bar is showing the update rather than a check's answer.
    private bool _showingUpdate;

    private void RenderAboutUpdate()
    {
        if (App.ViewModel.UpdatePhase == ViewModels.UpdatePhase.None)
        {
            // Dismissed from the banner: don't leave a stale "downloading" here.
            if (_showingUpdate) UpdateStatusBar.IsOpen = false;
            _showingUpdate = false;
            return;
        }
        _showingUpdate = true;
        UpdateBannerPresenter.Apply(UpdateStatusBar, AboutUpdateAction, AboutUpdateProgress, DownloadLink, App.ViewModel);
    }

    private async void OnAboutUpdateAction(object sender, RoutedEventArgs e) => await App.ViewModel.UpdateBannerActionAsync();

    // ---- Plugins tab ----
    // One card per registered plugin (built-ins + any plugin.json dropped into
    // %LOCALAPPDATA%\MarkSmith\Plugins\<id>\), generated in code rather than a DataTemplate so this
    // and the Avalonia SettingsView share one obvious pattern for the install/remove/progress wiring.

    private void BuildPluginCards()
    {
        PluginsPanel.Children.Clear();
        var index = 0;
        foreach (var plugin in App.Plugins.All)
        {
            PluginsPanel.Children.Add(BuildPluginCard(plugin));
            index++;
        }
        PluginsEmptyState.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (App.Plugins.LoadWarnings.Count > 0)
        {
            PluginWarningsBar.Message = string.Join("\n", App.Plugins.LoadWarnings);
            PluginWarningsBar.IsOpen = true;
        }
    }

    private UIElement BuildPluginCard(IMarksmithPlugin plugin)
    {
        // A SettingsCard like every other row in Settings: name + description beside the
        // Install / Remove buttons; secondary lines use the same caption style as the cards'.
        var caption = (Style)Resources["SettingsHintStyle"];
        var fences = plugin is IDiagramPlugin diagram
            ? new TextBlock
            {
                Text = "Code blocks: " + string.Join(", ", diagram.FenceLanguages.Select(l => "```" + l)),
                Style = caption,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            }
            : null;

        // Collapsed while empty so a not-yet-installed card doesn't carry a blank line of spacing.
        var status = new TextBlock { Style = caption, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
        void SetStatus(string message, bool failed = false)
        {
            status.Text = message;
            status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            // A failure in the error colour; everything else in the caption grey.
            if (failed) ThemeBrush.Set(status, TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            else { ThemeBrush.Clear(status, TextBlock.ForegroundProperty); status.ClearValue(TextBlock.ForegroundProperty); }
        }
        var ring = new ProgressRing { IsActive = false, Width = 18, Height = 18, Visibility = Visibility.Collapsed };
        // Green success tick shown once download hits 100% — animated in by ShowTick, replacing the
        // "Downloading… 100%" spinner/text so completion reads as a clear, finished state.
        var tick = new FontIcon
        {
            Glyph = "\uE73E", // CheckMark
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        }.Themed(FontIcon.ForegroundProperty, "SystemFillColorSuccessBrush");
        // Standard buttons, not accent: with seven plugins listed, a column of accent Installs
        // turned the page blue and none of them read as "the" action.
        var installButton = new Button
        {
            Content = "Install",
            MinWidth = 84,
        };
        AutomationProperties.SetName(installButton, $"Install {plugin.Name}");
        var removeButton = new Button { Content = "Remove", MinWidth = 84 };
        AutomationProperties.SetName(removeButton, $"Remove {plugin.Name}");

        void Refresh()
        {
            var installed = plugin.State == PluginInstallState.Installed;
            installButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            removeButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
            if (installed && string.IsNullOrEmpty(status.Text)) SetStatus("Installed");
        }

        installButton.Click += async (_, _) =>
        {
            installButton.IsEnabled = false;
            tick.Visibility = Visibility.Collapsed;
            tick.Opacity = 0;
            ring.Visibility = Visibility.Visible;
            ring.IsActive = true;
            SetStatus("Downloading…");

            // Install downloads tens of MB in a tight loop that reports progress far faster than
            // the UI needs — throttle to whole-percent updates so this doesn't flood the dispatcher.
            var lastPercent = -1;
            var progress = new Progress<double>(p =>
            {
                var percent = (int)(p * 100);
                if (percent == lastPercent) return;
                lastPercent = percent;
                DispatcherQueue.TryEnqueue(() => SetStatus($"Downloading… {percent}%"));
            });

            var ok = false;
            try
            {
                await plugin.InstallAsync(progress, CancellationToken.None);
                ok = true;
            }
            catch (Exception ex)
            {
                SetStatus($"Install failed: {ex.Message}", failed: true);
            }

            ring.IsActive = false;
            ring.Visibility = Visibility.Collapsed;
            installButton.IsEnabled = true;
            Refresh();

            if (ok)
            {
                SetStatus("Installed");
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
                SetStatus("Removed");
            }
            catch (Exception ex)
            {
                SetStatus($"Remove failed: {ex.Message}", failed: true);
            }
            Refresh();
            PluginsChanged?.Invoke();
        };

        Refresh();

        // Fence languages and the install status sit under the description, aligned with it.
        var details = new StackPanel { Spacing = 4 };
        if (fences != null) details.Children.Add(fences);
        details.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(ring);
        buttons.Children.Add(tick);
        buttons.Children.Add(installButton);
        buttons.Children.Add(removeButton);

        return new Controls.SettingsCard
        {
            Glyph = "\uEA86", // puzzle piece, as on the Plugins nav item
            Header = plugin.Name,
            Description = plugin.Description,
            Action = buttons,
            Details = details,
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

    private void OnHouseStyleReplyChanged(object sender, TextChangedEventArgs e) =>
        ApplyThemeButton.IsEnabled = !string.IsNullOrWhiteSpace(HouseStyleReplyBox.Text);

    private void OnCopyHouseStylePromptClick(object sender, RoutedEventArgs e) =>
        CopyWithTick(App.ViewModel.HouseStylePrompt, CopyPromptIcon);

    private async void OnImportDotxClick(object sender, RoutedEventArgs e)
    {
        var path = await MarkSmith.Services.NativeFilePicker.PickOpenFileAsync(
            this, "Import a Word house style", MarkSmith.Services.NativeFilePicker.Purpose.Templates,
            new[]
            {
                MarkSmith.Models.FileType.Of("Word templates and documents", ".dotx", ".docx"),
                MarkSmith.Models.FileType.Of("Word template", ".dotx"),
                MarkSmith.Models.FileType.Of("Word document", ".docx"),
            },
            okLabel: "Import");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            App.ViewModel.BeginHouseStyleImport(path);
        }
        catch (Exception ex)
        {
            // Reported inline rather than in a ContentDialog: Settings is itself a ContentDialog,
            // and WinUI can't open a second one on top of it.
            App.ViewModel.HouseStyleStatus = $"Couldn't read that template: {ex.Message}";
        }
    }
}

