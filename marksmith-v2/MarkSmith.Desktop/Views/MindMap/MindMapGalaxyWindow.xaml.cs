using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using MarkSmith.Models.MindMap;
using MarkSmith.Services;
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

            // Same chrome as the other studio windows: the header doubles as the title bar, instead
            // of a stock (light on a light-mode PC) system caption strip above an always-dark galaxy.
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            TitleBarInsets.Reserve(this, AppTitleBar);

            ViewModel.CanvasRedrawRequested += (s, e) => RequestRedraw();
            ViewModel.OpenDocumentRequested += (s, path) => OpenDocumentRequested?.Invoke(this, path);
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.NodeCreated += OnNodeCreated;

            // The canvas has to be able to take focus or the window never sees a key press.
            GalaxyCanvas.IsTabStop = true;

            this.Activated += OnWindowActivated;
            this.RootGrid.KeyDown += OnRootKeyDown;
            HoverPolish.Track(this.RootGrid);
            SetCursor(MinimapCanvas, Microsoft.UI.Input.InputSystemCursorShape.Hand);

            // Closing used to throw away every unsaved change without a word.
            AppWindow.Closing += OnAppWindowClosing;
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MindMapStudioViewModel.SelectedThemeName):
                    ApplyPalette();
                    break;
                case nameof(MindMapStudioViewModel.SelectedTagFilter):
                    UpdateTagPills();
                    break;
                case nameof(MindMapStudioViewModel.SelectedNode):
                    WatchSelectedNodeColour();
                    UpdateSwatches();
                    break;
                case nameof(MindMapStudioViewModel.PreviewNode):
                case nameof(MindMapStudioViewModel.IsPreviewCardVisible):
                    PlacePreviewCard();
                    break;
            }
        }

        // ---- Preview card placement ----

        private void OnPreviewCardSizeChanged(object sender, SizeChangedEventArgs e) => PlacePreviewCard();

        /// <summary>
        /// Puts the floating preview card in a corner that leaves the previewed node, the top
        /// overlays and the legend clear (Core <see cref="PreviewCardPlacement"/>). It always sat
        /// bottom-right, so hovering a node there covered the node it was describing.
        /// </summary>
        private PreviewCardPlacement.Corner? _previewCorner;

        private void PlacePreviewCard()
        {
            if (!ViewModel.IsPreviewCardVisible || ViewModel.PreviewNode is not { } node)
            {
                _previewCorner = null;
                return;
            }
            if (!_nodeVisuals.TryGetValue(node.Id, out var visual) || visual.Root.ActualWidth <= 0) return;
            double width = CanvasContainer.ActualWidth, height = CanvasContainer.ActualHeight;
            if (width <= 0 || height <= 0) return;

            Rect BoundsOf(FrameworkElement element) =>
                element.TransformToVisual(CanvasContainer).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

            const double gap = 12;
            var topReserved = TopOverlay.ActualHeight > 0 ? BoundsOf(TopOverlay).Bottom + gap : 0;
            var bottomReserved = ConnectionLegend.Visibility == Visibility.Visible && ConnectionLegend.ActualHeight > 0
                ? height - BoundsOf(ConnectionLegend).Top + gap
                : 0;
            var nodeBounds = BoundsOf(visual.Root);
            var cardWidth = PreviewOverlayCard.Width;
            var cardHeight = PreviewOverlayCard.ActualHeight > 0 ? PreviewOverlayCard.ActualHeight : PreviewOverlayCard.MaxHeight;

            var corner = PreviewCardPlacement.Choose(
                new PreviewCardPlacement.Box(nodeBounds.X, nodeBounds.Y, nodeBounds.Width, nodeBounds.Height),
                width, height, cardWidth, cardHeight, topReserved, bottomReserved, _previewCorner);
            _previewCorner = corner;
            var box = PreviewCardPlacement.At(corner, width, height, cardWidth, cardHeight, topReserved, bottomReserved);

            PreviewOverlayCard.HorizontalAlignment = HorizontalAlignment.Left;
            PreviewOverlayCard.VerticalAlignment = VerticalAlignment.Top;
            PreviewOverlayCard.Margin = new Thickness(box.X, box.Y, 0, 0);
        }

        // ---- Closing with unsaved changes ----

        private bool _allowClose;
        private bool _confirmingClose;

        private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (_allowClose || !ViewModel.IsDirty) return;

            args.Cancel = true;
            // Only one ContentDialog can be open per window; with the map report or a link dialog
            // up, the X leaves the window open and that dialog in front, rather than failing to
            // show a second one.
            if (_confirmingClose || HoverPolish.IsContentDialogOpen(this.Content.XamlRoot)) return;
            _confirmingClose = true;
            _ = ConfirmCloseAsync();
        }

        private async Task ConfirmCloseAsync()
        {
            try
            {
                // A text box holding an edit commits it on LostFocus; take focus first so that edit
                // is part of what gets saved.
                GalaxyCanvas.Focus(FocusState.Programmatic);

                var dialog = new ContentDialog
                {
                    Title = "Save changes to the map?",
                    Content = "Your map has changes that haven't been saved. Save them before closing?",
                    PrimaryButtonText = "Save",
                    SecondaryButtonText = "Don't save",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot
                };

                var result = await HoverPolish.ShowPolishedAsync(dialog);
                if (result == ContentDialogResult.None) return;
                if (result == ContentDialogResult.Primary)
                {
                    await ViewModel.SaveAsync();
                    if (ViewModel.IsDirty) return; // the save failed; the status bar says why
                }
                _allowClose = true;
                Close();
            }
            finally
            {
                _confirmingClose = false;
            }
        }

        // ---- Naming a new node ----

        /// <summary>A new node arrives selected with its placeholder title highlighted in the
        /// inspector, so typing names it and Enter goes back to the map for the next one.</summary>
        private void OnNodeCreated(object? sender, MindMapNodeViewModel node)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (ViewModel.SelectedNode != node) return;
                TitleBox.Focus(FocusState.Programmatic);
                TitleBox.SelectAll();
            });
        }

        /// <summary>Enter commits a single-line inspector field and hands the keyboard back to the
        /// map; Esc does the same. Tab/Enter on the map then keep adding nodes.</summary>
        private void OnInspectorFieldKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                GalaxyCanvas.Focus(FocusState.Programmatic);
            }
        }

        private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (!_initialized)
            {
                _initialized = true;
                // Load the user's saved galaxy. Until this call existed the studio showed the
                // built-in sample every single time, no matter what had been saved.
                await ViewModel.InitializeAsync();
                ApplyPalette();
                UpdateTagPills();
                WatchSelectedNodeColour();
                UpdateSwatches();
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
        {
            // A Canvas draws its children wherever they are, bounds or not: link labels and cards
            // near the right edge were painted over the (translucent) inspector.
            CanvasContainer.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            RequestRedraw(RedrawScope.Transform);
        }

        // ---- Retained scene ----

        private static readonly Microsoft.UI.Xaml.Media.FontFamily IconFontFamily =
            new("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Emoji, Segoe UI");

        private static readonly Microsoft.UI.Xaml.Media.FontFamily GlyphFontFamily =
            new("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>A small "icon + number" label. The icon is its own run in the icon font: a
        /// single fallback chain would draw the digits as Segoe Fluent Icons' boxes.</summary>
        private static (TextBlock Block, Microsoft.UI.Xaml.Documents.Run Text) GlyphLabel(string glyph, double size, Brush ink)
        {
            var block = new TextBlock { FontSize = size, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = glyph, FontFamily = GlyphFontFamily, FontSize = size });
            var text = new Microsoft.UI.Xaml.Documents.Run();
            block.Inlines.Add(text);
            return (block, text);
        }

        // UIElement.ProtectedCursor is protected in WinUI 3; reflection is the usual way to set it
        // on an element from outside (same helper as the other canvases).
        private static readonly System.Reflection.PropertyInfo? ProtectedCursorProperty =
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static void SetCursor(UIElement element, Microsoft.UI.Input.InputSystemCursorShape shape)
        {
            try { ProtectedCursorProperty?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape)); }
            catch { /* cursor feedback is cosmetic */ }
        }

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
            public Microsoft.UI.Xaml.Documents.Run ConnectionCount = null!;
            public Border VersionHost = null!;
            public TextBlock VersionText = null!;
            public Microsoft.UI.Xaml.Documents.Run VersionCount = null!;
            public TextBlock MissingMark = null!;

            public ScaleTransform Lift = null!;
            public bool Lifted;

            public SolidColorBrush CardFill = null!;
            public SolidColorBrush BorderStroke = null!;
            public SolidColorBrush TitleInk = null!;
            public SolidColorBrush BadgeFill = null!;
            public SolidColorBrush BadgeInk = null!;
            public SolidColorBrush MutedInk = null!;
            public SolidColorBrush TrackFill = null!;
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
                TrackFill = new SolidColorBrush(_palette.Track),
                ConnectionInk = new SolidColorBrush(_palette.Muted)
            };

            v.Lift = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
            v.Root = new Border
            {
                Background = v.CardFill,
                BorderBrush = v.BorderStroke,
                BorderThickness = new Thickness(1.4),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 5, 8, 5),
                Tag = node,
                RenderTransform = v.Lift,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            AutomationProperties.SetName(v.Root, node.Title);
            SetCursor(v.Root, Microsoft.UI.Input.InputSystemCursorShape.Hand);

            v.Detail = new Grid();
            v.Detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            v.Detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topPanel = new Grid();
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Node icons are Segoe Fluent code points (the auto-linker and starter vault) or, in
            // older/hand-edited vaults, emoji. A plain TextBlock in the text font drew every
            // Fluent one as a missing-glyph box, so give it a per-character fallback chain.
            // Inked like the title: without its own brush the icon took the app theme's text colour
            // and vanished on Clean White's white cards.
            v.Icon = new TextBlock { FontSize = 13, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = IconFontFamily, Foreground = v.TitleInk };
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
                Background = v.TrackFill,
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

            // Card glyphs come from Segoe Fluent Icons, like every other icon in the app; these
            // three were emoji (🔗 ⏱️ ⚠), which drew in colour at a different size and baseline.
            (v.ConnectionText, v.ConnectionCount) = GlyphLabel("", 9, v.ConnectionInk);
            v.Bottom.Children.Add(v.ConnectionText);

            (v.VersionText, v.VersionCount) = GlyphLabel("", 9, new SolidColorBrush(ColorFromHex("#38BDF8")));
            v.VersionText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            v.VersionHost = new Border
            {
                Background = new SolidColorBrush(ColorFromHex("#1C2D42")),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = v.VersionText
            };
            ToolTipService.SetToolTip(v.VersionHost, "Saved versions of this document");
            v.Bottom.Children.Add(v.VersionHost);

            v.MissingMark = new TextBlock
            {
                Text = "",
                FontFamily = GlyphFontFamily,
                FontSize = 10,
                Foreground = new SolidColorBrush(ColorFromHex("#F87171")),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(v.MissingMark, "The linked file isn't there any more");
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
            var menu = BuildLinkContextMenu(link);
            MakeLinkTarget(hit, link, menu);
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
                Child = e.LabelText
            };
            // The label is the most obvious thing to click on a link, but it ignored the pointer:
            // a click or right-click on "evidence for" fell through to whatever lay beneath (run
            // #47, real mouse). It now selects, highlights and opens the menu like the line does.
            MakeLinkTarget(e.Label, link, menu);
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

                Point start, end, c1, c2;
                if (e.FromNodeEdge)
                {
                    start = new Point(src.X + src.Width, src.Y + (src.Height / 2.0));
                    end = new Point(tgt.X, tgt.Y + (tgt.Height / 2.0));
                    double ctrl = Math.Max(Math.Abs(end.X - start.X) * 0.5, 40);
                    c1 = new Point(start.X + ctrl, start.Y);
                    c2 = new Point(end.X - ctrl, end.Y);
                }
                else
                {
                    (start, c1, c2, end) = RouteCrossLink(src.X, src.Y, src.Width, src.Height,
                        tgt.X, tgt.Y, tgt.Width, tgt.Height, e.LabelSize.Width);
                }

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
                    // Heads follow the curve's tangent at each end (control point -> end point), so
                    // they point into the card border the connector actually meets.
                    SetArrow(e.ArrowForward, c2, end, dir is MindMapLinkDirection.SourceToTarget or MindMapLinkDirection.Bidirectional);
                    SetArrow(e.ArrowBackward, c1, start, dir is MindMapLinkDirection.TargetToSource or MindMapLinkDirection.Bidirectional);

                    if (e.Label != null)
                    {
                        // Uses the size measured when the text last changed: measuring all of them
                        // on every frame of a drag is exactly the kind of work this rewrite removes.
                        // Centred on the curve's own midpoint (t = 0.5), not the chord's.
                        var mid = BezierMidpoint(start, c1, c2, end);
                        Canvas.SetLeft(e.Label, mid.X - (e.LabelSize.Width / 2.0));
                        Canvas.SetTop(e.Label, mid.Y - (e.LabelSize.Height / 2.0));
                    }
                }
            }
        }

        /// <summary>
        /// Route for a manual / inferred link between two cards (rects in world space). Links used to
        /// run centre to centre with the label at the chord midpoint — between cards stacked in one
        /// column that midpoint is the narrow gap between them, so the label sat on both cards'
        /// borders and the line itself hid under the cards. Cards that share a column now get a
        /// bracket-shaped arc out past their right edges, wide enough that the label on its apex
        /// clears both cards; any other pair runs between the facing side edges.
        /// </summary>
        internal static (Point Start, Point C1, Point C2, Point End) RouteCrossLink(
            double sx, double sy, double sw, double sh,
            double tx, double ty, double tw, double th,
            double labelWidth)
        {
            double sCy = sy + sh / 2.0, tCy = ty + th / 2.0;
            double overlapX = Math.Min(sx + sw, tx + tw) - Math.Max(sx, tx);
            if (overlapX > Math.Min(sw, tw) * 0.3)
            {
                double right = Math.Max(sx + sw, tx + tw);
                // Both controls sit at right + bulge, so the apex is at right + 0.75 * bulge; size the
                // bulge so a label centred on the apex keeps 12 px clear of the cards. Longer spans
                // bulge further, so links nested in one column read as nested brackets and their
                // labels step outwards instead of stacking on top of each other.
                double bulge = Math.Max(36, (labelWidth / 2.0 + 12) / 0.75) + Math.Abs(tCy - sCy) * 0.45;
                return (new Point(sx + sw, sCy), new Point(right + bulge, sCy),
                        new Point(right + bulge, tCy), new Point(tx + tw, tCy));
            }

            bool leftToRight = tx + tw / 2.0 >= sx + sw / 2.0;
            var start = new Point(leftToRight ? sx + sw : sx, sCy);
            var end = new Point(leftToRight ? tx : tx + tw, tCy);
            double ctrl = Math.Max(Math.Abs(end.X - start.X) * 0.5, 30) * (leftToRight ? 1 : -1);
            return (start, new Point(start.X + ctrl, start.Y), new Point(end.X - ctrl, end.Y), end);
        }

        internal static Point BezierMidpoint(Point p0, Point p1, Point p2, Point p3) =>
            new((p0.X + 3 * p1.X + 3 * p2.X + p3.X) / 8.0, (p0.Y + 3 * p1.Y + 3 * p2.Y + p3.Y) / 8.0);

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

            // Connectors end on the card border; back the head off it by a hair so the tip meets the
            // border instead of being half-covered by it.
            const double inset = 2;
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
                if (node.IsSelected) { borderColor = _palette.SelectionInk; borderThickness = 2.6; }
                else if (node.IsHighlighted) { borderColor = ColorFromHex("#22D3EE"); borderThickness = 2.4; }
                else if (node.IsNeighbor) { borderColor = accent; borderThickness = 2.2; }
                else { borderColor = accent; borderThickness = node.IsHub ? 2.2 : 1.4; }

                // Hover: a brighter, heavier border and a small lift, so the card under the pointer
                // reads as the one a click or drag will take.
                bool hovered = node.IsHovered && !node.IsSelected && _draggedNode == null;
                if (hovered)
                {
                    borderColor = Blend(borderColor, _palette.SelectionInk, 0.35);
                    borderThickness += 0.8;
                }

                v.CardFill.Color = CardFillFor(node);
                v.BorderStroke.Color = borderColor;
                v.Root.BorderThickness = new Thickness(borderThickness);
                v.Root.Opacity = node.IsDimmed ? (hovered ? 0.6 : 0.22) : 1.0;
                Canvas.SetZIndex(v.Root, node.IsSelected ? 2 : hovered ? 1 : 0);
                SetLift(v, hovered);
                if (AutomationProperties.GetName(v.Root) != node.Title) AutomationProperties.SetName(v.Root, node.Title);

                v.TitleInk.Color = _palette.Text;
                v.MutedInk.Color = _palette.Muted;
                v.TrackFill.Color = _palette.Track;
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
                    v.ConnectionCount.Text = node.IsHub ? $" {node.ConnectionCount} · hub" : $" {node.ConnectionCount}";
                    v.ConnectionText.FontWeight = node.IsHub ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
                    v.ConnectionInk.Color = node.IsHub ? _palette.HubInk : _palette.Muted;
                }

                v.VersionHost.Visibility = node.HasVersions ? Visibility.Visible : Visibility.Collapsed;
                if (node.HasVersions) v.VersionCount.Text = $" {node.VersionCount}";

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
                bool linkHovered = link.IsHovered && !link.IsSelected;
                e.Stroke.Color = link.IsSelected ? _palette.SelectionInk : ColorFromHex(link.ColorHex);
                e.Line.StrokeThickness = link.IsSelected ? 3.6 : (link.IsInferred ? 1.6 : 2.6) + (linkHovered ? 1.4 : 0);
                e.Line.Opacity = faded ? 0.15 : linkHovered ? 1.0 : (link.IsInferred ? 0.62 : 0.95);
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
            // The card was placed only when shown, so panning slid its node underneath it.
            if (ViewModel.IsPreviewCardVisible) PlacePreviewCard();
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
                if (_nodeIndex.TryGetValue(id, out var node))
                {
                    v.CardFill.Color = detailVisibility == Visibility.Collapsed
                        ? ColorFromHex(node.ColorHex)
                        : CardFillFor(node);
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

        // ---- Hover lift ----

        /// <summary>The fill a card should have right now: a faint wash of its accent while the
        /// pointer is over it.</summary>
        private Color CardFillFor(MindMapNodeViewModel node)
        {
            bool hovered = node.IsHovered && !node.IsSelected && _draggedNode == null;
            return hovered ? Blend(_palette.CardBackground, ColorFromHex(node.ColorHex), 0.12) : _palette.CardBackground;
        }

        private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
            from.A,
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));

        /// <summary>Raises a card a little under the pointer, with the same timing as every button's
        /// hover lift. Only animates when the state actually flips.</summary>
        private static void SetLift(NodeVisual v, bool lifted)
        {
            if (v.Lifted == lifted) return;
            v.Lifted = lifted;
            double to = lifted ? 1.035 : 1.0;

            if (!HoverPolish.AnimationsEnabled)
            {
                v.Lift.ScaleX = to;
                v.Lift.ScaleY = to;
                return;
            }

            var ease = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(140));
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            foreach (var property in new[] { nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY) })
            {
                var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = to, Duration = duration, EasingFunction = ease };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, v.Lift);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, property);
                sb.Children.Add(anim);
            }
            sb.Begin();
        }

        private void AttachNodeInteractions(Border border, MindMapNodeViewModel node)
        {
            border.PointerPressed += (s, e) =>
            {
                e.Handled = true;
                GalaxyCanvas.Focus(FocusState.Programmatic);
                ViewModel.SelectedNode = node;
                _ = node.RefreshVersionHistoryAsync(AppServices.VersionHistory);

                // A right-click selects the card and opens its menu; it mustn't also start a drag
                // that the menu then leaves half-finished.
                if (e.GetCurrentPoint(GalaxyCanvas).Properties.IsRightButtonPressed)
                {
                    RequestRedraw(RedrawScope.Appearance);
                    return;
                }

                _draggedNode = node;
                _dragMovedNode = false;
                _dragStartNodePoint = new Point(node.X, node.Y);
                _dragStartPointerPoint = e.GetCurrentPoint(GalaxyCanvas).Position;

                // Capture on the canvas, not the card, so a drag survives the pointer leaving the
                // card's bounds.
                GalaxyCanvas.CapturePointer(e.Pointer);
                SetCursor(GalaxyCanvas, Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                RequestRedraw(RedrawScope.Appearance);
            };

            border.DoubleTapped += (s, e) =>
            {
                e.Handled = true;
                ViewModel.OpenLinkedDocument(node);
            };

            border.PointerEntered += (s, e) =>
            {
                if (_draggedNode != null || _isPanning) return;
                node.IsHovered = true;
                RequestRedraw(RedrawScope.Appearance);
                if (!string.IsNullOrWhiteSpace(node.MarkdownContent))
                {
                    ViewModel.ShowPreviewCard(node);
                }
            };

            border.PointerExited += (s, e) =>
            {
                if (!node.IsHovered) return;
                node.IsHovered = false;
                RequestRedraw(RedrawScope.Appearance);
                // The preview follows the pointer off the card unless the node is the selection.
                if (ViewModel.SelectedNode != node && ViewModel.PreviewNode == node)
                {
                    ViewModel.HidePreviewCard();
                }
            };
        }

        private static MenuFlyoutItem MenuItem(string text, string glyph, string? keys = null)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (keys != null) item.KeyboardAcceleratorTextOverride = keys;
            return item;
        }

        /// <summary>
        /// The card's right-click menu. Built once per card; its Opening handler refreshes what
        /// depends on the node's current state, so "Open in editor" and "Version history" are
        /// disabled on a node with no file instead of quietly opening a history for its title.
        /// </summary>
        private MenuFlyout BuildNodeContextMenu(MindMapNodeViewModel node)
        {
            var flyout = new MenuFlyout();

            var openItem = MenuItem("Open in editor", "");
            openItem.Click += (s, e) => ViewModel.OpenLinkedDocument(node);
            flyout.Items.Add(openItem);

            var histItem = MenuItem("Version history", "");
            histItem.Click += (s, e) => OpenVersionHistoryForNode(node);
            flyout.Items.Add(histItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var childItem = MenuItem("Add child", "", "Tab");
            childItem.Click += (s, e) =>
            {
                ViewModel.SelectedNode = node;
                ViewModel.AddChildNode();
            };
            flyout.Items.Add(childItem);

            var linkItem = MenuItem("Link to another node…", "", "Ctrl+L");
            linkItem.Click += async (s, e) =>
            {
                ViewModel.SelectedNode = node;
                await ShowConnectDialogAsync();
            };
            flyout.Items.Add(linkItem);

            var moveItem = MenuItem("Move under…", "");
            moveItem.Click += async (s, e) => await ShowReparentDialogAsync(node);
            flyout.Items.Add(moveItem);

            var focusItem = MenuItem("Focus on its connections", "", "F");
            focusItem.Click += (s, e) =>
            {
                ViewModel.SelectedNode = node;
                ViewModel.IsFocusModeEnabled = true;
                ViewModel.CenterOn(node);
            };
            flyout.Items.Add(focusItem);

            var duplicateItem = MenuItem("Duplicate", "", "Ctrl+D");
            duplicateItem.Click += (s, e) =>
            {
                ViewModel.SelectedNode = node;
                ViewModel.DuplicateSelectedNode();
            };
            flyout.Items.Add(duplicateItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = MenuItem("Delete", "", "Delete");
            deleteItem.Click += (s, e) => ViewModel.DeleteNode(node);
            flyout.Items.Add(deleteItem);

            flyout.Opening += (s, e) =>
            {
                bool hasFile = node.HasFile;
                openItem.IsEnabled = hasFile;
                histItem.IsEnabled = hasFile;
                moveItem.IsEnabled = ViewModel.ReparentCandidates(node).Count > 0 || node.ParentId != null;
                linkItem.IsEnabled = ViewModel.Nodes.Count > 1;
            };

            return flyout;
        }

        private MenuFlyout BuildLinkContextMenu(MindMapLinkViewModel link)
        {
            var flyout = new MenuFlyout();

            var reverseItem = MenuItem("Reverse direction", "");
            reverseItem.Click += (s, e) =>
            {
                ViewModel.SelectedLink = link;
                ViewModel.ReverseLink(link);
            };
            flyout.Items.Add(reverseItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = MenuItem("Delete link", "", "Delete");
            deleteItem.Click += (s, e) =>
            {
                ViewModel.SelectedLink = link;
                ViewModel.DeleteSelectionCommand.Execute(null);
            };
            flyout.Items.Add(deleteItem);

            // Opening the menu selects the link, so the inspector shows which one it is about.
            flyout.Opening += (s, e) => ViewModel.SelectedLink = link;

            return flyout;
        }

        /// <summary>Makes <paramref name="target"/> (the link's wide hit stroke, or its label) act
        /// for the link: click selects it, hovering thickens it, right-click opens its menu.</summary>
        private void MakeLinkTarget(FrameworkElement target, MindMapLinkViewModel link, MenuFlyout menu)
        {
            target.Tag = link;
            target.PointerPressed += OnLinkPointerPressed;
            // Hovering a link thickens it, so you can tell which line a click will pick before
            // clicking among several crossing ones.
            target.PointerEntered += (s, args) =>
            {
                if (_draggedNode != null || _isPanning) return;
                link.IsHovered = true;
                RequestRedraw(RedrawScope.Appearance);
            };
            target.PointerExited += (s, args) =>
            {
                link.IsHovered = false;
                RequestRedraw(RedrawScope.Appearance);
            };
            SetCursor(target, Microsoft.UI.Input.InputSystemCursorShape.Hand);
            target.ContextFlyout = menu;
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
            // A selected node keeps its preview card after the pointer leaves it; once nothing is
            // selected the card would describe nothing on screen, so it goes too.
            ViewModel.HidePreviewCard();

            _isPanning = true;
            _lastPanPoint = e.GetCurrentPoint(GalaxyCanvas).Position;
            GalaxyCanvas.CapturePointer(e.Pointer);
            SetCursor(GalaxyCanvas, Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
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
            // Finish the gesture before releasing: ReleasePointerCapture raises PointerCaptureLost
            // synchronously, and that handler ends the gesture too.
            EndCanvasGesture();
            GalaxyCanvas.ReleasePointerCapture(e.Pointer);
        }

        /// <summary>Capture can be taken away mid-drag (Alt+Tab, a dialog, the window losing
        /// focus). Without this the next pointer move kept dragging a card with no button down.</summary>
        private void OnCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndCanvasGesture();

        private void EndCanvasGesture()
        {
            if (_draggedNode != null && _dragMovedNode)
            {
                _draggedNode.SyncToModel();
                ViewModel.IsDirty = true;
                ViewModel.StatusMessage = $"Moved '{_draggedNode.Title}'.";
            }

            bool wasDragging = _draggedNode != null;
            _isPanning = false;
            _draggedNode = null;
            _dragMovedNode = false;
            SetCursor(GalaxyCanvas, Microsoft.UI.Input.InputSystemCursorShape.Arrow);
            if (wasDragging) RequestRedraw(RedrawScope.Appearance);
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
            var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (focused is TextBox or AutoSuggestBox)
            {
                return;
            }

            bool ctrl = IsCtrlDown();

            // Tab / Enter / Delete / Backspace edit the galaxy only while the canvas itself has
            // focus (clicking a node or the canvas focuses it). Anywhere else they keep their normal
            // meaning — Tab moves between toolbar buttons instead of spawning a child node.
            bool canvasFocused = focused is null || ReferenceEquals(focused, GalaxyCanvas);

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
                case Windows.System.VirtualKey.NumberPad0 when ctrl:
                    FitToWindow();
                    e.Handled = true;
                    return;
                // Ctrl+= / Ctrl++ and Ctrl+- (main row 0xBB/0xBD, plus the numpad keys), matching the
                // zoom buttons' tooltips.
                case (Windows.System.VirtualKey)0xBB when ctrl:
                case Windows.System.VirtualKey.Add when ctrl:
                    OnZoomInClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case (Windows.System.VirtualKey)0xBD when ctrl:
                case Windows.System.VirtualKey.Subtract when ctrl:
                    OnZoomOutClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Delete when canvasFocused:
                case Windows.System.VirtualKey.Back when canvasFocused:
                    ViewModel.DeleteSelectionCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Tab when canvasFocused && !ctrl:
                    ViewModel.AddChildNodeCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Enter when canvasFocused:
                    ViewModel.AddSiblingNodeCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.F3:
                    ViewModel.FocusNextMatch(!IsShiftDown());
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.F2 when canvasFocused && ViewModel.SelectedNode != null:
                    // Rename, as in Explorer: straight into the inspector's title with it selected.
                    TitleBox.Focus(FocusState.Programmatic);
                    TitleBox.SelectAll();
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.F when !ctrl && canvasFocused:
                    // Advertised on the focus-mode button's tooltip.
                    OnToggleFocusModeClick(this, new RoutedEventArgs());
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

        /// <summary>The zoom read-out is also a button: one click back to 100%, about the middle of
        /// the view.</summary>
        private void OnZoomResetClick(object sender, RoutedEventArgs e)
            => ZoomAbout(new Point(CanvasWidth / 2.0, CanvasHeight / 2.0), 1.0 / Math.Max(ViewModel.ZoomLevel, 0.0001));

        private void OnFitToWindowClick(object sender, RoutedEventArgs e) => FitToWindow();

        /// <summary>
        /// Actually fits the galaxy to the window. Uses safe view insets so nodes have ample breathing room
        /// and never clip into window borders, behind the top pills/tour banner, or into the bottom legend/minimap.
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

            // Generous safe insets accounting for top title/tag banner, bottom legend, and minimap HUD
            const double safeInsetTop = 90.0;
            const double safeInsetBottom = 80.0;
            const double safeInsetLeft = 60.0;
            const double safeInsetRight = 60.0;

            double availW = Math.Max(200.0, CanvasWidth - (safeInsetLeft + safeInsetRight));
            double availH = Math.Max(200.0, CanvasHeight - (safeInsetTop + safeInsetBottom));

            const double padding = 80.0;
            double worldW = Math.Max(1.0, maxX - minX) + padding * 2.0;
            double worldH = Math.Max(1.0, maxY - minY) + padding * 2.0;

            double zoom = Math.Clamp(Math.Min(availW / worldW, availH / worldH), 0.15, 1.8);

            ViewModel.ZoomLevel = zoom;

            double targetCenterScreenX = safeInsetLeft + (availW / 2.0);
            double targetCenterScreenY = safeInsetTop + (availH / 2.0);

            double worldCenterX = (minX + maxX) / 2.0;
            double worldCenterY = (minY + maxY) / 2.0;

            ViewModel.ViewportOffsetX = -worldCenterX + ((targetCenterScreenX - CanvasWidth / 2.0) / zoom);
            ViewModel.ViewportOffsetY = -worldCenterY + ((targetCenterScreenY - CanvasHeight / 2.0) / zoom);
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

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            // Commit a half-typed field (tags, linked file) before writing.
            GalaxyCanvas.Focus(FocusState.Programmatic);
            await ViewModel.SaveAsync();
        }

        private async void OnSaveAsClick(object sender, RoutedEventArgs e)
        {
            GalaxyCanvas.Focus(FocusState.Programmatic);
            var path = await MarkSmith.Services.NativeFilePicker.PickSaveFileAsync(
                this, "Save a copy of the map", MarkSmith.Services.NativeFilePicker.Purpose.Galaxy,
                SanitizeFileName(ViewModel.Title) + ".msmap",
                new[] { MarkSmith.Models.FileType.Of("Document Galaxy map", ".msmap") }, okLabel: "Save copy");
            if (!string.IsNullOrEmpty(path)) await ViewModel.SaveCopyAsync(path);
        }

        private async void OnImportFolderClick(object sender, RoutedEventArgs e)
        {
            var path = await MarkSmith.Services.NativeFilePicker.PickFolderAsync(
                this, "Choose a folder to map", MarkSmith.Services.NativeFilePicker.Purpose.Galaxy,
                okLabel: "Map this folder");
            if (string.IsNullOrEmpty(path)) return;

            await ViewModel.ImportDirectoryAsync(path);
            FitToWindow();
        }

        private async void OnExportDocxClick(object sender, RoutedEventArgs e)
        {
            // Say it's a Pro export before asking where to save it, not after.
            if (!AppServices.License.CanExportDocx)
            {
                ViewModel.StatusMessage = MarkSmith.Models.ProGate.StatusLine(MarkSmith.Models.FeatureId.DocxExport, AppServices.License.State);
                return;
            }

            GalaxyCanvas.Focus(FocusState.Programmatic);
            var path = await MarkSmith.Services.NativeFilePicker.PickSaveFileAsync(
                this, "Export the map as a Word document", MarkSmith.Services.NativeFilePicker.Purpose.Exports,
                SanitizeFileName(ViewModel.Title) + ".docx",
                new[] { MarkSmith.Models.FileType.Of("Word document", ".docx") }, okLabel: "Export");
            if (!string.IsNullOrEmpty(path)) ViewModel.ExportToDocx(path);
        }

        private void OnCopyMermaidFlowchartClick(object sender, RoutedEventArgs e)
            => CopyMermaid(asFlowchart: true, "flowchart");

        private void OnCopyMermaidMindmapClick(object sender, RoutedEventArgs e)
            => CopyMermaid(asFlowchart: false, "mind map");

        private void CopyMermaid(bool asFlowchart, string label)
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(ViewModel.ExportToMermaid(asFlowchart));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                ViewModel.StatusMessage = $"Copied the map as a Mermaid {label}. Paste it into any document.";
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Couldn't copy to the clipboard: {ex.Message}";
            }
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

        private static TextBlock DialogLead(string text) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
        }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        private async Task ShowConnectDialogAsync()
        {
            if (ViewModel.SelectedNode == null)
            {
                ViewModel.StatusMessage = "Select the node to link from first, then choose Link.";
                return;
            }

            var source = ViewModel.SelectedNode;
            var otherNodes = ViewModel.Nodes.Where(n => n.Id != source.Id)
                .OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (otherNodes.Count == 0)
            {
                ViewModel.StatusMessage = "There's nothing else to link to yet. Add another node first.";
                return;
            }

            var combo = new ComboBox
            {
                Header = "Link to",
                ItemsSource = otherNodes,
                DisplayMemberPath = "Title",
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var labelBox = new TextBox
            {
                Header = "Why are these connected?",
                PlaceholderText = "e.g. grew out of, evidence for, supersedes"
            };

            var suggestions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (string preset in new[] { "grew out of", "evidence for", "supersedes", "references" })
            {
                var chip = new Button { Content = preset, FontSize = 12, Padding = new Thickness(10, 3, 10, 3), CornerRadius = new CornerRadius(12) };
                chip.Click += (s, args) =>
                {
                    labelBox.Text = preset;
                    labelBox.Focus(FocusState.Programmatic);
                    labelBox.SelectionStart = preset.Length;
                };
                suggestions.Children.Add(chip);
            }

            var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
            panel.Children.Add(DialogLead($"Draw a link from “{source.Title}” to another node, and say why they belong together. The reason is shown on the line."));
            panel.Children.Add(combo);
            panel.Children.Add(labelBox);
            panel.Children.Add(suggestions);

            var dialog = new ContentDialog
            {
                Title = "Link two documents",
                PrimaryButtonText = "Link",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
                Content = panel
            };
            dialog.Opened += (s, args) => combo.Focus(FocusState.Programmatic);

            if (await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog) == ContentDialogResult.Primary && combo.SelectedItem is MindMapNodeViewModel target)
            {
                ViewModel.ConnectNodes(source.Id, target.Id, labelBox.Text);
                RequestRedraw();
            }
        }

        private async Task ShowReparentDialogAsync(MindMapNodeViewModel node)
        {
            // Only nodes the move can actually go to: the node's own branch used to be listed too,
            // and picking one of those was refused after the dialog closed.
            var candidates = ViewModel.ReparentCandidates(node);
            var currentParent = candidates.FirstOrDefault(n => n.Id == node.ParentId);

            var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
            panel.Children.Add(DialogLead(currentParent != null
                ? $"“{node.Title}” is under “{currentParent.Title}”. Choose its new parent. Anything under it moves with it."
                : $"“{node.Title}” has no parent. Choose one to hang it from. Anything under it moves with it."));

            var combo = new ComboBox
            {
                Header = "New parent",
                ItemsSource = candidates,
                DisplayMemberPath = "Title",
                SelectedItem = currentParent ?? candidates.FirstOrDefault(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = candidates.Count > 0
            };
            panel.Children.Add(combo);

            var dialog = new ContentDialog
            {
                Title = "Move under another node",
                PrimaryButtonText = "Move",
                IsPrimaryButtonEnabled = candidates.Count > 0,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
                Content = panel
            };
            if (node.ParentId != null) dialog.SecondaryButtonText = "Remove parent";
            combo.SelectionChanged += (s, args) =>
                dialog.IsPrimaryButtonEnabled = combo.SelectedItem is MindMapNodeViewModel p && p.Id != node.ParentId;
            dialog.IsPrimaryButtonEnabled = combo.SelectedItem is MindMapNodeViewModel first && first.Id != node.ParentId;
            dialog.Opened += (s, args) => combo.Focus(FocusState.Programmatic);

            var result = await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
            if (result == ContentDialogResult.Primary && combo.SelectedItem is MindMapNodeViewModel parent)
            {
                ViewModel.ReparentNode(node.Id, parent.Id);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                ViewModel.ReparentNode(node.Id, null);
            }
        }

        private async void OnBrowseForFileClick(object sender, RoutedEventArgs e)
        {
            var node = ViewModel.SelectedNode;
            if (node == null) return;

            var path = await MarkSmith.Services.NativeFilePicker.PickOpenFileAsync(
                this, $"Attach a file to “{node.Title}”", MarkSmith.Services.NativeFilePicker.Purpose.Documents,
                new[]
                {
                    MarkSmith.Models.FileType.Of("Documents", ".md", ".markdown", ".txt", ".docx", ".pdf", ".pptx", ".epub"),
                    MarkSmith.Models.FileType.AllFiles,
                },
                okLabel: "Attach",
                folder: string.IsNullOrWhiteSpace(node.FilePath) ? null : System.IO.Path.GetDirectoryName(node.FilePath));
            if (string.IsNullOrEmpty(path)) return;

            ViewModel.AttachFile(node, path);
        }

        // ---- Map report ----

        /// <summary>
        /// The map report: headline figures as tiles, then the busiest documents, tags and
        /// anything unlinked as clickable rows that take you to that node. It used to be one
        /// monospace text dump with bullet characters, and nothing in it led anywhere.
        /// </summary>
        private async void OnShowInsightsClick(object sender, RoutedEventArgs e)
        {
            var insights = ViewModel.GetInsights();
            ContentDialog? dialog = null;

            var root = new StackPanel { Spacing = 18, MinWidth = 440 };

            var tiles = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (int c = 0; c < 3; c++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var figures = new List<(string Value, string Label)>
            {
                (insights.NodeCount.ToString("N0"), insights.NodeCount == 1 ? "document" : "documents"),
                (insights.LinkedFileCount.ToString("N0"), "linked to a file"),
                (insights.LinkCount.ToString("N0"), insights.LinkCount == 1 ? "named link" : "named links"),
                (insights.HierarchyEdgeCount.ToString("N0"), "parent and child"),
                (insights.ClusterCount.ToString("N0"), insights.ClusterCount == 1 ? "group" : "separate groups"),
                (insights.TotalWordCount > 0 ? insights.TotalWordCount.ToString("N0") : "—", "words"),
            };
            for (int i = 0; i < figures.Count; i++)
            {
                if (i % 3 == 0) tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var tile = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Child = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = figures[i].Value, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock { Text = figures[i].Label, FontSize = 12 }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush")
                        }
                    }
                }.Themed(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush").Themed(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                Grid.SetRow(tile, i / 3);
                Grid.SetColumn(tile, i % 3);
                tiles.Children.Add(tile);
            }
            root.Children.Add(tiles);

            StackPanel Section(string heading)
            {
                var section = new StackPanel { Spacing = 4 };
                section.Children.Add(new TextBlock { Text = heading, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
                root.Children.Add(section);
                return section;
            }

            Button NodeRow(string nodeId, string title, string detail)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 });
                var detailText = new TextBlock { Text = detail, FontSize = 12 }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                Grid.SetColumn(detailText, 1);
                row.Children.Add(detailText);

                var button = new Button
                {
                    Content = row,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 6, 10, 6),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };
                ToolTipService.SetToolTip(button, "Show this node on the map");
                button.Click += (s, args) =>
                {
                    dialog?.Hide();
                    var node = ViewModel.Nodes.FirstOrDefault(n => n.Id == nodeId);
                    if (node == null) return;
                    ViewModel.SelectedLink = null;
                    ViewModel.SelectedNode = node;
                    ViewModel.CenterOn(node);
                };
                return button;
            }

            if (insights.Hubs.Count > 0)
            {
                var hubs = Section("Most connected");
                foreach (var (id, title, degree) in insights.Hubs)
                {
                    hubs.Children.Add(NodeRow(id, title, degree == 1 ? "1 connection" : $"{degree} connections"));
                }
            }

            if (insights.IsolatedNodeIds.Count > 0)
            {
                var lonely = Section("Not linked to anything");
                lonely.Children.Add(new TextBlock
                {
                    Text = "Link these and they stop being lost.",
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 2)
                }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"));
                const int shown = 8;
                foreach (var id in insights.IsolatedNodeIds.Take(shown))
                {
                    var node = ViewModel.Nodes.FirstOrDefault(n => n.Id == id);
                    if (node != null) lonely.Children.Add(NodeRow(id, node.Title, node.FormatBadge));
                }
                if (insights.IsolatedNodeIds.Count > shown)
                {
                    lonely.Children.Add(new TextBlock
                    {
                        Text = $"and {insights.IsolatedNodeIds.Count - shown} more",
                        FontSize = 12,
                        Margin = new Thickness(10, 2, 0, 0),
                    }.Themed(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush"));
                }
            }

            if (insights.TopTags.Count > 0)
            {
                // Rows, like the lists above. A wrap grid sized every chip to the first one and
                // clipped the longer tags.
                var tags = Section("Most used tags");
                foreach (var (tag, count) in insights.TopTags)
                {
                    var row = new Grid { ColumnSpacing = 12 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock { Text = tag, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
                    var detail = new TextBlock { Text = count == 1 ? "1 node" : $"{count} nodes", FontSize = 12 }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                    Grid.SetColumn(detail, 1);
                    row.Children.Add(detail);

                    var button = new Button
                    {
                        Content = row,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(10, 6, 10, 6),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0)
                    };
                    ToolTipService.SetToolTip(button, "Show only the nodes with this tag");
                    button.Click += (s, args) =>
                    {
                        dialog?.Hide();
                        ViewModel.SelectedTagFilter = tag;
                    };
                    tags.Children.Add(button);
                }
            }

            if (insights.FormatBreakdown.Count > 0)
            {
                var formats = Section("Formats");
                formats.Children.Add(new TextBlock
                {
                    Text = string.Join("   ·   ", insights.FormatBreakdown
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => $"{kv.Key.TrimStart('.').ToUpperInvariant()} {kv.Value}")),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"));
            }

            dialog = new ContentDialog
            {
                Title = "Map report",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
                Content = new ScrollViewer { MaxHeight = 480, Content = root, Padding = new Thickness(0, 0, 12, 0) }
            };

            await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
        }

        // ---- Inspector & filters ----

        private void OnPaletteSwatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string hex)
            {
                ViewModel.RecolorSelectedNode(hex);
            }
        }

        private readonly HashSet<Button> _swatches = new();
        private MindMapNodeViewModel? _watchedNode;

        private void OnSwatchLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b) return;
            _swatches.Add(b);
            if (b.Tag is string hex) AutomationProperties.SetName(b, ColourName(hex));
            ToolTipService.SetToolTip(b, b.Tag is string h ? ColourName(h) : null);
            UpdateSwatches();
        }

        private void OnSwatchUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button b) _swatches.Remove(b);
        }

        /// <summary>Swatches showed hex codes as tooltips ("#FF7C4D"); these are their names.</summary>
        private static string ColourName(string hex) => hex.ToUpperInvariant() switch
        {
            "#FF7C4D" => "Orange",
            "#22D3EE" => "Cyan",
            "#34D399" => "Green",
            "#3B82F6" => "Blue",
            "#A855F7" => "Purple",
            "#EC4899" => "Pink",
            "#FBBF24" => "Yellow",
            "#E11D48" => "Crimson",
            _ => hex
        };

        /// <summary>Rings the swatch matching the selected node's colour, so the palette shows
        /// what the node is now and not just what it could be.</summary>
        private void UpdateSwatches()
        {
            string? current = ViewModel.SelectedNode?.ColorHex;
            var none = new SolidColorBrush(Colors.Transparent);
            foreach (var b in _swatches)
            {
                bool on = current != null && b.Tag is string hex && string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
                // The ring is the panel's text colour, so it follows a theme change like the text does.
                if (on) ThemeBrush.Set(b, Control.BorderBrushProperty, "TextFillColorPrimaryBrush");
                else { ThemeBrush.Clear(b, Control.BorderBrushProperty); b.BorderBrush = none; }
                AutomationProperties.SetItemStatus(b, on ? "Current colour" : "");
            }
        }

        /// <summary>Follows the selected node's colour, so a recolour (or an undo of one) moves the
        /// ring with it.</summary>
        private void WatchSelectedNodeColour()
        {
            if (_watchedNode != null) _watchedNode.PropertyChanged -= OnWatchedNodePropertyChanged;
            _watchedNode = ViewModel.SelectedNode;
            if (_watchedNode != null) _watchedNode.PropertyChanged += OnWatchedNodePropertyChanged;
        }

        private void OnWatchedNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MindMapNodeViewModel.ColorHex)) UpdateSwatches();
        }

        private void OnReverseLinkClick(object sender, RoutedEventArgs e) => ViewModel.ReverseLink(ViewModel.SelectedLink);

        /// <summary>
        /// Themes recolour the cards and text, not just the backdrop. "Clean White" used to swap
        /// the canvas to near-white while leaving every card dark-on-dark with white text, which
        /// rendered the whole map unreadable. Driven by the view model's theme, so a theme that
        /// was saved with the map (or restored by Undo) is drawn too.
        /// </summary>
        private void ApplyPalette()
        {
            _palette = GalaxyPalette.ForName(ViewModel.SelectedThemeName);
            CanvasContainer.Background = new SolidColorBrush(_palette.Background);
            MinimapOverlay.Background = new SolidColorBrush(_palette.MinimapBackground);
            // The canvas is always dark except for Clean White; the floating cards follow it, not
            // the OS theme, so they read on whichever background is behind them.
            CanvasContainer.RequestedTheme = _palette.IsLight ? ElementTheme.Light : ElementTheme.Dark;
            foreach (var e in _edgeVisuals.Values)
            {
                if (e.Link == null) e.Stroke.Color = ColorFromHex(_palette.HierarchyLine);
            }
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

        private readonly HashSet<Button> _tagPills = new();

        private void OnTagPillLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button b) _tagPills.Add(b);
            UpdateTagPills();
        }

        private void OnTagPillUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button b) _tagPills.Remove(b);
        }

        /// <summary>The active filter's pill (or "All tags") in the accent colour; the rest quiet.
        /// Nothing used to show that a filter was on except the map going dim.</summary>
        private void UpdateTagPills()
        {
            string? active = ViewModel.SelectedTagFilter;
            var res = Application.Current.Resources;
            foreach (var b in _tagPills)
            {
                bool on = b == AllTagsPill
                    ? string.IsNullOrEmpty(active)
                    : b.Tag is string tag && string.Equals(tag, active, StringComparison.OrdinalIgnoreCase);
                // Inactive pills go back to the button's own (theme-aware) look, so they follow the
                // canvas theme; app-level brushes would stay dark-theme on a light canvas.
                if (on)
                {
                    b.Background = (Brush)res["AccentFillColorDefaultBrush"];
                    b.Foreground = (Brush)res["TextOnAccentFillColorPrimaryBrush"];
                    b.BorderThickness = new Thickness(0);
                }
                else
                {
                    b.ClearValue(Control.BackgroundProperty);
                    b.ClearValue(Control.ForegroundProperty);
                    b.ClearValue(Control.BorderThicknessProperty);
                }
                if (b != AllTagsPill)
                {
                    ToolTipService.SetToolTip(b, on ? "Show every node again" : "Show only nodes with this tag");
                }
                AutomationProperties.SetItemStatus(b, on ? "Active filter" : "");
            }
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
            OpenVersionHistoryForNode(ViewModel.PreviewNode);
        }

        /// <summary>Version history is per file. A node with no file has none: the menu item and
        /// buttons are disabled for it, and this refuses rather than opening a history window for
        /// a file named after the node's title.</summary>
        private void OpenVersionHistoryForNode(MindMapNodeViewModel? node)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.FilePath)) return;
            History.HistoryWindow.ShowFor(node.FilePath);
        }

        private void OnOpenInEditorClick(object sender, RoutedEventArgs e)
        {
            var node = ViewModel.PreviewNode;
            ViewModel.HidePreviewCard();
            ViewModel.OpenLinkedDocument(node);
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
            var edgeStroke = new SolidColorBrush(ColorFromHex(_palette.HierarchyLine));
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
                    Fill = new SolidColorBrush(node.IsSelected ? _palette.SelectionInk : ColorFromHex(node.ColorHex)),
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

        private void OnMinimapPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _isMinimapDragging = false;

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
            /// <summary>The selected card's border and a selected link. It was hard-coded white,
            /// which vanished on Clean White's white cards.</summary>
            public Color SelectionInk { get; init; } = Colors.White;

            /// <summary>The "hub" connection count on busy cards.</summary>
            public Color HubInk { get; init; } = ColorFromHex("#FBBF24");

            /// <summary>The empty part of a card's progress bar (was white at 20%, invisible on
            /// light cards).</summary>
            public Color Track { get; init; } = ColorFromHex("#33FFFFFF");

            /// <summary>A light canvas. The overlays on it (tour banner, tag bar, legend, preview
            /// card) switch to the light theme with it, or their dark-theme text is white on white.</summary>
            public bool IsLight { get; init; }

            public static readonly GalaxyPalette MidnightGalaxy = new(
                ColorFromHex("#12131C"), ColorFromHex("#1C1C28"), ColorFromHex("#F1F1F8"),
                ColorFromHex("#9A9AB0"), "#5A6478", ColorFromHex("#E0161722"));

            public static GalaxyPalette ForName(string? name) => name switch
            {
                "Clean White" => new GalaxyPalette(
                    ColorFromHex("#F8FAFC"), ColorFromHex("#FFFFFF"), ColorFromHex("#111827"),
                    ColorFromHex("#64748B"), "#94A3B8", ColorFromHex("#E0F1F5FA"))
                {
                    SelectionInk = ColorFromHex("#0F172A"),
                    HubInk = ColorFromHex("#B45309"),
                    Track = ColorFromHex("#1F0F172A"),
                    IsLight = true
                },
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
