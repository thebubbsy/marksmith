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

    private bool _isRubberbanding;
    private Point _rubberbandStartPoint;

    private bool _isPanning;
    private Point _panStartMousePos;
    private double _panStartScrollX;
    private double _panStartScrollY;

    private bool _isDrawingConnector;
    private DiagramNodeViewModel? _connectorSourceNode;
    private string _connectorSourceAnchor = "Bottom";

    private DiagramNodeViewModel? _editingNode;
    private DiagramConnectorViewModel? _editingConnector;

    public MermaidStudioViewModel? ViewModel => DataContext as MermaidStudioViewModel;

    public MermaidCanvasControl()
    {
        InitializeComponent();

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
        if (ZoomLevelText != null && CanvasScrollViewer != null)
        {
            int zoomPercent = (int)Math.Round(CanvasScrollViewer.ZoomFactor * 100);
            ZoomLevelText.Text = $"{zoomPercent}%";
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
        CanvasScrollViewer.ChangeView(null, null, 1.0f);
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

                if (ViewModel != null)
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
        if (_isDraggingNode && _draggedNode != null && ViewModel != null && _initialSelectedNodePositions != null)
        {
            Point currentPos = e.GetCurrentPoint(InfiniteCanvasGrid).Position;
            double deltaX = currentPos.X - _pointerStartPos.X;
            double deltaY = currentPos.Y - _pointerStartPos.Y;

            foreach (var kvp in _initialSelectedNodePositions)
            {
                var node = kvp.Key;
                var startPos = kvp.Value;

                double newX = Math.Max(10, startPos.X + deltaX);
                double newY = Math.Max(10, startPos.Y + deltaY);

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
        if (_isDraggingNode)
        {
            _isDraggingNode = false;
            _draggedNode = null;
            _initialSelectedNodePositions = null;
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

            if (isMiddleButton || isRightButton || isCtrlClick)
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
            e.Handled = true;
        }
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            InfiniteCanvasGrid.ReleasePointerCapture(e.Pointer);
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
            DraftConnectorPath.Visibility = Visibility.Collapsed;
            NodesItemsControl.ReleasePointerCapture(e.Pointer);
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

        Canvas.SetLeft(InlineEditContainer, node.X);
        Canvas.SetTop(InlineEditContainer, node.Y);
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

        Canvas.SetLeft(InlineEditContainer, conn.MidpointX - 40);
        Canvas.SetTop(InlineEditContainer, conn.MidpointY - 15);
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

    private void OnInlineAcceptClick(object sender, RoutedEventArgs e)
    {
        CommitInPlaceEdit();
    }

    private void OnInlineCancelClick(object sender, RoutedEventArgs e)
    {
        CancelInPlaceEdit();
    }

    private void CommitInPlaceEdit()
    {
        if (_editingNode != null)
        {
            _editingNode.LabelText = InlineEditTextBox.Text;
            _editingNode.RecalculateBoundsForText();
            ViewModel?.UpdateConnectedConnectors(_editingNode);
            _editingNode = null;
        }
        else if (_editingConnector != null)
        {
            _editingConnector.Label = InlineEditTextBox.Text;
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
}
