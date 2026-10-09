using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        HoverPolish.Track(this);
    }

    /// <summary>Stops the status poll. Call after the hosting dialog closes.</summary>
    public void Detach() => _poll.Stop();

    // "Edge  edge://extensions  [copy]" as one button: the whole chip copies, and its icon ticks.
    private static Button CopyChip(string browser, string address)
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
        ToolTipService.SetToolTip(button, $"Copy {address} for {browser}'s address bar");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Copy {address}");
        button.Click += (_, _) => CopyFeedback.CopyWithTick(address, icon);
        return button;
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
        StatusText.Text = _error?.Text ?? BrowserExtensionPackage.Describe(phase, _apiPort());
        TurnOnButton.Visibility = phase == Phase.ApiOff ? Visibility.Visible : Visibility.Collapsed;
        ThemeBrush.Set(StatusCard, Border.BackgroundProperty,
            phase == Phase.Connected ? "SystemFillColorSuccessBackgroundBrush" : "CardBackgroundFillColorDefaultBrush");
    }

    private async void OnTurnOnClick(object sender, RoutedEventArgs e)
    {
        TurnOnButton.IsEnabled = false;
        string? error;
        try { (_, _, error) = await _turnOnApi(); }
        catch (Exception ex) { error = $"Couldn't turn on the connection: {ex.Message}"; }
        TurnOnButton.IsEnabled = true;
        if (error is null) Refresh(force: true);
        else ShowError(error);
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

    private void ShowError(string message)
    {
        _error = (message, CurrentPhase);
        Refresh(force: true);
    }
}
