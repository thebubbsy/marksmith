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

using MarkSmith.Models;

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

        // "Right-click for options" menus: open them from Shift+F10 / the Menu key too.

        KeyboardContextMenu.BridgeAll(

            (OpenConfigFolderButton, OnOpenConfigFolderRightTapped),

            (RefreshRecentDocsButton, OnRefreshRecentDocsButtonRightTapped),

            (RecentDocsCountPill, OnRecentDocsCountPillRightTapped),

            (TemplateCountPill, OnTemplateCountPillRightTapped),

            (Card1DiagramButton, OnCard1ButtonRightTapped),

            (Card1ShapeButton, OnCard1ButtonRightTapped),

            (Card1SmartArtButton, OnCard1ButtonRightTapped),

            (Card2CopySetupButton, OnCard2ButtonRightTapped),

            (CopyMcpPathButton, OnCard2ButtonRightTapped),

            (TurnOnApiButton, OnCard3ButtonRightTapped),

            (CopyApiUrlButton, OnCard3ButtonRightTapped),

            (GetExtensionButton, OnCard3ButtonRightTapped),

            (Card4CopyCommandButton, OnCard4ButtonRightTapped),

            (CopyCliPathButton, OnCard4ButtonRightTapped),

            (Card5ExpressButton, OnCard5ButtonRightTapped),

            (CopyExpressUrlButton, OnCard5ButtonRightTapped),

            (Card6GalaxyButton, OnCard6ButtonRightTapped),
            (VersionBadge, OnVersionInfoRightTapped),
            (LicenseBadge, OnVersionInfoRightTapped),
            (HealthStatusBadge, OnHealthStatusRightTapped),
            (UptimeStoragePill, OnUptimeStoragePillRightTapped),
            (MemoryUsagePill, OnMemoryUsagePillRightTapped));

        // Only three of the ten starter templates fit across the strip; let the mouse wheel

        // scroll it sideways instead of leaving the rest reachable only by the thin scrollbar.

        StarterTemplatesScroller.AddHandler(UIElement.PointerWheelChangedEvent,

            new PointerEventHandler(OnStarterTemplatesWheel), handledEventsToo: true);

        // The AutoSuggestBox's inner TextBox marks Esc (and Enter) handled before KeyDown reaches

        // these handlers, so the advertised "Esc clears" never fired. Listen to handled keys too.

        RecentSearchBox.AddHandler(UIElement.KeyDownEvent,

            new KeyEventHandler(OnRecentSearchBoxKeyDown), handledEventsToo: true);

        TemplateSearchBox.AddHandler(UIElement.KeyDownEvent,

            new KeyEventHandler(OnTemplateSearchKeyDown), handledEventsToo: true);

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

        // Resting the pointer on a result line (or tabbing to its close button) keeps it open.

        NoticeHold.Attach(Notification, _notificationTimer, () => Notification.IsOpen && Notification.Severity == InfoBarSeverity.Success);

        Unloaded += (_, _) => _notificationTimer.Stop();

        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);

        AddHandler(KeyDownEvent, new KeyEventHandler(OnHubKeyDown), true);

        HoverPolish.Track(this);

        Loaded += (_, _) => { LoadRecentFiles(); UpdateMemoryUsage(); UpdateUptimeAndCache(); FilterTemplates(""); };

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

        // This handler sees handled keys too, so keys pressed inside an open card or chip menu reached it:

        // 1-6 launched a studio behind the menu, F1 opened the shortcuts flyout and Shift+F10 a second menu.

        if (PopupKeys.FocusIsOutside(this)) return;



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

            // A focused card button with its own menu already opened it.

            if (KeyboardContextMenu.IsFromBridgedControl(e)) return;

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

                        OnVersionInfoClick(this, new RoutedEventArgs());

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

            AutomationProperties.SetName(VersionBadge, $"Copy version details ({VersionText.Text})");
            AutomationProperties.SetName(LicenseBadge, $"Copy plan details ({LicenseText.Text})");

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

        // Long results ("Couldn't reach the API: …") get time to be read before they close.

        _notificationTimer.Interval = NoticeTiming.For(Notification.Message, minimumSeconds: 6);

        if (success && !NoticeHold.IsHeld(Notification)) _notificationTimer.Start();

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

            ShowCardContextMenu(0, fe, KeyboardContextMenu.PointIn(e, fe));

    }



    private void OnCard1ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)

    {

        e.Handled = true;

        if (sender is FrameworkElement fe)

            ShowCardContextMenu(1, fe, KeyboardContextMenu.PointIn(e, fe));

    }



    private void OnCard2ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)

    {

        e.Handled = true;

        if (sender is FrameworkElement fe)

            ShowCardContextMenu(2, fe, KeyboardContextMenu.PointIn(e, fe));

    }



    private void OnCard3ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)

    {

        e.Handled = true;

        if (sender is FrameworkElement fe)

            ShowCardContextMenu(3, fe, KeyboardContextMenu.PointIn(e, fe));

    }



    private void OnCard4ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)

    {

        e.Handled = true;

        if (sender is FrameworkElement fe)

            ShowCardContextMenu(4, fe, KeyboardContextMenu.PointIn(e, fe));

    }



    private void OnCard5ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)

    {

        e.Handled = true;

        if (sender is FrameworkElement fe)

            ShowCardContextMenu(5, fe, KeyboardContextMenu.PointIn(e, fe));

    }



    private void OnCard6ButtonRightTapped(object sender, RightTappedRoutedEventArgs e)

    {

        e.Handled = true;

        if (sender is FrameworkElement fe)

            ShowCardContextMenu(6, fe, KeyboardContextMenu.PointIn(e, fe));

    }



    private void OnVersionInfoClick(object sender, RoutedEventArgs e)

    {

        var info = $"{VersionText.Text} · {LicenseText.Text}";

        CopyToClipboard(info, $"Copied version info ({info}).");

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

            menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));

        else

            menu.ShowAt(this);

    }



    private void OnHealthStatusClick(object sender, RoutedEventArgs e)

    {

        // Only what is known: nothing here checks the studios, so the copy no longer claims they are ready.
        var status = $"MarkSmith desktop · {VersionText?.Text} · {LicenseText?.Text} · .NET {Environment.Version}";

        CopyToClipboard(status, "Copied suite health status.");

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

            var diag = $"MarkSmith desktop{Environment.NewLine}{VersionText?.Text}{Environment.NewLine}License: {LicenseText?.Text}{Environment.NewLine}Platform: WinUI 3 desktop, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}{Environment.NewLine}.NET runtime: {Environment.Version}";

            CopyToClipboard(diag, "Copied system diagnostics.");

        };

        menu.Items.Add(copyStatusItem);

        if (HealthStatusBadge != null)

            menu.ShowAt(HealthStatusBadge, KeyboardContextMenu.PointIn(e, HealthStatusBadge));

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



    private string _recentDocsFilter = "";



    private void LoadRecentFiles(string? filter = null)

    {

        if (filter != null) _recentDocsFilter = filter;

        try

        {

            RecentFilesPanel.Children.Clear();

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var existingFiles = files.Where(File.Exists).ToList();

            var filtered = RecentFilesService.FilterRecentFiles(existingFiles, _recentDocsFilter).ToList();



            if (RecentDocsCountText != null)

            {

                RecentDocsCountText.Text = RecentFilesService.FormatRecentFilesCount(filtered.Count, existingFiles.Count, _recentDocsFilter);

            }



            var items = filtered.Take(4).ToList();

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
                    // Without the bridge, Shift+F10 here fell through to the hub-wide card menu.
                    KeyboardContextMenu.Bridge(btn, OnRecentFileItemRightTapped);



                    RecentFilesPanel.Children.Add(btn);

                }



                RecentFilesPanel.Visibility = Visibility.Visible;

                NoRecentDocsText.Visibility = Visibility.Collapsed;

            }

            else

            {

                RecentFilesPanel.Visibility = Visibility.Collapsed;

                NoRecentDocsText.Text = RecentFilesService.FormatRecentFilesEmptyState(existingFiles.Count, _recentDocsFilter);

                NoRecentDocsText.Visibility = Visibility.Visible;

            }

            SetRecentFilterChromeVisible(existingFiles.Count > 0);

        }

        catch

        {

            RecentFilesPanel.Children.Clear();

            RecentFilesPanel.Visibility = Visibility.Collapsed;

            NoRecentDocsText.Text = RecentFilesService.FormatRecentFilesEmptyState(0, null);

            NoRecentDocsText.Visibility = Visibility.Visible;

            if (RecentDocsCountText != null) RecentDocsCountText.Text = "0 docs";

            SetRecentFilterChromeVisible(false);

        }

    }



    // With no history at all, the filter box and the "0 docs" pill are just noise next to the

    // empty line. Hide them, moving focus on first if the filter box had it.

    private void SetRecentFilterChromeVisible(bool visible)

    {

        var target = visible ? Visibility.Visible : Visibility.Collapsed;

        if (RecentSearchBox.Visibility == target) return;

        if (!visible && XamlRoot is { } root &&

            Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is DependencyObject focused)

        {

            for (var d = focused; d is not null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))

            {

                // The count pill is a focusable button now too, and it collapses with the filter box.

                if (ReferenceEquals(d, RecentSearchBox) || ReferenceEquals(d, RecentDocsCountPill)) { RecentDocsActionBtn.Focus(FocusState.Programmatic); break; }

            }

        }

        RecentSearchBox.Visibility = target;

        RecentDocsCountPill.Visibility = target;

    }



    private async void OnRecentDocsCountPillClick(object sender, RoutedEventArgs e)

    {

        var files = App.RecentFiles?.Load() ?? new List<string>();

        var existing = files.Where(File.Exists).ToList();

        if (existing.Count == 0)
        {
            // Clicking the pill did nothing once every recent document had been moved or deleted.
            SetNotification(files.Count == 0
                ? "No recent documents to copy yet."
                : "Nothing to copy: the recent documents were moved or deleted.", success: false);
            return;
        }

        var summary = string.Join(Environment.NewLine, existing);

        // ClipboardWriter.Set threw on a busy clipboard and the click did nothing; say why instead.

        var error = await ClipboardWriter.TrySetTextAsync(summary);

        if (error is not null)

        {

            SetNotification($"Couldn't copy: {error}", success: false);

            return;

        }

        SetNotification($"Copied {CountText.Of(existing.Count, "recent document path")} to clipboard.", success: true);

        if (RecentDocsCountText is null) return;

        // A brief tick on the pill, then back to the count: it used to keep saying "3 paths" until the next refresh.

        var before = RecentDocsCountText.Text;

        var copied = $"\u2713 {CountText.Of(existing.Count, "path")}";

        RecentDocsCountText.Text = copied;

        await System.Threading.Tasks.Task.Delay(1600);

        if (RecentDocsCountText.Text == copied) RecentDocsCountText.Text = before;

    }



    private void OnRecentDocsCountPillRightTapped(object sender, RightTappedRoutedEventArgs e)

    {

        e.Handled = true;

        if (RecentDocsFlyout != null && RecentDocsCountPill != null)

        {

            RecentDocsFlyout.ShowAt(RecentDocsCountPill, KeyboardContextMenu.PointIn(e, RecentDocsCountPill));

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

        copyPathItem.Click += async (_, _) =>

        {

            var error = await Services.ClipboardWriter.TrySetTextAsync(fullPath);

            SetNotification(error is null ? $"Copied path: {System.IO.Path.GetFileName(fullPath)}" : $"Couldn't copy: {error}", success: error is null);

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



        menu.ShowAt(btn, KeyboardContextMenu.PointIn(e, btn));

    }



    private void OnRefreshRecentDocsClick(object sender, RoutedEventArgs e)

    {

        LoadRecentFiles();

        SetNotification("Refreshed recent documents.", success: true);

    }



    private void OnRecentDocsActionBtnClick(object sender, RoutedEventArgs e)

    {

        if (RecentDocsFlyout is not null && sender is FrameworkElement fe)

        {

            RecentDocsFlyout.ShowAt(fe);

        }

    }



    private void OnStarterTemplatesViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateStarterScrollChevrons();

    private void OnStarterTemplatesSizeChanged(object sender, SizeChangedEventArgs e) => UpdateStarterScrollChevrons();

    // Chevrons appear only on the side that has more chips; the template filter shrinks the strip,
    // so FilterTemplates refreshes them too.
    private void UpdateStarterScrollChevrons()
    {
        var sv = StarterTemplatesScroller;
        var canLeft = sv.ScrollableWidth > 0.5 && sv.HorizontalOffset > 0.5;
        var canRight = sv.ScrollableWidth > 0.5 && sv.HorizontalOffset < sv.ScrollableWidth - 0.5;
        StarterScrollLeftButton.Opacity = canLeft ? 1 : 0;
        StarterScrollLeftButton.IsHitTestVisible = canLeft;
        StarterScrollRightButton.Opacity = canRight ? 1 : 0;
        StarterScrollRightButton.IsHitTestVisible = canRight;
    }

    private void OnStarterScrollLeftClick(object sender, RoutedEventArgs e) => PageStarterTemplates(-1);

    private void OnStarterScrollRightClick(object sender, RoutedEventArgs e) => PageStarterTemplates(1);

    // Pages by most of a viewport so the last visible chip stays on screen as a landmark.
    private void PageStarterTemplates(int direction)
    {
        var sv = StarterTemplatesScroller;
        if (sv.ScrollableWidth <= 0) return;
        var step = Math.Max(80, sv.ViewportWidth * 0.8);
        sv.ChangeView(Math.Clamp(sv.HorizontalOffset + direction * step, 0, sv.ScrollableWidth), null, null);
    }

    private void OnStarterTemplatesWheel(object sender, PointerRoutedEventArgs e)

    {

        var sv = StarterTemplatesScroller;

        if (sv.ScrollableWidth <= 0) return;

        var delta = e.GetCurrentPoint(sv).Properties.MouseWheelDelta;

        var target = Math.Clamp(sv.HorizontalOffset - delta, 0, sv.ScrollableWidth);

        // At either end the wheel goes back to scrolling the hub itself.

        if (Math.Abs(target - sv.HorizontalOffset) < 0.5) return;

        sv.ChangeView(target, null, null);

        e.Handled = true;

    }



    private void OnRecentSearchBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)

    {

        LoadRecentFiles(sender.Text);

    }



    // Esc clears a non-empty filter (the empty-state line promises it); with nothing typed it is

    // left alone so the dialog's own Esc still closes the hub.

    private void OnRecentSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)

    {

        if (e.Key != Windows.System.VirtualKey.Escape || string.IsNullOrEmpty(RecentSearchBox.Text)) return;

        RecentSearchBox.Text = "";

        e.Handled = true;

    }



    private void OnFilterMarkdownRecentClick(object sender, RoutedEventArgs e)

    {

        RecentSearchBox.Text = ".md";

        LoadRecentFiles(".md");

        SetNotification("Filtered recent documents for .md files.", success: true);

    }



    private async void OnCopyAllRecentPathsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var existingFiles = files.Where(File.Exists);

            var list = RecentFilesService.FilterRecentFiles(existingFiles, _recentDocsFilter).ToList();

            if (list.Count > 0)

            {

                var text = string.Join(Environment.NewLine, list);

                var error = await Services.ClipboardWriter.TrySetTextAsync(text);

                SetNotification(error is null ? $"Copied {CountText.Of(list.Count, "recent document path")} to clipboard." : $"Couldn't copy the recent document paths. {error}", success: error is null);

            }

            else

            {

                SetNotification("No recent document paths to copy.", success: false);

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy paths: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentFileNamesOnlyClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var existingFiles = files.Where(File.Exists);

            var list = RecentFilesService.FilterRecentFiles(existingFiles, _recentDocsFilter).ToList();

            var names = RecentFilesService.ExtractFileNames(list);

            if (names.Count > 0)

            {

                var text = string.Join(Environment.NewLine, names);

                var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(text);

                var copyMessage = $"Copied {CountText.Of(names.Count, "recent file name")} to clipboard.";
                SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            }

            else

            {

                SetNotification("No recent file names to copy.", success: false);

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy file names: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentParentFoldersClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var existingFiles = files.Where(File.Exists);

            var list = RecentFilesService.FilterRecentFiles(existingFiles, _recentDocsFilter).ToList();

            var folders = RecentFilesService.ExtractParentDirectories(list);

            if (folders.Count > 0)

            {

                var text = string.Join(Environment.NewLine, folders);

                var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(text);

                var copyMessage = $"Copied {CountText.Of(folders.Count, "unique parent folder")} to clipboard.";
                SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            }

            else

            {

                SetNotification("No recent folders to copy.", success: false);

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy folders: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentAsMarkdownListClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var existingFiles = files.Where(File.Exists);

            var list = RecentFilesService.FilterRecentFiles(existingFiles, _recentDocsFilter).ToList();

            if (list.Count > 0)

            {

                var mdText = RecentFilesService.FormatAsMarkdownList(list);

                var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(mdText);

                var copyMessage = $"Copied {CountText.Of(list.Count, "recent document link")} as Markdown.";
                SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            }

            else

            {

                SetNotification("No recent documents to copy.", success: false);

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy Markdown links: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentLibrarySummaryClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatRecentLibrarySummary(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy library summary: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentExtensionBreakdownClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatRecentExtensionBreakdown(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy file type breakdown: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentDriveDistributionClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatRecentDriveDistribution(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy drive breakdown: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentAgeDistributionClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatRecentAgeDistribution(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy age breakdown: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentPathLengthClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatRecentPathLengthDistribution(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy path lengths: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentSizeDistributionClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatRecentSizeDistribution(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy file sizes: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentDirectorySpreadClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatRecentDirectorySpread(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy directory spread: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentNamingPatternsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatRecentFileNamingPatterns(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy naming patterns: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentMissingAuditClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatMissingRecentFilesAudit(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy missing files audit: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentSortedCatalogClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatRecentFilesSortedCatalog(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy sorted recent catalog: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentWorkspaceClustersClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatProjectWorkspaceClusters(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy project workspace clusters: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentDuplicatesAuditClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatDuplicateAndVersionedFilesAudit(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy duplicate files audit: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentHealthScorecardClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatLibraryHealthScorecard(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy library health scorecard: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentStalenessScorecardClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatStalenessTiersSummary(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy library staleness tiers: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentVolumeDistributionClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var summary = RecentFilesService.FormatVolumeDistributionSummary(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy library volume distribution: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentAccessHeatmapClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var heatmap = RecentFilesService.AuditAccessFrequencyHeatmap(files);

            var summary = RecentFilesService.FormatAccessFrequencyHeatmapSummary(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy access frequency heatmap: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentDormancyAlertsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditDormancyAlerts(files);

            var summary = audit.Summary;

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy library dormancy alerts: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentClusterDormancyClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceClusterDormancy(files);

            var summary = audit.Summary;

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy workspace cluster dormancy audit: {ex.Message}", success: false);

        }

    }



    private async void OnCopyRecentClusterVelocityClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditClusterActivityVelocity(files);

            var summary = audit.Summary;

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

            var copyMessage = $"Copied: {summary}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't copy workspace cluster activity velocity: {ex.Message}", success: false);

        }

    }



    private async void OnFilterByActiveClusterClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var chips = RecentFilesService.GetActiveClusterFilterChips(files);

            if (chips.Count > 0)

            {

                var top = chips[0];

                RecentSearchBox.Text = top.ClusterName;

                LoadRecentFiles(top.ClusterName);

                string summary = RecentFilesService.FormatActiveClusterFilterSummary(chips);

                var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(summary);

                var copyMessage = $"Filtered by top active cluster: '{top.ClusterName}' ({top.ActiveFiles} active documents)";
                SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

                if (RecentDocsActionBtn != null)

                {

                    Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Filtered");

                }

            }

            else

            {

                SetNotification("No active workspace clusters found in recent documents.", success: false);

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't filter by active cluster: {ex.Message}", success: false);

        }

    }



    private async void OnCopyFormatClusterMatrixClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditFormatClusterMatrix(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied format matrix: {CountText.Of(audit.TotalFormats, "format")} (Diversity: {audit.DiversityIndex:0.##})";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit format matrix: {ex.Message}", success: false);

        }

    }



    private async void OnCopyCompanionFormatCoverageClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditCompanionFormatCoverage(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied companion coverage: {audit.CoveragePercentage:0.#}% ({audit.FullyCoveredFamilies}/{audit.TotalFamilies} families)";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit companion coverage: {ex.Message}", success: false);

        }

    }



    private async void OnCopyDocumentStalenessClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditDocumentStalenessAndVelocity(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied document velocity: {audit.FreshnessIndex:0.#}% fresh ({audit.ActiveFiles} active, {audit.WarmFiles} warm)";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit document staleness: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceStorageFootprintClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceStorageFootprint(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied storage footprint: {audit.FormattedTotalSize} across {CountText.Of(audit.ExistingFiles, "file")} (avg {audit.FormattedAverageSize})";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit storage footprint: {ex.Message}", success: false);

        }

    }



    private async void OnCopyDocumentNamingConventionsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditDocumentNamingConventions(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied naming conventions audit: {audit.DominantConvention} ({audit.ConsistencyScore:0.#}%) across {CountText.Of(audit.TotalFiles, "file")}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit naming conventions: {ex.Message}", success: false);

        }

    }



    private async void OnCopyPathLengthAuditClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditPathLengthsAndNestingDepth(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied path length audit: {audit.SafetyCategory} · Max: {audit.MaxPathLength} chars across {CountText.Of(audit.TotalFiles, "file")}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit path lengths: {ex.Message}", success: false);

        }

    }



    private async void OnCopyExtensionDiversityClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditFileExtensionDiversity(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied extension diversity audit: {audit.DiversityCategory} · {CountText.Of(audit.UniqueExtensions, "format")} across {CountText.Of(audit.TotalFiles, "file")}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit extension diversity: {ex.Message}", success: false);

        }

    }



    private async void OnCopyDeadLinksAuditClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditDeadLinksAndOrphanAssets(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied link & asset audit: {audit.IntegrityCategory} · {audit.ValidLinkCount}/{audit.TotalRelativeLinks} valid ({audit.IntegrityScore:0.#}%)";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit links & assets: {ex.Message}", success: false);

        }

    }



    private async void OnCopyAssetCompressionAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditAssetCompressionAndFormatAdvisor(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied asset compression advisor: {audit.EfficiencyCategory} · {audit.AssetEfficiencyScore:0.#}% efficiency";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit asset compression: {ex.Message}", success: false);

        }

    }



    private async void OnCopyAssetDpiSvgAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditAssetDpiAndSvgOptimizationAdvisor(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied High-DPI & SVG advisor: {audit.ReadinessCategory} · {audit.OverallHighDpiReadinessScore:0.#}% ready";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit High-DPI & SVG assets: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceTypographyAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceTypographyAndFontEmbedding(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Typography advisor: {audit.TypographicHealthCategory} · {audit.TypographicSelfContainmentScore:0.#}% self-contained";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace typography: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceFileSizeDistributionAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceFileSizeDistribution(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Storage Distribution advisor: {audit.StorageHealthCategory} · {audit.StorageCompactnessScore:0.#}% compactness";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace file size distribution: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceAssetDeduplicationAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceAssetDeduplication(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Deduplication advisor: {audit.DeduplicationCategory} · {audit.DeduplicationEfficiencyScore:0.#}% efficiency";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace asset deduplication: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceHeadingAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceMarkdownHeaderUniformity(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Heading advisor: {audit.Category} · {audit.HeadingUniformityScore:0.#}% score";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace heading uniformity: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceCodeFenceAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceCodeFenceLanguageTags(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Code Fence advisor: {audit.Category} · {audit.CoverageScore:0.#}% score";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace code fence languages: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceMediaLinksAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceMediaLinkIntegrity(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Media Link advisor: {audit.Category} · {audit.IntegrityScore:0.#}% score";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace media link integrity: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceCrossDocAnchorsAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceCrossDocumentAnchors(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Cross-Doc Anchor advisor: {audit.Category} · {audit.CrossDocAnchorIntegrityScore:0.#}% score";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace cross-document anchors: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceTableFormattingAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceTableFormatting(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Table Formatting advisor: {audit.Category} · {audit.TabularIntegrityScore:0.#}% score";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace table formatting: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceDocumentGraphAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceDocumentGraphAndOrphans(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Document Linking Graph advisor: {audit.Category} · {audit.CrossReferenceScore:0.#}% score";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace document graph: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceMediaAssetAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceMediaAssetIntegrity(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Media Asset Integrity advisor: {audit.Category} · {audit.MediaIntegrityScore:0.#}% score";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace media assets: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceMediaAccessibilityAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceMediaAccessibility(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Media Alt-Text Accessibility advisor: {audit.Category} · {audit.AccessibilityComplianceScore:0.#}% score";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace media alt-text: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceCodeFenceLanguageAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceCodeFenceLanguageTags(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Code Fence Language advisor: {audit.Category} · {audit.CoverageScore:0.#}% coverage";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace code fence languages: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceFrontmatterAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceFrontmatterMetadata(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Frontmatter Metadata advisor: {audit.Category} · {audit.CompletenessScore:0.#}% completeness";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace frontmatter metadata: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceHeadingHierarchyAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceHeadingHierarchy(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Heading Hierarchy advisor: {audit.Category} · {audit.ErgonomicsScore:0.#}% score";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace heading hierarchy: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceHyperlinkDomainsAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceHyperlinkDomainsAndAnchors(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Hyperlink Domains advisor: {audit.Category} · {audit.LinkingHealthScore:0.#}% score";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace hyperlinks: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceCodeBlockLanguagesAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceCodeBlockLanguages(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Code Block Languages advisor: {audit.Category} · {audit.SyntaxHighlightingCoverageScore:0.#}% coverage";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace code block languages: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceCodeFenceIndentationAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceCodeFenceIndentation(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Code Fence Indentation advisor: {audit.Category} · {audit.ConsistencyScore:0.#}% consistency";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace code fence indentation: {ex.Message}", success: false);

        }

    }



    private async void OnCopyWorkspaceMarkdownDialectAdvisorClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceMarkdownDialectConsistency(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(audit.Summary);

            var copyMessage = $"Copied Markdown Dialect advisor: {audit.Category} · {audit.DialectAlignmentScore:0.#}% alignment";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Copied");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace markdown dialects: {ex.Message}", success: false);

        }

    }



    private async void OnGenerateWorkspaceDialectConfigFileClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceMarkdownDialectConsistency(files);

            string configJson = audit.RecommendedConfiguration ?? RecentFilesService.GenerateRecommendedDialectConfiguration(audit);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(configJson);

            var copyMessage = $"Generated & copied .marksmith.json config ({audit.DominantFenceType} · {audit.DialectAlignmentScore:0.#}% alignment)";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Config");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't generate .marksmith.json config: {ex.Message}", success: false);

        }

    }



    private async void OnSimulateWorkspaceDialectNormalizationClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var audit = RecentFilesService.AuditWorkspaceMarkdownDialectConsistency(files);

            var result = RecentFilesService.SimulateDialectNormalizationDryRun(audit, files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Dry-run: {CountText.Of(result.TotalReplacementCount, "replacement")} across {CountText.Of(result.FilesRequiringNormalization, "file")} (100% projected alignment)";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Dry-Run");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't simulate dialect normalization: {ex.Message}", success: false);

        }

    }



    private async void OnApplyWorkspaceDialectNormalizationClick(object sender, RoutedEventArgs e)

    {

        try

        {

            if (RecentDocsRewriteIsRunning()) return;

            var files = App.RecentFiles?.Load() ?? new List<string>();

            if (!await ConfirmRecentDocsActionAsync("Normalize Markdown dialect?", $"Rewrites code fences, list markers and heading styles in {DocumentCount(files.Count)} from your recent list to one style. A backup of each changed file is kept.", "Rewrite files")) return;

            if (RecentDocsRewriteIsRunning()) return;
            _recentDocsRewriteRunning = true;
            SetNotification($"Rewriting {DocumentCount(files.Count)}…");
            try
            {
                var result = await Task.Run(() => RecentFilesService.ApplyWorkspaceDialectNormalization(files));

                var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

                var copyMessage = $"Applied: {CountText.Of(result.TotalReplacementsApplied, "replacement")} across {CountText.Of(result.FilesNormalized, "file")} ({CountText.Of(result.BackupsCreatedCount, "backup")})";
                SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

                if (RecentDocsActionBtn != null)

                {

                    Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Applied");

                }
            }
            finally
            {
                _recentDocsRewriteRunning = false;
            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't apply dialect normalization: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceFrontmatterSchemasClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceFrontmatterSchemas(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Frontmatter: {result.ConformingFilesCount}/{result.FilesWithFrontmatter} conforming ({CountText.Of(result.MissingKeysViolationsCount, "missing key")}, {CountText.Of(result.DateFormatViolationsCount, "date error")})";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Frontmatter");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit frontmatter schemas: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceWikiLinksClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceWikiLinksAndAssets(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Links & Assets: {result.ValidTargetsCount}/{result.TotalLinksAudited} resolved ({result.BrokenTargetsCount} broken, {result.WikiLinksCount} wikilinks, {result.AssetLinksCount} assets)";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Links");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace links: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceTagTaxonomyClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceTagTaxonomy(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Tag Taxonomy: {CountText.Of(result.UniqueTagsCount, "unique tag")} ({CountText.Of(result.CasingInconsistenciesCount, "casing variant")}, {CountText.Of(result.PluralDuplicatesCount, "plural duplicate")}, {CountText.Of(result.SingletonTagsCount, "singleton")})";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Tags");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace tag taxonomy: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceWordCountsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceWordCountsAndReadingTimes(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Word Counts: {CountText.Of(result.TotalWords, "word")} (~{result.TotalReadingMinutes:0.#} min read across {CountText.Of(result.TotalFilesScanned, "doc")}, longest: {result.LongestDocumentName})";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Words");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace word counts: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceCodeFenceLanguagesClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceCodeFenceLanguages(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Code Blocks: {CountText.Of(result.TotalCodeBlocks, "block")} ({result.TaggedCodeBlocks:N0} tagged across {CountText.Of(result.TotalFilesScanned, "doc")})";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Fences");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace code fence languages: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceMathFormulasClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceMathFormulasAndKaTeX(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Math Formulas: {CountText.Of(result.TotalFormulasFound, "formula")} ({CountText.Of(result.SyntaxErrorCount, "error")} across {CountText.Of(result.TotalFilesScanned, "doc")})";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Math");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace math formulas: {ex.Message}", success: false);

        }

    }



        private async void OnAuditWorkspaceTableAlignmentClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceTableAlignment(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Table Alignment: {CountText.Of(result.TotalTablesFound, "table")} ({result.RaggedColumnCount:N0} ragged across {CountText.Of(result.TotalFilesScanned, "doc")})";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Tables");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't audit workspace table alignment: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceFrontmatterClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceFrontmatterSchemaValidation(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Frontmatter: {CountText.Of(result.FrontmatterDocumentCount, "doc")} with frontmatter ({CountText.Of(result.MissingRequiredKeysCount, "missing key")} across {CountText.Of(result.TotalDocumentsAudited, "doc")})";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Frontmatter");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Audit failed: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceTaskListsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceTaskListProgression(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Workspace Tasks: {result.CompletedTasksCount}/{result.TotalTasksFound} completed ({result.WorkspaceCompletionPercentage:0.#}%) across {CountText.Of(result.DocumentsWithTasksCount, "doc")}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Tasks");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Audit failed: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceInterDocLinksClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceInterDocumentLinks(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Cross-Doc Links: {CountText.Of(result.TotalLinksAudited, "link")} ({result.ValidLinksCount:N0} valid, {CountText.Of(result.MissingFileCount, "missing file")}, {CountText.Of(result.MissingAnchorCount, "missing anchor")}) across {CountText.Of(result.DocumentsWithLinksCount, "doc")}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Links");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Audit failed: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceImageAssetsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceImageAssetPaths(files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = $"Workspace Images: {result.TotalImagesFound} found ({result.ValidLocalImagesCount} valid local, {result.BrokenLocalImagesCount} broken) across {CountText.Of(result.DocumentsWithImagesCount, "doc")}";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Images");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Audit failed: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceMarkdownTablesClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceMarkdownTableFormatting(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Tables");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Audit failed: {ex.Message}", success: false);

        }

    }



private async void OnAuditWorkspaceMarkdownTableAutoAlignAndExportClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var report = RecentFilesService.ExportWorkspaceTableAutoAlignmentReport(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(report);

            var copyMessage = "Workspace table auto-align report copied to clipboard";
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Report");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Audit failed: {ex.Message}", success: false);

        }

    }







    private async void OnApplyWorkspaceMarkdownTableAutoAlignClick(object sender, RoutedEventArgs e)

    {

        try

        {

            if (RecentDocsRewriteIsRunning()) return;

            var files = App.RecentFiles?.Load() ?? new List<string>();

            if (!await ConfirmRecentDocsActionAsync("Align Markdown tables?", $"Pads and aligns the table columns in {DocumentCount(files.Count)} from your recent list. A .bak.md backup of each changed file is kept.", "Rewrite files")) return;

            if (RecentDocsRewriteIsRunning()) return;
            _recentDocsRewriteRunning = true;
            SetNotification($"Rewriting {DocumentCount(files.Count)}…");
            try
            {
                var result = await Task.Run(() => RecentFilesService.ApplyWorkspaceTableAutoAlignmentFixes(explicitFiles: files));

                var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

                var copyMessage = result.Summary;
                SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

                if (RecentDocsActionBtn != null)

                {

                    Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Auto-Align");

                }
            }
            finally
            {
                _recentDocsRewriteRunning = false;
            }

        }

        catch (Exception ex)

        {

            SetNotification($"Table auto-fix failed: {ex.Message}", success: false);

        }

    }



    private async void OnRestoreWorkspaceTableBackupsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            if (RecentDocsRewriteIsRunning()) return;

            var files = App.RecentFiles?.Load() ?? new List<string>();

            if (!await ConfirmRecentDocsActionAsync("Restore table backups?", $"Puts back the .bak.md copy of each of {DocumentCount(files.Count)} from your recent list that has one. Edits made since the tables were aligned are lost.", "Restore backups")) return;

            if (RecentDocsRewriteIsRunning()) return;
            _recentDocsRewriteRunning = true;
            SetNotification($"Rewriting {DocumentCount(files.Count)}…");
            try
            {
                var result = await Task.Run(() => RecentFilesService.RestoreWorkspaceTableAutoAlignmentBackups(explicitFiles: files));

                var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

                var copyMessage = result.Summary;
                SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

                if (RecentDocsActionBtn != null)

                {

                    Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Restored");

                }
            }
            finally
            {
                _recentDocsRewriteRunning = false;
            }

        }

        catch (Exception ex)

        {

            SetNotification($"Table restore failed: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceBacklinksClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceBacklinksAndUnreferencedAnchors(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Backlinks Audited");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Backlink audit failed: {ex.Message}", success: false);

        }

    }

    private async void OnAuditWorkspaceMediaAttachmentsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceMediaAndImageAttachments(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Media Audited");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Media audit failed: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceMathDelimitersClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceMathFormulasAndDelimiters(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Math Audited");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Math formula audit failed: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceMermaidDiagramsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceMermaidDiagrams(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Diagrams Audited");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Mermaid diagram audit error: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceWikilinksAndCrossReferencesClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceWikilinksAndCrossReferences(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, $"\u2713 CrossRefs {result.CrossReferenceIntegrityScore:F0}%");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Cross-reference audit error: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceMediaAssetOptimizationClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceMediaAssetOptimization(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, $"\u2713 Media {result.MediaOptimizationScore:F0}%");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Media optimization audit error: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceHeadingSlugCollisionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = App.RecentFiles?.Load() ?? new List<string>();
            var result = RecentFilesService.AuditWorkspaceHeadingSlugCollisions(explicitFiles: files);
            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);
            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);
            if (RecentDocsActionBtn != null)
            {
                RecentDocsActionBtn.Content = "Slugs Audited";
            }
        }
        catch (Exception ex)
        {
            SetNotification($"Failed to audit workspace heading slugs: {ex.Message}", success: false);
        }
    }


    private async void OnAuditWorkspaceRelativeLinksClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = App.RecentFiles?.Load() ?? new List<string>();
            var result = RecentFilesService.AuditWorkspaceRelativeLinks(explicitFiles: files);
            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);
            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);
            if (RecentDocsActionBtn != null)
            {
                RecentDocsActionBtn.Content = "Links Audited";
            }
        }
        catch (Exception ex)
        {
            SetNotification($"Link audit error: {ex.Message}", success: false);
        }
    }

    private async void OnAuditWorkspaceMediaAssetsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = App.RecentFiles?.Load() ?? new List<string>();
            var result = RecentFilesService.AuditWorkspaceMediaAssetsExistence(explicitFiles: files);
            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);
            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);
            if (RecentDocsActionBtn != null)
            {
                RecentDocsActionBtn.Flyout?.Hide();
            }
        }
        catch (Exception ex)
        {
            SetNotification($"Media asset audit failed: {ex.Message}", success: false);
        }
    }

    private async void OnAuditWorkspaceDocumentTaxonomyCohesionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = App.RecentFiles?.Load() ?? new List<string>();
            var result = RecentFilesService.AuditWorkspaceDocumentTaxonomyCohesion(explicitFiles: files);
            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);
            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);
            if (RecentDocsActionBtn != null)
            {
                RecentDocsActionBtn.Flyout?.Hide();
            }
        }
        catch (Exception ex)
        {
            SetNotification($"Taxonomy audit failed: {ex.Message}", success: false);
        }
    }

    private async void OnAuditWorkspaceDocumentAnchorSlugsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = App.RecentFiles?.Load() ?? new List<string>();
            var result = RecentFilesService.AuditWorkspaceDocumentAnchorSlugs(explicitFiles: files);
            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);
            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);
            if (RecentDocsActionBtn != null)
            {
                RecentDocsActionBtn.Flyout?.Hide();
            }
        }
        catch (Exception ex)
        {
            SetNotification($"Anchor slug audit failed: {ex.Message}", success: false);
        }
    }

    private async void OnAuditWorkspaceDocumentTocConsistencyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = App.RecentFiles?.Load() ?? new List<string>();
            var result = RecentFilesService.AuditWorkspaceDocumentTocConsistency(explicitFiles: files);
            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);
            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);
            if (RecentDocsActionBtn != null)
                RecentDocsActionBtn.Content = "✓ Copied TOC Audit";
        }
        catch (Exception ex)
        {
            SetNotification($"TOC audit error: {ex.Message}", success: false);
        }
    }

    private async void OnAuditWorkspaceCodeBlockLanguageTagsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceCodeBlockLanguageTags(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, $"\u2713 Lang {result.DocumentationQualityScore:F0}%");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Language tag audit failed: {ex.Message}", success: false);

        }

    }







    private async void OnAuditWorkspaceDocumentReadabilityDensityClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceDocumentReadabilityDensity(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, $"\u2713 Readability {result.ReadabilityBalanceScore:F0}%");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Readability audit error: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceFrontmatterAndDateHygieneClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceFrontmatterAndDateHygiene(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Frontmatter Audited");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Frontmatter audit error: {ex.Message}", success: false);

        }

    }





    private async void OnAuditWorkspaceCrossFileLinksClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceCrossFileLinks(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Links Audited");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Link audit failed: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceMarkdownTableAutoAlignClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceMarkdownTableAutoAlign(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Tables");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Audit failed: {ex.Message}", success: false);

        }

    }



    private async void OnAuditWorkspaceHeadingSlugsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var files = App.RecentFiles?.Load() ?? new List<string>();

            var result = RecentFilesService.AuditWorkspaceHeadingSlugsAndTocAnchors(explicitFiles: files);

            var clipboardError = await Services.ClipboardWriter.TrySetTextAsync(result.Summary);

            var copyMessage = result.Summary;
            SetNotification(clipboardError is null ? copyMessage : Services.CopyStatusText.Failed(copyMessage, clipboardError), success: clipboardError is null);

            if (RecentDocsActionBtn != null)

            {

                Services.CopyFeedback.ApplyButtonTick(RecentDocsActionBtn, null, "\u2713 Slugs");

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Audit failed: {ex.Message}", success: false);

        }

    }



private void OnClearRecentFilterClick(object sender, RoutedEventArgs e)

    {

        RecentSearchBox.Text = "";

        LoadRecentFiles("");

        SetNotification("Cleared recent document filter.", success: true);

    }



    private async void OnBrowseFileToOpenClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var filePath = await NativeFilePicker.PickOpenFileAsync(

                this,

                "Open Markdown Document",

                NativeFilePicker.Purpose.Documents,

                new[]

                {

                    FileType.Of("Markdown files", ".md", ".markdown", ".mdown", ".mkdn", ".txt"),

                    FileType.AllFiles

                },

                okLabel: "Open");



            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))

            {

                App.RecentFiles?.AddToRecent(filePath);

                LoadRecentFiles();

                try

                {

                    Process.Start(new ProcessStartInfo

                    {

                        FileName = filePath,

                        UseShellExecute = true

                    });

                    SetNotification($"Opened: {Path.GetFileName(filePath)}", success: true);

                }

                catch (Exception ex)

                {

                    SetNotification($"Couldn't launch document: {ex.Message}", success: false);

                }

            }

        }

        catch (Exception ex)

        {

            SetNotification($"Failed to browse document: {ex.Message}", success: false);

        }

    }



    private void OnOpenDocumentsFolderClick(object sender, RoutedEventArgs e)

    {

        try

        {

            string docsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            string marksmithFolder = Path.Combine(docsFolder, "MarkSmith");

            if (!Directory.Exists(marksmithFolder))

            {

                Directory.CreateDirectory(marksmithFolder);

            }



            Process.Start(new ProcessStartInfo

            {

                FileName = "explorer.exe",

                Arguments = $"\"{marksmithFolder}\"",

                UseShellExecute = true

            });

            SetNotification("Opened MarkSmith Documents folder.", success: true);

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't open documents folder: {ex.Message}", success: false);

        }

    }



    // Asks before an action from the recent documents menu rewrites the user's files or erases the
    // list: that menu is long, and one stray click used to run these straight away. The hub is a
    // ContentDialog, so the question is a flyout by the menu's button rather than a second dialog.
    private Task<bool> ConfirmRecentDocsActionAsync(string title, string message, string confirmText)
    {
        FrameworkElement anchor = RecentDocsActionBtn is { XamlRoot: not null, ActualWidth: > 0 } ? RecentDocsActionBtn : this;
        return Services.ConfirmFlyout.AskAsync(anchor, title, message, confirmText);
    }

    // One rewrite of the recent documents at a time: a second Apply / Restore started while the
    // first is still writing would read files that are halfway through being changed.
    private bool _recentDocsRewriteRunning;

    private bool RecentDocsRewriteIsRunning()
    {
        if (!_recentDocsRewriteRunning) return false;
        SetNotification("Still rewriting your recent documents. Try again when that finishes.", success: false);
        return true;
    }

    private static string DocumentCount(int count) => CountText.Of(count, "document");


    private async void OnClearRecentDocsClick(object sender, RoutedEventArgs e)

    {

        try

        {

            var count = App.RecentFiles?.Load().Count ?? 0;

            if (count > 0 && !await ConfirmRecentDocsActionAsync("Clear recent documents?", $"Removes {DocumentCount(count)} from the list. The files themselves are not touched.", "Clear list")) return;

            App.RecentFiles?.Clear();

            RecentFilesPanel.Children.Clear();

            RecentFilesPanel.Visibility = Visibility.Collapsed;

            NoRecentDocsText.Visibility = Visibility.Visible;

            SetNotification("Cleared recent documents history.", success: true);

        }

        catch (Exception ex)

        {

            SetNotification($"Couldn't clear recent documents: {ex.Message}", success: false);

        }

    }



    private void OnRefreshRecentDocsButtonRightTapped(object sender, RightTappedRoutedEventArgs e)

    {

        e.Handled = true;

        if (RecentDocsFlyout is not null && sender is FrameworkElement fe)

        {

            RecentDocsFlyout.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));

        }

    }



    private void OnRecentDocsStripRightTapped(object sender, RightTappedRoutedEventArgs e)

    {

        e.Handled = true;

        if (RecentDocsFlyout is not null && sender is FrameworkElement fe)

        {

            RecentDocsFlyout.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));

        }

    }



    // Double-click on the strip's bare background refreshes. Not from its controls: a double-click

    // on the count pill would copy twice and refresh, one on the filter box (selecting a word)

    // would reload the list under the cursor.

    private void OnRecentDocsStripDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)

    {

        for (var d = e.OriginalSource as DependencyObject; d is not null && !ReferenceEquals(d, sender); d = VisualTreeHelper.GetParent(d))

            if (d is ButtonBase or TextBox or AutoSuggestBox) return;

        e.Handled = true;

        OnRefreshRecentDocsClick(sender, new RoutedEventArgs());

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



    private void OnTemplatePostmortemClick(object sender, RoutedEventArgs e)

    {

        CreateAndOpenStarterDocument("Incident_Postmortem", """

# Incident Postmortem: [INC-XXXX] [Brief Description]



**Date of Incident:** YYYY-MM-DD | **Severity:** Sev-1 / Critical | **Incident Lead:** [Name]

**Duration:** [X hours, Y minutes] | **Impacted Users/Services:** [Details]



## 1. Executive Summary

High-level summary of what broke, the business/user impact, how it was detected, and how service was restored.



## 2. Incident Timeline (UTC)



| Time | Event | Logged By |

| :--- | :--- | :--- |

| 14:15 | Automated alert triggers on elevated 5xx error rate | AlertBot |

| 14:22 | On-call engineer initiates incident bridge | @engineer |

| 14:40 | Root cause identified in deployment artifact v2.14 | @lead |

| 14:52 | Safe rollback executed; error rates return to baseline | @engineer |

| 15:10 | Verification complete; incident resolved | @lead |



## 3. Root Cause Analysis (5 Whys)

1. **Why?** - The service experienced memory exhaustion and restarted in a crash loop.

2. **Why?** - An unindexed query produced a full table scan across millions of records.

3. **Why?** - A new filter was merged without verifying composite index coverage.

4. **Why?** - The CI verification suite used small fixture datasets that didn't reveal scale degradation.

5. **Why?** - Large-scale benchmark verification was not an automated PR gating check.



## 4. What Went Well & What Could Be Improved

### What Went Well

- Fast escalation: on-call acknowledged within 7 minutes.

- Clean rollback: zero data corruption or uncommitted state.



### Where We Got Lucky

- Occurred during daylight on-call rotation with full engineering presence.



### What Needs Improvement

- Automated canary alerts did not block promotion to 100% traffic.



## 5. Preventative Action Items



| Item | Action | Type | Owner | Due Date |

| :--- | :--- | :--- | :--- | :--- |

| ACT-01 | Add query execution plan lint check to CI pipeline | Prevent | @owner | YYYY-MM-DD |

| ACT-02 | Introduce 5% automated canary analysis gate | Mitigate | @owner | YYYY-MM-DD |

| ACT-03 | Run synthetic load stress tests on staging | Detect | @owner | YYYY-MM-DD |

""");

    }



    private void OnTemplateRfcClick(object sender, RoutedEventArgs e)

    {

        CreateAndOpenStarterDocument("RFC_Proposal", """

# RFC: [Proposal Title]



**RFC Number:** [00XX] | **Author:** [Author Name] | **Status:** Draft / Open for Review

**Target Milestone:** [Version / Quarter] | **Reviewers:** [Architects, Tech Leads]



## 1. Summary

A concise one-paragraph explanation of what is proposed and why it matters.



## 2. Motivation & Background

Why are we doing this? What problems or limitations does this address? What use cases will this enable?



## 3. Goals & Non-Goals

### Goals

- Goal 1: First-class native performance and predictability.

- Goal 2: Backward-compatible upgrade path for existing users.



### Non-Goals

- Explicitly out of scope requirements that will not be addressed in this RFC.



## 4. Proposed Architecture & Design



```mermaid

graph LR

    Input[Proposal Input] --> Parser[Validation & Parse]

    Parser --> Engine[Execution Engine]

    Engine --> Output[Rendered Artifact]

```



### Detailed Design & Implementation

- Specification of key interfaces, classes, and workflows.

- Error handling, timeouts, and degradation modes.



## 5. Alternatives Considered & Trade-offs

- **Alternative A:** [Description and reason rejected]

- **Alternative B:** [Description and reason rejected]



## 6. Security, Privacy & Reliability Considerations

- Impact on data isolation, telemetry, and privilege boundaries.

- Failure domains and blast radius mitigation.



## 7. Open Questions & Decision Log

- [ ] Question 1: Unresolved architectural trade-off to be discussed in review.

""");

    }



    private void OnTemplatePrdClick(object sender, RoutedEventArgs e)

    {

        CreateAndOpenStarterDocument("product-requirements-document", """

# Product Requirements Document (PRD)



> **Document Status:** Draft  

> **Author / PM:** [Product Lead]  

> **Target Release:** [v2.4 / Q4 2026]  

> **Last Updated:** [Date]



---



## 1. Executive Summary & Problem Statement

Brief 2-3 paragraph overview of the problem being solved, the user pain points observed, and why now is the strategic time to invest in this solution.



- **Pain Point 1:** [Description of key blocker or user friction]

- **Pain Point 2:** [Quantified impact on conversion, retention, or developer velocity]



## 2. Target Personas & User Stories

| Persona | User Story | Priority |

| :--- | :--- | :--- |

| **Enterprise Architect** | As an architect, I want declarative schema export so that I can validate security contracts in CI. | P0 |

| **Engineering Lead** | As a team lead, I want live preview synchronization so that our specs match deployed code. | P0 |

| **Technical Writer** | As an author, I want instant typography formatting so that published docs look uniform. | P1 |



## 3. End-to-End User Experience & Flow



```mermaid

graph LR

    Discover[User Discovers Need] --> Config[Configure Workspace]

    Config --> Validate[Automated Policy Check]

    Validate --> Deploy[Execute & Monitor]

    Deploy --> Feedback[Collect Telemetry & CSAT]

```



## 4. Functional Requirements & Scope

### In-Scope (Phase 1)

- [ ] **FR-1:** Native zero-dependency schema validation engine.

- [ ] **FR-2:** Single-click artifact bundle export with SHA-256 provenance stamp.

- [ ] **FR-3:** Responsive status bar telemetry and health indicators.



### Out-of-Scope (Deferred to Phase 2)

- Multi-region replication and legacy format migration tooling.



## 5. Non-Functional Requirements & Guardrails

- **Performance:** Cold start time < 800ms; memory footprint under 90MB on x64 desktop.

- **Reliability:** 99.99% successful local file render rate; zero crash degradation on corrupted AST input.

- **Accessibility:** Full WCAG 2.1 AA compliance, high-contrast theme support, and complete keyboard navigation.



## 6. Success Metrics & Launch Criteria

- **Adoption:** 65%+ weekly active user adoption within 30 days of General Availability.

- **Efficiency:** 40% reduction in time-to-first-published-spec measured via telemetry.

""");

    }



    private void OnTemplateApiDocClick(object sender, RoutedEventArgs e)

    {

        CreateAndOpenStarterDocument("rest-api-specification", """

# REST API Specification



> **Base URL:** `https://api.marksmith.internal/v1`  

> **Protocol:** HTTPS (TLS 1.3)  

> **Version:** 1.4.0  

> **Authentication:** Bearer JWT token (`Authorization: Bearer <token>`)



---



## 1. Authentication & Common Headers

All API requests must supply standard tracing and authorization headers:



```http

Authorization: Bearer eyJhbGciOi...

X-Request-ID: 7b3e028b-4976-4d44-934c-687424ad4a02

Content-Type: application/json

Accept: application/json

```



## 2. Request & Response Lifecycle



```mermaid

sequenceDiagram

    autonumber

    Client->>Gateway: POST /v1/documents (Bearer Token)

    Gateway->>AuthService: Validate JWT & Scopes

    AuthService-->>Gateway: 200 OK (Tenant ID: t_9021)

    Gateway->>Worker: Dispatch Document Synthesis Task

    Worker-->>Gateway: Synthesis Finished (SHA: 4f1a)

    Gateway-->>Client: 201 Created (Document JSON)

```



## 3. Endpoints Catalog



### `POST /v1/documents/render`

Renders raw Markdown into an OpenXML or PDF binary document bundle.



#### Request Parameters

- **`format`** *(string, query, required)* — Target export format: `docx`, `pdf`, `epub`.

- **`includeMermaid`** *(boolean, query, optional)* — Pre-render client diagrams to vector SVG (default: `true`).



#### Request Payload

```json

{

  "title": "Quarterly Systems Review",

  "author": "Engineering Governance Team",

  "theme": "NordicSlate",

  "content": "# Executive Overview\n\nAll systems operational."

}

```



#### Response Payload (`201 Created`)

```json

{

  "id": "doc_928103",

  "status": "ready",

  "format": "docx",

  "sizeBytes": 48204,

  "sha256": "8f4e2...a12c",

  "downloadUrl": "https://cdn.marksmith.internal/export/doc_928103.docx"

}

```



## 4. Status Codes & Error Schema

| Status Code | Reason | Resolution |

| :--- | :--- | :--- |

| `200 OK` | Request succeeded | Process returned payload. |

| `201 Created` | Resource synthesized | Download artifact via URL. |

| `400 Bad Request` | Malformed Markdown AST or invalid schema | Inspect `error.details` for line number. |

| `401 Unauthorized` | Missing or expired JWT bearer token | Refresh token via `/v1/auth/token`. |

| `429 Too Many Requests` | Tenant rate limit exceeded (100 req/min) | Back off using `Retry-After` header. |

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

        int total = 0;

        foreach (var child in StarterTemplatesPanel.Children)

        {

            if (child is Button btn)

            {

                total++;

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

        if (TemplateCountText != null)

        {

            TemplateCountText.Text = visibleCount == total ? $"{total} templates" : $"{visibleCount}/{total} templates";

        }

        if (TemplateCountPill != null)

        {

            ToolTipService.SetToolTip(TemplateCountPill, string.IsNullOrEmpty(query)

                ? $"Starter templates ({total}) · Click to search them · Right-click or Shift+F10 for categories"

                : $"Filtered templates: {visibleCount} of {total} match '{query}' · Click to show all · Right-click or Shift+F10 for categories");

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TemplateCountPill, string.IsNullOrEmpty(query)

                ? $"Search {total} starter templates"

                : $"Show all templates, {visibleCount} of {total} shown");

        }

        if (TemplateSearchStatusText != null)

        {

            if (string.IsNullOrEmpty(query))

            {

                TemplateSearchStatusText.Visibility = Visibility.Collapsed;

            }

            else

            {

                TemplateSearchStatusText.Text = visibleCount > 0 ? CountText.Of(visibleCount, "match", "matches") : "No templates match";

                TemplateSearchStatusText.Visibility = Visibility.Visible;

            }

        }

    }



    // One action per click: a filtered list is cleared; an unfiltered one hands off to the search box.

    // (Tapped cleared and DoubleTapped focused, so a double-click did both, and the keyboard could do neither.)

    private void OnTemplateCountPillClick(object sender, RoutedEventArgs e)

    {

        if (TemplateSearchBox is null) return;

        if (!string.IsNullOrEmpty(TemplateSearchBox.Text))

        {

            TemplateSearchBox.Text = "";

            FilterTemplates("");

        }

        else

        {

            TemplateSearchBox.Focus(FocusState.Keyboard);

        }

    }



    private void OnTemplateCountPillRightTapped(object sender, RightTappedRoutedEventArgs e)

    {

        e.Handled = true;

        var menu = new MenuFlyout();



        var allItem = new MenuFlyoutItem

        {

            Text = $"All templates ({StarterTemplatesPanel?.Children.OfType<Button>().Count() ?? 0})",

            Icon = new FontIcon { Glyph = "\uE81C" }

        };

        allItem.Click += (_, _) =>

        {

            if (TemplateSearchBox != null) TemplateSearchBox.Text = "";

            FilterTemplates("");

        };

        menu.Items.Add(allItem);



        menu.Items.Add(new MenuFlyoutSeparator());



        var techItem = new MenuFlyoutItem

        {

            Text = "Engineering & Specs (Tech Spec, ADR, RFC, REST API)",

            Icon = new FontIcon { Glyph = "\uE943" }

        };

        techItem.Click += (_, _) =>

        {

            if (TemplateSearchBox != null) TemplateSearchBox.Text = "spec";

            FilterTemplates("spec");

        };

        menu.Items.Add(techItem);



        var productItem = new MenuFlyoutItem

        {

            Text = "Product & Planning (PRD, Meeting Notes)",

            Icon = new FontIcon { Glyph = "\uE7C1" }

        };

        productItem.Click += (_, _) =>

        {

            if (TemplateSearchBox != null) TemplateSearchBox.Text = "planning";

            FilterTemplates("planning");

        };

        menu.Items.Add(productItem);



        var opsItem = new MenuFlyoutItem

        {

            Text = "Incident & Operations (Postmortem, Release Notes)",

            Icon = new FontIcon { Glyph = "\uE7BA" }

        };

        opsItem.Click += (_, _) =>

        {

            if (TemplateSearchBox != null) TemplateSearchBox.Text = "ops";

            FilterTemplates("ops");

        };

        menu.Items.Add(opsItem);



        var visualItem = new MenuFlyoutItem

        {

            Text = "Diagrams & Maps (SmartArt, MindMap)",

            Icon = new FontIcon { Glyph = "\uF003" }

        };

        visualItem.Click += (_, _) =>

        {

            if (TemplateSearchBox != null) TemplateSearchBox.Text = "visual";

            FilterTemplates("visual");

        };

        menu.Items.Add(visualItem);



        menu.Items.Add(new MenuFlyoutSeparator());



        var searchItem = new MenuFlyoutItem

        {

            Text = "Search templates",

            Icon = new FontIcon { Glyph = "\uE721" }

        };

        searchItem.Click += (_, _) => TemplateSearchBox?.Focus(FocusState.Programmatic);

        menu.Items.Add(searchItem);



        menu.ShowAt(TemplateCountPill, KeyboardContextMenu.PointIn(e, TemplateCountPill));

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

                else if (btn == TemplatePostmortemBtn) OnTemplatePostmortemClick(btn, new RoutedEventArgs());

                else if (btn == TemplateRfcBtn) OnTemplateRfcClick(btn, new RoutedEventArgs());

                else if (btn == TemplatePrdBtn) OnTemplatePrdClick(btn, new RoutedEventArgs());

                else if (btn == TemplateApiDocBtn) OnTemplateApiDocClick(btn, new RoutedEventArgs());

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



    private void OnMemoryUsagePillClick(object sender, RoutedEventArgs e)

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

    // MarkSmith names its temp files marksmith-*. The cache readout and the prune used to take
    // ms-* too, which counted (and, ten minutes on, deleted) other programs' temp files.
    internal static IEnumerable<FileInfo> EnumerateOwnTempFiles(DirectoryInfo tempDir) =>
        tempDir.EnumerateFiles("marksmith*", SearchOption.TopDirectoryOnly);



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

            var files = EnumerateOwnTempFiles(tempDir);

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

        ToolTipService.SetToolTip(UptimeStoragePill, $"Desktop engine uptime: {uptimeStr} · Temp cache: {CountText.Of(cacheFileCount, "file")} ({cacheMb:F2} MB) · Click to prune stale MarkSmith temp files · Right-click or Shift+F10 to copy diagnostics");

    }



    private void OnUptimeStoragePillClick(object sender, RoutedEventArgs e)

    {

        int deleted = 0;

        long freedBytes = 0;

        try

        {

            var tempDir = new DirectoryInfo(Path.GetTempPath());

            var files = EnumerateOwnTempFiles(tempDir);

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

            var files = EnumerateOwnTempFiles(tempDir);

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

                      $"- Temp Cache Footprint: {CountText.Of(cacheFileCount, "file")} ({cacheMb:F2} MB){Environment.NewLine}" +

                      $"- Temp Directory: {Path.GetTempPath()}{Environment.NewLine}" +

                      $"- Process ID: {proc.Id}{Environment.NewLine}" +

                      $"- Threads: {proc.Threads.Count}";



        CopyToClipboard(diag, "Copied engine uptime & cache diagnostics to clipboard.");

    }



    #endregion

}

