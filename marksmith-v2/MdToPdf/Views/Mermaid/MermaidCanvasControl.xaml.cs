using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MdToPdf.ViewModels.Mermaid;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Point = Windows.Foundation.Point;
using Rect = MdToPdf.ViewModels.Mermaid.Rect;

namespace MdToPdf.Views.Mermaid;

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

        // The minimap mirrors and navigates the main canvas ScrollViewer.
        MinimapControl.TargetScrollViewer = CanvasScrollViewer;

        ConnectorsItemsControl.PointerPressed += OnConnectorsItemsControlPointerPressed;
        ConnectorsItemsControl.DoubleTapped += OnConnectorsItemsControlDoubleTapped;

        NodesItemsControl.PointerPressed += OnNodesItemsControlPointerPressed;
        NodesItemsControl.PointerMoved += OnNodesItemsControlPointerMoved;
        NodesItemsControl.PointerReleased += OnNodesItemsControlPointerReleased;
        NodesItemsControl.DoubleTapped += OnNodesItemsControlDoubleTapped;

        CanvasScrollViewer.ViewChanged += OnCanvasViewChanged;
    }

    #region Zoom Controls

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

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        float newZoom = CanvasScrollViewer.ZoomFactor + 0.2f;
        if (newZoom <= CanvasScrollViewer.MaxZoomFactor)
        {
            CanvasScrollViewer.ChangeView(null, null, newZoom);
        }
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        float newZoom = CanvasScrollViewer.ZoomFactor - 0.2f;
        if (newZoom >= CanvasScrollViewer.MinZoomFactor)
        {
            CanvasScrollViewer.ChangeView(null, null, newZoom);
        }
    }

    private void OnZoomFitClick(object sender, RoutedEventArgs e)
    {
        FitToContent();
    }

    // Public zoom entry points so the Studio's keyboard accelerators (Ctrl+= / Ctrl+- / Ctrl+0) can
    // drive the canvas without duplicating the zoom math here.
    public void ZoomIn()
    {
        float newZoom = CanvasScrollViewer.ZoomFactor + 0.2f;
        if (newZoom <= CanvasScrollViewer.MaxZoomFactor)
            CanvasScrollViewer.ChangeView(null, null, newZoom);
    }

    public void ZoomOut()
    {
        float newZoom = CanvasScrollViewer.ZoomFactor - 0.2f;
        if (newZoom >= CanvasScrollViewer.MinZoomFactor)
            CanvasScrollViewer.ChangeView(null, null, newZoom);
    }

    public void ZoomReset() => CanvasScrollViewer.ChangeView(null, null, 1.0f);

    /// <summary>Sets the canvas zoom to an explicit factor (driven by the toolbar slider).</summary>
    public void SetZoomFactor(double factor)
    {
        float clamped = (float)Math.Clamp(factor, CanvasScrollViewer.MinZoomFactor, CanvasScrollViewer.MaxZoomFactor);
        if (Math.Abs(CanvasScrollViewer.ZoomFactor - clamped) > 0.001f)
            CanvasScrollViewer.ChangeView(null, null, clamped);
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
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var n in vm.Nodes)
        {
            minX = Math.Min(minX, n.X);
            minY = Math.Min(minY, n.Y);
            maxX = Math.Max(maxX, n.X + n.Width);
            maxY = Math.Max(maxY, n.Y + n.Height);
        }

        double contentW = maxX - minX + pad * 2;
        double contentH = maxY - minY + pad * 2;
        double vpW = CanvasScrollViewer.ViewportWidth;
        double vpH = CanvasScrollViewer.ViewportHeight;
        if (vpW <= 0 || vpH <= 0)
        {
            CanvasScrollViewer.ChangeView(0, 0, 1.0f);
            return;
        }

        float zoom = (float)Math.Clamp(
            Math.Min(vpW / contentW, vpH / contentH),
            CanvasScrollViewer.MinZoomFactor,
            CanvasScrollViewer.MaxZoomFactor);

        // Offsets are in un-scaled content coordinates; center the bounding box.
        double offsetX = (minX - pad) - (vpW / zoom - contentW) / 2;
        double offsetY = (minY - pad) - (vpH / zoom - contentH) / 2;

        CanvasScrollViewer.ChangeView(Math.Max(0, offsetX), Math.Max(0, offsetY), zoom);
    }

    #endregion

    #region Drag & Drop from Palette

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains("MermaidShapeType"))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
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

            ViewModel.AddNodeFromPalette(paletteItem, dropPos.X, dropPos.Y);
        }
    }

    #endregion

    #region Interactive Node Moving & Anchors

    private void OnNodesItemsControlPointerPressed(object sender, PointerRoutedEventArgs e)
    {
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

    // ---- Hover glow + anchor reveal -----------------------------------------------------------
    // Setting IsHovered drives the hover ring and reveals the connector anchor dots (bound to
    // ShowAnchors in the node template). Hover is suppressed mid-drag so the glow doesn't flicker
    // as the pointer crosses other nodes during a move.
    private void OnNodePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingNode || _isResizingNode || _isDrawingConnector) return;
        if (sender is FrameworkElement { DataContext: DiagramNodeViewModel node })
            node.IsHovered = true;
    }

    private void OnNodePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DiagramNodeViewModel node })
            node.IsHovered = false;
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
        var newNode = ViewModel.AddNodeFromPalette(item, pos.X, pos.Y);
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
        if (ReferenceEquals(e.OriginalSource, InfiniteCanvasGrid) || ReferenceEquals(e.OriginalSource, GridPatternCanvas) || ReferenceEquals(e.OriginalSource, CanvasRootGrid))
        {
            var pointerPoint = e.GetCurrentPoint(InfiniteCanvasGrid);
            bool isMiddleButton = pointerPoint.Properties.IsMiddleButtonPressed;
            bool isRightButton = pointerPoint.Properties.IsRightButtonPressed;
            bool isCtrlClick = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0 && pointerPoint.Properties.IsLeftButtonPressed;

            // Right-click: defer to release — a stationary right-click opens the context menu, a
            // right-DRAG pans. Capture the element under the cursor so the menu knows its target.
            if (isRightButton)
            {
                _rightClickPending = true;
                _rightClickStartPos = e.GetCurrentPoint(CanvasScrollViewer).Position;
                _rightClickNode = (e.OriginalSource as FrameworkElement)?.DataContext as DiagramNodeViewModel;
                _rightClickConnector = (e.OriginalSource as FrameworkElement)?.DataContext as DiagramConnectorViewModel;
                _isPanning = true; // becomes a pan if it turns into a drag
                _panStartMousePos = _rightClickStartPos;
                _panStartScrollX = CanvasScrollViewer.HorizontalOffset;
                _panStartScrollY = CanvasScrollViewer.VerticalOffset;
                InfiniteCanvasGrid.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

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

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            var currentPos = e.GetCurrentPoint(CanvasScrollViewer).Position;
            double deltaX = currentPos.X - _panStartMousePos.X;
            double deltaY = currentPos.Y - _panStartMousePos.Y;

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
        if (_isPanning)
        {
            bool wasRightClick = _rightClickPending;
            _isPanning = false;
            _rightClickPending = false;
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
                var targetNode = ViewModel.Nodes.FirstOrDefault(n =>
                    n.Id != _connectorSourceNode.Id &&
                    releasePos.X >= n.X && releasePos.X <= n.X + n.Width &&
                    releasePos.Y >= n.Y && releasePos.Y <= n.Y + n.Height);

                if (targetNode != null)
                {
                    ViewModel.AddConnector(_connectorSourceNode.Id, _connectorSourceAnchor, targetNode.Id, "Top");
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

        var target = vm.Nodes.FirstOrDefault(n =>
            n != _connectorSourceNode &&
            mousePos.X >= n.X && mousePos.X <= n.X + n.Width &&
            mousePos.Y >= n.Y && mousePos.Y <= n.Y + n.Height);

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

    #endregion

    #region In-Place Inline Text Editing & Connectors

    private void OnConnectorsItemsControlPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element && element.DataContext is DiagramConnectorViewModel connVM)
        {
            if (ViewModel != null)
            {
                ViewModel.SelectedConnector = connVM;
                ViewModel.SelectedNode = null;
            }
        }
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

    private void StartNodeInPlaceEdit(DiagramNodeViewModel node)
    {
        _editingNode = node;
        _editingConnector = null;

        Canvas.SetLeft(InPlaceEditorCanvas, node.X);
        Canvas.SetTop(InPlaceEditorCanvas, node.Y);
        InlineEditTextBox.Width = Math.Max(node.Width, 140);
        InlineEditTextBox.Height = Math.Max(node.Height, 60);
        InlineEditTextBox.Text = node.LabelText;

        InPlaceEditorCanvas.Visibility = Visibility.Visible;
        InlineEditTextBox.Focus(FocusState.Programmatic);
        InlineEditTextBox.SelectAll();
    }

    private void StartConnectorInPlaceEdit(DiagramConnectorViewModel conn)
    {
        _editingConnector = conn;
        _editingNode = null;

        Canvas.SetLeft(InPlaceEditorCanvas, conn.MidpointX - 40);
        Canvas.SetTop(InPlaceEditorCanvas, conn.MidpointY - 15);
        InlineEditTextBox.Width = 120;
        InlineEditTextBox.Height = 35;
        InlineEditTextBox.Text = conn.Label ?? string.Empty;

        InPlaceEditorCanvas.Visibility = Visibility.Visible;
        InlineEditTextBox.Focus(FocusState.Programmatic);
        InlineEditTextBox.SelectAll();
    }

    private void OnInlineEditKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitInPlaceEdit();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelInPlaceEdit();
            e.Handled = true;
        }
    }

    private void OnInlineEditLostFocus(object sender, RoutedEventArgs e)
    {
        CommitInPlaceEdit();
    }

    private void CommitInPlaceEdit()
    {
        if (_editingNode != null)
        {
            var newText = InlineEditTextBox.Text;
            // Only record an undo step when the rename actually changed something.
            if (!string.Equals(newText, _editingNode.LabelText, StringComparison.Ordinal))
            {
                ViewModel?.SnapshotForUndo();
                _editingNode.LabelText = newText;
                _editingNode.RecalculateBoundsForText();
                ViewModel?.UpdateConnectedConnectors(_editingNode);
            }
            _editingNode = null;
        }
        else if (_editingConnector != null)
        {
            var newText = InlineEditTextBox.Text;
            if (!string.Equals(newText, _editingConnector.Label, StringComparison.Ordinal))
            {
                ViewModel?.SnapshotForUndo();
                _editingConnector.Label = newText;
            }
            _editingConnector = null;
        }

        InPlaceEditorCanvas.Visibility = Visibility.Collapsed;
    }

    private void CancelInPlaceEdit()
    {
        _editingNode = null;
        _editingConnector = null;
        InPlaceEditorCanvas.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region Context Menu

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

            flyout.Items.Add(MenuItem("Edit Label", "\uE8BF", (s, e) => StartNodeInPlaceEdit(node)));
            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            flyout.Items.Add(MenuItem("Duplicate (Ctrl+D)", "\uE8C8", (s, e) => vm.DuplicateSelected()));
            flyout.Items.Add(MenuItem("Copy (Ctrl+C)", "\uE8C8", (s, e) => vm.CopySelected()));
            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            flyout.Items.Add(MenuItem("Bring to Front", "\uE74A", (s, e) => BringToFront(node)));
            flyout.Items.Add(MenuItem("Send to Back", "\uE74B", (s, e) => SendToBack(node)));
            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            flyout.Items.Add(MenuItem("Delete (Del)", "\uE74D", (s, e) => vm.DeleteSelected()));
        }
        else if (_rightClickConnector is { } conn)
        {
            vm.SelectedConnector = conn;
            vm.SelectedNode = null;

            flyout.Items.Add(MenuItem("Edit Label", "\uE8BF", (s, e) => StartConnectorInPlaceEdit(conn)));
            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            flyout.Items.Add(MenuItem("Delete Connector", "\uE74D", (s, e) => vm.DeleteSelected()));
        }
        else
        {
            // Empty canvas.
            flyout.Items.Add(MenuItem("Add Node Here", "\uE710", (s, e) => AddNodeAt(canvasPos)));
            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            flyout.Items.Add(MenuItem("Paste (Ctrl+V)", "\uE77F", (s, e) => vm.PasteClipboard()));
            flyout.Items.Add(MenuItem("Select All (Ctrl+A)", "\uE8B3", (s, e) => vm.SelectAll()));
            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            flyout.Items.Add(MenuItem("Fit to Content", "\uE73F", (s, e) => FitToContent()));
            flyout.Items.Add(MenuItem("Reset Zoom (Ctrl+0)", "\uE8A3", (s, e) => ZoomReset()));
        }

        // Anchor the flyout to the canvas at the pointer position.
        var anchor = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = canvasPos
        };
        flyout.ShowAt(InfiniteCanvasGrid, anchor);
    }

    private static Microsoft.UI.Xaml.Controls.MenuFlyoutItem MenuItem(string text, string glyph, RoutedEventHandler handler)
    {
        var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = text,
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = glyph }
        };
        item.Click += handler;
        return item;
    }

    private void BringToFront(DiagramNodeViewModel node)
    {
        var vm = ViewModel; if (vm is null) return;
        vm.SnapshotForUndo();
        int maxZ = vm.Nodes.Max(n => n.ZIndex);
        node.ZIndex = maxZ + 1;
        vm.StatusText = $"Brought '{node.Id}' to front.";
    }

    private void SendToBack(DiagramNodeViewModel node)
    {
        var vm = ViewModel; if (vm is null) return;
        vm.SnapshotForUndo();
        int minZ = vm.Nodes.Min(n => n.ZIndex);
        node.ZIndex = minZ - 1;
        vm.StatusText = $"Sent '{node.Id}' to back.";
    }

    private void AddNodeAt(Point pos)
    {
        var vm = ViewModel; if (vm is null) return;
        var item = new MermaidPaletteItem { Category = "Flowchart", ShapeType = "Rectangle", DefaultText = "New Node" };
        vm.AddNodeFromPalette(item, pos.X, pos.Y);
    }

    #endregion
}
