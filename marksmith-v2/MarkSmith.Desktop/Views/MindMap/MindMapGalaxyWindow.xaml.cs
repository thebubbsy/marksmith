using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;
using MarkSmith.Models.MindMap;
using MarkSmith.ViewModels.MindMap;

namespace MarkSmith.Views.MindMap
{
    public sealed partial class MindMapGalaxyWindow : Window
    {
        public MindMapStudioViewModel ViewModel { get; }

        private bool _isPanning;
        private Point _lastPanPoint;
        private MindMapNodeViewModel? _draggedNode;
        private Point _dragStartNodePoint;
        private Point _dragStartPointerPoint;

        public event EventHandler<string>? OpenDocumentRequested;

        public MindMapGalaxyWindow(MindMapStudioViewModel? viewModel = null)
        {
            this.InitializeComponent();
            ViewModel = viewModel ?? new MindMapStudioViewModel();
            this.RootGrid.DataContext = ViewModel;

            ViewModel.CanvasRedrawRequested += (s, e) => RedrawCanvas();
            ViewModel.OpenDocumentRequested += (s, path) => OpenDocumentRequested?.Invoke(this, path);

            this.Activated += (s, e) => RedrawCanvas();
        }

        private Grid RootGrid => (Grid)this.Content;

        public void RedrawCanvas()
        {
            GalaxyCanvas.Children.Clear();

            double zoom = ViewModel.ZoomLevel;
            double panX = ViewModel.ViewportOffsetX;
            double panY = ViewModel.ViewportOffsetY;

            // Center origin in canvas container
            double cx = (CanvasContainer.ActualWidth > 0 ? CanvasContainer.ActualWidth : 900) / 2.0;
            double cy = (CanvasContainer.ActualHeight > 0 ? CanvasContainer.ActualHeight : 600) / 2.0;

            Point ScreenPos(double worldX, double worldY) =>
                new Point(cx + (worldX + panX) * zoom, cy + (worldY + panY) * zoom);

            // 1. Draw Parent -> Child Bezier Connectors
            foreach (var node in ViewModel.Nodes)
            {
                if (string.IsNullOrEmpty(node.ParentId)) continue;
                var parent = ViewModel.Nodes.FirstOrDefault(n => n.Id == node.ParentId);
                if (parent == null) continue;

                var pStart = ScreenPos(parent.X + parent.Width, parent.Y + (parent.Height / 2.0));
                var pEnd = ScreenPos(node.X, node.Y + (node.Height / 2.0));

                var path = CreateBezierConnector(pStart, pEnd, node.ColorHex, false);
                GalaxyCanvas.Children.Add(path);
            }

            // 2. Draw Cross-Links (Synapses)
            foreach (var link in ViewModel.Links)
            {
                var src = ViewModel.Nodes.FirstOrDefault(n => n.Id == link.SourceNodeId);
                var tgt = ViewModel.Nodes.FirstOrDefault(n => n.Id == link.TargetNodeId);
                if (src == null || tgt == null) continue;

                var pStart = ScreenPos(src.X + (src.Width / 2.0), src.Y + (src.Height / 2.0));
                var pEnd = ScreenPos(tgt.X + (tgt.Width / 2.0), tgt.Y + (tgt.Height / 2.0));

                bool isDashed = link.Style == MindMapLinkStyle.Dashed;
                var path = CreateBezierConnector(pStart, pEnd, link.ColorHex, isDashed, 2.5);
                GalaxyCanvas.Children.Add(path);

                // Link Label
                if (!string.IsNullOrEmpty(link.Label))
                {
                    double midX = (pStart.X + pEnd.X) / 2.0;
                    double midY = (pStart.Y + pEnd.Y) / 2.0;
                    var labelBorder = new Border
                    {
                        Background = new SolidColorBrush(ColorFromHex("#1C1C28")),
                        BorderBrush = new SolidColorBrush(ColorFromHex(link.ColorHex)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        Child = new TextBlock
                        {
                            Text = link.Label,
                            FontSize = 10 * zoom,
                            Foreground = new SolidColorBrush(ColorFromHex("#E8E8F0"))
                        }
                    };
                    Canvas.SetLeft(labelBorder, midX - 30);
                    Canvas.SetTop(labelBorder, midY - 10);
                    GalaxyCanvas.Children.Add(labelBorder);
                }
            }

            // 3. Draw Nodes
            foreach (var node in ViewModel.Nodes)
            {
                var p = ScreenPos(node.X, node.Y);
                double nw = node.Width * zoom;
                double nh = node.Height * zoom;

                var nodeBorder = CreateNodeVisual(node, nw, nh, zoom);
                Canvas.SetLeft(nodeBorder, p.X);
                Canvas.SetTop(nodeBorder, p.Y);
                GalaxyCanvas.Children.Add(nodeBorder);
            }

            RedrawMinimap();
        }

        private Microsoft.UI.Xaml.Shapes.Path CreateBezierConnector(Point start, Point end, string hexColor, bool dashed, double strokeThickness = 2.0)
        {
            double dx = Math.Abs(end.X - start.X);
            double ctrlOffset = Math.Max(dx * 0.5, 40);

            var p1 = new Point(start.X + ctrlOffset, start.Y);
            var p2 = new Point(end.X - ctrlOffset, end.Y);

            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = end });

            var geom = new PathGeometry();
            geom.Figures.Add(figure);

            var path = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geom,
                Stroke = new SolidColorBrush(ColorFromHex(hexColor)),
                StrokeThickness = strokeThickness,
                IsHitTestVisible = false
            };

            if (dashed)
            {
                path.StrokeDashArray = new DoubleCollection { 3, 2 };
            }

            return path;
        }

        private FrameworkElement CreateNodeVisual(MindMapNodeViewModel node, double width, double height, double zoom)
        {
            bool isSelected = ViewModel.SelectedNode == node;
            var color = ColorFromHex(node.ColorHex);

            var border = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(ColorFromHex("#1C1C28")),
                BorderBrush = new SolidColorBrush(isSelected ? Colors.White : color),
                BorderThickness = new Thickness(isSelected ? 2.5 : 1.5),
                CornerRadius = new CornerRadius(8 * zoom),
                Padding = new Thickness(8 * zoom, 6 * zoom, 8 * zoom, 6 * zoom),
                Tag = node
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Top Row: Icon + Title + Format Badge
            var topPanel = new Grid();
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconText = new TextBlock
            {
                Text = node.Icon ?? "📄",
                FontSize = 13 * zoom,
                Margin = new Thickness(0, 0, 5 * zoom, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(iconText, 0);
            topPanel.Children.Add(iconText);

            var titleText = new TextBlock
            {
                Text = node.Title,
                FontSize = 11.5 * zoom,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 1);
            topPanel.Children.Add(titleText);

            var badgeBorder = new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(4 * zoom),
                Padding = new Thickness(4 * zoom, 1 * zoom, 4 * zoom, 1 * zoom),
                Margin = new Thickness(4 * zoom, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = node.FormatBadge,
                    FontSize = 9 * zoom,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.White)
                }
            };
            Grid.SetColumn(badgeBorder, 2);
            topPanel.Children.Add(badgeBorder);

            Grid.SetRow(topPanel, 0);
            grid.Children.Add(topPanel);

            // Bottom Row: Progress / Tags (if present)
            if (node.Progress > 0 || node.Tags.Count > 0)
            {
                var bottomPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6 * zoom,
                    Margin = new Thickness(0, 3 * zoom, 0, 0)
                };

                if (node.Progress > 0)
                {
                    var progText = new TextBlock
                    {
                        Text = $"{node.Progress}%",
                        FontSize = 9.5 * zoom,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = new SolidColorBrush(ColorFromHex("#34D399")),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    bottomPanel.Children.Add(progText);
                }

                if (node.Tags.Count > 0)
                {
                    var tagText = new TextBlock
                    {
                        Text = node.Tags[0],
                        FontSize = 9 * zoom,
                        Foreground = new SolidColorBrush(ColorFromHex("#9A9AB0")),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    bottomPanel.Children.Add(tagText);
                }

                Grid.SetRow(bottomPanel, 1);
                grid.Children.Add(bottomPanel);
            }

            border.Child = grid;

            // Events
            border.PointerPressed += (s, e) =>
            {
                e.Handled = true;
                ViewModel.SelectedNode = node;
                _draggedNode = node;
                _dragStartNodePoint = new Point(node.X, node.Y);
                _dragStartPointerPoint = e.GetCurrentPoint(GalaxyCanvas).Position;
                RedrawCanvas();
            };

            border.DoubleTapped += (s, e) =>
            {
                e.Handled = true;
                ViewModel.OpenLinkedDocument(node);
            };

            border.PointerEntered += (s, e) =>
            {
                if (!string.IsNullOrEmpty(node.MarkdownContent))
                {
                    ViewModel.ShowPreviewCard(node);
                }
            };

            return border;
        }

        private static Color ColorFromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Color.FromArgb(255, 255, 124, 77);
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[0..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                return Color.FromArgb(255, r, g, b);
            }
            return Color.FromArgb(255, 255, 124, 77);
        }

        // ---- Canvas Pointer & Pan/Zoom Handlers ----

        private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(GalaxyCanvas).Properties;
            if (props.IsLeftButtonPressed || props.IsMiddleButtonPressed)
            {
                _isPanning = true;
                _lastPanPoint = e.GetCurrentPoint(GalaxyCanvas).Position;
                GalaxyCanvas.CapturePointer(e.Pointer);
            }
        }

        private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var cur = e.GetCurrentPoint(GalaxyCanvas).Position;

            if (_draggedNode != null)
            {
                double dx = (cur.X - _dragStartPointerPoint.X) / ViewModel.ZoomLevel;
                double dy = (cur.Y - _dragStartPointerPoint.Y) / ViewModel.ZoomLevel;
                _draggedNode.X = _dragStartNodePoint.X + dx;
                _draggedNode.Y = _dragStartNodePoint.Y + dy;
                RedrawCanvas();
            }
            else if (_isPanning)
            {
                double dx = (cur.X - _lastPanPoint.X) / ViewModel.ZoomLevel;
                double dy = (cur.Y - _lastPanPoint.Y) / ViewModel.ZoomLevel;
                ViewModel.ViewportOffsetX += dx;
                ViewModel.ViewportOffsetY += dy;
                _lastPanPoint = cur;
                RedrawCanvas();
            }
        }

        private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isPanning = false;
            _draggedNode = null;
            GalaxyCanvas.ReleasePointerCapture(e.Pointer);
        }

        private void OnCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int delta = e.GetCurrentPoint(GalaxyCanvas).Properties.MouseWheelDelta;
            if (delta > 0)
            {
                ViewModel.ZoomLevel = Math.Min(3.0, ViewModel.ZoomLevel * 1.12);
            }
            else if (delta < 0)
            {
                ViewModel.ZoomLevel = Math.Max(0.2, ViewModel.ZoomLevel / 1.12);
            }
            RedrawCanvas();
        }

        // ---- Toolbar Actions ----

        private void OnZoomInClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ZoomLevel = Math.Min(3.0, ViewModel.ZoomLevel * 1.15);
            RedrawCanvas();
        }

        private void OnZoomOutClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ZoomLevel = Math.Max(0.2, ViewModel.ZoomLevel / 1.15);
            RedrawCanvas();
        }

        private void OnFitToWindowClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ZoomLevel = 1.0;
            ViewModel.ViewportOffsetX = 0;
            ViewModel.ViewportOffsetY = 0;
            RedrawCanvas();
        }

        private void OnLayoutTreeClick(object sender, RoutedEventArgs e) => ViewModel.ApplyLayout("tree");
        private void OnLayoutRadialClick(object sender, RoutedEventArgs e) => ViewModel.ApplyLayout("radial");
        private void OnLayoutForceClick(object sender, RoutedEventArgs e) => ViewModel.ApplyLayout("force");
        private void OnLayoutHierarchyClick(object sender, RoutedEventArgs e) => ViewModel.ApplyLayout("vertical");

        private async void OnImportFolderClick(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                await ViewModel.ImportDirectoryAsync(folder.Path);
                RedrawCanvas();
            }
        }

        private async void OnExportDocxClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("Word Document", new List<string> { ".docx" });
            picker.SuggestedFileName = $"{ViewModel.Title}.docx";

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                ViewModel.ExportToDocx(file.Path);
            }
        }

        private async void OnConnectSelectedClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedNode == null)
            {
                ViewModel.StatusMessage = "Pick a source node first, then click Link.";
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "🔗 Connect Document Galaxy Nodes",
                PrimaryButtonText = "Connect",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            var otherNodes = ViewModel.Nodes.Where(n => n.Id != ViewModel.SelectedNode.Id).ToList();
            var combo = new ComboBox
            {
                Header = "Target Document / Node to Link",
                ItemsSource = otherNodes,
                DisplayMemberPath = "Title",
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var labelBox = new TextBox
            {
                Header = "Relationship Reason / Label",
                Text = "spawned during project",
                PlaceholderText = "e.g. depends on, derived from, references"
            };

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(combo);
            panel.Children.Add(labelBox);
            dialog.Content = panel;

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && combo.SelectedItem is MindMapNodeViewModel target)
            {
                ViewModel.ConnectNodes(ViewModel.SelectedNode.Id, target.Id, labelBox.Text);
                RedrawCanvas();
            }
        }

        private void OnPaletteSwatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string hex)
            {
                ViewModel.RecolorSelectedNode(hex);
            }
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel.SelectedThemeName == "Clean White")
            {
                CanvasContainer.Background = new SolidColorBrush(ColorFromHex("#F8FAFC"));
            }
            else if (ViewModel.SelectedThemeName == "Nordic Slate")
            {
                CanvasContainer.Background = new SolidColorBrush(ColorFromHex("#1E293B"));
            }
            else if (ViewModel.SelectedThemeName == "Obsidian Dark")
            {
                CanvasContainer.Background = new SolidColorBrush(ColorFromHex("#0B0C10"));
            }
            else
            {
                CanvasContainer.Background = new SolidColorBrush(ColorFromHex("#12131C"));
            }
            RedrawCanvas();
        }

        private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            string q = sender.Text.Trim();
            if (string.IsNullOrEmpty(q))
            {
                RedrawCanvas();
                return;
            }

            var match = ViewModel.Nodes.FirstOrDefault(n => n.Title.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                ViewModel.SelectedNode = match;
                ViewModel.ViewportOffsetX = -match.X;
                ViewModel.ViewportOffsetY = -match.Y;
                RedrawCanvas();
            }
        }

        private void OnOpenSelectedDocumentClick(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenLinkedDocument(ViewModel.SelectedNode);
        }

        private void OnOpenInEditorClick(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenLinkedDocument(ViewModel.SelectedNode);
            ViewModel.HidePreviewCard();
        }

        private void OnClosePreviewCardClick(object sender, RoutedEventArgs e)
        {
            ViewModel.HidePreviewCard();
        }

        #region Minimap Radar Navigation

        private bool _isMinimapDragging;
        private double _minimapMinX, _minimapMinY, _minimapScale = 1.0;

        private void RedrawMinimap()
        {
            if (MinimapCanvas == null) return;
            MinimapCanvas.Children.Clear();

            if (ViewModel.Nodes.Count == 0) return;

            double minX = ViewModel.Nodes.Min(n => n.X);
            double maxX = ViewModel.Nodes.Max(n => n.X + n.Width);
            double minY = ViewModel.Nodes.Min(n => n.Y);
            double maxY = ViewModel.Nodes.Max(n => n.Y + n.Height);

            // Add world padding
            double padding = 250;
            minX -= padding; maxX += padding;
            minY -= padding; maxY += padding;

            double worldW = Math.Max(100, maxX - minX);
            double worldH = Math.Max(100, maxY - minY);

            double miniW = 168.0;
            double miniH = 108.0;
            double scale = Math.Min(miniW / worldW, miniH / worldH);

            _minimapMinX = minX;
            _minimapMinY = minY;
            _minimapScale = scale;

            // 1. Draw node dots
            foreach (var node in ViewModel.Nodes)
            {
                double nx = (node.X - minX) * scale;
                double ny = (node.Y - minY) * scale;
                double nw = Math.Max(3, node.Width * scale);
                double nh = Math.Max(2, node.Height * scale);

                var dot = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = nw,
                    Height = nh,
                    Fill = new SolidColorBrush(ColorFromHex(node.ColorHex)),
                    RadiusX = 1,
                    RadiusY = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, nx);
                Canvas.SetTop(dot, ny);
                MinimapCanvas.Children.Add(dot);
            }

            // 2. Draw Viewport Camera Lens
            double cx = (CanvasContainer.ActualWidth > 0 ? CanvasContainer.ActualWidth : 900) / 2.0;
            double cy = (CanvasContainer.ActualHeight > 0 ? CanvasContainer.ActualHeight : 600) / 2.0;
            double zoom = ViewModel.ZoomLevel;

            double viewWorldLeft = -ViewModel.ViewportOffsetX - (cx / zoom);
            double viewWorldTop = -ViewModel.ViewportOffsetY - (cy / zoom);
            double viewWorldWidth = (CanvasContainer.ActualWidth > 0 ? CanvasContainer.ActualWidth : 900) / zoom;
            double viewWorldHeight = (CanvasContainer.ActualHeight > 0 ? CanvasContainer.ActualHeight : 600) / zoom;

            double vx = (viewWorldLeft - minX) * scale;
            double vy = (viewWorldTop - minY) * scale;
            double vw = Math.Max(6, viewWorldWidth * scale);
            double vh = Math.Max(4, viewWorldHeight * scale);

            var lens = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = vw,
                Height = vh,
                Stroke = new SolidColorBrush(ColorFromHex("#7C4DFF")),
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(40, 124, 77, 255)),
                RadiusX = 2,
                RadiusY = 2,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(lens, vx);
            Canvas.SetTop(lens, vy);
            MinimapCanvas.Children.Add(lens);
        }

        private void OnMinimapPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isMinimapDragging = true;
            MinimapCanvas.CapturePointer(e.Pointer);
            PanToMinimapPoint(e.GetCurrentPoint(MinimapCanvas).Position);
        }

        private void OnMinimapPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isMinimapDragging)
            {
                PanToMinimapPoint(e.GetCurrentPoint(MinimapCanvas).Position);
            }
        }

        private void OnMinimapPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isMinimapDragging = false;
            MinimapCanvas.ReleasePointerCapture(e.Pointer);
        }

        private void PanToMinimapPoint(Point pt)
        {
            if (_minimapScale <= 0) return;
            double targetWorldX = _minimapMinX + (pt.X / _minimapScale);
            double targetWorldY = _minimapMinY + (pt.Y / _minimapScale);

            ViewModel.ViewportOffsetX = -targetWorldX;
            ViewModel.ViewportOffsetY = -targetWorldY;
            RedrawCanvas();
        }

        #endregion
    }
}
