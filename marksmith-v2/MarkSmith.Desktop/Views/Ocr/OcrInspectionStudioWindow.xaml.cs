using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.UI;
using MarkSmith.Ocr;
using MarkSmith.Services;

namespace MarkSmith.Views.Ocr;

/// <summary>
/// Standalone, user-controllable OCR Inspection Studio window.
/// Replaces the old auto-closing overlay with a permanent, interactive review studio where users can
/// flick through scanned/imported pages, inspect detected elements and boxes, and reclassify them
/// (e.g. Picture <-> ShapeMosaic <-> Diagram <-> Table <-> Text) with live Markdown preview before
/// explicitly clicking the Call-to-Action import button.
/// </summary>
public sealed partial class OcrInspectionStudioWindow : Window, IOcrObserver
{
    private static readonly FontFamily MonoFont = new("Consolas");

    private static readonly (OcrBlockKind Kind, string Label, int ShortcutNumber)[] ReclassifyKindOptions =
    {
        (OcrBlockKind.Picture, "Picture", 1),
        (OcrBlockKind.ShapeMosaic, "Mosaic / Shape", 2),
        (OcrBlockKind.Diagram, "Diagram", 3),
        (OcrBlockKind.Table, "Table", 4),
        (OcrBlockKind.KeyValue, "Key-Value", 5),
        (OcrBlockKind.Heading, "Heading", 6),
        (OcrBlockKind.List, "List", 7),
        (OcrBlockKind.Paragraph, "Text Block", 8),
    };

    private readonly OcrInspectionSession _session = new();
    private readonly TaskCompletionSource<OcrInspectionResult> _decisionTcs = new();
    private readonly Dictionary<OcrInspectionElement, Border> _canvasElementFrames = new();
    private readonly Dictionary<OcrInspectionElement, Border> _inspectorCards = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, OcrPagePreview> _pagePreviews = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<OcrInspectionPage> _pageQueue = new();
    private int _isProcessingQueue = 0;

    private CancellationTokenSource? _idleLaserCts;
    private CancellationTokenSource? _playbackCts;
    private OcrInspectionElement? _selectedElement;
    // Discard, Esc and the X all cancel the import. After reclassifying, CloseGuard asks first.
    private readonly Action _requestClose;
    private int _imagePixelWidth = 960;
    private int _imagePixelHeight = 1320;
    private bool _isReviewMode = false;
    private bool _markdownScopeIsDocument = false;
    private bool _isUpdatingSelection = false;
    private int _expectedPageCount = 1;
    private volatile string _currentEngineName = "MarkSmith OCR";
    private volatile bool _scanningCompleted = false;

    public OcrInspectionSession Session => _session;
    public bool IsReviewMode => _isReviewMode;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static readonly System.Reflection.PropertyInfo? CursorProperty =
        typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static void SetCursor(UIElement element, Microsoft.UI.Input.InputSystemCursorShape shape)
    {
        try { CursorProperty?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape)); }
        catch { /* cosmetic only */ }
    }

    public OcrInspectionStudioWindow()
    {
        this.InitializeComponent();
        // "Right-click for options" menus: open them from Shift+F10 / the Menu key too.
        KeyboardContextMenu.BridgeAll(
            (PrevPageButton, OnPrevPageButtonRightTapped),
            (NextPageButton, OnNextPageButtonRightTapped),
            (DiscardButton, OnDiscardButtonRightTapped),
            (ImportCtaButton, OnImportCtaButtonRightTapped),
            (ResetPageButton, OnResetPageButtonRightTapped),
            (ResetElementButton, OnResetElementButtonRightTapped),
            (CopyMarkdownButton, OnCopyMarkdownButtonRightTapped),
            (SessionMetricsButton, OnFooterBarRightTapped));
        // The read-only preview's options menu opens from the keyboard too; a selection keeps the
        // box's own Copy / Select all menu.
        TextBoxMenu.Attach(MarkdownPreviewTextBox, FillMarkdownPreviewMenu);
        ConfigureWindowChrome();
        SizeForDisplay();

        if (MarkdownScopeComboBox is not null && MarkdownScopeComboBox.SelectedIndex < 0)
        {
            MarkdownScopeComboBox.SelectedIndex = 0;
        }

        HoverPolish.Apply(RootGrid);

        this.Closed += (_, _) =>
        {
            _playbackCts?.Cancel();
            _idleLaserCts?.Cancel();
            _decisionTcs.TrySetResult(new OcrInspectionResult(
                Imported: false,
                Markdown: string.Empty,
                PageCount: _session.Pages.Count,
                ReclassifiedCount: 0));
        };

        RootGrid.PreviewKeyDown += OnRootKeyDown;

        _requestClose = CloseGuard.Attach(this,
            () => _session.TotalReclassifications > 0 && !_decisionTcs.Task.IsCompleted,
            () => { CompleteImport(); return true; },
            "reviewed text");
    }

    private void ConfigureWindowChrome()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);
        TitleBarInsets.Reserve(this, AppTitleBar);
        CaptionButtons.Follow(this, AppTitleBar);
    }

    private void SizeForDisplay()
    {
        try
        {
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            if (scale <= 0) scale = 1.0;

            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int width = Math.Min((int)(1220 * scale), workArea.Width - 60);
            int height = Math.Min((int)(820 * scale), workArea.Height - 60);

            AppWindow.Resize(new SizeInt32(Math.Max(980, width), Math.Max(660, height)));
        }
        catch
        {
            AppWindow.Resize(new SizeInt32(1180, 780));
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CanvasPaneColumn is null || InspectorPaneColumn is null) return;
        double w = e.NewSize.Width;
        if (w > 0 && w < 1120)
        {
            CanvasPaneColumn.Width = new GridLength(360);
            InspectorPaneColumn.Width = new GridLength(290);
        }
        else if (w >= 1120)
        {
            CanvasPaneColumn.Width = new GridLength(460);
            InspectorPaneColumn.Width = new GridLength(340);
        }
        UpdatePageSheetDimensions();
    }

    /// <summary>
    /// Awaits the explicit user import decision. Resolves when the user clicks 'Import into MarkSmith'
    /// or cancels/closes the window.
    /// </summary>
    public Task<OcrInspectionResult> WaitForDecisionAsync() => _decisionTcs.Task;

    // ---- IOcrObserver Implementation -------------------------------------------------------------

    public void PageStarted(OcrPagePreview preview)
    {
        _pagePreviews[preview.PageNumber] = preview;
        if (preview.PageCount > _expectedPageCount)
        {
            _expectedPageCount = preview.PageCount;
        }
        if (!string.IsNullOrWhiteSpace(preview.Engine))
        {
            _currentEngineName = preview.Engine;
        }

        var dq = this.DispatcherQueue;
        if (dq is null) return;

        dq.TryEnqueue(async () =>
        {
            try
            {
                EngineBadgeText.Text = _currentEngineName;
                _imagePixelWidth = Math.Max(1, preview.ImageWidth);
                _imagePixelHeight = Math.Max(1, preview.ImageHeight);
                CanvasMetricsText.Text = $"{_imagePixelWidth} × {_imagePixelHeight} px";

                string pageLabel = _expectedPageCount > 1
                    ? $"Page {preview.PageIndex + 1} of {_expectedPageCount}"
                    : $"Page {preview.PageNumber}";
                PageIndicatorText.Text = pageLabel;
                AutomationProperties.SetName(PageIndicatorText, pageLabel);

                SetStatusBannerText($"Scanning & Decomposing {pageLabel} with {_currentEngineName}…");
                WordStreamText.Text = "Calibrating page geometry & deskew angle…";
                StageProgressBar.IsIndeterminate = false;
                StageProgressBar.Value = 25;

                UpdatePageSheetDimensions();
                if (preview.Image.Length > 0)
                {
                    await LoadPreviewBitmapAsync(preview.Image);
                }

                StartLaserSweep();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OcrInspectionStudio PageStarted dispatch error: {ex}");
            }
        });
    }

    public void PageRead(OcrPageTrace trace)
    {
        if (!_pagePreviews.TryGetValue(trace.PageNumber, out var preview))
        {
            preview = new OcrPagePreview(
                trace.PageNumber,
                trace.PageNumber - 1,
                _expectedPageCount,
                Array.Empty<byte>(),
                _imagePixelWidth,
                _imagePixelHeight,
                _currentEngineName);
        }

        var page = _session.AddOrUpdatePage(preview, trace);
        _pageQueue.Enqueue(page);

        var dq = this.DispatcherQueue;
        if (dq is null) return;

        dq.TryEnqueue(() => _ = ProcessPageQueueAsync());
    }

    private async Task ProcessPageQueueAsync()
    {
        if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) != 0) return;
        try
        {
            _idleLaserCts?.Cancel();
            if (_playbackCts is null || _playbackCts.IsCancellationRequested)
            {
                _playbackCts = new CancellationTokenSource();
            }
            var token = _playbackCts.Token;

            while (_pageQueue.TryDequeue(out var page))
            {
                await AnimateSinglePageBreakdownAsync(page, token);
                if (_session.Pages.Count > 1 && !_pageQueue.IsEmpty)
                {
                    try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
                }
            }

            if ((_scanningCompleted || (_session.Pages.Count >= _expectedPageCount && _expectedPageCount > 0)) && !_isReviewMode)
            {
                _session.CurrentPageIndex = 0;
                RenderCurrentPage();
                EnterReviewMode();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OcrInspectionStudio ProcessPageQueueAsync error: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessingQueue, 0);
            if (!_pageQueue.IsEmpty)
            {
                var dq = this.DispatcherQueue;
                dq?.TryEnqueue(() => _ = ProcessPageQueueAsync());
            }
            else if (_scanningCompleted && !_isReviewMode)
            {
                var dq = this.DispatcherQueue;
                dq?.TryEnqueue(() =>
                {
                    if (!_isReviewMode)
                    {
                        _session.CurrentPageIndex = 0;
                        RenderCurrentPage();
                        EnterReviewMode();
                    }
                });
            }
        }
    }

    public void PageFailed(int pageNumber, string errorMessage) => ReportPageError(pageNumber, errorMessage);

    /// <summary>
    /// Ingests a synthesized or digital document import sequence, playing the reading breakdown
    /// page by page without skipping, and transitioning directly into the interactive review mode (never auto-closing).
    /// </summary>
    public async Task PlayImportExperience(string markdown, string sourceLabel)
    {
        EngineBadgeText.Text = sourceLabel;
        var pages = OcrTrace.SynthesizePagesFromMarkdown(markdown, sourceLabel);
        if (pages.Count == 0) return;

        _expectedPageCount = pages.Count;
        _pagePreviews.Clear();

        foreach (var (preview, trace) in pages)
        {
            _pagePreviews[preview.PageNumber] = preview;
            _session.AddOrUpdatePage(preview, trace);
        }

        _playbackCts?.Cancel();
        _playbackCts = new CancellationTokenSource();
        var token = _playbackCts.Token;

        // Animate breakdown page by page without skipping!
        for (int p = 0; p < _session.Pages.Count && !token.IsCancellationRequested; p++)
        {
            var page = _session.Pages[p];
            string pageLabel = _session.Pages.Count > 1
                ? $"Page {page.PageNumber} of {_session.Pages.Count}"
                : $"Page {page.PageNumber}";
            SetStatusBannerText($"Analyzing & Decomposing {pageLabel} with {sourceLabel}…");
            StageProgressBar.Value = Math.Round((p + 0.5) / _session.Pages.Count * 100);

            await AnimateSinglePageBreakdownAsync(page, token);

            if (_session.Pages.Count > 1 && p < _session.Pages.Count - 1)
            {
                try { await Task.Delay(140, token); } catch (OperationCanceledException) { break; }
            }
        }

        _session.CurrentPageIndex = 0;
        RenderCurrentPage();
        EnterReviewMode();
    }

    private async Task AnimateSinglePageBreakdownAsync(OcrInspectionPage page, CancellationToken token)
    {
        try
        {
            _session.GoToPage(page.PageIndex);
            UpdatePageFlickingState();

            if (page.Image.Length > 0)
            {
                _imagePixelWidth = page.ImageWidth;
                _imagePixelHeight = page.ImageHeight;
                CanvasMetricsText.Text = $"{_imagePixelWidth} × {_imagePixelHeight} px";
                UpdatePageSheetDimensions();
                await LoadPreviewBitmapAsync(page.Image);
            }
            else
            {
                DocumentImage.Source = null;
            }

            ElementsCanvas.Children.Clear();
            ElementCardsPanel.Children.Clear();
            _canvasElementFrames.Clear();
            _inspectorCards.Clear();
            if (NoElementsPlaceholder is not null)
                NoElementsPlaceholder.Visibility = Visibility.Collapsed;

            // 1. Scan laser sweep
            LaserBeam.Visibility = Visibility.Visible;
            double ch = PageSheetGrid.Height > 0 ? PageSheetGrid.Height : 520;
            const int sweepSteps = 6;
            for (int s = 0; s <= sweepSteps && !token.IsCancellationRequested; s++)
            {
                float frac = 0.08f + (0.84f * (s / (float)sweepSteps));
                LaserTransform.Y = Math.Clamp(frac * ch, 0, Math.Max(0, ch - 4));
                try { await Task.Delay(20, token); } catch (OperationCanceledException) { return; }
            }

            // 2. Progressive element cutouts & inspector cards
            int elemCount = page.Elements.Count;
            for (int i = 0; i < elemCount && !token.IsCancellationRequested; i++)
            {
                var elem = page.Elements[i];
                var frame = CreateCanvasElementFrame(elem);
                _canvasElementFrames[elem] = frame;
                AddPositionedOverlay(frame, elem.Box);

                var card = CreateInspectorCard(elem);
                _inspectorCards[elem] = card;
                ElementCardsPanel.Children.Add(card);

                LaserTransform.Y = Math.Clamp(elem.Box.Bottom * ch, 0, Math.Max(0, ch - 4));
                WordStreamText.Text = $"Located #{elem.Id} ({elem.KindDisplayName}) with {elem.WordCount} words…";
                HeaderElementsCountText.Text = _session.TotalElements.ToString();

                if (elemCount <= 12)
                {
                    try { await Task.Delay(30, token); } catch (OperationCanceledException) { return; }
                }
            }

            if (NoElementsPlaceholder is not null)
                NoElementsPlaceholder.Visibility = elemCount == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 3. Word token stream
            if (page.Words.Count > 0 && !token.IsCancellationRequested)
            {
                string wordsSample = string.Join(" ", page.Words.Take(8).Select(w => w.Text));
                WordStreamText.Text = $"Streaming words: {wordsSample}…";
                WordCountBadgeText.Text = $"{page.Words.Count} words";
                try { await Task.Delay(40, token); } catch (OperationCanceledException) { return; }
            }

            UpdateLiveMarkdownPreview();
            UpdateSessionTelemetry();
            WordStreamText.Text = $"Assembled {page.Words.Count} words into {page.Elements.Count} blocks.";
            StageProgressBar.Value = 100;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AnimateSinglePageBreakdownAsync error: {ex}");
        }
    }

    /// <summary>
    /// Explicitly completes the scanning/import sequence and transitions into interactive review mode.
    /// Can be invoked by document importers once all pages are finished.
    /// </summary>
    public void CompleteScanning()
    {
        _scanningCompleted = true;
        var dq = this.DispatcherQueue;
        if (dq is null) return;
        dq.TryEnqueue(() =>
        {
            try
            {
                if (_pageQueue.IsEmpty && Interlocked.CompareExchange(ref _isProcessingQueue, 0, 0) == 0 && !_isReviewMode)
                {
                    _session.CurrentPageIndex = 0;
                    RenderCurrentPage();
                    EnterReviewMode();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CompleteScanning error: {ex}");
            }
        });
    }

    /// <summary>
    /// Surfaces a non-fatal page-level warning or extraction failure in the Inspection Studio without closing it.
    /// </summary>
    public void ReportPageError(int pageNumber, string errorMessage)
    {
        var dq = this.DispatcherQueue;
        if (dq is null) return;
        dq.TryEnqueue(() =>
        {
            try
            {
                if (StatusBannerIcon is not null)
                {
                    StatusBannerIcon.Glyph = "\uE783"; // Warning triangle
                    StatusBannerIcon.Foreground = ThemeBrush.Get("SystemFillColorCautionBrush", RootGrid.ActualTheme);
                }
                SetStatusBannerText($"Page {pageNumber}: {errorMessage}");
                if (StatusSubtext is not null) StatusSubtext.Text = "MarkSmith continued importing the rest of the document. You can inspect available pages and review before applying.";
                SetFooterStatusText($"Page {pageNumber} issue: {errorMessage}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReportPageError error: {ex}");
            }
        });
    }

    private void SetStatusBannerText(string text)
    {
        if (StatusBannerText is null) return;
        StatusBannerText.Text = text;
        AutomationProperties.SetName(StatusBannerText, text);
    }

    private void SetFooterStatusText(string text)
    {
        if (FooterStatusText is null) return;
        FooterStatusText.Text = text;
        AutomationProperties.SetName(FooterStatusText, text);
    }

    // ---- Review Mode & Interactive State --------------------------------------------------------

    private void EnterReviewMode()
    {
        _isReviewMode = true;
        _playbackCts?.Cancel();
        _idleLaserCts?.Cancel();
        if (LaserBeam is not null) LaserBeam.Visibility = Visibility.Collapsed;

        if (StatusBannerIcon is not null)
        {
            StatusBannerIcon.Glyph = "\uE73E"; // Checkmark
            StatusBannerIcon.Foreground = ThemeBrush.Get("SystemFillColorSuccessBrush", RootGrid.ActualTheme);
        }
        SetStatusBannerText("Document Scan Complete — Inspect Detected Elements & Choose Import Settings");
        if (StatusSubtext is not null)
            StatusSubtext.Text = "Flick through pages (←/→), navigate elements (↑/↓), or press 1–8 to reclassify the selected block. Hit 'Import into MarkSmith' (Ctrl+Enter) to apply.";

        UpdatePageFlickingState();
        RenderCurrentPage();
        UpdateLiveMarkdownPreview();
        SetFooterStatusText("Inspection Studio active — Never auto-closes. Review detected elements before applying import.");
    }

    private void UpdatePageFlickingState()
    {
        if (PrevPageButton is not null)
        {
            bool canPrev = _session.CanPreviousPage;
            if (!canPrev && PrevPageButton.FocusState != FocusState.Unfocused)
            {
                if (NextPageButton is not null && _session.CanNextPage) NextPageButton.Focus(FocusState.Programmatic);
                else ImportCtaButton?.Focus(FocusState.Programmatic);
            }
            PrevPageButton.IsEnabled = canPrev;
        }

        if (NextPageButton is not null)
        {
            bool canNext = _session.CanNextPage;
            if (!canNext && NextPageButton.FocusState != FocusState.Unfocused)
            {
                if (PrevPageButton is not null && _session.CanPreviousPage) PrevPageButton.Focus(FocusState.Programmatic);
                else ImportCtaButton?.Focus(FocusState.Programmatic);
            }
            NextPageButton.IsEnabled = canNext;
        }

        int totalPages = Math.Max(1, _session.Pages.Count);
        int currentNum = _session.CurrentPage?.PageNumber ?? (_session.CurrentPageIndex + 1);
        if (PageIndicatorText is not null)
        {
            string label = $"Page {currentNum} of {totalPages}";
            PageIndicatorText.Text = label;
            AutomationProperties.SetName(PageIndicatorText, label);
        }
    }

    private void RenderCurrentPage()
    {
        var page = _session.CurrentPage;
        if (page is null) return;

        ElementsCanvas.Children.Clear();
        ElementCardsPanel.Children.Clear();
        _canvasElementFrames.Clear();
        _inspectorCards.Clear();

        if (page.Image.Length > 0)
        {
            _imagePixelWidth = page.ImageWidth;
            _imagePixelHeight = page.ImageHeight;
            CanvasMetricsText.Text = $"{_imagePixelWidth} × {_imagePixelHeight} px";
            UpdatePageSheetDimensions();
            _ = LoadPreviewBitmapAsync(page.Image);
        }
        else
        {
            DocumentImage.Source = null;
            UpdatePageSheetDimensions();
        }

        // Render detected element boundary boxes on canvas
        foreach (var elem in page.Elements)
        {
            var frame = CreateCanvasElementFrame(elem);
            _canvasElementFrames[elem] = frame;
            AddPositionedOverlay(frame, elem.Box);

            var card = CreateInspectorCard(elem);
            _inspectorCards[elem] = card;
            ElementCardsPanel.Children.Add(card);
        }

        if (NoElementsPlaceholder is not null)
        {
            NoElementsPlaceholder.Visibility = page.Elements.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_selectedElement is not null && page.Elements.Contains(_selectedElement))
        {
            SelectElement(_selectedElement);
        }
        else if (page.Elements.Count > 0)
        {
            SelectElement(page.Elements[0]);
        }
        else
        {
            ClearElementSelection();
        }

        UpdatePageFlickingState();
        UpdateLiveMarkdownPreview();
        UpdateSessionTelemetry();
    }

    private static string DescribeElementForAccessibility(OcrInspectionElement elem) =>
        $"Element {elem.Id}, {elem.KindDisplayName}, {elem.WordCount} words" +
        (elem.IsReclassified ? $" (reclassified from {elem.OriginalKind})" : string.Empty);

    private static string DescribeElementToolTip(OcrInspectionElement elem) =>
        $"#{elem.Id} · {elem.KindDisplayName} ({elem.WordCount} words)" +
        (elem.IsReclassified ? $" — Reclassified from {elem.OriginalKind}" : string.Empty) +
        "\nClick to select · Right-click or Shift+F10 for actions · Keys 1–8 to reclassify · R to reset";

    private Border CreateCanvasElementFrame(OcrInspectionElement elem)
    {
        var (strokeColor, fillColor) = GetKindPalette(elem.Kind);

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(strokeColor),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(fillColor),
            IsHitTestVisible = true,
        };
        SetCursor(border, Microsoft.UI.Input.InputSystemCursorShape.Hand);
        AutomationProperties.SetName(border, DescribeElementForAccessibility(elem));
        ToolTipService.SetToolTip(border, DescribeElementToolTip(elem));
        border.ContextFlyout = BuildElementContextMenu(elem);

        var badgeStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgeStack.Children.Add(new FontIcon
        {
            Glyph = elem.KindIconGlyph,
            FontSize = 9,
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
        });
        badgeStack.Children.Add(new TextBlock
        {
            Text = $"#{elem.Id} {elem.KindDisplayName}",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var badge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(2),
            Background = new SolidColorBrush(strokeColor),
            Child = badgeStack,
        };

        border.Child = badge;

        border.PointerEntered += (_, _) =>
        {
            var (sc, _) = GetKindPalette(elem.Kind);
            border.BorderThickness = new Thickness(2.5);
            border.Background = new SolidColorBrush(Color.FromArgb(90, sc.R, sc.G, sc.B));
            if (_inspectorCards.TryGetValue(elem, out var card))
            {
                card.BorderBrush = ThemeBrush.Get("AccentFillColorDefaultBrush", RootGrid.ActualTheme);
            }
        };

        border.PointerExited += (_, _) =>
        {
            var (sc, fc) = GetKindPalette(elem.Kind);
            bool isSel = ReferenceEquals(elem, _selectedElement);
            border.BorderThickness = new Thickness(isSel ? 3.0 : 1.5);
            border.Background = new SolidColorBrush(isSel
                ? Color.FromArgb(70, sc.R, sc.G, sc.B)
                : fc);
            if (_inspectorCards.TryGetValue(elem, out var card))
            {
                card.BorderBrush = isSel
                    ? ThemeBrush.Get("AccentFillColorDefaultBrush", RootGrid.ActualTheme)
                    : ThemeBrush.Get("ControlStrokeColorDefaultBrush", RootGrid.ActualTheme);
            }
        };

        border.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            SelectElement(elem);
        };

        border.RightTapped += (_, _) => SelectElement(elem);

        return border;
    }

    private Border CreateInspectorCard(OcrInspectionElement elem)
    {
        var (strokeColor, _) = GetKindPalette(elem.Kind);

        var cardStack = new StackPanel { Spacing = 5 };

        // Header row: Element index + Kind Badge + Area metrics
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = $"#{elem.Id} · {elem.KindDisplayName}" + (elem.IsReclassified ? " (Reclassified)" : string.Empty),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(strokeColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleText, 0);

        var metricsText = new TextBlock
        {
            Text = $"{elem.WordCount} words",
            FontSize = 10.5,
            FontFamily = MonoFont,
            Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush", RootGrid.ActualTheme),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(metricsText, 1);

        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(metricsText);
        cardStack.Children.Add(headerGrid);

        // Reclassification Dropdown Selector
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
        };
        AutomationProperties.SetName(combo, $"Reclassify element {elem.Id}");

        int selectedIdx = 0;
        for (int i = 0; i < ReclassifyKindOptions.Length; i++)
        {
            var opt = ReclassifyKindOptions[i];
            var item = new ComboBoxItem { Content = $"{opt.ShortcutNumber} · {opt.Label}", Tag = opt.Kind };
            combo.Items.Add(item);
            if (opt.Kind == elem.Kind) selectedIdx = i;
        }
        combo.SelectedIndex = selectedIdx;

        combo.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingSelection) return;
            if (combo.SelectedItem is ComboBoxItem cbi && cbi.Tag is OcrBlockKind newKind && newKind != elem.Kind)
            {
                SelectElement(elem);
                ReclassifyElement(elem, newKind);
            }
        };

        cardStack.Children.Add(combo);

        // Preview snippet text
        string previewSnippet = elem.CurrentMarkdown.Length > 90
            ? elem.CurrentMarkdown.Substring(0, 87) + "…"
            : elem.CurrentMarkdown;
        cardStack.Children.Add(new TextBlock
        {
            Text = previewSnippet,
            FontSize = 10.5,
            FontFamily = MonoFont,
            Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush", RootGrid.ActualTheme),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
        });

        var cardBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 7, 8, 7),
            Background = ThemeBrush.Get("SubtleFillColorSecondaryBrush", RootGrid.ActualTheme),
            BorderBrush = ThemeBrush.Get("ControlStrokeColorDefaultBrush", RootGrid.ActualTheme),
            BorderThickness = new Thickness(1),
            Child = cardStack,
        };
        SetCursor(cardBorder, Microsoft.UI.Input.InputSystemCursorShape.Hand);
        AutomationProperties.SetName(cardBorder, DescribeElementForAccessibility(elem));
        ToolTipService.SetToolTip(cardBorder, DescribeElementToolTip(elem));
        cardBorder.ContextFlyout = BuildElementContextMenu(elem);

        cardBorder.PointerEntered += (_, _) =>
        {
            cardBorder.BorderBrush = ThemeBrush.Get("AccentFillColorDefaultBrush", RootGrid.ActualTheme);
            if (_canvasElementFrames.TryGetValue(elem, out var frame))
            {
                var (sc, _) = GetKindPalette(elem.Kind);
                frame.BorderThickness = new Thickness(2.5);
                frame.Background = new SolidColorBrush(Color.FromArgb(90, sc.R, sc.G, sc.B));
            }
        };

        cardBorder.PointerExited += (_, _) =>
        {
            bool isSel = ReferenceEquals(elem, _selectedElement);
            cardBorder.BorderBrush = isSel
                ? ThemeBrush.Get("AccentFillColorDefaultBrush", RootGrid.ActualTheme)
                : ThemeBrush.Get("ControlStrokeColorDefaultBrush", RootGrid.ActualTheme);
            if (_canvasElementFrames.TryGetValue(elem, out var frame))
            {
                var (sc, fc) = GetKindPalette(elem.Kind);
                frame.BorderThickness = new Thickness(isSel ? 3.0 : 1.5);
                frame.Background = new SolidColorBrush(isSel
                    ? Color.FromArgb(70, sc.R, sc.G, sc.B)
                    : fc);
            }
        };

        cardBorder.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            SelectElement(elem);
        };

        cardBorder.RightTapped += (_, _) => SelectElement(elem);

        HoverPolish.Apply(cardBorder);
        return cardBorder;
    }

    private MenuFlyout BuildElementContextMenu(OcrInspectionElement elem)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            SelectElement(elem);
            menu.Items.Clear();

            var reclassifySubItem = new MenuFlyoutSubItem
            {
                Text = "Reclassify element as",
                Icon = new FontIcon { Glyph = "\uE8CB" }
            };

            foreach (var opt in ReclassifyKindOptions)
            {
                var kindItem = new ToggleMenuFlyoutItem
                {
                    Text = $"{opt.ShortcutNumber} · {opt.Label}",
                    IsChecked = elem.Kind == opt.Kind
                };
                var targetKind = opt.Kind;
                kindItem.Click += (_, _) =>
                {
                    SelectElement(elem);
                    if (elem.Kind != targetKind)
                    {
                        ReclassifyElement(elem, targetKind);
                    }
                };
                reclassifySubItem.Items.Add(kindItem);
            }
            menu.Items.Add(reclassifySubItem);

            var resetItem = new MenuFlyoutItem
            {
                Text = $"Reset to original ({elem.OriginalKind}) (R)",
                Icon = new FontIcon { Glyph = "\uE7A7" },
                IsEnabled = elem.IsReclassified
            };
            resetItem.Click += (_, _) =>
            {
                SelectElement(elem);
                ResetSelectedElement();
            };
            menu.Items.Add(resetItem);

            var copyElemItem = new MenuFlyoutItem
            {
                Text = "Copy element Markdown",
                KeyboardAcceleratorTextOverride = "Ctrl+C",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrWhiteSpace(elem.CurrentMarkdown)
            };
            copyElemItem.Click += (_, _) =>
            {
                SelectElement(elem);
                CopySelectedElementMarkdown();
            };
            menu.Items.Add(copyElemItem);

            string rawText = elem.Words.Count > 0
                ? string.Join(" ", elem.Words.Select(w => w.Text))
                : elem.CurrentMarkdown;
            if (!string.IsNullOrWhiteSpace(rawText))
            {
                var copyRawItem = new MenuFlyoutItem
                {
                    Text = "Copy raw text",
                    Icon = new FontIcon { Glyph = "\uE8FD" }
                };
                copyRawItem.Click += (_, _) =>
                {
                    _ = Services.ClipboardWriter.TrySetTextAsync(rawText);
                    SetFooterStatusText($"Copied {elem.KindDisplayName} text to clipboard.");
                };
                menu.Items.Add(copyRawItem);
            }

            var copyBoxItem = new MenuFlyoutItem
            {
                Text = $"Copy bounding box ({elem.Box.X:0.00}, {elem.Box.Y:0.00}, {elem.Box.Width:0.00}×{elem.Box.Height:0.00})",
                Icon = new FontIcon { Glyph = "\uE790" }
            };
            copyBoxItem.Click += (_, _) =>
            {
                var boxStr = $"X={elem.Box.X:0.000}, Y={elem.Box.Y:0.000}, W={elem.Box.Width:0.000}, H={elem.Box.Height:0.000}";
                _ = Services.ClipboardWriter.TrySetTextAsync(boxStr);
                SetFooterStatusText($"Copied bounding box: {boxStr}");
            };
            menu.Items.Add(copyBoxItem);

            var siblingsOfSameKind = _session.CurrentPage?.Elements.Where(e => e.Kind == elem.Kind).ToList() ?? new();
            if (siblingsOfSameKind.Count > 1)
            {
                var batchSubItem = new MenuFlyoutSubItem
                {
                    Text = $"Reclassify all {siblingsOfSameKind.Count} {elem.KindDisplayName}s as",
                    Icon = new FontIcon { Glyph = "\uE8D2" }
                };
                foreach (var opt in ReclassifyKindOptions)
                {
                    if (opt.Kind == elem.Kind) continue;
                    var batchKindItem = new MenuFlyoutItem
                    {
                        Text = $"{opt.ShortcutNumber} · {opt.Label}"
                    };
                    var targetKind = opt.Kind;
                    batchKindItem.Click += (_, _) =>
                    {
                        foreach (var targetElem in siblingsOfSameKind)
                        {
                            ReclassifyElement(targetElem, targetKind, refreshPreviewAndTelemetry: false);
                        }
                        UpdateSessionTelemetry();
                        UpdateLiveMarkdownPreview();
                        UpdateActionButtonsState();
                        SetFooterStatusText($"Reclassified {siblingsOfSameKind.Count} {elem.KindDisplayName} elements to {opt.Label}.");
                    };
                    batchSubItem.Items.Add(batchKindItem);
                }
                menu.Items.Add(batchSubItem);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            var resetPageItem = new MenuFlyoutItem
            {
                Text = "Reset all on page",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+R",
                Icon = new FontIcon { Glyph = "\uE72C" },
                IsEnabled = (_session.CurrentPage?.ReclassifiedCount ?? 0) > 0
            };
            resetPageItem.Click += (_, _) => ResetCurrentPageElements();
            menu.Items.Add(resetPageItem);
        };
        return menu;
    }

    private void OnPageSheetRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled) return;

        var menu = new MenuFlyout();

        var copyPageItem = new MenuFlyoutItem
        {
            Text = "Copy current page Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            KeyboardAcceleratorTextOverride = _markdownScopeIsDocument ? "" : "Ctrl+Shift+C"
        };
        copyPageItem.Click += (_, _) =>
        {
            var pageMd = _session.CurrentPage?.AssembledPageMarkdown ?? string.Empty;
            if (string.IsNullOrWhiteSpace(pageMd)) return;
            _ = Services.ClipboardWriter.TrySetTextAsync(pageMd);
            SetFooterStatusText($"Copied current page Markdown ({pageMd.Length:N0} chars) to clipboard.");
        };
        menu.Items.Add(copyPageItem);

        var copyAllItem = new MenuFlyoutItem
        {
            Text = "Copy full document Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyAllItem.Click += (_, _) =>
        {
            var allMd = _session.GetDocumentMarkdown();
            _ = Services.ClipboardWriter.TrySetTextAsync(allMd);
            SetFooterStatusText("Copied full document Markdown to clipboard.");
        };
        menu.Items.Add(copyAllItem);

        var resetPageItem = new MenuFlyoutItem
        {
            Text = "Reset all elements on page",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+R",
            Icon = new FontIcon { Glyph = "\uE72C" },
            IsEnabled = (_session.CurrentPage?.ReclassifiedCount ?? 0) > 0
        };
        resetPageItem.Click += (_, _) => ResetCurrentPageElements();
        menu.Items.Add(resetPageItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var prevPageItem = new MenuFlyoutItem
        {
            Text = "Previous page",
            KeyboardAcceleratorTextOverride = "Left / PgUp",
            Icon = new FontIcon { Glyph = "\uE76B" },
            IsEnabled = _session.CanPreviousPage
        };
        prevPageItem.Click += (_, _) => OnPrevPageClick(this, new RoutedEventArgs());
        menu.Items.Add(prevPageItem);

        var nextPageItem = new MenuFlyoutItem
        {
            Text = "Next page",
            KeyboardAcceleratorTextOverride = "Right / PgDn",
            Icon = new FontIcon { Glyph = "\uE76C" },
            IsEnabled = _session.CanNextPage
        };
        nextPageItem.Click += (_, _) => OnNextPageClick(this, new RoutedEventArgs());
        menu.Items.Add(nextPageItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var importItem = new MenuFlyoutItem
        {
            Text = "Import into MarkSmith",
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            Icon = new FontIcon { Glyph = "\uE898" }
        };
        importItem.Click += (_, _) => OnImportCtaClick(this, new RoutedEventArgs());
        menu.Items.Add(importItem);

        if (PageSheetBorder is not null)
        {
            menu.ShowAt(PageSheetBorder, KeyboardContextMenu.PointIn(e, PageSheetBorder));
            e.Handled = true;
        }
    }

    private void FillMarkdownPreviewMenu(MenuFlyout menu)
    {
        var copyMarkdownItem = new MenuFlyoutItem
        {
            Text = "Copy Markdown preview",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyMarkdownItem.Click += (s, ev) => OnCopyMarkdownClick(s, ev);
        menu.Items.Add(copyMarkdownItem);

        var selectAllItem = new MenuFlyoutItem
        {
            Text = "Select all",
            KeyboardAcceleratorTextOverride = "Ctrl+A",
            Icon = new FontIcon { Glyph = "\uE8B3" }
        };
        selectAllItem.Click += (_, _) =>
        {
            MarkdownPreviewTextBox.Focus(FocusState.Programmatic);
            MarkdownPreviewTextBox.SelectAll();
        };
        menu.Items.Add(selectAllItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var switchScopeItem = new MenuFlyoutItem
        {
            Text = _markdownScopeIsDocument ? "Switch to Current Page view" : "Switch to Full Document view",
            Icon = new FontIcon { Glyph = _markdownScopeIsDocument ? "\uE8FD" : "\uE8A5" }
        };
        switchScopeItem.Click += (_, _) =>
        {
            MarkdownScopeComboBox.SelectedIndex = _markdownScopeIsDocument ? 0 : 1;
        };
        menu.Items.Add(switchScopeItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var importItem = new MenuFlyoutItem
        {
            Text = "Import into MarkSmith",
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        importItem.Click += (s, ev) => OnImportCtaClick(s, ev);
        menu.Items.Add(importItem);

        // Esc is left to text boxes (OnRootKeyDown), so from the preview it does not discard.
        var discardItem = new MenuFlyoutItem
        {
            Text = "Cancel & Discard",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        discardItem.Click += (s, ev) => OnDiscardClick(s, ev);
        menu.Items.Add(discardItem);
    }

    private void OnSessionMetricsButtonClick(object sender, RoutedEventArgs e)
        => CopyFooterText($"{FooterStatusText.Text} — {SessionMetricsText.Text}", "session metrics and status");

    // Copies, then says so in the status line for 3 s. The status the copy replaced comes back
    // only if nothing else has written the status line meanwhile, and a second copy within the
    // 3 s keeps the original status rather than restoring the "Copied" note.
    private string? _statusBeforeCopyNote;
    private string? _copyNote;

    private async void CopyFooterText(string text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;
        var error = await ClipboardWriter.TrySetTextAsync(text);

        if (_copyNote is null || FooterStatusText.Text != _copyNote)
            _statusBeforeCopyNote = FooterStatusText.Text;
        var note = error is null ? $"Copied {what} to the clipboard." : $"Couldn't copy {what}: {error}";
        _copyNote = note;
        SetFooterStatusText(note);

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (FooterStatusText.Text != note || !ReferenceEquals(_copyNote, note)) return;
            SetFooterStatusText(_statusBeforeCopyNote ?? FooterStatusText.Text);
            _copyNote = null;
        };
        timer.Start();
    }

    private void OnFooterBarRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var copyMetricsItem = new MenuFlyoutItem
        {
            Text = "Copy session metrics",
            Icon = new FontIcon { Glyph = "\uE946" }
        };
        copyMetricsItem.Click += (_, _) => CopyFooterText(SessionMetricsText.Text, "session metrics");
        menu.Items.Add(copyMetricsItem);

        var copyStatusItem = new MenuFlyoutItem
        {
            Text = "Copy status message",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        // While a "Copied" note is showing, copy the status it stands in for, not the note.
        copyStatusItem.Click += (_, _) => CopyFooterText(
            _copyNote is not null && FooterStatusText.Text == _copyNote ? _statusBeforeCopyNote ?? "" : FooterStatusText.Text,
            "status message");
        menu.Items.Add(copyStatusItem);

        var copyMarkdownItem = new MenuFlyoutItem
        {
            Text = "Copy Markdown preview",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyMarkdownItem.Click += (s, ev) => OnCopyMarkdownClick(s, ev);
        menu.Items.Add(copyMarkdownItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var importItem = new MenuFlyoutItem
        {
            Text = "Import into MarkSmith",
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        importItem.Click += (s, ev) => OnImportCtaClick(s, ev);
        menu.Items.Add(importItem);

        var discardItem = new MenuFlyoutItem
        {
            Text = "Cancel & Discard",
            KeyboardAcceleratorTextOverride = "Esc",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        discardItem.Click += (s, ev) => OnDiscardClick(s, ev);
        menu.Items.Add(discardItem);

        menu.ShowAt(FooterBarGrid, KeyboardContextMenu.PointIn(e, FooterBarGrid));
    }

    private void OnPrevPageButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowPageNavContextMenu(sender as FrameworkElement ?? PrevPageButton, KeyboardContextMenu.PointIn(e, sender as UIElement ?? PrevPageButton));
    }

    private void OnNextPageButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowPageNavContextMenu(sender as FrameworkElement ?? NextPageButton, KeyboardContextMenu.PointIn(e, sender as UIElement ?? NextPageButton));
    }

    private void ShowPageNavContextMenu(FrameworkElement target, Windows.Foundation.Point? position)
    {
        var menu = new MenuFlyout();

        var prevItem = new MenuFlyoutItem
        {
            Text = "Previous page",
            Icon = new FontIcon { Glyph = "\uE76B" },
            KeyboardAcceleratorTextOverride = "Left / PgUp",
            IsEnabled = _session.CurrentPageIndex > 0
        };
        prevItem.Click += (s, ev) => OnPrevPageClick(s, ev);
        menu.Items.Add(prevItem);

        var nextItem = new MenuFlyoutItem
        {
            Text = "Next page",
            Icon = new FontIcon { Glyph = "\uE76C" },
            KeyboardAcceleratorTextOverride = "Right / PgDn",
            IsEnabled = _session.CurrentPageIndex < _session.Pages.Count - 1
        };
        nextItem.Click += (s, ev) => OnNextPageClick(s, ev);
        menu.Items.Add(nextItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var firstItem = new MenuFlyoutItem
        {
            Text = "First page",
            Icon = new FontIcon { Glyph = "\uE892" },
            KeyboardAcceleratorTextOverride = "Home",
            IsEnabled = _session.CurrentPageIndex > 0
        };
        firstItem.Click += (_, _) =>
        {
            if (_session.Pages.Count > 0)
            {
                _session.CurrentPageIndex = 0;
                RenderCurrentPage();
            }
        };
        menu.Items.Add(firstItem);

        var lastItem = new MenuFlyoutItem
        {
            Text = "Last page",
            Icon = new FontIcon { Glyph = "\uE893" },
            KeyboardAcceleratorTextOverride = "End",
            IsEnabled = _session.CurrentPageIndex < _session.Pages.Count - 1
        };
        lastItem.Click += (_, _) =>
        {
            if (_session.Pages.Count > 0)
            {
                _session.CurrentPageIndex = _session.Pages.Count - 1;
                RenderCurrentPage();
            }
        };
        menu.Items.Add(lastItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var importItem = new MenuFlyoutItem
        {
            Text = "Import into MarkSmith",
            Icon = new FontIcon { Glyph = "\uE898" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter"
        };
        importItem.Click += (s, ev) => OnImportCtaClick(s, ev);
        menu.Items.Add(importItem);

        if (position.HasValue)
            menu.ShowAt(target, position.Value);
        else
            menu.ShowAt(target);
    }

    private void OnDiscardButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? DiscardButton;
        var menu = new MenuFlyout();

        var discardItem = new MenuFlyoutItem
        {
            Text = "Cancel & Discard",
            Icon = new FontIcon { Glyph = "\uE711" },
            KeyboardAcceleratorTextOverride = "Esc"
        };
        discardItem.Click += (s, ev) => OnDiscardClick(s, ev);
        menu.Items.Add(discardItem);

        var resetPageItem = new MenuFlyoutItem
        {
            Text = "Reset page classifications",
            Icon = new FontIcon { Glyph = "\uE777" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+R"
        };
        resetPageItem.Click += (s, ev) => OnResetPageClick(s, ev);
        menu.Items.Add(resetPageItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var importItem = new MenuFlyoutItem
        {
            Text = "Import into MarkSmith",
            Icon = new FontIcon { Glyph = "\uE898" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter"
        };
        importItem.Click += (s, ev) => OnImportCtaClick(s, ev);
        menu.Items.Add(importItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnImportCtaButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? ImportCtaButton;
        var menu = new MenuFlyout();

        var importItem = new MenuFlyoutItem
        {
            Text = "Import into MarkSmith",
            Icon = new FontIcon { Glyph = "\uE898" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter"
        };
        importItem.Click += (s, ev) => OnImportCtaClick(s, ev);
        menu.Items.Add(importItem);

        var copyMarkdownItem = new MenuFlyoutItem
        {
            Text = "Copy live Markdown preview",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C"
        };
        copyMarkdownItem.Click += (s, ev) => OnCopyMarkdownClick(s, ev);
        menu.Items.Add(copyMarkdownItem);

        var copyAllItem = new MenuFlyoutItem
        {
            Text = "Copy full document Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyAllItem.Click += async (_, _) =>
        {
            var fullDoc = _session.GetDocumentMarkdown();
            await ClipboardWriter.TrySetTextAsync(fullDoc);
        };
        menu.Items.Add(copyAllItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var discardItem = new MenuFlyoutItem
        {
            Text = "Cancel & Discard",
            Icon = new FontIcon { Glyph = "\uE711" },
            KeyboardAcceleratorTextOverride = "Esc"
        };
        discardItem.Click += (s, ev) => OnDiscardClick(s, ev);
        menu.Items.Add(discardItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnResetPageButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? ResetPageButton;
        var menu = new MenuFlyout();

        var resetPageItem = new MenuFlyoutItem
        {
            Text = "Reset all elements on page",
            Icon = new FontIcon { Glyph = "\uE777" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+R"
        };
        resetPageItem.Click += (s, ev) => OnResetPageClick(s, ev);
        menu.Items.Add(resetPageItem);

        if (_selectedElement is not null)
        {
            var resetElemItem = new MenuFlyoutItem
            {
                Text = "Reset selected element",
                Icon = new FontIcon { Glyph = "\uE7A7" },
                KeyboardAcceleratorTextOverride = "R"
            };
            resetElemItem.Click += (s, ev) => OnResetElementClick(s, ev);
            menu.Items.Add(resetElemItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var reclassPic = new MenuFlyoutItem
        {
            Text = "Reclassify as Picture",
            Icon = new FontIcon { Glyph = "\uEB9F" },
            KeyboardAcceleratorTextOverride = "1"
        };
        reclassPic.Click += (_, _) => { if (_selectedElement is not null) ReclassifyElement(_selectedElement, OcrBlockKind.Picture); };
        menu.Items.Add(reclassPic);

        var reclassTable = new MenuFlyoutItem
        {
            Text = "Reclassify as Table",
            Icon = new FontIcon { Glyph = "\uE8A5" },
            KeyboardAcceleratorTextOverride = "4"
        };
        reclassTable.Click += (_, _) => { if (_selectedElement is not null) ReclassifyElement(_selectedElement, OcrBlockKind.Table); };
        menu.Items.Add(reclassTable);

        var reclassHeading = new MenuFlyoutItem
        {
            Text = "Reclassify as Heading",
            Icon = new FontIcon { Glyph = "\uE8D2" },
            KeyboardAcceleratorTextOverride = "6"
        };
        reclassHeading.Click += (_, _) => { if (_selectedElement is not null) ReclassifyElement(_selectedElement, OcrBlockKind.Heading); };
        menu.Items.Add(reclassHeading);

        var reclassText = new MenuFlyoutItem
        {
            Text = "Reclassify as Text Block",
            Icon = new FontIcon { Glyph = "\uE8D2" },
            KeyboardAcceleratorTextOverride = "8"
        };
        reclassText.Click += (_, _) => { if (_selectedElement is not null) ReclassifyElement(_selectedElement, OcrBlockKind.Paragraph); };
        menu.Items.Add(reclassText);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnResetElementButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? ResetElementButton;
        var menu = new MenuFlyout();

        var resetElemItem = new MenuFlyoutItem
        {
            Text = "Reset selected element",
            Icon = new FontIcon { Glyph = "\uE7A7" },
            KeyboardAcceleratorTextOverride = "R"
        };
        resetElemItem.Click += (s, ev) => OnResetElementClick(s, ev);
        menu.Items.Add(resetElemItem);

        var resetPageItem = new MenuFlyoutItem
        {
            Text = "Reset all elements on page",
            Icon = new FontIcon { Glyph = "\uE777" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+R"
        };
        resetPageItem.Click += (s, ev) => OnResetPageClick(s, ev);
        menu.Items.Add(resetPageItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        foreach (var opt in ReclassifyKindOptions)
        {
            var item = new MenuFlyoutItem
            {
                Text = $"Reclassify as {opt.Label}",
                Icon = new FontIcon { Glyph = opt.Kind == OcrBlockKind.Picture ? "\uEB9F" : opt.Kind == OcrBlockKind.Table ? "\uE8A5" : "\uE8D2" },
                KeyboardAcceleratorTextOverride = opt.ShortcutNumber.ToString()
            };
            var kind = opt.Kind;
            item.Click += (_, _) => { if (_selectedElement is not null) ReclassifyElement(_selectedElement, kind); };
            menu.Items.Add(item);
        }

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void OnCopyMarkdownButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = sender as FrameworkElement ?? CopyMarkdownButton;
        var menu = new MenuFlyout();

        var copyPreviewItem = new MenuFlyoutItem
        {
            Text = "Copy live Markdown preview",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C"
        };
        copyPreviewItem.Click += (s, ev) => OnCopyMarkdownClick(s, ev);
        menu.Items.Add(copyPreviewItem);

        var copyPageItem = new MenuFlyoutItem
        {
            Text = "Copy current page Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPageItem.Click += async (_, _) =>
        {
            var md = _session.CurrentPage?.AssembledPageMarkdown ?? string.Empty;
            await ClipboardWriter.TrySetTextAsync(md);
        };
        menu.Items.Add(copyPageItem);

        var copyDocItem = new MenuFlyoutItem
        {
            Text = "Copy full document Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyDocItem.Click += async (_, _) =>
        {
            var fullDoc = _session.GetDocumentMarkdown();
            await ClipboardWriter.TrySetTextAsync(fullDoc);
        };
        menu.Items.Add(copyDocItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var toggleScopeItem = new MenuFlyoutItem
        {
            Text = _markdownScopeIsDocument ? "Switch to Current Page view" : "Switch to Full Document view",
            Icon = new FontIcon { Glyph = _markdownScopeIsDocument ? "\uE8FD" : "\uE8A5" }
        };
        toggleScopeItem.Click += (_, _) =>
        {
            MarkdownScopeComboBox.SelectedIndex = _markdownScopeIsDocument ? 0 : 1;
        };
        menu.Items.Add(toggleScopeItem);

        var importItem = new MenuFlyoutItem
        {
            Text = "Import into MarkSmith",
            Icon = new FontIcon { Glyph = "\uE898" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter"
        };
        importItem.Click += (s, ev) => OnImportCtaClick(s, ev);
        menu.Items.Add(importItem);

        menu.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
    }

    private void ClearElementSelection()
    {
        _selectedElement = null;
        if (SelectedElementBadgeText is not null)
        {
            SelectedElementBadgeText.Text = "No element selected";
            AutomationProperties.SetName(SelectedElementBadgeText, SelectedElementBadgeText.Text);
        }
        if (QuickReclassifyComboBox is not null)
        {
            if (QuickReclassifyComboBox.FocusState != FocusState.Unfocused)
            {
                ImportCtaButton?.Focus(FocusState.Programmatic);
            }
            QuickReclassifyComboBox.IsEnabled = false;
        }
        UpdateActionButtonsState();
    }

    private void SelectElement(OcrInspectionElement elem)
    {
        _selectedElement = elem;

        // Reset previous highlights
        foreach (var (el, frame) in _canvasElementFrames)
        {
            var (sc, fc) = GetKindPalette(el.Kind);
            bool isTarget = ReferenceEquals(el, elem);
            frame.BorderThickness = new Thickness(isTarget ? 3.0 : 1.5);
            frame.Background = new SolidColorBrush(isTarget
                ? Color.FromArgb(70, sc.R, sc.G, sc.B)
                : fc);
        }

        foreach (var (el, card) in _inspectorCards)
        {
            bool isTarget = ReferenceEquals(el, elem);
            card.BorderBrush = isTarget
                ? ThemeBrush.Get("AccentFillColorDefaultBrush", RootGrid.ActualTheme)
                : ThemeBrush.Get("ControlStrokeColorDefaultBrush", RootGrid.ActualTheme);
            card.BorderThickness = new Thickness(isTarget ? 2 : 1);
        }

        if (_inspectorCards.TryGetValue(elem, out var selectedCard))
        {
            try { selectedCard.StartBringIntoView(); } catch { }
        }

        if (SelectedElementBadgeText is not null)
        {
            SelectedElementBadgeText.Text = $"Element #{elem.Id} ({elem.KindDisplayName})";
            AutomationProperties.SetName(SelectedElementBadgeText, SelectedElementBadgeText.Text);
        }

        if (QuickReclassifyComboBox is not null)
        {
            QuickReclassifyComboBox.IsEnabled = true;

            // Sync QuickReclassifyComboBox
            _isUpdatingSelection = true;
            try
            {
                for (int i = 0; i < QuickReclassifyComboBox.Items.Count; i++)
                {
                    if (QuickReclassifyComboBox.Items[i] is ComboBoxItem item &&
                        string.Equals(item.Tag?.ToString(), elem.Kind.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        QuickReclassifyComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        }

        UpdateActionButtonsState();
    }

    private void UpdateActionButtonsState()
    {
        bool canResetElement = _selectedElement?.IsReclassified == true;
        if (ResetElementButton is not null)
        {
            if (!canResetElement && ResetElementButton.FocusState != FocusState.Unfocused)
            {
                if (QuickReclassifyComboBox is not null && QuickReclassifyComboBox.IsEnabled)
                    QuickReclassifyComboBox.Focus(FocusState.Programmatic);
                else
                    ImportCtaButton?.Focus(FocusState.Programmatic);
            }
            ResetElementButton.IsEnabled = canResetElement;
        }

        bool canResetPage = (_session.CurrentPage?.ReclassifiedCount ?? 0) > 0;
        if (ResetPageButton is not null)
        {
            if (!canResetPage && ResetPageButton.FocusState != FocusState.Unfocused)
            {
                if (QuickReclassifyComboBox is not null && QuickReclassifyComboBox.IsEnabled)
                    QuickReclassifyComboBox.Focus(FocusState.Programmatic);
                else
                    ImportCtaButton?.Focus(FocusState.Programmatic);
            }
            ResetPageButton.IsEnabled = canResetPage;
        }
    }

    private void ReclassifyElement(OcrInspectionElement elem, OcrBlockKind newKind, bool refreshPreviewAndTelemetry = true)
    {
        elem.Reclassify(newKind);

        // Update canvas frame visual
        if (_canvasElementFrames.TryGetValue(elem, out var frame))
        {
            var (sc, fc) = GetKindPalette(elem.Kind);
            bool isSel = ReferenceEquals(elem, _selectedElement);
            frame.BorderBrush = new SolidColorBrush(sc);
            frame.BorderThickness = new Thickness(isSel ? 3.0 : 1.5);
            frame.Background = new SolidColorBrush(isSel
                ? Color.FromArgb(70, sc.R, sc.G, sc.B)
                : fc);
            if (frame.Child is Border badge)
            {
                badge.Background = new SolidColorBrush(sc);
                if (badge.Child is StackPanel badgeStack)
                {
                    if (badgeStack.Children.Count > 0 && badgeStack.Children[0] is FontIcon badgeIcon)
                        badgeIcon.Glyph = elem.KindIconGlyph;
                    if (badgeStack.Children.Count > 1 && badgeStack.Children[1] is TextBlock badgeLabel)
                        badgeLabel.Text = $"#{elem.Id} {elem.KindDisplayName}";
                }
                else if (badge.Child is TextBlock badgeText)
                {
                    badgeText.Text = $"#{elem.Id} {elem.KindDisplayName}";
                }
            }
            AutomationProperties.SetName(frame, DescribeElementForAccessibility(elem));
            ToolTipService.SetToolTip(frame, DescribeElementToolTip(elem));
        }

        // Keep the middle pane inspector card in sync
        if (_inspectorCards.TryGetValue(elem, out var card) && card.Child is StackPanel cardStack)
        {
            var (strokeColor, _) = GetKindPalette(elem.Kind);
            if (cardStack.Children.Count > 0 && cardStack.Children[0] is Grid headerGrid &&
                headerGrid.Children.Count > 0 && headerGrid.Children[0] is TextBlock titleText)
            {
                titleText.Text = $"#{elem.Id} · {elem.KindDisplayName}" + (elem.IsReclassified ? " (Reclassified)" : string.Empty);
                titleText.Foreground = new SolidColorBrush(strokeColor);
            }
            if (cardStack.Children.Count > 1 && cardStack.Children[1] is ComboBox cardCombo)
            {
                _isUpdatingSelection = true;
                try
                {
                    for (int ci = 0; ci < cardCombo.Items.Count; ci++)
                    {
                        if (cardCombo.Items[ci] is ComboBoxItem item && item.Tag is OcrBlockKind k && k == elem.Kind)
                        {
                            if (cardCombo.SelectedIndex != ci) cardCombo.SelectedIndex = ci;
                            break;
                        }
                    }
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
            }
            if (cardStack.Children.Count > 2 && cardStack.Children[2] is TextBlock snippetText)
            {
                snippetText.Text = elem.CurrentMarkdown.Length > 90
                    ? elem.CurrentMarkdown.Substring(0, 87) + "…"
                    : elem.CurrentMarkdown;
            }
            AutomationProperties.SetName(card, DescribeElementForAccessibility(elem));
            ToolTipService.SetToolTip(card, DescribeElementToolTip(elem));
        }

        // Keep the selection inspector in sync if this element is currently selected
        if (ReferenceEquals(elem, _selectedElement))
        {
            if (SelectedElementBadgeText is not null)
            {
                SelectedElementBadgeText.Text = $"Element #{elem.Id} ({elem.KindDisplayName})";
                AutomationProperties.SetName(SelectedElementBadgeText, SelectedElementBadgeText.Text);
            }
            _isUpdatingSelection = true;
            try
            {
                for (int i = 0; i < QuickReclassifyComboBox.Items.Count; i++)
                {
                    if (QuickReclassifyComboBox.Items[i] is ComboBoxItem item &&
                        string.Equals(item.Tag?.ToString(), elem.Kind.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        QuickReclassifyComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        }

        if (refreshPreviewAndTelemetry)
        {
            UpdateSessionTelemetry();
            UpdateLiveMarkdownPreview();
            UpdateActionButtonsState();
            SetFooterStatusText($"Reclassified Element #{elem.Id} to {elem.KindDisplayName}. Preview updated live.");
        }
    }

    private void UpdateLiveMarkdownPreview()
    {
        if (MarkdownPreviewTextBox is null) return;

        if (_session.Pages.Count == 0)
        {
            MarkdownPreviewTextBox.Text = string.Empty;
            if (CopyMarkdownButton is not null)
            {
                if (CopyMarkdownButton.FocusState != FocusState.Unfocused)
                {
                    if (MarkdownScopeComboBox is not null && MarkdownScopeComboBox.IsEnabled)
                        MarkdownScopeComboBox.Focus(FocusState.Programmatic);
                    else
                        ImportCtaButton?.Focus(FocusState.Programmatic);
                }
                CopyMarkdownButton.IsEnabled = false;
            }
            return;
        }

        string md = _markdownScopeIsDocument
            ? _session.GetDocumentMarkdown()
            : (_session.CurrentPage?.AssembledPageMarkdown ?? string.Empty);

        MarkdownPreviewTextBox.Text = md;
        if (CopyMarkdownButton is not null)
        {
            bool canCopy = !string.IsNullOrWhiteSpace(md);
            if (!canCopy && CopyMarkdownButton.FocusState != FocusState.Unfocused)
            {
                if (MarkdownScopeComboBox is not null && MarkdownScopeComboBox.IsEnabled)
                    MarkdownScopeComboBox.Focus(FocusState.Programmatic);
                else
                    ImportCtaButton?.Focus(FocusState.Programmatic);
            }
            CopyMarkdownButton.IsEnabled = canCopy;
        }
    }

    private void UpdateSessionTelemetry()
    {
        if (HeaderElementsCountText is not null)
            HeaderElementsCountText.Text = _session.TotalElements.ToString();
        if (HeaderReclassifiedCountText is not null)
            HeaderReclassifiedCountText.Text = _session.TotalReclassifications.ToString();
        if (WordCountBadgeText is not null)
            WordCountBadgeText.Text = $"{_session.CurrentPage?.Words.Count ?? 0} words";

        if (SessionMetricsText is not null)
        {
            string metrics = $"{_session.Pages.Count} Page{(_session.Pages.Count == 1 ? "" : "s")} · " +
                             $"{_session.TotalElements} Elements · " +
                             $"{_session.TotalWords} Words · " +
                             $"{_session.TotalReclassifications} Reclassified";
            SessionMetricsText.Text = metrics;
            AutomationProperties.SetName(SessionMetricsText, metrics);
        }
    }

    private static (Color Stroke, Color Fill) GetKindPalette(OcrBlockKind kind)
    {
        return kind switch
        {
            OcrBlockKind.Picture => (Color.FromArgb(255, 168, 85, 247), Color.FromArgb(32, 168, 85, 247)),       // Purple
            OcrBlockKind.ShapeMosaic => (Color.FromArgb(255, 6, 182, 212), Color.FromArgb(32, 6, 182, 212)),       // Cyan
            OcrBlockKind.Diagram => (Color.FromArgb(255, 99, 102, 241), Color.FromArgb(32, 99, 102, 241)),        // Indigo
            OcrBlockKind.Table => (Color.FromArgb(255, 20, 184, 166), Color.FromArgb(32, 20, 184, 166)),          // Teal
            OcrBlockKind.KeyValue => (Color.FromArgb(255, 245, 158, 11), Color.FromArgb(32, 245, 158, 11)),       // Amber
            OcrBlockKind.Heading => (Color.FromArgb(255, 59, 130, 246), Color.FromArgb(32, 59, 130, 246)),         // Blue
            OcrBlockKind.List => (Color.FromArgb(255, 16, 185, 129), Color.FromArgb(32, 16, 185, 129)),           // Green
            OcrBlockKind.Paragraph or _ => (Color.FromArgb(255, 100, 116, 139), Color.FromArgb(24, 100, 116, 139)), // Slate
        };
    }

    // ---- Canvas Viewport Positioning ------------------------------------------------------------

    private void OnPageViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePageSheetDimensions();
    }

    private void UpdatePageSheetDimensions()
    {
        if (PageViewportGrid is null || PageSheetGrid is null) return;

        double availW = PageViewportGrid.ActualWidth;
        double availH = PageViewportGrid.ActualHeight;
        if (availW <= 16 || availH <= 16 || double.IsNaN(availW) || double.IsNaN(availH) || double.IsInfinity(availW) || double.IsInfinity(availH))
        {
            availW = 380;
            availH = 520;
        }

        double aspect = _imagePixelWidth / (double)Math.Max(1, _imagePixelHeight);
        if (double.IsNaN(aspect) || double.IsInfinity(aspect) || aspect <= 0) aspect = 0.75;
        double fittedW = availW;
        double fittedH = fittedW / aspect;
        if (fittedH > availH)
        {
            fittedH = availH;
            fittedW = fittedH * aspect;
        }

        fittedW = Math.Max(120, Math.Round(fittedW));
        fittedH = Math.Max(160, Math.Round(fittedH));
        if (double.IsNaN(fittedW) || double.IsInfinity(fittedW)) fittedW = 380;
        if (double.IsNaN(fittedH) || double.IsInfinity(fittedH)) fittedH = 520;

        PageSheetGrid.Width = fittedW;
        PageSheetGrid.Height = fittedH;
        if (LaserBeam is not null) LaserBeam.Width = PageSheetGrid.Width;
        if (ElementsCanvas is not null)
        {
            ElementsCanvas.Width = PageSheetGrid.Width;
            ElementsCanvas.Height = PageSheetGrid.Height;
            foreach (var (elem, frame) in _canvasElementFrames)
            {
                ApplyOverlayBounds(frame, elem.Box);
            }
        }
    }

    private void ApplyOverlayBounds(FrameworkElement element, OcrNormRect box)
    {
        if (double.IsNaN(box.X) || double.IsNaN(box.Y) || double.IsNaN(box.Width) || double.IsNaN(box.Height)) return;
        double cw = ElementsCanvas.Width > 0 ? ElementsCanvas.Width : Math.Max(100, PageViewportGrid.ActualWidth);
        double ch = ElementsCanvas.Height > 0 ? ElementsCanvas.Height : Math.Max(100, PageViewportGrid.ActualHeight);
        if (double.IsNaN(cw) || double.IsInfinity(cw) || cw <= 0) cw = 380;
        if (double.IsNaN(ch) || double.IsInfinity(ch) || ch <= 0) ch = 520;

        double left = Math.Clamp(box.X * cw, 0, Math.Max(0, cw - 4));
        double top = Math.Clamp(box.Y * ch, 0, Math.Max(0, ch - 4));
        double width = Math.Clamp(box.Width * cw, 8, Math.Max(8, cw - left));
        double height = Math.Clamp(box.Height * ch, 8, Math.Max(8, ch - top));

        if (double.IsNaN(left) || double.IsInfinity(left)) left = 0;
        if (double.IsNaN(top) || double.IsInfinity(top)) top = 0;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) width = 8;
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0) height = 8;

        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = width;
        element.Height = height;
    }

    private void AddPositionedOverlay(FrameworkElement element, OcrNormRect box)
    {
        if (double.IsNaN(box.X) || double.IsNaN(box.Y) || double.IsNaN(box.Width) || double.IsNaN(box.Height)) return;
        ApplyOverlayBounds(element, box);
        ElementsCanvas.Children.Add(element);
    }

    private async Task LoadPreviewBitmapAsync(byte[] jpegBytes)
    {
        if (jpegBytes is null || jpegBytes.Length == 0) return;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(jpegBytes.AsBuffer());
            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            DocumentImage.Source = bmp;
        }
        catch
        {
            // Ignore decode failures on synthetic mock bytes
        }
    }

    private void StartLaserSweep()
    {
        _idleLaserCts?.Cancel();
        _idleLaserCts = new CancellationTokenSource();
        _ = RunLaserSweepAsync(_idleLaserCts.Token);
    }

    private async Task RunLaserSweepAsync(CancellationToken token)
    {
        LaserBeam.Visibility = Visibility.Visible;
        int step = 0;
        while (!token.IsCancellationRequested)
        {
            float phase = 0.06f + (0.84f * ((step % 40) / 40f));
            double ch = PageSheetGrid.Height > 0 ? PageSheetGrid.Height : 520;
            LaserTransform.Y = Math.Clamp(phase * ch, 0, Math.Max(0, ch - 4));
            step++;
            try { await Task.Delay(35, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---- Event Handlers & Page Flicking ---------------------------------------------------------

    private void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        if (_session.CanPreviousPage)
        {
            _session.PreviousPage();
            RenderCurrentPage();
        }
    }

    private void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        if (_session.CanNextPage)
        {
            _session.NextPage();
            RenderCurrentPage();
        }
    }

    private void OnMarkdownScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarkdownScopeComboBox is null) return;
        _markdownScopeIsDocument = MarkdownScopeComboBox.SelectedIndex == 1;
        UpdateLiveMarkdownPreview();
    }

    private void OnCopyMarkdownClick(object sender, RoutedEventArgs e)
    {
        string? md = MarkdownPreviewTextBox?.Text;
        if (string.IsNullOrWhiteSpace(md)) return;
        if (CopyMarkdownButton is not null)
        {
            CopyFeedback.CopyWithTick(CopyMarkdownButton, CopyMarkdownIcon, md);
        }
        else
        {
            _ = ClipboardWriter.TrySetTextAsync(md);
        }
        string scopeLabel = _markdownScopeIsDocument ? "full document" : "current page";
        SetFooterStatusText($"Copied {scopeLabel} Markdown ({md.Length:N0} chars) to clipboard.");
    }

    private async void CopySelectedElementMarkdown()
    {
        if (_selectedElement is null || string.IsNullOrWhiteSpace(_selectedElement.CurrentMarkdown)) return;
        int id = _selectedElement.Id;
        string kindName = _selectedElement.KindDisplayName;
        var error = await ClipboardWriter.TrySetTextAsync(_selectedElement.CurrentMarkdown);
        if (error is null)
        {
            SetFooterStatusText($"Copied Element #{id} ({kindName}) Markdown to clipboard.");
        }
        else
        {
            SetFooterStatusText(error);
        }
    }

    private void OnQuickReclassifySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || _selectedElement is null || QuickReclassifyComboBox is null) return;
        if (QuickReclassifyComboBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<OcrBlockKind>(item.Tag?.ToString(), out var newKind) &&
            newKind != _selectedElement.Kind)
        {
            ReclassifyElement(_selectedElement, newKind);
        }
    }

    private void OnResetElementClick(object sender, RoutedEventArgs e)
    {
        ResetSelectedElement();
    }

    private void ResetSelectedElement()
    {
        if (_selectedElement is null || !_selectedElement.IsReclassified) return;
        ReclassifyElement(_selectedElement, _selectedElement.OriginalKind);
        SetFooterStatusText($"Reset Element #{_selectedElement.Id} to original {_selectedElement.KindDisplayName}.");
    }

    private void OnResetPageClick(object sender, RoutedEventArgs e)
    {
        ResetCurrentPageElements();
    }

    private void ResetCurrentPageElements()
    {
        var page = _session.CurrentPage;
        if (page is null) return;
        int resetCount = 0;
        foreach (var elem in page.Elements)
        {
            if (elem.IsReclassified)
            {
                ReclassifyElement(elem, elem.OriginalKind, refreshPreviewAndTelemetry: false);
                resetCount++;
            }
        }
        if (resetCount > 0)
        {
            UpdateSessionTelemetry();
            UpdateLiveMarkdownPreview();
            UpdateActionButtonsState();
            SetFooterStatusText($"Reset {resetCount} element{(resetCount == 1 ? "" : "s")} on Page {page.PageNumber} to original OCR classification.");
        }
    }

    private void SelectPreviousElement()
    {
        var page = _session.CurrentPage;
        if (page is null || page.Elements.Count == 0) return;
        if (_selectedElement is null)
        {
            SelectElement(page.Elements[^1]);
            return;
        }
        int idx = page.Elements.IndexOf(_selectedElement);
        int prevIdx = idx <= 0 ? page.Elements.Count - 1 : idx - 1;
        SelectElement(page.Elements[prevIdx]);
    }

    private void SelectNextElement()
    {
        var page = _session.CurrentPage;
        if (page is null || page.Elements.Count == 0) return;
        if (_selectedElement is null)
        {
            SelectElement(page.Elements[0]);
            return;
        }
        int idx = page.Elements.IndexOf(_selectedElement);
        int nextIdx = (idx < 0 || idx >= page.Elements.Count - 1) ? 0 : idx + 1;
        SelectElement(page.Elements[nextIdx]);
    }

    private void OnImportCtaClick(object sender, RoutedEventArgs e)
    {
        CompleteImport();
        this.Close();
    }

    private void CompleteImport()
    {
        string finalMarkdown = _session.GetDocumentMarkdown();
        _decisionTcs.TrySetResult(new OcrInspectionResult(
            Imported: true,
            Markdown: finalMarkdown,
            PageCount: _session.Pages.Count,
            ReclassifiedCount: _session.TotalReclassifications));
    }

    // The Closed handler reports the discard; going through the guard means reclassifications
    // are never thrown away by a stray Esc.
    private void OnDiscardClick(object sender, RoutedEventArgs e) => _requestClose();

    private bool IsTextOrComboInputFocused()
    {
        try
        {
            var focused = RootGrid.XamlRoot is not null
                ? FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject
                : null;
            while (focused is not null)
            {
                if (focused is TextBox) return true;
                if (focused is ComboBoxItem or SelectorItem or MenuFlyoutItemBase) return true;
                if (focused is ComboBox cb && cb.IsDropDownOpen) return true;
                focused = VisualTreeHelper.GetParent(focused);
            }
        }
        catch { }
        return false;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            bool inputFocused = IsTextOrComboInputFocused();

            // Global shortcuts that work anywhere in the studio
            if (e.Key == Windows.System.VirtualKey.Enter && ctrl)
            {
                e.Handled = true;
                OnImportCtaClick(this, new RoutedEventArgs());
                return;
            }
            if (e.Key == Windows.System.VirtualKey.C && ctrl && shift && !alt)
            {
                e.Handled = true;
                OnCopyMarkdownClick(this, new RoutedEventArgs());
                return;
            }
            if (e.Key == Windows.System.VirtualKey.R && ctrl && shift && !alt)
            {
                e.Handled = true;
                ResetCurrentPageElements();
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Escape && !inputFocused)
            {
                e.Handled = true;
                OnDiscardClick(this, new RoutedEventArgs());
                return;
            }

            // Do not hijack arrow keys, Home/End, or single-key shortcuts when editing/selecting inside a TextBox or open ComboBox
            if (inputFocused) return;

            if (e.Key == Windows.System.VirtualKey.C && ctrl && !shift && !alt)
            {
                if (_selectedElement is not null)
                {
                    e.Handled = true;
                    CopySelectedElementMarkdown();
                }
                return;
            }

            // Context menu key (Application or Shift+F10) for the selected element
            if (KeyboardContextMenu.IsContextMenuKey(e.Key))
            {
                // Toolbar/footer buttons with their own menus (Prev/Next page, Discard, Import…) open those.
                if (KeyboardContextMenu.IsFromBridgedControl(e)) return;
                e.Handled = true;
                if (_selectedElement is not null &&
                    _inspectorCards.TryGetValue(_selectedElement, out var card) &&
                    card.ContextFlyout is MenuFlyout flyout)
                {
                    flyout.ShowAt(card, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
                }
                else if (PageSheetBorder is not null)
                {
                    // Nothing selected: the page's own menu (copy, reset, page nav, import) rather
                    // than nothing at all.
                    KeyboardContextMenu.Invoke(PageSheetBorder, OnPageSheetRightTapped);
                }
                return;
            }

            if (ctrl || alt) return;

            if (e.Key == Windows.System.VirtualKey.Left || e.Key == Windows.System.VirtualKey.PageUp)
            {
                if (_session.CanPreviousPage)
                {
                    e.Handled = true;
                    OnPrevPageClick(this, new RoutedEventArgs());
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Right || e.Key == Windows.System.VirtualKey.PageDown)
            {
                if (_session.CanNextPage)
                {
                    e.Handled = true;
                    OnNextPageClick(this, new RoutedEventArgs());
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Home)
            {
                if (_session.CanPreviousPage)
                {
                    e.Handled = true;
                    _session.GoToFirstPage();
                    RenderCurrentPage();
                }
            }
            else if (e.Key == Windows.System.VirtualKey.End)
            {
                if (_session.CanNextPage)
                {
                    e.Handled = true;
                    _session.GoToLastPage();
                    RenderCurrentPage();
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Up || (int)e.Key == 219) // Up or '['
            {
                e.Handled = true;
                SelectPreviousElement();
            }
            else if (e.Key == Windows.System.VirtualKey.Down || (int)e.Key == 221) // Down or ']'
            {
                e.Handled = true;
                SelectNextElement();
            }
            else if (e.Key == Windows.System.VirtualKey.R && !shift)
            {
                if (_selectedElement?.IsReclassified == true)
                {
                    e.Handled = true;
                    ResetSelectedElement();
                }
            }
            else if (!shift)
            {
                int shortcutNum = e.Key switch
                {
                    >= Windows.System.VirtualKey.Number1 and <= Windows.System.VirtualKey.Number8
                        => (int)e.Key - (int)Windows.System.VirtualKey.Number0,
                    >= Windows.System.VirtualKey.NumberPad1 and <= Windows.System.VirtualKey.NumberPad8
                        => (int)e.Key - (int)Windows.System.VirtualKey.NumberPad0,
                    _ => 0
                };
                if (shortcutNum > 0 && _selectedElement is not null &&
                    OcrInspectionSession.TryMapShortcutNumberToKind(shortcutNum, out var newKind))
                {
                    e.Handled = true;
                    if (_selectedElement.Kind != newKind)
                    {
                        ReclassifyElement(_selectedElement, newKind);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnRootKeyDown error: {ex}");
        }
    }
}
