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

        /// <summary>
        /// How much of the scene a redraw request actually needs to touch. The canvas used to
        /// clear and rebuild every card, connector and flyout on every request — roughly 1,700
        /// object allocations and a full layout pass for a 20-node map — and a request is raised
        /// on every pointer move while panning or dragging. Scoping the work is what keeps the UI
        /// thread free: a pan now moves one transform and allocates nothing.
        /// </summary>
        [Flags]
        private enum RedrawScope
        {
            None = 0,
            Transform = 1,
            Geometry = 2,
            Appearance = 4,
            Scene = 8,
            All = Transform | Geometry | Appearance | Scene
        }

        private RedrawScope _pendingScope = RedrawScope.None;

        private void RequestRedraw() => RequestRedraw(RedrawScope.All);

        private void RequestRedraw(RedrawScope scope)
        {
            _pendingScope |= scope;

            if (_redrawQueued) return;
            _redrawQueued = true;

            var queue = this.DispatcherQueue;
            if (queue == null)
            {
                _redrawQueued = false;
                FlushRedraw();
                return;
            }

            queue.TryEnqueue(() =>
            {
                _redrawQueued = false;
                FlushRedraw();
            });
        }

        private void FlushRedraw()
        {
            var scope = _pendingScope;
            _pendingScope = RedrawScope.None;
            if (scope == RedrawScope.None) return;

            if (scope.HasFlag(RedrawScope.Scene)) SyncScene();
            if (scope.HasFlag(RedrawScope.Appearance)) UpdateAppearance();
            if (scope.HasFlag(RedrawScope.Geometry)) UpdateGeometry();
            if (scope.HasFlag(RedrawScope.Transform)) UpdateTransform();

            if (scope.HasFlag(RedrawScope.Scene) || scope.HasFlag(RedrawScope.Geometry))
            {
                RedrawMinimapContent();
            }
            UpdateMinimapLens();
        }

        /// <summary>Kept for callers that just want "something changed, redraw".</summary>
        public void RedrawCanvas()
        {
            _pendingScope = RedrawScope.All;
            FlushRedraw();
        }

        private void OnCanvasContainerSizeChanged(object sender, SizeChangedEventArgs e)
            => RequestRedraw(RedrawScope.Transform);

        // ---- Retained scene ----

        private sealed class NodeVisual
        {
            public Border Root = null!;
            public Grid Detail = null!;
            public TextBlock Icon = null!;
            public TextBlock Title = null!;
            public Border BadgeHost = null!;
            public TextBlock BadgeText = null!;
            public StackPanel Bottom = null!;
            public Border ProgressTrack = null!;
            public Border ProgressFill = null!;
            public TextBlock ProgressText = null!;
            public TextBlock TagText = null!;
            public TextBlock ConnectionText = null!;
            public Border VersionHost = null!;
            public TextBlock VersionText = null!;
            public TextBlock MissingMark = null!;

            public SolidColorBrush CardFill = null!;
            public SolidColorBrush BorderStroke = null!;
            public SolidColorBrush TitleInk = null!;
            public SolidColorBrush BadgeFill = null!;
            public SolidColorBrush BadgeInk = null!;
            public SolidColorBrush MutedInk = null!;
            public SolidColorBrush ConnectionInk = null!;
        }

        private sealed class EdgeVisual
        {
            public Microsoft.UI.Xaml.Shapes.Path Line = null!;
            public PathFigure Figure = null!;
            public BezierSegment Segment = null!;
            public SolidColorBrush Stroke = null!;

            public Microsoft.UI.Xaml.Shapes.Path? Hit;
            public PathFigure? HitFigure;
            public BezierSegment? HitSegment;

            public Polygon? ArrowForward;
            public Polygon? ArrowBackward;
            public SolidColorBrush? ArrowFill;

            public Border? Label;
            public TextBlock? LabelText;
            /// <summary>Cached so a drag repositions the pill without re-measuring text.</summary>
            public Size LabelSize;

            /// <summary>Dash state currently applied, so the collection is only rebuilt when it
            /// genuinely flips rather than on every appearance pass.</summary>
            public bool Dashed;

            public MindMapLinkViewModel? Link;   // null for hierarchy edges
            public string SourceId = "";
            public string TargetId = "";
            public bool FromNodeEdge;            // hierarchy edges leave the parent's right edge
        }

        private readonly Dictionary<string, NodeVisual> _nodeVisuals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EdgeVisual> _edgeVisuals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MindMapNodeViewModel> _nodeIndex = new(StringComparer.Ordinal);

        private const double WorldHitStrokeScreenWidth = 18.0;
        private const double DetailLodZoom = 0.4;
        private const double LabelLodZoom = 0.55;

        private static string HierarchyKey(string childId) => "h:" + childId;

        /// <summary>
        /// Reconciles the retained visuals with the view model's collections. Only genuinely new
        /// or removed nodes and links cost anything; a redraw of an unchanged scene allocates
        /// nothing at all.
        /// </summary>
        private void SyncScene()
        {
            _nodeIndex.Clear();
            foreach (var n in ViewModel.Nodes) _nodeIndex[n.Id] = n;

            // Nodes
            foreach (var id in _nodeVisuals.Keys.Where(k => !_nodeIndex.ContainsKey(k)).ToList())
            {
                NodeLayer.Children.Remove(_nodeVisuals[id].Root);
                _nodeVisuals.Remove(id);
            }
            foreach (var node in ViewModel.Nodes)
            {
                if (_nodeVisuals.ContainsKey(node.Id)) continue;
                var visual = BuildNodeVisual(node);
                _nodeVisuals[node.Id] = visual;
                NodeLayer.Children.Add(visual.Root);
            }

            // Edges: hierarchy first, then cross-links.
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in ViewModel.Nodes)
            {
                if (node.ParentId != null && _nodeIndex.ContainsKey(node.ParentId))
                {
                    wanted.Add(HierarchyKey(node.Id));
                }
            }
            foreach (var link in ViewModel.Links)
            {
                if (_nodeIndex.ContainsKey(link.SourceNodeId) && _nodeIndex.ContainsKey(link.TargetNodeId))
                {
                    wanted.Add(link.Id);
                }
            }

            foreach (var key in _edgeVisuals.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                RemoveEdgeVisual(_edgeVisuals[key]);
                _edgeVisuals.Remove(key);
            }

            foreach (var node in ViewModel.Nodes)
            {
                if (node.ParentId == null || !_nodeIndex.ContainsKey(node.ParentId)) continue;
                string key = HierarchyKey(node.Id);
                if (_edgeVisuals.TryGetValue(key, out var existing))
                {
                    existing.SourceId = node.ParentId;
                    existing.TargetId = node.Id;
                    continue;
                }
                _edgeVisuals[key] = BuildHierarchyEdge(node.ParentId, node.Id);
            }

            foreach (var link in ViewModel.Links)
            {
                if (!_nodeIndex.ContainsKey(link.SourceNodeId) || !_nodeIndex.ContainsKey(link.TargetNodeId)) continue;
                if (_edgeVisuals.TryGetValue(link.Id, out var existing))
                {
                    existing.Link = link;
                    existing.SourceId = link.SourceNodeId;
                    existing.TargetId = link.TargetNodeId;
                    continue;
                }
                _edgeVisuals[link.Id] = BuildLinkEdge(link);
            }
        }

        private void RemoveEdgeVisual(EdgeVisual e)
        {
            EdgeLayer.Children.Remove(e.Line);
            if (e.Hit != null) EdgeLayer.Children.Remove(e.Hit);
            if (e.ArrowForward != null) EdgeLayer.Children.Remove(e.ArrowForward);
            if (e.ArrowBackward != null) EdgeLayer.Children.Remove(e.ArrowBackward);
            if (e.Label != null) EdgeLayer.Children.Remove(e.Label);
        }

        // ---- Building (runs once per node/link, not once per frame) ----

        private NodeVisual BuildNodeVisual(MindMapNodeViewModel node)
        {
            var v = new NodeVisual
            {
                CardFill = new SolidColorBrush(_palette.CardBackground),
                BorderStroke = new SolidColorBrush(ColorFromHex(node.ColorHex)),
                TitleInk = new SolidColorBrush(_palette.Text),
                BadgeFill = new SolidColorBrush(ColorFromHex(node.ColorHex)),
                BadgeInk = new SolidColorBrush(Colors.White),
                MutedInk = new SolidColorBrush(_palette.Muted),
                ConnectionInk = new SolidColorBrush(_palette.Muted)
            };

            v.Root = new Border
            {
                Background = v.CardFill,
                BorderBrush = v.BorderStroke,
                BorderThickness = new Thickness(1.4),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 5, 8, 5),
                Tag = node
            };

            v.Detail = new Grid();
            v.Detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            v.Detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topPanel = new Grid();
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            v.Icon = new TextBlock { FontSize = 13, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(v.Icon, 0);
            topPanel.Children.Add(v.Icon);

            v.Title = new TextBlock
            {
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = v.TitleInk,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(v.Title, 1);
            topPanel.Children.Add(v.Title);

            v.BadgeText = new TextBlock { FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = v.BadgeInk };
            v.BadgeHost = new Border
            {
                Background = v.BadgeFill,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = v.BadgeText
            };
            Grid.SetColumn(v.BadgeHost, 2);
            topPanel.Children.Add(v.BadgeHost);

            Grid.SetRow(topPanel, 0);
            v.Detail.Children.Add(topPanel);

            v.Bottom = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 3, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            };

            v.ProgressFill = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(ColorFromHex("#34D399")),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            v.ProgressTrack = new Border
            {
                Width = 46,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(ColorFromHex("#33FFFFFF")),
                VerticalAlignment = VerticalAlignment.Center,
                Child = v.ProgressFill
            };
            v.Bottom.Children.Add(v.ProgressTrack);

            v.ProgressText = new TextBlock
            {
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(ColorFromHex("#34D399")),
                VerticalAlignment = VerticalAlignment.Center
            };
            v.Bottom.Children.Add(v.ProgressText);

            v.TagText = new TextBlock { FontSize = 9, Foreground = v.MutedInk, VerticalAlignment = VerticalAlignment.Center };
            v.Bottom.Children.Add(v.TagText);

            v.ConnectionText = new TextBlock { FontSize = 9, Foreground = v.ConnectionInk, VerticalAlignment = VerticalAlignment.Center };
            v.Bottom.Children.Add(v.ConnectionText);

            v.VersionText = new TextBlock
            {
                FontSize = 8.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFromHex("#38BDF8"))
            };
            v.VersionHost = new Border
            {
                Background = new SolidColorBrush(ColorFromHex("#1C2D42")),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Child = v.VersionText
            };
            v.Bottom.Children.Add(v.VersionHost);

            v.MissingMark = new TextBlock
            {
                Text = "⚠",
                FontSize = 10,
                Foreground = new SolidColorBrush(ColorFromHex("#E11D48")),
                VerticalAlignment = VerticalAlignment.Center
            };
            v.Bottom.Children.Add(v.MissingMark);

            Grid.SetRow(v.Bottom, 1);
            v.Detail.Children.Add(v.Bottom);

            v.Root.Child = v.Detail;

            // Built once, for the life of the visual. This used to be reconstructed — flyout,
            // menu items and their closures — for every node on every frame.
            v.Root.ContextFlyout = BuildNodeContextMenu(node);
            AttachNodeInteractions(v.Root, node);

            return v;
        }

        private EdgeVisual BuildHierarchyEdge(string parentId, string childId)
        {
            var e = new EdgeVisual
            {
                SourceId = parentId,
                TargetId = childId,
                FromNodeEdge = true,
                Stroke = new SolidColorBrush(ColorFromHex(_palette.HierarchyLine))
            };
            (e.Line, e.Figure, e.Segment) = BuildBezierPath(e.Stroke, 1.8, dashed: false);
            e.Line.Opacity = 0.75;
            EdgeLayer.Children.Add(e.Line);
            return e;
        }

        private EdgeVisual BuildLinkEdge(MindMapLinkViewModel link)
        {
            var e = new EdgeVisual
            {
                Link = link,
                SourceId = link.SourceNodeId,
                TargetId = link.TargetNodeId,
                FromNodeEdge = false,
                Stroke = new SolidColorBrush(ColorFromHex(link.ColorHex))
            };

            (e.Line, e.Figure, e.Segment) = BuildBezierPath(e.Stroke, 2.6, dashed: true);
            e.Dashed = true;
            EdgeLayer.Children.Add(e.Line);

            // A generous transparent stroke makes the line clickable.
            var (hit, hitFigure, hitSegment) = BuildBezierPath(new SolidColorBrush(Colors.Transparent), WorldHitStrokeScreenWidth, dashed: false);
            hit.IsHitTestVisible = true;
            hit.Tag = link;
            hit.PointerPressed += OnLinkPointerPressed;
            hit.ContextFlyout = BuildLinkContextMenu(link);
            e.Hit = hit;
            e.HitFigure = hitFigure;
            e.HitSegment = hitSegment;
            EdgeLayer.Children.Add(hit);

            e.ArrowFill = new SolidColorBrush(ColorFromHex(link.ColorHex));
            e.ArrowForward = new Polygon { Fill = e.ArrowFill, IsHitTestVisible = false };
            e.ArrowForward.Points.Add(new Point());
            e.ArrowForward.Points.Add(new Point());
            e.ArrowForward.Points.Add(new Point());
            EdgeLayer.Children.Add(e.ArrowForward);

            e.ArrowBackward = new Polygon { Fill = e.ArrowFill, IsHitTestVisible = false };
            e.ArrowBackward.Points.Add(new Point());
            e.ArrowBackward.Points.Add(new Point());
            e.ArrowBackward.Points.Add(new Point());
            EdgeLayer.Children.Add(e.ArrowBackward);

            e.LabelText = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(_palette.Text) };
            e.Label = new Border
            {
                Background = new SolidColorBrush(_palette.CardBackground),
                BorderBrush = new SolidColorBrush(ColorFromHex(link.ColorHex)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                IsHitTestVisible = false,
                Child = e.LabelText
            };
            EdgeLayer.Children.Add(e.Label);

            return e;
        }

        private static (Microsoft.UI.Xaml.Shapes.Path Path, PathFigure Figure, BezierSegment Segment) BuildBezierPath(
            Brush stroke, double thickness, bool dashed)
        {
            var segment = new BezierSegment();
            var figure = new PathFigure { IsClosed = false };
            figure.Segments.Add(segment);

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            var path = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            if (dashed)
            {
                path.StrokeDashCap = PenLineCap.Round;
                path.StrokeDashArray = new DoubleCollection { 3, 2.5 };
            }
            return (path, figure, segment);
        }

        // ---- Per-frame updates (no allocation) ----

        /// <summary>Positions in world space. Called while dragging a node; the transform handles
        /// pan and zoom, so nothing here depends on the camera.</summary>
        private void UpdateGeometry()
        {
            foreach (var (id, v) in _nodeVisuals)
            {
                if (!_nodeIndex.TryGetValue(id, out var node)) continue;
                v.Root.Width = Math.Max(24, node.Width);
                v.Root.Height = Math.Max(18, node.Height);
                Canvas.SetLeft(v.Root, node.X);
                Canvas.SetTop(v.Root, node.Y);
            }

            foreach (var e in _edgeVisuals.Values)
            {
                if (!_nodeIndex.TryGetValue(e.SourceId, out var src) || !_nodeIndex.TryGetValue(e.TargetId, out var tgt)) continue;

                Point start, end;
                if (e.FromNodeEdge)
                {
                    start = new Point(src.X + src.Width, src.Y + (src.Height / 2.0));
                    end = new Point(tgt.X, tgt.Y + (tgt.Height / 2.0));
                }
                else
                {
                    start = new Point(src.X + (src.Width / 2.0), src.Y + (src.Height / 2.0));
                    end = new Point(tgt.X + (tgt.Width / 2.0), tgt.Y + (tgt.Height / 2.0));
                }

                double ctrl = Math.Max(Math.Abs(end.X - start.X) * 0.5, 40);
                var c1 = new Point(start.X + ctrl, start.Y);
                var c2 = new Point(end.X - ctrl, end.Y);

                e.Figure.StartPoint = start;
                e.Segment.Point1 = c1;
                e.Segment.Point2 = c2;
                e.Segment.Point3 = end;

                if (e.HitFigure != null && e.HitSegment != null)
                {
                    e.HitFigure.StartPoint = start;
                    e.HitSegment.Point1 = c1;
                    e.HitSegment.Point2 = c2;
                    e.HitSegment.Point3 = end;
                }

                if (e.Link != null)
                {
                    var dir = e.Link.Direction;
                    SetArrow(e.ArrowForward, start, end, dir is MindMapLinkDirection.SourceToTarget or MindMapLinkDirection.Bidirectional);
                    SetArrow(e.ArrowBackward, end, start, dir is MindMapLinkDirection.TargetToSource or MindMapLinkDirection.Bidirectional);

                    if (e.Label != null)
                    {
                        // Uses the size measured when the text last changed: measuring all of them
                        // on every frame of a drag is exactly the kind of work this rewrite removes.
                        Canvas.SetLeft(e.Label, ((start.X + end.X) / 2.0) - (e.LabelSize.Width / 2.0));
                        Canvas.SetTop(e.Label, ((start.Y + end.Y) / 2.0) - (e.LabelSize.Height / 2.0));
                    }
                }
            }
        }

        private static void SetArrow(Polygon? arrow, Point from, Point to, bool visible)
        {
            if (arrow == null) return;
            if (!visible)
            {
                arrow.Visibility = Visibility.Collapsed;
                return;
            }
            arrow.Visibility = Visibility.Visible;

            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001) { dx = 1; dy = 0; len = 1; }
            dx /= len;
            dy /= len;

            // Back the head off the card edge so it points at the node rather than sitting under it.
            const double inset = 26;
            const double size = 9;
            var tip = new Point(to.X - dx * inset, to.Y - dy * inset);

            arrow.Points[0] = tip;
            arrow.Points[1] = new Point(tip.X - dx * size - dy * (size * 0.5), tip.Y - dy * size + dx * (size * 0.5));
            arrow.Points[2] = new Point(tip.X - dx * size + dy * (size * 0.5), tip.Y - dy * size - dx * (size * 0.5));
        }

        /// <summary>Colours, text and emphasis. Runs on selection and filter changes, not on pan.</summary>
        private void UpdateAppearance()
        {
            foreach (var (id, v) in _nodeVisuals)
            {
                if (!_nodeIndex.TryGetValue(id, out var node)) continue;

                var accent = ColorFromHex(node.ColorHex);

                Color borderColor;
                double borderThickness;
                if (node.IsSelected) { borderColor = Colors.White; borderThickness = 2.6; }
                else if (node.IsHighlighted) { borderColor = ColorFromHex("#22D3EE"); borderThickness = 2.4; }
                else if (node.IsNeighbor) { borderColor = accent; borderThickness = 2.2; }
                else { borderColor = accent; borderThickness = node.IsHub ? 2.2 : 1.4; }

                v.CardFill.Color = _palette.CardBackground;
                v.BorderStroke.Color = borderColor;
                v.Root.BorderThickness = new Thickness(borderThickness);
                v.Root.Opacity = node.IsDimmed ? 0.22 : 1.0;

                v.TitleInk.Color = _palette.Text;
                v.MutedInk.Color = _palette.Muted;
                v.BadgeFill.Color = accent;
                v.BadgeInk.Color = ReadableOn(accent);

                v.Icon.Text = node.Icon ?? "📄";
                v.Title.Text = node.Title;
                v.BadgeText.Text = node.FormatBadge;

                bool hasProgress = node.Progress > 0;
                v.ProgressTrack.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
                v.ProgressText.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
                if (hasProgress)
                {
                    v.ProgressFill.Width = Math.Max(1, v.ProgressTrack.Width * (node.Progress / 100.0));
                    v.ProgressText.Text = node.ProgressText;
                }

                bool hasTag = node.Tags.Count > 0;
                v.TagText.Visibility = hasTag ? Visibility.Visible : Visibility.Collapsed;
                if (hasTag) v.TagText.Text = node.Tags[0];

                bool hasConnections = node.ConnectionCount > 0;
                v.ConnectionText.Visibility = hasConnections ? Visibility.Visible : Visibility.Collapsed;
                if (hasConnections)
                {
                    v.ConnectionText.Text = node.IsHub ? $"🔗 {node.ConnectionCount} hub" : $"🔗 {node.ConnectionCount}";
                    v.ConnectionText.FontWeight = node.IsHub ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
                    v.ConnectionInk.Color = node.IsHub ? ColorFromHex("#FBBF24") : _palette.Muted;
                }

                v.VersionHost.Visibility = node.HasVersions ? Visibility.Visible : Visibility.Collapsed;
                if (node.HasVersions) v.VersionText.Text = $"⏱️ {node.VersionCount}";

                v.MissingMark.Visibility = node.IsFileMissing ? Visibility.Visible : Visibility.Collapsed;

                v.Bottom.Visibility = (hasProgress || hasTag || hasConnections || node.HasVersions || node.IsFileMissing)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            foreach (var e in _edgeVisuals.Values)
            {
                bool faded = _nodeIndex.TryGetValue(e.SourceId, out var s) && _nodeIndex.TryGetValue(e.TargetId, out var t)
                             && s.IsDimmed && t.IsDimmed;

                if (e.Link == null)
                {
                    e.Stroke.Color = ColorFromHex(_palette.HierarchyLine);
                    e.Line.Opacity = faded ? 0.18 : 0.75;
                    continue;
                }

                var link = e.Link;
                e.Stroke.Color = link.IsSelected ? Colors.White : ColorFromHex(link.ColorHex);
                e.Line.StrokeThickness = link.IsSelected ? 3.6 : (link.IsInferred ? 1.6 : 2.6);
                e.Line.Opacity = faded ? 0.15 : (link.IsInferred ? 0.62 : 0.95);
                bool wantDashed = link.Style == MindMapLinkStyle.Dashed || link.IsInferred;
                if (wantDashed != e.Dashed)
                {
                    e.Dashed = wantDashed;
                    // An empty collection is a solid stroke; assigning null would be a nullable
                    // warning for no benefit.
                    e.Line.StrokeDashArray = wantDashed ? new DoubleCollection { 3, 2.5 } : new DoubleCollection();
                }

                if (e.ArrowFill != null) e.ArrowFill.Color = e.Stroke.Color;

                if (e.Label != null && e.LabelText != null)
                {
                    string text = link.DisplayLabel;
                    bool textChanged = e.LabelText.Text != text;
                    e.LabelText.Text = text;
                    e.LabelText.FontStyle = link.IsInferred ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
                    ((SolidColorBrush)e.LabelText.Foreground).Color = _palette.Text;
                    ((SolidColorBrush)e.Label.Background).Color = _palette.CardBackground;
                    ((SolidColorBrush)e.Label.BorderBrush).Color = ColorFromHex(link.ColorHex);
                    e.Label.Opacity = link.IsInferred ? 0.8 : 1.0;

                    if (textChanged || e.LabelSize.Width <= 0)
                    {
                        e.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        e.LabelSize = e.Label.DesiredSize;
                    }
                }

                bool hide = faded;
                e.Line.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
                if (e.Hit != null) e.Hit.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            }

            ApplyLevelOfDetail();
        }

        /// <summary>
        /// The camera. Panning and zooming are one transform write — this is the whole reason the
        /// scene is retained rather than rebuilt.
        /// </summary>
        private void UpdateTransform()
        {
            double zoom = ViewModel.ZoomLevel;
            WorldTransform.ScaleX = zoom;
            WorldTransform.ScaleY = zoom;
            WorldTransform.TranslateX = (CanvasWidth / 2.0) + (ViewModel.ViewportOffsetX * zoom);
            WorldTransform.TranslateY = (CanvasHeight / 2.0) + (ViewModel.ViewportOffsetY * zoom);

            ApplyLevelOfDetail();
        }

        /// <summary>
        /// Keeps the click target for a connector a constant size on screen, and drops text when it
        /// would be too small to read anyway.
        /// </summary>
        private void ApplyLevelOfDetail()
        {
            double zoom = Math.Max(ViewModel.ZoomLevel, 0.01);
            var detailVisibility = zoom < DetailLodZoom ? Visibility.Collapsed : Visibility.Visible;
            var labelVisibility = zoom < LabelLodZoom ? Visibility.Collapsed : Visibility.Visible;

            foreach (var (id, v) in _nodeVisuals)
            {
                if (v.Detail.Visibility != detailVisibility) v.Detail.Visibility = detailVisibility;

                // Far out, a card is a few pixels tall; the accent chip alone still reads as a star.
                if (detailVisibility == Visibility.Collapsed)
                {
                    if (_nodeIndex.TryGetValue(id, out var node)) v.CardFill.Color = ColorFromHex(node.ColorHex);
                }
                else
                {
                    v.CardFill.Color = _palette.CardBackground;
                }
            }

            foreach (var e in _edgeVisuals.Values)
            {
                if (e.Hit != null) e.Hit.StrokeThickness = WorldHitStrokeScreenWidth / zoom;
                if (e.Label != null)
                {
                    var want = e.Line.Visibility == Visibility.Collapsed ? Visibility.Collapsed : labelVisibility;
                    if (e.Label.Visibility != want) e.Label.Visibility = want;
                }
            }
        }

        private double CanvasWidth => CanvasContainer.ActualWidth > 0 ? CanvasContainer.ActualWidth : 900;
        private double CanvasHeight => CanvasContainer.ActualHeight > 0 ? CanvasContainer.ActualHeight : 600;

        private void AttachNodeInteractions(Border border, MindMapNodeViewModel node)
        {
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

                // Capture on the canvas, not the card, so a drag survives the pointer leaving the
                // card's bounds.
                GalaxyCanvas.CapturePointer(e.Pointer);
                RequestRedraw(RedrawScope.Appearance);
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
                // The preview follows the pointer off the card unless the node is the selection.
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
                RequestRedraw(RedrawScope.Appearance);
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
            RequestRedraw(RedrawScope.Appearance);
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
                RequestRedraw(RedrawScope.Geometry);
            }
            else if (_isPanning)
            {
                double dx = (cur.X - _lastPanPoint.X) / ViewModel.ZoomLevel;
                double dy = (cur.Y - _lastPanPoint.Y) / ViewModel.ZoomLevel;
                ViewModel.ViewportOffsetX += dx;
                ViewModel.ViewportOffsetY += dy;
                _lastPanPoint = cur;
                RequestRedraw(RedrawScope.Transform);
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
            RequestRedraw(RedrawScope.Transform);
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
                RequestRedraw(RedrawScope.Transform);
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
            RequestRedraw(RedrawScope.Transform);
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

        private Microsoft.UI.Xaml.Shapes.Rectangle? _minimapLens;

        /// <summary>
        /// Rebuilds the radar's dots and edges. Only called when the scene or a position actually
        /// changed — panning just moves the lens, which is one property write.
        /// </summary>
        private void RedrawMinimapContent()
        {
            if (MinimapCanvas == null) return;
            MinimapCanvas.Children.Clear();
            _minimapLens = null;

            if (ViewModel.Nodes.Count == 0) return;

            double minX = ViewModel.Nodes.Min(n => n.X);
            double maxX = ViewModel.Nodes.Max(n => n.X + n.Width);
            double minY = ViewModel.Nodes.Min(n => n.Y);
            double maxY = ViewModel.Nodes.Max(n => n.Y + n.Height);

            const double padding = 250;
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

            // Edges first: without them the radar is a field of unrelated dots and tells you
            // nothing about where the clusters are.
            var edgeStroke = new SolidColorBrush(ColorFromHex("#3A3A55"));
            foreach (var link in ViewModel.Links)
            {
                if (!_nodeIndex.TryGetValue(link.SourceNodeId, out var s1) || !_nodeIndex.TryGetValue(link.TargetNodeId, out var t1)) continue;
                MinimapCanvas.Children.Add(new Line
                {
                    X1 = (s1.X + s1.Width / 2.0 - minX) * scale,
                    Y1 = (s1.Y + s1.Height / 2.0 - minY) * scale,
                    X2 = (t1.X + t1.Width / 2.0 - minX) * scale,
                    Y2 = (t1.Y + t1.Height / 2.0 - minY) * scale,
                    Stroke = edgeStroke,
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

            _minimapLens = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(ColorFromHex("#7C4DFF")),
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(40, 124, 77, 255)),
                RadiusX = 2,
                RadiusY = 2,
                IsHitTestVisible = false
            };
            MinimapCanvas.Children.Add(_minimapLens);
            UpdateMinimapLens();
        }

        /// <summary>Moves the viewport rectangle on the radar. This is all a pan needs to touch.</summary>
        private void UpdateMinimapLens()
        {
            if (_minimapLens == null || _minimapScale <= 0) return;

            double zoom = Math.Max(ViewModel.ZoomLevel, 0.0001);
            double viewWorldLeft = -ViewModel.ViewportOffsetX - (CanvasWidth / 2.0 / zoom);
            double viewWorldTop = -ViewModel.ViewportOffsetY - (CanvasHeight / 2.0 / zoom);

            _minimapLens.Width = Math.Max(6, (CanvasWidth / zoom) * _minimapScale);
            _minimapLens.Height = Math.Max(4, (CanvasHeight / zoom) * _minimapScale);
            Canvas.SetLeft(_minimapLens, (viewWorldLeft - _minimapMinX) * _minimapScale);
            Canvas.SetTop(_minimapLens, (viewWorldTop - _minimapMinY) * _minimapScale);
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
            RequestRedraw(RedrawScope.Transform);
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
