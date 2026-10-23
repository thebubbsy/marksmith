using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MarkSmith.Services;
using MarkSmith.ViewModels.Mermaid;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;

namespace MarkSmith.Views.Mermaid;

public sealed partial class MermaidDiagramStudioControl : UserControl
{
    public MermaidStudioViewModel? ViewModel => DataContext as MermaidStudioViewModel;

    public UIElement TitleBarElement => AppTitleBar;

    public event EventHandler<string>? SyncToMarkdownRequested;

    // Kept so the keyboard accelerators (zoom in/out/reset, Ctrl+F) can drive the canvas and palette directly.
    private readonly MermaidCanvasControl _canvas;
    private readonly NodePaletteControl _palette;

    // ---- Bidirectional Code <-> Canvas sync (mermaid.live-style) --------------------------
    // True while we are writing CodeEditorTextBox.Text ourselves (canvas -> code push) so the
    // TextChanged handler doesn't treat it as a user edit and echo it back into the canvas.
    private bool _programmaticCodeUpdate;
    // Set when the user types in the Code pane; the debounce timer then pushes code -> canvas.
    private bool _codeDirty;
    // The canonical generated code that the canvas currently represents. When the canvas drifts
    // from this (node drag, palette add, align, …) and the Code pane isn't being edited, we push
    // the fresh code into the editor.
    private string _lastPushedCode = string.Empty;
    // Debounce timer so we don't re-parse on every keystroke.
    private readonly DispatcherTimer _codeSyncTimer;

    // ---- Rendered Mermaid preview (WebView2 + bundled mermaid.min.js) ----------------------
    // The PREVIEW tab shows the canonical mermaid.js rendering of the current diagram — exactly
    // what lands in the exported PDF/HTML. The WebView2 host is created lazily the first time the
    // tab is opened (WebView2 startup is expensive) and re-rendered, debounced, whenever the
    // generated code changes.
    private WebView2? _previewWebView;
    private bool _previewInitStarted;                 // guard against double initialization
    private bool _previewReady;                       // host page loaded, __renderDiagram defined
    private bool _previewDirty = true;                // diagram changed since the last render
    private bool _previewRenderInFlight;              // guard against overlapping renders
    private string _lastPreviewCode = string.Empty;   // code currently shown in the preview

    public MermaidDiagramStudioControl()
    {
        InitializeComponent();
        // "Right-click for options" menus: open them from Shift+F10 / the Menu key too.
        KeyboardContextMenu.BridgeAll(
            (TemplatesButton, OnTemplatesButtonRightTapped),
            (AutoLayoutButton, OnAutoLayoutButtonRightTapped),
            (GridSnapToggle, OnGridSnapToggleRightTapped),
            (AlignButton, OnAlignButtonRightTapped),
            (UndoToolbarButton, OnUndoButtonRightTapped),
            (RedoToolbarButton, OnRedoButtonRightTapped),
            (ExportSvgButton, OnExportSvgButtonRightTapped),
            (CopySvgButton, OnCopySvgButtonRightTapped),
            (SyncToMarkdownButton, OnSyncToMarkdownButtonRightTapped),
            (CanvasSummaryButton, OnStatusBarRightTapped));
        // The Code pane's options menu opens from right-click, Shift+F10 and the Menu key, and a
        // selection keeps the box's own Cut / Copy / Paste menu.
        TextBoxMenu.Attach(CodeEditorTextBox, FillCodeEditorMenu);

        _palette = new NodePaletteControl();
        PaletteContainer.Child = _palette;
        _canvas = new MermaidCanvasControl();
        CanvasContainer.Child = _canvas;
        // The palette rows said "Add shape to canvas" but only dragging did anything.
        _palette.ShapeRequested += (_, item) => _canvas.AddInView(item);
        _palette.ReturnFocusToCanvasRequested += (_, _) => _canvas.FocusCanvas();

        // Ctrl+= and Ctrl+- on the main keyboard (the XAML pair only covers the numpad keys, so
        // the shortcuts the zoom tooltips name did nothing on a laptop).
        foreach (var (key, zoomIn) in new[] { ((Windows.System.VirtualKey)187, true), ((Windows.System.VirtualKey)189, false) })
        {
            var accel = new KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Control };
            accel.Invoked += zoomIn ? OnZoomInAcceleratorInvoked : OnZoomOutAcceleratorInvoked;
            KeyboardAccelerators.Add(accel);
        }

        // Ctrl+] / Ctrl+[ / Ctrl+Shift+] / Ctrl+Shift+[ z-ordering shortcuts (OEM_6 = 221 ']', OEM_4 = 219 '[').
        foreach (var (key, shift, forward) in new[]
        {
            ((Windows.System.VirtualKey)221, false, true),
            ((Windows.System.VirtualKey)219, false, false),
            ((Windows.System.VirtualKey)221, true, true),
            ((Windows.System.VirtualKey)219, true, false)
        })
        {
            var mods = shift
                ? Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift
                : Windows.System.VirtualKeyModifiers.Control;
            var accel = new KeyboardAccelerator { Key = key, Modifiers = mods };
            bool isShift = shift, isForward = forward;
            accel.Invoked += (_, args) =>
            {
                if (IsEditingText()) return;
                if (isForward)
                {
                    if (isShift) _canvas.BringToFront();
                    else _canvas.BringForward();
                }
                else
                {
                    if (isShift) _canvas.SendToBack();
                    else _canvas.SendBackward();
                }
                args.Handled = true;
            };
            KeyboardAccelerators.Add(accel);
        }

        // The Studio window assigns DataContext AFTER LoadFromMarkdown runs, so this fires once
        // the restored palette is known — keeps the preset buttons' active highlight accurate.
        // It also seeds the Code pane with the initial generated source.
        DataContextChanged += (s, e) =>
        {
            HighlightActivePalette();
            InitializeCodeEditor();

            // Wire the toolbar zoom slider (ViewModel.ZoomFactor) to the canvas ScrollViewer.
            if (e.NewValue is MermaidStudioViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
                WatchCollections(vm);

                if (StatusBarText != null && !string.IsNullOrEmpty(vm.StatusText))
                {
                    AnnounceLiveRegion(StatusBarText, vm.StatusText);
                }

                // Every whole-diagram load (template, another block) lands fitted in view, and so
                // does the diagram the Studio opened with (it was loaded before DataContext).
                vm.DiagramLoaded += (_, _) => _canvas.FitToContentAfterLayout();
                if (_canvas.IsLoaded) _canvas.FitToContentAfterLayout();
                else _canvas.Loaded += FitOnFirstLoad;
            }
            UpdateSelectionChrome();
        };

        // Live code editing: debounce keystrokes, then sync code -> canvas (and, when the editor
        // is idle, canvas -> code).
        CodeEditorTextBox.TextChanged += OnCodeEditorTextChanged;
        _codeSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _codeSyncTimer.Tick += OnCodeSyncTimerTick;
        _codeSyncTimer.Start();

        // Lazily spin up the rendered-preview WebView2 the first time its tab is selected.
        LeftPanePivot.SelectionChanged += OnLeftPaneSelectionChanged;

        HoverPolish.Track(this);
        Loaded += (_, _) => DimDisabledToolbarButtons(StudioToolbar);
    }

    private static void AnnounceLiveRegion(FrameworkElement? element, string? text, bool assertive = false)
    {
        if (element is null || string.IsNullOrWhiteSpace(text)) return;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            element,
            assertive
                ? Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive
                : Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(element)
            ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    // Toolbar icons carry explicit colours (cyan, red for Delete), which the Button's disabled
    // visual state can't reach — a disabled Undo or Delete looked exactly as live as an enabled
    // one. Fade the whole button instead while it's disabled.
    private static void DimDisabledToolbarButtons(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button)
            {
                button.Opacity = button.IsEnabled ? 1 : 0.4;
                button.IsEnabledChanged += (s, _) =>
                {
                    var b = (Control)s;
                    b.Opacity = b.IsEnabled ? 1 : 0.4;
                };
                continue;
            }
            DimDisabledToolbarButtons(child);
        }
    }

    // Seeds the Code pane once the ViewModel is available (DataContext is assigned after the
    // diagram loads, so the generated source reflects the restored canvas).
    private void InitializeCodeEditor()
    {
        if (ViewModel is null) return;
        string code = CodePaneText();
        _programmaticCodeUpdate = true;
        CodeEditorTextBox.Text = code;
        _programmaticCodeUpdate = false;
        _lastPushedCode = code;
        _codeDirty = false;
    }

    // The Code tab shows the diagram without its %% {"id":...} position lines: a column of
    // JSON cut off at the pane edge was the first thing it showed. Positions are kept by node id
    // when the code is edited (SyncCanvasFromCode), and Sync to Markdown still writes them.
    private string CodePaneText() =>
        MarkSmith.Mermaid.Sync.MermaidSpatialMetadataService.StripFromCode(ViewModel!.GenerateMermaidCode());

    private void OnCodeEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        // Ignore our own canvas -> code pushes; only real user edits mark the code dirty.
        if (_programmaticCodeUpdate) return;
        _codeDirty = true;
        CodeStatusText.Text = "Editing… (canvas updates when you pause)";
    }

    private void OnCodeSyncTimerTick(object? sender, object e)
    {
        if (ViewModel is null) return;

        if (_codeDirty)
        {
            // Code -> canvas: re-parse what the user typed and rebuild the canvas (preserving
            // surviving node positions). Leave the editor text untouched so their caret/selection
            // and formatting survive.
            SyncCanvasFromEditor(CodeEditorTextBox.Text, "Synced to canvas");
            return;
        }

        // Canvas -> code: if the canvas drifted from what the editor shows and the user isn't
        // actively editing, push the fresh generated source into the Code pane.
        if (CodeEditorTextBox.FocusState != FocusState.Unfocused) { RefreshPreview(); return; }
        string current = CodePaneText();
        if (!string.Equals(current, _lastPushedCode, StringComparison.Ordinal))
        {
            _programmaticCodeUpdate = true;
            CodeEditorTextBox.Text = current;
            _programmaticCodeUpdate = false;
            _lastPushedCode = current;
        }

        // Keep the rendered preview in step with the diagram (debounced by this same timer).
        RefreshPreview();
    }

    // ---- Rendered Mermaid preview ----------------------------------------------------------

    private bool IsPreviewTabSelected() =>
        LeftPanePivot.SelectedItem is PivotItem { Tag: "PREVIEW" };

    private void OnLeftPaneSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsPreviewTabSelected()) return;

        if (!_previewInitStarted)
        {
            _ = InitializePreviewAsync();
        }
        else if (_previewReady)
        {
            // Returning to the tab — the diagram may have changed while it was hidden.
            _previewDirty = true;
            RefreshPreview();
        }
    }

    private async Task InitializePreviewAsync()
    {
        if (_previewInitStarted) return;
        _previewInitStarted = true;

        var webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0x0D, 0x0E, 0x14)
        };
        PreviewContainer.Child = webView;
        _previewWebView = webView;

        try
        {
            await webView.EnsureCoreWebView2Async(await Services.WebView2EnvironmentFactory.CreateAsync());
            var core = webView.CoreWebView2;
            if (core is null) { _previewInitStarted = false; return; }

            // Serve the bundled mermaid.min.js from the offline virtual host (the same mapping the
            // main window uses) — no CDN, works fully offline.
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "web");
            if (Directory.Exists(dir))
            {
                try
                {
                    core.SetVirtualHostNameToFolderMapping(
                        Services.WebAssets.Host, dir,
                        Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                }
                catch { /* mapping already set — harmless */ }
            }

            // The page defines window.__renderDiagram in a synchronous <script> block, so it is
            // guaranteed to exist by the time NavigationCompleted fires.
            core.NavigationCompleted += (s, args) =>
            {
                _previewReady = true;
                _previewDirty = true;
                RefreshPreview();
            };

            core.NavigateToString(BuildPreviewHtml());
        }
        catch
        {
            _previewInitStarted = false; // allow a retry on the next tab visit
        }
    }

    // Fire-and-forget wrapper so the sync timer and tab-switch handlers can trigger a render
    // without awaiting it.
    private void RefreshPreview() => _ = RefreshPreviewAsync();

    private async Task RefreshPreviewAsync()
    {
        // Only render when the host page is up and its tab is actually visible.
        if (!_previewReady || _previewWebView?.CoreWebView2 is null) return;
        if (!IsPreviewTabSelected()) { _previewDirty = true; return; }
        if (_previewRenderInFlight) { _previewDirty = true; return; }
        if (ViewModel is null) return;

        string code = ViewModel.GenerateMermaidCode();
        if (!_previewDirty && string.Equals(code, _lastPreviewCode, StringComparison.Ordinal)) return;

        _previewRenderInFlight = true;
        try
        {
            // JSON-encode the source so quotes/newlines/backslashes survive into the JS literal.
            string js = "window.__renderDiagram(" + System.Text.Json.JsonSerializer.Serialize(code) + ");";
            await _previewWebView.CoreWebView2.ExecuteScriptAsync(js);
            _lastPreviewCode = code;
            _previewDirty = false;
        }
        catch { /* WebView2 mid-navigation/disposed — skip this frame */ }
        finally { _previewRenderInFlight = false; }
    }

    private static string BuildPreviewHtml()
    {
        string mermaidSrc = Services.WebAssets.Mermaid;
        return $$"""
            <!DOCTYPE html><html><head><meta charset="UTF-8">
            <script src="{{mermaidSrc}}"></script>
            <style>
              html,body{margin:0;padding:0;background:#0D0E14;overflow:auto;}
              #diagram{display:flex;justify-content:center;align-items:flex-start;padding:16px;}
              #diagram svg{max-width:100%;height:auto;}
              #err{display:none;color:#EF476F;background:#2B1420;border:1px solid #EF476F;border-radius:6px;
                   font-family:'Cascadia Mono',Consolas,monospace;font-size:11px;white-space:pre-wrap;
                   padding:8px 10px;margin:12px;}
              #hint{color:#5A6478;font-family:'Segoe UI',sans-serif;font-size:11px;text-align:center;padding:24px;}
            </style>
            </head><body>
            <div id="diagram"><div id="hint">Rendering preview…</div></div>
            <div id="err"></div>
            <script>
              mermaid.initialize({ startOnLoad:false, theme:'dark', securityLevel:'strict',
                themeCSS: {{Services.MermaidLabelStyle.ThemeCss("#0D0E14")}},
                flowchart:{ useMaxWidth:true, htmlLabels:true },
                maxTextSize:10000000, maxNodes:10000 });
              let __seq = 0;
              window.__renderDiagram = async function(code) {
                const mySeq = ++__seq;
                const errEl = document.getElementById('err');
                const holder = document.getElementById('diagram');
                const id = 'pv-svg-' + mySeq;
                // Drop any stale mermaid error element from a previous failed render.
                const stale = document.getElementById('d' + id); if (stale) stale.remove();
                try {
                  const { svg } = await mermaid.render(id, code);
                  if (mySeq !== __seq) return 'stale';   // a newer render superseded this one
                  holder.innerHTML = svg;
                  errEl.style.display = 'none';
                  return 'ok';
                } catch (e) {
                  if (mySeq !== __seq) return 'stale';
                  errEl.textContent = (e && e.message) ? e.message : String(e);
                  errEl.style.display = 'block';
                  return 'err';
                }
              };
            </script>
            </body></html>
            """;
    }

    // True while the caret is inside a text-editing surface (the inline node/connector label editor
    // or an inspector TextBox). Editing shortcuts (Delete/arrows/etc.) must not fire there — Delete
    // should remove a character and the arrows should move the caret, not nudge a node.
    // A desktop window has to name its XamlRoot: the parameterless call returns null, which made
    // this always false.
    private bool IsEditingText() =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox;

    // Shortcuts no TextBox uses (Ctrl+Shift+C / E) still work from the Code pane.
    private bool IsEditingTextOutsideCode() =>
        IsEditingText() && CodeEditorTextBox.FocusState == FocusState.Unfocused;

    private bool IsFocusWithinCanvas()
    {
        if (XamlRoot is not { } root) return false;
        if (FocusManager.GetFocusedElement(root) is not DependencyObject focused) return false;
        if (focused is TextBox) return false;
        for (var cur = focused; cur is not null; cur = cur is UIElement ? VisualTreeHelper.GetParent(cur) : null)
        {
            if (ReferenceEquals(cur, CanvasContainer)) return true;
        }
        return false;
    }

    private void OnPalettePresetClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement { Tag: string name }) return;
        // A preset recolours everything: one undo step (it used to be none).
        if (!string.Equals(ViewModel.ActivePalette, name, StringComparison.Ordinal)) ViewModel.SnapshotForUndo();
        ViewModel.ActivePalette = name;
        HighlightActivePalette();
    }

    // Highlights the active preset button (also reflects state restored from a loaded
    // %%{init}%% directive, not just in-session clicks).
    private void HighlightActivePalette()
    {
        var active = ViewModel?.ActivePalette ?? string.Empty;
        var accent = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x4C, 0xC9, 0xF0));
        var rest = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x8D, 0x99, 0xAE));
        foreach (var btn in new[] { PresetCatppuccin, PresetNord, PresetEmerald, PresetMono })
        {
            bool isActive = btn.Tag is string tag && tag == active;
            btn.BorderBrush = isActive ? accent : rest;
            btn.BorderThickness = isActive ? new Thickness(1.5) : new Thickness(1);
        }
    }

    private void FitOnFirstLoad(object sender, RoutedEventArgs e)
    {
        _canvas.Loaded -= FitOnFirstLoad;
        _canvas.FitToContentAfterLayout();
    }

    private void OnAutoLayoutClick(object sender, RoutedEventArgs e)
    {
        // force: true clears every node's HasCustomPosition flag first - without it the
        // layout engine skips any node that carries saved metadata positions (which is
        // every node after a load), making the button appear dead.
        ViewModel?.SnapshotForUndo();
        ViewModel?.ApplyAutoLayout(force: true);
        _canvas.FitToContentAfterLayout();
    }

    // ---- Template gallery (mermaid.live / draw.io parity) ---------------------------------
    // Builds a grouped flyout of curated starter diagrams and loads the chosen one into the
    // canvas. Loading snapshots the current diagram first, so it is a single undo step.
    private void OnTemplatesClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement anchor) return;

        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };

        foreach (var group in MermaidTemplates.ByCategory)
        {
            flyout.Items.Add(new MenuFlyoutSubItem { Text = group.Key });
            var sub = (MenuFlyoutSubItem)flyout.Items[^1];
            foreach (var template in group)
            {
                var item = new MenuFlyoutItem { Text = template.Name };
                item.Click += (_, _) => LoadTemplate(template);
                sub.Items.Add(item);
            }
        }

        flyout.ShowAt(anchor);
    }

    private void LoadTemplate(MermaidTemplate template)
    {
        if (ViewModel is null) return;
        ViewModel.SnapshotForUndo();          // make "load template" a single undo step
        ViewModel.LoadFromMermaidCode(template.Code);
        ViewModel.StatusText = $"Loaded '{template.Name}' template ({template.Category}).";
    }

    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.DeleteSelected();
    }

    private void OnReverseConnectorClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.ReverseConnector();
        if (ViewModel.SelectedConnector is not null) UpdateConnectorInspector(ViewModel);
    }

    private void OnFitNodeToLabelClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.FitSelectedNodesToLabel();
    }

    private void OnInspectorInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == Windows.System.VirtualKey.Escape || (e.Key == Windows.System.VirtualKey.Enter && ctrl))
        {
            _canvas.FocusCanvas();
            e.Handled = true;
        }
    }

    private void OnInspectorSingleLineKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Escape or Windows.System.VirtualKey.Enter)
        {
            _canvas.FocusCanvas();
            e.Handled = true;
        }
    }

    // ---- Align & Distribute flyout handlers ----------------------------------------------
    private void OnAlignLeftClick(object sender, RoutedEventArgs e) => ViewModel?.AlignLeft();
    private void OnAlignHCenterClick(object sender, RoutedEventArgs e) => ViewModel?.AlignHorizontalCenter();
    private void OnAlignRightClick(object sender, RoutedEventArgs e) => ViewModel?.AlignRight();
    private void OnAlignTopClick(object sender, RoutedEventArgs e) => ViewModel?.AlignTop();
    private void OnAlignVMiddleClick(object sender, RoutedEventArgs e) => ViewModel?.AlignVerticalMiddle();
    private void OnAlignBottomClick(object sender, RoutedEventArgs e) => ViewModel?.AlignBottom();
    private void OnDistributeHClick(object sender, RoutedEventArgs e) => ViewModel?.DistributeHorizontally();
    private void OnDistributeVClick(object sender, RoutedEventArgs e) => ViewModel?.DistributeVertically();

    private void OnUndoClick(object sender, RoutedEventArgs e) => UndoRedo(redo: false);

    private void OnRedoClick(object sender, RoutedEventArgs e) => UndoRedo(redo: true);

    // In the Code pane, the inspector fields and an in-place label, Ctrl+Z / Ctrl+Y undo the typing:
    // accelerators run before the TextBox sees the key, so this used to undo a canvas step instead.
    private void OnUndoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        UndoRedo(redo: false);
        args.Handled = true;
    }

    private void OnRedoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        UndoRedo(redo: true);
        args.Handled = true;
    }

    // Undoing the last step disables the Undo button. When it held focus (or opened the menu that
    // did it), focus moves to whichever of Undo / Redo is still enabled, else the canvas, instead
    // of falling out of the toolbar.
    private void UndoRedo(bool redo, Control? menuOrigin = null)
    {
        if (ViewModel is null) return;
        Control? holder = menuOrigin
            ?? (UndoToolbarButton.FocusState != FocusState.Unfocused ? UndoToolbarButton
                : RedoToolbarButton.FocusState != FocusState.Unfocused ? RedoToolbarButton : null);
        if (redo) ViewModel.Redo(); else ViewModel.Undo();
        if (holder is null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (holder.IsEnabled) return;
            Control next = UndoToolbarButton.IsEnabled ? UndoToolbarButton : RedoToolbarButton;
            if (next.IsEnabled) next.Focus(FocusState.Keyboard);
            else _canvas.FocusCanvas();
        });
    }

    // ---- World-class editing accelerators -----------------------------------------------
    // Each guards against firing while a TextBox has focus so inline label editing and the
    // inspector fields keep their native Delete/arrow-key behaviour.

    private void OnDeleteAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        ViewModel?.DeleteSelected();
        args.Handled = true;
    }

    private void OnDuplicateAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        ViewModel?.DuplicateSelected();
        args.Handled = true;
    }

    private void OnCopyAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        ViewModel?.CopySelected();
        args.Handled = true;
    }

    private void OnCopySvgAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingTextOutsideCode()) return;
        OnCopySvgClick(CopySvgButton, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnExportSvgAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingTextOutsideCode()) return;
        OnExportSvgClick(ExportSvgButton, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnPasteAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        ViewModel?.PasteClipboard();
        args.Handled = true;
    }

    private void OnSelectAllAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        ViewModel?.SelectAll();
        args.Handled = true;
    }

    private void OnRenameAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        if (_canvas.StartSelectedInPlaceEdit()) args.Handled = true;
    }

    private void OnFindShapesAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (CodeEditorTextBox.FocusState != FocusState.Unfocused) return;
        if (LeftPanePivot.SelectedIndex != 0) LeftPanePivot.SelectedIndex = 0;
        _palette.FocusSearchBox();
        args.Handled = true;
    }

    private void OnSyncAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        OnSyncToMarkdownClick(this, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnFocusInspectorAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel is null) return;
        if (ViewModel.SelectedNode is not null && NodeInspectorPanel.Visibility == Visibility.Visible)
        {
            NodeLabelTextBox.Focus(FocusState.Programmatic);
            NodeLabelTextBox.SelectAll();
            args.Handled = true;
        }
        else if (ViewModel.SelectedConnector is not null && ConnectorInspectorPanel.Visibility == Visibility.Visible)
        {
            ConnectorLabelTextBox.Focus(FocusState.Programmatic);
            ConnectorLabelTextBox.SelectAll();
            args.Handled = true;
        }
    }

    private void OnTabCycleAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsFocusWithinCanvas() || ViewModel is null || ViewModel.Nodes.Count == 0) return;
        bool reverse = sender.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        ViewModel.CycleSelection(reverse);
        args.Handled = true;
    }

    private void OnContextMenuAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        // A focused toolbar/zoom/quick-add button opens its own menu (KeyboardContextMenu bridge);
        // opening the canvas menu as well would dismiss it straight away.
        if (XamlRoot is { } root && KeyboardContextMenu.IsWithinBridgedControl(FocusManager.GetFocusedElement(root) as DependencyObject)) return;
        _canvas.ShowContextMenuForSelection();
        args.Handled = true;
    }

    private void OnEscapeAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        // Esc mid-drag puts the shapes back; only an idle Esc clears the selection.
        if (!_canvas.CancelActiveGesture()) ViewModel?.ClearSelection();
        args.Handled = true;
    }

    private void OnNudgeAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText() || ViewModel is null) return;
        bool coarse = sender.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        var (dx, dy) = sender.Key switch
        {
            Windows.System.VirtualKey.Left => (-1.0, 0.0),
            Windows.System.VirtualKey.Right => (1.0, 0.0),
            Windows.System.VirtualKey.Up => (0.0, -1.0),
            Windows.System.VirtualKey.Down => (0.0, 1.0),
            _ => (0.0, 0.0)
        };
        if (dx == 0 && dy == 0) return;
        ViewModel.NudgeSelected(dx, dy, coarse);
        args.Handled = true;
    }

    private void OnZoomInAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _canvas.ZoomIn();
        args.Handled = true;
    }

    private void OnZoomOutAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _canvas.ZoomOut();
        args.Handled = true;
    }

    private void OnZoomResetAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _canvas.ZoomReset();
        args.Handled = true;
    }

    private void OnSelectShapesTabAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText() && FocusManager.GetFocusedElement(XamlRoot) is TextBox tb && tb != CodeEditorTextBox) return;
        LeftPanePivot.SelectedIndex = 0;
        args.Handled = true;
    }

    private void OnSelectCodeTabAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        LeftPanePivot.SelectedIndex = 1;
        CodeEditorTextBox.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void OnSelectPreviewTabAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText() && FocusManager.GetFocusedElement(XamlRoot) is TextBox tb && tb != CodeEditorTextBox) return;
        LeftPanePivot.SelectedIndex = 2;
        args.Handled = true;
    }

    private void FillCodeEditorMenu(MenuFlyout menu)
    {
        bool hasCode = !string.IsNullOrEmpty(CodeEditorTextBox.Text);

        // Ctrl+Enter writes the diagram into the document (OnSyncAcceleratorInvoked); the canvas
        // follows the code by itself once typing pauses, so "update now" has no shortcut.
        var writeItem = new MenuFlyoutItem
        {
            Text = "Write diagram to document",
            Icon = new FontIcon { Glyph = "\uE72C" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter"
        };
        writeItem.Click += (_, _) => OnSyncToMarkdownClick(this, new RoutedEventArgs());
        menu.Items.Add(writeItem);

        var syncItem = new MenuFlyoutItem
        {
            Text = "Update canvas now",
            Icon = new FontIcon { Glyph = "\uE895" },
            IsEnabled = hasCode
        };
        syncItem.Click += (_, _) =>
        {
            SyncCanvasFromEditor(CodeEditorTextBox.Text, "Canvas updated");
            _canvas.FocusCanvas();
        };
        menu.Items.Add(syncItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Ctrl+C / Ctrl+V in the box act on the selection and the caret, so these whole-source
        // items carry no shortcut.
        var copyItem = new MenuFlyoutItem
        {
            Text = "Copy all source",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            IsEnabled = hasCode
        };
        copyItem.Click += async (_, _) =>
        {
            var err = await ClipboardWriter.TrySetTextAsync(CodeEditorTextBox.Text ?? string.Empty);
            ShowCodeStatus(err is null ? "Copied the Mermaid source" : $"Copy failed: {err}", assertive: err is not null);
        };
        menu.Items.Add(copyItem);

        var replaceItem = new MenuFlyoutItem
        {
            Text = "Replace source with clipboard",
            Icon = new FontIcon { Glyph = "\uE77F" }
        };
        replaceItem.Click += async (_, _) => await ReplaceCodeFromClipboardAsync();
        menu.Items.Add(replaceItem);

        var selectAllItem = new MenuFlyoutItem
        {
            Text = "Select all",
            Icon = new FontIcon { Glyph = "\uE8B3" },
            KeyboardAcceleratorTextOverride = "Ctrl+A",
            IsEnabled = hasCode
        };
        selectAllItem.Click += (_, _) =>
        {
            CodeEditorTextBox.SelectAll();
            CodeEditorTextBox.Focus(FocusState.Programmatic);
        };
        menu.Items.Add(selectAllItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var copySvgItem = new MenuFlyoutItem
        {
            Text = "Copy diagram as SVG",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C"
        };
        copySvgItem.Click += (_, _) => OnCopySvgClick(this, new RoutedEventArgs());
        menu.Items.Add(copySvgItem);

        var exportSvgItem = new MenuFlyoutItem
        {
            Text = "Export as SVG\u2026",
            Icon = new FontIcon { Glyph = "\uEDE1" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+E"
        };
        exportSvgItem.Click += (_, _) => OnExportSvgClick(this, new RoutedEventArgs());
        menu.Items.Add(exportSvgItem);
    }

    private void SyncCanvasFromEditor(string code, string doneText)
    {
        if (ViewModel is null) return;
        bool ok = ViewModel.SyncCanvasFromCode(code);
        _codeDirty = false;
        _lastPushedCode = CodePaneText();
        RefreshPreview();
        ShowCodeStatus(ok
            ? $"{doneText} \u00b7 {ViewModel.Nodes.Count} nodes, {ViewModel.Connectors.Count} edges"
            : "\u26a0 Mermaid syntax error \u2014 fix the highlighted code", assertive: !ok);
    }

    // The menu item used to swallow every failure, so an empty clipboard looked like a dead item.
    private async System.Threading.Tasks.Task ReplaceCodeFromClipboardAsync()
    {
        string? text = null;
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                text = await content.GetTextAsync();
        }
        catch (Exception ex)
        {
            ShowCodeStatus($"Couldn't read the clipboard: {ex.Message}", assertive: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowCodeStatus("The clipboard has no text to paste", assertive: true);
            return;
        }
        CodeEditorTextBox.Text = text;
        SyncCanvasFromEditor(text, "Replaced from the clipboard");
    }

    private void ShowCodeStatus(string text, bool assertive = false)
    {
        CodeStatusText.Text = text;
        AnnounceLiveRegion(CodeStatusText, text, assertive);
    }

    private void OnAutoLayoutButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var autoItem = new MenuFlyoutItem
        {
            Text = "Auto-arrange nodes",
            Icon = new FontIcon { Glyph = "\uECA5" }
        };
        autoItem.Click += (_, _) => OnAutoLayoutClick(this, new RoutedEventArgs());
        menu.Items.Add(autoItem);

        var tdItem = new MenuFlyoutItem
        {
            Text = "Top-Down layout (TD)",
            Icon = new FontIcon { Glyph = "\uE81E" }
        };
        tdItem.Click += (_, _) =>
        {
            if (DirectionComboBox != null) DirectionComboBox.SelectedIndex = 0;
            OnAutoLayoutClick(this, new RoutedEventArgs());
        };
        menu.Items.Add(tdItem);

        var lrItem = new MenuFlyoutItem
        {
            Text = "Left-Right layout (LR)",
            Icon = new FontIcon { Glyph = "\uE8A9" }
        };
        lrItem.Click += (_, _) =>
        {
            if (DirectionComboBox != null) DirectionComboBox.SelectedIndex = 1;
            OnAutoLayoutClick(this, new RoutedEventArgs());
        };
        menu.Items.Add(lrItem);

        var buItem = new MenuFlyoutItem
        {
            Text = "Bottom-Up layout (BT)",
            Icon = new FontIcon { Glyph = "\uE74A" }
        };
        buItem.Click += (_, _) =>
        {
            if (DirectionComboBox != null) DirectionComboBox.SelectedIndex = 2;
            OnAutoLayoutClick(this, new RoutedEventArgs());
        };
        menu.Items.Add(buItem);

        var rlItem = new MenuFlyoutItem
        {
            Text = "Right-Left layout (RL)",
            Icon = new FontIcon { Glyph = "\uE76B" }
        };
        rlItem.Click += (_, _) =>
        {
            if (DirectionComboBox != null) DirectionComboBox.SelectedIndex = 3;
            OnAutoLayoutClick(this, new RoutedEventArgs());
        };
        menu.Items.Add(rlItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var fitItem = new MenuFlyoutItem
        {
            Text = "Fit diagram in window",
            Icon = new FontIcon { Glyph = "\uE9A6" }
        };
        fitItem.Click += (_, _) => _canvas.FitToContentAfterLayout();
        menu.Items.Add(fitItem);

        var anchor = sender as FrameworkElement ?? (FrameworkElement)AutoLayoutButton;
        menu.ShowAt(anchor, KeyboardContextMenu.PointIn(e, anchor));
    }

    private void OnGridSnapToggleRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var snapItem = new MenuFlyoutItem
        {
            Text = GridSnapToggle.IsChecked == true ? "Disable grid snap" : "Enable grid snap (10 px)",
            Icon = new FontIcon { Glyph = "\uE80A" }
        };
        snapItem.Click += (_, _) =>
        {
            GridSnapToggle.IsChecked = !(GridSnapToggle.IsChecked == true);
        };
        menu.Items.Add(snapItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var elbowItem = new MenuFlyoutItem
        {
            Text = "Connector routing: Elbow (90°)",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        elbowItem.Click += (_, _) => { if (RoutingComboBox != null) RoutingComboBox.SelectedIndex = 0; };
        menu.Items.Add(elbowItem);

        var straightItem = new MenuFlyoutItem
        {
            Text = "Connector routing: Straight",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        straightItem.Click += (_, _) => { if (RoutingComboBox != null) RoutingComboBox.SelectedIndex = 1; };
        menu.Items.Add(straightItem);

        var curvedItem = new MenuFlyoutItem
        {
            Text = "Connector routing: Curved",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        curvedItem.Click += (_, _) => { if (RoutingComboBox != null) RoutingComboBox.SelectedIndex = 2; };
        menu.Items.Add(curvedItem);

        var anchor = sender as FrameworkElement ?? (FrameworkElement)GridSnapToggle;
        menu.ShowAt(anchor, KeyboardContextMenu.PointIn(e, anchor));
    }

    private void OnExportSvgButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var exportItem = new MenuFlyoutItem
        {
            Text = "Export SVG file",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+E",
            Icon = new FontIcon { Glyph = "\uEDE1" }
        };
        exportItem.Click += (_, _) => OnExportSvgClick(this, new RoutedEventArgs());
        menu.Items.Add(exportItem);

        var copySvgItem = new MenuFlyoutItem
        {
            Text = "Copy diagram as SVG",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copySvgItem.Click += (_, _) => OnCopySvgClick(this, new RoutedEventArgs());
        menu.Items.Add(copySvgItem);

        var syncItem = new MenuFlyoutItem
        {
            Text = "Sync to Markdown",
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        syncItem.Click += (_, _) => OnSyncToMarkdownClick(this, new RoutedEventArgs());
        menu.Items.Add(syncItem);

        var anchor = sender as FrameworkElement ?? (FrameworkElement)ExportSvgButton;
        menu.ShowAt(anchor, KeyboardContextMenu.PointIn(e, anchor));
    }

    private void OnCopySvgButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var copySvgItem = new MenuFlyoutItem
        {
            Text = "Copy diagram as SVG",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copySvgItem.Click += (_, _) => OnCopySvgClick(this, new RoutedEventArgs());
        menu.Items.Add(copySvgItem);

        var copyCodeItem = new MenuFlyoutItem
        {
            Text = "Copy Mermaid source code",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyCodeItem.Click += (_, _) =>
        {
            var code = CodePaneText();
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(code);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            AnnounceLiveRegion(StatusBarText, "Copied Mermaid source code to clipboard.");
        };
        menu.Items.Add(copyCodeItem);

        var exportItem = new MenuFlyoutItem
        {
            Text = "Export SVG file",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+E",
            Icon = new FontIcon { Glyph = "\uEDE1" }
        };
        exportItem.Click += (_, _) => OnExportSvgClick(this, new RoutedEventArgs());
        menu.Items.Add(exportItem);

        var anchor = sender as FrameworkElement ?? (FrameworkElement)CopySvgButton;
        menu.ShowAt(anchor, KeyboardContextMenu.PointIn(e, anchor));
    }

    private void OnSyncToMarkdownButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var syncItem = new MenuFlyoutItem
        {
            Text = "Sync to Markdown",
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        syncItem.Click += (_, _) => OnSyncToMarkdownClick(this, new RoutedEventArgs());
        menu.Items.Add(syncItem);

        var copyCodeItem = new MenuFlyoutItem
        {
            Text = "Copy Mermaid source code",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyCodeItem.Click += (_, _) =>
        {
            var code = CodePaneText();
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(code);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            AnnounceLiveRegion(StatusBarText, "Copied Mermaid source code to clipboard.");
        };
        menu.Items.Add(copyCodeItem);

        var copySvgItem = new MenuFlyoutItem
        {
            Text = "Copy diagram as SVG",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copySvgItem.Click += (_, _) => OnCopySvgClick(this, new RoutedEventArgs());
        menu.Items.Add(copySvgItem);

        var exportItem = new MenuFlyoutItem
        {
            Text = "Export SVG file",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+E",
            Icon = new FontIcon { Glyph = "\uEDE1" }
        };
        exportItem.Click += (_, _) => OnExportSvgClick(this, new RoutedEventArgs());
        menu.Items.Add(exportItem);

        var anchor = sender as FrameworkElement ?? (FrameworkElement)SyncToMarkdownButton;
        menu.ShowAt(anchor, KeyboardContextMenu.PointIn(e, anchor));
    }

    /// <summary>
    /// Listens for ViewModel.ZoomFactor changes (driven by the toolbar slider) and pushes
    /// the new zoom level into the canvas ScrollViewer.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ViewModel is null) return;
        switch (e.PropertyName)
        {
            case nameof(MermaidStudioViewModel.ZoomFactor):
                _canvas.SetZoomFactor(ViewModel.ZoomFactor);
                break;
            case nameof(MermaidStudioViewModel.StatusText):
                AnnounceLiveRegion(StatusBarText, ViewModel.StatusText);
                break;
            case nameof(MermaidStudioViewModel.ActivePalette):
                HighlightActivePalette();
                break;
            case nameof(MermaidStudioViewModel.Nodes):
            case nameof(MermaidStudioViewModel.Connectors):
            case nameof(MermaidStudioViewModel.SelectedNodes):
                WatchCollections(ViewModel); // a load can swap in new collections
                UpdateSelectionChrome();
                break;
            case nameof(MermaidStudioViewModel.SelectedNode):
            case nameof(MermaidStudioViewModel.SelectedConnector):
            case nameof(MermaidStudioViewModel.IsGridSnapEnabled):
                UpdateSelectionChrome();
                break;
        }
    }

    // ---- Selection-driven chrome ---------------------------------------------------------
    // The inspector shows the panel for what is selected (or an empty state), Delete and Align
    // enable only when they would do something, and the status bar says what the counts are.

    private System.Collections.Specialized.INotifyCollectionChanged[] _watched = [];

    private void WatchCollections(MermaidStudioViewModel vm)
    {
        foreach (var c in _watched) c.CollectionChanged -= OnWatchedCollectionChanged;
        _watched = [vm.Nodes, vm.Connectors, vm.SelectedNodes];
        foreach (var c in _watched) c.CollectionChanged += OnWatchedCollectionChanged;
    }

    private void OnWatchedCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateSelectionChrome();

    private void UpdateSelectionChrome()
    {
        var vm = ViewModel;
        bool node = vm?.SelectedNode is not null;
        bool connector = !node && vm?.SelectedConnector is not null;
        NodeInspectorPanel.Visibility = node ? Visibility.Visible : Visibility.Collapsed;
        ConnectorInspectorPanel.Visibility = connector ? Visibility.Visible : Visibility.Collapsed;
        InspectorEmptyState.Visibility = node || connector ? Visibility.Collapsed : Visibility.Visible;
        if (connector) UpdateConnectorInspector(vm!);

        int selectedNodes = vm?.SelectedNodes.Count ?? 0;
        DeleteSelectedButton.IsEnabled = node || connector || selectedNodes > 0;
        AlignButton.IsEnabled = selectedNodes >= 2;

        if (vm is null)
        {
            CanvasSummaryText.Text = "";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CanvasSummaryButton, "Copy diagram summary");
            return;
        }
        static string Count(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";
        var parts = new List<string> { Count(vm.Nodes.Count, "node"), Count(vm.Connectors.Count, "connector") };
        if (selectedNodes > 1) parts.Add($"{selectedNodes} selected");
        parts.Add(vm.IsGridSnapEnabled ? "Snap to 10 px grid" : "Snap off");
        CanvasSummaryText.Text = string.Join("  ·  ", parts);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CanvasSummaryText, CanvasSummaryText.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CanvasSummaryButton, $"Copy diagram summary, {CanvasSummaryText.Text}");
    }

    // Show the connector vocabulary of the current diagram type (flowchart line + head, sequence
    // message kind, class relationship; state and ER links have no style choices) and name the
    // two ends by their labels.
    private void UpdateConnectorInspector(MermaidStudioViewModel vm)
    {
        var conn = vm.SelectedConnector!;
        string NameOf(string id)
        {
            var label = vm.Nodes.FirstOrDefault(n => n.Id == id)?.LabelText;
            var first = (label ?? "").Split('\r', '\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
            return string.IsNullOrEmpty(first) ? id : first;
        }
        ConnectorFromText.Text = NameOf(conn.SourceNodeId);
        ConnectorToText.Text = NameOf(conn.TargetNodeId);
        ToolTipService.SetToolTip(ConnectorFromText, ConnectorFromText.Text);
        ToolTipService.SetToolTip(ConnectorToText, ConnectorToText.Text);

        var type = vm.SelectedDiagramType;
        bool flow = type is MarkSmith.Mermaid.Ast.MermaidDiagramType.Flowchart;
        ConnectorLineCombo.Visibility = flow ? Visibility.Visible : Visibility.Collapsed;
        ConnectorHeadCombo.Visibility = flow ? Visibility.Visible : Visibility.Collapsed;
        ConnectorMessageCombo.Visibility = type is MarkSmith.Mermaid.Ast.MermaidDiagramType.Sequence ? Visibility.Visible : Visibility.Collapsed;
        ConnectorRelationCombo.Visibility = type is MarkSmith.Mermaid.Ast.MermaidDiagramType.Class ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSyncToMarkdownClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            string code = ViewModel.GenerateMermaidCode();
            SyncToMarkdownRequested?.Invoke(this, code);
        }
    }

    // ---- SVG export (draw.io / mermaid.live parity) ---------------------------------------
    // Renders the current canvas (nodes + connectors, arrowheads and labels included) to a
    // standalone SVG. "Export SVG" saves to a file; "Copy SVG" puts it on the clipboard both
    // as plain text and as HTML so it pastes as a vector image into rich editors.

    private string BuildSvg() =>
        MermaidSvgExporter.GenerateSvg(ViewModel!.Nodes, ViewModel.Connectors);

    private async void OnExportSvgClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        string svg = BuildSvg();

        var path = await MarkSmith.Services.NativeFilePicker.PickSaveFileAsync(
            this, "Export the diagram as SVG", MarkSmith.Services.NativeFilePicker.Purpose.Exports,
            "diagram.svg", new[] { MarkSmith.Models.FileType.Of("SVG image", ".svg") }, okLabel: "Export");
        if (string.IsNullOrEmpty(path)) return; // user cancelled

        await System.IO.File.WriteAllTextAsync(path, svg);
        ViewModel.StatusText = $"SVG exported to {System.IO.Path.GetFileName(path)}.";
    }

    private async void OnCopySvgClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        string svg = BuildSvg();

        var package = new DataPackage();
        package.SetText(svg);
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(svg));
        // This used to call Clipboard.SetContent bare: a busy clipboard threw out of the click.
        var error = await MarkSmith.Services.ClipboardWriter.TrySetAsync(package);
        ViewModel.StatusText = error is null ? "SVG copied to the clipboard." : $"The SVG wasn't copied. {error}";

        var btn = sender as Button ?? CopySvgButton;
        if (btn is not null)
        {
            var icon = CopySvgIcon ?? (btn.Content as FontIcon);
            if (icon is not null)
            {
                CopyFeedback.ApplyIconTick(icon, btn, error);
            }
            else
            {
                CopyFeedback.ApplyButtonTick(btn, error, "✓ SVG");
            }
        }
    }

    private void OnStatusBarDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // A double-click on the summary button is two copies, not a request to refit the view.
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return;
        }
        e.Handled = true;
        _canvas.FitToContent();
    }

    private void OnCanvasSummaryClick(object sender, RoutedEventArgs e)
    {
        var summary = CanvasSummaryText.Text;
        if (string.IsNullOrWhiteSpace(summary)) return;
        try
        {
            var dp = new DataPackage();
            dp.SetText(summary);
            ClipboardWriter.Set(dp);
            if (ViewModel != null) ViewModel.StatusText = "Copied diagram summary to clipboard.";
        }
        catch
        {
            if (ViewModel != null) ViewModel.StatusText = "Couldn't copy the summary: the clipboard is busy. Try again.";
        }
    }

    private void OnStatusBarRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        e.Handled = true;
        var menu = new MenuFlyout();

        var copyStatus = new MenuFlyoutItem
        {
            Text = "Copy diagram status & summary",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyStatus.Click += (_, _) =>
        {
            try
            {
                var dp = new DataPackage();
                dp.SetText($"{StatusBarText.Text} — {CanvasSummaryText.Text}");
                ClipboardWriter.Set(dp);
                if (ViewModel != null) ViewModel.StatusText = "Copied status & summary to clipboard.";
            }
            catch { }
        };
        menu.Items.Add(copyStatus);

        menu.Items.Add(new MenuFlyoutSeparator());

        var zoomFit = new MenuFlyoutItem
        {
            Text = "Fit whole diagram",
            Icon = new FontIcon { Glyph = "\uE9A6" }
        };
        zoomFit.Click += (_, _) => _canvas.FitToContent();
        menu.Items.Add(zoomFit);

        var zoomActual = new MenuFlyoutItem
        {
            Text = "Actual size (100%)",
            KeyboardAcceleratorTextOverride = "Ctrl+0",
            Icon = new FontIcon { Glyph = "\uE71E" }
        };
        zoomActual.Click += (_, _) => _canvas.ZoomReset();
        menu.Items.Add(zoomActual);

        var autoLayout = new MenuFlyoutItem
        {
            Text = "Auto-arrange nodes",
            Icon = new FontIcon { Glyph = "\uECA5" }
        };
        autoLayout.Click += (s, args) => OnAutoLayoutClick(s, new RoutedEventArgs());
        menu.Items.Add(autoLayout);

        var toggleSnap = new MenuFlyoutItem
        {
            Text = ViewModel?.IsGridSnapEnabled == true ? "Disable grid snap" : "Enable grid snap",
            Icon = new FontIcon { Glyph = "\uE80A" }
        };
        toggleSnap.Click += (_, _) =>
        {
            if (ViewModel != null) ViewModel.IsGridSnapEnabled = !ViewModel.IsGridSnapEnabled;
        };
        menu.Items.Add(toggleSnap);

        menu.Items.Add(new MenuFlyoutSeparator());

        var syncDoc = new MenuFlyoutItem
        {
            Text = "Sync to document",
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        syncDoc.Click += (s, args) => OnSyncToMarkdownClick(s, new RoutedEventArgs());
        menu.Items.Add(syncDoc);

        menu.ShowAt(fe, new FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, fe) });
    }

    private void OnUndoButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        e.Handled = true;
        ShowUndoRedoMenu(fe, KeyboardContextMenu.PointIn(e, fe));
    }

    private void OnRedoButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        e.Handled = true;
        ShowUndoRedoMenu(fe, KeyboardContextMenu.PointIn(e, fe));
    }

    private void ShowUndoRedoMenu(FrameworkElement fe, Windows.Foundation.Point pos)
    {
        var menu = new MenuFlyout();

        var undo = new MenuFlyoutItem
        {
            Text = "Undo",
            Icon = new FontIcon { Glyph = "\uE7A7" },
            KeyboardAcceleratorTextOverride = "Ctrl+Z",
            IsEnabled = ViewModel?.CanUndo ?? false
        };
        undo.Click += (_, _) => UndoRedo(redo: false, fe as Control);
        menu.Items.Add(undo);

        var redo = new MenuFlyoutItem
        {
            Text = "Redo",
            Icon = new FontIcon { Glyph = "\uE7A6" },
            KeyboardAcceleratorTextOverride = "Ctrl+Y",
            IsEnabled = ViewModel?.CanRedo ?? false
        };
        redo.Click += (_, _) => UndoRedo(redo: true, fe as Control);
        menu.Items.Add(redo);

        menu.Items.Add(new MenuFlyoutSeparator());

        var selectAll = new MenuFlyoutItem
        {
            Text = "Select all nodes",
            Icon = new FontIcon { Glyph = "\uE8B3" },
            KeyboardAcceleratorTextOverride = "Ctrl+A"
        };
        selectAll.Click += (_, _) => ViewModel?.SelectAll();
        menu.Items.Add(selectAll);

        var autoLayout = new MenuFlyoutItem
        {
            Text = "Auto-arrange nodes",
            Icon = new FontIcon { Glyph = "\uECA5" }
        };
        autoLayout.Click += (s, args) => OnAutoLayoutClick(s, new RoutedEventArgs());
        menu.Items.Add(autoLayout);

        menu.ShowAt(fe, new FlyoutShowOptions { Position = pos });
    }

    private void OnAlignButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        e.Handled = true;
        if (AlignButton.Flyout != null)
            AlignButton.Flyout.ShowAt(fe, new FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, fe) });
    }

    private void OnTemplatesButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        e.Handled = true;
        OnTemplatesClick(sender, new RoutedEventArgs());
    }
}
