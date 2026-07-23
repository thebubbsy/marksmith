using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MdToPdf.Mermaid.Sync;

namespace MdToPdf;

public sealed partial class MainWindow : Window, Services.IWebRenderHost, Services.IUiPrompts
{
    private static readonly HashSet<string> PreviewAffectingProperties = new()
    {
        nameof(ViewModels.MainViewModel.PastedMarkdown),
        nameof(ViewModels.MainViewModel.InputFilePath),
        nameof(ViewModels.MainViewModel.UsePasteSource),
        nameof(ViewModels.MainViewModel.SelectedThemeName),
        nameof(ViewModels.MainViewModel.ContentWidth),
        nameof(ViewModels.MainViewModel.A4FixedWidth),
        nameof(ViewModels.MainViewModel.UnlimitedHeight),
        nameof(ViewModels.MainViewModel.IncludeToc),
        nameof(ViewModels.MainViewModel.ShowAttribution),
        nameof(ViewModels.MainViewModel.NoEmoji),
        nameof(ViewModels.MainViewModel.DashMode),
        nameof(ViewModels.MainViewModel.DashCustom),
        nameof(ViewModels.MainViewModel.HeadingShift),
        nameof(ViewModels.MainViewModel.BoldMode),
        nameof(ViewModels.MainViewModel.ItalicMode),
        nameof(ViewModels.MainViewModel.BrandFontFamily),
    };

    private static readonly HashSet<string> AutomationProperties = new()
    {
        nameof(ViewModels.MainViewModel.AutoClipboardIngest),
        nameof(ViewModels.MainViewModel.WatchFolderEnabled),
        nameof(ViewModels.MainViewModel.WatchFolder),
        nameof(ViewModels.MainViewModel.ApiEnabled),
        nameof(ViewModels.MainViewModel.ApiPort),
    };

    private readonly DispatcherQueueTimer _previewDebounce;

    // Preview refresh intensity. Typing does a silent "light" refresh (no sprite, no blur); a paste or
    // a style/theme change does a "heavy" refresh — the loading sprite over a blur that clears as it
    // completes. PropertyChanged fires per keystroke, so a single edit's delta is our typing signal.
    private int _lastMarkdownLen;
    private bool _nextRefreshHeavy;
    private bool _isPreviewRefreshing;
    private bool _pendingPreviewRefresh;
    private string? _lastRenderedMarkdown;
    // True while the mermaid snapshot renderer owns the WebView — preview refreshes (e.g. the ingest
    // debounce firing mid-harvest) must not navigate away from the render page.
    private bool _mermaidHarvestActive;
    private const int HeavyChangeThreshold = 32; // chars changed in one edit above which it's a paste, not typing
    private readonly Services.ClipboardIngestService _clipboardIngest;
    private readonly Services.FolderIngestService _folderIngest;
    private readonly Services.AutomationManager _automationManager;
    private readonly Services.ExportCoordinator _exportCoordinator = new();
    private H.NotifyIcon.TaskbarIcon? _trayIcon;
    private bool _exitRequested;
    private bool _showingLogsDialog;
    private List<string> _sessionLogFiles = new();

    // Preview loading spinner state. The spinner ticks at ~60fps and stays up for at least SpinMinSec
    // so a fast render never looks like a white flash. It hides only once BOTH the navigation has
    // completed and the minimum time passed. Two styles alternate each time (not random): mode 0 spins
    // the logo about its centre, mode 1 traces an upright figure-eight.
    private const double SpinMinSec = 0.65;
    private const double SpinDt = 1.0 / 60.0;
    private DispatcherQueueTimer? _spinTimer;
    private int _spinMode;         // 0 spin, 1 figure-eight — alternates on each show
    private double _spinPhase;     // seconds the spinner has been visible
    private bool _spinNavDone;
    private bool _spinActive;

    public IRelayCommand ShowWindowCommand { get; }
    public IRelayCommand ExitApplicationCommand { get; }

    private ViewModels.MainViewModel ViewModel => App.ViewModel;

    public MainWindow()
    {
        ShowWindowCommand = new RelayCommand(() =>
        {
            AppWindow.Show();
            Activate();
        });
        ExitApplicationCommand = new RelayCommand(() =>
        {
            _exitRequested = true;
            _trayIcon?.Dispose();
            Close();
        });

        InitializeComponent();

        Title = "MarkSmith";
        // Unpackaged app: the exe icon covers Explorer/taskbar, but the title bar needs an
        // explicit runtime assignment (relative paths resolve against the CWD, so anchor to base).
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1220, 800));
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        RootGrid.DataContext = ViewModel;

        // Live preview-width ruler (the hairline under the preview): report the pane's width in CSS
        // pixels as the user resizes. WebView2 maps 1 CSS px to 1 DIP at zoom 1, so ActualWidth is
        // the same number the rendered document sees for its own px-based page width.
        PreviewWebView.SizeChanged += (_, e) =>
            PreviewWidthText.Text = $"{(int)Math.Round(e.NewSize.Width)} px";

        // Typing in the paste editor fires PropertyChanged per keystroke; coalesce preview
        // reloads so WebView2 isn't re-navigated on every character.
        _previewDebounce = DispatcherQueue.CreateTimer();
        _previewDebounce.Interval = TimeSpan.FromMilliseconds(250);
        _previewDebounce.IsRepeating = false;
        _previewDebounce.Tick += async (_, _) =>
        {
            var heavy = _nextRefreshHeavy;
            _nextRefreshHeavy = false;
            await RefreshPreviewAsync(heavy);
        };

        _spinTimer = DispatcherQueue.CreateTimer();
        _spinTimer.Interval = TimeSpan.FromMilliseconds(16);
        _spinTimer.IsRepeating = true;
        _spinTimer.Tick += (_, _) => OnSpinTick();

        _clipboardIngest = new Services.ClipboardIngestService(DispatcherQueue, (text, origin, output) => IngestFromSource(text, origin, output));
        _folderIngest = new Services.FolderIngestService(DispatcherQueue, path => _ = OnWatchedFileAsync(path));
        _automationManager = new Services.AutomationManager(
            App.LlmSource,
            () => ViewModel.ThemeNames.ToList(),
            (md, origin, ovr) => DispatcherQueue.TryEnqueue(() => IngestFromSource(md, origin, ovr)),
            ConvertForApiAsync,
            App.Governance,
            () => App.Settings.Current.AllowedExtensionId,
            () => App.Settings.Current,
            settings => { App.Settings.Current.UpdateFrom(settings); App.Settings.Save(); },
            BatchConvertForApiAsync);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        App.License.Changed += () => DispatcherQueue.TryEnqueue(() => { SyncAdvancedSection(); UpdateLicenseBanner(); });
        SyncSourcePanels();
        ApplyAutomationSettings();
        SyncAdvancedSection();
        UpdateLicenseBanner();
        ExtensionTip.IsOpen = ViewModel.ShowExtensionTip;
        HistoryList.ItemsSource = ViewModel.History; // Flyout popups don't inherit DataContext
        InitTrayIcon();

        AppWindow.Closing += (sender, e) =>
        {
            if (ViewModel.MinimizeToTray && !_exitRequested)
            {
                e.Cancel = true;
                AppWindow.Hide(); // watchers + API keep running; tray icon brings it back
                return;
            }

            if (_sessionLogFiles.Count > 0 && !_showingLogsDialog)
            {
                e.Cancel = true;
                _showingLogsDialog = true;
                _ = ShowDebugLogsDialogAndExitAsync();
            }
        };

        Closed += (_, _) =>
        {
            _clipboardIngest.Dispose();
            _folderIngest.Dispose();
            _automationManager.Dispose();
            _trayIcon?.Dispose();
        };

        ViewModel.LoadPresets();
        _ = InitializePreviewAsync();
        _ = LoadMarkdownFilesAsync(); // scan for real .md files in the background

        // First-run: show the guided tour once the visual tree is ready (XamlRoot available).
        if (!App.Settings.Current.HasSeenWelcome)
            RootGrid.Loaded += OnFirstRunLoaded;
    }

    private void OnFirstRunLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnFirstRunLoaded; // one-shot
        DispatcherQueue.TryEnqueue(() => _ = ShowWelcomeTourAsync());
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.UsePasteSource))
        {
            SyncSourcePanels();
        }

        if (e.PropertyName == nameof(ViewModels.MainViewModel.DetectedSourceText))
        {
            DetectedBadge.Visibility = string.IsNullOrEmpty(ViewModel.DetectedSourceText)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        if (e.PropertyName == nameof(ViewModels.MainViewModel.AdvancedMode))
        {
            SyncAdvancedSection();
        }

        if (e.PropertyName is not null && AutomationProperties.Contains(e.PropertyName))
        {
            ApplyAutomationSettings();
        }

        if (e.PropertyName is not null && PreviewAffectingProperties.Contains(e.PropertyName))
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.PastedMarkdown))
            {
                var len = ViewModel.PastedMarkdown?.Length ?? 0;
                if (Math.Abs(len - _lastMarkdownLen) > HeavyChangeThreshold)
                {
                    _nextRefreshHeavy = true;
                    MaybeShowPlainPasteHint(ViewModel.PastedMarkdown ?? "");
                }
                _lastMarkdownLen = len;
            }
            else
            {
                _nextRefreshHeavy = true; // theme / width / TOC / formatting — a visible re-render
            }
            _previewDebounce.Stop();
            _previewDebounce.Start();
        }
    }

    private bool _plainPasteHintShown;

    // A sizeable paste with no Markdown structure at all almost certainly came from plain
    // copy-paste out of an AI chat — the formatting is already lost. Nudge once per session toward
    // the extension's "Copy as Markdown" button, and bump /api/attention so that button pulses in
    // the browser right now.
    private void MaybeShowPlainPasteHint(string text)
    {
        if (_plainPasteHintShown || text.Length < 250) return;
        if (!LooksLikePlainText(text)) return;
        _plainPasteHintShown = true;
        ExtensionHintBar.IsOpen = true;
        Services.ApiServer.AttentionTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static bool LooksLikePlainText(string t)
    {
        if (t.Contains("```") || t.Contains("](") || t.Contains("**") || t.Contains("【")) return false;
        foreach (var raw in t.Split('\n'))
        {
            var s = raw.TrimStart();
            if (s.StartsWith('#') || s.StartsWith("- ") || s.StartsWith("* ") || s.StartsWith("> ")
                || s.StartsWith('|') || (s.Length > 2 && char.IsDigit(s[0]) && s[1] == '.' && s[2] == ' '))
                return false; // any structural markdown → not a plain paste
        }
        return true;
    }



    // Shows the upgrade banner only when the Pro trial is nearly up (<= 5 days) or has ended.
    private void UpdateLicenseBanner()
    {
        var st = App.License.State;
        if (st.Edition == Models.Edition.Pro) { LicenseBanner.IsOpen = false; return; }

        if (st.Edition == Models.Edition.Trial)
        {
            var daysLeft = st.ExpiresUtc is { } e ? Math.Max(0, (int)Math.Ceiling((e - DateTimeOffset.UtcNow).TotalDays)) : 0;
            if (daysLeft > 5) { LicenseBanner.IsOpen = false; return; }
            LicenseBanner.Severity = InfoBarSeverity.Informational;
            LicenseBanner.Title = $"Pro trial — {daysLeft} day{(daysLeft == 1 ? "" : "s")} left";
            LicenseBanner.Message = "Keep DOCX export, editable equations, automation, and footer-free exports.";
        }
        else // Free
        {
            LicenseBanner.Severity = InfoBarSeverity.Warning;
            LicenseBanner.Title = "Your Pro trial has ended";
            LicenseBanner.Message = "Upgrade to unlock DOCX export, editable equations, automation, and remove the footer.";
        }
        LicenseBanner.IsOpen = true;
    }

    private async void OnUpgradeClick(object sender, RoutedEventArgs e)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(Services.LicenseService.StoreUrl)); }
        catch { /* no browser / bad uri */ }
    }

    // Advanced formatting personalization is a Pro feature: the section shows only when Advanced mode
    // is on AND the user is Pro (or on trial). Re-synced whenever either changes.
    private void SyncAdvancedSection() =>
        AdvancedStyleExpander.Visibility =
            (ViewModel.AdvancedMode && App.License.IsPro) ? Visibility.Visible : Visibility.Collapsed;

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: Models.ExportPreset preset })
            ViewModel.ApplyPreset(preset);
    }

    private async void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "e.g. Client report — dark, branded", Margin = new Thickness(0, 12, 0, 0) };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Save preset",
            Content = new StackPanel { Children = { new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Save the current theme, width, cleanup, formatting, diagram mode and branding as a named preset." }, box } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            ViewModel.SavePreset(box.Text);
            ViewModel.StatusText = $"Preset saved: {box.Text.Trim()}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
    }

    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetsCombo.SelectedItem is Models.ExportPreset preset)
        {
            ViewModel.DeletePreset(preset);
            PresetsCombo.SelectedItem = null;
        }
    }

    // "Create a theme": a color picker per theme element, prefilled from the selected theme so users
    // nudge a coherent starting point rather than eight black swatches. Selecting an existing CUSTOM
    // theme turns this into its editor (Delete offered). Mirrors the Avalonia build's OnCreateThemeClick;
    // themes persist via CustomThemeStore (shared engine) and work everywhere a built-in does.
    private async void OnCreateThemeClick(object sender, RoutedEventArgs e)
    {
        var baseTheme = App.Themes.GetOrDefault(ViewModel.SelectedThemeName);
        var editingCustom = !App.Themes.IsBuiltin(baseTheme.Name);

        var nameBox = new TextBox
        {
            PlaceholderText = "Theme name",
            Text = editingCustom ? baseTheme.Name : $"My {baseTheme.Name}",
        };

        (string Label, string Hint, string Hex)[] elements =
        {
            ("Background", "the page itself", baseTheme.Background),
            ("Text", "body copy", baseTheme.Text),
            ("Headings", "titles + accent", baseTheme.Heading),
            ("Code blocks", "code + callout fill", baseTheme.Code),
            ("Borders", "tables, rules, boxes", baseTheme.Border),
            ("Primary", "labels inside diagrams", baseTheme.Primary),
            ("Panels", "quote/alert backgrounds", baseTheme.Secondary),
            ("Lines", "diagram connectors", baseTheme.Line),
        };

        static Windows.UI.Color Parse(string hex)
        {
            var s = hex.TrimStart('#');
            if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
            return Windows.UI.Color.FromArgb(255,
                Convert.ToByte(s.Substring(0, 2), 16), Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16));
        }
        static string Hex(Windows.UI.Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

        var pickers = new ColorPicker[elements.Length];
        var rows = new StackPanel { Spacing = 8 };
        rows.Children.Add(nameBox);
        for (int i = 0; i < elements.Length; i++)
        {
            // A full ColorPicker × 8 is a wall of color wheels — use a compact swatch that opens the
            // picker in a flyout, so the dialog stays scannable.
            var picker = new ColorPicker
            {
                Color = Parse(elements[i].Hex),
                IsAlphaEnabled = false,
                IsColorChannelTextInputVisible = false,
                ColorSpectrumShape = ColorSpectrumShape.Ring,
            };
            pickers[i] = picker;

            var swatch = new Button
            {
                Width = 92, Height = 30,
                Background = new SolidColorBrush(picker.Color),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 128, 128, 128)),
                BorderThickness = new Thickness(1),
            };
            picker.ColorChanged += (_, args) => swatch.Background = new SolidColorBrush(args.NewColor);
            swatch.Flyout = new Flyout { Content = picker };

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = elements[i].Label });
            label.Children.Add(new TextBlock { Text = elements[i].Hint, FontSize = 11, Opacity = 0.6 });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 0);
            Grid.SetColumn(swatch, 1);
            grid.Children.Add(label);
            grid.Children.Add(swatch);
            rows.Children.Add(grid);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = editingCustom ? $"Edit “{baseTheme.Name}”" : "Create a theme",
            Content = new ScrollViewer { Content = rows, MaxHeight = 480 },
            PrimaryButtonText = "Save theme",
            SecondaryButtonText = editingCustom ? "Delete" : null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Secondary && editingCustom)
        {
            Services.CustomThemeStore.Remove(baseTheme.Name);
            RefreshThemeNames(select: App.Themes.All[0].Name);
            return;
        }
        if (result != ContentDialogResult.Primary) return;

        var name = (nameBox.Text ?? "").Trim();
        if (name.Length == 0) name = "My theme";
        if (App.Themes.IsBuiltin(name)) name += " (custom)"; // never shadow a stock theme name

        Services.CustomThemeStore.AddOrUpdate(new Models.ThemeDefinition(
            name,
            Hex(pickers[0].Color), Hex(pickers[1].Color), Hex(pickers[2].Color), Hex(pickers[3].Color),
            Hex(pickers[4].Color), Hex(pickers[5].Color), Hex(pickers[6].Color), Hex(pickers[7].Color)));
        RefreshThemeNames(select: name);
    }

    private void RefreshThemeNames(string select)
    {
        ViewModel.ThemeNames.Clear();
        foreach (var t in App.Themes.All) ViewModel.ThemeNames.Add(t.Name);
        ViewModel.SelectedThemeName = select; // triggers preview refresh
    }

    private void OnTakeTourClick(object sender, RoutedEventArgs e) => _ = ShowWelcomeTourAsync();

    // The first-run guided tour. Auto-shown once (gated on settings.HasSeenWelcome); the title-bar
    // tour button replays it. A borderless dialog hosting the WelcomeTour control, closed when the
    // tour raises Completed. Finishing (not skipping) can then load a sample document and hand off
    // to the TeachingTip walkthrough of the real controls, per the checkboxes on the final page.
    private async Task ShowWelcomeTourAsync()
    {
        Views.WelcomeTour? tour = null;
        try
        {
            tour = new Views.WelcomeTour();
            var dialog = new ContentDialog
            {
                Content = tour,
                XamlRoot = Content.XamlRoot,
                Padding = new Thickness(0),
            };
            // The tour card is 540 wide; the ContentDialog's default ContentDialogMaxWidth (~548)
            // leaves no room for the dialog's own chrome, so the right edge — the Next / Get started
            // button — was being clipped. Give it generous headroom in both dimensions so nothing
            // clips and no scrollbar appears to overlap the buttons.
            dialog.Resources["ContentDialogMaxWidth"] = 760.0;
            dialog.Resources["ContentDialogMinWidth"] = 560.0;
            dialog.Resources["ContentDialogMaxHeight"] = 940.0;
            tour.Completed += (_, _) => dialog.Hide();
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Never let the tour crash the app — but never hide the failure either. A silent
            // catch here shipped a dead "?" button once already.
            ViewModel.StatusText = $"Tour failed to open: {ex.GetType().Name}: {ex.Message}";
        }

        if (!App.Settings.Current.HasSeenWelcome)
        {
            App.Settings.Current.HasSeenWelcome = true;
            App.Settings.Save();
        }

        if (tour?.LoadSampleRequested == true) LoadSampleDocument();
    }

    // A showcase document for the tour: something in every direction the app is good at —
    // detection-worthy prose, a table, KaTeX math, a Mermaid diagram (built-in), and a PlantUML
    // fence. If the PlantUML plugin isn't installed the preview shows its "install this plugin"
    // affordance instead, which is itself worth discovering. Only offered from the tour, and only
    // replaces the editor when the user hasn't typed anything (never clobber real work).
    private const string SampleMarkdown = """
        # Quarterly Review — Sample Document

        This is a **sample** so you can try MarkSmith without hunting for a Markdown file.
        Restyle it on the right, then hit **Generate PDF** below.

        > [!TIP]
        > Everything here survives export: the table, the math, and the diagrams.
        
        ---
        
        # Table of Contents
        
        - [Data Tables](#data-tables)
        - [Math](#math)
        - [Diagrams](#diagrams)
        - [Formatting & Code](#formatting--code)
        - [Definition Lists](#definition-lists)
        - [Task Lists](#task-lists)
        - [Admonitions](#admonitions)

        ---

        ## Data Tables

        | Region | Revenue | Change |
        |--------|---------|--------|
        | APAC   | $4.2M   | +12%   |
        | EU     | $3.1M   | +5%    |
        | US     | $5.5M   | +9%    |

        ---
        
        ## Math

        Reserves follow $R = \sum_{i=1}^{n} p_i \cdot L_i$ — and in Word export this becomes a
        real, editable equation, not a picture.
        
        Block equations work too:
        $$
        \begin{bmatrix}
        1 & 2 & 3 \\
        4 & 5 & 6 \\
        7 & 8 & 9
        \end{bmatrix}
        $$

        ---
        
        ## Diagrams

        ### Mermaid Flowchart

        ```mermaid
        flowchart LR
          A[Paste a chat] --> B{MarkSmith}
          B --> C[Polished PDF]
          B --> D[Editable Word]
        ```

        ### PlantUML Sequence

        ```plantuml
        @startuml
        You -> MarkSmith: paste markdown
        MarkSmith --> You: finished document
        @enduml
        ```
        
        ### Graphviz
        
        ```graphviz
        digraph G {
            A -> B;
            A -> C;
            B -> D;
            C -> D;
        }
        ```
        
        Six diagram languages render from plain code fences — Mermaid is built in, and PlantUML,
        Graphviz, D2, Typst and Vega-Lite are one-click installs in **Settings → Plugins**.
        
        ---
        
        ## Formatting & Code
        
        *Italic*, **Bold**, ***Bold Italic***, ~~Strikethrough~~, ==Highlight==, and `Inline code`.
        Subscript: H~2~O | Superscript: X^2^
        
        ```python
        def hello_world():
            print("Syntax highlighting works!")
        ```
        
        ---
        
        ## Definition Lists
        
        MarkSmith
        : The tool you are using right now.
        
        Markdown
        : A lightweight markup language.
        
        ---
        
        ## Task Lists
        
        - [x] Completed task
        - [ ] Incomplete task
        
        ---
        
        ## Admonitions
        
        > [!WARNING]
        > This is a warning admonition.
        
        !!! note
            Python Markdown style admonitions work too!
            
        ---
        
        ## Try it yourself!
        
        Try editing this markdown in the textbox on the left to see the live preview instantly update.
        """;

    private void LoadSampleDocument()
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.PastedMarkdown)) return;
        ViewModel.UsePasteSource = true;
        ViewModel.PastedMarkdown = SampleMarkdown;
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settingsView = new Views.SettingsView();
        // Installing/removing a diagram engine changes what the open document can render, so re-run
        // the live preview (heavy path re-invokes the plugin renderers) as soon as it happens —
        // even while the Settings dialog is still open, so the change is visible the moment it closes.
        settingsView.PluginsChanged += () => DispatcherQueue.TryEnqueue(() => _ = RefreshPreviewAsync(heavy: true));
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = settingsView,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // ---- Automation (clipboard watcher / folder watcher / REST API) ----

    private void ApplyAutomationSettings()
    {
        _automationManager.ApplyAutomationSettings(
            ViewModel,
            () => _clipboardIngest.Start(),
            () => _clipboardIngest.Stop(),
            _clipboardIngest.IsRunning,
            folder => _folderIngest.Start(folder),
            () => _folderIngest.Stop(),
            _folderIngest.IsRunning,
            status => ApiUrlText.Text = status);
    }

    // Tray icon is created in code, not markup — the WASDK 1.6 XAML compiler crashes on
    // H.NotifyIcon's XAML types, but consuming them from C# works fine.
    private void InitTrayIcon()
    {
        try
        {
            var menu = new MenuFlyout();
            menu.Items.Add(new MenuFlyoutItem { Text = "Open MarkSmith", Command = ShowWindowCommand });
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = ExitApplicationCommand });

            _trayIcon = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "MarkSmith",
                IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/tray.png")),
                ContextMenuMode = H.NotifyIcon.ContextMenuMode.SecondWindow,
                NoLeftClickDelay = true,
                LeftClickCommand = ShowWindowCommand,
                ContextFlyout = menu,
            };
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            _trayIcon = null; // tray is a convenience — never block startup on it
            System.Diagnostics.Debug.WriteLine($"Tray icon unavailable: {ex.Message}");
        }
    }

    private void OnExtensionTipClosed(object sender, object args) => ViewModel.ShowExtensionTip = false;

    // Clipboard / API / extension ingests land here. Always update the UI; when "auto-generate PDF
    // from AI-chat ingests" is on, also export a PDF — so the extension sending a conversation at
    // its end produces a finished document with no clicks.
    private void IngestFromSource(string text, string origin, Models.OutputOverride? output = null)
    {
        // Source-page metadata (font, definitive source, model, title, language/direction, brand
        // accent) is applied inside IngestMarkdown so the live preview reflects it immediately —
        // see MainViewModel.IngestMarkdown.
        ViewModel.IngestMarkdown(text, origin, output);
        if (!ViewModel.AutoConvertIngests) return;
        if (App.License.CanAutomate) _ = AutoExportIngestAsync(output);
        else
        {
            ViewModel.StatusText = "Hands-free auto-convert is a MarkSmith Pro feature. The content is ready — export it manually, or upgrade in Settings ⚙.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
        }
    }

    private static string[] ParseFormats(string? format) => Services.ExportCoordinator.ParseFormats(format);

    private async Task AutoExportIngestAsync(Models.OutputOverride? output)
    {
        await _exportCoordinator.AutoExportIngestAsync(
            ViewModel,
            output,
            this,
            () => new OffscreenScope(this),
            ShowPdfToast,
            () => RefreshPreviewAsync());
    }

    private async Task OnWatchedFileAsync(string path)
    {
        await _exportCoordinator.OnWatchedFileAsync(
            ViewModel,
            path,
            this,
            () => new OffscreenScope(this),
            ShowPdfToast,
            () => RefreshPreviewAsync());
    }

    private static void ShowPdfToast(string pdfPath)
    {
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText("PDF ready")
                .AddText(Path.GetFileName(pdfPath))
                .AddArgument("action", "open")
                .AddArgument("path", pdfPath)
                .AddButton(new AppNotificationButton("Open PDF")
                    .AddArgument("action", "open").AddArgument("path", pdfPath))
                .AddButton(new AppNotificationButton("Show in folder")
                    .AddArgument("action", "folder").AddArgument("path", pdfPath))
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch
        {
            // Toasts are best-effort (notifications can be disabled system-wide); the InfoBar still reports.
        }
    }

    private void OnHistoryItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Models.HistoryEntry entry) return;
        if (File.Exists(entry.OutputPath))
        {
            var ext = System.IO.Path.GetExtension(entry.OutputPath).ToLowerInvariant();
            if (ext is ".pdf" or ".docx" or ".pptx" or ".epub" or ".md")
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.OutputPath) { UseShellExecute = true });
            }
            else
            {
                ViewModel.StatusText = $"Blocked opening untrusted file type: {ext}";
                ViewModel.StatusSeverity = Models.StatusSeverity.Error;
            }
        }
        else
        {
            ViewModel.StatusText = $"File no longer exists: {entry.OutputPath}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
        }
    }

    private async void OnBrowseWatchFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.WatchFolder = folder.Path;
    }

    // Batch: convert every .md in a chosen folder to a themed PDF, one by one through the same
    // classify → normalize → render pipeline the watched folder uses. Pro (automation) feature.
    private async void OnBatchConvertClick(object sender, RoutedEventArgs e)
    {
        if (!App.License.CanAutomate)
        {
            ViewModel.StatusText = "Batch conversion is a MarkSmith Pro feature. Upgrade in Settings ⚙.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var files = Directory.GetFiles(folder.Path, "*.md", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            ViewModel.StatusText = $"No .md files found in {folder.Path}.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }

        // Which format? PDF/DOCX/PPTX/EPUB — the same choice the individual exports offer.
        var fmt = await AskBatchFormatAsync(files.Length);
        if (fmt is null) return;
        var docxGated = fmt is "docx" && !App.License.CanExportDocx;
        if (docxGated) { ViewModel.StatusText = "Word export is a MarkSmith Pro feature."; ViewModel.StatusSeverity = Models.StatusSeverity.Warning; return; }

        if (fmt == "pdf" && !await EnsurePreviewWebViewAsync())
        {
            ViewModel.StatusText = "Batch failed: the preview engine couldn't start. Try again.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
            return;
        }

        try
        {
            var result = await _exportCoordinator.BatchConvertForApiAsync(
                ViewModel,
                folder.Path,
                fmt,
                null,
                this,
                () => new OffscreenScope(this),
                () => RefreshPreviewAsync(false));

            var propDone = result.GetType().GetProperty("done")?.GetValue(result);
            var propFailed = result.GetType().GetProperty("failed")?.GetValue(result);
            var propFolder = result.GetType().GetProperty("outputFolder")?.GetValue(result);

            int done = propDone is int d ? d : 0;
            int failed = propFailed is int f ? f : 0;
            string outFolder = propFolder is string s ? s : App.Settings.Current.OutputFolder;

            ViewModel.StatusText = failed == 0
                ? $"Batch done: {done} {fmt.ToUpperInvariant()} file{(done == 1 ? "" : "s")} in {outFolder}"
                : $"Batch done: {done} converted, {failed} failed — see {outFolder}";
            ViewModel.StatusSeverity = failed == 0 ? Models.StatusSeverity.Success : Models.StatusSeverity.Warning;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Batch convert failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    // Ask which format to batch-convert to; returns "pdf"/"docx"/"pptx"/"epub" or null if cancelled.
    private async Task<string?> AskBatchFormatAsync(int count)
    {
        var combo = new ComboBox { SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
        combo.Items.Add("PDF");
        combo.Items.Add("Word (DOCX)");
        combo.Items.Add("PowerPoint (PPTX)");
        combo.Items.Add("EPUB");
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Batch convert {count} file{(count == 1 ? "" : "s")}",
            Content = new StackPanel { Children = { new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Every .md file in the folder is converted to your chosen format, using the current Style settings." }, combo } },
            PrimaryButtonText = "Convert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return combo.SelectedIndex switch { 1 => "docx", 2 => "pptx", 3 => "epub", _ => "pdf" };
    }

    private async void OnBrowseBrandLogoClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) ViewModel.BrandLogoPath = file.Path;
    }

    private async void OnBrowseRunningDocClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "AI-notebook" };
        picker.FileTypeChoices.Add("Word document", new List<string> { ".docx" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is not null) ViewModel.RunningDocPath = file.Path;
    }

    private async Task<byte[]> ConvertForApiAsync(string markdown, Models.OutputOverride? output)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var bytes = await _exportCoordinator.ConvertForApiAsync(
                    ViewModel,
                    markdown,
                    output,
                    this,
                    () => new OffscreenScope(this),
                    () => RefreshPreviewAsync());
                tcs.SetResult(bytes);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return await tcs.Task;
    }

    private async Task<object> BatchConvertForApiAsync(string folderPath, string format, Models.OutputOverride? ovr)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var result = await _exportCoordinator.BatchConvertForApiAsync(
                    ViewModel,
                    folderPath,
                    format,
                    ovr,
                    this,
                    () => new OffscreenScope(this),
                    () => RefreshPreviewAsync(false));
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return await tcs.Task;
    }

    // ---- Source panel (File | Paste) ----

    private void OnSourceSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // ViewModel.UsePasteSource = sender.SelectedItem == PasteTab;
    }

    private void SyncSourcePanels()
    {
        // var paste = ViewModel.UsePasteSource;
        // SourceSelector.SelectedItem = paste ? PasteTab : FileTab;
        // FilePanel.Visibility = paste ? Visibility.Collapsed : Visibility.Visible;
        // PastePanel.Visibility = paste ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSourceDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = "Drop to load";
            }
        }
    }

    private async void OnSourceDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        // Importer plugins (e.g. Pandoc) widen what "a droppable document" means — see
        // MdToPdf.Core/Plugins/PluginFileReader for where the conversion happens on read.
        var importerExts = App.Plugins.AllImporterExtensions;
        var file = items.OfType<StorageFile>()
            .FirstOrDefault(f => f.FileType is ".md" or ".markdown" or ".txt"
                || importerExts.Contains(f.FileType.TrimStart('.').ToLowerInvariant()));
        if (file is not null)
        {
            ViewModel.InputFilePath = file.Path;
            ViewModel.UsePasteSource = false;
        }
    }

    private async void OnBrowseFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".markdown");
        foreach (var ext in App.Plugins.AllImporterExtensions) picker.FileTypeFilter.Add("." + ext);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.InputFilePath = file.Path;
            ViewModel.UsePasteSource = false;
        }
    }

    private async void OnBrowseLogoClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.BrandLogoPath = file.Path;
        }
    }

    private void OnMarkdownFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: Services.MarkdownFileEntry entry })
            ViewModel.LoadRecentCommand.Execute(entry.Path);
    }

    private async void OnRescanMarkdownClick(object sender, RoutedEventArgs e)
    {
        await LoadMarkdownFilesAsync();
    }

    // Discover .md files across the user's common folders; drives the Step 1 picker. Toggles the
    // inline "Scanning…" hint and disables Rescan while it runs.
    private async Task LoadMarkdownFilesAsync()
    {
        RescanButton.IsEnabled = false;
        ScanningLabel.Visibility = Visibility.Visible;
        try { await ViewModel.RefreshMarkdownFilesAsync(); }
        finally
        {
            ScanningLabel.Visibility = Visibility.Collapsed;
            RescanButton.IsEnabled = true;
        }
    }

    // ---- Export ----

    private async void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.OutputFolder = folder.Path;
    }

    // The "call to user" for a page-dominating diagram: keep mermaid's exact layout (Web Layout view)
    // or reflow it to fit the printed page. Returns 1 = exact, 2 = reflow. Persists if "remember".
    public async Task<int> AskOversizedDiagramModeAsync()
    {
        var remember = new CheckBox { Content = "Remember my choice (change later in Settings)", Margin = new Thickness(0, 14, 0, 0) };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "This document has a large diagram that won't fit a printed page. How should MarkSmith put it into Word?"
        });

        var rbGroup = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var rbExact = new RadioButton { Content = "Keep exact layout (Opens in Web Layout view)", IsChecked = true, Tag = 1 };
        var rbReflow = new RadioButton { Content = "Reflow to fit page (Uniform scale)", Tag = 2 };
        var rbCompactSpace = new RadioButton { Content = "Compact spacing (Shrink gaps first)", Tag = 6 };
        var rbCompactShapes = new RadioButton { Content = "Compact shapes (Shrink shapes first)", Tag = 7 };
        var rbUltraCompact = new RadioButton { Content = "Ultra compact (Shrink both equally)", Tag = 8 };

        rbGroup.Children.Add(rbExact);
        rbGroup.Children.Add(rbReflow);
        rbGroup.Children.Add(rbCompactSpace);
        rbGroup.Children.Add(rbCompactShapes);
        rbGroup.Children.Add(rbUltraCompact);

        body.Children.Add(rbGroup);
        body.Children.Add(remember);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Large diagram",
            Content = body,
            PrimaryButtonText = "OK",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return 1; // default to exact on cancel

        int mode = 1;
        if (rbReflow.IsChecked == true) mode = 2;
        else if (rbCompactSpace.IsChecked == true) mode = 6;
        else if (rbCompactShapes.IsChecked == true) mode = 7;
        else if (rbUltraCompact.IsChecked == true) mode = 8;

        if (remember.IsChecked == true)
        {
            App.Settings.Current.OversizedDiagramMode = mode;
            App.Settings.Save();
            ViewModel.OversizedDiagramMode = mode; // keep the Settings UI in sync
        }
        return mode;
    }

    private void OnOpenOutputClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.LastOutputPath is { } path && File.Exists(path))
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".pdf" or ".docx" or ".pptx" or ".epub" or ".md")
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
    }

    // ---- Preview ----

    private async Task InitializePreviewAsync()
    {
        await PreviewWebView.EnsureCoreWebView2Async();
        MapAssetHost(PreviewWebView.CoreWebView2);
        
        // Intercept local image requests and serve them directly from disk.
        PreviewWebView.CoreWebView2.AddWebResourceRequestedFilter("https://localimg.marksmith/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Image);
        PreviewWebView.CoreWebView2.WebResourceRequested += OnLocalImageRequested;

        // The preview auto-refreshes on every change (debounced). Navigation completing satisfies
        // one of the two conditions to hide the spinner; the minimum-time gate satisfies the other.
        PreviewWebView.CoreWebView2.NavigationCompleted += (_, _) => _spinNavDone = true;
        PreviewWebView.CoreWebView2.WebMessageReceived += OnPreviewWebMessage;
        await RefreshPreviewAsync();
    }

    private void OnLocalImageRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            var q = uri.Query;
            if (!q.StartsWith("?path="))
            {
                e.Response = PreviewWebView.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }

            var path = Uri.UnescapeDataString(q.Substring(6));
            if (!File.Exists(path))
            {
                e.Response = PreviewWebView.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
                ".webp" => "image/webp", ".bmp" => "image/bmp", ".svg" => "image/svg+xml", _ => "application/octet-stream",
            };

            var stream = File.OpenRead(path).AsRandomAccessStream();
            e.Response = PreviewWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                stream, 200, "OK", $"Content-Type: {mime}");
        }
        catch
        {
            e.Response = PreviewWebView.CoreWebView2.Environment.CreateWebResourceResponse(null, 500, "Error", "");
        }
    }

    // The focused diagram viewer posts {type:"save-diagram", format, data} when the user clicks
    // PNG/SVG; show a save picker and write the file. Best-effort — a bad message is ignored.
    private async void OnPreviewWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(json)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            if (type == "mermaid-error")
            {
                var error = root.GetProperty("error").GetString() ?? "Unknown parse error";
                System.Diagnostics.Debug.WriteLine($"[Mermaid Error] {error}");
                ViewModel.StatusText = $"Mermaid Syntax Error: {error}";
                ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
                return;
            }
            if (type == "launch-mermaid-studio" || type == "edit-mermaid-code")
            {
                var code = root.TryGetProperty("code", out var cProp) ? cProp.GetString() : "";
                var idx = root.TryGetProperty("index", out var iProp) ? iProp.GetInt32() : 0;
                DispatcherQueue.TryEnqueue(() => _ = ShowMermaidDiagramStudioWindowAsync(code ?? "", idx));
                return;
            }
            if (type == "mermaid-node-edit")
            {
                var oldText = root.GetProperty("oldText").GetString();
                var newText = root.GetProperty("newText").GetString();
                if (!string.IsNullOrEmpty(oldText) && !string.IsNullOrEmpty(newText) && oldText != newText)
                {
                    var currentMd = ViewModel.CurrentMarkdown ?? "";
                    if (currentMd.Contains(oldText))
                    {
                        ViewModel.CurrentMarkdown = currentMd.Replace(oldText, newText);
                        ViewModel.StatusText = $"Diagram label updated: '{oldText}' → '{newText}'";
                        ViewModel.StatusSeverity = Models.StatusSeverity.Success;
                    }
                }
                return;
            }
            if (type == "page-overflow")
            {
                var elements = root.GetProperty("elements");
                var desc = string.Join(", ", elements.EnumerateArray().Select(el => el.GetProperty("element").GetString()));
                System.Diagnostics.Debug.WriteLine($"[Page Overflow] Elements overflow: {desc}");
                ViewModel.StatusText = $"Page Overflow: {desc} exceed page width.";
                ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
                return;
            }
            if (type != "save-diagram") return;

            var format = root.GetProperty("format").GetString() ?? "png";
            var data = root.GetProperty("data").GetString() ?? "";

            var picker = new FileSavePicker { SuggestedFileName = "diagram" };
            picker.FileTypeChoices.Add(format.ToUpperInvariant() + " image", new List<string> { "." + format });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            if (format == "svg")
                await File.WriteAllTextAsync(file.Path, data);
            else
            {
                var b64 = data.Contains(',') ? data[(data.IndexOf(',') + 1)..] : data;
                await File.WriteAllBytesAsync(file.Path, Convert.FromBase64String(b64));
            }
            ViewModel.StatusText = $"Diagram saved: {file.Path}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Error processing message: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    private async Task ShowMermaidDiagramStudioWindowAsync(string sampleCode, int targetIndex)
    {
        var currentMd = ViewModel.CurrentMarkdown ?? "";
        
        var studioWindow = new Views.Mermaid.MermaidDiagramStudioWindow(currentMd, targetIndex);
        studioWindow.SyncToMarkdownRequested += async (s, markdown) =>
        {
            ViewModel.CurrentMarkdown = studioWindow.ViewModel.SyncToMarkdown(ViewModel.CurrentMarkdown ?? "");
            ViewModel.StatusText = "Mermaid diagram code updated via Visual Studio.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
            await RefreshPreviewAsync(heavy: true);
        };
        studioWindow.Activate();
        await Task.CompletedTask;
    }

    // Serve the bundled web assets (mermaid, KaTeX, highlight.js) from a real https origin so
    // NavigateToString pages can load them — no CDN, works offline. Referenced as
    // https://{Services.WebAssets.Host}/mermaid.min.js etc.
    private static void MapAssetHost(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "web");
        if (!Directory.Exists(dir)) return;
        try
        {
            core.SetVirtualHostNameToFolderMapping(
                Services.WebAssets.Host, dir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        }
        catch { /* mapping already set or unavailable — CDN fallback in the HTML still works */ }
    }

    // The WebView initializes asynchronously after launch, but headless work (auto-generate on
    // ingest, watched files, API convert, batch) can arrive first — "WebView2 is not initialized".
    // Every headless caller awaits this before rendering.
    private async Task<bool> EnsurePreviewWebViewAsync()
    {
        if (PreviewWebView.CoreWebView2 is not null) return true;
        try { await PreviewWebView.EnsureCoreWebView2Async(); } catch { return false; }
        return PreviewWebView.CoreWebView2 is not null;
    }

    // WebView2 doesn't reliably paint while the window is hidden (tray mode), which can yield blank
    // PDFs. For the duration of a headless render, present the window off-screen, unactivated, and
    // hidden from the taskbar/Alt-Tab — the user never sees a thing — then hide it again.
    private (bool Hidden, Windows.Graphics.PointInt32 Pos) BeginOffscreenRender()
    {
        if (AppWindow.IsVisible) return (false, default);
        var pos = AppWindow.Position;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        AppWindow.Show(false); // no activation — focus stays wherever the user has it
        return (true, pos);
    }

    private void EndOffscreenRender((bool Hidden, Windows.Graphics.PointInt32 Pos) state)
    {
        if (!state.Hidden) return;
        AppWindow.Hide();
        AppWindow.Move(state.Pos);
        AppWindow.IsShownInSwitchers = true;
    }

    private sealed class OffscreenScope : IDisposable
    {
        private readonly MainWindow _window;
        private readonly (bool Hidden, Windows.Graphics.PointInt32 Pos) _state;

        public OffscreenScope(MainWindow window)
        {
            _window = window;
            _state = window.BeginOffscreenRender();
        }

        public void Dispose()
        {
            _window.EndOffscreenRender(_state);
        }
    }

    // Called on every refresh. Starts the animated spinner if it isn't already running; otherwise
    // just marks the new navigation in-flight so it stays up until the latest render finishes.
    private void StartSpinner()
    {
        if (_spinActive) { _spinNavDone = false; return; }

        _spinActive = true;
        _spinNavDone = false;
        _spinPhase = 0;
        _spinMode = 1 - _spinMode; // alternate: spin, then figure-eight, then spin…

        SpinnerLogoTransform.TranslateX = 0;
        SpinnerLogoTransform.TranslateY = 0;
        SpinnerLogoTransform.Rotation = 0;

        PreviewSpinner.Visibility = Visibility.Visible;
        _spinTimer!.Start();
    }

    private void OnSpinTick()
    {
        _spinPhase += SpinDt;

        if (_spinMode == 0)
        {
            // Spin the logo about its centre (RenderTransformOrigin 0.5,0.5).
            SpinnerLogoTransform.Rotation = (_spinPhase * 300) % 360;
        }
        else
        {
            // Upright logo tracing a figure-eight (Gerono lemniscate: x = A sin t, y = (A/2) sin 2t).
            var t = _spinPhase * 2.4;
            const double a = 46;
            SpinnerLogoTransform.TranslateX = a * Math.Sin(t);
            SpinnerLogoTransform.TranslateY = a * 0.55 * Math.Sin(2 * t);
            SpinnerLogoTransform.Rotation = 0; // stays upright
        }

        if (_spinNavDone && _spinPhase >= SpinMinSec) HideSpinner();
    }

    private void HideSpinner()
    {
        _spinTimer!.Stop();
        _spinActive = false;
        PreviewSpinner.Visibility = Visibility.Collapsed;
        // Reveal the freshly-rendered content: the blur clears over a smooth transition as the sprite goes.
        _ = PreviewWebView.CoreWebView2?.ExecuteScriptAsync(
            "document.body && document.body.classList.remove('ms-loading')");
    }

    // ---- IWebRenderHost / IUiPrompts: the portable seam MainViewModel and MermaidHarvestService
    // (both in MdToPdf.Core) drive PDF export and mermaid harvesting through, instead of reaching
    // into a WebView2 control directly. See MdToPdf.Core/Rendering/IWebRenderHost.cs.

    public Task<bool> EnsureReadyAsync() => EnsurePreviewWebViewAsync();

    public Task NavigateToStringAsync(string html)
    {
        var core = PreviewWebView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        var tcs = new TaskCompletionSource();
        void OnNavigationCompleted(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnNavigationCompleted;
            tcs.TrySetResult();
        }
        core.NavigationCompleted += OnNavigationCompleted;
        core.NavigateToString(html);
        return tcs.Task;
    }

    public async Task<string?> ExecuteScriptAsync(string javaScript)
    {
        var core = PreviewWebView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        return await core.ExecuteScriptAsync(javaScript);
    }

    public async Task<bool> PrintToPdfAsync(string outputPath, Services.PdfPageSetup setup)
    {
        var core = PreviewWebView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        var printSettings = core.Environment.CreatePrintSettings();
        printSettings.ShouldPrintBackgrounds = setup.PrintBackgrounds;
        printSettings.ShouldPrintHeaderAndFooter = false;
        printSettings.PageWidth = setup.PageWidthIn;
        printSettings.PageHeight = setup.PageHeightIn;
        printSettings.MarginTop = setup.MarginTopIn;
        printSettings.MarginBottom = setup.MarginBottomIn;
        printSettings.MarginLeft = setup.MarginLeftIn;
        printSettings.MarginRight = setup.MarginRightIn;
        return await core.PrintToPdfAsync(outputPath, printSettings);
    }

    // The mermaid render page must not be clobbered by the live preview's debounced auto-refresh
    // while a harvest is in flight; restore the live preview once the harvest ends. Exactly the
    // wrapper the three harvest methods used to inline before their bodies moved to
    // MdToPdf.Core/Services/MermaidHarvestService.cs.
    public Task BeginHarvestAsync()
    {
        _mermaidHarvestActive = true;
        _previewDebounce.Stop();
        return Task.CompletedTask;
    }

    public async Task EndHarvestAsync()
    {
        _mermaidHarvestActive = false;
        await RefreshPreviewAsync(false);
    }

    private async Task RefreshPreviewAsync(bool heavy = true)
    {
        if (PreviewWebView.CoreWebView2 is null) return;
        if (_mermaidHarvestActive) return; // snapshot renderer owns the WebView right now

        if (_isPreviewRefreshing)
        {
            _pendingPreviewRefresh = true;
            return;
        }

        _isPreviewRefreshing = true;
        _pendingPreviewRefresh = false;

        try
        {
            if (heavy) StartSpinner();

            var vm = ViewModel;
            string markdown;
            if (vm.UsePasteSource)
            {
                markdown = vm.PastedMarkdown;
            }
            else if (!string.IsNullOrWhiteSpace(vm.InputFilePath) && File.Exists(vm.InputFilePath))
            {
                // Offload file reading to background thread to prevent UI stutter
                markdown = await Task.Run(async () => await Plugins.PluginFileReader.ReadAsMarkdownAsync(vm.InputFilePath));
            }
            else
            {
                markdown = "# MarkSmith\n\nDrop a Markdown file on **1 · Source**, or switch to **Paste** and start typing.";
            }

            // Memoization: skip if the markdown hasn't changed AND this isn't a heavy refresh (e.g. theme change)
            if (!heavy && markdown == _lastRenderedMarkdown)
            {
                return;
            }

            // Move the heavy markdown parsing and HTML generation to a background thread
            var html = await Task.Run(() => 
            {
                var prepped = vm.PrepareMarkdown(markdown);
                return vm.BuildPreviewHtml(prepped, interactive: true);
            });
            
            _lastRenderedMarkdown = markdown;

            if (vm.IsDebugModeEnabled)
            {
                try
                {
                    var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith", "DebugLogs");
                    Directory.CreateDirectory(logsDir);
                    var logFile = Path.Combine(logsDir, $"Preview_Session_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("n").Substring(0, 4)}.log");
                    
                    var prompt = "Tell me everything wrong with the way we displayed the MD format in this HTML and how to resolve it:\n\n";
                    File.WriteAllText(logFile, prompt + html);
                    _sessionLogFiles.Add(logFile);
                }
                catch { }
            }

            // Heavy refreshes render blurred, then unblur when the spinner clears (see HideSpinner).
            if (heavy) html = html.Replace("<body>", "<body class=\"ms-loading\">");
            PreviewWebView.CoreWebView2.NavigateToString(html);
        }
        finally
        {
            _isPreviewRefreshing = false;
            if (_pendingPreviewRefresh)
            {
                // Trigger another refresh if changes happened while we were rendering
                _previewDebounce.Start();
            }
        }
    }

    private void OnToggleDebugModeInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.IsDebugModeEnabled = !ViewModel.IsDebugModeEnabled;
        ViewModel.StatusText = ViewModel.IsDebugModeEnabled ? "Debug Mode Enabled (HTML logging active)" : "Debug Mode Disabled";
        ViewModel.StatusSeverity = ViewModel.IsDebugModeEnabled ? Models.StatusSeverity.Warning : Models.StatusSeverity.Informational;
        args.Handled = true;
    }

    private async Task ShowDebugLogsDialogAndExitAsync()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var file in _sessionLogFiles)
        {
            if (File.Exists(file))
            {
                sb.AppendLine($"--- LOG FILE: {Path.GetFileName(file)} ---");
                sb.AppendLine(File.ReadAllText(file));
                sb.AppendLine();
            }
        }

        var textBox = new TextBox
        {
            Text = sb.ToString(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 400
        };
        ScrollViewer.SetVerticalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);

        var dialog = new ContentDialog
        {
            Title = "Debug Mode - Session Logs",
            Content = textBox,
            CloseButtonText = "Close and Exit",
            XamlRoot = RootGrid.XamlRoot
        };

        await dialog.ShowAsync();
        
        _exitRequested = true;
        Close();
    }

    private async void OnConvertPdfClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToPdfAsync();
    }

    private async void OnConvertDocxClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToDocxAsync();
    }

    private void OnCenterViewSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ViewPreviewTab == null) return;
        var isPreview = sender.SelectedItem == ViewPreviewTab;
        if (PastePanel != null) PastePanel.Visibility = isPreview ? Visibility.Collapsed : Visibility.Visible;
        if (PreviewCard != null) PreviewCard.Visibility = isPreview ? Visibility.Visible : Visibility.Collapsed;
        if (PreviewWidthContainer != null) PreviewWidthContainer.Visibility = isPreview ? Visibility.Visible : Visibility.Collapsed;
        if (isPreview)
        {
            _ = RefreshPreviewAsync(heavy: true);
        }
    }

    private void InsertMarkdown(string prefix, string suffix = "")
    {
        var tb = PasteTextBox;
        if (tb == null) return;
        try
        {
            var selected = tb.SelectedText;
            tb.SelectedText = prefix + selected + suffix;
            if (string.IsNullOrEmpty(selected))
            {
                tb.SelectionStart = tb.SelectionStart - suffix.Length;
                tb.SelectionLength = 0;
            }
            tb.Focus(FocusState.Programmatic);
        }
        catch
        {
            int selStart = tb.SelectionStart;
            int selLen = tb.SelectionLength;
            string text = tb.Text ?? "";
            string sel = text.Substring(selStart, selLen);
            string rep = prefix + sel + suffix;
            tb.Text = text.Remove(selStart, selLen).Insert(selStart, rep);
            tb.SelectionStart = selStart + prefix.Length;
            tb.SelectionLength = sel.Length;
            tb.Focus(FocusState.Programmatic);
        }
    }

    private void OnBoldClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("**", "**");
    }

    private void OnItalicClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("*", "*");
    }

    private void OnStrikethroughClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("~~", "~~");
    }

    private void OnCodeBlockClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n```\n", "\n```\n");
    }

    private void OnBlockquoteClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("> ", "");
    }

    private void OnInsertWorkflowClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::workflow\n- Step 1\n- Step 2\n- Step 3\n:::\n");
    }

    private void OnInsertTimelineClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::timeline\n- 2020: Started\n- 2023: Progress\n- 2026: Done\n:::\n");
    }

    private void OnInsertSmartArtClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::smartart type=\"process\"\n- Step 1\n- Step 2\n:::\n");
    }

    private void OnInsertTabsClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::tabs\n=== Tab 1\nContent 1\n=== Tab 2\nContent 2\n:::\n");
    }

    private void OnInsertColumnsClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::columns count=\"2\"\nColumn 1 content\n===\nColumn 2 content\n:::\n");
    }

    private void OnInsertCanvasClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::canvas\n<svg viewBox=\"0 0 100 100\" width=\"200\" height=\"200\">\n  <circle cx=\"50\" cy=\"50\" r=\"40\" stroke=\"black\" stroke-width=\"3\" fill=\"red\" />\n</svg>\n:::\n");
    }

    private void OnBulletListClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("- ", "");
    }

    private void OnNumberedListClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("1. ", "");
    }

    private void OnTaskListClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("- [ ] ", "");
    }

    private void OnH1Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("# ", "");
    }

    private void OnH2Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("## ", "");
    }

    private void OnH3Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("### ", "");
    }

        private void OnH4Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("#### ", "");
    }

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("[", "](url)");
    }

    private void OnImageClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("![", "](image.png)");
    }

    private void OnTableClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n| Header 1 | Header 2 |\n| --- | --- |\n| Value 1 | Value 2 |\n");
    }

    private void OnInsertEmbedClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::embed provider=\"youtube\" src=\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\"\n:::\n");
    }

    private void OnInsertChartClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::chart type=\"bar\"\nQ1,10\nQ2,25\nQ3,15\n:::\n");
    }

    private void OnInsertDatagridClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::datagrid\nlabel,value\nQ1,10\nQ2,25\n:::\n");
    }

    private void OnInsertReferencesClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::references\n@paper-id\nauthor: Author Name\ntitle: Publication Title\nyear: 2026\n:::\n");
    }

    private void OnInsertAiContextClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::ai-context\npromptHash: abc123\nmodel: Gemini Pro\ntimestamp: " + DateTime.Now.ToString("yyyy-MM-dd") + "\n:::\n");
    }

    private void OnOpenMermaidStudioClick(object sender, RoutedEventArgs e)
    {
        var studioWindow = new Views.Mermaid.MermaidDiagramStudioWindow(ViewModel.CurrentMarkdown);
        studioWindow.SyncToMarkdownRequested += (s, markdown) =>
        {
            ViewModel.CurrentMarkdown = studioWindow.ViewModel.SyncToMarkdown(ViewModel.CurrentMarkdown);
        };
        studioWindow.Activate();
    }

    public Task<MdToPdf.Models.RenderOption?> ShowAmbiguityResolverDialogAsync(MdToPdf.Models.AmbiguityCase ambiguity)
    {
        return Task.FromResult<MdToPdf.Models.RenderOption?>(null);
    }
}

