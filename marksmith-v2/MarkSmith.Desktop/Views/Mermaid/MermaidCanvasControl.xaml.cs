using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MarkSmith.Services;
using MarkSmith.ViewModels.Mermaid;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Point = Windows.Foundation.Point;
using Rect = MarkSmith.ViewModels.Mermaid.Rect;

namespace MarkSmith.Views.Mermaid;

public sealed partial class MermaidCanvasControl : UserControl
{
    private bool _isDraggingNode;
    private DiagramNodeViewModel? _draggedNode;
    private Point _pointerStartPos;
    private Point _nodeStartPos;
    private Dictionary<DiagramNodeViewModel, Point>? _initialSelectedNodePositions;
    private bool _dragSnapshotTaken;

    private bool _isRubberbanding;
    private Point _rubberbandStartPoint;

    private bool _isPanning;
    private Point _panStartMousePos;
    private double _panStartScrollX;
    private double _panStartScrollY;

    // Right-click opens a context menu on release if the pointer barely moved; a right-DRAG still
    // pans (so existing muscle memory isn't broken). Tracked here.
    private bool _rightClickPending;
    private Point _rightClickStartPos;
    private DiagramNodeViewModel? _rightClickNode;
    private DiagramConnectorViewModel? _rightClickConnector;

    private bool _isDrawingConnector;
    private DiagramNodeViewModel? _connectorSourceNode;
    private string _connectorSourceAnchor = "Bottom";
    // The node currently highlighted as the prospective connector drop target (so it can be
    // un-highlighted when the pointer moves off it or the draw completes).
    private DiagramNodeViewModel? _currentConnectionTarget;

    // Corner resize handles: drag a selected node's corner to change its Width/Height (and X/Y
    // for the NW/NE/SW edges that move). One undo snapshot per resize gesture.
    private bool _isResizingNode;
    private DiagramNodeViewModel? _resizeNode;
    private string _resizeDirection = string.Empty; // "ResizeNW" / "ResizeNE" / "ResizeSW" / "ResizeSE"
    private Point _resizeStartPointer;
    private double _resizeStartX, _resizeStartY, _resizeStartW, _resizeStartH;
    private bool _resizeSnapshotTaken;
    private const double MinNodeWidth = 40;
    private const double MinNodeHeight = 24;

    private DiagramNodeViewModel? _editingNode;
    private DiagramConnectorViewModel? _editingConnector;

    public MermaidStudioViewModel? ViewModel => DataContext as MermaidStudioViewModel;

    public MermaidCanvasControl()
    {
        InitializeComponent();
        // "Right-click for options" menus: open them from Shift+F10 / the Menu key too.
        KeyboardContextMenu.BridgeAll(
            (ZoomResetButton, OnZoomResetRightTapped),
            (ZoomOutButton, OnZoomResetRightTapped),
            (ZoomInButton, OnZoomResetRightTapped),
            (ZoomFitButton, OnZoomResetRightTapped));

        // The minimap mirrors and navigates the main canvas ScrollViewer.
        MinimapControl.TargetScrollViewer = CanvasScrollViewer;
        CanvasScrollViewer.SizeChanged += (_, _) => UpdateMinimapVisibility();

        ConnectorsItemsControl.PointerPressed += OnConnectorsItemsControlPointerPressed;
        // Capture lost mid-gesture (Alt+Tab, a dialog, a second button): the gesture is over and
        // whatever it moved stays where it is. Without this a node drag outlived the lost capture
        // and the node kept following the pointer on plain hover.
        InfiniteCanvasGrid.PointerCaptureLost += (_, _) => EndGesture(restore: false);
        NodesItemsControl.PointerCaptureLost += (_, _) => EndGesture(restore: false);

        _hoverExitTimer = DispatcherQueue.CreateTimer();
        _hoverExitTimer.Interval = TimeSpan.FromMilliseconds(220);
        _hoverExitTimer.IsRepeating = false;
        _hoverExitTimer.Tick += (_, _) => FlushHoverExit();
        ConnectorsItemsControl.DoubleTapped += OnConnectorsItemsControlDoubleTapped;

        NodesItemsControl.PointerPressed += OnNodesItemsControlPointerPressed;
        NodesItemsControl.PointerMoved += OnNodesItemsControlPointerMoved;
        NodesItemsControl.PointerReleased += OnNodesItemsControlPointerReleased;
        NodesItemsControl.DoubleTapped += OnNodesItemsControlDoubleTapped;

        CanvasScrollViewer.ViewChanged += OnCanvasViewChanged;

        // Only the static toolbar chrome (zoom in/out/fit) is covered here — the per-node quick-add
        // buttons live in NodesItemsControl's DataTemplate and are polished individually as each
        // node is realized (see OnNodeTemplateLoaded).
        HoverPolish.Track(this);

        DataContextChanged += (_, _) => WatchNodesForEmptyHint();
    }

    // ---- Empty-canvas hint: shown while the diagram has no nodes ----------------------------
    private MermaidStudioViewModel? _hintVm;
    private System.Collections.Specialized.INotifyCollectionChanged? _hintNodes;

    private void WatchNodesForEmptyHint()
    {
        if (_hintVm != null) _hintVm.PropertyChanged -= OnHintVmPropertyChanged;
        _hintVm = ViewModel;
        if (_hintVm != null) _hintVm.PropertyChanged += OnHintVmPropertyChanged;
        WatchNodeCollection();
    }

    private void OnHintVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Loading a template or a diagram from the document can swap the whole collection.
        if (e.PropertyName == nameof(MermaidStudioViewModel.Nodes)) WatchNodeCollection();
    }

    private void WatchNodeCollection()
    {
        if (_hintNodes != null) _hintNodes.CollectionChanged -= OnHintNodesChanged;
        _hintNodes = _hintVm?.Nodes;
        if (_hintNodes != null) _hintNodes.CollectionChanged += OnHintNodesChanged;
        UpdateEmptyCanvasHint();
    }

    private void OnHintNodesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateEmptyCanvasHint();

    private void UpdateEmptyCanvasHint() =>
        EmptyCanvasHint.Visibility = _hintVm is { Nodes.Count: 0 } ? Visibility.Visible : Visibility.Collapsed;

    #region Zoom Controls

    // The 200×150 overview covered a third of a small canvas (at the window's minimum it sat on the
    // sample's decision node), so it steps aside below this size and comes back when there is room.
    // It takes no focus, so collapsing it can't strand the keyboard.
    internal const double MinimapMinViewportWidth = 600;
    internal const double MinimapMinViewportHeight = 420;

    private void UpdateMinimapVisibility()
    {
        bool fits = CanvasScrollViewer.ActualWidth >= MinimapMinViewportWidth
                    && CanvasScrollViewer.ActualHeight >= MinimapMinViewportHeight;
        var want = fits ? Visibility.Visible : Visibility.Collapsed;
        if (MinimapControl.Visibility == want) return;
        MinimapControl.Visibility = want;
        if (fits) MinimapControl.Refresh();
    }

    private void OnCanvasViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (CanvasScrollViewer == null) return;

        if (ZoomLevelText != null)
        {
            int zoomPercent = (int)Math.Round(CanvasScrollViewer.ZoomFactor * 100);
            ZoomLevelText.Text = $"{zoomPercent}%";
        }

        // Sync the ScrollViewer's zoom back to the ViewModel so the toolbar slider stays in sync.
        if (ViewModel is MermaidStudioViewModel vm)
        {
            double rounded = Math.Round(CanvasScrollViewer.ZoomFactor, 2);
            if (Math.Abs(vm.ZoomFactor - rounded) > 0.001)
                vm.ZoomFactor = rounded;
        }
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomOut();

    private void OnZoomResetClick(object sender, RoutedEventArgs e) => ZoomReset();

    private void OnZoomResetRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var anchor = sender as FrameworkElement ?? (FrameworkElement)InfiniteCanvasGrid;
        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();

        var actualItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Reset zoom to 100%",
            KeyboardAcceleratorTextOverride = "Ctrl+0",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE71E" }
        };
        actualItem.Click += (_, _) => ZoomReset();
        menu.Items.Add(actualItem);

        var fitItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Fit whole diagram in view",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE9A6" }
        };
        fitItem.Click += (_, _) => FitToContent();
        menu.Items.Add(fitItem);

        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());

        var subStops = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
        {
            Text = "Preset zoom levels",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE71D" }
        };

        var stops = new (string label, float factor)[]
        {
            ("25%", 0.25f),
            ("50%", 0.50f),
            ("75%", 0.75f),
            ("100%", 1.00f),
            ("125%", 1.25f),
            ("150%", 1.50f),
            ("200%", 2.00f)
        };

        float currentZoom = (float)CanvasScrollViewer.ZoomFactor;

        foreach (var (lbl, factor) in stops)
        {
            // Radio items, like every other "pick one" menu: the level in use is checked.
            var item = new Microsoft.UI.Xaml.Controls.RadioMenuFlyoutItem
            {
                Text = lbl,
                GroupName = "MermaidZoomPreset",
                IsChecked = Math.Abs(currentZoom - factor) < 0.04f
            };
            float target = factor;
            item.Click += (_, _) => ZoomTo(target);
            subStops.Items.Add(item);
        }
        menu.Items.Add(subStops);

        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());

        var copyZoomItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Copy zoom level",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE8C8" }
        };
        // ClipboardWriter.Set threw on a busy clipboard (the app-wide handler ate it): no copy, no word.
        copyZoomItem.Click += async (_, _) =>
        {
            var text = ZoomLevelText?.Text ?? $"{Math.Round(CanvasScrollViewer.ZoomFactor * 100):0}%";
            var error = await ClipboardWriter.TrySetTextAsync(text);
            if (ViewModel != null) ViewModel.StatusText = error is null ? $"Copied zoom level: {text}" : $"The zoom level wasn't copied. {error}";
        };
        menu.Items.Add(copyZoomItem);

        menu.ShowAt(anchor, KeyboardContextMenu.PointIn(e, anchor));
    }

    private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowContextMenu(KeyboardContextMenu.PointIn(e, InfiniteCanvasGrid));
    }

    private void OnZoomFitClick(object sender, RoutedEventArgs e)
    {
        FitToContent();
    }

    // Public zoom entry points so the Studio's keyboard accelerators (Ctrl+= / Ctrl+- / Ctrl+0) can
    // drive the canvas without duplicating the zoom math here. The buttons step through the usual
    // editor stops (they used to add 0.2, so 100% went 120, 140 and back down to 20, 40...), and
    // every zoom keeps the middle of the view where it was: a bare ChangeView(null, null, zoom)
    // zooms about the top-left corner, so the diagram slid out of view.
    public void ZoomIn() => ZoomTo(MarkSmith.Core.Mermaid.Routing.ZoomSteps.Next(CanvasScrollViewer.ZoomFactor, CanvasScrollViewer.MaxZoomFactor));

    public void ZoomOut() => ZoomTo(MarkSmith.Core.Mermaid.Routing.ZoomSteps.Previous(CanvasScrollViewer.ZoomFactor, CanvasScrollViewer.MinZoomFactor));

    public void ZoomReset() => ZoomTo(1.0);

    /// <summary>Sets the canvas zoom to an explicit factor (driven by the toolbar slider).</summary>
    public void SetZoomFactor(double factor)
    {
        if (Math.Abs(CanvasScrollViewer.ZoomFactor - factor) > 0.001)
            ZoomTo(factor);
    }

    private void ZoomTo(double factor)
    {
        float zoom = (float)Math.Clamp(factor, CanvasScrollViewer.MinZoomFactor, CanvasScrollViewer.MaxZoomFactor);
        double old = Math.Max(0.01, CanvasScrollViewer.ZoomFactor);
        double halfW = CanvasScrollViewer.ViewportWidth / 2, halfH = CanvasScrollViewer.ViewportHeight / 2;
        // The canvas point at the middle of the view, then the offsets that put it back there.
        double cx = (CanvasScrollViewer.HorizontalOffset + halfW) / old;
        double cy = (CanvasScrollViewer.VerticalOffset + halfH) / old;
        CanvasScrollViewer.ChangeView(Math.Max(0, cx * zoom - halfW), Math.Max(0, cy * zoom - halfH), zoom);
    }

    /// <summary>Fit once the freshly loaded nodes have been measured and laid out.</summary>
    public void FitToContentAfterLayout()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            UpdateLayout();
            FitToContent();
        });
    }

    public void FitToContent()
    {
        var vm = ViewModel;
        if (vm == null || vm.Nodes.Count == 0)
        {
            CanvasScrollViewer.ChangeView(0, 0, 1.0f);
            return;
        }

        // True fit-to-content: compute the bounding box of every node, derive the zoom
        // that fits it into the current viewport (with a comfort margin), and center it.
        const double pad = 60;
        // Nodes plus what hangs off them (sequence lifelines, self-call loops).
        var bounds = vm.GetContentBounds()!.Value;
        double minX = bounds.Left, minY = bounds.Top, maxX = bounds.Right, maxY = bounds.Bottom;

        double contentW = maxX - minX + pad * 2;
        double contentH = maxY - minY + pad * 2;
        double vpW = CanvasScrollViewer.ViewportWidth;
        // The minimap sits over the bottom-left corner; fit into the band above it.
        double vpH = CanvasScrollViewer.ViewportHeight - (MinimapControl.Visibility == Visibility.Visible ? MinimapControl.ActualHeight + 20 : 0);
        if (vpW <= 0 || vpH <= 0)
        {
            CanvasScrollViewer.ChangeView(0, 0, 1.0f);
            return;
        }

        // Never magnify past 100%: a three-node diagram used to "fit" at 400%.
        float zoom = (float)Math.Clamp(
            Math.Min(1.0, Math.Min(vpW / contentW, vpH / contentH)),
            CanvasScrollViewer.MinZoomFactor,
            CanvasScrollViewer.MaxZoomFactor);

        // Offsets are in un-scaled content coordinates; center the bounding box.
        double offsetX = (minX - pad) - (vpW / zoom - contentW) / 2;
        double offsetY = (minY - pad) - (vpH / zoom - contentH) / 2;

        // ChangeView takes offsets in zoomed pixels, not canvas units: at any fit below 100% the
        // unscaled offset overshot and the diagram landed off to the left/top of the viewport.
        CanvasScrollViewer.ChangeView(Math.Max(0, offsetX * zoom), Math.Max(0, offsetY * zoom), zoom);
    }

    #endregion

    #region Drag & Drop from Palette

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains("MermaidShapeType"))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            // Say what a drop does instead of the shell's generic "Copy" badge.
            e.DragUIOverride.Caption = "Add to diagram";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private async void OnCanvasDrop(object sender, DragEventArgs e)
    {
        Point dropPos = e.GetPosition(InfiniteCanvasGrid);
        if (e.DataView.Contains("MermaidShapeType") && ViewModel != null)
        {
            string category = e.DataView.Contains("MermaidCategory")
                ? (await e.DataView.GetDataAsync("MermaidCategory") as string ?? "Flowchart")
                : "Flowchart";

            string shapeType = await e.DataView.GetDataAsync("MermaidShapeType") as string ?? "Rectangle";
            string text = e.DataView.Contains("MermaidText")
                ? (await e.DataView.GetDataAsync("MermaidText") as string ?? "New Node")
                : "New Node";

            var paletteItem = new MermaidPaletteItem
            {
                Category = category,
                ShapeType = shapeType,
                DefaultText = text
            };

            // Centred under the pointer: the shape lands where it was let go, not hanging off it.
            ViewModel.AddNodeFromPalette(paletteItem, dropPos.X, dropPos.Y, centreOnPoint: true);
        }
    }

    #endregion

    #region Interactive Node Moving & Anchors

    private void OnNodesItemsControlPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(InfiniteCanvasGrid).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.OriginalSource is FrameworkElement element)
        {
            // Check if user grabbed a corner RESIZE handle (must run before the anchor check —
            // both carry a string Tag + node DataContext, and a resize grab must not start a
            // connector draw).
            if (element.Tag is string resizeTag && resizeTag.StartsWith("Resize", StringComparison.Ordinal)
                && element.DataContext is DiagramNodeViewModel resizeNodeVM)
            {
                _isResizingNode = true;
                _resizeNode = resizeNodeVM;
                _resizeDirection = resizeTag;
                _resizeSnapshotTaken = false;
                _resizeStartPointer = e.GetCurrentPoint(InfiniteCanvasGrid).Position;
                _resizeStartX = resizeNodeVM.X;
                _resizeStartY = resizeNodeVM.Y;
                _resizeStartW = resizeNodeVM.Width;
                _resizeStartH = resizeNodeVM.Height;

                if (ViewModel != null && !resizeNodeVM.IsSelected)
                    ViewModel.SelectNode(resizeNodeVM, false);

                NodesItemsControl.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            // Check if user clicked an Anchor handle dot
            if (element.Tag is string anchorTag && element.DataContext is DiagramNodeViewModel anchorNodeVM)
            {
                _isDrawingConnector = true;
                _connectorSourceNode = anchorNodeVM;
                _connectorSourceAnchor = anchorTag;

                var vmPoint = anchorNodeVM.GetAnchorPoint(_connectorSourceAnchor);
                Point srcPoint = new Point((float)vmPoint.X, (float)vmPoint.Y);

                DraftConnectorPath.Data = new PathGeometry
                {
                    Figures = { new PathFigure { StartPoint = srcPoint, Segments = { new LineSegment { Point = srcPoint } } } }
                };
                DraftConnectorPath.Visibility = Visibility.Visible;

                NodesItemsControl.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            // Check if user clicked a Node
            if (element.DataContext is DiagramNodeViewModel nodeVM)
            {
                bool isMultiSelect = (e.KeyModifiers & (Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift)) != 0;
                bool isAltDuplicate = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Menu) != 0;

                // Alt+drag: stamp a duplicate and drag the copy (Figma/Illustrator convention —
                // Alt is used because Ctrl is already taken by multi-select).
                if (isAltDuplicate && ViewModel != null)
                {
                    nodeVM = ViewModel.DuplicateSingleNodeForDrag(nodeVM);
                }
                else if (ViewModel != null)
                {
                    if (!nodeVM.IsSelected && !isMultiSelect)
                    {
                        ViewModel.SelectNode(nodeVM, false);
                    }
                    else if (isMultiSelect)
                    {
                        ViewModel.SelectNode(nodeVM, true);
                    }
                }

                FlushHoverExit();
                _isDraggingNode = true;
                _draggedNode = nodeVM;
                _dragSnapshotTaken = false;
                _pointerStartPos = e.GetCurrentPoint(InfiniteCanvasGrid).Position;
                _nodeStartPos = new Point(nodeVM.X, nodeVM.Y);

                if (ViewModel != null && ViewModel.SelectedNodes.Count > 0)
                {
                    _initialSelectedNodePositions = ViewModel.SelectedNodes.ToDictionary(n => n, n => new Point(n.X, n.Y));
                }
                else
                {
                    _initialSelectedNodePositions = new Dictionary<DiagramNodeViewModel, Point> { [nodeVM] = new Point(nodeVM.X, nodeVM.Y) };
                }

                NodesItemsControl.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }
    }

    private void OnNodesItemsControlPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isResizingNode && _resizeNode != null && ViewModel != null)
        {
            // One undo step per resize gesture (taken on the first real move).
            if (!_resizeSnapshotTaken)
            {
                ViewModel.SnapshotForUndo();
                _resizeSnapshotTaken = true;
            }

            Point cur = e.GetCurrentPoint(InfiniteCanvasGrid).Position;
            ApplyResize(cur.X - _resizeStartPointer.X, cur.Y - _resizeStartPointer.Y);
            ViewModel.UpdateConnectedConnectors(_resizeNode);
            e.Handled = true;
            return;
        }

        if (_isDraggingNode && _draggedNode != null && ViewModel != null && _initialSelectedNodePositions != null)
        {
            // Snapshot once per drag (on the first real move) so a whole reposition is a single undo
            // step, and a mere select-click (no movement) never pollutes the undo stack.
            if (!_dragSnapshotTaken)
            {
                ViewModel.SnapshotForUndo();
                _dragSnapshotTaken = true;
            }
            Point currentPos = e.GetCurrentPoint(InfiniteCanvasGrid).Position;
            double deltaX = currentPos.X - _pointerStartPos.X;
            double deltaY = currentPos.Y - _pointerStartPos.Y;

            // Smart alignment: compute a magnetic snap (and guide lines) against sibling nodes based
            // on the primary node's proposed position, then apply the same offset to the whole
            // selection so multi-drag stays coherent.
            double primaryX = Math.Max(10, _nodeStartPos.X + deltaX);
            double primaryY = Math.Max(10, _nodeStartPos.Y + deltaY);
            var (snapDx, snapDy) = ComputeAlignmentSnap(_draggedNode, primaryX, primaryY);

            foreach (var kvp in _initialSelectedNodePositions)
            {
                var node = kvp.Key;
                var startPos = kvp.Value;

                double newX = Math.Max(10, startPos.X + deltaX + snapDx);
                double newY = Math.Max(10, startPos.Y + deltaY + snapDy);

                if (ViewModel.IsGridSnapEnabled)
                {
                    newX = Math.Round(newX / ViewModel.GridSnapSize) * ViewModel.GridSnapSize;
                    newY = Math.Round(newY / ViewModel.GridSnapSize) * ViewModel.GridSnapSize;
                }

                node.X = newX;
                node.Y = newY;
                ViewModel.UpdateConnectedConnectors(node);
            }
            e.Handled = true;
        }
    }

    private void OnNodesItemsControlPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isResizingNode)
        {
            _isResizingNode = false;
            _resizeNode = null;
            NodesItemsControl.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        if (_isDraggingNode)
        {
            _isDraggingNode = false;
            _draggedNode = null;
            _initialSelectedNodePositions = null;
            ClearAlignmentGuides();
            NodesItemsControl.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnQuickAddButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string dirTag && btn.DataContext is DiagramNodeViewModel nodeVM && ViewModel != null)
        {
            var newNode = ViewModel.QuickAddNode(nodeVM, dirTag);
            if (newNode != null)
            {
                StartNodeInPlaceEdit(newNode);
            }
        }
    }

    private void OnQuickAddButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button btn || btn.Tag is not string dirTag || btn.DataContext is not DiagramNodeViewModel nodeVM || ViewModel == null)
            return;

        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();

        void SpawnShape(string shapeName, string? shapeType = null, string? defaultLabel = null)
        {
            var newNode = ViewModel.QuickAddNode(nodeVM, dirTag);
            if (newNode != null)
            {
                if (!string.IsNullOrEmpty(shapeType))
                {
                    newNode.Shape = shapeType;
                    if (!string.IsNullOrEmpty(defaultLabel))
                        newNode.LabelText = defaultLabel;
                    ViewModel.UpdateConnectedConnectors(newNode);
                }
                StartNodeInPlaceEdit(newNode);
            }
        }

        var defaultItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = $"Add connected shape ({dirTag})",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE710" }
        };
        defaultItem.Click += (_, _) => SpawnShape("Default");
        menu.Items.Add(defaultItem);

        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());

        var rectItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Add Process / Rectangle",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE710" }
        };
        rectItem.Click += (_, _) => SpawnShape("Rectangle", "Rectangle", "Process");
        menu.Items.Add(rectItem);

        var decisionItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Add Decision / Diamond",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE790" }
        };
        decisionItem.Click += (_, _) => SpawnShape("Decision", "Rhombus", "Decision");
        menu.Items.Add(decisionItem);

        var circleItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Add Circle / State",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uEA3B" }
        };
        circleItem.Click += (_, _) => SpawnShape("Circle", "Circle", "State");
        menu.Items.Add(circleItem);

        var dbItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Add Database Cylinder",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE74C" }
        };
        dbItem.Click += (_, _) => SpawnShape("Database", "CylindricalDatabase", "Database");
        menu.Items.Add(dbItem);

        var cloudItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Add Cloud",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE753" }
        };
        cloudItem.Click += (_, _) => SpawnShape("Cloud", "Cloud", "Cloud");
        menu.Items.Add(cloudItem);

        var noteItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Add Note / Subroutine",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE70B" }
        };
        noteItem.Click += (_, _) => SpawnShape("Note", "Subroutine", "Subroutine");
        menu.Items.Add(noteItem);

        menu.ShowAt(btn, KeyboardContextMenu.PointIn(e, btn));
    }

    // ---- Hover glow + anchor reveal -----------------------------------------------------------
    // Setting IsHovered drives the hover ring and reveals the connector anchor dots (bound to
    // ShowAnchors in the node template). Hover is suppressed mid-drag so the glow doesn't flicker
    // as the pointer crosses other nodes during a move.
    // Leaving a node hides its anchors and quick-add arrows after a short grace, so the pointer can
    // cross the gap between the node and an arrow (or cut a corner) without them vanishing first.
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hoverExitTimer;
    private DiagramNodeViewModel? _hoverExitPending;

    private void OnNodePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingNode || _isResizingNode || _isDrawingConnector) return;
        if (sender is FrameworkElement { DataContext: DiagramNodeViewModel node })
        {
            if (!ReferenceEquals(_hoverExitPending, node)) FlushHoverExit();
            else { _hoverExitTimer.Stop(); _hoverExitPending = null; }
            node.IsHovered = true;
        }
    }

    private void OnNodePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DiagramNodeViewModel node })
        {
            FlushHoverExit();
            _hoverExitPending = node;
            _hoverExitTimer.Start();
        }
    }

    private void FlushHoverExit()
    {
        _hoverExitTimer.Stop();
        if (_hoverExitPending is { } node) node.IsHovered = false;
        _hoverExitPending = null;
    }

    // Each node is its own DataTemplate instance realized on demand, so the app-wide hover-lift
    // pass at construction time never sees the quick-add direction buttons inside it. Loaded
    // fires once per realized node, so wire them up here instead, along with the cursors that say
    // what each part does: move on the body, a crosshair on the connector dots, diagonal arrows on
    // the resize corners and a hand on the quick-add arrows.
    private void OnNodeTemplateLoaded(object sender, RoutedEventArgs e)
    {
        var root = (FrameworkElement)sender;
        HoverPolish.Apply(root);
        SetCursor(root, Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
        if (root.DataContext is DiagramNodeViewModel node) ApplyZIndex(node);

        foreach (var child in Descendants(root))
        {
            switch (child)
            {
                case Button button:
                    SetCursor(button, Microsoft.UI.Input.InputSystemCursorShape.Hand);
                    // The quick-add arrows' shape chooser: Shift+F10 / Menu key as well as right-click.
                    if (button.Tag is "Top" or "Right" or "Bottom" or "Left")
                        KeyboardContextMenu.Bridge(button, OnQuickAddButtonRightTapped);
                    break;
                case Grid { Tag: "ResizeNW" or "ResizeSE" } nwse:
                    SetCursor(nwse, Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast);
                    ConfigureResizeCorner(nwse);
                    break;
                case Grid { Tag: "ResizeNE" or "ResizeSW" } nesw:
                    SetCursor(nesw, Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest);
                    ConfigureResizeCorner(nesw);
                    break;
                case Grid { Tag: "Top" or "Right" or "Bottom" or "Left" } anchor when anchor.Children.Count == 1 && anchor.Children[0] is Ellipse dot:
                    SetCursor(anchor, Microsoft.UI.Input.InputSystemCursorShape.Cross);
                    // The dot swells under the pointer: "grab here to connect".
                    dot.CenterPoint = new System.Numerics.Vector3(6, 6, 0);
                    dot.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
                    anchor.PointerEntered += (_, _) => dot.Scale = new System.Numerics.Vector3(1.5f, 1.5f, 1);
                    anchor.PointerExited += (_, _) => dot.Scale = System.Numerics.Vector3.One;
                    break;
            }
        }
    }

    private static void ConfigureResizeCorner(Grid handle)
    {
        if (handle.Children.Count == 1 && handle.Children[0] is Microsoft.UI.Xaml.Shapes.Rectangle sq)
        {
            sq.CenterPoint = new System.Numerics.Vector3(4.5f, 4.5f, 0);
            sq.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
        }
    }

    private void OnResizeHandlePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid { Children.Count: 1 } handle && handle.Children[0] is Microsoft.UI.Xaml.Shapes.Rectangle sq)
        {
            sq.Scale = new System.Numerics.Vector3(1.3f, 1.3f, 1f);
            sq.Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x4C, 0xC9, 0xF0));
        }
    }

    private void OnResizeHandlePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid { Children.Count: 1 } handle && handle.Children[0] is Microsoft.UI.Xaml.Shapes.Rectangle sq)
        {
            sq.Scale = System.Numerics.Vector3.One;
            sq.Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xED, 0xF2, 0xF4));
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grandchild in Descendants(child)) yield return grandchild;
        }
    }

    /// <summary>Puts a node's ZIndex on its item container, the element the canvas actually
    /// stacks. Bring to Front / Send to Back changed the number and nothing moved.</summary>
    private void ApplyZIndex(DiagramNodeViewModel node)
    {
        if (NodesItemsControl.ContainerFromItem(node) is UIElement container)
            Canvas.SetZIndex(container, node.ZIndex);
    }

    // ---- Double-click empty canvas → drop a fresh node straight into inline edit --------------
    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Only act on a genuine empty-canvas double-tap (not on a node/connector, which have their
        // own double-tap handlers for inline editing).
        if (e.OriginalSource is FrameworkElement el && el.DataContext is DiagramNodeViewModel) return;
        if (e.OriginalSource is FrameworkElement el2 && el2.DataContext is DiagramConnectorViewModel) return;

        if (ViewModel is null) return;
        Point pos = e.GetPosition(InfiniteCanvasGrid);
        var item = new MermaidPaletteItem { Category = "Flowchart", ShapeType = "Rectangle", DefaultText = "New Node" };
        var newNode = ViewModel.AddNodeFromPalette(item, pos.X, pos.Y, centreOnPoint: true);
        if (newNode != null)
        {
            ViewModel.SelectNode(newNode, false);
            StartNodeInPlaceEdit(newNode);
        }
        e.Handled = true;
    }

    // Applies a corner-resize delta to the node being resized. The grabbed corner moves with the
    // pointer while the opposite corner stays anchored; minimum size is enforced so a node can
    // never be collapsed out of existence. Honors grid snap when enabled.
    private void ApplyResize(double dx, double dy)
    {
        var n = _resizeNode!;
        double x = _resizeStartX, y = _resizeStartY, w = _resizeStartW, h = _resizeStartH;

        bool left = _resizeDirection.Contains('W');
        bool right = _resizeDirection.Contains('E');
        bool top = _resizeDirection.Contains('N');
        bool bottom = _resizeDirection.Contains('S');

        if (right) w = _resizeStartW + dx;
        if (bottom) h = _resizeStartH + dy;
        if (left) { w = _resizeStartW - dx; x = _resizeStartX + dx; }
        if (top) { h = _resizeStartH - dy; y = _resizeStartY + dy; }

        // Clamp to the minimum size, keeping the opposite edge fixed.
        if (w < MinNodeWidth) { if (left) x = _resizeStartX + (_resizeStartW - MinNodeWidth); w = MinNodeWidth; }
        if (h < MinNodeHeight) { if (top) y = _resizeStartY + (_resizeStartH - MinNodeHeight); h = MinNodeHeight; }

        if (ViewModel!.IsGridSnapEnabled)
        {
            double g = ViewModel.GridSnapSize;
            x = Math.Round(x / g) * g;
            y = Math.Round(y / g) * g;
            w = Math.Round(w / g) * g;
            h = Math.Round(h / g) * g;
            if (w < MinNodeWidth) w = MinNodeWidth;
            if (h < MinNodeHeight) h = MinNodeHeight;
        }

        n.X = x;
        n.Y = y;
        n.Width = w;
        n.Height = h;
    }

    // ---- Smart alignment guides (Figma / draw.io-style) ---------------------------------------
    // While a node is dragged, its left/center/right and top/center/bottom edges are compared
    // against every sibling node. When an edge comes within AlignSnapThreshold px of a sibling's
    // edge, the node magnetically snaps to it and a dashed guide line is drawn across both nodes.
    private const double AlignSnapThreshold = 7;

    private (double snapDx, double snapDy) ComputeAlignmentSnap(DiagramNodeViewModel dragged, double proposedX, double proposedY)
    {
        ClearAlignmentGuides();
        var vm = ViewModel;
        if (vm is null) return (0, 0);

        double w = dragged.Width, h = dragged.Height;
        // Proposed edges & centers of the dragged node.
        double[] vEdges = { proposedX, proposedX + w / 2, proposedX + w };
        double[] hEdges = { proposedY, proposedY + h / 2, proposedY + h };

        double bestDx = 0, bestDy = 0;
        double bestVDist = AlignSnapThreshold, bestHDist = AlignSnapThreshold;
        double guideVx = double.NaN, guideHy = double.NaN;
        DiagramNodeViewModel? vMatch = null, hMatch = null;

        foreach (var sib in vm.Nodes)
        {
            if (sib == dragged) continue;
            // Skip nodes moving together with the drag (they stay in relative position).
            if (_initialSelectedNodePositions != null && _initialSelectedNodePositions.ContainsKey(sib)) continue;

            double[] sibV = { sib.X, sib.X + sib.Width / 2, sib.X + sib.Width };
            double[] sibH = { sib.Y, sib.Y + sib.Height / 2, sib.Y + sib.Height };

            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double dv = sibV[j] - vEdges[i];
                    if (Math.Abs(dv) < bestVDist)
                    {
                        bestVDist = Math.Abs(dv);
                        bestDx = dv;
                        guideVx = sibV[j];
                        vMatch = sib;
                    }

                    double dh = sibH[j] - hEdges[i];
                    if (Math.Abs(dh) < bestHDist)
                    {
                        bestHDist = Math.Abs(dh);
                        bestDy = dh;
                        guideHy = sibH[j];
                        hMatch = sib;
                    }
                }
            }
        }

        // Show a vertical guide if we snapped horizontally (aligned on an X edge).
        if (vMatch != null && !double.IsNaN(guideVx))
        {
            double top = Math.Min(proposedY, vMatch.Y) - 24;
            double bottom = Math.Max(proposedY + h, vMatch.Y + vMatch.Height) + 24;
            VerticalAlignGuide.X1 = guideVx; VerticalAlignGuide.X2 = guideVx;
            VerticalAlignGuide.Y1 = top; VerticalAlignGuide.Y2 = bottom;
            VerticalAlignGuide.Visibility = Visibility.Visible;
        }
        else
        {
            bestDx = 0;
        }

        // Show a horizontal guide if we snapped vertically (aligned on a Y edge).
        if (hMatch != null && !double.IsNaN(guideHy))
        {
            double left = Math.Min(proposedX, hMatch.X) - 24;
            double right = Math.Max(proposedX + w, hMatch.X + hMatch.Width) + 24;
            HorizontalAlignGuide.X1 = left; HorizontalAlignGuide.X2 = right;
            HorizontalAlignGuide.Y1 = guideHy; HorizontalAlignGuide.Y2 = guideHy;
            HorizontalAlignGuide.Visibility = Visibility.Visible;
        }
        else
        {
            bestDy = 0;
        }

        return (bestDx, bestDy);
    }

    private void ClearAlignmentGuides()
    {
        VerticalAlignGuide.Visibility = Visibility.Collapsed;
        HorizontalAlignGuide.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region Anchor Line Connectors Canvas Handlers & Marquee Selection

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint(InfiniteCanvasGrid);
        bool isRightButton = pointerPoint.Properties.IsRightButtonPressed;

        // Right-click (on a node, connector, or empty canvas): defer to release — a stationary
        // right-click opens the context menu, a right-DRAG pans. Capture the element under the
        // cursor so the menu knows its target.
        if (isRightButton)
        {
            _rightClickPending = true;
            _rightClickStartPos = e.GetCurrentPoint(CanvasScrollViewer).Position;
            (_rightClickNode, _rightClickConnector) = ResolveContextTarget(e.OriginalSource as DependencyObject);
            if (_rightClickNode is null && _rightClickConnector is null && ViewModel is { } vm)
            {
                _rightClickNode = vm.NodeAt(pointerPoint.Position.X, pointerPoint.Position.Y, tolerance: 0);
            }
            _isPanning = true; // becomes a pan if it turns into a drag
            _panStartMousePos = _rightClickStartPos;
            _panStartScrollX = CanvasScrollViewer.HorizontalOffset;
            _panStartScrollY = CanvasScrollViewer.VerticalOffset;
            InfiniteCanvasGrid.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(e.OriginalSource, InfiniteCanvasGrid) || ReferenceEquals(e.OriginalSource, GridPatternCanvas) || ReferenceEquals(e.OriginalSource, CanvasRootGrid))
        {
            bool isMiddleButton = pointerPoint.Properties.IsMiddleButtonPressed;
            bool isCtrlClick = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0 && pointerPoint.Properties.IsLeftButtonPressed;

            if (isMiddleButton || isCtrlClick)
            {
                _isPanning = true;
                _panStartMousePos = e.GetCurrentPoint(CanvasScrollViewer).Position;
                _panStartScrollX = CanvasScrollViewer.HorizontalOffset;
                _panStartScrollY = CanvasScrollViewer.VerticalOffset;
                InfiniteCanvasGrid.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            _isRubberbanding = true;
            _rubberbandStartPoint = pointerPoint.Position;

            Canvas.SetLeft(RubberbandSelectionBox, _rubberbandStartPoint.X);
            Canvas.SetTop(RubberbandSelectionBox, _rubberbandStartPoint.Y);
            RubberbandSelectionBox.Width = 0;
            RubberbandSelectionBox.Height = 0;
            RubberbandSelectionBox.Visibility = Visibility.Visible;

            InfiniteCanvasGrid.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private static (DiagramNodeViewModel? Node, DiagramConnectorViewModel? Connector) ResolveContextTarget(DependencyObject? source)
    {
        for (var d = source; d is not null; )
        {
            if (d is FrameworkElement fe)
            {
                if (fe.DataContext is DiagramNodeViewModel node) return (node, null);
                if (fe.DataContext is DiagramConnectorViewModel conn) return (null, conn);
            }
            d = d is UIElement ? VisualTreeHelper.GetParent(d) : null;
        }
        return (null, null);
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (UpdateMessageDrag(e)) { e.Handled = true; return; }
        if (_isPanning)
        {
            var currentPos = e.GetCurrentPoint(CanvasScrollViewer).Position;
            double deltaX = currentPos.X - _panStartMousePos.X;
            double deltaY = currentPos.Y - _panStartMousePos.Y;

            // A right-press only becomes a pan once it moves; until then it may still be a click.
            if (!_panCursorShown && (!_rightClickPending || deltaX * deltaX + deltaY * deltaY >= 25))
            {
                SetCursor(InfiniteCanvasGrid, Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                _panCursorShown = true;
            }

            CanvasScrollViewer.ChangeView(_panStartScrollX - deltaX, _panStartScrollY - deltaY, null);
            e.Handled = true;
            return;
        }

        if (_isRubberbanding)
        {
            Point currentPos = e.GetCurrentPoint(InfiniteCanvasGrid).Position;
            double minX = Math.Min(_rubberbandStartPoint.X, currentPos.X);
            double maxX = Math.Max(_rubberbandStartPoint.X, currentPos.X);
            double minY = Math.Min(_rubberbandStartPoint.Y, currentPos.Y);
            double maxY = Math.Max(_rubberbandStartPoint.Y, currentPos.Y);

            Canvas.SetLeft(RubberbandSelectionBox, minX);
            Canvas.SetTop(RubberbandSelectionBox, minY);
            RubberbandSelectionBox.Width = maxX - minX;
            RubberbandSelectionBox.Height = maxY - minY;
            e.Handled = true;
            return;
        }

        if (_isDrawingConnector && _connectorSourceNode != null)
        {
            Point mousePos = e.GetCurrentPoint(InfiniteCanvasGrid).Position;
            var vmPoint = _connectorSourceNode.GetAnchorPoint(_connectorSourceAnchor);
            Point srcPoint = new Point((float)vmPoint.X, (float)vmPoint.Y);

            Point midPoint = (_connectorSourceAnchor is "Left" or "Right")
                ? new Point(mousePos.X, srcPoint.Y)
                : new Point(srcPoint.X, mousePos.Y);

            var pathGeo = new PathGeometry();
            var figure = new PathFigure { StartPoint = srcPoint };
            figure.Segments.Add(new LineSegment { Point = midPoint });
            figure.Segments.Add(new LineSegment { Point = mousePos });
            pathGeo.Figures.Add(figure);

            DraftConnectorPath.Data = pathGeo;

            // Highlight the node under the cursor as the prospective connection target so the user
            // gets clear "drop here" feedback before releasing.
            UpdateConnectionTargetHighlight(mousePos);
            e.Handled = true;
        }
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (FinishMessageDrag(e)) { e.Handled = true; return; }
        if (_isPanning)
        {
            bool wasRightClick = _rightClickPending;
            _isPanning = false;
            _rightClickPending = false;
            ResetPanCursor();
            InfiniteCanvasGrid.ReleasePointerCapture(e.Pointer);

            // A stationary right-click (moved < 5px) opens the context menu instead of panning.
            if (wasRightClick)
            {
                var endPos = e.GetCurrentPoint(CanvasScrollViewer).Position;
                double dx = endPos.X - _rightClickStartPos.X;
                double dy = endPos.Y - _rightClickStartPos.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 5)
                {
                    ShowContextMenu(e.GetCurrentPoint(InfiniteCanvasGrid).Position);
                }
                _rightClickNode = null;
                _rightClickConnector = null;
            }
            e.Handled = true;
            return;
        }

        if (_isRubberbanding)
        {
            Point releasePos = e.GetCurrentPoint(InfiniteCanvasGrid).Position;
            double minX = Math.Min(_rubberbandStartPoint.X, releasePos.X);
            double maxX = Math.Max(_rubberbandStartPoint.X, releasePos.X);
            double minY = Math.Min(_rubberbandStartPoint.Y, releasePos.Y);
            double maxY = Math.Max(_rubberbandStartPoint.Y, releasePos.Y);

            bool isAdditive = (e.KeyModifiers & (Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift)) != 0;

            var selectionRect = new Rect(minX, minY, maxX - minX, maxY - minY);
            ViewModel?.SelectNodesInRect(selectionRect, isAdditive);

            _isRubberbanding = false;
            RubberbandSelectionBox.Visibility = Visibility.Collapsed;
            InfiniteCanvasGrid.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        if (_isDrawingConnector)
        {
            Point releasePos = e.GetCurrentPoint(InfiniteCanvasGrid).Position;

            if (ViewModel != null && _connectorSourceNode != null)
            {
                // Same test as the highlight, so a release on the ringed node always connects.
                var targetNode = ViewModel.NodeAt(releasePos.X, releasePos.Y, ConnectDropTolerance, except: _connectorSourceNode);

                if (targetNode != null)
                {
                    ViewModel.AddConnector(_connectorSourceNode.Id, _connectorSourceAnchor, targetNode.Id, "Top");
                }
                else
                {
                    ViewModel.StatusText = "Not connected: let go over another shape to connect to it.";
                }
            }

            _isDrawingConnector = false;
            _connectorSourceNode = null;
            ClearConnectionTargetHighlight();
            DraftConnectorPath.Visibility = Visibility.Collapsed;
            NodesItemsControl.ReleasePointerCapture(e.Pointer);
        }
    }

    // Highlights the node under the pointer (excluding the connector's source) as the prospective
    // drop target; un-highlights the previous target when the pointer moves off it.
    private void UpdateConnectionTargetHighlight(Point mousePos)
    {
        var vm = ViewModel;
        if (vm is null) return;

        var target = vm.NodeAt(mousePos.X, mousePos.Y, ConnectDropTolerance, except: _connectorSourceNode);

        if (target == _currentConnectionTarget) return;

        if (_currentConnectionTarget != null)
            _currentConnectionTarget.IsConnectionTarget = false;

        _currentConnectionTarget = target;
        if (target != null)
            target.IsConnectionTarget = true;
    }

    private void ClearConnectionTargetHighlight()
    {
        if (_currentConnectionTarget != null)
        {
            _currentConnectionTarget.IsConnectionTarget = false;
            _currentConnectionTarget = null;
        }
    }

    // A release this close to a node's box still lands on it: the anchor dots sit half outside it.
    private const double ConnectDropTolerance = 14;

    private bool _panCursorShown;

    private void ResetPanCursor()
    {
        if (!_panCursorShown) return;
        SetCursor(InfiniteCanvasGrid, Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        _panCursorShown = false;
    }

    /// <summary>Esc while dragging, resizing, connecting, marquee-selecting or panning: stops the
    /// gesture and puts back what it moved. Returns false when nothing was in progress, so Esc
    /// can fall through to clearing the selection.</summary>
    public bool CancelActiveGesture()
    {
        bool active = EndGesture(restore: true);
        if (active)
        {
            NodesItemsControl.ReleasePointerCaptures();
            InfiniteCanvasGrid.ReleasePointerCaptures();
            if (ViewModel is { } vm) vm.StatusText = "Cancelled.";
        }
        return active;
    }

    // Ends whatever gesture is in progress. With restore, a drag or resize puts the nodes back and
    // drops the undo step it took, so a cancelled gesture leaves no trace. The release handlers
    // clear their own flags before releasing capture, so the capture-lost call that follows a
    // normal release finds nothing to end.
    private bool EndGesture(bool restore)
    {
        bool active = false;
        var vm = ViewModel;

        if (_isDraggingNode)
        {
            active = true;
            if (restore && vm != null && _initialSelectedNodePositions != null && _dragSnapshotTaken)
            {
                foreach (var (node, start) in _initialSelectedNodePositions)
                {
                    node.X = start.X;
                    node.Y = start.Y;
                    vm.UpdateConnectedConnectors(node);
                }
                vm.DiscardLastSnapshot();
            }
            _isDraggingNode = false;
            _draggedNode = null;
            _initialSelectedNodePositions = null;
            ClearAlignmentGuides();
        }

        if (_isResizingNode)
        {
            active = true;
            if (restore && vm != null && _resizeNode is { } n && _resizeSnapshotTaken)
            {
                n.X = _resizeStartX; n.Y = _resizeStartY; n.Width = _resizeStartW; n.Height = _resizeStartH;
                vm.UpdateConnectedConnectors(n);
                vm.DiscardLastSnapshot();
            }
            _isResizingNode = false;
            _resizeNode = null;
        }

        if (_isDrawingConnector)
        {
            active = true;
            _isDrawingConnector = false;
            _connectorSourceNode = null;
            ClearConnectionTargetHighlight();
            DraftConnectorPath.Visibility = Visibility.Collapsed;
        }

        if (_isRubberbanding)
        {
            active = true;
            _isRubberbanding = false;
            RubberbandSelectionBox.Visibility = Visibility.Collapsed;
        }

        if (_isPanning)
        {
            active = true;
            _isPanning = false;
            _rightClickPending = false;
        }
        ResetPanCursor();

        if (_messageDrag != null)
        {
            active = true;
            _messageDrag = null;
            _messageDragActive = false;
            HorizontalAlignGuide.Visibility = Visibility.Collapsed;
            SetCursor(InfiniteCanvasGrid, Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }

        return active;
    }

    #endregion

    #region In-Place Inline Text Editing & Connectors

    // Hover: a soft halo on the connector and a hand cursor, so a thin line reads as clickable.
    // The wide transparent hit path in the template is what actually catches the pointer.
    private void OnConnectorPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DiagramConnectorViewModel conn } fe)
        {
            conn.IsHovered = true;
            SetCursor(fe, Microsoft.UI.Input.InputSystemCursorShape.Hand);
        }
    }

    private void OnConnectorPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DiagramConnectorViewModel conn })
            conn.IsHovered = false;
    }

    // UIElement.ProtectedCursor is protected in WinUI 3; reflection is the usual way to set it on
    // an element we don't subclass (same helper as Shape Studio).
    private static readonly System.Reflection.PropertyInfo? ProtectedCursorProperty =
        typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static void SetCursor(UIElement element, Microsoft.UI.Input.InputSystemCursorShape shape)
    {
        try { ProtectedCursorProperty?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape)); }
        catch { /* cursor is cosmetic */ }
    }

    private void OnConnectorsItemsControlPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(InfiniteCanvasGrid).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.OriginalSource is FrameworkElement element && element.DataContext is DiagramConnectorViewModel connVM)
        {
            if (ViewModel != null)
            {
                ViewModel.SelectedConnector = connVM;
                ViewModel.SelectedNode = null;

                // A sequence message can be dragged up or down to another row (the mouse twin of
                // ↑/↓). It only becomes a drag once the pointer has moved a few pixels.
                var point = e.GetCurrentPoint(InfiniteCanvasGrid);
                if (ViewModel.IsSequenceDiagram && point.Properties.IsLeftButtonPressed)
                {
                    _messageDrag = connVM;
                    _messageDragActive = false;
                    _messageDragStartY = point.Position.Y;
                    InfiniteCanvasGrid.CapturePointer(e.Pointer);
                }
            }
        }
    }

    private DiagramConnectorViewModel? _messageDrag;
    private bool _messageDragActive;
    private double _messageDragStartY;

    // While a message is dragged: a line across the canvas where it will land.
    private bool UpdateMessageDrag(PointerRoutedEventArgs e)
    {
        if (_messageDrag is null || ViewModel is null) return false;
        double y = e.GetCurrentPoint(InfiniteCanvasGrid).Position.Y;
        if (!_messageDragActive && Math.Abs(y - _messageDragStartY) < 6) return true;
        _messageDragActive = true;
        int row = ViewModel.SequenceRowAt(y);
        if (ViewModel.SequenceRowY(row) is { } rowY && ViewModel.GetContentBounds() is { } bounds)
        {
            HorizontalAlignGuide.X1 = bounds.X - 20;
            HorizontalAlignGuide.X2 = bounds.X + bounds.Width + 20;
            HorizontalAlignGuide.Y1 = HorizontalAlignGuide.Y2 = rowY;
            HorizontalAlignGuide.Visibility = Visibility.Visible;
        }
        SetCursor(InfiniteCanvasGrid, Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);
        return true;
    }

    private bool FinishMessageDrag(PointerRoutedEventArgs e)
    {
        if (_messageDrag is not { } conn || ViewModel is null) return false;
        if (_messageDragActive)
        {
            int row = ViewModel.SequenceRowAt(e.GetCurrentPoint(InfiniteCanvasGrid).Position.Y);
            ViewModel.MoveMessageToRow(conn, row);
        }
        _messageDrag = null;
        _messageDragActive = false;
        HorizontalAlignGuide.Visibility = Visibility.Collapsed;
        SetCursor(InfiniteCanvasGrid, Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        InfiniteCanvasGrid.ReleasePointerCapture(e.Pointer);
        return true;
    }

    private void OnNodesItemsControlDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element && element.DataContext is DiagramNodeViewModel nodeVM)
        {
            StartNodeInPlaceEdit(nodeVM);
            e.Handled = true;
        }
    }

    private void OnConnectorsItemsControlDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element && element.DataContext is DiagramConnectorViewModel connVM)
        {
            StartConnectorInPlaceEdit(connVM);
            e.Handled = true;
        }
    }

    /// <summary>Opens the inline label editor for the selected node or connector (F2).</summary>
    public bool StartSelectedInPlaceEdit()
    {
        if (ViewModel is not { } vm) return false;
        if (vm.SelectedNode is { } node && vm.SelectedNodes.Count <= 1)
        {
            StartNodeInPlaceEdit(node);
            return true;
        }
        if (vm.SelectedConnector is { } conn)
        {
            StartConnectorInPlaceEdit(conn);
            return true;
        }
        return false;
    }

    /// <summary>Returns keyboard focus to the canvas viewport.</summary>
    public void FocusCanvas()
    {
        CanvasScrollViewer.Focus(FocusState.Programmatic);
    }

    private void StartNodeInPlaceEdit(DiagramNodeViewModel node)
    {
        _editingNode = node;
        _editingConnector = null;

        // Position the TextBox, not its host: InPlaceEditorCanvas sits in a Grid, where
        // Canvas.Left/Top do nothing, so the editor always opened at the canvas's top-left corner
        // instead of over the node being renamed. Centred on the node when it's the wider one.
        double boxW = Math.Max(node.Width, 140);
        double boxH = Math.Max(node.Height, 60);
        double left = node.X + (node.Width - boxW) / 2;
        double top = node.Y + (node.Height - boxH) / 2;
        InlineEditTextBox.Width = boxW;
        InlineEditTextBox.Height = boxH;
        Canvas.SetLeft(InlineEditTextBox, left);
        Canvas.SetTop(InlineEditTextBox, top);
        Canvas.SetLeft(InlineEditActionBar, left + boxW - 52);
        Canvas.SetTop(InlineEditActionBar, top + boxH + 4);
        InlineEditTextBox.Text = node.LabelText;
        // A multi-line TextBox ignores VerticalContentAlignment; pad the label down to the middle.
        var lineCount = Math.Max(1, (node.LabelText ?? "").Split('\r', '\n').Count(l => l.Length > 0));
        InlineEditTextBox.Padding = new Thickness(8, Math.Max(4, (InlineEditTextBox.Height - lineCount * 19) / 2 - 2), 8, 4);

        InPlaceEditorCanvas.Visibility = Visibility.Visible;
        InlineEditTextBox.Focus(FocusState.Programmatic);
        InlineEditTextBox.SelectAll();
    }

    private void StartConnectorInPlaceEdit(DiagramConnectorViewModel conn)
    {
        _editingConnector = conn;
        _editingNode = null;

        double left = conn.MidpointX - 60;
        double top = conn.MidpointY - 17;
        InlineEditTextBox.Width = 120;
        InlineEditTextBox.Height = 35;
        Canvas.SetLeft(InlineEditTextBox, left);
        Canvas.SetTop(InlineEditTextBox, top);
        Canvas.SetLeft(InlineEditActionBar, left + 120 - 52);
        Canvas.SetTop(InlineEditActionBar, top + 35 + 4);
        InlineEditTextBox.Text = conn.Label ?? string.Empty;
        InlineEditTextBox.Padding = new Thickness(6, 6, 6, 4);

        InPlaceEditorCanvas.Visibility = Visibility.Visible;
        InlineEditTextBox.Focus(FocusState.Programmatic);
        InlineEditTextBox.SelectAll();
    }

    // PreviewKeyDown, not KeyDown: with AcceptsReturn the TextBox consumes Enter itself, so a
    // KeyDown handler never saw it — Enter added a line and the only way to commit was clicking
    // away. Enter commits; Shift+Enter still adds a line for a multi-line label.
    private void OnInlineEditKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == Windows.System.VirtualKey.Enter && !shift)
        {
            CommitInPlaceEdit(returnFocusToCanvas: true);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelInPlaceEdit();
            e.Handled = true;
        }
    }

    private void OnInlineEditCommitClick(object sender, RoutedEventArgs e)
    {
        CommitInPlaceEdit(returnFocusToCanvas: true);
    }

    private void OnInlineEditCancelClick(object sender, RoutedEventArgs e)
    {
        CancelInPlaceEdit();
    }

    private void OnInlineEditLostFocus(object sender, RoutedEventArgs e)
    {
        CommitInPlaceEdit(returnFocusToCanvas: false);
    }

    private void CommitInPlaceEdit(bool returnFocusToCanvas = false)
    {
        if (_editingNode != null)
        {
            var newText = InlineEditTextBox.Text;
            // Only record an undo step when the rename actually changed something.
            if (!string.Equals(newText, _editingNode.LabelText, StringComparison.Ordinal))
            {
                ViewModel?.SnapshotForUndo();
                using var _ = ViewModel?.SuspendEditTracking(); // the snapshot above is the step
                _editingNode.LabelText = newText;
                _editingNode.RecalculateBoundsForText();
                ViewModel?.UpdateConnectedConnectors(_editingNode);
                if (ViewModel is { } vm)
                    vm.StatusText = $"Renamed shape to '{MermaidStudioViewModel.DisplayName(_editingNode)}'.";
            }
            _editingNode = null;
        }
        else if (_editingConnector != null)
        {
            var newText = InlineEditTextBox.Text;
            if (!string.Equals(newText, _editingConnector.Label, StringComparison.Ordinal))
            {
                ViewModel?.SnapshotForUndo();
                using var _ = ViewModel?.SuspendEditTracking();
                _editingConnector.Label = newText;
                if (ViewModel is { } vm)
                    vm.StatusText = string.IsNullOrWhiteSpace(newText)
                        ? "Cleared connector label."
                        : $"Set connector label to '{newText}'.";
            }
            _editingConnector = null;
        }

        InPlaceEditorCanvas.Visibility = Visibility.Collapsed;
        if (returnFocusToCanvas) FocusCanvas();
    }

    private void CancelInPlaceEdit()
    {
        _editingNode = null;
        _editingConnector = null;
        InPlaceEditorCanvas.Visibility = Visibility.Collapsed;
        if (ViewModel is { } vm) vm.StatusText = "Cancelled label edit.";
        FocusCanvas();
    }

    #endregion

    #region Context Menu

    /// <summary>Opens the canvas context menu at the selected node, selected connector, or viewport centre (Shift+F10 / Menu key).</summary>
    public void ShowContextMenuForSelection()
    {
        if (ViewModel is not { } vm) return;
        if (vm.SelectedNode is { } node)
        {
            _rightClickNode = node;
            _rightClickConnector = null;
            ShowContextMenu(new Point(node.X + node.Width / 2, node.Y + node.Height / 2));
        }
        else if (vm.SelectedConnector is { } conn)
        {
            _rightClickNode = null;
            _rightClickConnector = conn;
            ShowContextMenu(new Point(conn.MidpointX, conn.MidpointY));
        }
        else
        {
            _rightClickNode = null;
            _rightClickConnector = null;
            double zoom = Math.Max(0.01, CanvasScrollViewer.ZoomFactor);
            double cx = (CanvasScrollViewer.HorizontalOffset + CanvasScrollViewer.ViewportWidth / 2) / zoom;
            double cy = (CanvasScrollViewer.VerticalOffset + CanvasScrollViewer.ViewportHeight / 2) / zoom;
            ShowContextMenu(new Point(cx > 0 ? cx : 240, cy > 0 ? cy : 180));
        }
        _rightClickNode = null;
        _rightClickConnector = null;
    }

    // Builds and opens an adaptive right-click menu. The target (node / connector / empty canvas)
    // was captured on pointer-press; the menu offers the most useful ops for each, mirroring
    // dedicated diagram editors.
    private void ShowContextMenu(Point canvasPos)
    {
        var vm = ViewModel;
        if (vm is null) return;

        var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();

        if (_rightClickNode is { } node)
        {
            // Ensure the right-clicked node is selected so menu ops act on it.
            if (!node.IsSelected) vm.SelectNode(node, false);
            bool many = vm.SelectedNodes.Count > 1;

            var rename = MenuItem("Rename", 0xE8AC, (s, e) => StartNodeInPlaceEdit(node), "F2");
            rename.IsEnabled = !many;
            flyout.Items.Add(rename);

            var fitLabel = MenuItem("Fit Size to Label", 0xE740, (s, e) => vm.FitSelectedNodesToLabel(node));
            fitLabel.IsEnabled = many || !node.IsPseudoState;
            flyout.Items.Add(fitLabel);

            if (!many && node.CanQuickAdd)
            {
                var connectSub = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
                {
                    Text = "Add Connected Shape",
                    Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE710" }
                };
                connectSub.Items.Add(MenuItem("Above", 0xE70E, (s, e) => StartNodeInPlaceEdit(vm.QuickAddNode(node, "Top"))));
                connectSub.Items.Add(MenuItem("To the Right", 0xE76C, (s, e) => StartNodeInPlaceEdit(vm.QuickAddNode(node, "Right"))));
                connectSub.Items.Add(MenuItem("Below", 0xE70D, (s, e) => StartNodeInPlaceEdit(vm.QuickAddNode(node, "Bottom"))));
                connectSub.Items.Add(MenuItem("To the Left", 0xE76B, (s, e) => StartNodeInPlaceEdit(vm.QuickAddNode(node, "Left"))));
                flyout.Items.Add(connectSub);
            }

            if (BuildChangeShapeSubmenu(vm, node) is { } shapeSub)
                flyout.Items.Add(shapeSub);

            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            flyout.Items.Add(MenuItem("Duplicate", 0xE7C4, (s, e) => vm.DuplicateSelected(), "Ctrl+D"));
            flyout.Items.Add(MenuItem("Copy", 0xE8C8, (s, e) => vm.CopySelected(), "Ctrl+C"));
            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());

            bool canFront = vm.CanBringForward(node);
            bool canBack = vm.CanSendBackward(node);
            var front = MenuItem("Bring to Front", 0xE74A, (s, e) => BringToFront(node), "Ctrl+Shift+]");
            front.IsEnabled = canFront;
            var forward = MenuItem("Bring Forward", 0xE74A, (s, e) => BringForward(node), "Ctrl+]");
            forward.IsEnabled = canFront;
            var backward = MenuItem("Send Backward", 0xE74B, (s, e) => SendBackward(node), "Ctrl+[");
            backward.IsEnabled = canBack;
            var back = MenuItem("Send to Back", 0xE74B, (s, e) => SendToBack(node), "Ctrl+Shift+[");
            back.IsEnabled = canBack;
            flyout.Items.Add(front);
            flyout.Items.Add(forward);
            flyout.Items.Add(backward);
            flyout.Items.Add(back);

            if (many)
            {
                flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
                var alignSub = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
                {
                    Text = "Align & Distribute",
                    Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE8E4" }
                };
                alignSub.Items.Add(MenuItem("Align Left", 0xE8E4, (s, e) => vm.AlignLeft()));
                alignSub.Items.Add(MenuItem("Align Center", 0xE8E3, (s, e) => vm.AlignHorizontalCenter()));
                alignSub.Items.Add(MenuItem("Align Right", 0xE8E2, (s, e) => vm.AlignRight()));
                alignSub.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
                alignSub.Items.Add(MenuItem("Align Top", 0xE8E4, (s, e) => vm.AlignTop()));
                alignSub.Items.Add(MenuItem("Align Middle", 0xE8E3, (s, e) => vm.AlignVerticalMiddle()));
                alignSub.Items.Add(MenuItem("Align Bottom", 0xE8E2, (s, e) => vm.AlignBottom()));
                alignSub.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
                var distH = MenuItem("Distribute Horizontally", 0xE8E3, (s, e) => vm.DistributeHorizontally());
                distH.IsEnabled = vm.SelectedNodes.Count >= 3;
                var distV = MenuItem("Distribute Vertically", 0xE8E3, (s, e) => vm.DistributeVertically());
                distV.IsEnabled = vm.SelectedNodes.Count >= 3;
                alignSub.Items.Add(distH);
                alignSub.Items.Add(distV);
                flyout.Items.Add(alignSub);
            }

            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            flyout.Items.Add(MenuItem(many ? $"Delete {vm.SelectedNodes.Count} shapes" : "Delete", 0xE74D, (s, e) => vm.DeleteSelected(), "Del"));
        }
        else if (_rightClickConnector is { } conn)
        {
            vm.SelectedConnector = conn;
            vm.SelectedNode = null;

            flyout.Items.Add(MenuItem(string.IsNullOrEmpty(conn.Label) ? "Add Label" : "Edit Label", 0xE8AC, (s, e) => StartConnectorInPlaceEdit(conn), "F2"));
            flyout.Items.Add(MenuItem("Reverse Direction", 0xE8AB, (s, e) => vm.ReverseConnector(conn)));

            AppendConnectorStyleSubmenus(flyout, vm, conn);

            if (vm.IsSequenceDiagram)
            {
                flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
                flyout.Items.Add(MenuItem("Move Message Up", 0xE74A, (s, e) => vm.MoveSelectedMessage(-1), "↑"));
                flyout.Items.Add(MenuItem("Move Message Down", 0xE74B, (s, e) => vm.MoveSelectedMessage(1), "↓"));
            }

            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            flyout.Items.Add(MenuItem("Delete Connector", 0xE74D, (s, e) => vm.DeleteSelected(), "Del"));
        }
        else
        {
            // Empty canvas.
            flyout.Items.Add(MenuItem("Add Shape Here", 0xE710, (s, e) => AddNodeAt(canvasPos), "Double-click"));
            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            var undo = MenuItem("Undo", 0xE7A7, (s, e) => vm.Undo(), "Ctrl+Z");
            undo.IsEnabled = vm.CanUndo;
            flyout.Items.Add(undo);
            var redo = MenuItem("Redo", 0xE7A6, (s, e) => vm.Redo(), "Ctrl+Y");
            redo.IsEnabled = vm.CanRedo;
            flyout.Items.Add(redo);
            var paste = MenuItem("Paste", 0xE77F, (s, e) => vm.PasteClipboard(), "Ctrl+V");
            paste.IsEnabled = vm.CanPaste;
            flyout.Items.Add(paste);
            var selectAll = MenuItem("Select All", 0xE8B3, (s, e) => vm.SelectAll(), "Ctrl+A");
            selectAll.IsEnabled = vm.Nodes.Count > 0;
            flyout.Items.Add(selectAll);
            var autoLayout = MenuItem("Auto-arrange Nodes", 0xECA5, (s, e) =>
            {
                vm.SnapshotForUndo();
                vm.ApplyAutoLayout(force: true);
                FitToContentAfterLayout();
            });
            autoLayout.IsEnabled = vm.Nodes.Count > 1;
            flyout.Items.Add(autoLayout);
            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            var fit = MenuItem("Fit to Content", 0xE9A6, (s, e) => FitToContent());
            fit.IsEnabled = vm.Nodes.Count > 0;
            flyout.Items.Add(fit);
            flyout.Items.Add(MenuItem("Zoom to 100%", 0xE71E, (s, e) => ZoomReset(), "Ctrl+0"));
            if (vm.Nodes.Count > 0)
            {
                flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
                var copyMermaid = MenuItem("Copy diagram as Mermaid", 0xE8C8, async (s, e) =>
                {
                    var code = vm.GenerateMermaidCode();
                    var error = await ClipboardWriter.TrySetTextAsync(code);
                    vm.StatusText = error is null ? "Copied diagram as Mermaid to clipboard." : $"The Mermaid code wasn't copied. {error}";
                }, "Ctrl+Shift+C");
                flyout.Items.Add(copyMermaid);
            }
        }

        // Anchor the flyout to the canvas at the pointer position.
        var anchor = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = canvasPos
        };
        flyout.ShowAt(InfiniteCanvasGrid, anchor);
    }

    private static Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem? BuildChangeShapeSubmenu(MermaidStudioViewModel vm, DiagramNodeViewModel node)
    {
        (string Code, string Label)[] shapes = vm.SelectedDiagramType switch
        {
            MarkSmith.Mermaid.Ast.MermaidDiagramType.Flowchart => new[]
            {
                ("Rectangle", "Rectangle Box"),
                ("RoundedRectangle", "Rounded Rectangle"),
                ("Stadium", "Stadium (Pill)"),
                ("Subroutine", "Subroutine"),
                ("CylindricalDatabase", "Database Cylinder"),
                ("Circle", "Circle"),
                ("Rhombus", "Decision Rhombus"),
                ("Hexagon", "Hexagon"),
            },
            MarkSmith.Mermaid.Ast.MermaidDiagramType.Sequence => new[]
            {
                ("Participant", "Participant Box"),
                ("Actor", "Actor Figure"),
            },
            MarkSmith.Mermaid.Ast.MermaidDiagramType.Class => new[]
            {
                ("ClassBox", "Class Box"),
                ("Interface", "Interface"),
            },
            MarkSmith.Mermaid.Ast.MermaidDiagramType.State => new[]
            {
                ("NormalState", "State Box"),
                ("ChoiceState", "Choice Diamond"),
                ("Start", "Start Point [*]"),
                ("End", "End Point [*]"),
            },
            MarkSmith.Mermaid.Ast.MermaidDiagramType.Gantt => new[]
            {
                ("TaskBar", "Task Bar"),
                ("Milestone", "Milestone"),
            },
            MarkSmith.Mermaid.Ast.MermaidDiagramType.Mindmap => new[]
            {
                ("RootNode", "Central Theme"),
                ("BranchNode", "Branch Topic"),
            },
            _ => Array.Empty<(string, string)>()
        };

        if (shapes.Length == 0) return null;

        var sub = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
        {
            Text = "Change Shape",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uF158" }
        };

        foreach (var (code, label) in shapes)
        {
            var item = new Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem
            {
                Text = label,
                IsChecked = string.Equals(node.Shape, code, StringComparison.OrdinalIgnoreCase)
            };
            var captured = code;
            item.Click += (_, _) => vm.ChangeSelectedNodesShape(captured, node);
            sub.Items.Add(item);
        }

        return sub;
    }

    private static void AppendConnectorStyleSubmenus(Microsoft.UI.Xaml.Controls.MenuFlyout flyout, MermaidStudioViewModel vm, DiagramConnectorViewModel conn)
    {
        if (vm.SelectedDiagramType == MarkSmith.Mermaid.Ast.MermaidDiagramType.Sequence)
        {
            var msgSub = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
            {
                Text = "Message Type",
                Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE8AB" }
            };
            (string Code, string Label)[] msgTypes =
            {
                ("SolidArrow", "Solid Arrow (->>)"),
                ("DottedArrow", "Dotted Reply (-->>)"),
                ("SolidLine", "Solid No Arrow (->)"),
                ("DottedLine", "Dotted No Arrow (-->)"),
                ("SolidCross", "Lost Message (-x)"),
                ("DottedCross", "Dotted Cross (--x)"),
                ("SolidOpen", "Async Arrow (-))"),
                ("DottedOpen", "Dotted Async (--))"),
            };
            foreach (var (code, label) in msgTypes)
            {
                var item = new Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem
                {
                    Text = label,
                    IsChecked = string.Equals(conn.LineStyle, code, StringComparison.OrdinalIgnoreCase)
                };
                var captured = code;
                var capturedLabel = label;
                item.Click += (_, _) =>
                {
                    vm.SnapshotForUndo();
                    using var _ = vm.SuspendEditTracking();
                    conn.LineStyle = captured;
                    vm.StatusText = $"Message type set to {capturedLabel}.";
                };
                msgSub.Items.Add(item);
            }
            flyout.Items.Add(msgSub);
        }
        else if (vm.SelectedDiagramType == MarkSmith.Mermaid.Ast.MermaidDiagramType.Class)
        {
            var relSub = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
            {
                Text = "Relationship Type",
                Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE8AB" }
            };
            (string Code, string Label)[] relTypes =
            {
                ("Inheritance", "Inheritance (--|>)"),
                ("Composition", "Composition (--*)"),
                ("Aggregation", "Aggregation (--o)"),
                ("Association", "Association (-->)"),
                ("Dependency", "Dependency (..>)"),
                ("Realization", "Realization (..|>)"),
            };
            foreach (var (code, label) in relTypes)
            {
                var item = new Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem
                {
                    Text = label,
                    IsChecked = string.Equals(conn.EndHead, code, StringComparison.OrdinalIgnoreCase)
                };
                var captured = code;
                var capturedLabel = label;
                item.Click += (_, _) =>
                {
                    vm.SnapshotForUndo();
                    using var _ = vm.SuspendEditTracking();
                    conn.EndHead = captured;
                    vm.StatusText = $"Relationship type set to {capturedLabel}.";
                };
                relSub.Items.Add(item);
            }
            flyout.Items.Add(relSub);
        }
        else if (vm.SelectedDiagramType == MarkSmith.Mermaid.Ast.MermaidDiagramType.Er)
        {
            var erSub = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
            {
                Text = "Relationship Line",
                Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE8AB" }
            };
            (string Code, string Label)[] erStyles =
            {
                ("Solid", "Identifying (Solid)"),
                ("Dashed", "Non-identifying (Dashed)"),
            };
            foreach (var (code, label) in erStyles)
            {
                var item = new Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem
                {
                    Text = label,
                    IsChecked = string.Equals(conn.LineStyle, code, StringComparison.OrdinalIgnoreCase)
                };
                var captured = code;
                var capturedLabel = label;
                item.Click += (_, _) =>
                {
                    vm.SnapshotForUndo();
                    using var _ = vm.SuspendEditTracking();
                    conn.LineStyle = captured;
                    vm.StatusText = $"Relationship line set to {capturedLabel}.";
                };
                erSub.Items.Add(item);
            }
            flyout.Items.Add(erSub);
        }
        else
        {
            var lineSub = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
            {
                Text = "Line Style",
                Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE70F" }
            };
            foreach (var (code, label) in new[] { ("Solid", "Solid (---)"), ("Dotted", "Dotted (-.-)"), ("Thick", "Thick (===)") })
            {
                var item = new Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem
                {
                    Text = label,
                    IsChecked = string.Equals(conn.LineStyle, code, StringComparison.OrdinalIgnoreCase)
                };
                var captured = code;
                var capturedLabel = label;
                item.Click += (_, _) =>
                {
                    vm.SnapshotForUndo();
                    using var _ = vm.SuspendEditTracking();
                    conn.LineStyle = captured;
                    vm.StatusText = $"Line style set to {capturedLabel}.";
                };
                lineSub.Items.Add(item);
            }
            flyout.Items.Add(lineSub);

            var headSub = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
            {
                Text = "Arrow Head",
                Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE8AB" }
            };
            foreach (var (start, end, label) in new[]
            {
                ("None", "Normal", "Arrow (-->)"),
                ("None", "None", "No Arrow (---)"),
                ("None", "Circle", "Circle (--o)"),
                ("None", "Cross", "Cross (--x)"),
                ("Normal", "Normal", "Bi-directional (<-->)"),
            })
            {
                var item = new Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem
                {
                    Text = label,
                    IsChecked = string.Equals(conn.StartHead, start, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(conn.EndHead, end, StringComparison.OrdinalIgnoreCase)
                };
                var capStart = start;
                var capEnd = end;
                var capLabel = label;
                item.Click += (_, _) =>
                {
                    vm.SnapshotForUndo();
                    using var _ = vm.SuspendEditTracking();
                    conn.StartHead = capStart;
                    conn.EndHead = capEnd;
                    vm.StatusText = $"Arrow head set to {capLabel}.";
                };
                headSub.Items.Add(item);
            }
            flyout.Items.Add(headSub);
        }
    }

    // The shortcut goes in the menu's right-hand column (KeyboardAcceleratorTextOverride), not in
    // brackets after the name.
    private static Microsoft.UI.Xaml.Controls.MenuFlyoutItem MenuItem(string text, int glyph, RoutedEventHandler handler, string? shortcut = null)
    {
        var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = text,
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = ((char)glyph).ToString() }
        };
        if (shortcut != null) item.KeyboardAcceleratorTextOverride = shortcut;
        item.Click += handler;
        return item;
    }

    // Stacking is canvas-only (Mermaid has no z-order), so it is not an undo step: undo would
    // have restored identical code and done nothing.
    public void ApplyAllZIndices()
    {
        var vm = ViewModel; if (vm is null) return;
        foreach (var n in vm.Nodes) ApplyZIndex(n);
    }

    public void BringToFront(DiagramNodeViewModel? node = null)
    {
        var vm = ViewModel; if (vm is null) return;
        vm.BringToFront(node);
        ApplyAllZIndices();
    }

    public void BringForward(DiagramNodeViewModel? node = null)
    {
        var vm = ViewModel; if (vm is null) return;
        vm.BringForward(node);
        ApplyAllZIndices();
    }

    public void SendBackward(DiagramNodeViewModel? node = null)
    {
        var vm = ViewModel; if (vm is null) return;
        vm.SendBackward(node);
        ApplyAllZIndices();
    }

    public void SendToBack(DiagramNodeViewModel? node = null)
    {
        var vm = ViewModel; if (vm is null) return;
        vm.SendToBack(node);
        ApplyAllZIndices();
    }

    /// <summary>Adds a palette shape in the visible part of the canvas: the free spot nearest the
    /// centre of the view, so a click never drops a shape on top of an existing node.</summary>
    public void AddInView(MermaidPaletteItem item)
    {
        var vm = ViewModel; if (vm is null) return;
        double zoom = Math.Max(0.01, CanvasScrollViewer.ZoomFactor);
        double cx = (CanvasScrollViewer.HorizontalOffset + CanvasScrollViewer.ViewportWidth / 2) / zoom;
        double cy = (CanvasScrollViewer.VerticalOffset + CanvasScrollViewer.ViewportHeight / 2) / zoom;
        double w = item.ShapeType == "TaskBar" ? 260 : 140, h = 60; // AddNodeFromPalette's sizes
        const double gap = 24, step = 30;

        bool Free(double x, double y) => !vm.Nodes.Any(n =>
            x < n.X + n.Width + gap && x + w + gap > n.X && y < n.Y + n.Height + gap && y + h + gap > n.Y);

        // Rings of candidate positions around the centre, nearest first.
        double bestX = cx - w / 2, bestY = cy - h / 2;
        if (!Free(bestX, bestY))
        {
            var found = false;
            for (int ring = 1; ring <= 30 && !found; ring++)
            {
                double bestD = double.MaxValue;
                for (int i = -ring; i <= ring; i++)
                    foreach (var (dx, dy) in new[] { (i, -ring), (i, ring), (-ring, i), (ring, i) })
                    {
                        double x = cx - w / 2 + dx * step, y = cy - h / 2 + dy * step;
                        if (x < 20 || y < 20 || !Free(x, y)) continue;
                        double d = dx * dx + dy * dy;
                        if (d < bestD) { bestD = d; bestX = x; bestY = y; found = true; }
                    }
            }
        }
        vm.AddNodeFromPalette(item, bestX, bestY);
    }

    private void AddNodeAt(Point pos)
    {
        var vm = ViewModel; if (vm is null) return;
        var item = new MermaidPaletteItem { Category = "Flowchart", ShapeType = "Rectangle", DefaultText = "New Node" };
        vm.AddNodeFromPalette(item, pos.X, pos.Y, centreOnPoint: true);
    }

    #endregion
}
