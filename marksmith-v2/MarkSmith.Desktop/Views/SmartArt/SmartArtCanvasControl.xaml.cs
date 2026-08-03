using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using MarkSmith.ViewModels.SmartArt;

namespace MarkSmith.Views.SmartArt;

public sealed partial class SmartArtCanvasControl : UserControl
{
    private bool _isDraggingNode;
    private Point _dragStartPoint;
    private SmartArtCanvasNodeViewModel? _activeNode;

    public SmartArtCanvasControl()
    {
        InitializeComponent();
    }

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (DataContext is SmartArtDesignerViewModel vm)
        {
            var pos = e.GetPosition(MainCanvas);

            string category = await e.DataView.GetDataAsync("SmartArtCategory") as string ?? "Basic";
            string shapeType = await e.DataView.GetDataAsync("SmartArtShapeType") as string ?? "roundRect";
            string text = await e.DataView.GetDataAsync("SmartArtText") as string ?? "New Shape";
            string color = await e.DataView.GetDataAsync("SmartArtColor") as string ?? "#0078d4";

            var newNode = new SmartArtCanvasNodeViewModel
            {
                X = Math.Max(20, pos.X - 60),
                Y = Math.Max(20, pos.Y - 30),
                Width = 130,
                Height = 60,
                ShapeType = shapeType,
                Category = category,
                Text = text,
                Color = color
            };

            vm.CanvasNodes.Add(newNode);
            vm.SyncCanvasToMarkdown();
        }
    }

    private void OnNodePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is SmartArtCanvasNodeViewModel node)
        {
            _activeNode = node;
            _isDraggingNode = true;
            _dragStartPoint = e.GetCurrentPoint(MainCanvas).Position;
            elem.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnNodePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingNode && _activeNode != null)
        {
            var currentPoint = e.GetCurrentPoint(MainCanvas).Position;
            double dx = currentPoint.X - _dragStartPoint.X;
            double dy = currentPoint.Y - _dragStartPoint.Y;

            _activeNode.X = Math.Max(0, _activeNode.X + dx);
            _activeNode.Y = Math.Max(0, _activeNode.Y + dy);

            _dragStartPoint = currentPoint;
            e.Handled = true;
        }
    }

    private void OnNodePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingNode)
        {
            _isDraggingNode = false;
            if (sender is FrameworkElement elem)
            {
                elem.ReleasePointerCapture(e.Pointer);
            }
            if (DataContext is SmartArtDesignerViewModel vm)
            {
                vm.SyncCanvasToMarkdown();
            }
            _activeNode = null;
            e.Handled = true;
        }
    }

    private void OnNodeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is SmartArtCanvasNodeViewModel node)
        {
            node.IsEditingText = true;
            e.Handled = true;
        }
    }

    private void OnNodeTextLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is SmartArtCanvasNodeViewModel node)
        {
            node.IsEditingText = false;
            if (DataContext is SmartArtDesignerViewModel vm)
            {
                vm.SyncCanvasToMarkdown();
            }
        }
    }

    private void OnNodeTextKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && sender is FrameworkElement elem && elem.DataContext is SmartArtCanvasNodeViewModel node)
        {
            node.IsEditingText = false;
            if (DataContext is SmartArtDesignerViewModel vm)
            {
                vm.SyncCanvasToMarkdown();
            }
            e.Handled = true;
        }
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Clear selection
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e) { }
    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e) { }
}
