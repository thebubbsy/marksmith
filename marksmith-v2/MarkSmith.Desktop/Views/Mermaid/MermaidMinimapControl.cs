using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Dispatching;
using MarkSmith.ViewModels.Mermaid;

namespace MarkSmith.Views.Mermaid;

/// <summary>
/// A miniature overview of the diagram. It draws every node as a small block and every connector
/// as a hairline, scaled to fit the diagram plus the region currently visible in the main canvas
/// (outlined), so the overview is readable at any diagram size. Clicking or dragging on the minimap
/// recentres the canvas on that spot.
/// </summary>
public sealed class MermaidMinimapControl : UserControl
{
    // The map used to show the whole fixed 4000x3000 canvas world, so an ordinary diagram near the
    // origin was a few pixels in one corner. It now fits the content + viewport bounds instead.
    private const double MapWidth = 200;
    private const double MapHeight = 150;
    private const double Pad = 40;

    private static readonly SolidColorBrush NodeBrush = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x4C, 0xC9, 0xF0));
    private static readonly SolidColorBrush ConnectorBrush = new(Microsoft.UI.ColorHelper.FromArgb(0x99, 0x8D, 0x99, 0xAE));
    private static readonly SolidColorBrush ViewportFill = new(Microsoft.UI.ColorHelper.FromArgb(0x22, 0x4C, 0xC9, 0xF0));
    private static readonly SolidColorBrush ViewportStroke = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x4C, 0xC9, 0xF0));
    private static readonly SolidColorBrush MapBackground = new(Microsoft.UI.ColorHelper.FromArgb(0xE6, 0x21, 0x22, 0x34));

    private readonly Canvas _mapCanvas;
    private readonly Rectangle _viewportRect;
    // World -> map transform, recomputed each refresh (frozen while the user drags on the map so the
    // point under the pointer doesn't slide as the viewport moves).
    private double _scale = 0.05, _originX, _originY;
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
        ToolTipService.SetToolTip(Content, "Overview — click or drag to move around the diagram");

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
        if (!_isNavigating) ComputeTransform();

        if (ViewModel is { } vm)
        {
            // Connectors first so nodes sit on top.
            foreach (var c in vm.Connectors)
            {
                _mapCanvas.Children.Add(new Line
                {
                    X1 = MapX(c.SourceX), Y1 = MapY(c.SourceY),
                    X2 = MapX(c.TargetX), Y2 = MapY(c.TargetY),
                    Stroke = ConnectorBrush,
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                });
            }

            foreach (var n in vm.Nodes)
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(3, n.Width * _scale),
                    Height = Math.Max(2, n.Height * _scale),
                    RadiusX = 1.5, RadiusY = 1.5,
                    Fill = NodeBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, MapX(n.X));
                Canvas.SetTop(rect, MapY(n.Y));
                _mapCanvas.Children.Add(rect);
            }
        }

        UpdateViewportRect();
        _mapCanvas.Children.Add(_viewportRect);
    }

    private double MapX(double worldX) => (worldX - _originX) * _scale;
    private double MapY(double worldY) => (worldY - _originY) * _scale;

    // ScrollViewer offsets and viewport sizes are in zoomed (screen) units; content is unzoomed.
    private static (double X, double Y, double W, double H) VisibleWorld(ScrollViewer sv)
    {
        double zoom = Math.Max(0.01, sv.ZoomFactor);
        return (sv.HorizontalOffset / zoom, sv.VerticalOffset / zoom, sv.ViewportWidth / zoom, sv.ViewportHeight / zoom);
    }

    private void ComputeTransform()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Include(double x, double y, double w, double h)
        {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + w); maxY = Math.Max(maxY, y + h);
        }
        if (ViewModel is { } vm)
            foreach (var n in vm.Nodes) Include(n.X - Pad, n.Y - Pad, n.Width + 2 * Pad, n.Height + 2 * Pad);
        if (TargetScrollViewer is { } sv && sv.ViewportWidth > 0)
        {
            var v = VisibleWorld(sv);
            Include(v.X, v.Y, v.W, v.H);
        }
        if (minX == double.MaxValue) { _scale = 0.05; _originX = _originY = 0; return; }

        double w = Math.Max(1, maxX - minX), h = Math.Max(1, maxY - minY);
        _scale = Math.Min(MapWidth / w, MapHeight / h);
        // Centre the fitted bounds in the map.
        _originX = minX - (MapWidth / _scale - w) / 2;
        _originY = minY - (MapHeight / _scale - h) / 2;
    }

    private void UpdateViewportRect()
    {
        if (TargetScrollViewer is not { } sv)
        {
            _viewportRect.Visibility = Visibility.Collapsed;
            return;
        }

        _viewportRect.Visibility = Visibility.Visible;
        var v = VisibleWorld(sv);
        Canvas.SetLeft(_viewportRect, MapX(v.X));
        Canvas.SetTop(_viewportRect, MapY(v.Y));
        _viewportRect.Width = Math.Max(6, v.W * _scale);
        _viewportRect.Height = Math.Max(6, v.H * _scale);
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
        double worldX = p.X / _scale + _originX;
        double worldY = p.Y / _scale + _originY;
        var v = VisibleWorld(sv);
        // ChangeView takes offsets in zoomed units.
        sv.ChangeView((worldX - v.W / 2) * zoom, (worldY - v.H / 2) * zoom, null, disableAnimation: true);
    }
}
