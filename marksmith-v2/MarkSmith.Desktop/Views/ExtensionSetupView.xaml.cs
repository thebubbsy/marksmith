using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MarkSmith.Services;
using Phase = MarkSmith.Services.BrowserExtensionPackage.ConnectionPhase;

namespace MarkSmith.Views;

// The in-app "Get the extension" guide. Every entry point (the Source panel card, the plain-paste
// hint bar, Suite Hub) opens this instead of the extension's GitHub source folder. The status card
// polls while the dialog is open; the host must call Detach() after the dialog closes (a
// ContentDialog's popup raises Unloaded while it is still showing, so Unloaded can't be used).
public sealed partial class ExtensionSetupView : UserControl
{
    private readonly Func<bool> _apiEnabled;
    private readonly Func<bool> _apiRunning;
    private readonly Func<int> _apiPort;
    private readonly Func<bool> _extensionConnected;
    private readonly Func<Task<(bool Running, int Port, string? Error)>> _turnOnApi;
    private readonly DispatcherQueueTimer _poll;
    private readonly string? _folder;
    // A failure message (turning the API on, opening a folder) holds until the phase changes.
    private (string Text, Phase For)? _error;
    private Phase? _shown;

    public ExtensionSetupView(Func<bool> apiEnabled, Func<bool> apiRunning, Func<int> apiPort, Func<bool> extensionConnected,
        Func<Task<(bool Running, int Port, string? Error)>> turnOnApi)
    {
        InitializeComponent();
        LiveAnnounce.Track(StatusText);
        // "Right-click for options" menus: open them from Shift+F10 / the Menu key too.
        KeyboardContextMenu.BridgeAll(
            (CopyFolderButton, OnCopyFolderButtonRightTapped),
            (OpenFolderButton, OnOpenFolderButtonRightTapped),
            (TurnOnButton, OnTurnOnButtonRightTapped),
            (CheckNowButton, OnStatusCardRightTapped),
            (GuideHyperlink, OnGuideHyperlinkRightTapped),
            (Step1Badge, OnStepBadgeRightTapped),
            (Step2Badge, OnStepBadgeRightTapped),
            (Step3Badge, OnStepBadgeRightTapped));
        _apiEnabled = apiEnabled;
        _apiRunning = apiRunning;
        _apiPort = apiPort;
        _extensionConnected = extensionConnected;
        _turnOnApi = turnOnApi;

        foreach (var (browser, address) in BrowserExtensionPackage.ExtensionPages)
            BrowserPagesPanel.Children.Add(CopyChip(browser, address));

        _folder = BrowserExtensionPackage.Locate(AppContext.BaseDirectory);
        if (_folder is null)
        {
            BundledPanel.Visibility = Visibility.Collapsed;
            MissingPanel.Visibility = Visibility.Visible;
        }
        else
        {
            FolderPathBox.Text = _folder;
            var version = BrowserExtensionPackage.Version(_folder);
            VersionText.Text = version is null
                ? "Updates to MarkSmith update this folder too; the browser picks them up when it restarts."
                : $"MarkSmith Connector {version}. Updates to MarkSmith update this folder too; the browser picks them up when it restarts.";
        }

        _poll = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(2);
        _poll.IsRepeating = true;
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        Refresh();
        AddHandler(KeyDownEvent, new KeyEventHandler(OnSetupKeyDown), true);
        HoverPolish.Track(this);
    }

    private void OnSetupKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && shift && e.Key == Windows.System.VirtualKey.C)
        {
            if (_folder is not null)
            {
                CopyFeedback.CopyWithTick(_folder, CopyFolderIcon);
                e.Handled = true;
            }
        }
        else if (ctrl && !shift && e.Key == Windows.System.VirtualKey.O)
        {
            if (_folder is not null)
            {
                OnOpenFolderClick(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        else if (ctrl && !shift && e.Key == Windows.System.VirtualKey.R)
        {
            Refresh(force: true);
            e.Handled = true;
        }
    }

    /// <summary>Stops the status poll. Call after the hosting dialog closes.</summary>
    public void Detach() => _poll.Stop();

    // "Edge  edge://extensions  [copy]" as one button: the whole chip copies, and its icon ticks.
    private Button CopyChip(string browser, string address)
    {
        var icon = new FontIcon { Glyph = "\uE8C8", FontSize = 13 };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new TextBlock { Text = browser, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = address,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"));
        content.Children.Add(icon);
        var button = new Button { Content = content };
        ToolTipService.SetToolTip(button, $"Copy {address} for {browser}'s address bar (Right-click or Shift+F10 for options)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Copy {address} for {browser}'s address bar");
        button.Click += (_, _) => CopyFeedback.CopyWithTick(address, icon);
        // The chips are focusable, so their menu has to open from Shift+F10 / the Menu key too.
        RightTappedEventHandler showMenu = (_, e) =>
        {
            e.Handled = true;
            ShowChipContextMenu(button, icon, browser, address, KeyboardContextMenu.PointIn(e, button));
        };
        button.RightTapped += showMenu;
        KeyboardContextMenu.Bridge(button, showMenu);
        return button;
    }

    private void ShowChipContextMenu(Button button, FontIcon icon, string browser, string address, Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();

        var copyAddressItem = new MenuFlyoutItem
        {
            Text = $"Copy {browser} extensions URL",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyAddressItem.Click += (_, _) => CopyFeedback.CopyWithTick(address, icon);
        menu.Items.Add(copyAddressItem);

        var copyStepItem = new MenuFlyoutItem
        {
            Text = "Copy Step 1 instructions",
            Icon = new FontIcon { Glyph = "\uE8FD" }
        };
        copyStepItem.Click += (_, _) => CopyFeedback.CopyWithBubble(button,
            $"Step 1: Open {browser}'s extensions page by pasting {address} into the address bar.",
            "Copied Step 1 instructions");
        menu.Items.Add(copyStepItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var guideItem = new MenuFlyoutItem
        {
            Text = "Read extension guide",
            Icon = new FontIcon { Glyph = "\uE8A7" }
        };
        guideItem.Click += (s, ev) => OnOpenGuideClick(s, ev);
        menu.Items.Add(guideItem);

        menu.ShowAt(button, position);
    }

    private void OnCheckNowClick(object sender, RoutedEventArgs e) => Refresh(force: true);

    private void OnStatusCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInside(source, TurnOnButton))
            return;

        e.Handled = true;
        var menu = new MenuFlyout();

        var refreshItem = new MenuFlyoutItem
        {
            Text = "Check connection now",
            Icon = new FontIcon { Glyph = "\uE72C" },
            KeyboardAcceleratorTextOverride = "Ctrl+R"
        };
        refreshItem.Click += (_, _) => Refresh(force: true);
        menu.Items.Add(refreshItem);

        if (!string.IsNullOrEmpty(StatusText.Text))
        {
            // No shortcut: Ctrl+C belongs to whatever text is selected.
            var copyStatusItem = new MenuFlyoutItem
            {
                Text = "Copy connection status",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyStatusItem.Click += (_, _) => CopyFeedback.CopyWithTick(StatusText.Text, CheckNowIcon);
            menu.Items.Add(copyStatusItem);
        }

        if (_folder is not null)
        {
            var openFolderItem = new MenuFlyoutItem
            {
                Text = "Open extension folder",
                Icon = new FontIcon { Glyph = "\uE838" },
                KeyboardAcceleratorTextOverride = "Ctrl+O"
            };
            openFolderItem.Click += (s, ev) => OnOpenFolderClick(s, ev);
            menu.Items.Add(openFolderItem);

            var copyFolderItem = new MenuFlyoutItem
            {
                Text = "Copy folder path",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C"
            };
            copyFolderItem.Click += (s, ev) => OnCopyFolderClick(s, ev);
            menu.Items.Add(copyFolderItem);
        }

        var guideItem = new MenuFlyoutItem
        {
            Text = "Read extension guide",
            Icon = new FontIcon { Glyph = "\uE8A7" }
        };
        guideItem.Click += (s, ev) => OnOpenGuideClick(s, ev);
        menu.Items.Add(guideItem);

        if (CurrentPhase == Phase.ApiOff && TurnOnButton.Visibility == Visibility.Visible)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var turnOnItem = new MenuFlyoutItem
            {
                Text = "Turn on local API",
                Icon = new FontIcon { Glyph = "\uE7E8" }
            };
            turnOnItem.Click += (s, ev) => OnTurnOnClick(s, ev);
            menu.Items.Add(turnOnItem);
        }

        menu.ShowAt(StatusCard, KeyboardContextMenu.PointIn(e, StatusCard));
    }

    private void OnStep1BadgeClick(object sender, RoutedEventArgs e)
    {
        var firstChip = BrowserPagesPanel.Children.OfType<Button>().FirstOrDefault();
        firstChip?.Focus(FocusState.Programmatic);
    }

    // The status card is a Border and can't take focus; its Check now button is the way in.
    private void OnStep2BadgeClick(object sender, RoutedEventArgs e) => CheckNowButton.Focus(FocusState.Programmatic);

    private void OnStep3BadgeClick(object sender, RoutedEventArgs e) => FocusFolderOrFallback();

    // The folder controls are collapsed when this copy of MarkSmith doesn't ship the extension.
    private void FocusFolderOrFallback()
    {
        if (_folder is not null) CopyFolderButton.Focus(FocusState.Programmatic);
        else CheckNowButton.Focus(FocusState.Programmatic);
    }

    private void OnStepBadgeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? StatusCard;
        var menu = new MenuFlyout();

        var jump1 = new MenuFlyoutItem
        {
            Text = "Focus Step 1 (Browser extension pages)",
            Icon = new FontIcon { Glyph = "\uE71B" }
        };
        jump1.Click += (_, _) => OnStep1BadgeClick(this, new RoutedEventArgs());
        menu.Items.Add(jump1);

        var jump3 = new MenuFlyoutItem
        {
            Text = "Focus Step 3 (Extension folder)",
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        jump3.Click += (_, _) => FocusFolderOrFallback();
        menu.Items.Add(jump3);

        var jump2 = new MenuFlyoutItem { Text = "Focus connection check", Icon = new FontIcon { Glyph = "\uE72C" } };
        jump2.Click += (_, _) => CheckNowButton.Focus(FocusState.Programmatic);
        menu.Items.Insert(1, jump2);

        menu.Items.Add(new MenuFlyoutSeparator());

        if (_folder is not null)
        {
            var copyFolderItem = new MenuFlyoutItem
            {
                Text = "Copy extension folder path",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyFolderItem.Click += (s, ev) => OnCopyFolderClick(s, ev);
            menu.Items.Add(copyFolderItem);
        }

        var guideItem = new MenuFlyoutItem
        {
            Text = "Read extension guide",
            Icon = new FontIcon { Glyph = "\uE8A7" }
        };
        guideItem.Click += (s, ev) => OnOpenGuideClick(s, ev);
        menu.Items.Add(guideItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private Phase CurrentPhase => BrowserExtensionPackage.Phase(_apiEnabled(), _apiRunning(), _extensionConnected());

    private void Refresh(bool force = false)
    {
        var phase = CurrentPhase;
        if (_error is { } error && error.For != phase) _error = null;
        if (!force && phase == _shown) return;
        _shown = phase;

        WaitingRing.IsActive = phase == Phase.Waiting;
        WaitingRing.Visibility = phase == Phase.Waiting ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = phase == Phase.Waiting ? Visibility.Collapsed : Visibility.Visible;
        StatusIcon.Glyph = phase == Phase.Connected ? "\uE73E" : "\uE7BA"; // CheckMark / Warning
        ThemeBrush.Set(StatusIcon, FontIcon.ForegroundProperty,
            phase == Phase.Connected ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        var message = _error?.Text ?? BrowserExtensionPackage.Describe(phase, _apiPort());
        StatusText.Text = message;
        AutomationProperties.SetName(StatusText, message);
        AutomationProperties.SetLiveSetting(StatusText, AutomationLiveSetting.Polite);
        // "Waiting for the extension…" then "Connected": polled, so only a new answer is read out.
        LiveAnnounce.RaiseIfChanged(StatusText, message);
        AutomationProperties.SetName(StatusCard, $"Extension status: {message}");
        if (phase != Phase.ApiOff && TurnOnButton.FocusState != FocusState.Unfocused)
        {
            CheckNowButton.Focus(FocusState.Programmatic);
        }
        TurnOnButton.Visibility = phase == Phase.ApiOff ? Visibility.Visible : Visibility.Collapsed;
        ThemeBrush.Set(StatusCard, Border.BackgroundProperty,
            phase == Phase.Connected ? "SystemFillColorSuccessBackgroundBrush" : "CardBackgroundFillColorDefaultBrush");
    }

    private async void OnTurnOnClick(object sender, RoutedEventArgs e)
    {
        var hadFocus = TurnOnButton.FocusState != FocusState.Unfocused;
        TurnOnButton.IsEnabled = false;
        if (hadFocus)
        {
            CheckNowButton.Focus(FocusState.Programmatic);
        }
        string? error;
        try { (_, _, error) = await _turnOnApi(); }
        catch (Exception ex) { error = $"Couldn't turn on the connection: {ex.Message}"; }
        TurnOnButton.IsEnabled = true;
        if (error is null)
        {
            Refresh(force: true);
        }
        else
        {
            ShowError(error);
            if (hadFocus) TurnOnButton.Focus(FocusState.Programmatic);
        }
    }

    private void OnCopyFolderClick(object sender, RoutedEventArgs e)
    {
        if (_folder is not null) CopyFeedback.CopyWithTick(_folder, CopyFolderIcon);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_folder is null) return;
        try { Process.Start(new ProcessStartInfo { FileName = _folder, UseShellExecute = true }); }
        catch (Exception ex) { ShowError($"Couldn't open the folder: {ex.Message}"); }
    }

    private void OnOpenGuideClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = BrowserExtensionPackage.GuideUrl, UseShellExecute = true }); }
        catch (Exception ex) { ShowError($"Couldn't open the guide: {ex.Message}"); }
    }

    private void OnGuideHyperlinkRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? (FrameworkElement)this;
        var menu = new MenuFlyout();

        var openGuideItem = new MenuFlyoutItem
        {
            Text = "Open guide in browser",
            Icon = new FontIcon { Glyph = "\uE8A7" }
        };
        openGuideItem.Click += (s, ev) => OnOpenGuideClick(s, ev);
        menu.Items.Add(openGuideItem);

        var copyUrlItem = new MenuFlyoutItem
        {
            Text = "Copy guide web address",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyUrlItem.Click += (_, _) =>
        {
            if (copyUrlItem.Icon is FontIcon fi) CopyFeedback.CopyWithTick(BrowserExtensionPackage.GuideUrl, fi);
        };
        menu.Items.Add(copyUrlItem);

        if (_folder is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var copyFolderItem = new MenuFlyoutItem
            {
                Text = "Copy extension folder path",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C"
            };
            copyFolderItem.Click += (s, ev) => OnCopyFolderClick(s, ev);
            menu.Items.Add(copyFolderItem);

            var openFolderItem = new MenuFlyoutItem
            {
                Text = "Show in File Explorer",
                Icon = new FontIcon { Glyph = "\uE838" },
                KeyboardAcceleratorTextOverride = "Ctrl+O"
            };
            openFolderItem.Click += (s, ev) => OnOpenFolderClick(s, ev);
            menu.Items.Add(openFolderItem);
        }

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnCopyFolderButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? CopyFolderButton;
        var menu = new MenuFlyout();

        if (_folder is not null)
        {
            var copyItem = new MenuFlyoutItem
            {
                Text = "Copy folder path",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C"
            };
            copyItem.Click += (s, ev) => OnCopyFolderClick(s, ev);
            menu.Items.Add(copyItem);

            var openItem = new MenuFlyoutItem
            {
                Text = "Show in File Explorer",
                Icon = new FontIcon { Glyph = "\uE838" },
                KeyboardAcceleratorTextOverride = "Ctrl+O"
            };
            openItem.Click += (s, ev) => OnOpenFolderClick(s, ev);
            menu.Items.Add(openItem);

            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var guideItem = new MenuFlyoutItem
        {
            Text = "Read extension guide",
            Icon = new FontIcon { Glyph = "\uE8A7" }
        };
        guideItem.Click += (s, ev) => OnOpenGuideClick(s, ev);
        menu.Items.Add(guideItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnOpenFolderButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? OpenFolderButton;
        var menu = new MenuFlyout();

        if (_folder is not null)
        {
            var openItem = new MenuFlyoutItem
            {
                Text = "Show in File Explorer",
                Icon = new FontIcon { Glyph = "\uE838" },
                KeyboardAcceleratorTextOverride = "Ctrl+O"
            };
            openItem.Click += (s, ev) => OnOpenFolderClick(s, ev);
            menu.Items.Add(openItem);

            var copyItem = new MenuFlyoutItem
            {
                Text = "Copy folder path",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C"
            };
            copyItem.Click += (s, ev) => OnCopyFolderClick(s, ev);
            menu.Items.Add(copyItem);

            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var guideItem = new MenuFlyoutItem
        {
            Text = "Read extension guide",
            Icon = new FontIcon { Glyph = "\uE8A7" }
        };
        guideItem.Click += (s, ev) => OnOpenGuideClick(s, ev);
        menu.Items.Add(guideItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnTurnOnButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? TurnOnButton;
        var menu = new MenuFlyout();

        var turnOnItem = new MenuFlyoutItem
        {
            Text = "Turn on Local API server",
            Icon = new FontIcon { Glyph = "\uE968" }
        };
        turnOnItem.Click += (s, ev) => OnTurnOnClick(s, ev);
        menu.Items.Add(turnOnItem);

        var checkItem = new MenuFlyoutItem
        {
            Text = "Check connection status",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        checkItem.Click += (_, _) => Refresh(force: true);
        menu.Items.Add(checkItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var guideItem = new MenuFlyoutItem
        {
            Text = "Read extension guide",
            Icon = new FontIcon { Glyph = "\uE8A7" }
        };
        guideItem.Click += (s, ev) => OnOpenGuideClick(s, ev);
        menu.Items.Add(guideItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void ShowError(string message)
    {
        _error = (message, CurrentPhase);
        Refresh(force: true);
    }
}
