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

    private CancellationTokenSource? _idleLaserCts;
    private OcrInspectionElement? _selectedElement;
    private int _imagePixelWidth = 960;
    private int _imagePixelHeight = 1320;
    private bool _isReviewMode = false;
    private bool _markdownScopeIsDocument = false;

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

        this.Closed += (_, _) =>
        {
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
        this.DispatcherQueue.TryEnqueue(async () =>
        {
            EngineBadgeText.Text = string.IsNullOrWhiteSpace(preview.Engine) ? "MarkSmith OCR" : preview.Engine;
            _imagePixelWidth = Math.Max(1, preview.ImageWidth);
            _imagePixelHeight = Math.Max(1, preview.ImageHeight);
            CanvasMetricsText.Text = $"{_imagePixelWidth} × {_imagePixelHeight} px";

            string pageLabel = preview.PageCount > 1
                ? $"Page {preview.PageIndex + 1} of {preview.PageCount}"
                : $"Page {preview.PageNumber}";
            PageIndicatorText.Text = pageLabel;

            StatusBannerText.Text = $"Scanning & Decomposing {pageLabel} with {EngineBadgeText.Text}…";
            WordStreamText.Text = $"Calibrating page geometry & deskew angle…";
            StageProgressBar.IsIndeterminate = false;
            StageProgressBar.Value = 35;

            UpdatePageSheetDimensions();
            if (preview.Image.Length > 0)
            {
                await LoadPreviewBitmapAsync(preview.Image);
            }

            StartLaserSweep();
        });
    }

    public void PageRead(OcrPageTrace trace)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            var preview = new OcrPagePreview(
                trace.PageNumber,
                trace.PageNumber - 1,
                Math.Max(1, _session.Pages.Count + 1),
                Array.Empty<byte>(),
                _imagePixelWidth,
                _imagePixelHeight,
                EngineBadgeText.Text);

            var page = _session.AddOrUpdatePage(preview, trace);
            RenderCurrentPage();
            UpdateSessionTelemetry();

            WordStreamText.Text = $"Assembled {trace.Words.Count} words into {trace.Blocks.Count} blocks.";
            StageProgressBar.Value = 100;

            EnterReviewMode();
        });
    }

    /// <summary>
    /// Ingests a synthesized or digital document import sequence, playing the reading breakdown
    /// and transitioning directly into the interactive review mode (never auto-closing).
    /// </summary>
    public async Task PlayImportExperience(string markdown, string sourceLabel)
    {
        EngineBadgeText.Text = sourceLabel;
        var pages = OcrTrace.SynthesizePagesFromMarkdown(markdown, sourceLabel);
        if (pages.Count == 0) return;

        foreach (var (preview, trace) in pages)
        {
            _session.AddOrUpdatePage(preview, trace);
        }

        _session.CurrentPageIndex = 0;
        var firstPage = _session.CurrentPage;
        if (firstPage is not null && firstPage.Image.Length > 0)
        {
            _imagePixelWidth = firstPage.ImageWidth;
            _imagePixelHeight = firstPage.ImageHeight;
            CanvasMetricsText.Text = $"{_imagePixelWidth} × {_imagePixelHeight} px";
            UpdatePageSheetDimensions();
            await LoadPreviewBitmapAsync(firstPage.Image);
        }

        RenderCurrentPage();
        UpdateSessionTelemetry();
        EnterReviewMode();
    }

    // ---- Review Mode & Interactive State --------------------------------------------------------

    private void EnterReviewMode()
    {
        _isReviewMode = true;
        _idleLaserCts?.Cancel();
        LaserBeam.Visibility = Visibility.Collapsed;

        StatusBannerIcon.Glyph = "\uE73E"; // Checkmark
        StatusBannerIcon.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        StatusBannerText.Text = "✓ Document Scan Complete — Inspect Detected Elements & Choose Import Settings";
        StatusSubtext.Text = "Flick through pages to inspect detected elements. Click any element to reclassify it between Picture, Mosaic, Diagram, Table, Key-Value, or Text. Hit 'Import into MarkSmith' to apply.";

        UpdatePageFlickingState();
        UpdateLiveMarkdownPreview();
        FooterStatusText.Text = "Inspection Studio ready — Review detected elements before applying import.";
    }

    private void UpdatePageFlickingState()
    {
        PrevPageButton.IsEnabled = _session.CanPreviousPage;
        NextPageButton.IsEnabled = _session.CanNextPage;

        int totalPages = Math.Max(1, _session.Pages.Count);
        int currentNum = _session.CurrentPage?.PageNumber ?? (_session.CurrentPageIndex + 1);
        PageIndicatorText.Text = $"Page {currentNum} of {totalPages}";
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
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
        });

        var cardBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 7, 8, 7),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
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
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
            card.BorderThickness = new Thickness(isTarget ? 2 : 1);
        }

        SelectedElementBadgeText.Text = $"Element #{elem.Id} ({elem.KindDisplayName})";

        // Sync QuickReclassifyComboBox
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

        UpdateSessionTelemetry();
        UpdateLiveMarkdownPreview();
        FooterStatusText.Text = $"Reclassified Element #{elem.Id} to {elem.KindDisplayName}. Preview updated live.";
    }

    private void UpdateLiveMarkdownPreview()
    {
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
        HeaderElementsCountText.Text = _session.TotalElements.ToString();
        HeaderReclassifiedCountText.Text = _session.TotalReclassifications.ToString();
        WordCountBadgeText.Text = $"{_session.CurrentPage?.Words.Count ?? 0} words";

        SessionMetricsText.Text = $"{_session.Pages.Count} Page{(_session.Pages.Count == 1 ? "" : "s")} · " +
                                  $"{_session.TotalElements} Elements · " +
                                  $"{_session.TotalWords} Words · " +
                                  $"{_session.TotalReclassifications} Reclassified";
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
        double availW = PageViewportGrid.ActualWidth;
        double availH = PageViewportGrid.ActualHeight;
        if (availW <= 16 || availH <= 16)
        {
            availW = 380;
            availH = 520;
        }

        double aspect = _imagePixelWidth / (double)Math.Max(1, _imagePixelHeight);
        double fittedW = availW;
        double fittedH = fittedW / aspect;
        if (fittedH > availH)
        {
            fittedH = availH;
            fittedW = fittedH * aspect;
        }

        PageSheetGrid.Width = Math.Max(120, Math.Round(fittedW));
        PageSheetGrid.Height = Math.Max(160, Math.Round(fittedH));
        LaserBeam.Width = PageSheetGrid.Width;
        ElementsCanvas.Width = PageSheetGrid.Width;
        ElementsCanvas.Height = PageSheetGrid.Height;
    }

    private void AddPositionedOverlay(FrameworkElement element, OcrNormRect box)
    {
        double cw = ElementsCanvas.Width > 0 ? ElementsCanvas.Width : Math.Max(100, PageViewportGrid.ActualWidth);
        double ch = ElementsCanvas.Height > 0 ? ElementsCanvas.Height : Math.Max(100, PageViewportGrid.ActualHeight);

        double left = Math.Clamp(box.X * cw, 0, cw - 4);
        double top = Math.Clamp(box.Y * ch, 0, ch - 4);
        double width = Math.Clamp(box.Width * cw, 8, Math.Max(8, cw - left));
        double height = Math.Clamp(box.Height * ch, 8, Math.Max(8, ch - top));

        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = width;
        element.Height = height;

        ElementsCanvas.Children.Add(element);
    }

    private async Task LoadPreviewBitmapAsync(byte[] jpegBytes)
    {
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
        _markdownScopeIsDocument = MarkdownScopeComboBox.SelectedIndex == 1;
        UpdateLiveMarkdownPreview();
    }

    private void OnQuickReclassifySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedElement is null) return;
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
}
