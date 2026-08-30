using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
using MarkSmith.Services.MindMap;
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
        private bool _dragMovedNode;
        private bool _initialized;

        // Redraws are coalesced onto the dispatcher: a drag raises a redraw request per pointer
        // move, and rebuilding the whole canvas synchronously for each one made dragging a large
        // map crawl.
        private bool _redrawQueued;

        private GalaxyPalette _palette = GalaxyPalette.MidnightGalaxy;

        public event EventHandler<string>? OpenDocumentRequested;

        public MindMapGalaxyWindow(MindMapStudioViewModel? viewModel = null)
        {
            this.InitializeComponent();
            ViewModel = viewModel ?? new MindMapStudioViewModel();
            this.RootGrid.DataContext = ViewModel;

            ViewModel.CanvasRedrawRequested += (s, e) => RequestRedraw();
            ViewModel.OpenDocumentRequested += (s, path) => OpenDocumentRequested?.Invoke(this, path);

            // The canvas has to be able to take focus or the window never sees a key press.
            GalaxyCanvas.IsTabStop = true;

            this.Activated += OnWindowActivated;
            this.RootGrid.KeyDown += OnRootKeyDown;
        }

        private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (!_initialized)
            {
                _initialized = true;
                // Load the user's saved galaxy. Until this call existed the studio showed the
                // built-in sample every single time, no matter what had been saved.
                await ViewModel.InitializeAsync();
                FitToWindow();
            }
            RequestRedraw();
        }

        // ---- Redraw scheduling ----

        private void RequestRedraw()
        {
            if (_redrawQueued) return;
            _redrawQueued = true;

            var queue = this.DispatcherQueue;
            if (queue == null)
            {
                _redrawQueued = false;
                RedrawCanvas();
                return;
            }

            queue.TryEnqueue(() =>
            {
                _redrawQueued = false;
                RedrawCanvas();
            });
        }

        private void OnCanvasContainerSizeChanged(object sender, SizeChangedEventArgs e) => RequestRedraw();

        // ---- Canvas rendering ----

        private double CanvasWidth => CanvasContainer.ActualWidth > 0 ? CanvasContainer.ActualWidth : 900;
        private double CanvasHeight => CanvasContainer.ActualHeight > 0 ? CanvasContainer.ActualHeight : 600;

        private Point ScreenPos(double worldX, double worldY)
        {
            double zoom = ViewModel.ZoomLevel;
            return new Point(
                (CanvasWidth / 2.0) + (worldX + ViewModel.ViewportOffsetX) * zoom,
                (CanvasHeight / 2.0) + (worldY + ViewModel.ViewportOffsetY) * zoom);
        }

        public void RedrawCanvas()
        {
            GalaxyCanvas.Children.Clear();

            double zoom = ViewModel.ZoomLevel;
            var byId = new Dictionary<string, MindMapNodeViewModel>(StringComparer.Ordinal);
            foreach (var n in ViewModel.Nodes) byId[n.Id] = n;

            // 1. Hierarchy connectors, drawn first so cards sit on top of their own lines.
            foreach (var node in ViewModel.Nodes)
            {
                if (node.ParentId == null || !byId.TryGetValue(node.ParentId, out var parent)) continue;

                var pStart = ScreenPos(parent.X + parent.Width, parent.Y + (parent.Height / 2.0));
                var pEnd = ScreenPos(node.X, node.Y + (node.Height / 2.0));

                bool faded = node.IsDimmed && parent.IsDimmed;
                var path = CreateBezierConnector(pStart, pEnd, _palette.HierarchyLine, dashed: false,
                    strokeThickness: 1.8 * Math.Max(zoom, 0.35), opacity: faded ? 0.18 : 0.75);
                GalaxyCanvas.Children.Add(path);
            }

            // 2. Cross-links — the edges that carry the meaning.
            foreach (var link in ViewModel.Links)
            {
                if (!byId.TryGetValue(link.SourceNodeId, out var src) || !byId.TryGetValue(link.TargetNodeId, out var tgt)) continue;

                var pStart = ScreenPos(src.X + (src.Width / 2.0), src.Y + (src.Height / 2.0));
                var pEnd = ScreenPos(tgt.X + (tgt.Width / 2.0), tgt.Y + (tgt.Height / 2.0));

                bool faded = src.IsDimmed && tgt.IsDimmed;
                bool dashed = link.Style == MindMapLinkStyle.Dashed || link.IsInferred;
                double thickness = (link.IsSelected ? 3.6 : (link.IsInferred ? 1.6 : 2.6)) * Math.Max(zoom, 0.35);
                var color = link.IsSelected ? ColorFromHex("#FFFFFF") : ColorFromHex(link.ColorHex);

                var path = CreateBezierConnector(pStart, pEnd, color, dashed, thickness,
                    opacity: faded ? 0.15 : (link.IsInferred ? 0.62 : 0.95));
                GalaxyCanvas.Children.Add(path);

                // A generous transparent stroke on top makes the line clickable. The old canvas set
                // IsHitTestVisible=false on every connector, so a link could never be selected and
                // the entire link inspector and "delete link" path were unreachable.
                var hit = CreateBezierConnector(pStart, pEnd, Colors.Transparent, dashed: false, strokeThickness: 16);
                hit.IsHitTestVisible = true;
                hit.Tag = link;
                hit.PointerPressed += OnLinkPointerPressed;
                hit.ContextFlyout = BuildLinkContextMenu(link);
                GalaxyCanvas.Children.Add(hit);

                if (!faded && link.Direction != MindMapLinkDirection.None)
                {
                    AddArrowHead(pStart, pEnd, color, zoom, link.Direction);
                }

                if (!faded && zoom > 0.55 && !string.IsNullOrWhiteSpace(link.DisplayLabel))
                {
                    AddLinkLabel(link, pStart, pEnd, zoom);
                }
            }

            // 3. Nodes.
            foreach (var node in ViewModel.Nodes)
            {
                var p = ScreenPos(node.X, node.Y);
                var visual = CreateNodeVisual(node, node.Width * zoom, node.Height * zoom, zoom);
                Canvas.SetLeft(visual, p.X);
                Canvas.SetTop(visual, p.Y);
                GalaxyCanvas.Children.Add(visual);
            }

            RedrawMinimap();
        }

        private void AddLinkLabel(MindMapLinkViewModel link, Point start, Point end, double zoom)
        {
            var labelBorder = new Border
            {
                Background = new SolidColorBrush(_palette.CardBackground),
                BorderBrush = new SolidColorBrush(ColorFromHex(link.ColorHex)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6 * zoom, 2 * zoom, 6 * zoom, 2 * zoom),
                Opacity = link.IsInferred ? 0.8 : 1.0,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = link.DisplayLabel,
                    FontSize = Math.Max(8, 10 * zoom),
                    FontStyle = link.IsInferred ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                    Foreground = new SolidColorBrush(_palette.Text)
                }
            };

            // Measure so the pill is genuinely centred on the line. The old code subtracted a fixed
            // 30/10, which left every label visibly off-centre and overlapping the connector.
            labelBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = labelBorder.DesiredSize.Width;
            double h = labelBorder.DesiredSize.Height;

            Canvas.SetLeft(labelBorder, ((start.X + end.X) / 2.0) - (w / 2.0));
            Canvas.SetTop(labelBorder, ((start.Y + end.Y) / 2.0) - (h / 2.0));
            GalaxyCanvas.Children.Add(labelBorder);
        }

        /// <summary>Direction is part of the model and was never drawn — an arrowless line cannot
        /// tell you whether the research fed the proposal or the other way round.</summary>
        private void AddArrowHead(Point start, Point end, Color color, double zoom, MindMapLinkDirection direction)
        {
            if (direction == MindMapLinkDirection.SourceToTarget || direction == MindMapLinkDirection.Bidirectional)
            {
                GalaxyCanvas.Children.Add(BuildArrow(start, end, color, zoom));
            }
            if (direction == MindMapLinkDirection.TargetToSource || direction == MindMapLinkDirection.Bidirectional)
            {
                GalaxyCanvas.Children.Add(BuildArrow(end, start, color, zoom));
            }
        }

        private static Polygon BuildArrow(Point from, Point to, Color color, double zoom)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001) { dx = 1; dy = 0; len = 1; }
            dx /= len;
            dy /= len;

            // Back the head off the card edge so it points at the node rather than sitting under it.
            double inset = 26 * Math.Max(zoom, 0.4);
            var tip = new Point(to.X - dx * inset, to.Y - dy * inset);
            double size = Math.Max(5, 9 * zoom);

            var left = new Point(tip.X - dx * size - dy * (size * 0.5), tip.Y - dy * size + dx * (size * 0.5));
            var right = new Point(tip.X - dx * size + dy * (size * 0.5), tip.Y - dy * size - dx * (size * 0.5));

            var arrow = new Polygon
            {
                Fill = new SolidColorBrush(color),
                IsHitTestVisible = false
            };
            arrow.Points.Add(tip);
            arrow.Points.Add(left);
            arrow.Points.Add(right);
            return arrow;
        }

        private Microsoft.UI.Xaml.Shapes.Path CreateBezierConnector(Point start, Point end, string hexColor, bool dashed, double strokeThickness = 2.0, double opacity = 1.0)
            => CreateBezierConnector(start, end, ColorFromHex(hexColor), dashed, strokeThickness, opacity);

        private Microsoft.UI.Xaml.Shapes.Path CreateBezierConnector(Point start, Point end, Color color, bool dashed, double strokeThickness = 2.0, double opacity = 1.0)
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
                Stroke = new SolidColorBrush(color),
                StrokeThickness = strokeThickness,
                StrokeLineJoin = PenLineJoin.Round,
                Opacity = opacity,
                IsHitTestVisible = false
            };

            if (dashed)
            {
                path.StrokeDashArray = new DoubleCollection { 3, 2.5 };
                path.StrokeDashCap = PenLineCap.Round;
            }

            return path;
        }

        private FrameworkElement CreateNodeVisual(MindMapNodeViewModel node, double width, double height, double zoom)
        {
            bool isSelected = node.IsSelected;
            var accent = ColorFromHex(node.ColorHex);

            Color borderColor;
            double borderThickness;
            if (isSelected) { borderColor = Colors.White; borderThickness = 2.6; }
            else if (node.IsHighlighted) { borderColor = ColorFromHex("#22D3EE"); borderThickness = 2.4; }
            else if (node.IsNeighbor) { borderColor = accent; borderThickness = 2.2; }
            else { borderColor = accent; borderThickness = node.IsHub ? 2.2 : 1.4; }

            var border = new Border
            {
                Width = Math.Max(24, width),
                Height = Math.Max(18, height),
                Background = new SolidColorBrush(_palette.CardBackground),
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(borderThickness),
                Opacity = node.IsDimmed ? 0.22 : 1.0,
                CornerRadius = new CornerRadius(Math.Max(3, 9 * zoom)),
                Padding = new Thickness(8 * zoom, 5 * zoom, 8 * zoom, 5 * zoom),
                Tag = node
            };

            // Below this scale the card is a few pixels tall and text only turns it into mush; the
            // coloured chip alone still reads as a star in the constellation.
            if (zoom < 0.42)
            {
                border.Background = new SolidColorBrush(accent);
                AttachNodeInteractions(border, node);
                return border;
            }

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Top row: icon + title + format badge
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
                Foreground = new SolidColorBrush(_palette.Text),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 1);
            topPanel.Children.Add(titleText);

            var badgeBorder = new Border
            {
                Background = new SolidColorBrush(accent),
                CornerRadius = new CornerRadius(4 * zoom),
                Padding = new Thickness(4 * zoom, 1 * zoom, 4 * zoom, 1 * zoom),
                Margin = new Thickness(4 * zoom, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = node.FormatBadge,
                    FontSize = 9 * zoom,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(ReadableOn(accent))
                }
            };
            Grid.SetColumn(badgeBorder, 2);
            topPanel.Children.Add(badgeBorder);

            Grid.SetRow(topPanel, 0);
            grid.Children.Add(topPanel);

            var bottomPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6 * zoom,
                Margin = new Thickness(0, 3 * zoom, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            };

            if (node.Progress > 0)
            {
                // A number alone made progress invisible at a glance; a real bar reads across a
                // whole map without being focused on.
                var track = new Border
                {
                    Width = Math.Max(18, 46 * zoom),
                    Height = Math.Max(3, 4 * zoom),
                    CornerRadius = new CornerRadius(2 * zoom),
                    Background = new SolidColorBrush(ColorFromHex("#33FFFFFF")),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var fill = new Border
                {
                    Width = Math.Max(1, track.Width * (node.Progress / 100.0)),
                    Height = track.Height,
                    CornerRadius = track.CornerRadius,
                    Background = new SolidColorBrush(ColorFromHex("#34D399")),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                track.Child = fill;
                bottomPanel.Children.Add(track);

                bottomPanel.Children.Add(new TextBlock
                {
                    Text = node.ProgressText,
                    FontSize = 9 * zoom,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(ColorFromHex("#34D399")),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (node.Tags.Count > 0)
            {
                bottomPanel.Children.Add(new TextBlock
                {
                    Text = node.Tags[0],
                    FontSize = 9 * zoom,
                    Foreground = new SolidColorBrush(_palette.Muted),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (node.ConnectionCount > 0)
            {
                bottomPanel.Children.Add(new TextBlock
                {
                    Text = node.IsHub ? $"🔗 {node.ConnectionCount} hub" : $"🔗 {node.ConnectionCount}",
                    FontSize = 9 * zoom,
                    FontWeight = node.IsHub ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = new SolidColorBrush(node.IsHub ? ColorFromHex("#FBBF24") : _palette.Muted),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (node.HasVersions)
            {
                bottomPanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(ColorFromHex("#1C2D42")),
                    CornerRadius = new CornerRadius(3 * zoom),
                    Padding = new Thickness(4 * zoom, 1 * zoom, 4 * zoom, 1 * zoom),
                    Child = new TextBlock
                    {
                        Text = $"⏱️ {node.VersionCount}",
                        FontSize = 8.5 * zoom,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(ColorFromHex("#38BDF8"))
                    }
                });
            }

            if (node.IsFileMissing)
            {
                bottomPanel.Children.Add(new TextBlock
                {
                    Text = "⚠",
                    FontSize = 10 * zoom,
                    Foreground = new SolidColorBrush(ColorFromHex("#E11D48")),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (bottomPanel.Children.Count > 0)
            {
                Grid.SetRow(bottomPanel, 1);
                grid.Children.Add(bottomPanel);
            }

            border.Child = grid;
            AttachNodeInteractions(border, node);
            return border;
        }

        private void AttachNodeInteractions(Border border, MindMapNodeViewModel node)
        {
            border.ContextFlyout = BuildNodeContextMenu(node);

            border.PointerPressed += (s, e) =>
            {
                e.Handled = true;
                GalaxyCanvas.Focus(FocusState.Programmatic);
                ViewModel.SelectedNode = node;
                _ = node.RefreshVersionHistoryAsync(AppServices.VersionHistory);

                _draggedNode = node;
                _dragMovedNode = false;
                _dragStartNodePoint = new Point(node.X, node.Y);
                _dragStartPointerPoint = e.GetCurrentPoint(GalaxyCanvas).Position;

                // Capture on the canvas, not the card. The card is destroyed and rebuilt on the
                // very next redraw, which silently dropped the capture and made a drag stop the
                // moment the pointer left the original card bounds.
                GalaxyCanvas.CapturePointer(e.Pointer);
                RequestRedraw();
            };

            border.DoubleTapped += (s, e) =>
            {
                e.Handled = true;
                ViewModel.OpenLinkedDocument(node);
            };

            border.PointerEntered += (s, e) =>
            {
                node.IsHovered = true;
                if (!string.IsNullOrWhiteSpace(node.MarkdownContent))
                {
                    ViewModel.ShowPreviewCard(node);
                }
            };

            border.PointerExited += (s, e) =>
            {
                node.IsHovered = false;
                // The preview used to appear on hover and then never go away, so it sat over the
                // canvas covering the very nodes you were trying to reach. It now follows the
                // pointer off the card unless the node is the current selection.
                if (ViewModel.SelectedNode != node && ViewModel.PreviewTitle == node.Title)
                {
                    ViewModel.HidePreviewCard();
                }
            };
        }

        private MenuFlyout BuildNodeContextMenu(MindMapNodeViewModel node)
        {
            var flyout = new MenuFlyout();

            var openItem = new MenuFlyoutItem { Text = "📂 Open document" };
            openItem.Click += (s, e) => ViewModel.OpenLinkedDocument(node);
            flyout.Items.Add(openItem);

            var linkItem = new MenuFlyoutItem { Text = "🔗 Link to another document…" };
            linkItem.Click += async (s, e) =>
            {
                ViewModel.SelectedNode = node;
                await ShowConnectDialogAsync();
            };
            flyout.Items.Add(linkItem);

            var moveItem = new MenuFlyoutItem { Text = "🔀 Move under…" };
            moveItem.Click += async (s, e) => await ShowReparentDialogAsync(node);
            flyout.Items.Add(moveItem);

            var focusItem = new MenuFlyoutItem { Text = "🔦 Focus on this constellation" };
            focusItem.Click += (s, e) =>
            {
                ViewModel.SelectedNode = node;
                ViewModel.IsFocusModeEnabled = true;
                ViewModel.CenterOn(node);
            };
            flyout.Items.Add(focusItem);

            var duplicateItem = new MenuFlyoutItem { Text = "⧉ Duplicate node" };
            duplicateItem.Click += (s, e) =>
            {
                ViewModel.SelectedNode = node;
                ViewModel.DuplicateSelectedNode();
            };
            flyout.Items.Add(duplicateItem);

            var histItem = new MenuFlyoutItem { Text = "⏱️ Time machine & history" };
            histItem.Click += (s, e) => OpenVersionHistoryForNode(node);
            flyout.Items.Add(histItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem { Text = "🗑️ Delete node" };
            // Deleting whichever node happened to be selected rather than the one right-clicked was
            // a genuine data-loss hazard.
            deleteItem.Click += (s, e) => ViewModel.DeleteNode(node);
            flyout.Items.Add(deleteItem);

            return flyout;
        }

        private MenuFlyout BuildLinkContextMenu(MindMapLinkViewModel link)
        {
            var flyout = new MenuFlyout();

            var reverseItem = new MenuFlyoutItem { Text = "⇄ Change direction" };
            reverseItem.Click += (s, e) =>
            {
                ViewModel.SelectedLink = link;
                ViewModel.PushUndo("Change link direction");
                link.ReverseDirection();
                link.SyncToModel();
                RequestRedraw();
            };
            flyout.Items.Add(reverseItem);

            var deleteItem = new MenuFlyoutItem { Text = "🗑️ Delete relationship" };
            deleteItem.Click += (s, e) =>
            {
                ViewModel.SelectedLink = link;
                ViewModel.DeleteSelectionCommand.Execute(null);
            };
            flyout.Items.Add(deleteItem);

            return flyout;
        }

        private void OnLinkPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is MindMapLinkViewModel link)
            {
                e.Handled = true;
                GalaxyCanvas.Focus(FocusState.Programmatic);
                ViewModel.SelectedLink = link;
                RequestRedraw();
            }
        }

        private static Color ColorFromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Color.FromArgb(255, 255, 124, 77);
            string s = hex.Trim().TrimStart('#');
            try
            {
                if (s.Length == 6)
                {
                    return Color.FromArgb(255, Convert.ToByte(s[0..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16));
                }
                if (s.Length == 8)
                {
                    return Color.FromArgb(Convert.ToByte(s[0..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16), Convert.ToByte(s[6..8], 16));
                }
            }
            catch (FormatException) { /* fall through to the default accent */ }
            return Color.FromArgb(255, 255, 124, 77);
        }

        /// <summary>Black or white, whichever stays legible on the given fill. The format badge
        /// used to be hardcoded white, which vanished on the yellow and cyan branch colours.</summary>
        private static Color ReadableOn(Color background)
        {
            double luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
            return luminance > 0.6 ? Color.FromArgb(255, 17, 24, 39) : Colors.White;
        }

        // ---- Canvas pointer, pan & zoom ----

        private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            GalaxyCanvas.Focus(FocusState.Programmatic);

            var props = e.GetCurrentPoint(GalaxyCanvas).Properties;
            if (props.IsRightButtonPressed) return;

            // Clicking empty space clears the selection, which is also how you get back out of a
            // link selection without deleting anything.
            ViewModel.SelectedNode = null;
            ViewModel.SelectedLink = null;

            _isPanning = true;
            _lastPanPoint = e.GetCurrentPoint(GalaxyCanvas).Position;
            GalaxyCanvas.CapturePointer(e.Pointer);
            RequestRedraw();
        }

        private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var cur = e.GetCurrentPoint(GalaxyCanvas).Position;

            if (_draggedNode != null)
            {
                double dx = (cur.X - _dragStartPointerPoint.X) / ViewModel.ZoomLevel;
                double dy = (cur.Y - _dragStartPointerPoint.Y) / ViewModel.ZoomLevel;

                if (!_dragMovedNode && Math.Abs(dx) + Math.Abs(dy) > 2)
                {
                    // Snapshot once, at the start of the gesture: capturing per pointer-move would
                    // fill the undo stack with a hundred entries for a single drag.
                    ViewModel.PushUndo($"Move '{_draggedNode.Title}'");
                    _dragMovedNode = true;
                }

                _draggedNode.X = _dragStartNodePoint.X + dx;
                _draggedNode.Y = _dragStartNodePoint.Y + dy;
                RequestRedraw();
            }
            else if (_isPanning)
            {
                double dx = (cur.X - _lastPanPoint.X) / ViewModel.ZoomLevel;
                double dy = (cur.Y - _lastPanPoint.Y) / ViewModel.ZoomLevel;
                ViewModel.ViewportOffsetX += dx;
                ViewModel.ViewportOffsetY += dy;
                _lastPanPoint = cur;
                RequestRedraw();
            }
        }

        private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_draggedNode != null && _dragMovedNode)
            {
                _draggedNode.SyncToModel();
                ViewModel.IsDirty = true;
                ViewModel.StatusMessage = $"Moved '{_draggedNode.Title}'.";
            }

            _isPanning = false;
            _draggedNode = null;
            _dragMovedNode = false;
            GalaxyCanvas.ReleasePointerCapture(e.Pointer);
        }

        private void OnCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int delta = e.GetCurrentPoint(GalaxyCanvas).Properties.MouseWheelDelta;
            if (delta == 0) return;

            var pointerPos = e.GetCurrentPoint(GalaxyCanvas).Position;
            ZoomAbout(pointerPos, delta > 0 ? 1.12 : 1 / 1.12);
            e.Handled = true;
        }

        /// <summary>
        /// Zooms around the pointer rather than the middle of the canvas, so the thing under the
        /// cursor stays under the cursor. Zooming always about the centre made it impossible to
        /// magnify anything near the edge of a large map without chasing it with the pan.
        /// </summary>
        private void ZoomAbout(Point screenPoint, double factor)
        {
            double oldZoom = ViewModel.ZoomLevel;
            double newZoom = Math.Clamp(oldZoom * factor, 0.15, 4.0);
            if (Math.Abs(newZoom - oldZoom) < 0.0001) return;

            double worldX = ((screenPoint.X - CanvasWidth / 2.0) / oldZoom) - ViewModel.ViewportOffsetX;
            double worldY = ((screenPoint.Y - CanvasHeight / 2.0) / oldZoom) - ViewModel.ViewportOffsetY;

            ViewModel.ZoomLevel = newZoom;
            ViewModel.ViewportOffsetX = ((screenPoint.X - CanvasWidth / 2.0) / newZoom) - worldX;
            ViewModel.ViewportOffsetY = ((screenPoint.Y - CanvasHeight / 2.0) / newZoom) - worldY;
            RequestRedraw();
        }

        // ---- Keyboard ----

        private static bool IsDown(Windows.System.VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        private static bool IsCtrlDown() => IsDown(Windows.System.VirtualKey.Control);
        private static bool IsShiftDown() => IsDown(Windows.System.VirtualKey.Shift);

        private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Never steal a keystroke that belongs to a text box in the inspector.
            if (FocusManager.GetFocusedElement(this.Content.XamlRoot) is TextBox or AutoSuggestBox)
            {
                return;
            }

            bool ctrl = IsCtrlDown();

            switch (e.Key)
            {
                case Windows.System.VirtualKey.F when ctrl:
                    SearchBox.Focus(FocusState.Programmatic);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Z when ctrl:
                    ViewModel.UndoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Y when ctrl:
                    ViewModel.RedoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.S when ctrl:
                    await ViewModel.SaveAsync();
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.D when ctrl:
                    ViewModel.DuplicateSelectedNode();
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.L when ctrl:
                    await ShowConnectDialogAsync();
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Number0 when ctrl:
                    FitToWindow();
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Delete:
                case Windows.System.VirtualKey.Back:
                    ViewModel.DeleteSelectionCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Tab:
                    ViewModel.AddChildNodeCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Enter:
                    ViewModel.AddSiblingNodeCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.F3:
                    ViewModel.FocusNextMatch(!IsShiftDown());
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Escape:
                    ViewModel.SearchQuery = "";
                    SearchBox.Text = "";
                    ViewModel.SelectedTagFilter = null;
                    ViewModel.IsFocusModeEnabled = false;
                    ViewModel.HidePreviewCard();
                    e.Handled = true;
                    return;
            }
        }

        // ---- Toolbar actions ----

        private void OnZoomInClick(object sender, RoutedEventArgs e)
            => ZoomAbout(new Point(CanvasWidth / 2.0, CanvasHeight / 2.0), 1.15);

        private void OnZoomOutClick(object sender, RoutedEventArgs e)
            => ZoomAbout(new Point(CanvasWidth / 2.0, CanvasHeight / 2.0), 1 / 1.15);

        private void OnFitToWindowClick(object sender, RoutedEventArgs e) => FitToWindow();

        /// <summary>
        /// Actually fits the galaxy to the window. The button used to reset zoom to 100% and the
        /// pan to the origin, which is not the same thing at all — on any imported vault it left
        /// most of the map off-screen and looked like the button had done nothing.
        /// </summary>
        private void FitToWindow()
        {
            if (ViewModel.Nodes.Count == 0)
            {
                ViewModel.ZoomLevel = 1.0;
                ViewModel.ViewportOffsetX = 0;
                ViewModel.ViewportOffsetY = 0;
                RequestRedraw();
                return;
            }

            double minX = ViewModel.Nodes.Min(n => n.X);
            double maxX = ViewModel.Nodes.Max(n => n.X + n.Width);
            double minY = ViewModel.Nodes.Min(n => n.Y);
            double maxY = ViewModel.Nodes.Max(n => n.Y + n.Height);

            const double margin = 80;
            double worldW = Math.Max(1, maxX - minX) + margin * 2;
            double worldH = Math.Max(1, maxY - minY) + margin * 2;

            double zoom = Math.Clamp(Math.Min(CanvasWidth / worldW, CanvasHeight / worldH), 0.15, 2.0);

            ViewModel.ZoomLevel = zoom;
            ViewModel.ViewportOffsetX = -(minX + maxX) / 2.0;
            ViewModel.ViewportOffsetY = -(minY + maxY) / 2.0;
            RequestRedraw();
        }

        private void OnLayoutTreeClick(object sender, RoutedEventArgs e) => ApplyLayoutAndFit("tree");
        private void OnLayoutRadialClick(object sender, RoutedEventArgs e) => ApplyLayoutAndFit("radial");
        private void OnLayoutForceClick(object sender, RoutedEventArgs e) => ApplyLayoutAndFit("force");
        private void OnLayoutHierarchyClick(object sender, RoutedEventArgs e) => ApplyLayoutAndFit("vertical");
        private void OnLayoutClustersClick(object sender, RoutedEventArgs e) => ApplyLayoutAndFit("clusters");

        private void ApplyLayoutAndFit(string layout)
        {
            ViewModel.ApplyLayout(layout);
            FitToWindow();
        }

        private void OnToggleFocusModeClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ToggleFocusModeCommand.Execute(null);
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync();

        private async void OnSaveAsClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("MarkSmith Galaxy Map", new List<string> { ".msmap" });
            picker.SuggestedFileName = SanitizeFileName(ViewModel.Title);
            InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file != null) await ViewModel.SaveAsync(file.Path);
        }

        private async void OnImportFolderClick(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializePicker(picker);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            await ViewModel.ImportDirectoryAsync(folder.Path);
            FitToWindow();
        }

        private async void OnExportDocxClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("Word Document", new List<string> { ".docx" });
            picker.SuggestedFileName = SanitizeFileName(ViewModel.Title);
            InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file != null) ViewModel.ExportToDocx(file.Path);
        }

        private void OnCopyMermaidFlowchartClick(object sender, RoutedEventArgs e)
            => CopyMermaid(asFlowchart: true, "flowchart");

        private void OnCopyMermaidMindmapClick(object sender, RoutedEventArgs e)
            => CopyMermaid(asFlowchart: false, "mindmap");

        private void CopyMermaid(bool asFlowchart, string label)
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(ViewModel.ExportToMermaid(asFlowchart));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                ViewModel.StatusMessage = $"✓ Mermaid {label} copied — paste it straight into any MarkSmith document.";
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"⚠ Could not copy to the clipboard: {ex.Message}";
            }
        }

        private void InitializePicker(object picker)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        private static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Document Galaxy";
            var sb = new StringBuilder(name.Length);
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            foreach (char c in name.Trim())
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '-' : c);
            }
            string result = sb.ToString().Trim();
            return result.Length == 0 ? "Document Galaxy" : result;
        }

        // ---- Dialogs ----

        private async void OnConnectSelectedClick(object sender, RoutedEventArgs e) => await ShowConnectDialogAsync();

        private async Task ShowConnectDialogAsync()
        {
            if (ViewModel.SelectedNode == null)
            {
                ViewModel.StatusMessage = "Pick a source node first, then click 🔗 Link.";
                return;
            }

            var source = ViewModel.SelectedNode;
            var otherNodes = ViewModel.Nodes.Where(n => n.Id != source.Id).ToList();
            if (otherNodes.Count == 0)
            {
                ViewModel.StatusMessage = "There is nothing else to link to yet — add another document first.";
                return;
            }

            var combo = new ComboBox
            {
                Header = "Target document / node",
                ItemsSource = otherNodes,
                DisplayMemberPath = "Title",
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var labelBox = new TextBox
            {
                Header = "Why are these connected?",
                Text = "",
                PlaceholderText = "e.g. grew out of, evidence for, supersedes, argues against"
            };

            var suggestions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (string preset in new[] { "grew out of", "evidence for", "supersedes", "references" })
            {
                var chip = new Button { Content = preset, FontSize = 11, Padding = new Thickness(8, 3, 8, 3) };
                chip.Click += (s, args) => labelBox.Text = preset;
                suggestions.Children.Add(chip);
            }

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = $"From: {source.Title}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            panel.Children.Add(combo);
            panel.Children.Add(labelBox);
            panel.Children.Add(suggestions);

            var dialog = new ContentDialog
            {
                Title = "🔗 Connect two documents",
                PrimaryButtonText = "Connect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
                Content = panel
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && combo.SelectedItem is MindMapNodeViewModel target)
            {
                ViewModel.ConnectNodes(source.Id, target.Id, labelBox.Text);
                RequestRedraw();
            }
        }

        private async Task ShowReparentDialogAsync(MindMapNodeViewModel node)
        {
            var candidates = ViewModel.Nodes.Where(n => n.Id != node.Id).ToList();

            var combo = new ComboBox
            {
                Header = "New parent",
                ItemsSource = candidates,
                DisplayMemberPath = "Title",
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var dialog = new ContentDialog
            {
                Title = $"🔀 Move '{node.Title}'",
                PrimaryButtonText = "Move",
                SecondaryButtonText = "Detach (no parent)",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot,
                Content = combo
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && combo.SelectedItem is MindMapNodeViewModel parent)
            {
                ViewModel.ReparentNode(node.Id, parent.Id);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                ViewModel.ReparentNode(node.Id, null);
            }
        }

        private async void OnEditTagsClick(object sender, RoutedEventArgs e)
        {
            var node = ViewModel.SelectedNode;
            if (node == null) return;

            var box = new TextBox
            {
                Header = "Tags, separated by spaces or commas",
                Text = string.Join(" ", node.Tags),
                PlaceholderText = "#research #q3 #launch"
            };

            var dialog = new ContentDialog
            {
                Title = $"🏷️ Tags for '{node.Title}'",
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
                Content = box
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            ViewModel.PushUndo("Edit tags");
            var parsed = MindMapGraph.NormalizeTags(
                (box.Text ?? "").Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));

            node.Tags.Clear();
            foreach (string t in parsed) node.Tags.Add(t);
            node.SyncToModel();

            ViewModel.RefreshDistinctTags();
            ViewModel.ApplyFilterAndSearch();
            ViewModel.IsDirty = true;
            RequestRedraw();
        }

        private async void OnBrowseForFileClick(object sender, RoutedEventArgs e)
        {
            var node = ViewModel.SelectedNode;
            if (node == null) return;

            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            foreach (string ext in new[] { ".md", ".markdown", ".txt", ".docx", ".pdf", ".pptx", ".epub" })
            {
                picker.FileTypeFilter.Add(ext);
            }
            InitializePicker(picker);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            ViewModel.PushUndo("Attach file to node");
            node.FilePath = file.Path;
            if (string.IsNullOrWhiteSpace(node.Title) || node.Title.StartsWith("New ", StringComparison.Ordinal))
            {
                node.Title = System.IO.Path.GetFileNameWithoutExtension(file.Path);
            }
            node.SyncToModel();
            ViewModel.IsDirty = true;
            RequestRedraw();
        }

        private async void OnShowInsightsClick(object sender, RoutedEventArgs e)
        {
            var insights = ViewModel.GetInsights();
            var sb = new StringBuilder();

            sb.AppendLine($"Documents & nodes:  {insights.NodeCount}");
            sb.AppendLine($"Linked to real files:  {insights.LinkedFileCount}");
            sb.AppendLine($"Named relationships:  {insights.LinkCount}");
            sb.AppendLine($"Hierarchy edges:  {insights.HierarchyEdgeCount}");
            sb.AppendLine($"Separate clusters:  {insights.ClusterCount} (largest holds {insights.LargestClusterSize})");
            sb.AppendLine($"Connections per document:  {insights.Density}");
            if (insights.TotalWordCount > 0) sb.AppendLine($"Words across the vault:  {insights.TotalWordCount:N0}");

            if (insights.Hubs.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Busiest documents");
                foreach (var (_, title, degree) in insights.Hubs)
                {
                    sb.AppendLine($"   • {title} — {degree} connections");
                }
            }

            if (insights.FormatBreakdown.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Formats");
                foreach (var kvp in insights.FormatBreakdown)
                {
                    sb.AppendLine($"   • {kvp.Key.ToUpperInvariant()} — {kvp.Value}");
                }
            }

            if (insights.TopTags.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Most-used tags");
                foreach (var (tag, count) in insights.TopTags)
                {
                    sb.AppendLine($"   • {tag} — {count}");
                }
            }

            if (insights.IsolatedNodeIds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"{insights.IsolatedNodeIds.Count} document(s) are not connected to anything yet — link them and they stop being lost.");
            }

            var dialog = new ContentDialog
            {
                Title = "📊 Galaxy topology",
                CloseButtonText = "Close",
                XamlRoot = this.Content.XamlRoot,
                Content = new ScrollViewer
                {
                    MaxHeight = 460,
                    Content = new TextBlock
                    {
                        Text = sb.ToString(),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            };

            await dialog.ShowAsync();
        }

        // ---- Inspector & filters ----

        private void OnPaletteSwatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string hex)
            {
                ViewModel.RecolorSelectedNode(hex);
            }
        }

        private void OnReverseLinkClick(object sender, RoutedEventArgs e)
        {
            var link = ViewModel.SelectedLink;
            if (link == null) return;

            ViewModel.PushUndo("Change link direction");
            link.ReverseDirection();
            link.SyncToModel();
            ViewModel.IsDirty = true;
            RequestRedraw();
        }

        /// <summary>
        /// Themes now recolour the cards and text, not just the backdrop. "Clean White" used to
        /// swap the canvas to near-white while leaving every card dark-on-dark with white text,
        /// which rendered the whole map unreadable.
        /// </summary>
        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            _palette = GalaxyPalette.ForName(ViewModel.SelectedThemeName);
            CanvasContainer.Background = new SolidColorBrush(_palette.Background);
            MinimapOverlay.Background = new SolidColorBrush(_palette.MinimapBackground);
            RequestRedraw();
        }

        private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            // Only filter as you type. Jumping the camera to the first hit on every keystroke, as
            // this used to, threw the viewport around while you were still typing the word.
            ViewModel.SearchQuery = sender.Text?.Trim() ?? "";
        }

        private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            ViewModel.SearchQuery = sender.Text?.Trim() ?? "";
            ViewModel.FocusNextMatch();
        }

        private void OnTagPillClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                ViewModel.SelectedTagFilter = (ViewModel.SelectedTagFilter == tag) ? null : tag;
            }
        }

        private void OnAllTagsClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SelectedTagFilter = null;
        }

        private void OnOpenSelectedDocumentClick(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenLinkedDocument(ViewModel.SelectedNode);
        }

        private void OnOpenVersionHistoryClick(object sender, RoutedEventArgs e)
        {
            OpenVersionHistoryForNode(ViewModel.SelectedNode);
        }

        private void OnPreviewVersionHistoryClick(object sender, RoutedEventArgs e)
        {
            var path = !string.IsNullOrWhiteSpace(ViewModel.PreviewFilePath)
                ? ViewModel.PreviewFilePath
                : (ViewModel.SelectedNode?.FilePath ?? ViewModel.SelectedNode?.Title);
            if (!string.IsNullOrWhiteSpace(path))
            {
                var historyWin = new History.HistoryWindow(initialFilePath: path);
                historyWin.Activate();
            }
        }

        private void OpenVersionHistoryForNode(MindMapNodeViewModel? node)
        {
            if (node == null) return;
            var path = !string.IsNullOrWhiteSpace(node.FilePath) ? node.FilePath : node.Title;
            var historyWin = new History.HistoryWindow(initialFilePath: path);
            historyWin.Activate();
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

            double padding = 250;
            minX -= padding; maxX += padding;
            minY -= padding; maxY += padding;

            double worldW = Math.Max(100, maxX - minX);
            double worldH = Math.Max(100, maxY - minY);

            const double miniW = 168.0;
            const double miniH = 108.0;
            double scale = Math.Min(miniW / worldW, miniH / worldH);

            _minimapMinX = minX;
            _minimapMinY = minY;
            _minimapScale = scale;

            var byId = new Dictionary<string, MindMapNodeViewModel>(StringComparer.Ordinal);
            foreach (var n in ViewModel.Nodes) byId[n.Id] = n;

            // Edges first: without them the radar is a field of unrelated dots and tells you
            // nothing about where the clusters are.
            foreach (var link in ViewModel.Links)
            {
                if (!byId.TryGetValue(link.SourceNodeId, out var s) || !byId.TryGetValue(link.TargetNodeId, out var t)) continue;
                MinimapCanvas.Children.Add(new Line
                {
                    X1 = (s.X + s.Width / 2.0 - minX) * scale,
                    Y1 = (s.Y + s.Height / 2.0 - minY) * scale,
                    X2 = (t.X + t.Width / 2.0 - minX) * scale,
                    Y2 = (t.Y + t.Height / 2.0 - minY) * scale,
                    Stroke = new SolidColorBrush(ColorFromHex("#3A3A55")),
                    StrokeThickness = 0.6,
                    IsHitTestVisible = false
                });
            }

            foreach (var node in ViewModel.Nodes)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = Math.Max(3, node.Width * scale),
                    Height = Math.Max(2, node.Height * scale),
                    Fill = new SolidColorBrush(node.IsSelected ? Colors.White : ColorFromHex(node.ColorHex)),
                    // Dimmed nodes fade on the radar too, so a tag filter or focus shows up here.
                    Opacity = node.IsDimmed ? 0.2 : 1.0,
                    RadiusX = 1,
                    RadiusY = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, (node.X - minX) * scale);
                Canvas.SetTop(dot, (node.Y - minY) * scale);
                MinimapCanvas.Children.Add(dot);
            }

            // Viewport camera lens
            double zoom = ViewModel.ZoomLevel;
            double viewWorldLeft = -ViewModel.ViewportOffsetX - (CanvasWidth / 2.0 / zoom);
            double viewWorldTop = -ViewModel.ViewportOffsetY - (CanvasHeight / 2.0 / zoom);

            var lens = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = Math.Max(6, (CanvasWidth / zoom) * scale),
                Height = Math.Max(4, (CanvasHeight / zoom) * scale),
                Stroke = new SolidColorBrush(ColorFromHex("#7C4DFF")),
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(40, 124, 77, 255)),
                RadiusX = 2,
                RadiusY = 2,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(lens, (viewWorldLeft - minX) * scale);
            Canvas.SetTop(lens, (viewWorldTop - minY) * scale);
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
            ViewModel.ViewportOffsetX = -(_minimapMinX + (pt.X / _minimapScale));
            ViewModel.ViewportOffsetY = -(_minimapMinY + (pt.Y / _minimapScale));
            RequestRedraw();
        }

        #endregion

        /// <summary>
        /// The colours a theme actually has to change. Previously "theme" only repainted the canvas
        /// background, so every palette but the dark one produced unreadable cards.
        /// </summary>
        private sealed record GalaxyPalette(
            Color Background,
            Color CardBackground,
            Color Text,
            Color Muted,
            string HierarchyLine,
            Color MinimapBackground)
        {
            public static readonly GalaxyPalette MidnightGalaxy = new(
                ColorFromHex("#12131C"), ColorFromHex("#1C1C28"), ColorFromHex("#F1F1F8"),
                ColorFromHex("#9A9AB0"), "#5A6478", ColorFromHex("#E0161722"));

            public static GalaxyPalette ForName(string? name) => name switch
            {
                "Clean White" => new GalaxyPalette(
                    ColorFromHex("#F8FAFC"), ColorFromHex("#FFFFFF"), ColorFromHex("#111827"),
                    ColorFromHex("#64748B"), "#94A3B8", ColorFromHex("#E0F1F5FA")),
                "Nordic Slate" => new GalaxyPalette(
                    ColorFromHex("#1E293B"), ColorFromHex("#273549"), ColorFromHex("#E2E8F0"),
                    ColorFromHex("#94A3B8"), "#64748B", ColorFromHex("#E0172033")),
                "Obsidian Dark" => new GalaxyPalette(
                    ColorFromHex("#0B0C10"), ColorFromHex("#15171F"), ColorFromHex("#E8E8F0"),
                    ColorFromHex("#8A8AA0"), "#4A5266", ColorFromHex("#E00B0C10")),
                "Cyberpunk Neon" => new GalaxyPalette(
                    ColorFromHex("#0A0118"), ColorFromHex("#180A2E"), ColorFromHex("#F0E7FF"),
                    ColorFromHex("#A78BFA"), "#7C4DFF", ColorFromHex("#E0140A28")),
                _ => MidnightGalaxy
            };
        }
    }
}
