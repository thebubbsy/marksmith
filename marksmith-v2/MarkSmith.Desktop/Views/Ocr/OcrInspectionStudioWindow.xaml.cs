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
using Microsoft.UI.Xaml.Controls;
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
        ConfigureWindowChrome();
        SizeForDisplay();

        if (MarkdownScopeComboBox is not null && MarkdownScopeComboBox.SelectedIndex < 0)
        {
            MarkdownScopeComboBox.SelectedIndex = 0;
        }

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

                StatusBannerText.Text = $"Scanning & Decomposing {pageLabel} with {_currentEngineName}…";
                WordStreamText.Text = $"Calibrating page geometry & deskew angle…";
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
            StatusBannerText.Text = $"Analyzing & Decomposing {pageLabel} with {sourceLabel}…";
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
                if (StatusBannerText is not null) StatusBannerText.Text = $"Page {pageNumber}: {errorMessage}";
                if (StatusSubtext is not null) StatusSubtext.Text = "MarkSmith continued importing the rest of the document. You can inspect available pages and review before applying.";
                if (FooterStatusText is not null) FooterStatusText.Text = $"Page {pageNumber} issue: {errorMessage}";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReportPageError error: {ex}");
            }
        });
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
        if (StatusBannerText is not null) StatusBannerText.Text = "✓ Document Scan Complete — Inspect Detected Elements & Choose Import Settings";
        if (StatusSubtext is not null) StatusSubtext.Text = "Flick through pages to inspect detected elements. Click any element to reclassify it between Picture, Mosaic, Diagram, Table, Key-Value, or Text. Hit 'Import into MarkSmith' to apply.";

        UpdatePageFlickingState();
        RenderCurrentPage();
        UpdateLiveMarkdownPreview();
        if (FooterStatusText is not null)
            FooterStatusText.Text = "Inspection Studio active — Never auto-closes. Review detected elements before applying import.";
    }

    private void UpdatePageFlickingState()
    {
        if (PrevPageButton is not null) PrevPageButton.IsEnabled = _session.CanPreviousPage;
        if (NextPageButton is not null) NextPageButton.IsEnabled = _session.CanNextPage;

        int totalPages = Math.Max(1, _session.Pages.Count);
        int currentNum = _session.CurrentPage?.PageNumber ?? (_session.CurrentPageIndex + 1);
        if (PageIndicatorText is not null) PageIndicatorText.Text = $"Page {currentNum} of {totalPages}";
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

        UpdatePageFlickingState();
        UpdateLiveMarkdownPreview();
        UpdateSessionTelemetry();
    }

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

        var badge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(2),
            Background = new SolidColorBrush(strokeColor),
            Child = new TextBlock
            {
                Text = $"{elem.KindIconGlyph} {elem.KindDisplayName}",
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
            }
        };

        border.Child = badge;

        border.PointerEntered += (_, _) =>
        {
            border.BorderThickness = new Thickness(2.5);
            border.Background = new SolidColorBrush(Color.FromArgb(90, strokeColor.R, strokeColor.G, strokeColor.B));
        };

        border.PointerExited += (_, _) =>
        {
            bool isSel = ReferenceEquals(elem, _selectedElement);
            border.BorderThickness = new Thickness(isSel ? 3.0 : 1.5);
            border.Background = new SolidColorBrush(isSel
                ? Color.FromArgb(70, strokeColor.R, strokeColor.G, strokeColor.B)
                : fillColor);
        };

        border.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            SelectElement(elem);
        };

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
            Text = $"#{elem.Id} · {elem.KindDisplayName}",
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

        var kinds = new (OcrBlockKind Kind, string Label)[]
        {
            (OcrBlockKind.Picture, "🖼 Picture"),
            (OcrBlockKind.ShapeMosaic, "✨ Mosaic / Shape"),
            (OcrBlockKind.Diagram, "📐 Diagram"),
            (OcrBlockKind.Table, "📊 Table"),
            (OcrBlockKind.KeyValue, "🔑 Key-Value"),
            (OcrBlockKind.Heading, "📌 Heading"),
            (OcrBlockKind.List, "📑 List"),
            (OcrBlockKind.Paragraph, "📝 Text Block"),
        };

        int selectedIdx = 0;
        for (int i = 0; i < kinds.Length; i++)
        {
            var item = new ComboBoxItem { Content = kinds[i].Label, Tag = kinds[i].Kind };
            combo.Items.Add(item);
            if (kinds[i].Kind == elem.Kind) selectedIdx = i;
        }
        combo.SelectedIndex = selectedIdx;

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem cbi && cbi.Tag is OcrBlockKind newKind && newKind != elem.Kind)
            {
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

        cardBorder.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            SelectElement(elem);
        };

        return cardBorder;
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

        SelectedElementBadgeText.Text = $"Element #{elem.Id} ({elem.KindDisplayName})";

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

    private void ReclassifyElement(OcrInspectionElement elem, OcrBlockKind newKind)
    {
        elem.Reclassify(newKind);

        // Update canvas frame visual
        if (_canvasElementFrames.TryGetValue(elem, out var frame))
        {
            var (sc, fc) = GetKindPalette(elem.Kind);
            frame.BorderBrush = new SolidColorBrush(sc);
            frame.Background = new SolidColorBrush(fc);
            if (frame.Child is Border badge && badge.Child is TextBlock badgeText)
            {
                badge.Background = new SolidColorBrush(sc);
                badgeText.Text = $"{elem.KindIconGlyph} {elem.KindDisplayName}";
            }
        }

        // Keep the middle pane inspector card in sync
        if (_inspectorCards.TryGetValue(elem, out var card) && card.Child is StackPanel cardStack)
        {
            var (strokeColor, _) = GetKindPalette(elem.Kind);
            if (cardStack.Children.Count > 0 && cardStack.Children[0] is Grid headerGrid &&
                headerGrid.Children.Count > 0 && headerGrid.Children[0] is TextBlock titleText)
            {
                titleText.Text = $"#{elem.Id} · {elem.KindDisplayName}";
                titleText.Foreground = new SolidColorBrush(strokeColor);
            }
            if (cardStack.Children.Count > 1 && cardStack.Children[1] is ComboBox cardCombo)
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
            if (cardStack.Children.Count > 2 && cardStack.Children[2] is TextBlock snippetText)
            {
                snippetText.Text = elem.CurrentMarkdown.Length > 90
                    ? elem.CurrentMarkdown.Substring(0, 87) + "…"
                    : elem.CurrentMarkdown;
            }
        }

        // Keep the right pane selection inspector in sync if this element is currently selected
        if (ReferenceEquals(elem, _selectedElement))
        {
            SelectedElementBadgeText.Text = $"Element #{elem.Id} ({elem.KindDisplayName})";
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

        UpdateSessionTelemetry();
        UpdateLiveMarkdownPreview();
        if (FooterStatusText is not null)
            FooterStatusText.Text = $"Reclassified Element #{elem.Id} to {elem.KindDisplayName}. Preview updated live.";
    }

    private void UpdateLiveMarkdownPreview()
    {
        if (MarkdownPreviewTextBox is null) return;

        if (_session.Pages.Count == 0)
        {
            MarkdownPreviewTextBox.Text = string.Empty;
            return;
        }

        string md = _markdownScopeIsDocument
            ? _session.GetDocumentMarkdown()
            : (_session.CurrentPage?.AssembledPageMarkdown ?? string.Empty);

        MarkdownPreviewTextBox.Text = md;
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
            SessionMetricsText.Text = $"{_session.Pages.Count} Page{(_session.Pages.Count == 1 ? "" : "s")} · " +
                                      $"{_session.TotalElements} Elements · " +
                                      $"{_session.TotalWords} Words · " +
                                      $"{_session.TotalReclassifications} Reclassified";
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
        }
    }

    private void AddPositionedOverlay(FrameworkElement element, OcrNormRect box)
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

    private void OnQuickReclassifySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || _selectedElement is null || QuickReclassifyComboBox is null) return;
        if (QuickReclassifyComboBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<OcrBlockKind>(item.Tag?.ToString(), out var newKind) &&
            newKind != _selectedElement.Kind)
        {
            ReclassifyElement(_selectedElement, newKind);
            // Refresh middle pane inspector card ComboBox to match
            RenderCurrentPage();
            SelectElement(_selectedElement);
        }
    }

    private void OnResetElementClick(object sender, RoutedEventArgs e)
    {
        if (_selectedElement is null) return;
        _selectedElement.ResetToOriginal();
        RenderCurrentPage();
        SelectElement(_selectedElement);
        UpdateLiveMarkdownPreview();
        UpdateSessionTelemetry();
        if (FooterStatusText is not null)
            FooterStatusText.Text = $"Reset Element #{_selectedElement.Id} to original {_selectedElement.KindDisplayName}.";
    }

    private void OnImportCtaClick(object sender, RoutedEventArgs e)
    {
        string finalMarkdown = _session.GetDocumentMarkdown();
        _decisionTcs.TrySetResult(new OcrInspectionResult(
            Imported: true,
            Markdown: finalMarkdown,
            PageCount: _session.Pages.Count,
            ReclassifiedCount: _session.TotalReclassifications));
        this.Close();
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        _decisionTcs.TrySetResult(new OcrInspectionResult(
            Imported: false,
            Markdown: string.Empty,
            PageCount: _session.Pages.Count,
            ReclassifiedCount: 0));
        this.Close();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                OnDiscardClick(this, new RoutedEventArgs());
            }
            else if (e.Key == Windows.System.VirtualKey.Left || e.Key == Windows.System.VirtualKey.PageUp)
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
            else if (e.Key == Windows.System.VirtualKey.Enter &&
                     Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                e.Handled = true;
                OnImportCtaClick(this, new RoutedEventArgs());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnRootKeyDown error: {ex}");
        }
    }
}
