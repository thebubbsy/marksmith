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
        _mapCanvas.RightTapped += OnMapRightTapped;
        _mapCanvas.DoubleTapped += OnMapDoubleTapped;

        var restBorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x2B, 0x2D, 0x42));
        var hoverBorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xAA, 0x4C, 0xC9, 0xF0));
        var border = new Border
        {
            Child = _mapCanvas,
            CornerRadius = new CornerRadius(8),
            BorderBrush = restBorderBrush,
            BorderThickness = new Thickness(1),
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(140) },
        };
        border.PointerEntered += (_, _) => border.BorderBrush = hoverBorderBrush;
        border.PointerExited += (_, _) => border.BorderBrush = restBorderBrush;
        Content = border;
        ToolTipService.SetToolTip(border, "Overview · Click or drag to pan, Double-click to zoom 100%, Right-click for options");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "Diagram overview minimap");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(this, "Click or drag to pan the diagram viewport");
        try { ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand); }
        catch { /* cursor is cosmetic */ }

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
            // Sequence lifelines, then connectors, so nodes sit on top.
            foreach (var n in vm.Nodes)
            {
                if (!n.HasLifeline) continue;
                _mapCanvas.Children.Add(new Line
                {
                    X1 = MapX(n.LifelineX), Y1 = MapY(n.LifelineTop),
                    X2 = MapX(n.LifelineX), Y2 = MapY(n.LifelineBottom),
                    Stroke = ConnectorBrush,
                    StrokeThickness = 1,
                    Opacity = 0.6,
                    IsHitTestVisible = false,
                });
            }
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
            if (vm.GetContentBounds() is { } b) Include(b.X - Pad, b.Y - Pad, b.Width + 2 * Pad, b.Height + 2 * Pad);
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
        var pt = e.GetCurrentPoint(_mapCanvas);
        if (pt.Properties.IsRightButtonPressed) return;

        _isNavigating = true;
        _mapCanvas.CapturePointer(e.Pointer);
        NavigateToPointer(e);
        e.Handled = true;
    }

    private void OnMapRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var menu = new MenuFlyout();

        var centerItem = new MenuFlyoutItem
        {
            Text = "Center view here",
            Icon = new FontIcon { Glyph = "\uE73E" }
        };
        centerItem.Click += (_, _) =>
        {
            if (TargetScrollViewer is not { } sv) return;
            var p = e.GetPosition(_mapCanvas);
            double zoom = Math.Max(0.01, sv.ZoomFactor);
            double worldX = p.X / _scale + _originX;
            double worldY = p.Y / _scale + _originY;
            var v = VisibleWorld(sv);
            sv.ChangeView((worldX - v.W / 2) * zoom, (worldY - v.H / 2) * zoom, null, disableAnimation: false);
        };
        menu.Items.Add(centerItem);

        var fitItem = new MenuFlyoutItem
        {
            Text = "Fit all in window",
            Icon = new FontIcon { Glyph = "\uE9A6" }
        };
        fitItem.Click += (_, _) =>
        {
            if (TargetScrollViewer is not { } sv) return;
            sv.ZoomToFactor(1.0f);
        };
        menu.Items.Add(fitItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var zoom100Item = new MenuFlyoutItem
        {
            Text = "Zoom to 100%",
            KeyboardAcceleratorTextOverride = "Ctrl+0",
            Icon = new FontIcon { Glyph = "\uE71E" }
        };
        zoom100Item.Click += (_, _) =>
        {
            TargetScrollViewer?.ZoomToFactor(1.0f);
        };
        menu.Items.Add(zoom100Item);

        var zoomInItem = new MenuFlyoutItem
        {
            Text = "Zoom in (+15%, Ctrl+=)",
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        zoomInItem.Click += (_, _) =>
        {
            if (TargetScrollViewer is { } sv) sv.ZoomToFactor(sv.ZoomFactor * 1.15f);
        };
        menu.Items.Add(zoomInItem);

        var zoomOutItem = new MenuFlyoutItem
        {
            Text = "Zoom out (-15%, Ctrl+-)",
            Icon = new FontIcon { Glyph = "\uE738" }
        };
        zoomOutItem.Click += (_, _) =>
        {
            if (TargetScrollViewer is { } sv) sv.ZoomToFactor(sv.ZoomFactor / 1.15f);
        };
        menu.Items.Add(zoomOutItem);

        if (ViewModel is { } vm)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var autoLayoutItem = new MenuFlyoutItem
            {
                Text = "Auto-arrange diagram nodes",
                Icon = new FontIcon { Glyph = "\uECA5" }
            };
            autoLayoutItem.Click += (_, _) => vm.ApplyAutoLayout(force: true);
            menu.Items.Add(autoLayoutItem);

            var snapItem = new MenuFlyoutItem
            {
                Text = vm.IsGridSnapEnabled ? "Disable grid snap" : "Enable grid snap",
                Icon = new FontIcon { Glyph = "\uE80A" }
            };
            snapItem.Click += (_, _) => vm.IsGridSnapEnabled = !vm.IsGridSnapEnabled;
            menu.Items.Add(snapItem);

            var statsItem = new MenuFlyoutItem
            {
                Text = "Copy diagram statistics",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            statsItem.Click += (_, _) =>
            {
                if (TargetScrollViewer is { } svTarget)
                {
                    var v = VisibleWorld(svTarget);
                    string stats = $"Mermaid diagram: {vm.Nodes.Count} nodes, {vm.Connectors.Count} connectors · Zoom {(svTarget.ZoomFactor * 100):0}% · Viewport {v.W:0}x{v.H:0} at ({v.X:0}, {v.Y:0})";
                    MarkSmith.Services.CopyFeedback.CopyWithBubble(this, stats, "Copied diagram statistics");
                }
            };
            menu.Items.Add(statsItem);
        }

        menu.ShowAt(_mapCanvas, e.GetPosition(_mapCanvas));
        e.Handled = true;
    }

    private void OnMapDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (TargetScrollViewer is not { } sv) return;
        sv.ZoomToFactor(1.0f);
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
