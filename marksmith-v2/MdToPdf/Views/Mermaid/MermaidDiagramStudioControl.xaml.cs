using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MdToPdf.Services;
using MdToPdf.ViewModels.Mermaid;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MdToPdf.Views.Mermaid;

public sealed partial class MermaidDiagramStudioControl : UserControl
{
    public MermaidStudioViewModel? ViewModel => DataContext as MermaidStudioViewModel;

    public UIElement TitleBarElement => AppTitleBar;

    public event EventHandler<string>? SyncToMarkdownRequested;

    // Kept so the keyboard accelerators (zoom in/out/reset) can drive the canvas directly.
    private readonly MermaidCanvasControl _canvas;

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

        PaletteContainer.Child = new NodePaletteControl();
        _canvas = new MermaidCanvasControl();
        CanvasContainer.Child = _canvas;

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
            }
        };

        // Live code editing: debounce keystrokes, then sync code -> canvas (and, when the editor
        // is idle, canvas -> code).
        CodeEditorTextBox.TextChanged += OnCodeEditorTextChanged;
        _codeSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _codeSyncTimer.Tick += OnCodeSyncTimerTick;
        _codeSyncTimer.Start();

        // Lazily spin up the rendered-preview WebView2 the first time its tab is selected.
        LeftPanePivot.SelectionChanged += OnLeftPaneSelectionChanged;
    }

    // Seeds the Code pane once the ViewModel is available (DataContext is assigned after the
    // diagram loads, so the generated source reflects the restored canvas).
    private void InitializeCodeEditor()
    {
        if (ViewModel is null) return;
        string code = ViewModel.GenerateMermaidCode();
        _programmaticCodeUpdate = true;
        CodeEditorTextBox.Text = code;
        _programmaticCodeUpdate = false;
        _lastPushedCode = code;
        _codeDirty = false;
    }

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
            bool ok = ViewModel.SyncCanvasFromCode(CodeEditorTextBox.Text);
            _codeDirty = false;
            _lastPushedCode = ViewModel.GenerateMermaidCode();
            CodeStatusText.Text = ok
                ? $"Synced to canvas · {ViewModel.Nodes.Count} nodes, {ViewModel.Connectors.Count} edges"
                : "⚠ Mermaid syntax error — fix the highlighted code";
            RefreshPreview();
            return;
        }

        // Canvas -> code: if the canvas drifted from what the editor shows and the user isn't
        // actively editing, push the fresh generated source into the Code pane.
        if (CodeEditorTextBox.FocusState != FocusState.Unfocused) { RefreshPreview(); return; }
        string current = ViewModel.GenerateMermaidCode();
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
    private static bool IsEditingText() =>
        FocusManager.GetFocusedElement() is TextBox;

    private void OnPalettePresetClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement { Tag: string name }) return;
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

    private void OnAutoLayoutClick(object sender, RoutedEventArgs e)
    {
        // force: true clears every node's HasCustomPosition flag first - without it the
        // layout engine skips any node that carries saved metadata positions (which is
        // every node after a load), making the button appear dead.
        ViewModel?.SnapshotForUndo();
        ViewModel?.ApplyAutoLayout(force: true);
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

    // ---- Align & Distribute flyout handlers ----------------------------------------------
    private void OnAlignLeftClick(object sender, RoutedEventArgs e) => ViewModel?.AlignLeft();
    private void OnAlignHCenterClick(object sender, RoutedEventArgs e) => ViewModel?.AlignHorizontalCenter();
    private void OnAlignRightClick(object sender, RoutedEventArgs e) => ViewModel?.AlignRight();
    private void OnAlignTopClick(object sender, RoutedEventArgs e) => ViewModel?.AlignTop();
    private void OnAlignVMiddleClick(object sender, RoutedEventArgs e) => ViewModel?.AlignVerticalMiddle();
    private void OnAlignBottomClick(object sender, RoutedEventArgs e) => ViewModel?.AlignBottom();
    private void OnDistributeHClick(object sender, RoutedEventArgs e) => ViewModel?.DistributeHorizontally();
    private void OnDistributeVClick(object sender, RoutedEventArgs e) => ViewModel?.DistributeVertically();

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.Undo();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.Redo();
    }

    private void OnUndoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel?.Undo();
        args.Handled = true;
    }

    private void OnRedoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel?.Redo();
        args.Handled = true;
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

    private void OnEscapeAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingText()) return;
        ViewModel?.ClearSelection();
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

    /// <summary>
    /// Listens for ViewModel.ZoomFactor changes (driven by the toolbar slider) and pushes
    /// the new zoom level into the canvas ScrollViewer.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MermaidStudioViewModel.ZoomFactor) && ViewModel is not null)
        {
            _canvas.SetZoomFactor(ViewModel.ZoomFactor);
        }
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

        var picker = new FileSavePicker { SuggestedFileName = "diagram" };
        picker.FileTypeChoices.Add("SVG image", new List<string> { ".svg" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return; // user cancelled

        await FileIO.WriteTextAsync(file, svg);
        ViewModel.StatusText = $"SVG exported to {file.Name}.";
    }

    private void OnCopySvgClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        string svg = BuildSvg();

        var package = new DataPackage();
        package.SetText(svg);
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(svg));
        Clipboard.SetContent(package);

        ViewModel.StatusText = "SVG copied to the clipboard.";
    }
}
