using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
        // The house-style round trip reports progress here while the user waits.
        LiveAnnounce.Track(HouseStyleStatusText);
        // Lines that answer a choice the user just made: the file name a pattern produces, what the
        // chosen OCR engine does, whether the local API started.
        LiveAnnounce.Track(FileNamePreviewText);
        LiveAnnounce.Track(OcrEngineDescriptionText);
        LiveAnnounce.Track(ApiStatusNormalText);
        LiveAnnounce.Track(ApiStatusErrorText);
        // "Right-click for options" menus: open them from Shift+F10 / the Menu key too.
        KeyboardContextMenu.BridgeAll(
            (RescanCloudButton, OnRescanCloudButtonRightTapped),
            (CopyPromptButton, OnCopyPromptButtonRightTapped),
            (ApplyThemeButton, OnApplyThemeButtonRightTapped),
            (DeactivateButton, OnDeactivateButtonRightTapped),
            (BuyButton, OnBuyButtonRightTapped),
            (StartTrialButton, OnStartTrialButtonRightTapped),
            (ActivateButton, OnActivateButtonRightTapped),
            (CheckButton, OnCheckButtonRightTapped),
            (VersionButton, OnAboutHeaderRightTapped),
            (PdfPagePreviewButton, OnPdfPagePreviewRightTapped));
        // The page list's own menu (open, next / previous page, copy name) was right-click only.
        foreach (var navItem in Nav.MenuItems.OfType<NavigationViewItem>())
            KeyboardContextMenu.Bridge(navItem, OnNavItemRightTapped);
        DataContext = App.ViewModel;
        VersionText.Text = $"Version {App.Updates.CurrentDisplayVersion}";
        AutomationProperties.SetName(VersionButton, $"Copy version details, {VersionText.Text}");
        RefreshLicenseUi();
        BuildPluginCards();
        // Listen until the dialog closes (Detach). Not until Unloaded: the dialog's popup raises
        // Unloaded while Settings is still on screen, which silently cut off every live update
        // after it (the PDF page preview, Google's status, a download in About, license changes).
        App.License.Changed += OnLicenseChanged;
        App.ViewModel.PropertyChanged += OnUpdateStateChanged;
        RenderGeneralDynamicNames();
        RenderAboutUpdate();
        RenderPdfBands();
        RenderApiStatus();
        RenderHouseStyleStatus();
        RenderGoogleState();
        GoogleSecretBox.Password = App.ViewModel.GoogleClientSecret; // masked; pre-fill for convenience
        Nav.SelectedItem = Nav.MenuItems[0];
        ApplyResponsiveLayout(Root.Width);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnSettingsKeyDown), true);
        HoverPolish.Track(this);
    }

    private void OnSettingsKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (UpdateStatusBar.IsOpen)
            {
                UpdateStatusBar.IsOpen = false;
                e.Handled = true;
            }
            else if (LicenseStatusBar.IsOpen)
            {
                LicenseStatusBar.IsOpen = false;
                e.Handled = true;
            }
            else if (PluginWarningsBar.IsOpen)
            {
                PluginWarningsBar.IsOpen = false;
                e.Handled = true;
            }
            return;
        }

        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == Windows.System.VirtualKey.Tab)
        {
            StepNavPage(shift ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (ctrl && !shift && e.Key is Windows.System.VirtualKey.PageDown or Windows.System.VirtualKey.PageUp)
        {
            StepNavPage(e.Key == Windows.System.VirtualKey.PageDown ? 1 : -1);
            e.Handled = true;
            return;
        }

        if ((ctrl || alt) && !shift && e.Key is >= Windows.System.VirtualKey.Number1 and <= Windows.System.VirtualKey.Number7)
        {
            var idx = (int)e.Key - (int)Windows.System.VirtualKey.Number1;
            var items = Nav.MenuItems.OfType<NavigationViewItem>().ToList();
            if (idx >= 0 && idx < items.Count)
            {
                Nav.SelectedItem = items[idx];
                e.Handled = true;
            }
        }
    }

    private void StepNavPage(int delta)
    {
        var items = Nav.MenuItems.OfType<NavigationViewItem>().ToList();
        if (items.Count == 0) return;
        var current = Nav.SelectedItem is NavigationViewItem selected ? items.IndexOf(selected) : 0;
        if (current < 0) current = 0;
        var next = (current + delta + items.Count) % items.Count;
        Nav.SelectedItem = items[next];
    }

    /// <summary>
    /// Sizes the view to the window it opens in. The dialog adds ~200px of chrome (title, button
    /// row, padding) around this content, so on a short window a fixed height pushed Close off
    /// the bottom. Pages scroll, so shrinking is always safe.
    /// </summary>
    public void FitTo(Windows.Foundation.Size window)
    {
        Root.Width = Math.Clamp(window.Width - 100, 480, 820);
        Root.Height = Math.Clamp(window.Height - 180, 360, 640);
        ApplyResponsiveLayout(Root.Width);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;

        // Collapse the left navigation rail to icons on narrow windows so cards keep comfortable room.
        if (Nav is not null)
        {
            Nav.PaneDisplayMode = width < 620
                ? NavigationViewPaneDisplayMode.LeftCompact
                : NavigationViewPaneDisplayMode.Left;
            Nav.OpenPaneLength = width < 720 ? 164 : 188;
        }

        // Stack the PDF page preview below the header/footer inputs when narrow.
        if (PdfPagePreviewPanel is not null)
        {
            if (width < 640)
            {
                Grid.SetRow(PdfPagePreviewPanel, 1);
                Grid.SetColumn(PdfPagePreviewPanel, 0);
                Grid.SetColumnSpan(PdfPagePreviewPanel, 2);
                PdfPagePreviewPanel.Margin = new Thickness(0, 12, 0, 0);
                PdfPagePreviewPanel.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                Grid.SetRow(PdfPagePreviewPanel, 0);
                Grid.SetColumn(PdfPagePreviewPanel, 1);
                Grid.SetColumnSpan(PdfPagePreviewPanel, 1);
                PdfPagePreviewPanel.Margin = new Thickness(0);
                PdfPagePreviewPanel.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }

        // Wrap "Check for updates" beneath the MarkSmith version summary on narrow widths.
        if (AboutCheckPanel is not null)
        {
            if (width < 600)
            {
                Grid.SetRow(AboutCheckPanel, 1);
                Grid.SetColumn(AboutCheckPanel, 1);
                Grid.SetColumnSpan(AboutCheckPanel, 2);
                AboutCheckPanel.Margin = new Thickness(0, 10, 0, 0);
                AboutCheckPanel.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                Grid.SetRow(AboutCheckPanel, 0);
                Grid.SetColumn(AboutCheckPanel, 2);
                Grid.SetColumnSpan(AboutCheckPanel, 1);
                AboutCheckPanel.Margin = new Thickness(0);
                AboutCheckPanel.HorizontalAlignment = HorizontalAlignment.Right;
            }
        }
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
        target.ChangeView(null, 0, null, disableAnimation: true);
        HoverPolish.Apply(target);
        PlayPageEntrance(target);
    }

    // The incoming page fades in while rising 12px (the short entrance Windows Settings uses), so
    // switching pages reads as navigation rather than a hard content swap.
    private static void PlayPageEntrance(UIElement page)
    {
        if (!HoverPolish.AnimationsEnabled)
        {
            page.Opacity = 1;
            page.RenderTransform = null;
            return;
        }

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
        if (!string.IsNullOrEmpty(message))
        {
            AutomationProperties.SetName(LicenseStatusBar, message);
            AutomationProperties.SetLiveSetting(LicenseStatusBar, AutomationLiveSetting.Polite);
            LicenseStatusBar.IsOpen = true;
            // "License activated" / "That key isn't valid": the answer to the button just pressed.
            LiveAnnounce.Raise(LicenseStatusBar);
        }
        else
        {
            LicenseStatusBar.IsOpen = false;
        }
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

    private void OnPdfUserPasswordKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        PdfOwnerPasswordBox?.Focus(FocusState.Programmatic);
    }

    private void OnGoogleClientIdKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        GoogleSecretBox?.Focus(FocusState.Programmatic);
    }

    private void OnGoogleSecretKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        if (GoogleSignInButton is { Visibility: Visibility.Visible, IsEnabled: true })
        {
            e.Handled = true;
            GoogleSignInButton.Focus(FocusState.Programmatic);
        }
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
        // Read-only, not disabled: a disabled key box (where Enter starts this) threw focus to the
        // window's first tab stop for the whole check.
        var keyBoxFocus = KeyBox.FocusState;
        // Keyboard users keep their focus rectangle on whatever the result focuses.
        var how = keyBoxFocus == FocusState.Keyboard || ActivateButton.FocusState == FocusState.Keyboard
            ? FocusState.Keyboard : FocusState.Programmatic;
        var parked = FocusRescue.Park(ActivateButton, KeyBox);
        ActivateButton.IsEnabled = false;
        KeyBox.IsReadOnly = true;
        LicenseActionRing.Visibility = Visibility.Visible;
        LicenseActionRing.IsActive = true;
        var ok = false;
        try
        {
            (ok, var message) = await App.License.ActivateAsync(KeyBox.Text.Trim());
            ShowLicenseStatus(ok, message);
            if (ok) KeyBox.Text = "";
            RefreshLicenseUi();
        }
        finally
        {
            LicenseActionRing.IsActive = false;
            LicenseActionRing.Visibility = Visibility.Collapsed;
            KeyBox.IsReadOnly = false;
            ActivateButton.IsEnabled = !string.IsNullOrWhiteSpace(KeyBox.Text);
            // Only while focus still waits on the key box (or was lost when the key row closed): a
            // check that finished after the user moved on used to pull focus back from wherever they were.
            var stillWaiting = parked.Waiting
                || (keyBoxFocus != FocusState.Unfocused && FocusRescue.StillOn(KeyBox));
            if (stillWaiting && ok && DeactivateButton.Visibility == Visibility.Visible)
                DeactivateButton.Focus(how);
            else if (stillWaiting && KeyEntryRow.Visibility == Visibility.Visible)
                KeyBox.Focus(how);
        }
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
        var parked = FocusRescue.Park(DeactivateButton, BuyButton, StartTrialButton);
        DeactivateButton.IsEnabled = false;
        DeactivateRing.Visibility = Visibility.Visible;
        DeactivateRing.IsActive = true;
        try
        {
            var (ok, message) = await App.License.DeactivateAsync();
            ShowLicenseStatus(ok, message);
            RefreshLicenseUi();
        }
        finally
        {
            DeactivateRing.IsActive = false;
            DeactivateRing.Visibility = Visibility.Collapsed;
            DeactivateButton.IsEnabled = true;
            // Only while focus still waits where Park left it: after a mouse click (or once the user
            // has moved on) the key box used to pull focus from wherever they were.
            if (parked.Waiting)
            {
                if (KeyEntryRow.Visibility == Visibility.Visible)
                    KeyBox.Focus(parked.How);
                else
                    parked.Return();
            }
        }
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
    private void OnRescanCloud(object sender, RoutedEventArgs e)
    {
        AnimateRescanCloud();
        App.ViewModel.RefreshCloudProviders();
        if (CloudRescanStatusText is not null)
        {
            var count = App.ViewModel.CloudProviders.Count;
            var localCount = Math.Max(0, count - 1); // WebDAV is always present alongside local sync folders
            var summary = localCount switch
            {
                0 => "No local cloud sync folders found on this PC (WebDAV is available).",
                1 => "Found 1 local cloud sync folder on this PC.",
                _ => $"Found {localCount} local cloud sync folders on this PC.",
            };
            CloudRescanStatusText.Text = summary;
            CloudRescanStatusText.Visibility = Visibility.Visible;
            AutomationProperties.SetName(CloudRescanStatusText, summary);
            AutomationProperties.SetLiveSetting(CloudRescanStatusText, AutomationLiveSetting.Polite);
            // Rescan says the same sentence twice in a row when nothing changed, so raise every time.
            LiveAnnounce.Raise(CloudRescanStatusText);
        }
    }

    private void AnimateRescanCloud()
    {
        if (RescanCloudRotate is null || !HoverPolish.AnimationsEnabled) return;
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var sb = new Storyboard();
        Storyboard.SetTarget(anim, RescanCloudRotate);
        Storyboard.SetTargetProperty(anim, "Angle");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        var parked = FocusRescue.Park(CheckButton, VersionButton);
        CheckButton.IsEnabled = false;
        CheckRing.Visibility = Visibility.Visible;
        CheckRing.IsActive = true;
        UpdateStatusBar.IsOpen = false;

        var result = await App.ViewModel.CheckForUpdatesNowAsync();

        CheckRing.IsActive = false;
        CheckRing.Visibility = Visibility.Collapsed;
        CheckButton.IsEnabled = true;
        // Back to Check for updates only if it had focus: a mouse click no longer pulls focus
        // here from wherever the user went while the check ran.
        parked.Return();
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
        AutomationProperties.SetName(UpdateStatusBar, $"{UpdateStatusBar.Title}: {UpdateStatusBar.Message}");
        AutomationProperties.SetLiveSetting(UpdateStatusBar, AutomationLiveSetting.Polite);
        UpdateStatusBar.IsClosable = true;
        AboutUpdateAction.Visibility = Visibility.Collapsed;
        AboutUpdateProgress.Visibility = Visibility.Collapsed;
        DownloadLink.Visibility = result.Ok ? Visibility.Collapsed : Visibility.Visible;
        DownloadLink.Content = "Open the releases page";
        DownloadLink.NavigateUri = new Uri(UpdateService.ReleasesUrl);
        UpdateStatusBar.IsOpen = true;
        // Marked live above, but the answer was only read out if the user went looking for it.
        LiveAnnounce.Raise(UpdateStatusBar);
    }

    private void OnVersionButtonClick(object sender, RoutedEventArgs e) => CopyVersionDetails();

    // One copy for the version button and both menus' "Copy version details": the same text, and
    // the same answer in the update bar (including when the clipboard is busy).
    private async void CopyVersionDetails()
    {
        var text = $"MarkSmith \u2014 Version {App.Updates.CurrentDisplayVersion}";
        var error = await ClipboardWriter.TrySetTextAsync(text);

        UpdateStatusBar.Severity = error is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        UpdateStatusBar.Title = error is null ? "Copied to clipboard" : "Couldn't copy";
        UpdateStatusBar.Message = error ?? text;
        UpdateStatusBar.IsOpen = true;
        LiveAnnounce.Raise(UpdateStatusBar);
    }

    private void OnAboutHeaderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var copyVersionItem = new MenuFlyoutItem
        {
            Text = "Copy version details",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyVersionItem.Click += (_, _) => CopyVersionDetails();
        menu.Items.Add(copyVersionItem);

        var checkUpdatesItem = new MenuFlyoutItem
        {
            Text = "Check for updates now",
            Icon = new FontIcon { Glyph = "\uE895" }
        };
        checkUpdatesItem.Click += (s, ev) => OnCheckForUpdates(s, ev);
        menu.Items.Add(checkUpdatesItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var releasesItem = new MenuFlyoutItem
        {
            Text = "Releases & changelog",
            Icon = new FontIcon { Glyph = "\uE81C" }
        };
        releasesItem.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://github.com/thebubbsy/MarkSmith/releases", UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        menu.Items.Add(releasesItem);

        var repoItem = new MenuFlyoutItem
        {
            Text = "GitHub repository",
            Icon = new FontIcon { Glyph = "\uE943" }
        };
        repoItem.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://github.com/thebubbsy/MarkSmith", UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        menu.Items.Add(repoItem);

        var reportItem = new MenuFlyoutItem
        {
            Text = "Report an issue",
            Icon = new FontIcon { Glyph = "\uEBE8" }
        };
        reportItem.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://github.com/thebubbsy/MarkSmith/issues", UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        menu.Items.Add(reportItem);

        var licenseItem = new MenuFlyoutItem
        {
            Text = "License agreement (EULA)",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        licenseItem.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://github.com/thebubbsy/MarkSmith/blob/main/LICENSE", UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        menu.Items.Add(licenseItem);

        menu.ShowAt(AboutHeaderGrid, KeyboardContextMenu.PointIn(e, AboutHeaderGrid));
    }

    private void OnCheckButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? CheckButton;
        var menu = new MenuFlyout();

        var checkUpdatesItem = new MenuFlyoutItem
        {
            Text = "Check for updates",
            Icon = new FontIcon { Glyph = "\uE895" }
        };
        checkUpdatesItem.Click += (s, ev) => OnCheckForUpdates(s, ev);
        menu.Items.Add(checkUpdatesItem);

        var copyVersionItem = new MenuFlyoutItem
        {
            Text = "Copy version details",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyVersionItem.Click += (_, _) => CopyVersionDetails();
        menu.Items.Add(copyVersionItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var releasesItem = new MenuFlyoutItem
        {
            Text = "Releases & changelog",
            Icon = new FontIcon { Glyph = "\uE81C" }
        };
        releasesItem.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://github.com/thebubbsy/MarkSmith/releases", UseShellExecute = true }); }
            catch { }
        };
        menu.Items.Add(releasesItem);

        var reportItem = new MenuFlyoutItem
        {
            Text = "Report an issue",
            Icon = new FontIcon { Glyph = "\uEBE8" }
        };
        reportItem.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://github.com/thebubbsy/MarkSmith/issues", UseShellExecute = true }); }
            catch { }
        };
        menu.Items.Add(reportItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnBuyButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? BuyButton;
        var menu = new MenuFlyout();

        var buyItem = new MenuFlyoutItem
        {
            Text = "Buy MarkSmith Pro",
            Icon = new FontIcon { Glyph = "\uE719" }
        };
        buyItem.Click += (s, ev) => OnBuyPro(s, ev);
        menu.Items.Add(buyItem);

        var trialItem = new MenuFlyoutItem
        {
            Text = "Start free trial",
            Icon = new FontIcon { Glyph = "\uE734" }
        };
        trialItem.Click += (s, ev) => OnStartTrialClick(s, ev);
        menu.Items.Add(trialItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var copyPricingItem = new MenuFlyoutItem
        {
            Text = "Copy purchase link",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPricingItem.Click += (_, _) => CopyFeedback.CopyWithBubble(target, "https://marksmith.app/#pricing", "Copied purchase link");
        menu.Items.Add(copyPricingItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnStartTrialButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? StartTrialButton;
        var menu = new MenuFlyout();

        var trialItem = new MenuFlyoutItem
        {
            Text = "Start free trial",
            Icon = new FontIcon { Glyph = "\uE734" }
        };
        trialItem.Click += (s, ev) => OnStartTrialClick(s, ev);
        menu.Items.Add(trialItem);

        var buyItem = new MenuFlyoutItem
        {
            Text = "Buy MarkSmith Pro",
            Icon = new FontIcon { Glyph = "\uE719" }
        };
        buyItem.Click += (s, ev) => OnBuyPro(s, ev);
        menu.Items.Add(buyItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnActivateButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? ActivateButton;
        var menu = new MenuFlyout();

        var activateItem = new MenuFlyoutItem
        {
            Text = "Activate license key",
            Icon = new FontIcon { Glyph = "\uE8D7" },
            IsEnabled = ActivateButton.IsEnabled
        };
        activateItem.Click += (s, ev) => OnActivateLicense(s, ev);
        menu.Items.Add(activateItem);

        var pasteItem = new MenuFlyoutItem
        {
            Text = "Paste key from clipboard",
            Icon = new FontIcon { Glyph = "\uE77F" }
        };
        pasteItem.Click += async (_, _) =>
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    var text = await content.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        KeyBox.Text = text.Trim();
                    }
                }
            }
            catch { }
        };
        menu.Items.Add(pasteItem);

        var clearItem = new MenuFlyoutItem
        {
            Text = "Clear license key",
            Icon = new FontIcon { Glyph = "\uE894" }
        };
        clearItem.Click += (_, _) => KeyBox.Text = "";
        menu.Items.Add(clearItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnDeactivateButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? DeactivateButton;
        var menu = new MenuFlyout();

        var deactItem = new MenuFlyoutItem
        {
            Text = "Remove license from this PC",
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deactItem.Click += (s, ev) => OnDeactivateLicense(s, ev);
        menu.Items.Add(deactItem);

        var copyKeyItem = new MenuFlyoutItem
        {
            Text = "Copy current license key",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyKeyItem.Click += async (_, _) =>
        {
            var key = KeyBox.Text?.Trim();
            if (!string.IsNullOrEmpty(key))
                CopyFeedback.CopyWithBubble(target, key, "Copied license key");
        };
        menu.Items.Add(copyKeyItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnRescanCloudButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? RescanCloudButton;
        var menu = new MenuFlyout();

        var rescanItem = new MenuFlyoutItem
        {
            Text = "Rescan for cloud drives",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        rescanItem.Click += (s, ev) => OnRescanCloud(s, ev);
        menu.Items.Add(rescanItem);

        var copySubfolderItem = new MenuFlyoutItem
        {
            Text = "Copy sync subfolder name",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copySubfolderItem.Click += (_, _) => CopyFeedback.CopyWithBubble(target, App.ViewModel.CloudSubfolder, "Copied sync subfolder name");
        menu.Items.Add(copySubfolderItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnCopyPromptButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? CopyPromptButton;
        var menu = new MenuFlyout();

        var copyPromptItem = new MenuFlyoutItem
        {
            Text = "Copy AI house style prompt",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPromptItem.Click += (s, ev) => OnCopyHouseStylePromptClick(s, ev);
        menu.Items.Add(copyPromptItem);

        var pasteReplyItem = new MenuFlyoutItem
        {
            Text = "Paste AI JSON reply",
            Icon = new FontIcon { Glyph = "\uE77F" }
        };
        pasteReplyItem.Click += async (_, _) =>
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    var text = await content.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                        HouseStyleReplyBox.Text = text.Trim();
                }
            }
            catch { }
        };
        menu.Items.Add(pasteReplyItem);

        var clearReplyItem = new MenuFlyoutItem
        {
            Text = "Clear AI JSON reply",
            Icon = new FontIcon { Glyph = "\uE894" }
        };
        clearReplyItem.Click += (_, _) => HouseStyleReplyBox.Text = "";
        menu.Items.Add(clearReplyItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnApplyThemeButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? ApplyThemeButton;
        var menu = new MenuFlyout();

        var applyItem = new MenuFlyoutItem
        {
            Text = "Apply house style theme",
            Icon = new FontIcon { Glyph = "\uE73E" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            IsEnabled = ApplyThemeButton.IsEnabled
        };
        applyItem.Click += (s, ev) => OnApplyHouseStyleJsonClick(s, ev);
        menu.Items.Add(applyItem);

        var pasteAndApplyItem = new MenuFlyoutItem
        {
            Text = "Paste and apply AI theme",
            Icon = new FontIcon { Glyph = "\uE77F" }
        };
        pasteAndApplyItem.Click += async (s, ev) =>
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    var text = await content.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        HouseStyleReplyBox.Text = text.Trim();
                        OnApplyHouseStyleJsonClick(s, ev);
                    }
                }
            }
            catch { }
        };
        menu.Items.Add(pasteAndApplyItem);

        var clearReplyItem = new MenuFlyoutItem
        {
            Text = "Clear reply text",
            Icon = new FontIcon { Glyph = "\uE894" }
        };
        clearReplyItem.Click += (_, _) => HouseStyleReplyBox.Text = "";
        menu.Items.Add(clearReplyItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnUpdateStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(App.ViewModel.FileNamePreview):
            case nameof(App.ViewModel.OcrEngineDescription):
                DispatcherQueue.TryEnqueue(RenderGeneralDynamicNames);
                break;
            case nameof(App.ViewModel.UpdatePhase):
                DispatcherQueue.TryEnqueue(RenderAboutUpdate);
                break;
            case nameof(App.ViewModel.PdfBandsPreview):
                DispatcherQueue.TryEnqueue(RenderPdfBands);
                break;
            case nameof(App.ViewModel.ApiStatusText):
            case nameof(App.ViewModel.ApiStatusIsError):
                DispatcherQueue.TryEnqueue(RenderApiStatus);
                break;
            case nameof(App.ViewModel.HouseStyleStatus):
                DispatcherQueue.TryEnqueue(RenderHouseStyleStatus);
                break;
            case nameof(App.ViewModel.GoogleSignInPhase):
            case nameof(App.ViewModel.GoogleDeviceCode):
            case nameof(App.ViewModel.GoogleAuthStatus):
                DispatcherQueue.TryEnqueue(RenderGoogleState);
                break;
        }
    }

    private void RenderGeneralDynamicNames()
    {
        var vm = App.ViewModel;
        if (FileNamePreviewText is not null)
        {
            var preview = vm.FileNamePreview ?? "";
            AutomationProperties.SetName(FileNamePreviewText,
                string.IsNullOrEmpty(preview) ? "Example file name" : $"Example file name: {preview}");
        }
        if (OcrEngineDescriptionText is not null)
        {
            AutomationProperties.SetName(OcrEngineDescriptionText, vm.OcrEngineDescription ?? "");
        }
    }

    private void RenderApiStatus()
    {
        var vm = App.ViewModel;
        var text = vm.ApiStatusText ?? "";
        if (ApiStatusNormalText is not null)
            AutomationProperties.SetName(ApiStatusNormalText, text);
        if (ApiStatusErrorText is not null)
            AutomationProperties.SetName(ApiStatusErrorText, text);
    }

    private void RenderHouseStyleStatus()
    {
        if (HouseStyleStatusText is not null)
            AutomationProperties.SetName(HouseStyleStatusText, App.ViewModel.HouseStyleStatus ?? "");
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
        AutomationProperties.SetName(PdfPagePreviewButton, spoken.Count == 0
            ? "Page preview: no header or footer"
            : "Page preview: " + string.Join(", ", spoken));
    }

    private void OnPdfPagePreviewClick(object sender, RoutedEventArgs e)
    {
        // The next position in the menu's order (an unknown value starts again at the first).
        var current = Array.FindIndex(PdfPageNumberPositions, p => p.Value == App.ViewModel.PdfPageNumberPosition);
        App.ViewModel.PdfPageNumberPosition = PdfPageNumberPositions[(current + 1) % PdfPageNumberPositions.Length].Value;
    }

    // The page-number positions in the order the preview's click (or Enter) cycles through them.
    private static readonly (string Value, string Label)[] PdfPageNumberPositions =
    {
        ("BottomRight", "Bottom right"),
        ("BottomCenter", "Bottom center"),
        ("TopRight", "Top right"),
        ("None", "None"),
    };

    private void OnPdfPagePreviewRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var copyBandsItem = new MenuFlyoutItem
        {
            Text = "Copy header & footer preview",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyBandsItem.Click += async (_, _) =>
        {
            var header = PdfPreviewHeader.Text;
            var footer = PdfPreviewFooter.Text;
            var summary = $"Header: {(string.IsNullOrEmpty(header) ? "(none)" : header)} | Footer: {(string.IsNullOrEmpty(footer) ? "(none)" : footer)}";
            CopyFeedback.CopyWithBubble(PdfPagePreviewButton, summary, "Copied header and footer");
        };
        menu.Items.Add(copyBandsItem);

        var resetBandsItem = new MenuFlyoutItem
        {
            Text = "Reset header & footer to defaults",
            Icon = new FontIcon { Glyph = "\uE777" }
        };
        resetBandsItem.Click += (_, _) =>
        {
            App.ViewModel.PdfHeaderTemplate = string.Empty;
            App.ViewModel.PdfFooterTemplate = string.Empty;
            App.ViewModel.PdfPageNumberPosition = "BottomRight";
        };
        menu.Items.Add(resetBandsItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var posSubItem = new MenuFlyoutSubItem
        {
            Text = "Page number position",
            Icon = new FontIcon { Glyph = "\uE7C3" }
        };

        // Radio items: the open menu shows which position is in use.
        var currentPosition = App.ViewModel.PdfPageNumberPosition;
        foreach (var (value, label) in PdfPageNumberPositions)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = label,
                GroupName = "PdfPageNumberPosition",
                IsChecked = string.Equals(currentPosition, value, StringComparison.Ordinal)
                    || (value == "BottomRight" && string.IsNullOrEmpty(currentPosition)),
            };
            item.Click += (_, _) => App.ViewModel.PdfPageNumberPosition = value;
            posSubItem.Items.Add(item);
        }

        menu.Items.Add(posSubItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var insertTitle = new MenuFlyoutItem
        {
            Text = "Insert {title} into Header",
            Icon = new FontIcon { Glyph = "\uE70F" }
        };
        insertTitle.Click += (_, _) =>
        {
            var cur = App.ViewModel.PdfHeaderTemplate ?? string.Empty;
            App.ViewModel.PdfHeaderTemplate = string.IsNullOrEmpty(cur) ? "{title}" : $"{cur} — {{title}}";
        };
        menu.Items.Add(insertTitle);

        var insertPage = new MenuFlyoutItem
        {
            Text = "Insert Page {page} of {pages} into Footer",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        insertPage.Click += (_, _) =>
        {
            App.ViewModel.PdfFooterTemplate = "Page {page} of {pages}";
        };
        menu.Items.Add(insertPage);

        menu.ShowAt(PdfPagePreviewButton, KeyboardContextMenu.PointIn(e, PdfPagePreviewButton));
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
        AutomationProperties.SetName(GoogleStatusBar, vm.GoogleAuthStatus ?? "");
        AutomationProperties.SetLiveSetting(GoogleStatusBar, AutomationLiveSetting.Polite);
        // "Waiting for you to approve…" then "Connected as …": re-rendered on every state change,
        // so only a new message is read out.
        LiveAnnounce.RaiseIfChanged(GoogleStatusBar, vm.GoogleAuthStatus);
        var hasCode = !string.IsNullOrEmpty(vm.GoogleDeviceCode);
        GoogleCodeText.Text = vm.GoogleDeviceCode;
        AutomationProperties.SetName(GoogleCodeText,
            hasCode ? $"Google sign-in code: {vm.GoogleDeviceCode}" : "Google sign-in code");
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
            AutomationProperties.SetName(PluginWarningsBar, $"{PluginWarningsBar.Title}: {PluginWarningsBar.Message}");
            AutomationProperties.SetLiveSetting(PluginWarningsBar, AutomationLiveSetting.Polite);
            PluginWarningsBar.IsOpen = true;
            LiveAnnounce.RaiseIfChanged(PluginWarningsBar, PluginWarningsBar.Message);
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
        // "Downloading… 40%", "Installed", "Couldn't download": coalesced, so a progress run is
        // read out as it settles rather than once per percent.
        LiveAnnounce.Track(status);
        void SetStatus(string message, bool failed = false)
        {
            status.Text = message;
            status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(status, string.IsNullOrEmpty(message) ? "" : $"{plugin.Name}: {message}");
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
        ToolTipService.SetToolTip(installButton, $"Download and install {plugin.Name}");
        var removeButton = new Button { Content = "Remove", MinWidth = 84 };
        AutomationProperties.SetName(removeButton, $"Remove {plugin.Name}");
        ToolTipService.SetToolTip(removeButton, $"Remove {plugin.Name} from this PC");

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
                if (removeButton.Visibility == Visibility.Visible)
                    removeButton.Focus(FocusState.Programmatic);
                // A new engine can change what the current document renders — refresh the preview.
                PluginsChanged?.Invoke();
            }
            else if (installButton.Visibility == Visibility.Visible)
            {
                installButton.Focus(FocusState.Programmatic);
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
            if (installButton.Visibility == Visibility.Visible)
                installButton.Focus(FocusState.Programmatic);
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

        var card = new Controls.SettingsCard
        {
            Glyph = "\uEA86", // puzzle piece, as on the Plugins nav item
            Header = plugin.Name,
            Description = plugin.Description,
            Action = buttons,
            Details = details,
        };

        var flyout = new MenuFlyout();
        var copyIdItem = new MenuFlyoutItem
        {
            Text = $"Copy plugin ID ({plugin.Id})",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyIdItem.Click += async (_, _) =>
        {
            if (await ClipboardWriter.TrySetTextAsync(plugin.Id) is string copyError)
            {
                SetStatus($"Couldn't copy the plugin ID. {copyError}", failed: true);
                return;
            }
            SetStatus($"Copied plugin ID \u201C{plugin.Id}\u201D");
        };
        flyout.Items.Add(copyIdItem);

        var openFolderItem = new MenuFlyoutItem
        {
            Text = "Open plugins folder",
            Icon = new FontIcon { Glyph = "\uE838" }
        };
        openFolderItem.Click += (s, e) => OnOpenPluginsFolderClick(s, e);
        flyout.Items.Add(openFolderItem);

        card.ContextFlyout = flyout;
        card.KeyDown += async (s, e) =>
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (e.Key == Windows.System.VirtualKey.C && ctrl && !shift && !alt)
            {
                e.Handled = true;
                if (await ClipboardWriter.TrySetTextAsync(plugin.Id) is string copyError)
            {
                SetStatus($"Couldn't copy the plugin ID. {copyError}", failed: true);
                return;
            }
                SetStatus($"Copied plugin ID \u201C{plugin.Id}\u201D");
            }
        };

        return card;
    }

    // Pop the success tick in: fade + a slight overshoot scale so completion feels like a positive
    // "done", not a control quietly toggling visibility. RenderTransform scale + Opacity are both
    // composition-independent animations, so this stays smooth without EnableDependentAnimation.
    private static void ShowTick(FontIcon icon)
    {
        if (!HoverPolish.AnimationsEnabled)
        {
            icon.RenderTransform = null;
            icon.Opacity = 1;
            icon.Visibility = Visibility.Visible;
            return;
        }

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

    private void OnHouseStyleReplyKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == Windows.System.VirtualKey.Enter && ApplyThemeButton.IsEnabled)
        {
            e.Handled = true;
            OnApplyHouseStyleJsonClick(sender, e);
        }
    }

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

    private async void OnCopyPluginsFolderPathClick(object sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith", "Plugins");
        var error = await MarkSmith.Services.ClipboardWriter.TrySetTextAsync(folder);
        if (sender is Button b)
        {
            MarkSmith.Services.CopyFeedback.ApplyButtonTick(b, error);
        }
    }

    private async void OnOpenPluginsFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith", "Plugins");
        System.IO.Directory.CreateDirectory(folder);
        if (sender is Button btn)
        {
            MarkSmith.Services.CopyFeedback.ApplyButtonTick(btn, null);
        }
        try
        {
            var storageFolder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder);
            await Windows.System.Launcher.LaunchFolderAsync(storageFolder);
        }
        catch
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void OnNavItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not NavigationViewItem item) return;
        e.Handled = true;
        Nav.SelectedItem = item;
        var menu = new MenuFlyout();

        // Ctrl+1 to Ctrl+7 jump to the pages in list order (OnSettingsKeyDown).
        var pageIndex = Nav.MenuItems.IndexOf(item);
        var openPage = new MenuFlyoutItem
        {
            Text = $"Open {item.Content} page",
            Icon = new FontIcon { Glyph = "\uE8E5" },
            KeyboardAcceleratorTextOverride = pageIndex is >= 0 and < 7 ? $"Ctrl+{pageIndex + 1}" : ""
        };
        openPage.Click += (_, _) => Nav.SelectedItem = item;
        menu.Items.Add(openPage);

        var nextPage = new MenuFlyoutItem
        {
            Text = "Next page",
            Icon = new FontIcon { Glyph = "\uE76C" },
            KeyboardAcceleratorTextOverride = "Ctrl+Tab"
        };
        nextPage.Click += (_, _) => StepNavPage(1);
        menu.Items.Add(nextPage);

        var prevPage = new MenuFlyoutItem
        {
            Text = "Previous page",
            Icon = new FontIcon { Glyph = "\uE76B" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+Tab"
        };
        prevPage.Click += (_, _) => StepNavPage(-1);
        menu.Items.Add(prevPage);

        menu.Items.Add(new MenuFlyoutSeparator());

        var copyName = new MenuFlyoutItem
        {
            Text = "Copy page name",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyName.Click += (_, _) => MarkSmith.Services.CopyFeedback.CopyWithBubble(item, item.Content?.ToString() ?? "", "Copied page name");
        menu.Items.Add(copyName);

        menu.ShowAt(item, new FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, item) });
    }
}

