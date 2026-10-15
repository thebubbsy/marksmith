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
        ToolTipService.SetToolTip(button, $"Copy {address} for {browser}'s address bar (Right-click for options)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Copy {address} for {browser}'s address bar");
        button.Click += (_, _) => CopyFeedback.CopyWithTick(address, icon);
        button.RightTapped += (s, e) =>
        {
            e.Handled = true;
            ShowChipContextMenu(button, icon, browser, address, e.GetPosition(button));
        };
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
        copyStepItem.Click += async (_, _) =>
        {
            var text = $"Step 1: Open {browser}'s extensions page by pasting {address} into the address bar.";
            await ClipboardWriter.TrySetTextAsync(text);
        };
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

    private void OnStatusCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        Refresh(force: true);
        if (!string.IsNullOrEmpty(StatusText.Text))
        {
            _ = ClipboardWriter.TrySetTextAsync(StatusText.Text);
        }
    }

    private void OnStatusCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInside(source, TurnOnButton))
            return;

        e.Handled = true;
        var menu = new MenuFlyout();

        var refreshItem = new MenuFlyoutItem
        {
            Text = "Check connection now (Ctrl+R)",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        refreshItem.Click += (_, _) => Refresh(force: true);
        menu.Items.Add(refreshItem);

        if (!string.IsNullOrEmpty(StatusText.Text))
        {
            var copyStatusItem = new MenuFlyoutItem
            {
                Text = "Copy connection status (Ctrl+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyStatusItem.Click += async (_, _) =>
            {
                await ClipboardWriter.TrySetTextAsync(StatusText.Text);
            };
            menu.Items.Add(copyStatusItem);
        }

        if (_folder is not null)
        {
            var openFolderItem = new MenuFlyoutItem
            {
                Text = "Open extension folder (Ctrl+O)",
                Icon = new FontIcon { Glyph = "\uE838" }
            };
            openFolderItem.Click += (s, ev) => OnOpenFolderClick(s, ev);
            menu.Items.Add(openFolderItem);

            var copyFolderItem = new MenuFlyoutItem
            {
                Text = "Copy folder path (Ctrl+Shift+C)",
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

        menu.ShowAt(StatusCard, e.GetPosition(StatusCard));
    }

    private void OnStep1BadgeTapped(object sender, TappedRoutedEventArgs e)
    {
        var firstChip = BrowserPagesPanel.Children.OfType<Button>().FirstOrDefault();
        firstChip?.Focus(FocusState.Programmatic);
    }

    private void OnStep2BadgeTapped(object sender, TappedRoutedEventArgs e)
    {
        StatusCard.Focus(FocusState.Programmatic);
    }

    private void OnStep3BadgeTapped(object sender, TappedRoutedEventArgs e)
    {
        (CopyFolderButton ?? (Control)FolderPathBox).Focus(FocusState.Programmatic);
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
        jump1.Click += (_, _) => OnStep1BadgeTapped(this, new TappedRoutedEventArgs());
        menu.Items.Add(jump1);

        var jump3 = new MenuFlyoutItem
        {
            Text = "Focus Step 3 (Extension folder)",
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        jump3.Click += (_, _) => OnStep3BadgeTapped(this, new TappedRoutedEventArgs());
        menu.Items.Add(jump3);

        menu.Items.Add(new MenuFlyoutSeparator());

        if (_folder is not null)
        {
            var copyFolderItem = new MenuFlyoutItem
            {
                Text = "Copy extension folder path (Ctrl+Shift+C)",
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

        menu.ShowAt(target, e.GetPosition(target));
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
        AutomationProperties.SetName(StatusCard, $"Extension status: {message}");
        if (phase != Phase.ApiOff && TurnOnButton.FocusState != FocusState.Unfocused)
        {
            (CopyFolderButton ?? (Control)FolderPathBox).Focus(FocusState.Programmatic);
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
            (CopyFolderButton ?? (Control)FolderPathBox).Focus(FocusState.Programmatic);
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

        menu.ShowAt(target, e.GetPosition(target));
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

        menu.ShowAt(target, e.GetPosition(target));
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

        menu.ShowAt(target, e.GetPosition(target));
    }

    private void ShowError(string message)
    {
        _error = (message, CurrentPhase);
        Refresh(force: true);
    }
}
