using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Dispatching;
using MdToPdf.ViewModels.Mermaid;

namespace MdToPdf.Views.Mermaid;

/// <summary>
/// A miniature overview of the whole diagram canvas. It renders every node as a small block and
/// every connector as a hairline, scaled down from the 4000x3000 canvas world, and overlays a
/// rectangle showing the region currently visible in the main canvas ScrollViewer. Clicking or
/// dragging on the minimap recentres the canvas on that spot, so you can jump around a large
/// diagram without scrolling/panning the full canvas.
/// </summary>
public sealed class MermaidMinimapControl : UserControl
{
    // The canvas world is the fixed 4000x3000 InfiniteCanvasGrid. The minimap is a 200x150 view of
    // that world, which works out to a uniform 0.05 scale on both axes (4000/200 == 3000/150 == 20).
    private const double WorldWidth = 4000;
    private const double WorldHeight = 3000;
    private const double MapWidth = 200;
    private const double MapHeight = 150;
    private static readonly double WorldToMapScale = MapWidth / WorldWidth;

    private static readonly SolidColorBrush NodeBrush = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x4C, 0xC9, 0xF0));
    private static readonly SolidColorBrush ConnectorBrush = new(Microsoft.UI.ColorHelper.FromArgb(0x99, 0x8D, 0x99, 0xAE));
    private static readonly SolidColorBrush ViewportFill = new(Microsoft.UI.ColorHelper.FromArgb(0x33, 0x4C, 0xC9, 0xF0));
    private static readonly SolidColorBrush ViewportStroke = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x4C, 0xC9, 0xF0));
    private static readonly SolidColorBrush MapBackground = new(Microsoft.UI.ColorHelper.FromArgb(0xE6, 0x21, 0x22, 0x34));

    private readonly Canvas _mapCanvas;
    private readonly Rectangle _viewportRect;
    private DispatcherQueueTimer? _refreshTimer;
    private bool _isNavigating;

    /// <summary>The canvas ScrollViewer this minimap mirrors and navigates. Set by the host control.</summary>
    public ScrollViewer? TargetScrollViewer { get; set; }

    private MermaidStudioViewModel? ViewModel => DataContext as MermaidStudioViewModel;

    public MermaidMinimapControl()
    {
        Width = MapWidth;
        Height = MapHeight;

        _viewportRect = new Rectangle
        {
            Fill = ViewportFill,
            Stroke = ViewportStroke,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };

        _mapCanvas = new Canvas
        {
            Width = MapWidth,
            Height = MapHeight,
            Background = MapBackground,
        };
        _mapCanvas.Children.Add(_viewportRect);

        _mapCanvas.PointerPressed += OnMapPointerPressed;
        _mapCanvas.PointerMoved += OnMapPointerMoved;
        _mapCanvas.PointerReleased += OnMapPointerReleased;

        Content = new Border
        {
            Child = _mapCanvas,
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x2B, 0x2D, 0x42)),
            BorderThickness = new Thickness(1),
        };
        ToolTipService.SetToolTip(Content, "Minimap - click or drag to navigate");

        Loaded += (_, _) => StartRefreshTimer();
        Unloaded += (_, _) => StopRefreshTimer();
    }

    // The diagram can change in many ways (drag, add, delete, auto-layout, undo/redo), so rather than
    // wiring every individual event we repaint on a light 5fps tick while visible. A handful of small
    // shapes is trivial to redraw, and this guarantees the minimap is always correct.
    private void StartRefreshTimer()
    {
        if (_refreshTimer is null)
        {
            _refreshTimer = DispatcherQueue.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(200);
            _refreshTimer.IsRepeating = true;
            _refreshTimer.Tick += (_, _) => Refresh();
        }
        _refreshTimer.Start();
        Refresh();
    }

    private void StopRefreshTimer() => _refreshTimer?.Stop();

    /// <summary>Repaints the nodes, connectors and the visible-viewport rectangle.</summary>
    public void Refresh()
    {
        _mapCanvas.Children.Clear();

        if (ViewModel is { } vm)
        {
            // Connectors first so nodes sit on top.
            foreach (var c in vm.Connectors)
            {
                _mapCanvas.Children.Add(new Line
                {
                    X1 = c.SourceX * WorldToMapScale,
                    Y1 = c.SourceY * WorldToMapScale,
                    X2 = c.TargetX * WorldToMapScale,
                    Y2 = c.TargetY * WorldToMapScale,
                    Stroke = ConnectorBrush,
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                });
            }

            foreach (var n in vm.Nodes)
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(3, n.Width * WorldToMapScale),
                    Height = Math.Max(2, n.Height * WorldToMapScale),
                    Fill = NodeBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, n.X * WorldToMapScale);
                Canvas.SetTop(rect, n.Y * WorldToMapScale);
                _mapCanvas.Children.Add(rect);
            }
        }

        UpdateViewportRect();
        _mapCanvas.Children.Add(_viewportRect);
    }

    private void UpdateViewportRect()
    {
        if (TargetScrollViewer is not { } sv)
        {
            _viewportRect.Visibility = Visibility.Collapsed;
            return;
        }

        _viewportRect.Visibility = Visibility.Visible;
        double zoom = Math.Max(0.01, sv.ZoomFactor);
        // Offsets are already in content units; the viewport size is in screen DIPs, so divide by the
        // zoom factor to bring it back into content units before scaling down to the minimap.
        Canvas.SetLeft(_viewportRect, sv.HorizontalOffset * WorldToMapScale);
        Canvas.SetTop(_viewportRect, sv.VerticalOffset * WorldToMapScale);
        _viewportRect.Width = Math.Max(6, sv.ViewportWidth / zoom * WorldToMapScale);
        _viewportRect.Height = Math.Max(6, sv.ViewportHeight / zoom * WorldToMapScale);
    }

    private void OnMapPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isNavigating = true;
        _mapCanvas.CapturePointer(e.Pointer);
        NavigateToPointer(e);
        e.Handled = true;
    }

    private void OnMapPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isNavigating) return;
        NavigateToPointer(e);
        e.Handled = true;
    }

    private void OnMapPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isNavigating = false;
        _mapCanvas.ReleasePointerCapture(e.Pointer);
    }

    // Centre the main canvas viewport on the world-space point under the pointer.
    private void NavigateToPointer(PointerRoutedEventArgs e)
    {
        if (TargetScrollViewer is not { } sv) return;
        var p = e.GetCurrentPoint(_mapCanvas).Position;
        double zoom = Math.Max(0.01, sv.ZoomFactor);
        double contentX = p.X / WorldToMapScale;
        double contentY = p.Y / WorldToMapScale;
        double halfViewportW = sv.ViewportWidth / zoom / 2;
        double halfViewportH = sv.ViewportHeight / zoom / 2;
        sv.ChangeView(contentX - halfViewportW, contentY - halfViewportH, null, disableAnimation: true);
    }
}
