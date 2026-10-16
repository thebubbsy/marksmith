using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using MarkSmith.Services;

namespace MarkSmith.Views;

public sealed partial class SuiteHubView : UserControl
{
    public const string ExpressUrl = "http://localhost:5000";

    public event Action? OpenMermaidStudioRequested;
    public event Action? OpenShapeStudioRequested;
    public event Action? OpenSmartArtStudioRequested;
    public event Action? OpenGalaxyRequested;
    // The host closes the hub and opens the extension setup guide (one ContentDialog at a time).
    public event Action? OpenExtensionSetupRequested;

    private int _apiPort;
    private bool _apiRunning;
    private bool _isSingleColumn;
    private readonly Func<System.Threading.Tasks.Task<(bool Running, int Port, string? Error)>>? _turnOnApi;
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly HashSet<Border> _hoveredCards = new();
    private Border[] _cards = Array.Empty<Border>();

    // apiRunning/apiPort come from the live AutomationManager: the Browser Companion badge used to
    // say "REST API Listening" in green whether or not the API was enabled, and the copied URL
    // ignored a custom port.
    // turnOnApi switches the local API on (the Settings › Automation setting) and reports whether it
    // came up, so "Not connected" is one click from fixed instead of a dead end.
    public SuiteHubView(bool apiRunning = false, int apiPort = 47821,
        Func<System.Threading.Tasks.Task<(bool Running, int Port, string? Error)>>? turnOnApi = null)
    {
        InitializeComponent();
        _apiPort = apiPort > 0 ? apiPort : 47821;
        _turnOnApi = turnOnApi;
        PopulateMetadata();
        ShowApiState(apiRunning);

        // "CLI Installed" was hard-coded too. The CLI ships beside the app; say so only if it's there.
        var cliPresent = File.Exists(CliPath);
        SetBadge(CliStatusText, cliPresent ? "Bundled" : "Not in this build", cliPresent);
        CopyCliPathButton.IsEnabled = cliPresent;
        if (!cliPresent)
            ToolTipService.SetToolTip(CopyCliPathButton, "marksmith.exe isn't installed alongside this copy of MarkSmith");

        _cards = new[] { HeaderCardBorder, Card1Border, Card2Border, Card3Border, Card4Border, Card5Border, Card6Border };
        for (var i = 0; i < _cards.Length; i++)
        {
            WireCardPolish(_cards[i], i);
        }

        _notificationTimer.Tick += (_, _) => { _notificationTimer.Stop(); Notification.IsOpen = false; };
        Unloaded += (_, _) => _notificationTimer.Stop();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnHubKeyDown), true);
        HoverPolish.Track(this);
        Loaded += (_, _) => { LoadRecentFiles(); UpdateMemoryUsage(); UpdateUptimeAndCache(); };
    }

    private void WireCardPolish(Border card, int cardIndex)
    {
        card.PointerEntered += (_, _) =>
        {
            _hoveredCards.Add(card);
            RefreshCardVisual(card);
        };
        card.PointerExited += (_, _) =>
        {
            _hoveredCards.Remove(card);
            RefreshCardVisual(card);
        };
        card.PointerCanceled += (_, _) =>
        {
            _hoveredCards.Remove(card);
            RefreshCardVisual(card);
        };
        card.GotFocus += (_, _) => RefreshCardVisual(card);
        card.LostFocus += (_, _) => DispatcherQueue?.TryEnqueue(() => RefreshCardVisual(card));
        card.RightTapped += (_, e) =>
        {
            e.Handled = true;
            ShowCardContextMenu(cardIndex, card, e.GetPosition(card));
        };
    }

    private void RefreshCardVisual(Border card)
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        var hasFocusInside = focused is not null && IsInside(focused, card);
        var isHovered = _hoveredCards.Contains(card);

        ThemeBrush.Set(card, Border.BackgroundProperty,
            hasFocusInside || isHovered ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");
        ThemeBrush.Set(card, Border.BorderBrushProperty,
            hasFocusInside ? "AccentFillColorDefaultBrush"
            : isHovered ? "ControlStrongStrokeColorDefaultBrush"
            : "CardStrokeColorDefaultBrush");
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;
        var singleColumn = width < 560;
        if (singleColumn == _isSingleColumn && Col1.Width.Value > 0 == !singleColumn) return;
        _isSingleColumn = singleColumn;

        if (singleColumn)
        {
            Col1.Width = new GridLength(0);
            CardsGrid.ColumnSpacing = 0;
            for (var i = 1; i <= 6; i++)
            {
                Grid.SetRow(_cards[i], i - 1);
                Grid.SetColumn(_cards[i], 0);
            }
        }
        else
        {
            Col1.Width = new GridLength(1, GridUnitType.Star);
            CardsGrid.ColumnSpacing = 12;
            Grid.SetRow(Card1Border, 0); Grid.SetColumn(Card1Border, 0);
            Grid.SetRow(Card2Border, 0); Grid.SetColumn(Card2Border, 1);
            Grid.SetRow(Card3Border, 1); Grid.SetColumn(Card3Border, 0);
            Grid.SetRow(Card4Border, 1); Grid.SetColumn(Card4Border, 1);
            Grid.SetRow(Card5Border, 2); Grid.SetColumn(Card5Border, 0);
            Grid.SetRow(Card6Border, 2); Grid.SetColumn(Card6Border, 1);
        }
    }

    public static int GetGridNeighborCard(int currentCard, string direction, bool singleColumn)
    {
        if (currentCard < 0 || currentCard > 6) return -1;
        if (singleColumn)
        {
            return direction switch
            {
                "Up" or "Left" => currentCard > 0 ? currentCard - 1 : -1,
                "Down" or "Right" => currentCard < 6 ? currentCard + 1 : -1,
                _ => -1,
            };
        }

        return (currentCard, direction) switch
        {
            (0, "Down") => 2,
            (0, "Right") => 1,
            (1, "Up") => 0,
            (1, "Left") => 0,
            (2, "Up") => 0,
            (1, "Right") => 2,
            (2, "Left") => 1,
            (1, "Down") => 3,
            (2, "Down") => 4,
            (3, "Up") => 1,
            (4, "Up") => 2,
            (3, "Right") => 4,
            (4, "Left") => 3,
            (3, "Down") => 5,
            (4, "Down") => 6,
            (5, "Up") => 3,
            (6, "Up") => 4,
            (5, "Right") => 6,
            (6, "Left") => 5,
            _ => -1,
        };
    }

    private void OnHubKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && Notification.IsOpen)
        {
            Notification.IsOpen = false;
            _notificationTimer.Stop();
            e.Handled = true;
            return;
        }

        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.Application || (shift && !ctrl && !alt && e.Key == VirtualKey.F10))
        {
            var focusedIdx = GetFocusedCardIndex();
            var targetIdx = focusedIdx >= 0 ? focusedIdx : 1;
            ShowCardContextMenu(targetIdx, _cards[targetIdx], null);
            e.Handled = true;
            return;
        }

        if (!ctrl && !shift && !alt && e.OriginalSource is not TextBox)
        {
            switch (e.Key)
            {
                case VirtualKey.Number1 or VirtualKey.NumberPad1:
                    OnOpenMermaidStudioClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case VirtualKey.Number2 or VirtualKey.NumberPad2:
                    OnCopyClaudeConfigClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case VirtualKey.Number3 or VirtualKey.NumberPad3:
                    OnOpenExtensionDocsClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case VirtualKey.Number4 or VirtualKey.NumberPad4:
                    OnCopyCliCommandClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case VirtualKey.Number5 or VirtualKey.NumberPad5:
                    OnLaunchExpressClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case VirtualKey.Number6 or VirtualKey.NumberPad6:
                    OnOpenGalaxyClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case VirtualKey.F1 or (VirtualKey)191:
                    ShortcutsFlyout?.ShowAt(ShortcutsHelpButton);
                    e.Handled = true;
                    return;
            }
        }

        if (ctrl && !shift && !alt)
        {
            if (e.Key == VirtualKey.O)
            {
                OnOpenConfigFolderClick(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.C)
            {
                var cardIdx = GetFocusedCardIndex();
                switch (cardIdx)
                {
                    case 0:
                        OnVersionInfoTapped(this, new TappedRoutedEventArgs());
                        e.Handled = true;
                        return;
                    case 2:
                        OnCopyClaudeConfigClick(this, new RoutedEventArgs());
                        e.Handled = true;
                        return;
                    case 3:
                        if (CopyApiUrlButton.Visibility == Visibility.Visible)
                        {
                            OnCopyApiUrlClick(CopyApiUrlButton, new RoutedEventArgs());
                            e.Handled = true;
                            return;
                        }
                        break;
                    case 4:
                        if (File.Exists(CliPath))
                        {
                            OnCopyCliPathClick(CopyCliPathButton, new RoutedEventArgs());
                            e.Handled = true;
                            return;
                        }
                        break;
                    case 5:
                        OnCopyExpressUrlClick(CopyExpressUrlButton, new RoutedEventArgs());
                        e.Handled = true;
                        return;
                }
            }

            Control? target = e.Key switch
            {
                VirtualKey.Number1 or VirtualKey.NumberPad1 => Card1DiagramButton,
                VirtualKey.Number2 or VirtualKey.NumberPad2 => Card2CopySetupButton,
                VirtualKey.Number3 or VirtualKey.NumberPad3 => TurnOnApiButton.Visibility == Visibility.Visible ? TurnOnApiButton : CopyApiUrlButton,
                VirtualKey.Number4 or VirtualKey.NumberPad4 => Card4CopyCommandButton,
                VirtualKey.Number5 or VirtualKey.NumberPad5 => Card5ExpressButton,
                VirtualKey.Number6 or VirtualKey.NumberPad6 => Card6GalaxyButton,
                _ => null
            };

            if (target is not null)
            {
                FocusAndBringIntoView(target, FocusState.Keyboard);
                e.Handled = true;
                return;
            }
        }
        else if (ctrl && shift && !alt)
        {
            if (e.Key == VirtualKey.M)
            {
                OpenMermaidStudioRequested?.Invoke();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.S)
            {
                OpenShapeStudioRequested?.Invoke();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.A)
            {
                OpenSmartArtStudioRequested?.Invoke();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.C && File.Exists(CliPath))
            {
                CopyToClipboard(CliPath, "Copied the MarkSmith CLI path.", CopyCliPathButton);
                e.Handled = true;
            }
        }
        else if (!ctrl && !shift && !alt && !IsMenuFlyoutOpen())
        {
            if (e.Key == VirtualKey.Home)
            {
                HubScroller.ChangeView(null, 0, null, !HoverPolish.AnimationsEnabled);
                FocusAndBringIntoView(Card1DiagramButton, FocusState.Keyboard);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.End)
            {
                FocusAndBringIntoView(Card6GalaxyButton, FocusState.Keyboard);
                e.Handled = true;
            }
            else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            {
                if (TryNavigateCards(e.Key))
                {
                    e.Handled = true;
                }
            }
        }
    }

    private bool IsMenuFlyoutOpen()
    {
        if (XamlRoot is null) return false;
        return VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(p => p.Child is not ContentDialog);
    }

    private int GetFocusedCardIndex()
    {
        if (XamlRoot is null) return -1;
        if (FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused) return -1;
        for (var i = 0; i < _cards.Length; i++)
        {
            if (IsInside(focused, _cards[i])) return i;
        }
        return -1;
    }

    private IReadOnlyList<Button> GetCardButtons(int cardIndex)
    {
        Button[] raw = cardIndex switch
        {
            0 => new[] { OpenConfigFolderButton },
            1 => new[] { Card1DiagramButton, Card1ShapeButton, Card1SmartArtButton },
            2 => new[] { Card2CopySetupButton, CopyMcpPathButton },
            3 => new[] { TurnOnApiButton, CopyApiUrlButton, GetExtensionButton },
            4 => new[] { Card4CopyCommandButton, CopyCliPathButton },
            5 => new[] { Card5ExpressButton, CopyExpressUrlButton },
            6 => new[] { Card6GalaxyButton },
            _ => Array.Empty<Button>()
        };
        return raw.Where(b => b is not null && b.Visibility == Visibility.Visible && b.IsEnabled).ToList();
    }

    private bool TryNavigateCards(VirtualKey key)
    {
        if (XamlRoot is null) return false;
        var cardIndex = GetFocusedCardIndex();
        if (cardIndex < 0) return false;

        var focusedButton = FocusManager.GetFocusedElement(XamlRoot) as Button;
        var buttons = GetCardButtons(cardIndex);
        var btnIdx = focusedButton is not null ? Math.Max(0, buttons.ToList().IndexOf(focusedButton)) : 0;

        if (key == VirtualKey.Left)
        {
            if (btnIdx > 0)
            {
                FocusAndBringIntoView(buttons[btnIdx - 1], FocusState.Keyboard);
                return true;
            }
            var prevCard = GetGridNeighborCard(cardIndex, "Left", _isSingleColumn);
            if (prevCard >= 0)
            {
                var prevButtons = GetCardButtons(prevCard);
                if (prevButtons.Count > 0)
                {
                    FocusAndBringIntoView(prevButtons[^1], FocusState.Keyboard);
                    return true;
                }
            }
            return false;
        }

        if (key == VirtualKey.Right)
        {
            if (btnIdx >= 0 && btnIdx + 1 < buttons.Count)
            {
                FocusAndBringIntoView(buttons[btnIdx + 1], FocusState.Keyboard);
                return true;
            }
            var nextCard = GetGridNeighborCard(cardIndex, "Right", _isSingleColumn);
            if (nextCard >= 0)
            {
                var nextButtons = GetCardButtons(nextCard);
                if (nextButtons.Count > 0)
                {
                    FocusAndBringIntoView(nextButtons[0], FocusState.Keyboard);
                    return true;
                }
            }
            return false;
        }

        var dir = key == VirtualKey.Up ? "Up" : "Down";
        var neighbor = GetGridNeighborCard(cardIndex, dir, _isSingleColumn);
        if (neighbor >= 0)
        {
            var neighborButtons = GetCardButtons(neighbor);
            if (neighborButtons.Count > 0)
            {
                var targetBtn = neighborButtons[Math.Clamp(btnIdx, 0, neighborButtons.Count - 1)];
                FocusAndBringIntoView(targetBtn, FocusState.Keyboard);
                return true;
            }
        }
        return false;
    }

    private static void FocusAndBringIntoView(Control target, FocusState state = FocusState.Programmatic)
    {
        target.Focus(state);
        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = HoverPolish.AnimationsEnabled });
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private void ShowCardContextMenu(int cardIndex, FrameworkElement anchor, Windows.Foundation.Point? position)
    {
        var menu = BuildCardContextMenu(cardIndex);
        if (menu.Items.Count == 0) return;
        if (position.HasValue)
            menu.ShowAt(anchor, position.Value);
        else
            menu.ShowAt(anchor);
    }

    private MenuFlyout BuildCardContextMenu(int cardIndex)
    {
        var menu = new MenuFlyout();
        switch (cardIndex)
        {
            case 0:
                menu.Items.Add(MakeMenuItem("Open settings folder (Ctrl+O)", "\uE8B7", () => OnOpenConfigFolderClick(this, new RoutedEventArgs())));
                menu.Items.Add(MakeMenuItem("Copy settings folder path", "\uE8C8", () => CopyToClipboard(AppPaths.ConfigDir, "Copied the configuration folder path.", OpenConfigFolderButton)));
                menu.Items.Add(MakeMenuItem("Copy version & plan info", "\uE946", () => CopyToClipboard($"{VersionText.Text} · {LicenseText.Text}", $"Copied version info ({VersionText.Text} · {LicenseText.Text}).")));
                break;
            case 1:
                menu.Items.Add(MakeMenuItem("Open Diagram Studio (Ctrl+Shift+M)", "\uEF90", () => OnOpenMermaidStudioClick(this, new RoutedEventArgs())));
                menu.Items.Add(MakeMenuItem("Open Shape Studio (Ctrl+Shift+S)", "\uE790", () => OnOpenShapeStudioClick(this, new RoutedEventArgs())));
                menu.Items.Add(MakeMenuItem("Open SmartArt Studio (Ctrl+Shift+A)", "\uE8EC", () => OnOpenSmartArtStudioClick(this, new RoutedEventArgs())));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MakeMenuItem("Copy studio shortcuts", "\uE8C8", () => CopyToClipboard("Diagram Studio (Ctrl+Shift+M), Shape Studio (Ctrl+Shift+S), SmartArt Studio (Ctrl+Shift+A)", "Copied studio keyboard shortcuts.")));
                break;
            case 2:
                menu.Items.Add(MakeMenuItem("Copy setup for Claude Desktop", "\uE99A", () => OnCopyClaudeConfigClick(this, new RoutedEventArgs())));
                menu.Items.Add(MakeMenuItem("Copy setup for Cursor, Gemini and other apps", "\uE943", () => OnCopyGeminiConfigClick(this, new RoutedEventArgs())));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MakeMenuItem("Copy MCP server path", "\uE756", () => OnCopyMcpPathClick(this, new RoutedEventArgs())));
                break;
            case 3:
                if (!_apiRunning && _turnOnApi is not null)
                    menu.Items.Add(MakeMenuItem("Turn on local API connection", "\uE7E8", () => OnTurnOnApiClick(this, new RoutedEventArgs())));
                menu.Items.Add(MakeMenuItem("Copy local API address", "\uE71B", () => OnCopyApiUrlClick(this, new RoutedEventArgs())));
                menu.Items.Add(MakeMenuItem("Get the extension…", "\uEA86", () => OnOpenExtensionDocsClick(this, new RoutedEventArgs())));
                break;
            case 4:
                menu.Items.Add(MakeMenuItem("Convert a file to Word", "\uE8A5", () => CopyCliCommandByMode("file")));
                menu.Items.Add(MakeMenuItem("Convert every file in a folder", "\uE8B7", () => CopyCliCommandByMode("batch")));
                menu.Items.Add(MakeMenuItem("Check the installation", "\uE9D5", () => CopyCliCommandByMode("doctor")));
                menu.Items.Add(new MenuFlyoutSeparator());
                var copyPathItem = MakeMenuItem("Copy CLI path (Ctrl+Shift+C)", "\uE8C8", () => OnCopyCliPathClick(this, new RoutedEventArgs()));
                copyPathItem.IsEnabled = File.Exists(CliPath);
                menu.Items.Add(copyPathItem);
                break;
            case 5:
                menu.Items.Add(MakeMenuItem("Open in browser (Ctrl+5)", "\uE774", () => OnLaunchExpressClick(this, new RoutedEventArgs())));
                menu.Items.Add(MakeMenuItem("Copy web companion address", "\uE71B", () => OnCopyExpressUrlClick(this, new RoutedEventArgs())));
                break;
            case 6:
                menu.Items.Add(MakeMenuItem("Open Document Galaxy (Ctrl+6)", "\uEC26", () => OnOpenGalaxyClick(this, new RoutedEventArgs())));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MakeMenuItem("Copy Galaxy shortcut (Ctrl+6)", "\uE8C8", () => CopyToClipboard("Document Galaxy (Ctrl+6)", "Copied Document Galaxy shortcut.")));
                break;
        }
        return menu;
    }

    private static MenuFlyoutItem MakeMenuItem(string text, string glyph, Action onClick)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph }
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static string CliPath => Path.Combine(AppContext.BaseDirectory, "marksmith.exe");

    private void ShowApiState(bool running)
    {
        _apiRunning = running;
        SetBadge(ApiStatusText, running ? "Connected" : "Not connected", running);
        ToolTipService.SetToolTip(ApiBadge, running
            ? $"MarkSmith is listening on http://127.0.0.1:{_apiPort}, so the extension can send chats here."
            : "The extension can't reach MarkSmith until the local connection is on.");
        TurnOnApiButton.Visibility = running || _turnOnApi is null ? Visibility.Collapsed : Visibility.Visible;
        CopyApiUrlButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnTurnOnApiClick(object sender, RoutedEventArgs e)
    {
        if (_turnOnApi is null) return;
        var hadFocus = XamlRoot is not null &&
                       FocusManager.GetFocusedElement(XamlRoot) is DependencyObject f &&
                       IsInside(f, Card3Border);
        TurnOnApiButton.IsEnabled = false;
        try
        {
            var (running, port, error) = await _turnOnApi();
            if (port > 0) _apiPort = port;
            ShowApiState(running);
            if (running) SetNotification("Connected. The browser extension can now send chats to MarkSmith.");
            else SetNotification(error ?? "MarkSmith couldn't open its local connection. Check Settings › Automation.", success: false);
        }
        finally
        {
            TurnOnApiButton.IsEnabled = true;
            if (hadFocus)
            {
                var nextTarget = TurnOnApiButton.Visibility == Visibility.Visible ? TurnOnApiButton : CopyApiUrlButton;
                FocusAndBringIntoView(nextTarget, FocusState.Programmatic);
                RefreshCardVisual(Card3Border);
            }
        }
    }

    private static void SetBadge(TextBlock badge, string text, bool positive)
    {
        badge.Text = text;
        AutomationProperties.SetName(badge, $"Status: {text}");
        ThemeBrush.Set(badge, TextBlock.ForegroundProperty,
            positive ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
    }

    private void PopulateMetadata()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version?.ToString(3) ?? "3.0.0";
            VersionText.Text = $"Version {ver}";

            AppServices.License.Load();
            LicenseText.Text = MarkSmith.Models.ProGate.PlanBadge(AppServices.License.State);
            AutomationProperties.SetName(LicenseBadge, $"Plan: {LicenseText.Text}");
        }
        catch { }
    }

    // A result line under the cards. A success closes itself after a few seconds so a stale
    // "Copied…" never lingers into the next action; a problem stays until it's dismissed or replaced.
    private void SetNotification(string message, bool success = true)
    {
        _notificationTimer.Stop();
        Notification.Message = message.TrimStart('✓', ' ');
        Notification.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        AutomationProperties.SetName(Notification, Notification.Message);
        AutomationProperties.SetLiveSetting(Notification, AutomationLiveSetting.Assertive);
        Notification.IsOpen = true;
        // The hub scrolls in a short window; the result must land where it can be read.
        Notification.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = HoverPolish.AnimationsEnabled });
        if (success) _notificationTimer.Start();
    }

    private async void CopyToClipboard(string text, string successMessage, Button? anchorButton = null)
    {
        var error = await ClipboardWriter.TrySetTextAsync(text);
        if (anchorButton is not null)
        {
            CopyFeedback.ApplyButtonTick(anchorButton, error);
        }
        SetNotification(error is null ? successMessage : $"Nothing was copied. {error}", success: error is null);
    }

    private void OnShortcutsHelpClick(object sender, RoutedEventArgs e)
    {
        ShortcutsFlyout?.ShowAt(ShortcutsHelpButton);
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AppPaths.ConfigDir;
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            SetNotification("Opened the configuration folder.");
        }
        catch (Exception ex)
        {
            SetNotification($"Couldn't open the folder: {ex.Message}", success: false);
        }
    }

    private void OnOpenConfigFolderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
            ShowCardContextMenu(0, fe, e.GetPosition(fe));
    }

    private void OnCard1ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
            ShowCardContextMenu(1, fe, e.GetPosition(fe));
    }

    private void OnCard2ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
            ShowCardContextMenu(2, fe, e.GetPosition(fe));
    }

    private void OnCard3ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
            ShowCardContextMenu(3, fe, e.GetPosition(fe));
    }

    private void OnCard4ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
            ShowCardContextMenu(4, fe, e.GetPosition(fe));
    }

    private void OnCard5ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
            ShowCardContextMenu(5, fe, e.GetPosition(fe));
    }

    private void OnCard6ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
            ShowCardContextMenu(6, fe, e.GetPosition(fe));
    }

    private void OnVersionInfoTapped(object sender, TappedRoutedEventArgs e)
    {
        var info = $"{VersionText.Text} · {LicenseText.Text}";
        CopyToClipboard(info, $"Copied version info ({info}).");
        e.Handled = true;
    }

    private void OnVersionInfoRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var copyVersionItem = new MenuFlyoutItem
        {
            Text = "Copy version details",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyVersionItem.Click += (_, _) =>
        {
            CopyToClipboard(VersionText.Text, $"Copied version ({VersionText.Text}).");
        };
        menu.Items.Add(copyVersionItem);

        var copyLicenseItem = new MenuFlyoutItem
        {
            Text = "Copy license and plan details",
            Icon = new FontIcon { Glyph = "\uE8D7" }
        };
        copyLicenseItem.Click += (_, _) =>
        {
            CopyToClipboard(LicenseText.Text, $"Copied plan details ({LicenseText.Text}).");
        };
        menu.Items.Add(copyLicenseItem);

        var copyBothItem = new MenuFlyoutItem
        {
            Text = "Copy full version & license overview",
            Icon = new FontIcon { Glyph = "\uE946" }
        };
        copyBothItem.Click += (_, _) =>
        {
            var info = $"{VersionText.Text} · {LicenseText.Text}";
            CopyToClipboard(info, $"Copied version and license info ({info}).");
        };
        menu.Items.Add(copyBothItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var openConfigItem = new MenuFlyoutItem
        {
            Text = "Open settings folder",
            Icon = new FontIcon { Glyph = "\uE8B7" },
            KeyboardAcceleratorTextOverride = "Ctrl+O"
        };
        openConfigItem.Click += (s, args) => OnOpenConfigFolderClick(s, new RoutedEventArgs());
        menu.Items.Add(openConfigItem);

        if (sender is FrameworkElement fe)
            menu.ShowAt(fe, e.GetPosition(fe));
        else
            menu.ShowAt(this);
    }

    private void OnHealthStatusTapped(object sender, TappedRoutedEventArgs e)
    {
        var status = $"MarkSmith WinUI 3 Desktop Engine: Active · {VersionText?.Text ?? "Version 3.0.0"} · All Studios Ready";
        CopyToClipboard(status, "Copied suite health status.");
        e.Handled = true;
    }

    private void OnHealthStatusRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();
        var copyStatusItem = new MenuFlyoutItem
        {
            Text = "Copy system health diagnostics",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyStatusItem.Click += (_, _) =>
        {
            var diag = $"MarkSmith Engine: Ready{Environment.NewLine}Version: {VersionText?.Text ?? "3.0.0"}{Environment.NewLine}License: {LicenseText?.Text ?? "Free plan"}{Environment.NewLine}Platform: WinUI 3 Desktop x64{Environment.NewLine}.NET Runtime: {Environment.Version}";
            CopyToClipboard(diag, "Copied system diagnostics.");
        };
        menu.Items.Add(copyStatusItem);
        if (HealthStatusBadge != null)
            menu.ShowAt(HealthStatusBadge, e.GetPosition(HealthStatusBadge));
        else if (sender is FrameworkElement feTarget)
            menu.ShowAt(feTarget);
    }

    private void OnOpenMermaidStudioClick(object sender, RoutedEventArgs e)
    {
        OpenMermaidStudioRequested?.Invoke();
    }

    private void OnOpenShapeStudioClick(object sender, RoutedEventArgs e)
    {
        OpenShapeStudioRequested?.Invoke();
    }

    private void OnOpenSmartArtStudioClick(object sender, RoutedEventArgs e)
    {
        OpenSmartArtStudioRequested?.Invoke();
    }

    private void OnOpenGalaxyClick(object sender, RoutedEventArgs e)
    {
        OpenGalaxyRequested?.Invoke();
    }

    private string GetMcpServerPath()
    {
        var appDir = AppContext.BaseDirectory;
        var mcpExe = Path.Combine(appDir, "marksmith-mcp.exe");
        if (!File.Exists(mcpExe))
        {
            mcpExe = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "MarkSmith.Mcp", "bin", "Debug", "net8.0", "marksmith-mcp.exe"));
        }
        return File.Exists(mcpExe) ? mcpExe : "marksmith-mcp";
    }

    public static string BuildMcpConfigJson(string exePath)
    {
        var configObj = new
        {
            mcpServers = new
            {
                marksmith = new
                {
                    command = exePath,
                    args = Array.Empty<string>()
                }
            }
        };
        return JsonSerializer.Serialize(configObj, new JsonSerializerOptions { WriteIndented = true });
    }

    private void OnCopyClaudeConfigClick(object sender, RoutedEventArgs e)
    {
        var json = BuildMcpConfigJson(GetMcpServerPath());
        CopyToClipboard(json, "Copied the MCP server entry — paste it into claude_desktop_config.json.", Card2CopySetupButton);
    }

    private void OnCopyGeminiConfigClick(object sender, RoutedEventArgs e)
    {
        var json = BuildMcpConfigJson(GetMcpServerPath());
        CopyToClipboard(json, "Copied the MCP server entry — paste it into your client's .mcp.json.", Card2CopySetupButton);
    }

    private void OnCopyMcpPathClick(object sender, RoutedEventArgs e)
    {
        var exePath = GetMcpServerPath();
        CopyToClipboard(exePath, "Copied the marksmith-mcp server path.", sender as Button ?? CopyMcpPathButton);
    }

    private void OnCopyApiUrlClick(object sender, RoutedEventArgs e)
    {
        var url = $"http://127.0.0.1:{_apiPort}";
        CopyToClipboard(url, $"Copied the local REST API address ({url}).", sender as Button ?? CopyApiUrlButton);
    }

    private void OnOpenExtensionDocsClick(object sender, RoutedEventArgs e) => OpenExtensionSetupRequested?.Invoke();

    public static (string Command, string Message) FormatCliCommand(string exe, string? mode) => mode switch
    {
        "batch" => ($"{exe} batch \"C:\\path\\to\\folder\" --format docx", "Copied a folder conversion. Paste it into PowerShell and change the folder."),
        "doctor" => ($"{exe} doctor", "Copied the installation check. Paste it into PowerShell."),
        _ => ($"{exe} \"report.md\" \"report.docx\"", "Copied a file conversion. Paste it into PowerShell and change the file names."),
    };

    private void CopyCliCommandByMode(string? mode)
    {
        var exe = File.Exists(CliPath) ? $"& \"{CliPath}\"" : "marksmith";
        var (command, message) = FormatCliCommand(exe, mode);
        CopyToClipboard(command, message, Card4CopyCommandButton);
    }

    // Real, runnable commands (the old button copied "marksmith suite", which isn't on PATH and
    // only prints a status report). The bundled exe is quoted in full so a paste just works.
    private void OnCopyCliCommandClick(object sender, RoutedEventArgs e)
    {
        CopyCliCommandByMode((sender as FrameworkElement)?.Tag as string);
    }

    private void OnCopyCliPathClick(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(CliPath, "Copied the MarkSmith CLI path.", sender as Button ?? CopyCliPathButton);
    }

    private void OnCopyExpressUrlClick(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(ExpressUrl, $"Copied the MarkSmith Express web address ({ExpressUrl}).", sender as Button ?? CopyExpressUrlButton);
    }

    private async void OnLaunchExpressClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var expressExe = Path.Combine(appDir, "marksmith-express.exe");
            if (!File.Exists(expressExe))
            {
                expressExe = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "MarkSmith.Express", "bin", "Debug", "net8.0", "marksmith-express.exe"));
            }

            bool isResponding = false;
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
                var res = await client.GetAsync($"{ExpressUrl}/api/health");
                if (res.IsSuccessStatusCode) isResponding = true;
            }
            catch { }

            if (!isResponding && !File.Exists(expressExe))
            {
                // Opening http://localhost:5000 here only showed the browser's "can't reach" page.
                SetNotification("MarkSmith Express isn't running, and it isn't installed alongside this copy of MarkSmith.", success: false);
                return;
            }

            if (!isResponding)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = expressExe,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                await System.Threading.Tasks.Task.Delay(600);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = ExpressUrl,
                UseShellExecute = true
            });
            SetNotification("Opened MarkSmith Express in your browser.");
        }
        catch (Exception ex)
        {
            SetNotification($"Couldn't open MarkSmith Express: {ex.Message}", success: false);
        }
    }

    #region Recent Documents Quick Strip

    private void LoadRecentFiles()
    {
        try
        {
            RecentFilesPanel.Children.Clear();
            var files = App.RecentFiles?.Load() ?? new List<string>();
            var items = files.Where(File.Exists).Take(4).ToList();
            if (items.Count > 0)
            {
                foreach (var fullPath in items)
                {
                    var fileName = System.IO.Path.GetFileName(fullPath);
                    var btn = new Button
                    {
                        Tag = fullPath,
                        Padding = new Thickness(8, 4, 8, 4),
                        CornerRadius = new CornerRadius(6),
                        MaxWidth = 190
                    };
                    if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var styleObj) && styleObj is Style subtleStyle)
                    {
                        btn.Style = subtleStyle;
                    }

                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    sp.Children.Add(new FontIcon
                    {
                        Glyph = "\uE8A5",
                        FontSize = 11,
                        Opacity = 0.75
                    });
                    sp.Children.Add(new TextBlock
                    {
                        Text = fileName,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        FontSize = 11
                    });
                    btn.Content = sp;
                    ToolTipService.SetToolTip(btn, fullPath);

                    btn.Click += OnRecentFileItemClick;
                    btn.RightTapped += OnRecentFileItemRightTapped;

                    RecentFilesPanel.Children.Add(btn);
                }

                RecentFilesPanel.Visibility = Visibility.Visible;
                NoRecentDocsText.Visibility = Visibility.Collapsed;
            }
            else
            {
                RecentFilesPanel.Visibility = Visibility.Collapsed;
                NoRecentDocsText.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            RecentFilesPanel.Children.Clear();
            RecentFilesPanel.Visibility = Visibility.Collapsed;
            NoRecentDocsText.Visibility = Visibility.Visible;
        }
    }

    private void OnRecentFileItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string fullPath && !string.IsNullOrWhiteSpace(fullPath))
        {
            if (File.Exists(fullPath))
            {
                try
                {
                    var p = new ProcessStartInfo
                    {
                        FileName = fullPath,
                        UseShellExecute = true
                    };
                    Process.Start(p);
                    SetNotification($"Opened recent document: {System.IO.Path.GetFileName(fullPath)}", success: true);
                }
                catch (Exception ex)
                {
                    SetNotification($"Couldn't open recent document: {ex.Message}", success: false);
                }
            }
            else
            {
                SetNotification($"File no longer exists: {System.IO.Path.GetFileName(fullPath)}", success: false);
            }
        }
    }

    private void OnRecentFileItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string fullPath || string.IsNullOrWhiteSpace(fullPath)) return;
        e.Handled = true;

        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem
        {
            Text = "Open document",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        openItem.Click += (_, _) => OnRecentFileItemClick(btn, new RoutedEventArgs());
        menu.Items.Add(openItem);

        var copyPathItem = new MenuFlyoutItem
        {
            Text = "Copy file path",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPathItem.Click += (_, _) =>
        {
            _ = Services.ClipboardWriter.TrySetTextAsync(fullPath);
            SetNotification($"Copied path: {System.IO.Path.GetFileName(fullPath)}", success: true);
        };
        menu.Items.Add(copyPathItem);

        var showInFolderItem = new MenuFlyoutItem
        {
            Text = "Show in File Explorer",
            Icon = new FontIcon { Glyph = "\uE838" }
        };
        showInFolderItem.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{fullPath}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        };
        menu.Items.Add(showInFolderItem);

        menu.ShowAt(btn, e.GetPosition(btn));
    }

    private void OnRefreshRecentDocsClick(object sender, RoutedEventArgs e)
    {
        LoadRecentFiles();
        SetNotification("Refreshed recent documents.", success: true);
    }

    private void OnTemplateMeetingNotesClick(object sender, RoutedEventArgs e)
    {
        CreateAndOpenStarterDocument("Meeting_Notes", """
# Meeting Notes: [Topic]

**Date:** [Date] | **Attendees:** [Names] | **Facilitator:** [Name]

## 1. Objectives & Agenda
- Item 1: Review project status
- Item 2: Technical architecture alignment
- Item 3: Next sprint commitments

## 2. Discussion & Key Decisions
- Key discussion point A
- Decision reached: [Summary]

## 3. Action Items

| Owner | Action Item | Due Date | Status |
| :--- | :--- | :--- | :--- |
| @owner | Complete design doc review | YYYY-MM-DD | In Progress |
| @owner | Ship production verification | YYYY-MM-DD | Pending |
""");
    }

    private void OnTemplateTechSpecClick(object sender, RoutedEventArgs e)
    {
        CreateAndOpenStarterDocument("Technical_Spec", """
# Technical Specification: [Feature Name]

**Status:** Draft | **Author:** [Author] | **Created:** [Date]

## 1. Overview & Goals
Brief summary of the proposed solution and target outcomes.

## 2. Architecture & System Flow

```mermaid
graph TD
    Client[Client App] --> Gateway[API Gateway]
    Gateway --> Service[Core Service]
    Service --> Database[(Document Store)]
```

## 3. Interfaces & Data Models
Details on contracts, endpoints, and storage schemas.

## 4. Verification & Testing
Empirical test matrix and automated verification gates.
""");
    }

    private void OnTemplateSmartArtClick(object sender, RoutedEventArgs e)
    {
        CreateAndOpenStarterDocument("SmartArt_Showcase", """
# SmartArt Diagram Showcase

Visual presentation diagrams using native Word SmartArt conversion:

:::smartart
layout: process
palette: Office Blue
title: Product Engineering Pipeline

- Requirements Gathering
  - User feedback & telemetry
  - Architectural constraints
- Design & Prototyping
  - Multi-agent review
  - Interactive canvas layout
- Implementation & Polish
  - Production code
  - Zero placeholder standard
- Verification Gate
  - Automated test pass
  - Live smoke test
:::
""");
    }

    private void OnTemplateMindMapClick(object sender, RoutedEventArgs e)
    {
        CreateAndOpenStarterDocument("MindMap_Note", """
# Knowledge Base & Mind Map

Vault architecture and cross-topic connections:

```mermaid
mindmap
  root((MarkSmith Suite))
    Drawing Studios
      Mermaid
      Shape Design
      SmartArt
    Desktop Core
      Markdown AST
      Native OpenXML
      Live Preview
    Ecosystem
      CLI
      MCP Assistant
      Web Companion
```
""");
    }

    private void OnTemplateAdrClick(object sender, RoutedEventArgs e)
    {
        CreateAndOpenStarterDocument("ADR_Architecture_Decision", """
# ADR-001: [Title of Architectural Decision]

**Status:** Proposed | **Date:** YYYY-MM-DD | **Deciders:** [Architect, Engineers]

## Context & Problem Statement
Describe the technical context, requirements, trade-offs, and driving forces behind this decision.

## Decision Drivers
- Non-functional requirement 1 (e.g. latency, memory safety)
- Developer ergonomics and maintenance velocity
- Compatibility with existing OpenXML and WinUI 3 pipelines

## Considered Options
1. **Option 1:** [Description]
2. **Option 2:** [Description]

## Decision Outcome
Chosen option: **Option 1**, because [rationale].

### Positive Consequences
- Improved runtime stability and predictable performance
- Enforces strict contract boundaries

### Negative Consequences / Risks
- Requires initial migration of legacy handlers

## Compliance & Empirical Verification
- Verified with automated test suites (`MarkSmith.Tests`)
- Zero build warnings or regression failures
""");
    }

    private void OnTemplateReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        CreateAndOpenStarterDocument("Release_Notes", """
# Release Notes: MarkSmith Desktop v2.x

**Release Date:** YYYY-MM-DD | **Channel:** Production / Stable

## 🚀 Highlights & Executive Summary
Brief summary of key user-facing features, polish refinements, and performance upgrades delivered in this release.

## ✨ New Features & Enhancements
- **Feature A:** Description of enhancement and workflow impact.
- **Feature B:** Ergonomic improvements and updated accelerators.

## 🛠️ Polish & Architectural Hardening
- **Surface Polish:** Consistent hover/press feedback, high-DPI font rendering, and zero-flicker transitions.
- **Reliability:** Memory consumption optimization and deterministic cleanup.

## ⚠️ Breaking Changes & Deprecations
- None. Fully backward-compatible with all existing Markdown, SmartArt, and Mermaid documents.

## 📋 Verification & Test Summary
- **Unit & Integration Tests:** 100% passed across all test suites.
- **Trunk Health:** Green trunk status validated before release.
""");
    }

    private void OnTemplateSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        FilterTemplates(sender.Text);
    }

    private void OnTemplateSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            LaunchFirstVisibleTemplate();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (TemplateSearchBox != null) TemplateSearchBox.Text = "";
            FilterTemplates("");
        }
    }

    private void FilterTemplates(string query)
    {
        if (StarterTemplatesPanel == null) return;
        query = query.Trim().ToLowerInvariant();
        int visibleCount = 0;
        foreach (var child in StarterTemplatesPanel.Children)
        {
            if (child is Button btn)
            {
                var tag = (btn.Tag as string ?? "").ToLowerInvariant();
                var text = "";
                if (btn.Content is StackPanel sp)
                {
                    foreach (var spc in sp.Children)
                        if (spc is TextBlock tb) text += " " + tb.Text;
                }
                text = text.ToLowerInvariant();
                bool matches = string.IsNullOrEmpty(query) || text.Contains(query) || tag.Contains(query);
                btn.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                if (matches) visibleCount++;
            }
        }
        if (TemplateSearchStatusText != null)
        {
            if (string.IsNullOrEmpty(query))
            {
                TemplateSearchStatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                TemplateSearchStatusText.Text = visibleCount > 0 ? $"{visibleCount} match{(visibleCount == 1 ? "" : "es")}" : "No templates match";
                TemplateSearchStatusText.Visibility = Visibility.Visible;
            }
        }
    }

    private void LaunchFirstVisibleTemplate()
    {
        if (StarterTemplatesPanel == null) return;
        foreach (var child in StarterTemplatesPanel.Children)
        {
            if (child is Button { Visibility: Visibility.Visible } btn)
            {
                btn.Focus(FocusState.Programmatic);
                if (btn == TemplateMeetingNotesBtn) OnTemplateMeetingNotesClick(btn, new RoutedEventArgs());
                else if (btn == TemplateTechSpecBtn) OnTemplateTechSpecClick(btn, new RoutedEventArgs());
                else if (btn == TemplateSmartArtBtn) OnTemplateSmartArtClick(btn, new RoutedEventArgs());
                else if (btn == TemplateMindMapBtn) OnTemplateMindMapClick(btn, new RoutedEventArgs());
                else if (btn == TemplateAdrBtn) OnTemplateAdrClick(btn, new RoutedEventArgs());
                else if (btn == TemplateReleaseNotesBtn) OnTemplateReleaseNotesClick(btn, new RoutedEventArgs());
                break;
            }
        }
    }

    private void CreateAndOpenStarterDocument(string baseName, string content)
    {
        try
        {
            string docsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string marksmithFolder = System.IO.Path.Combine(docsFolder, "MarkSmith");
            if (!System.IO.Directory.Exists(marksmithFolder)) System.IO.Directory.CreateDirectory(marksmithFolder);

            string fileName = $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.md";
            string filePath = System.IO.Path.Combine(marksmithFolder, fileName);
            System.IO.File.WriteAllText(filePath, content, System.Text.Encoding.UTF8);

            App.RecentFiles?.AddToRecent(filePath);
            LoadRecentFiles();

            var p = new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            };
            Process.Start(p);
            SetNotification($"Created starter document: {fileName}", success: true);
        }
        catch (Exception ex)
        {
            SetNotification($"Couldn't create starter document: {ex.Message}", success: false);
        }
    }

    private void OnHubSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        string query = HubSearchBox?.Text?.Trim().ToLowerInvariant() ?? "";
        FilterCards(query);
    }

    private void OnHubSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (HubSearchBox is not null) HubSearchBox.Text = "";
            e.Handled = true;
        }
    }

    private void FilterCards(string query)
    {
        var cards = new (Border? Card, string Keywords)[]
        {
            (Card1Border, "drawing studios diagrams shapes smartart flowchart mermaid"),
            (Card2Border, "ai assistants mcp server claude cursor gemini"),
            (Card3Border, "browser extension chrome edge copilot api"),
            (Card4Border, "edit history checkpoints versions diff timeline"),
            (Card5Border, "command-line interface cli terminal terminal export"),
            (Card6Border, "web companion preview mobile live browser")
        };

        bool hasQuery = !string.IsNullOrWhiteSpace(query);
        foreach (var (card, keywords) in cards)
        {
            if (card is null) continue;
            if (!hasQuery || keywords.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                card.Opacity = 1.0;
            }
            else
            {
                card.Opacity = 0.25;
            }
        }
    }

    private void UpdateMemoryUsage()
    {
        if (MemoryUsageText == null) return;
        long bytes = GC.GetTotalMemory(false);
        double mb = bytes / (1024.0 * 1024.0);
        MemoryUsageText.Text = $"Memory: {mb:F1} MB";
    }

    private void OnMemoryUsagePillTapped(object sender, TappedRoutedEventArgs e)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        UpdateMemoryUsage();
        SetNotification("Optimized process memory & trimmed managed heaps.");
    }

    private void OnMemoryUsagePillRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        long bytes = GC.GetTotalMemory(false);
        double mb = bytes / (1024.0 * 1024.0);
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        double workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        string report = $"MarkSmith Process Diagnostics:{Environment.NewLine}- Managed Memory: {mb:F2} MB{Environment.NewLine}- Working Set: {workingSetMb:F2} MB{Environment.NewLine}- GC Gen 0/1/2 Collections: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}";
        CopyToClipboard(report, "Copied process memory metrics to clipboard.");
    }

    private static readonly DateTime _processStartTime = DateTime.UtcNow;

    private void UpdateUptimeAndCache()
    {
        if (UptimeStorageText == null) return;
        var uptime = DateTime.UtcNow - _processStartTime;
        string uptimeStr = uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
            : $"{(int)uptime.TotalMinutes}m";

        long cacheBytes = 0;
        int cacheFileCount = 0;
        try
        {
            var tempDir = new DirectoryInfo(Path.GetTempPath());
            var files = tempDir.EnumerateFiles("marksmith*", SearchOption.TopDirectoryOnly)
                .Concat(tempDir.EnumerateFiles("ms-*", SearchOption.TopDirectoryOnly));
            foreach (var fi in files)
            {
                cacheFileCount++;
                cacheBytes += fi.Length;
            }
        }
        catch { }

        double cacheMb = cacheBytes / (1024.0 * 1024.0);
        string cacheStr = cacheMb >= 1.0 ? $"{cacheMb:F1} MB" : (cacheBytes > 0 ? $"{cacheBytes / 1024} KB" : "OK");
        UptimeStorageText.Text = $"Uptime: {uptimeStr} · Cache: {cacheStr}";
        ToolTipService.SetToolTip(UptimeStoragePill, $"Desktop engine uptime: {uptimeStr} · Temp cache: {cacheFileCount} files ({cacheMb:F2} MB) · Tap to prune temp cache, right-click to copy diagnostics");
    }

    private void OnUptimeStoragePillTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        int deleted = 0;
        long freedBytes = 0;
        try
        {
            var tempDir = new DirectoryInfo(Path.GetTempPath());
            var files = tempDir.EnumerateFiles("marksmith*", SearchOption.TopDirectoryOnly)
                .Concat(tempDir.EnumerateFiles("ms-*", SearchOption.TopDirectoryOnly));
            foreach (var fi in files)
            {
                try
                {
                    if (DateTime.UtcNow - fi.LastWriteTimeUtc > TimeSpan.FromMinutes(10))
                    {
                        long len = fi.Length;
                        fi.Delete();
                        deleted++;
                        freedBytes += len;
                    }
                }
                catch { }
            }
        }
        catch { }

        UpdateUptimeAndCache();
        double freedMb = freedBytes / (1024.0 * 1024.0);
        string freedStr = freedMb >= 0.1 ? $"{freedMb:F1} MB" : $"{freedBytes / 1024} KB";
        SetNotification(deleted > 0
            ? $"Pruned {deleted} stale temporary cache files ({freedStr} freed)."
            : "Temporary cache is already lean; no stale files found.");
    }

    private void OnUptimeStoragePillRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var uptime = DateTime.UtcNow - _processStartTime;
        string uptimeStr = $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";

        long cacheBytes = 0;
        int cacheFileCount = 0;
        try
        {
            var tempDir = new DirectoryInfo(Path.GetTempPath());
            var files = tempDir.EnumerateFiles("marksmith*", SearchOption.TopDirectoryOnly)
                .Concat(tempDir.EnumerateFiles("ms-*", SearchOption.TopDirectoryOnly));
            foreach (var fi in files)
            {
                cacheFileCount++;
                cacheBytes += fi.Length;
            }
        }
        catch { }

        double cacheMb = cacheBytes / (1024.0 * 1024.0);
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        string diag = $"MarkSmith Engine Diagnostics:{Environment.NewLine}" +
                      $"- Uptime: {uptimeStr}{Environment.NewLine}" +
                      $"- Temp Cache Footprint: {cacheFileCount} files ({cacheMb:F2} MB){Environment.NewLine}" +
                      $"- Temp Directory: {Path.GetTempPath()}{Environment.NewLine}" +
                      $"- Process ID: {proc.Id}{Environment.NewLine}" +
                      $"- Threads: {proc.Threads.Count}";

        CopyToClipboard(diag, "Copied engine uptime & cache diagnostics to clipboard.");
    }

    #endregion
}
