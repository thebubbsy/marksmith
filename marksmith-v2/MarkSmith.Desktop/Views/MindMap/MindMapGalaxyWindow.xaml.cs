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
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            SizeForDisplay();

            ViewModel = viewModel ?? new MindMapStudioViewModel();
            this.RootGrid.DataContext = ViewModel;

            // Same chrome as the other studio windows: the header doubles as the title bar, instead
            // of a stock (light on a light-mode PC) system caption strip above an always-dark galaxy.
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            TitleBarInsets.Reserve(this, AppTitleBar);
            CaptionButtons.Follow(this, AppTitleBar);

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

            SyncAccessibleStatusLabels();
        }

        private void SizeForDisplay()
        {
            try
            {
                var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
                if (scale <= 0) scale = 1;
                var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
                    Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
                AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    Math.Min((int)(1440 * scale), work.Width), Math.Min((int)(880 * scale), work.Height)));
                if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                {
                    presenter.PreferredMinimumWidth = Math.Min((int)(1100 * scale), work.Width);
                    presenter.PreferredMinimumHeight = Math.Min((int)(620 * scale), work.Height);
                }
            }
            catch { /* best effort: default size still works */ }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private void SyncAccessibleStatusLabels()
        {
            if (StatusBarText != null && !string.IsNullOrEmpty(ViewModel.StatusMessage))
            {
                AutomationProperties.SetName(StatusBarText, ViewModel.StatusMessage);
                AutomationProperties.SetLiveSetting(StatusBarText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            }
            if (SearchMatchLabel != null)
                AutomationProperties.SetName(SearchMatchLabel, ViewModel.SearchMatchText ?? "");
            if (InsightsSummaryText != null)
                AutomationProperties.SetName(InsightsSummaryText, ViewModel.InsightsSummary ?? "");
            if (NodesCountText != null)
                AutomationProperties.SetName(NodesCountText, ViewModel.NodesCountText ?? "");
            if (LinksCountText != null)
                AutomationProperties.SetName(LinksCountText, ViewModel.LinksCountText ?? "");
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
                case nameof(MindMapStudioViewModel.StatusMessage):
                case nameof(MindMapStudioViewModel.SearchMatchText):
                case nameof(MindMapStudioViewModel.InsightsSummary):
                case nameof(MindMapStudioViewModel.NodesCountText):
                case nameof(MindMapStudioViewModel.LinksCountText):
                    SyncAccessibleStatusLabels();
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
            public SolidColorBrush ProgressFillBrush = null!;
            public SolidColorBrush ProgressInk = null!;
            public SolidColorBrush VersionFill = null!;
            public SolidColorBrush VersionInk = null!;
            public SolidColorBrush MissingInk = null!;
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
            public ScaleTransform? LabelLift;
            public bool LabelLifted;
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
                ConnectionInk = new SolidColorBrush(_palette.Muted),
                ProgressFillBrush = new SolidColorBrush(_palette.ProgressFill),
                ProgressInk = new SolidColorBrush(_palette.ProgressInk),
                VersionFill = new SolidColorBrush(_palette.VersionFill),
                VersionInk = new SolidColorBrush(_palette.VersionInk),
                MissingInk = new SolidColorBrush(_palette.MissingInk)
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
            AutomationProperties.SetName(v.Root, node.AccessibleSummary);
            ToolTipService.SetToolTip(v.Root, node.AccessibleSummary);
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
                Background = v.ProgressFillBrush,
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
                Foreground = v.ProgressInk,
                VerticalAlignment = VerticalAlignment.Center
            };
            v.Bottom.Children.Add(v.ProgressText);

            v.TagText = new TextBlock { FontSize = 9, Foreground = v.MutedInk, VerticalAlignment = VerticalAlignment.Center };
            v.Bottom.Children.Add(v.TagText);

            // Card glyphs come from Segoe Fluent Icons, like every other icon in the app; these
            // three were emoji (🔗 ⏱️ ⚠), which drew in colour at a different size and baseline.
            (v.ConnectionText, v.ConnectionCount) = GlyphLabel("\uE71B", 9, v.ConnectionInk);
            v.Bottom.Children.Add(v.ConnectionText);

            (v.VersionText, v.VersionCount) = GlyphLabel("\uE81C", 9, v.VersionInk);
            v.VersionText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            v.VersionHost = new Border
            {
                Background = v.VersionFill,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = v.VersionText
            };
            ToolTipService.SetToolTip(v.VersionHost, "Saved versions of this document");
            v.Bottom.Children.Add(v.VersionHost);

            v.MissingMark = new TextBlock
            {
                Text = "\uE7BA",
                FontFamily = GlyphFontFamily,
                FontSize = 10,
                Foreground = v.MissingInk,
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

            e.LabelLift = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
            e.LabelText = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(_palette.Text) };
            e.Label = new Border
            {
                Background = new SolidColorBrush(_palette.CardBackground),
                BorderBrush = new SolidColorBrush(ColorFromHex(link.ColorHex)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                RenderTransform = e.LabelLift,
                RenderTransformOrigin = new Point(0.5, 0.5),
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
                else if (node.IsHighlighted) { borderColor = _palette.HighlightBorder; borderThickness = 2.4; }
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
                string summary = node.AccessibleSummary;
                if (AutomationProperties.GetName(v.Root) != summary)
                {
                    AutomationProperties.SetName(v.Root, summary);
                    ToolTipService.SetToolTip(v.Root, summary);
                }

                v.TitleInk.Color = _palette.Text;
                v.MutedInk.Color = _palette.Muted;
                v.TrackFill.Color = _palette.Track;
                v.BadgeFill.Color = accent;
                v.BadgeInk.Color = ReadableOn(accent);
                v.ProgressFillBrush.Color = _palette.ProgressFill;
                v.ProgressInk.Color = _palette.ProgressInk;
                v.VersionFill.Color = _palette.VersionFill;
                v.VersionInk.Color = _palette.VersionInk;
                v.MissingInk.Color = _palette.MissingInk;

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
                bool hasEndpoints = _nodeIndex.TryGetValue(e.SourceId, out var s) & _nodeIndex.TryGetValue(e.TargetId, out var t);
                bool faded = hasEndpoints && s!.IsDimmed && t!.IsDimmed;

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
                    ((SolidColorBrush)e.Label.BorderBrush).Color = link.IsSelected ? _palette.SelectionInk : ColorFromHex(link.ColorHex);
                    e.Label.BorderThickness = new Thickness(link.IsSelected || linkHovered ? 1.6 : 1.0);
                    e.Label.Opacity = link.IsInferred && !link.IsSelected && !linkHovered ? 0.85 : 1.0;

                    string linkDesc = link.DescribeEndpoints(s?.Title, t?.Title);
                    if (AutomationProperties.GetName(e.Label) != linkDesc)
                    {
                        AutomationProperties.SetName(e.Label, linkDesc);
                        ToolTipService.SetToolTip(e.Label, linkDesc);
                    }
                    if (e.Hit != null && AutomationProperties.GetName(e.Hit) != linkDesc)
                    {
                        AutomationProperties.SetName(e.Hit, linkDesc);
                        ToolTipService.SetToolTip(e.Hit, linkDesc);
                    }
                    SetLabelLift(e, linkHovered || link.IsSelected);

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
            AnimateScale(v.Lift, lifted ? 1.035 : 1.0);
        }

        private static void SetLabelLift(EdgeVisual e, bool lifted)
        {
            if (e.LabelLift == null || e.LabelLifted == lifted) return;
            e.LabelLifted = lifted;
            AnimateScale(e.LabelLift, lifted ? 1.05 : 1.0);
        }

        private static void AnimateScale(ScaleTransform transform, double to)
        {
            if (!HoverPolish.AnimationsEnabled)
            {
                transform.ScaleX = to;
                transform.ScaleY = to;
                return;
            }

            var ease = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(140));
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            foreach (var property in new[] { nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY) })
            {
                var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = to, Duration = duration, EasingFunction = ease };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, transform);
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

            var openItem = MenuItem("Open in editor", "\uE8A7");
            openItem.Click += (s, e) => ViewModel.OpenLinkedDocument(node);
            flyout.Items.Add(openItem);

            var histItem = MenuItem("Version history", "\uE81C");
            histItem.Click += (s, e) => OpenVersionHistoryForNode(node);
            flyout.Items.Add(histItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var copyItem = MenuItem("Copy title", "\uE8C8", "Ctrl+C");
            copyItem.Click += async (s, e) =>
            {
                ViewModel.SelectedNode = node;
                await CopyNodeTitleAsync(node);
            };
            flyout.Items.Add(copyItem);

            var renameItem = MenuItem("Rename", "\uE8AC", "F2");
            renameItem.Click += (s, e) =>
            {
                ViewModel.SelectedNode = node;
                TitleBox.Focus(FocusState.Programmatic);
                TitleBox.SelectAll();
            };
            flyout.Items.Add(renameItem);

            var childItem = MenuItem("Add child", "\uE710", "Tab");
            childItem.Click += (s, e) =>
            {
                ViewModel.SelectedNode = node;
                ViewModel.AddChildNode();
            };
            flyout.Items.Add(childItem);

            var siblingItem = MenuItem("Add sibling", "\uE710", "Enter");
            siblingItem.Click += (s, e) =>
            {
                ViewModel.SelectedNode = node;
                ViewModel.AddSiblingNode();
            };
            flyout.Items.Add(siblingItem);

            var linkItem = MenuItem("Link to another node…", "\uE71B", "Ctrl+L");
            linkItem.Click += async (s, e) =>
            {
                ViewModel.SelectedNode = node;
                await ShowConnectDialogAsync();
            };
            flyout.Items.Add(linkItem);

            var moveItem = MenuItem("Move under…", "\uE8DE");
            moveItem.Click += async (s, e) => await ShowReparentDialogAsync(node);
            flyout.Items.Add(moveItem);

            var focusItem = MenuItem("Focus on its connections", "\uE890", "F");
            focusItem.Click += (s, e) =>
            {
                if (ViewModel.IsFocusModeEnabled && ViewModel.SelectedNode == node)
                {
                    ViewModel.IsFocusModeEnabled = false;
                }
                else
                {
                    ViewModel.SelectedNode = node;
                    ViewModel.IsFocusModeEnabled = true;
                    ViewModel.CenterOn(node);
                }
            };
            flyout.Items.Add(focusItem);

            var duplicateItem = MenuItem("Duplicate", "\uE8C8", "Ctrl+D");
            duplicateItem.Click += (s, e) =>
            {
                ViewModel.SelectedNode = node;
                ViewModel.DuplicateSelectedNode();
            };
            flyout.Items.Add(duplicateItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = MenuItem("Delete", "\uE74D", "Delete");
            deleteItem.Click += (s, e) => ViewModel.DeleteNode(node);
            flyout.Items.Add(deleteItem);

            flyout.Opening += (s, e) =>
            {
                bool hasFile = node.HasFile;
                openItem.IsEnabled = hasFile;
                histItem.IsEnabled = hasFile;
                moveItem.IsEnabled = ViewModel.ReparentCandidates(node).Count > 0 || node.ParentId != null;
                linkItem.IsEnabled = ViewModel.Nodes.Count > 1;
                focusItem.Text = ViewModel.IsFocusModeEnabled && ViewModel.SelectedNode == node
                    ? "Show whole map"
                    : "Focus on its connections";
            };

            return flyout;
        }

        private MenuFlyout BuildLinkContextMenu(MindMapLinkViewModel link)
        {
            var flyout = new MenuFlyout();

            var editReasonItem = MenuItem("Edit reason…", "\uE70F", "F2");
            editReasonItem.Click += (s, e) =>
            {
                ViewModel.SelectedLink = link;
                LinkLabelBox.Focus(FocusState.Programmatic);
                LinkLabelBox.SelectAll();
            };
            flyout.Items.Add(editReasonItem);

            var reverseItem = MenuItem("Reverse direction", "\uE8AB");
            reverseItem.Click += (s, e) =>
            {
                ViewModel.SelectedLink = link;
                ViewModel.ReverseLink(link);
            };
            flyout.Items.Add(reverseItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = MenuItem("Delete link", "\uE74D", "Delete");
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

            var pt = e.GetCurrentPoint(GalaxyCanvas);
            if (pt.Properties.IsRightButtonPressed)
            {
                var pos = pt.Position;
                double worldX = ((pos.X - CanvasWidth / 2.0) / ViewModel.ZoomLevel) - ViewModel.ViewportOffsetX;
                double worldY = ((pos.Y - CanvasHeight / 2.0) / ViewModel.ZoomLevel) - ViewModel.ViewportOffsetY;
                ShowCanvasContextMenu(pos, worldX, worldY);
                e.Handled = true;
                return;
            }

            // Clicking empty space clears the selection, which is also how you get back out of a
            // link selection without deleting anything.
            ViewModel.SelectedNode = null;
            ViewModel.SelectedLink = null;
            // A selected node keeps its preview card after the pointer leaves it; once nothing is
            // selected the card would describe nothing on screen, so it goes too.
            ViewModel.HidePreviewCard();

            _isPanning = true;
            _lastPanPoint = pt.Position;
            GalaxyCanvas.CapturePointer(e.Pointer);
            SetCursor(GalaxyCanvas, Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
            RequestRedraw(RedrawScope.Appearance);
        }

        private void ShowCanvasContextMenu(Point screenPos, double worldX, double worldY)
        {
            var flyout = new MenuFlyout();

            var addHereItem = MenuItem("Add node here", "\uE710");
            addHereItem.Click += (s, e) => ViewModel.AddNodeAt(worldX - 95, worldY - 28);
            flyout.Items.Add(addHereItem);

            var importItem = MenuItem("Import folder…", "\uE8B7");
            importItem.Click += OnImportFolderClick;
            flyout.Items.Add(importItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var arrangeSub = new MenuFlyoutSubItem
            {
                Text = "Arrange",
                Icon = new FontIcon { Glyph = "\uE80A" },
                IsEnabled = ViewModel.Nodes.Count > 1
            };
            var treeItem = MenuItem("Tree (left to right)", "\uE80A");
            treeItem.Click += OnLayoutTreeClick;
            arrangeSub.Items.Add(treeItem);
            var hierItem = MenuItem("Hierarchy (top to bottom)", "\uE80A");
            hierItem.Click += OnLayoutHierarchyClick;
            arrangeSub.Items.Add(hierItem);
            var radialItem = MenuItem("Radial around the root", "\uEC26");
            radialItem.Click += OnLayoutRadialClick;
            arrangeSub.Items.Add(radialItem);
            var clusterItem = MenuItem("By folder & tag", "\uF246");
            clusterItem.Click += OnLayoutClustersClick;
            arrangeSub.Items.Add(clusterItem);
            var forceItem = MenuItem("Spread apart", "\uE945");
            forceItem.Click += OnLayoutForceClick;
            arrangeSub.Items.Add(forceItem);
            flyout.Items.Add(arrangeSub);

            var fitItem = MenuItem("Fit to window", "\uE9A6", "Ctrl+0");
            fitItem.IsEnabled = ViewModel.Nodes.Count > 0;
            fitItem.Click += OnFitToWindowClick;
            flyout.Items.Add(fitItem);

            var resetZoomItem = MenuItem("Zoom to 100%", "\uE71E");
            resetZoomItem.Click += OnZoomResetClick;
            flyout.Items.Add(resetZoomItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var copyMermaidItem = MenuItem("Copy map as Mermaid", "\uE8C8", "Ctrl+Shift+C");
            copyMermaidItem.Click += (s, e) => CopyMermaid(asFlowchart: true, "flowchart");
            flyout.Items.Add(copyMermaidItem);

            flyout.ShowAt(GalaxyCanvas, screenPos);
        }

        private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.Handled) return;
            var pos = e.GetPosition(GalaxyCanvas);
            double worldX = ((pos.X - CanvasWidth / 2.0) / ViewModel.ZoomLevel) - ViewModel.ViewportOffsetX;
            double worldY = ((pos.Y - CanvasHeight / 2.0) / ViewModel.ZoomLevel) - ViewModel.ViewportOffsetY;
            ShowCanvasContextMenu(pos, worldX, worldY);
            e.Handled = true;
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
        private static bool IsAltDown() => IsDown(Windows.System.VirtualKey.Menu);

        private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Never steal a keystroke that belongs to a text box in the inspector,
            // except Ctrl+F which should always jump to the SearchBox.
            var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (focused is TextBox or AutoSuggestBox)
            {
                if (IsCtrlDown() && e.Key == Windows.System.VirtualKey.F)
                {
                    SearchBox.Focus(FocusState.Programmatic);
                    e.Handled = true;
                }
                return;
            }

            bool ctrl = IsCtrlDown();
            bool shift = IsShiftDown();
            bool alt = IsAltDown();

            // Tab / Enter / Delete / Backspace / Arrows edit or navigate the galaxy only while the
            // canvas itself has focus (clicking a node or the canvas focuses it). Anywhere else they
            // keep their normal meaning — Tab moves between toolbar buttons instead of spawning a child node.
            bool canvasFocused = focused is null || ReferenceEquals(focused, GalaxyCanvas);

            switch (e.Key)
            {
                case Windows.System.VirtualKey.F when ctrl:
                    SearchBox.Focus(FocusState.Programmatic);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Z when ctrl && shift:
                    ViewModel.RedoCommand.Execute(null);
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
                case Windows.System.VirtualKey.C when ctrl && !shift && canvasFocused && ViewModel.SelectedNode != null:
                    await CopyNodeTitleAsync(ViewModel.SelectedNode);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.C when ctrl && shift && canvasFocused:
                    CopyMermaid(asFlowchart: true, "flowchart");
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
                case Windows.System.VirtualKey.Number1 when ctrl:
                case Windows.System.VirtualKey.NumberPad1 when ctrl:
                    OnZoomResetClick(this, new RoutedEventArgs());
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
                case Windows.System.VirtualKey.Tab when canvasFocused && !ctrl && !alt:
                case Windows.System.VirtualKey.N when canvasFocused && !ctrl && !alt:
                    ViewModel.AddChildNodeCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Enter when canvasFocused && !ctrl && !alt:
                    ViewModel.AddSiblingNodeCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Home when canvasFocused && !ctrl && !alt:
                    if (ViewModel.SelectRootNode() is { } rootNode)
                    {
                        ViewModel.CenterOn(rootNode);
                        RequestRedraw();
                    }
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Left when canvasFocused && !ctrl:
                case Windows.System.VirtualKey.Right when canvasFocused && !ctrl:
                case Windows.System.VirtualKey.Up when canvasFocused && !ctrl:
                case Windows.System.VirtualKey.Down when canvasFocused && !ctrl:
                    double dirX = e.Key == Windows.System.VirtualKey.Left ? -1 : e.Key == Windows.System.VirtualKey.Right ? 1 : 0;
                    double dirY = e.Key == Windows.System.VirtualKey.Up ? -1 : e.Key == Windows.System.VirtualKey.Down ? 1 : 0;
                    if (alt)
                    {
                        double step = shift ? 40.0 : 10.0;
                        if (ViewModel.NudgeSelectedNode(dirX * step, dirY * step))
                            RequestRedraw(RedrawScope.Geometry | RedrawScope.Appearance);
                    }
                    else
                    {
                        if (ViewModel.NavigateDirection(dirX, dirY) is { } nextNode)
                            RequestRedraw(RedrawScope.Appearance);
                    }
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.F3:
                    ViewModel.FocusNextMatch(!shift);
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.F2 when canvasFocused && ViewModel.SelectedNode != null:
                    // Rename, as in Explorer: straight into the inspector's title with it selected.
                    TitleBox.Focus(FocusState.Programmatic);
                    TitleBox.SelectAll();
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.F2 when canvasFocused && ViewModel.SelectedLink != null:
                    LinkLabelBox.Focus(FocusState.Programmatic);
                    LinkLabelBox.SelectAll();
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.Application when canvasFocused:
                case Windows.System.VirtualKey.F10 when canvasFocused && shift:
                    if (ViewModel.SelectedNode is { } selNode && _nodeVisuals.TryGetValue(selNode.Id, out var vis) && vis.Root.ContextFlyout is MenuFlyout nodeFlyout)
                    {
                        nodeFlyout.ShowAt(vis.Root, new Point(vis.Root.ActualWidth / 2, vis.Root.ActualHeight / 2));
                    }
                    else if (ViewModel.SelectedLink is { } selLink)
                    {
                        BuildLinkContextMenu(selLink).ShowAt(GalaxyCanvas, new Point(CanvasWidth / 2.0, CanvasHeight / 2.0));
                    }
                    else
                    {
                        ShowCanvasContextMenu(new Point(CanvasWidth / 2.0, CanvasHeight / 2.0), -ViewModel.ViewportOffsetX, -ViewModel.ViewportOffsetY);
                    }
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

        private void OnFocusToggleRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var target = sender as FrameworkElement ?? GalaxyCanvas;
            e.Handled = true;
            var menu = new MenuFlyout();

            var toggleFocusItem = new MenuFlyoutItem
            {
                Text = ViewModel.IsFocusModeEnabled ? "Exit focus mode (F)" : "Enter focus mode (F)",
                Icon = new FontIcon { Glyph = "\uE890" }
            };
            toggleFocusItem.Click += (_, _) => ViewModel.ToggleFocusModeCommand.Execute(null);
            menu.Items.Add(toggleFocusItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var focusSelectedItem = new MenuFlyoutItem
            {
                Text = "Focus on selected node's connections",
                Icon = new FontIcon { Glyph = "\uE7B3" },
                IsEnabled = ViewModel.SelectedNode != null
            };
            focusSelectedItem.Click += (_, _) =>
            {
                if (ViewModel.SelectedNode is { } node)
                {
                    ViewModel.IsFocusModeEnabled = true;
                    ViewModel.CenterOn(node);
                }
            };
            menu.Items.Add(focusSelectedItem);

            var clearSelectionItem = new MenuFlyoutItem
            {
                Text = "Clear selection (Esc)",
                Icon = new FontIcon { Glyph = "\uE894" },
                IsEnabled = ViewModel.HasSelection
            };
            clearSelectionItem.Click += (_, _) =>
            {
                ViewModel.SelectedNode = null;
                ViewModel.SelectedLink = null;
                ViewModel.HidePreviewCard();
            };
            menu.Items.Add(clearSelectionItem);

            var fitWindowItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window (Ctrl+0)",
                Icon = new FontIcon { Glyph = "\uE9A6" }
            };
            fitWindowItem.Click += (_, _) => FitToWindow();
            menu.Items.Add(fitWindowItem);

            menu.ShowAt(target, e.GetPosition(target));
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

        private async Task CopyNodeTitleAsync(MindMapNodeViewModel node)
        {
            var error = await MarkSmith.Services.ClipboardWriter.TrySetTextAsync(node.Title);
            ViewModel.StatusMessage = error is null
                ? $"Copied “{node.Title}” to clipboard."
                : $"Couldn't copy: {error}";
        }

        private void OnCopyMermaidFlowchartClick(object sender, RoutedEventArgs e)
            => CopyMermaid(asFlowchart: true, "flowchart");

        private void OnCopyMermaidMindmapClick(object sender, RoutedEventArgs e)
            => CopyMermaid(asFlowchart: false, "mind map");

        private async void CopyMermaid(bool asFlowchart, string label)
        {
            try
            {
                var error = await MarkSmith.Services.ClipboardWriter.TrySetTextAsync(ViewModel.ExportToMermaid(asFlowchart));
                if (error is null)
                {
                    ViewModel.StatusMessage = $"Copied the map as a Mermaid {label}. Paste it into any document.";
                    if (ExportDropDownButton is not null)
                    {
                        MarkSmith.Services.CopyFeedback.ApplyButtonTick(ExportDropDownButton, null);
                    }
                }
                else
                {
                    ViewModel.StatusMessage = $"The map wasn't copied. {error}";
                }
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Couldn't build the Mermaid {label}: {ex.Message}";
            }
        }

        private void OnExportDropDownRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (ExportDropDownButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var docxItem = new MenuFlyoutItem
            {
                Text = "Export as Word document…",
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            docxItem.Click += OnExportDocxClick;
            menu.Items.Add(docxItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyFlowchartItem = new MenuFlyoutItem
            {
                Text = "Copy as Mermaid flowchart (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyFlowchartItem.Click += OnCopyMermaidFlowchartClick;
            menu.Items.Add(copyFlowchartItem);

            var copyMindmapItem = new MenuFlyoutItem
            {
                Text = "Copy as Mermaid mind map",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyMindmapItem.Click += OnCopyMermaidMindmapClick;
            menu.Items.Add(copyMindmapItem);

            var saveCopyItem = new MenuFlyoutItem
            {
                Text = "Save a copy of the map…",
                Icon = new FontIcon { Glyph = "\uEA35" }
            };
            saveCopyItem.Click += OnSaveAsClick;
            menu.Items.Add(saveCopyItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyTitleItem = new MenuFlyoutItem
            {
                Text = "Copy map title",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyTitleItem.Click += (_, _) =>
            {
                _ = MarkSmith.Services.ClipboardWriter.TrySetTextAsync(ViewModel.Title ?? "Document Galaxy");
                ViewModel.StatusMessage = "Copied map title to clipboard";
            };
            menu.Items.Add(copyTitleItem);

            var copySummaryItem = new MenuFlyoutItem
            {
                Text = "Copy map summary",
                Icon = new FontIcon { Glyph = "\uE946" }
            };
            copySummaryItem.Click += (_, _) =>
            {
                var summary = $"{ViewModel.Title} · {ViewModel.NodesCountText} · {ViewModel.LinksCountText}";
                _ = MarkSmith.Services.ClipboardWriter.TrySetTextAsync(summary);
                ViewModel.StatusMessage = "Copied map summary to clipboard";
            };
            menu.Items.Add(copySummaryItem);

            menu.ShowAt(ExportDropDownButton, e.GetPosition(ExportDropDownButton));
        }

        private void OnArrangeDropDownRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (ArrangeDropDownButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var treeItem = new MenuFlyoutItem
            {
                Text = "Tree layout, left to right",
                Icon = new FontIcon { Glyph = "\uE81E" }
            };
            treeItem.Click += OnLayoutTreeClick;
            menu.Items.Add(treeItem);

            var hierItem = new MenuFlyoutItem
            {
                Text = "Hierarchy layout, top down",
                Icon = new FontIcon { Glyph = "\uF003" }
            };
            hierItem.Click += OnLayoutHierarchyClick;
            menu.Items.Add(hierItem);

            var radialItem = new MenuFlyoutItem
            {
                Text = "Radial, around the root",
                Icon = new FontIcon { Glyph = "\uE707" }
            };
            radialItem.Click += OnLayoutRadialClick;
            menu.Items.Add(radialItem);

            var clusterItem = new MenuFlyoutItem
            {
                Text = "Clusters, by tag and link",
                Icon = new FontIcon { Glyph = "\uF0E2" }
            };
            clusterItem.Click += OnLayoutClustersClick;
            menu.Items.Add(clusterItem);

            var forceItem = new MenuFlyoutItem
            {
                Text = "Force-directed web",
                Icon = new FontIcon { Glyph = "\uEC26" }
            };
            forceItem.Click += OnLayoutForceClick;
            menu.Items.Add(forceItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var fitItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window (Ctrl+0)",
                Icon = new FontIcon { Glyph = "\uE9A6" }
            };
            fitItem.Click += (s, args) => OnFitToWindowClick(this, new RoutedEventArgs());
            menu.Items.Add(fitItem);

            menu.ShowAt(ArrangeDropDownButton, e.GetPosition(ArrangeDropDownButton));
        }

        private void OnImportFolderButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (ImportFolderButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var importItem = new MenuFlyoutItem
            {
                Text = "Import folder…",
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            importItem.Click += OnImportFolderClick;
            menu.Items.Add(importItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var fitItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window (Ctrl+0)",
                Icon = new FontIcon { Glyph = "\uE9A6" }
            };
            fitItem.Click += (s, args) => OnFitToWindowClick(this, new RoutedEventArgs());
            menu.Items.Add(fitItem);

            var clearItem = new MenuFlyoutItem
            {
                Text = "Clear selection (Esc)",
                Icon = new FontIcon { Glyph = "\uE894" },
                IsEnabled = ViewModel.HasSelection
            };
            clearItem.Click += (s, args) =>
            {
                ViewModel.SelectedNode = null;
                ViewModel.SelectedLink = null;
                ViewModel.HidePreviewCard();
            };
            menu.Items.Add(clearItem);

            menu.ShowAt(ImportFolderButton, e.GetPosition(ImportFolderButton));
        }

        private void OnSaveMapButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (SaveMapButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var saveItem = new MenuFlyoutItem
            {
                Text = "Save map (Ctrl+S)",
                Icon = new FontIcon { Glyph = "\uE74E" }
            };
            saveItem.Click += (s, args) => OnSaveClick(this, new RoutedEventArgs());
            menu.Items.Add(saveItem);

            var saveAsItem = new MenuFlyoutItem
            {
                Text = "Save a copy of the map… (Ctrl+Shift+S)",
                Icon = new FontIcon { Glyph = "\uEA35" }
            };
            saveAsItem.Click += (s, args) => OnSaveAsClick(this, new RoutedEventArgs());
            menu.Items.Add(saveAsItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyFlowchartItem = new MenuFlyoutItem
            {
                Text = "Copy as Mermaid flowchart (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyFlowchartItem.Click += OnCopyMermaidFlowchartClick;
            menu.Items.Add(copyFlowchartItem);

            var copyMindmapItem = new MenuFlyoutItem
            {
                Text = "Copy as Mermaid mind map",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyMindmapItem.Click += OnCopyMermaidMindmapClick;
            menu.Items.Add(copyMindmapItem);

            var exportDocxItem = new MenuFlyoutItem
            {
                Text = "Export Word document…",
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            exportDocxItem.Click += OnExportDocxClick;
            menu.Items.Add(exportDocxItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyTitleItem = new MenuFlyoutItem
            {
                Text = "Copy map title",
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            copyTitleItem.Click += (s, args) =>
            {
                var title = ViewModel.Title;
                _ = MarkSmith.Services.ClipboardWriter.TrySetTextAsync(title);
                ViewModel.StatusMessage = $"Copied map title: {title}";
            };
            menu.Items.Add(copyTitleItem);

            menu.ShowAt(SaveMapButton, e.GetPosition(SaveMapButton));
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
            UpdateTagPills();
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

        private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                SearchBox.Text = "";
                ViewModel.SearchQuery = "";
                e.Handled = true;
                GalaxyCanvas.Focus(FocusState.Programmatic);
            }
            else if (e.Key == Windows.System.VirtualKey.Down)
            {
                e.Handled = true;
                GalaxyCanvas.Focus(FocusState.Programmatic);
            }
            else if (e.Key == Windows.System.VirtualKey.F3)
            {
                e.Handled = true;
                ViewModel.FocusNextMatch(!IsShiftDown());
            }
        }

        private void OnSearchBoxRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();

            var nextItem = new MenuFlyoutItem
            {
                Text = "Next match (Enter / F3)",
                Icon = new FontIcon { Glyph = "\uE70D" },
                IsEnabled = ViewModel.HasSearchQuery
            };
            nextItem.Click += (_, _) => ViewModel.FocusNextMatch(forward: true);
            menu.Items.Add(nextItem);

            var prevItem = new MenuFlyoutItem
            {
                Text = "Previous match (Shift+F3)",
                Icon = new FontIcon { Glyph = "\uE70E" },
                IsEnabled = ViewModel.HasSearchQuery
            };
            prevItem.Click += (_, _) => ViewModel.FocusNextMatch(forward: false);
            menu.Items.Add(prevItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var clearItem = new MenuFlyoutItem
            {
                Text = "Clear search (Esc)",
                Icon = new FontIcon { Glyph = "\uE894" },
                IsEnabled = !string.IsNullOrEmpty(SearchBox.Text)
            };
            clearItem.Click += (_, _) =>
            {
                SearchBox.Text = "";
                ViewModel.SearchQuery = "";
                GalaxyCanvas.Focus(FocusState.Programmatic);
            };
            menu.Items.Add(clearItem);

            var focusSearchItem = new MenuFlyoutItem
            {
                Text = "Focus search box",
                Icon = new FontIcon { Glyph = "\uE721" }
            };
            focusSearchItem.Click += (_, _) => SearchBox.Focus(FocusState.Programmatic);
            menu.Items.Add(focusSearchItem);

            var returnCanvasItem = new MenuFlyoutItem
            {
                Text = "Return to galaxy map (Esc)",
                Icon = new FontIcon { Glyph = "\uE790" }
            };
            returnCanvasItem.Click += (_, _) => GalaxyCanvas.Focus(FocusState.Programmatic);
            menu.Items.Add(returnCanvasItem);

            menu.ShowAt(SearchBox, e.GetPosition(SearchBox));
        }

        private void OnZoomResetRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var anchor = sender as FrameworkElement ?? (FrameworkElement)GalaxyCanvas;
            var menu = new MenuFlyout();

            var fitItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window (Ctrl+0)",
                Icon = new FontIcon { Glyph = "\uE9A6" }
            };
            fitItem.Click += (_, _) => OnFitToWindowClick(this, new RoutedEventArgs());
            menu.Items.Add(fitItem);

            var actualItem = new MenuFlyoutItem
            {
                Text = "Reset zoom to 100% (Ctrl+1)",
                Icon = new FontIcon { Glyph = "\uE71E" }
            };
            actualItem.Click += (_, _) => OnZoomResetClick(this, new RoutedEventArgs());
            menu.Items.Add(actualItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var stops = new (string label, double factor)[]
            {
                ("25%", 0.25),
                ("50%", 0.50),
                ("75%", 0.75),
                ("100%", 1.00),
                ("125%", 1.25),
                ("150%", 1.50),
                ("200%", 2.00)
            };

            foreach (var (lbl, factor) in stops)
            {
                var item = new MenuFlyoutItem
                {
                    Text = lbl,
                    Icon = Math.Abs(ViewModel.ZoomLevel - factor) < 0.02 ? new FontIcon { Glyph = "\uE73E" } : null
                };
                double target = factor;
                item.Click += (_, _) =>
                {
                    ZoomAbout(new Point(CanvasWidth / 2.0, CanvasHeight / 2.0), target / Math.Max(ViewModel.ZoomLevel, 0.0001));
                };
                menu.Items.Add(item);
            }

            menu.ShowAt(anchor, e.GetPosition(anchor));
        }

        private void OnZoomResetDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            OnZoomResetClick(sender, new RoutedEventArgs());
        }

        private void OnSearchMatchLabelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var matchText = SearchMatchLabel?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(matchText)) return;
            e.Handled = true;
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(matchText);
            ClipboardWriter.Set(dp);
            ViewModel.StatusMessage = $"Copied search match count: {matchText}";
        }

        private void OnSearchMatchLabelRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (SearchMatchLabel is null) return;
            e.Handled = true;
            var matchText = SearchMatchLabel.Text ?? string.Empty;
            var menu = new MenuFlyout();

            var copyMatchItem = new MenuFlyoutItem
            {
                Text = !string.IsNullOrWhiteSpace(matchText) ? $"Copy match count ({matchText})" : "Copy match count",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrWhiteSpace(matchText)
            };
            copyMatchItem.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(matchText))
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(matchText);
                    ClipboardWriter.Set(dp);
                    ViewModel.StatusMessage = $"Copied search match count: {matchText}";
                }
            };
            menu.Items.Add(copyMatchItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var nextItem = new MenuFlyoutItem
            {
                Text = "Next match (Enter / F3)",
                Icon = new FontIcon { Glyph = "\uE70D" },
                IsEnabled = ViewModel.HasSearchQuery
            };
            nextItem.Click += (_, _) => ViewModel.FocusNextMatch(forward: true);
            menu.Items.Add(nextItem);

            var prevItem = new MenuFlyoutItem
            {
                Text = "Previous match (Shift+F3)",
                Icon = new FontIcon { Glyph = "\uE70E" },
                IsEnabled = ViewModel.HasSearchQuery
            };
            prevItem.Click += (_, _) => ViewModel.FocusNextMatch(forward: false);
            menu.Items.Add(prevItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var clearItem = new MenuFlyoutItem
            {
                Text = "Clear search (Esc)",
                Icon = new FontIcon { Glyph = "\uE894" },
                IsEnabled = !string.IsNullOrEmpty(SearchBox?.Text)
            };
            clearItem.Click += (_, _) =>
            {
                if (SearchBox != null)
                {
                    SearchBox.Text = "";
                    ViewModel.SearchQuery = "";
                    GalaxyCanvas.Focus(FocusState.Programmatic);
                }
            };
            menu.Items.Add(clearItem);

            var focusSearchItem = new MenuFlyoutItem
            {
                Text = "Focus search box (Ctrl+F)",
                Icon = new FontIcon { Glyph = "\uE721" }
            };
            focusSearchItem.Click += (_, _) => SearchBox?.Focus(FocusState.Programmatic);
            menu.Items.Add(focusSearchItem);

            menu.ShowAt(SearchMatchLabel, e.GetPosition(SearchMatchLabel));
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

        private void OnAllTagsPillRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var menu = new MenuFlyout();

            var allItem = new MenuFlyoutItem
            {
                Text = "Show all nodes (Clear tag filter)",
                Icon = new FontIcon { Glyph = "\uE8EC" }
            };
            allItem.Click += (_, _) => ViewModel.SelectedTagFilter = null;
            menu.Items.Add(allItem);

            var copyTagsItem = new MenuFlyoutItem
            {
                Text = "Copy all tag names",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyTagsItem.Click += (_, _) =>
            {
                var tags = string.Join(", ", ViewModel.DistinctTags);
                _ = Services.ClipboardWriter.TrySetTextAsync(tags);
                ViewModel.StatusMessage = "Copied all tags to clipboard";
            };
            menu.Items.Add(copyTagsItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var fitItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window (Ctrl+0)",
                Icon = new FontIcon { Glyph = "\uE9A6" }
            };
            fitItem.Click += (_, _) => OnFitToWindowClick(this, new RoutedEventArgs());
            menu.Items.Add(fitItem);

            menu.ShowAt(AllTagsPill, e.GetPosition(AllTagsPill));
            e.Handled = true;
        }

        private void OnDynamicTagPillRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;
            var menu = new MenuFlyout();

            var filterItem = new MenuFlyoutItem
            {
                Text = $"Filter to tag '{tag}'",
                Icon = new FontIcon { Glyph = "\uE71C" }
            };
            filterItem.Click += (_, _) => ViewModel.SelectedTagFilter = tag;
            menu.Items.Add(filterItem);

            if (ViewModel.SelectedTagFilter != null)
            {
                var clearFilterItem = new MenuFlyoutItem
                {
                    Text = "Clear active tag filter (Esc)",
                    Icon = new FontIcon { Glyph = "\uE711" }
                };
                clearFilterItem.Click += (_, _) => ViewModel.SelectedTagFilter = null;
                menu.Items.Add(clearFilterItem);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyTagItem = new MenuFlyoutItem
            {
                Text = $"Copy tag '{tag}'",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyTagItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(tag);
                ViewModel.StatusMessage = $"Copied tag '{tag}' to clipboard";
            };
            menu.Items.Add(copyTagItem);

            if (ViewModel.SelectedNode is { } selNode)
            {
                bool hasTag = selNode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
                if (!hasTag)
                {
                    var addTagItem = new MenuFlyoutItem
                    {
                        Text = $"Add '{tag}' to selected node",
                        Icon = new FontIcon { Glyph = "\uE710" }
                    };
                    addTagItem.Click += (_, _) =>
                    {
                        selNode.Tags.Add(tag);
                        ViewModel.StatusMessage = $"Added tag '{tag}' to '{selNode.Title}'";
                    };
                    menu.Items.Add(addTagItem);
                }
                else
                {
                    var removeTagItem = new MenuFlyoutItem
                    {
                        Text = $"Remove '{tag}' from selected node",
                        Icon = new FontIcon { Glyph = "\uE74D" }
                    };
                    removeTagItem.Click += (_, _) =>
                    {
                        selNode.Tags.Remove(tag);
                        ViewModel.StatusMessage = $"Removed tag '{tag}' from '{selNode.Title}'";
                    };
                    menu.Items.Add(removeTagItem);
                }
            }

            menu.ShowAt(btn, e.GetPosition(btn));
            e.Handled = true;
        }

        private void OnTagPillsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (ViewModel.SelectedTagFilter is not null)
                {
                    ViewModel.SelectedTagFilter = null;
                    AllTagsPill?.Focus(FocusState.Programmatic);
                    e.Handled = true;
                }
            }
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
            foreach (var b in _tagPills)
            {
                bool on = b == AllTagsPill
                    ? string.IsNullOrEmpty(active)
                    : b.Tag is string tag && string.Equals(tag, active, StringComparison.OrdinalIgnoreCase);
                // Inactive pills go back to the button's own (theme-aware) look, so they follow the
                // canvas theme; app-level brushes would stay dark-theme on a light canvas.
                if (on)
                {
                    ThemeBrush.Set(b, Control.BackgroundProperty, "AccentFillColorDefaultBrush");
                    ThemeBrush.Set(b, Control.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
                    b.BorderThickness = new Thickness(0);
                }
                else
                {
                    ThemeBrush.Clear(b, Control.BackgroundProperty);
                    ThemeBrush.Clear(b, Control.ForegroundProperty);
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

        private void OnPreviewOverlayCardRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();

            var openItem = new MenuFlyoutItem
            {
                Text = "Open in editor",
                Icon = new FontIcon { Glyph = "\uE8A7" },
                IsEnabled = ViewModel.PreviewHasFile
            };
            openItem.Click += OnOpenInEditorClick;
            menu.Items.Add(openItem);

            var historyItem = new MenuFlyoutItem
            {
                Text = "Version history",
                Icon = new FontIcon { Glyph = "\uE81C" },
                IsEnabled = ViewModel.PreviewHasFile
            };
            historyItem.Click += OnPreviewVersionHistoryClick;
            menu.Items.Add(historyItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyTitleItem = new MenuFlyoutItem
            {
                Text = "Copy preview title",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !string.IsNullOrEmpty(ViewModel.PreviewTitle)
            };
            copyTitleItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(ViewModel.PreviewTitle ?? "");
                ViewModel.StatusMessage = "Copied preview title to clipboard";
            };
            menu.Items.Add(copyTitleItem);

            var copyMarkdownItem = new MenuFlyoutItem
            {
                Text = "Copy preview Markdown",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrEmpty(ViewModel.PreviewMarkdown)
            };
            copyMarkdownItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(ViewModel.PreviewMarkdown ?? "");
                ViewModel.StatusMessage = "Copied preview Markdown to clipboard";
            };
            menu.Items.Add(copyMarkdownItem);

            if (!string.IsNullOrEmpty(ViewModel.PreviewFilePath))
            {
                var copyPathItem = new MenuFlyoutItem
                {
                    Text = "Copy file path",
                    Icon = new FontIcon { Glyph = "\uE8C8" }
                };
                copyPathItem.Click += (_, _) =>
                {
                    _ = Services.ClipboardWriter.TrySetTextAsync(ViewModel.PreviewFilePath ?? "");
                    ViewModel.StatusMessage = "Copied file path to clipboard";
                };
                menu.Items.Add(copyPathItem);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            var closeItem = new MenuFlyoutItem
            {
                Text = "Close preview card",
                Icon = new FontIcon { Glyph = "\uE711" },
                KeyboardAcceleratorTextOverride = "Esc"
            };
            closeItem.Click += OnClosePreviewCardClick;
            menu.Items.Add(closeItem);

            if (sender is FrameworkElement fe)
                menu.ShowAt(fe, e.GetPosition(fe));
            else if (PreviewOverlayCard is not null)
                menu.ShowAt(PreviewOverlayCard, e.GetPosition(PreviewOverlayCard));
            else
                menu.ShowAt(this.Content, e.GetPosition(this.Content));
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

        private void OnMinimapOverlayRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();

            var fitItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window",
                Icon = new FontIcon { Glyph = "\uE9A6" },
                KeyboardAcceleratorTextOverride = "Ctrl+0"
            };
            fitItem.Click += OnFitToWindowClick;
            menu.Items.Add(fitItem);

            var resetZoomItem = new MenuFlyoutItem
            {
                Text = "Reset zoom (100%)",
                Icon = new FontIcon { Glyph = "\uE71E" }
            };
            resetZoomItem.Click += OnZoomResetClick;
            menu.Items.Add(resetZoomItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var zoomInItem = new MenuFlyoutItem
            {
                Text = "Zoom in",
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            zoomInItem.Click += (_, _) => ZoomAbout(new Point(CanvasWidth / 2.0, CanvasHeight / 2.0), 1.15);
            menu.Items.Add(zoomInItem);

            var zoomOutItem = new MenuFlyoutItem
            {
                Text = "Zoom out",
                Icon = new FontIcon { Glyph = "\uE738" }
            };
            zoomOutItem.Click += OnZoomOutClick;
            menu.Items.Add(zoomOutItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var clearSelItem = new MenuFlyoutItem
            {
                Text = "Clear selection",
                Icon = new FontIcon { Glyph = "\uE894" },
                KeyboardAcceleratorTextOverride = "Esc",
                IsEnabled = ViewModel.HasSelection
            };
            clearSelItem.Click += (_, _) =>
            {
                ViewModel.SelectedNode = null;
                ViewModel.SelectedLink = null;
                ViewModel.HidePreviewCard();
            };
            menu.Items.Add(clearSelItem);

            if (sender is FrameworkElement fe)
                menu.ShowAt(fe, e.GetPosition(fe));
            else if (MinimapOverlay is not null)
                menu.ShowAt(MinimapOverlay, e.GetPosition(MinimapOverlay));
            else
                menu.ShowAt(this.Content, e.GetPosition(this.Content));
        }

        private void OnAddChildButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (AddChildToolButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var childItem = MenuItem("Add child node", "\uE710", "Tab");
            childItem.Click += (_, _) => ViewModel.AddChildNodeCommand.Execute(null);
            menu.Items.Add(childItem);

            var siblingItem = MenuItem("Add sibling node", "\uE710", "Enter");
            siblingItem.Click += (_, _) => ViewModel.AddSiblingNodeCommand.Execute(null);
            menu.Items.Add(siblingItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var linkItem = MenuItem("Link selected node", "\uE71B", "Ctrl+L");
            linkItem.IsEnabled = ViewModel.HasSelection;
            linkItem.Click += OnConnectSelectedClick;
            menu.Items.Add(linkItem);

            var renameItem = MenuItem("Rename selected node", "\uE8AC", "F2");
            renameItem.IsEnabled = ViewModel.SelectedNode != null;
            renameItem.Click += (_, _) =>
            {
                TitleBox?.Focus(FocusState.Programmatic);
                TitleBox?.SelectAll();
            };
            menu.Items.Add(renameItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var fitItem = MenuItem("Fit all nodes in window", "\uE9A6", "Ctrl+0");
            fitItem.Click += OnFitToWindowClick;
            menu.Items.Add(fitItem);

            menu.ShowAt(AddChildToolButton, e.GetPosition(AddChildToolButton));
        }

        private void OnAddSiblingButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (AddSiblingToolButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var siblingItem = MenuItem("Add sibling node", "\uE710", "Enter");
            siblingItem.Click += (_, _) => ViewModel.AddSiblingNodeCommand.Execute(null);
            menu.Items.Add(siblingItem);

            var childItem = MenuItem("Add child node", "\uE710", "Tab");
            childItem.Click += (_, _) => ViewModel.AddChildNodeCommand.Execute(null);
            menu.Items.Add(childItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var linkItem = MenuItem("Link selected node", "\uE71B", "Ctrl+L");
            linkItem.IsEnabled = ViewModel.HasSelection;
            linkItem.Click += OnConnectSelectedClick;
            menu.Items.Add(linkItem);

            var deleteItem = MenuItem("Delete selected", "\uE74D", "Delete");
            deleteItem.IsEnabled = ViewModel.HasSelection;
            deleteItem.Click += (_, _) => ViewModel.DeleteSelectionCommand.Execute(null);
            menu.Items.Add(deleteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var fitItem = MenuItem("Fit all nodes in window", "\uE9A6", "Ctrl+0");
            fitItem.Click += OnFitToWindowClick;
            menu.Items.Add(fitItem);

            menu.ShowAt(AddSiblingToolButton, e.GetPosition(AddSiblingToolButton));
        }

        private void OnLinkToolButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (LinkToolButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var linkItem = MenuItem("Link to another node", "\uE71B", "Ctrl+L");
            linkItem.IsEnabled = ViewModel.HasSelection;
            linkItem.Click += OnConnectSelectedClick;
            menu.Items.Add(linkItem);

            var clearSelItem = MenuItem("Clear selection", "\uE894", "Esc");
            clearSelItem.IsEnabled = ViewModel.HasSelection;
            clearSelItem.Click += (_, _) =>
            {
                ViewModel.SelectedNode = null;
                ViewModel.SelectedLink = null;
                ViewModel.HidePreviewCard();
            };
            menu.Items.Add(clearSelItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyMermaidItem = MenuItem("Copy as Mermaid mind map", "\uE8C8");
            copyMermaidItem.Click += OnCopyMermaidMindmapClick;
            menu.Items.Add(copyMermaidItem);

            var copyFlowchartItem = MenuItem("Copy as Mermaid flowchart", "\uE8C8", "Ctrl+Shift+C");
            copyFlowchartItem.Click += OnCopyMermaidFlowchartClick;
            menu.Items.Add(copyFlowchartItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var fitItem = MenuItem("Fit all nodes in window", "\uE9A6", "Ctrl+0");
            fitItem.Click += OnFitToWindowClick;
            menu.Items.Add(fitItem);

            menu.ShowAt(LinkToolButton, e.GetPosition(LinkToolButton));
        }

        private void OnDeleteToolButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (DeleteToolButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var deleteItem = MenuItem("Delete selection", "\uE74D", "Delete");
            deleteItem.IsEnabled = ViewModel.HasSelection;
            deleteItem.Click += (_, _) => ViewModel.DeleteSelectionCommand.Execute(null);
            menu.Items.Add(deleteItem);

            var clearSelItem = MenuItem("Clear selection", "\uE894", "Esc");
            clearSelItem.IsEnabled = ViewModel.HasSelection;
            clearSelItem.Click += (_, _) =>
            {
                ViewModel.SelectedNode = null;
                ViewModel.SelectedLink = null;
                ViewModel.HidePreviewCard();
            };
            menu.Items.Add(clearSelItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var undoItem = MenuItem("Undo", "\uE7A7", "Ctrl+Z");
            undoItem.IsEnabled = ViewModel.CanUndo;
            undoItem.Click += (_, _) => ViewModel.UndoCommand.Execute(null);
            menu.Items.Add(undoItem);

            var redoItem = MenuItem("Redo", "\uE7A6", "Ctrl+Y");
            redoItem.IsEnabled = ViewModel.CanRedo;
            redoItem.Click += (_, _) => ViewModel.RedoCommand.Execute(null);
            menu.Items.Add(redoItem);

            menu.ShowAt(DeleteToolButton, e.GetPosition(DeleteToolButton));
        }

        private void OnUndoToolButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (UndoToolButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var undoItem = MenuItem("Undo", "\uE7A7", "Ctrl+Z");
            undoItem.IsEnabled = ViewModel.CanUndo;
            undoItem.Click += (_, _) => ViewModel.UndoCommand.Execute(null);
            menu.Items.Add(undoItem);

            var redoItem = MenuItem("Redo", "\uE7A6", "Ctrl+Y");
            redoItem.IsEnabled = ViewModel.CanRedo;
            redoItem.Click += (_, _) => ViewModel.RedoCommand.Execute(null);
            menu.Items.Add(redoItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var fitItem = MenuItem("Fit all nodes in window", "\uE9A6", "Ctrl+0");
            fitItem.Click += OnFitToWindowClick;
            menu.Items.Add(fitItem);

            var resetZoomItem = MenuItem("Reset zoom (100%)", "\uE71E");
            resetZoomItem.Click += OnZoomResetClick;
            menu.Items.Add(resetZoomItem);

            menu.ShowAt(UndoToolButton, e.GetPosition(UndoToolButton));
        }

        private void OnRedoToolButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (RedoToolButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var redoItem = MenuItem("Redo", "\uE7A6", "Ctrl+Y");
            redoItem.IsEnabled = ViewModel.CanRedo;
            redoItem.Click += (_, _) => ViewModel.RedoCommand.Execute(null);
            menu.Items.Add(redoItem);

            var undoItem = MenuItem("Undo", "\uE7A7", "Ctrl+Z");
            undoItem.IsEnabled = ViewModel.CanUndo;
            undoItem.Click += (_, _) => ViewModel.UndoCommand.Execute(null);
            menu.Items.Add(undoItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var fitItem = MenuItem("Fit all nodes in window", "\uE9A6", "Ctrl+0");
            fitItem.Click += OnFitToWindowClick;
            menu.Items.Add(fitItem);

            var resetZoomItem = MenuItem("Reset zoom (100%)", "\uE71E");
            resetZoomItem.Click += OnZoomResetClick;
            menu.Items.Add(resetZoomItem);

            menu.ShowAt(RedoToolButton, e.GetPosition(RedoToolButton));
        }

        private void OnStatusBarDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            OnFitToWindowClick(this, new RoutedEventArgs());
        }

        private void OnStatusBarRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var target = sender as FrameworkElement ?? (FrameworkElement)GalaxyCanvas;
            ShowStatusBarContextMenu(target, e.GetPosition(target));
        }

        private void ShowStatusBarContextMenu(FrameworkElement target, Point? position)
        {
            var menu = new MenuFlyout();

            var fitItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window (Ctrl+0)",
                Icon = new FontIcon { Glyph = "\uE9A6" }
            };
            fitItem.Click += (_, _) => OnFitToWindowClick(this, new RoutedEventArgs());
            menu.Items.Add(fitItem);

            var actualItem = new MenuFlyoutItem
            {
                Text = "Reset zoom to 100% (Ctrl+1)",
                Icon = new FontIcon { Glyph = "\uE71E" }
            };
            actualItem.Click += (_, _) => OnZoomResetClick(this, new RoutedEventArgs());
            menu.Items.Add(actualItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyStatusItem = new MenuFlyoutItem
            {
                Text = "Copy status message",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyStatusItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(ViewModel.StatusMessage ?? "");
                ViewModel.StatusMessage = "Copied status message to clipboard";
            };
            menu.Items.Add(copyStatusItem);

            var copyTopologyItem = new MenuFlyoutItem
            {
                Text = "Copy map topology overview",
                Icon = new FontIcon { Glyph = "\uE946" }
            };
            copyTopologyItem.Click += (_, _) =>
            {
                var summary = $"{ViewModel.Title} — {ViewModel.NodesCountText}, {ViewModel.LinksCountText}";
                _ = Services.ClipboardWriter.TrySetTextAsync(summary);
                ViewModel.StatusMessage = "Copied map topology overview to clipboard";
            };
            menu.Items.Add(copyTopologyItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var saveItem = new MenuFlyoutItem
            {
                Text = "Save map (Ctrl+S)",
                Icon = new FontIcon { Glyph = "\uE74E" },
                IsEnabled = ViewModel.IsDirty
            };
            saveItem.Click += (_, _) => ViewModel.SaveCommand.Execute(null);
            menu.Items.Add(saveItem);

            if (position.HasValue) menu.ShowAt(target, position.Value);
            else menu.ShowAt(target);
        }

        private void OnNodesCountDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var text = ViewModel.NodesCountText;
            if (string.IsNullOrWhiteSpace(text)) return;
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            ClipboardWriter.Set(dp);
            ViewModel.StatusMessage = $"Copied node count: {text}";
        }

        private void OnNodesCountRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (NodesCountText is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var countText = ViewModel.NodesCountText ?? string.Empty;
            var copyCountItem = new MenuFlyoutItem
            {
                Text = !string.IsNullOrWhiteSpace(countText) ? $"Copy node count ({countText})" : "Copy node count",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrWhiteSpace(countText)
            };
            copyCountItem.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(countText))
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(countText);
                    ClipboardWriter.Set(dp);
                    ViewModel.StatusMessage = $"Copied node count: {countText}";
                }
            };
            menu.Items.Add(copyCountItem);

            var addNodeItem = new MenuFlyoutItem
            {
                Text = "Add new node (Insert / Tab)",
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            addNodeItem.Click += (_, _) => ViewModel.AddChildNodeCommand.Execute(null);
            menu.Items.Add(addNodeItem);

            var clearSelectionItem = new MenuFlyoutItem
            {
                Text = "Clear node selection (Esc)",
                Icon = new FontIcon { Glyph = "\uE894" },
                IsEnabled = ViewModel.HasSelection
            };
            clearSelectionItem.Click += (_, _) =>
            {
                ViewModel.SelectedNode = null;
                ViewModel.SelectedLink = null;
                ViewModel.HidePreviewCard();
            };
            menu.Items.Add(clearSelectionItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var searchItem = new MenuFlyoutItem
            {
                Text = "Search nodes (Ctrl+F)",
                Icon = new FontIcon { Glyph = "\uE721" }
            };
            searchItem.Click += (_, _) => SearchBox?.Focus(FocusState.Programmatic);
            menu.Items.Add(searchItem);

            var fitItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window (Ctrl+0)",
                Icon = new FontIcon { Glyph = "\uE9A6" }
            };
            fitItem.Click += (_, _) => OnFitToWindowClick(this, new RoutedEventArgs());
            menu.Items.Add(fitItem);

            menu.ShowAt(NodesCountText, e.GetPosition(NodesCountText));
        }

        private void OnLinksCountDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var text = ViewModel.LinksCountText;
            if (string.IsNullOrWhiteSpace(text)) return;
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            ClipboardWriter.Set(dp);
            ViewModel.StatusMessage = $"Copied connection count: {text}";
        }

        private void OnLinksCountRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (LinksCountText is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var countText = ViewModel.LinksCountText ?? string.Empty;
            var copyCountItem = new MenuFlyoutItem
            {
                Text = !string.IsNullOrWhiteSpace(countText) ? $"Copy connection count ({countText})" : "Copy connection count",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrWhiteSpace(countText)
            };
            copyCountItem.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(countText))
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(countText);
                    ClipboardWriter.Set(dp);
                    ViewModel.StatusMessage = $"Copied connection count: {countText}";
                }
            };
            menu.Items.Add(copyCountItem);

            var radialLayoutItem = new MenuFlyoutItem
            {
                Text = "Arrange as Radial Galaxy",
                Icon = new FontIcon { Glyph = "\uE8B1" }
            };
            radialLayoutItem.Click += (_, _) => ViewModel.ApplyLayout("Radial");
            menu.Items.Add(radialLayoutItem);

            var treeLayoutItem = new MenuFlyoutItem
            {
                Text = "Arrange as Horizontal Tree",
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            treeLayoutItem.Click += (_, _) => ViewModel.ApplyLayout("Horizontal");
            menu.Items.Add(treeLayoutItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var fitItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window (Ctrl+0)",
                Icon = new FontIcon { Glyph = "\uE9A6" }
            };
            fitItem.Click += (_, _) => OnFitToWindowClick(this, new RoutedEventArgs());
            menu.Items.Add(fitItem);

            menu.ShowAt(LinksCountText, e.GetPosition(LinksCountText));
        }

        private void OnMapTitleTextDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var title = ViewModel.Title ?? string.Empty;
            if (string.IsNullOrEmpty(title)) return;
            _ = Services.ClipboardWriter.TrySetTextAsync(title);
            ViewModel.StatusMessage = $"Copied map title: {title}";
        }

        private void OnMapTitleTextRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (MapTitleText is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var title = ViewModel.Title ?? string.Empty;
            var copyTitleItem = new MenuFlyoutItem
            {
                Text = !string.IsNullOrEmpty(title) ? $"Copy map title ({title})" : "Copy map title",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrEmpty(title)
            };
            copyTitleItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(title);
                ViewModel.StatusMessage = $"Copied map title: {title}";
            };
            menu.Items.Add(copyTitleItem);

            var summary = $"{title} · {ViewModel.NodesCountText} · {ViewModel.LinksCountText}";
            var copySummaryItem = new MenuFlyoutItem
            {
                Text = "Copy map summary",
                Icon = new FontIcon { Glyph = "\uE946" }
            };
            copySummaryItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(summary);
                ViewModel.StatusMessage = $"Copied map summary: {summary}";
            };
            menu.Items.Add(copySummaryItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var saveItem = new MenuFlyoutItem
            {
                Text = "Save map changes (Ctrl+S)",
                Icon = new FontIcon { Glyph = "\uE74E" }
            };
            saveItem.Click += (s, args) => OnSaveClick(this, new RoutedEventArgs());
            menu.Items.Add(saveItem);

            var fitItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window (Ctrl+0)",
                Icon = new FontIcon { Glyph = "\uE9A6" }
            };
            fitItem.Click += (_, _) => OnFitToWindowClick(this, new RoutedEventArgs());
            menu.Items.Add(fitItem);

            var addNodeItem = new MenuFlyoutItem
            {
                Text = "Add child node (Tab)",
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            addNodeItem.Click += (_, _) => ViewModel.AddChildNode();
            menu.Items.Add(addNodeItem);

            menu.ShowAt(MapTitleText, e.GetPosition(MapTitleText));
        }

        private async void OnUnsavedBadgeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            GalaxyCanvas.Focus(FocusState.Programmatic);
            await ViewModel.SaveAsync();
        }

        private void OnUnsavedBadgeRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (UnsavedBadge is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var saveItem = new MenuFlyoutItem
            {
                Text = "Save map changes (Ctrl+S)",
                Icon = new FontIcon { Glyph = "\uE74E" }
            };
            saveItem.Click += (s, args) => OnSaveClick(this, new RoutedEventArgs());
            menu.Items.Add(saveItem);

            var saveAsItem = new MenuFlyoutItem
            {
                Text = "Save copy as… (Ctrl+Shift+S)",
                Icon = new FontIcon { Glyph = "\uE792" }
            };
            saveAsItem.Click += (s, args) => OnSaveAsClick(this, new RoutedEventArgs());
            menu.Items.Add(saveAsItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var undoItem = new MenuFlyoutItem
            {
                Text = "Undo last change (Ctrl+Z)",
                Icon = new FontIcon { Glyph = "\uE7A7" },
                IsEnabled = ViewModel.CanUndo
            };
            undoItem.Click += (_, _) => ViewModel.Undo();
            menu.Items.Add(undoItem);

            var fitItem = new MenuFlyoutItem
            {
                Text = "Fit all nodes in window (Ctrl+0)",
                Icon = new FontIcon { Glyph = "\uE9A6" }
            };
            fitItem.Click += (_, _) => OnFitToWindowClick(this, new RoutedEventArgs());
            menu.Items.Add(fitItem);

            menu.ShowAt(UnsavedBadge, e.GetPosition(UnsavedBadge));
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

            public Color ProgressFill { get; init; } = ColorFromHex("#34D399");
            public Color ProgressInk { get; init; } = ColorFromHex("#34D399");
            public Color VersionFill { get; init; } = ColorFromHex("#1C2D42");
            public Color VersionInk { get; init; } = ColorFromHex("#7DD3FC");
            public Color MissingInk { get; init; } = ColorFromHex("#FCA5A5");
            public Color HighlightBorder { get; init; } = ColorFromHex("#22D3EE");

            /// <summary>A light canvas. The overlays on it (tour banner, tag bar, legend, preview
            /// card) switch to the light theme with it, or their dark-theme text is white on white.</summary>
            public bool IsLight { get; init; }

            public static readonly GalaxyPalette MidnightGalaxy = ForName(MindMapStudioViewModel.DefaultThemeName);

            public static GalaxyPalette ForName(string? name)
            {
                var spec = MindMapStudioViewModel.GetThemePalette(name);
                bool isLight = string.Equals(name, "Clean White", StringComparison.Ordinal);
                Color minimapBg = name switch
                {
                    "Clean White" => ColorFromHex("#E0F1F5FA"),
                    "Nordic Slate" => ColorFromHex("#E0172033"),
                    "Obsidian Dark" => ColorFromHex("#E00B0C10"),
                    "Cyberpunk Neon" => ColorFromHex("#E0140A28"),
                    _ => ColorFromHex("#E0161722")
                };
                return new GalaxyPalette(
                    ColorFromHex(spec.Bg),
                    ColorFromHex(spec.Card),
                    ColorFromHex(spec.Ink),
                    ColorFromHex(spec.SubInk),
                    spec.Edge,
                    minimapBg)
                {
                    SelectionInk = isLight ? ColorFromHex("#0F172A") : Colors.White,
                    HubInk = isLight ? ColorFromHex("#B45309") : ColorFromHex("#FBBF24"),
                    Track = isLight ? ColorFromHex("#1F0F172A") : ColorFromHex("#33FFFFFF"),
                    ProgressFill = ColorFromHex(spec.ProgressFill),
                    ProgressInk = ColorFromHex(spec.ProgressInk),
                    VersionFill = ColorFromHex(spec.VersionFill),
                    VersionInk = ColorFromHex(spec.VersionInk),
                    MissingInk = ColorFromHex(spec.MissingInk),
                    HighlightBorder = ColorFromHex(spec.HighlightBorder),
                    IsLight = isLight
                };
            }
        }
    }
}
