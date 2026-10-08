using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.System;
using MarkSmith.Core.Composer;
using MarkSmith.Services;
using MarkSmith.ViewModels.ShapeStudio;

namespace MarkSmith.Views.ShapeStudio
{
    public sealed partial class ShapeDesignStudioWindow : Window
    {
        /// <summary>One row of the colour-scheme flyout: the name and its swatches.</summary>
        public sealed class PaletteChoice
        {
            public string Name { get; init; } = "";
            public List<SolidColorBrush> Swatches { get; init; } = new();
        }

        public ShapeDesignStudioViewModel ViewModel { get; }
        public event EventHandler<string>? InsertToDocumentRequested;

        // Shape drag (moves the whole selection). The pointer's total travel is compared with what
        // the group actually moved, so a drag held against the canvas edge doesn't leave the shapes
        // trailing behind the pointer once it comes back.
        private ShapeCanvasItemViewModel? _dragShape;
        private Point _dragStart;
        private bool _dragMoved;
        private double _dragAppliedX, _dragAppliedY;

        // Rubber-band selection on empty canvas
        private bool _marquee;
        private bool _marqueeAdditive;
        private Point _marqueeStart;

        // Resize handles
        private ShapeCanvasItemViewModel? _adornedShape;
        private readonly List<(Microsoft.UI.Xaml.Shapes.Rectangle Handle, ShapeDesignStudioViewModel.ResizeEdges Edges)> _handles = new();
        private ShapeDesignStudioViewModel.ResizeEdges _resizeEdges;
        private (double X, double Y, double W, double H) _resizeFrom;
        private Point _resizeStart;
        private bool _resized;

        // Drag-to-draw with an armed tool
        private bool _drawing;
        private Point _drawStart;

        private ShapeCanvasItemViewModel? _hoverShape;
        private ShapeCanvasItemViewModel? _inspectorUndoFor;
        private readonly List<PaletteChoice> _paletteChoices = new();
        private bool _syncingPalette;
        private string? _armedStatus;

        public ShapeDesignStudioWindow()
        {
            this.InitializeComponent();
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            SizeForDisplay();
            ViewModel = new ShapeDesignStudioViewModel();
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            TitleBarInsets.Reserve(this, AppTitleBar); // keep the action buttons clear of min/max/close
            this.RootGrid.DataContext = ViewModel;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.InsertToDocumentRequested += (s, block) => InsertToDocumentRequested?.Invoke(this, block);
            HoverPolish.Track(this.RootGrid);
            this.RootGrid.PreviewKeyDown += OnRootPreviewKeyDown;
            InspectorPanel.GettingFocus += OnInspectorGettingFocus;
            BuildPaletteChoices();
            BuildResizeHandles();
            MarqueeRect.Fill = new SolidColorBrush(AccentColor(0x33));
            SetCursor(MainCanvas, Microsoft.UI.Input.InputSystemCursorShape.Arrow);
            // Open with the canvas focused (no focus ring) so Ctrl+Z / Del work at once and the
            // first title-bar button doesn't come up wearing a keyboard-focus rectangle.
            this.RootGrid.Loaded += (_, _) => CanvasScroller.Focus(FocusState.Programmatic);
        }

        // Sized in DIPs (AppWindow sizes are physical pixels, so the default opened cramped on a
        // scaled display) with a floor that keeps the toolbar, both side panes and a usable canvas
        // on screen. Without one the window could be squeezed until the canvas vanished.
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
                    presenter.PreferredMinimumWidth = Math.Min((int)(1180 * scale), work.Width);
                    presenter.PreferredMinimumHeight = Math.Min((int)(620 * scale), work.Height);
                }
            }
            catch { /* best effort: the default size still works */ }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        // The colour-scheme button drops its name when the toolbar is too narrow for it; it used to
        // slide over the Duplicate and Delete buttons instead.
        private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ToolStrip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bool roomy = e.NewSize.Width - 16 >= ToolStrip.DesiredSize.Width + 180;
            PaletteNameText.Visibility = roomy ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- zoom ----

        private static readonly double[] ZoomSteps = { 0.2, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.25, 1.5, 2, 3, 4 };

        private void OnCanvasViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            ZoomText.Text = $"{Math.Round(CanvasScroller.ZoomFactor * 100)}%";
            UpdateAdorner(); // handles stay the same size on screen at any zoom
        }

        /// <summary>Shows the whole diagram: zooms out (never in past 100%) until it fits the view.
        /// Presets are laid out up to ~720 px wide, so on a narrower canvas their right side used to
        /// open cut off with nothing on screen to say so.</summary>
        private void FitCanvasToContent()
        {
            double vw = CanvasScroller.ViewportWidth, vh = CanvasScroller.ViewportHeight;
            if (vw <= 0 || vh <= 0) return;
            if (ViewModel.Shapes.Count == 0 || ViewModel.IsDense)
            {
                CanvasScroller.ChangeView(0, 0, 1f);
                return;
            }
            double right = ViewModel.Shapes.Max(s => s.X + s.Width) + 24;
            double bottom = ViewModel.Shapes.Max(s => s.Y + s.Height) + 24;
            double zoom = Math.Clamp(Math.Min(vw / right, vh / bottom), CanvasScroller.MinZoomFactor, 1.0);
            CanvasScroller.ChangeView(0, 0, (float)zoom);
        }

        private void FitCanvasAfterLayout() => DispatcherQueue.TryEnqueue(FitCanvasToContent);

        private void ZoomTo(double factor)
        {
            double vw = CanvasScroller.ViewportWidth, vh = CanvasScroller.ViewportHeight, z = CanvasScroller.ZoomFactor;
            factor = Math.Clamp(factor, CanvasScroller.MinZoomFactor, CanvasScroller.MaxZoomFactor);
            // Keep the middle of the view where it is.
            double cx = (CanvasScroller.HorizontalOffset + vw / 2) / z, cy = (CanvasScroller.VerticalOffset + vh / 2) / z;
            CanvasScroller.ChangeView(Math.Max(0, cx * factor - vw / 2), Math.Max(0, cy * factor - vh / 2), (float)factor);
        }

        private void ZoomStep(int direction)
        {
            double z = CanvasScroller.ZoomFactor;
            double next = direction > 0
                ? ZoomSteps.FirstOrDefault(f => f > z + 0.001, ZoomSteps[^1])
                : ZoomSteps.LastOrDefault(f => f < z - 0.001, ZoomSteps[0]);
            ZoomTo(next);
        }

        private void OnZoomFitClick(object sender, RoutedEventArgs e) => FitCanvasToContent();
        private void OnZoomActualClick(object sender, RoutedEventArgs e) => ZoomTo(1);
        private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomStep(+1);
        private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomStep(-1);

        // ---- preset miniatures ----

        // Built once per preset and colour scheme on a throwaway studio; the list virtualises, so
        // only the rows scrolled into view ever pay for it.
        private static readonly Dictionary<string, IReadOnlyList<ShapeCanvasItemViewModel>> ThumbCache = new();
        private readonly HashSet<Canvas> _presetThumbs = new();

        private void OnPresetThumbLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Canvas c) return;
            _presetThumbs.Add(c);
            RenderPresetThumb(c);
        }

        private void OnPresetThumbUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Canvas c) _presetThumbs.Remove(c);
        }

        private void OnPresetThumbDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Canvas c) RenderPresetThumb(c);
        }

        private void RenderPresetThumb(Canvas c)
        {
            if (c.DataContext is not DiagramPreset preset || ViewModel is null) return;
            string palette = ViewModel.SelectedPaletteName ?? "Office Blue";
            string key = preset.Name + "|" + palette;
            if (c.Tag as string == key) return;
            c.Tag = key;
            c.Children.Clear();
            if (!ThumbCache.TryGetValue(key, out var shapes))
            {
                try { shapes = ShapeDesignStudioViewModel.PreviewPreset(preset, palette); }
                catch { shapes = Array.Empty<ShapeCanvasItemViewModel>(); }
                ThumbCache[key] = shapes;
            }
            if (shapes.Count == 0) return;

            double minX = shapes.Min(s => s.X), minY = shapes.Min(s => s.Y);
            double width = Math.Max(1, shapes.Max(s => s.X + s.Width) - minX);
            double height = Math.Max(1, shapes.Max(s => s.Y + s.Height) - minY);
            c.Width = width;
            c.Height = height;
            // The miniature is drawn at roughly 1/12 scale; a connector's real 2 pt stroke would vanish.
            double hairline = Math.Max(width / 54, height / 36);
            foreach (var s in shapes)
            {
                var p = new Microsoft.UI.Xaml.Shapes.Path { Width = Math.Max(1, s.Width), Height = Math.Max(1, s.Height), IsHitTestVisible = false };
                if (s.PathPoints is { Count: >= 2 })
                {
                    p.Stretch = Stretch.None;
                    p.Data = BuildPolylineGeometry(s.PathPoints, s.Width, s.Height);
                    p.Stroke = BrushFromHex(s.Fill);
                    p.StrokeThickness = Math.Max(s.StrokeWidthPt, hairline);
                }
                else
                {
                    var outline = PresetGeometry.Outline(s.Prst, s.Width, s.Height);
                    bool roundRect = string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase);
                    p.Stretch = outline is not null || roundRect ? Stretch.None : Stretch.Fill;
                    p.Data = outline is not null ? MakePolygonGeometry(outline)
                           : roundRect ? BuildRoundRectGeometry(s.Width, s.Height)
                           : MarkSmith.Converters.ShapeGeometries.For(s.Prst);
                    p.Fill = BrushFromHex(s.Fill);
                }
                if (s.Rotation != 0)
                {
                    p.RenderTransformOrigin = new Point(0.5, 0.5);
                    p.RenderTransform = new RotateTransform { Angle = s.Rotation };
                }
                Canvas.SetLeft(p, s.X - minX);
                Canvas.SetTop(p, s.Y - minY);
                c.Children.Add(p);
            }
        }

        // ---- left pane tabs ----

        private void OnLeftTabsChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            var selected = sender.SelectedItem;
            PresetsPanel.Visibility = selected == PresetsTab ? Visibility.Visible : Visibility.Collapsed;
            ShapesPanel.Visibility = selected == ShapesTab ? Visibility.Visible : Visibility.Collapsed;
            PicturePanel.Visibility = selected == PictureTab ? Visibility.Visible : Visibility.Collapsed;
            // Leaving the Shapes tab puts a half-chosen tool down.
            if (selected != ShapesTab) ViewModel.ArmedTool = null;
        }

        // ---- colour schemes ----

        private void BuildPaletteChoices()
        {
            foreach (var name in ViewModel.PaletteNames)
            {
                var colours = ShapeDesignStudioViewModel.ColorPalettes.TryGetValue(name, out var c) ? c : Array.Empty<string>();
                _paletteChoices.Add(new PaletteChoice { Name = name, Swatches = colours.Select(BrushFromHex).ToList() });
            }
            PaletteList.ItemsSource = _paletteChoices;
            SyncPaletteUi();
        }

        private void SyncPaletteUi()
        {
            _syncingPalette = true;
            try
            {
                PaletteList.SelectedItem = _paletteChoices.FirstOrDefault(p => p.Name == ViewModel.SelectedPaletteName);
                PaletteButtonSwatches.Children.Clear();
                var choice = PaletteList.SelectedItem as PaletteChoice;
                foreach (var b in (choice?.Swatches ?? new List<SolidColorBrush>()).Take(4))
                {
                    PaletteButtonSwatches.Children.Add(new Border { Width = 8, Height = 14, CornerRadius = new CornerRadius(2), Background = b });
                }
            }
            finally { _syncingPalette = false; }
        }

        private void OnPaletteListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingPalette || PaletteList.SelectedItem is not PaletteChoice choice) return;
            ViewModel.SelectedPaletteName = choice.Name;
            if (ViewModel.HasShapes && !ViewModel.IsDense) ViewModel.ApplyPaletteThemeCommand.Execute(null);
            else ViewModel.StatusMessage = $"Colour scheme: {choice.Name} — presets and new shapes will use it.";
            PaletteFlyout.Hide();
        }

        // ---- keyboard ----
        // Del delete · Ctrl+D duplicate · Ctrl+A select all · Ctrl+Z / Ctrl+Y undo/redo · Esc cancels the
        // armed tool, then the selection · arrows nudge 1 px (Shift: 10 px) while the canvas has focus.
        // Text fields keep their own keys.
        private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var focused = FocusManager.GetFocusedElement(this.RootGrid.XamlRoot);
            if (focused is TextBox or PasswordBox or AutoSuggestBox) return;

            bool ctrl = IsDown(VirtualKey.Control);
            bool shift = IsDown(VirtualKey.Shift);

            switch (e.Key)
            {
                case VirtualKey.Z when ctrl && shift:
                case VirtualKey.Y when ctrl:
                    ViewModel.RedoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case VirtualKey.Z when ctrl:
                    ViewModel.UndoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case VirtualKey.Number0 or VirtualKey.NumberPad0 when ctrl:
                    FitCanvasToContent();
                    e.Handled = true;
                    return;
                case VirtualKey.Number1 or VirtualKey.NumberPad1 when ctrl:
                    ZoomTo(1);
                    e.Handled = true;
                    return;
                case (VirtualKey)187 or VirtualKey.Add when ctrl: // '=' and '+'
                    ZoomStep(+1);
                    e.Handled = true;
                    return;
                case (VirtualKey)189 or VirtualKey.Subtract when ctrl: // '-'
                    ZoomStep(-1);
                    e.Handled = true;
                    return;
                case VirtualKey.A when ctrl:
                    ViewModel.SelectAllCommand.Execute(null);
                    e.Handled = true;
                    return;
                case VirtualKey.D when ctrl && ViewModel.HasSelection:
                    ViewModel.DuplicateSelectedCommand.Execute(null);
                    e.Handled = true;
                    return;
                case VirtualKey.Delete when ViewModel.HasSelection:
                    ViewModel.RemoveSelectedCommand.Execute(null);
                    e.Handled = true;
                    return;
                case VirtualKey.Escape when ViewModel.IsPlacing:
                    ViewModel.ArmedTool = null;
                    ViewModel.StatusMessage = "Placing cancelled.";
                    e.Handled = true;
                    return;
                case VirtualKey.Escape when ViewModel.HasSelection:
                    ViewModel.ClearSelection();
                    e.Handled = true;
                    return;
            }

            if (ReferenceEquals(focused, CanvasScroller) && ViewModel.HasSelection &&
                e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            {
                double step = shift ? 10 : 1;
                if (!e.KeyStatus.WasKeyDown) ViewModel.RecordUndo(); // one undo step per key press, not per repeat
                ViewModel.NudgeSelection(
                    e.Key == VirtualKey.Left ? -step : e.Key == VirtualKey.Right ? step : 0,
                    e.Key == VirtualKey.Up ? -step : e.Key == VirtualKey.Down ? step : 0);
                e.Handled = true;
            }
        }

        private static bool IsDown(VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // Inspector edits (type, position, size, fill, rotation, label) get one undo step per shape:
        // the canvas is recorded the first time focus enters the inspector for a given selection.
        private void OnInspectorGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            var shape = ViewModel.SelectedShape;
            if (shape is null || ReferenceEquals(shape, _inspectorUndoFor)) return;
            _inspectorUndoFor = shape;
            ViewModel.RecordUndo();
        }

        private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ShapeDesignStudioViewModel.SelectedShape):
                    if (!ReferenceEquals(ViewModel.SelectedShape, _inspectorUndoFor)) _inspectorUndoFor = null;
                    if (ViewModel.SelectedShape is { } sel) ShapesList.ScrollIntoView(sel);
                    TrackAdornedShape(ViewModel.SelectedShape);
                    return;
                case nameof(ShapeDesignStudioViewModel.SelectionCount):
                case nameof(ShapeDesignStudioViewModel.IsDense):
                    UpdateAdorner();
                    return;
                case nameof(ShapeDesignStudioViewModel.SelectedPaletteName):
                    SyncPaletteUi();
                    foreach (var thumb in _presetThumbs) RenderPresetThumb(thumb);
                    return;
                case nameof(ShapeDesignStudioViewModel.ArmedTool):
                    SetCursor(MainCanvas, ViewModel.IsPlacing ? Microsoft.UI.Input.InputSystemCursorShape.Cross : Microsoft.UI.Input.InputSystemCursorShape.Arrow);
                    UpdateAdorner();
                    if (ViewModel.ArmedTool is { } tool)
                    {
                        _armedStatus = $"{ShapeCanvasItemViewModel.DisplayNameFor(tool)} — click the canvas to place it, or drag to draw it.";
                        ViewModel.StatusMessage = _armedStatus;
                    }
                    else if (ViewModel.StatusMessage == _armedStatus)
                    {
                        // Put down without placing (tab switch): don't leave the instruction behind.
                        ViewModel.StatusMessage = "Ready.";
                    }
                    return;
                case nameof(ShapeDesignStudioViewModel.PreviewPng):
                    break;
                default:
                    return;
            }

            try
            {
                var bytes = ViewModel.PreviewPng;
                if (bytes == null)
                {
                    DensePreview.Source = null;
                    return;
                }
                var bmp = await BitmapFromBytesAsync(bytes);
                // A newer trace (or ClearAll) may have replaced the bytes while we were decoding —
                // never let a stale bitmap win over the current state.
                if (!ReferenceEquals(bytes, ViewModel.PreviewPng)) return;
                DensePreview.Source = bmp;
            }
            catch { /* decode failure — preview stays blank, studio keeps working */ }
        }

        private static async System.Threading.Tasks.Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage> BitmapFromBytesAsync(byte[] bytes)
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bmp.SetSourceAsync(stream);
            return bmp;
        }

        // ---- canvas: select, place, drag-to-draw ----

        private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            CanvasScroller.Focus(FocusState.Pointer);
            var pos = e.GetCurrentPoint(MainCanvas).Position;
            if (ViewModel.ArmedTool is null)
            {
                // Select tool: a click on empty canvas clears the selection; a drag draws a
                // rubber band that selects every shape it touches (Ctrl/Shift adds to it).
                _marqueeAdditive = IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift);
                if (!_marqueeAdditive) ViewModel.ClearSelection();
                if (ViewModel.IsDense) { e.Handled = true; return; }
                _marquee = true;
                _marqueeStart = pos;
                MainCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            _drawing = true;
            _drawStart = pos;
            Canvas.SetLeft(DrawGhost, pos.X);
            Canvas.SetTop(DrawGhost, pos.Y);
            DrawGhost.Width = 0;
            DrawGhost.Height = 0;
            MainCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_marquee)
            {
                var m = DragRect(_marqueeStart, e.GetCurrentPoint(MainCanvas).Position);
                if (m.Width < 3 && m.Height < 3) return;
                MarqueeRect.Visibility = Visibility.Visible;
                Canvas.SetLeft(MarqueeRect, m.X);
                Canvas.SetTop(MarqueeRect, m.Y);
                MarqueeRect.Width = m.Width;
                MarqueeRect.Height = m.Height;
                MarqueeRect.StrokeThickness = 1 / CanvasScroller.ZoomFactor;
                e.Handled = true;
                return;
            }
            if (!_drawing) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            var r = DragRect(_drawStart, cur);
            if (r.Width < 4 && r.Height < 4) return;
            DrawGhost.Visibility = Visibility.Visible;
            Canvas.SetLeft(DrawGhost, r.X);
            Canvas.SetTop(DrawGhost, r.Y);
            DrawGhost.Width = r.Width;
            DrawGhost.Height = r.Height;
            e.Handled = true;
        }

        private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_marquee)
            {
                // Finish first: releasing capture raises PointerCaptureLost synchronously, which
                // cancels the band.
                FinishMarquee(e.GetCurrentPoint(MainCanvas).Position);
                MainCanvas.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
                return;
            }
            if (!_drawing) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            MainCanvas.ReleasePointerCapture(e.Pointer);
            FinishDraw(cur);
            e.Handled = true;
        }

        private void OnCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _marquee = false;
            MarqueeRect.Visibility = Visibility.Collapsed;
            if (!_drawing) return;
            _drawing = false;
            DrawGhost.Visibility = Visibility.Collapsed;
        }

        private void FinishMarquee(Point end)
        {
            bool dragged = MarqueeRect.Visibility == Visibility.Visible;
            _marquee = false;
            MarqueeRect.Visibility = Visibility.Collapsed;
            if (!dragged) return; // a plain click: the selection was already cleared on press
            var r = DragRect(_marqueeStart, end);
            int hit = ViewModel.SelectInRect(r.X, r.Y, r.Width, r.Height, _marqueeAdditive);
            int n = ViewModel.SelectionCount;
            ViewModel.StatusMessage = hit == 0 && n == 0 ? "No shapes there — drag across a shape to select it."
                : n == 1 ? $"Selected {ViewModel.SelectedShape?.DisplayName.ToLowerInvariant()}"
                : $"Selected {n} shapes";
        }

        private void FinishDraw(Point end)
        {
            _drawing = false;
            DrawGhost.Visibility = Visibility.Collapsed;
            var tool = ViewModel.ArmedTool;
            if (tool is null) return;

            var r = DragRect(_drawStart, end);
            ViewModel.RecordUndo();
            if (r.Width >= 12 && r.Height >= 12)
            {
                // Dragged out: exactly the rectangle the user drew.
                ViewModel.AddShapeAt(tool, r.X, r.Y, r.Width, r.Height);
            }
            else
            {
                // Click: the default size, centred on the click.
                ViewModel.AddShapeAt(tool, Math.Max(0, _drawStart.X - 60), Math.Max(0, _drawStart.Y - 35));
            }
            // One shape per pick: the tool goes back to Select so the next click selects instead
            // of stamping another copy.
            ViewModel.ArmedTool = null;
        }

        private static Rect DragRect(Point a, Point b) =>
            new(Math.Max(0, Math.Min(a.X, b.X)), Math.Max(0, Math.Min(a.Y, b.Y)), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

        // ---- shapes: click / Ctrl+click / drag the selection ----

        private void OnShapePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // While placing, a click on top of a shape still places (let it reach the canvas).
            if (ViewModel.IsPlacing) return;
            if (sender is not FrameworkElement fe || fe.DataContext is not ShapeCanvasItemViewModel shape) return;
            CanvasScroller.Focus(FocusState.Pointer);

            if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift))
            {
                ViewModel.ToggleSelection(shape);
                e.Handled = true;
                return;
            }

            ViewModel.ClickSelect(shape);
            _dragShape = shape;
            _dragMoved = false;
            _dragAppliedX = _dragAppliedY = 0;
            _dragStart = e.GetCurrentPoint(MainCanvas).Position;
            fe.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnShapePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_dragShape == null) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            double dx = cur.X - _dragStart.X;
            double dy = cur.Y - _dragStart.Y;
            if (!_dragMoved)
            {
                if (Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return; // a click, not a drag
                _dragMoved = true;
                ViewModel.RecordUndo();
            }
            // dx/dy are the pointer's travel since the press; move by whatever is still owed.
            var (mx, my) = ViewModel.NudgeSelection(dx - _dragAppliedX, dy - _dragAppliedY);
            _dragAppliedX += mx;
            _dragAppliedY += my;
            e.Handled = true;
        }

        private void OnShapePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_dragShape == null) return;
            if (sender is FrameworkElement fe) fe.ReleasePointerCapture(e.Pointer);
            if (_dragMoved)
            {
                int n = ViewModel.SelectionCount;
                ViewModel.StatusMessage = n > 1 ? $"Moved {n} shapes" : $"Moved {_dragShape.DisplayName.ToLowerInvariant()} to ({_dragShape.X:F0}, {_dragShape.Y:F0})";
            }
            _dragShape = null;
            e.Handled = true;
        }

        private void OnShapePointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ShapeCanvasItemViewModel s)
            {
                SetCursor(fe, ViewModel.IsPlacing ? Microsoft.UI.Input.InputSystemCursorShape.Cross : Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                _hoverShape = s;
                if (_shapePaths.TryGetValue(s, out var p)) ApplyShapeVisual(p, s, hovered: true);
            }
        }

        private void OnShapePointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ShapeCanvasItemViewModel s)
            {
                if (ReferenceEquals(_hoverShape, s)) _hoverShape = null;
                if (_shapePaths.TryGetValue(s, out var p)) ApplyShapeVisual(p, s);
            }
        }

        // item -> the Path its template loaded. Paths live INSIDE the ItemsControl item
        // templates (never as direct children of MainCanvas), so this map is the only way to
        // reach a shape's visual for live updates (fill/preset edits repaint immediately).
        private readonly Dictionary<ShapeCanvasItemViewModel, Microsoft.UI.Xaml.Shapes.Path> _shapePaths = new();
        // Reverse map so Unloaded never has to read DataContext: during a template swap (ClearAll →
        // regenerate) WinUI raises Unloaded on Paths it is already tearing down, and touching their
        // DataContext threw a COMException that took the whole app down.
        private readonly Dictionary<Microsoft.UI.Xaml.Shapes.Path, ShapeCanvasItemViewModel> _pathItems = new();

        private void TrackShapePath(Microsoft.UI.Xaml.Shapes.Path p, ShapeCanvasItemViewModel s)
        {
            if (_pathItems.TryGetValue(p, out var previous) && !ReferenceEquals(previous, s))
            {
                previous.PropertyChanged -= OnShapeItemChanged;
                if (_shapePaths.TryGetValue(previous, out var prevPath) && ReferenceEquals(prevPath, p))
                    _shapePaths.Remove(previous);
            }
            s.PropertyChanged -= OnShapeItemChanged;
            s.PropertyChanged += OnShapeItemChanged;
            _shapePaths[s] = p;
            _pathItems[p] = s;
            p.Unloaded -= OnShapePathUnloaded;
            p.Unloaded += OnShapePathUnloaded;
        }

        private void OnShapeLoaded(object sender, RoutedEventArgs e)
        {
            // Set geometry + fill from the item in code — the XamlReader-based geometry
            // builder is not reliable inside a value converter at template-load time.
            if (sender is Microsoft.UI.Xaml.Shapes.Path p && p.DataContext is ShapeCanvasItemViewModel s)
            {
                ApplyShapeVisual(p, s);
                TrackShapePath(p, s);
            }
        }

        private void OnShapeDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Microsoft.UI.Xaml.Shapes.Path p && args.NewValue is ShapeCanvasItemViewModel s)
            {
                ApplyShapeVisual(p, s);
                TrackShapePath(p, s);
            }
        }

        private void OnShapePathUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Shapes.Path p)
            {
                p.Unloaded -= OnShapePathUnloaded;
                if (_pathItems.Remove(p, out var s))
                {
                    if (_shapePaths.TryGetValue(s, out var current) && ReferenceEquals(current, p))
                        _shapePaths.Remove(s);
                    s.PropertyChanged -= OnShapeItemChanged;
                }
            }
        }

        private static readonly DoubleCollection SelectionDash = new() { 4, 2 };

        /// <summary>(Re)paint one shape's Path from its item — geometry, fill, stroke, selection, hover.</summary>
        private static void ApplyShapeVisual(Microsoft.UI.Xaml.Shapes.Path p, ShapeCanvasItemViewModel s, bool hovered = false)
        {
            bool isLine = s.PathPoints is { Count: >= 2 };
            bool isRoundRect = string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase);
            var outline = isLine ? null : PresetGeometry.Outline(s.Prst, s.Width, s.Height);
            try
            {
                // Lines are drawn at their real pixel size with no stretch. Stretch="Fill" scales the
                // geometry's bounds to the element, and a straight connector's bounds are zero-wide
                // (or zero-tall) — the degenerate scale threw horizontal/vertical connectors off
                // their boxes (the org chart's tree lines floated across the canvas).
                // Rounded rectangles are also built at real size: stretching a 100×100 template
                // squashed the corners into a pillow on any wide shape. Word's roundRect corner is
                // 1/6 of the shorter side, so that is what the canvas draws too.
                // Chevrons, trapezoids and hexagons are built at real size too, with Word's own
                // proportions (PresetGeometry): a stretched template gave a wide chevron a notch twice
                // as deep as the exported one, so the canvas and the document disagreed.
                p.Stretch = isLine || isRoundRect || outline is not null ? Stretch.None : Stretch.Fill;
                p.Data = isLine ? BuildPolylineGeometry(s.PathPoints!, s.Width, s.Height)
                       : isRoundRect ? BuildRoundRectGeometry(s.Width, s.Height)
                       : outline is not null ? MakePolygonGeometry(outline)
                       : MarkSmith.Converters.ShapeGeometries.For(s.Prst);
            }
            catch { }
            try
            {
                p.Fill = isLine ? null : BrushFromHex(s.Fill);
                string contrast = p.ActualTheme == ElementTheme.Light ? "1F1F1F" : "FFFFFF";
                if (s.IsSelected)
                {
                    // Selection: a dashed outline in a contrasting colour (light on the dark canvas,
                    // dark on the light one) so the selected shape is unmistakable on any fill.
                    p.Stroke = BrushFromHex(contrast);
                    p.StrokeThickness = isLine ? Math.Max(2, s.StrokeWidthPt) : 2;
                    p.StrokeDashArray = isLine ? null : SelectionDash;
                }
                else if (hovered)
                {
                    p.Stroke = BrushFromHex(contrast);
                    p.StrokeThickness = isLine ? Math.Max(2, s.StrokeWidthPt) : 1.5;
                    p.StrokeDashArray = null;
                }
                else
                {
                    p.Stroke = BrushFromHex(s.Fill);
                    p.StrokeThickness = isLine ? Math.Max(1, s.StrokeWidthPt) : 1.5;
                    p.StrokeDashArray = null;
                }
            }
            catch { }
        }

        private void OnShapeItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not ShapeCanvasItemViewModel s) return;
            bool sized = e.PropertyName == nameof(s.Width) || e.PropertyName == nameof(s.Height);
            bool realSizeGeometry = s.PathPoints is { Count: >= 2 } || string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase)
                || PresetGeometry.Outline(s.Prst, 1, 1) is not null;
            if ((e.PropertyName == nameof(s.Fill) || e.PropertyName == nameof(s.Prst) ||
                 e.PropertyName == nameof(s.IsSelected) || e.PropertyName == nameof(s.PathPoints) || (sized && realSizeGeometry)) &&
                _shapePaths.TryGetValue(s, out var path))
            {
                ApplyShapeVisual(path, s, hovered: ReferenceEquals(s, _hoverShape));
            }
        }

        private static Geometry BuildRoundRectGeometry(double width, double height)
        {
            double w = Math.Max(1, width), h = Math.Max(1, height);
            double r = PresetGeometry.RoundRectRadius(w, h);
            var fig = new PathFigure { StartPoint = new Point(r, 0), IsClosed = true, IsFilled = true };
            fig.Segments.Add(new LineSegment { Point = new Point(w - r, 0) });
            fig.Segments.Add(new ArcSegment { Point = new Point(w, r), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
            fig.Segments.Add(new LineSegment { Point = new Point(w, h - r) });
            fig.Segments.Add(new ArcSegment { Point = new Point(w - r, h), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
            fig.Segments.Add(new LineSegment { Point = new Point(r, h) });
            fig.Segments.Add(new ArcSegment { Point = new Point(0, h - r), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
            fig.Segments.Add(new LineSegment { Point = new Point(0, r) });
            fig.Segments.Add(new ArcSegment { Point = new Point(r, 0), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return geo;
        }

        private static PathGeometry MakePolygonGeometry(IReadOnlyList<(double X, double Y)> pts)
        {
            var figure = new PathFigure { StartPoint = new Point(pts[0].X, pts[0].Y), IsClosed = true, IsFilled = true };
            for (int i = 1; i < pts.Count; i++) figure.Segments.Add(new LineSegment { Point = new Point(pts[i].X, pts[i].Y) });
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            return geo;
        }

        /// <summary>Maps a 0..100 local-space polyline onto the item's actual width × height.</summary>
        private static Geometry BuildPolylineGeometry(List<(double X, double Y)> pts, double width, double height)
        {
            double sx = Math.Max(0, width) / 100.0, sy = Math.Max(0, height) / 100.0;
            var scaled = new List<(double X, double Y)>(pts.Count);
            foreach (var (x, y) in pts) scaled.Add((x * sx, y * sy));
            return MakePolylineGeometry(scaled);
        }

        private static PathGeometry MakePolylineGeometry(List<(double X, double Y)> pts)
        {
            if (pts == null || pts.Count == 0) return new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(pts[0].X, pts[0].Y), IsFilled = false };
            for (int i = 1; i < pts.Count; i++)
            {
                figure.Segments.Add(new LineSegment { Point = new Point(pts[i].X, pts[i].Y) });
            }
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            return geo;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SolidColorBrush> BrushCache = new();

        private static SolidColorBrush BrushFromHex(string hex)
        {
            hex = (hex ?? "0078D4").Trim().TrimStart('#');
            if (hex.Length != 6) hex = "0078D4";
            return BrushCache.GetOrAdd(hex, static h =>
            {
                try
                {
                    byte r = Convert.ToByte(h.Substring(0, 2), 16);
                    byte g = Convert.ToByte(h.Substring(2, 2), 16);
                    byte b = Convert.ToByte(h.Substring(4, 2), 16);
                    return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
                }
                catch
                {
                    return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
                }
            });
        }

        // ---- resize handles ----
        // One unrotated, unlocked shape gets a frame with eight handles; dragging one resizes the
        // shape from the opposite edge (Shift keeps the proportions on a corner), and the label
        // refits as it goes. Rotated shapes and connectors are still sized from the inspector.

        private const double HandleSize = 9;

        private void BuildResizeHandles()
        {
            var E = ShapeDesignStudioViewModel.ResizeEdges.None;
            var L = ShapeDesignStudioViewModel.ResizeEdges.Left;
            var T = ShapeDesignStudioViewModel.ResizeEdges.Top;
            var R = ShapeDesignStudioViewModel.ResizeEdges.Right;
            var B = ShapeDesignStudioViewModel.ResizeEdges.Bottom;
            var specs = new (ShapeDesignStudioViewModel.ResizeEdges Edges, Microsoft.UI.Input.InputSystemCursorShape Cursor, string Name)[]
            {
                (L | T, Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast, "top-left"),
                (T | E, Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth, "top"),
                (R | T, Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest, "top-right"),
                (R | E, Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast, "right"),
                (R | B, Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast, "bottom-right"),
                (B | E, Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth, "bottom"),
                (L | B, Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest, "bottom-left"),
                (L | E, Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast, "left"),
            };
            foreach (var (edges, cursor, name) in specs)
            {
                var h = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = HandleSize, Height = HandleSize, RadiusX = 1.5, RadiusY = 1.5,
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                    Stroke = new SolidColorBrush(AccentColor(0xFF)),
                    StrokeThickness = 1.25,
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(h, $"Resize from the {name}");
                ToolTipService.SetToolTip(h, edges.HasFlag(L) || edges.HasFlag(R) ? (edges.HasFlag(T) || edges.HasFlag(B) ? "Drag to resize · Shift keeps the proportions" : "Drag to change the width") : "Drag to change the height");
                SetCursor(h, cursor);
                h.PointerPressed += OnHandlePointerPressed;
                h.PointerMoved += OnHandlePointerMoved;
                h.PointerReleased += OnHandlePointerReleased;
                h.PointerCaptureLost += (_, _) => _resizeEdges = ShapeDesignStudioViewModel.ResizeEdges.None;
                SelectionAdorner.Children.Add(h);
                _handles.Add((h, edges));
            }
        }

        private void TrackAdornedShape(ShapeCanvasItemViewModel? shape)
        {
            if (_adornedShape is not null) _adornedShape.PropertyChanged -= OnAdornedShapeChanged;
            _adornedShape = shape;
            if (_adornedShape is not null) _adornedShape.PropertyChanged += OnAdornedShapeChanged;
            UpdateAdorner();
        }

        private void OnAdornedShapeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ShapeCanvasItemViewModel.X) or nameof(ShapeCanvasItemViewModel.Y)
                or nameof(ShapeCanvasItemViewModel.Width) or nameof(ShapeCanvasItemViewModel.Height)
                or nameof(ShapeCanvasItemViewModel.Rotation) or nameof(ShapeCanvasItemViewModel.PathPoints))
                UpdateAdorner();
        }

        private void UpdateAdorner()
        {
            var s = _adornedShape;
            // Turned shapes get handles too: the frame and handles turn with the shape, and a drag
            // is read in the shape's own frame (ShapeDesignStudioViewModel.ResizeRotatedRect).
            bool show = s is not null && !ViewModel.IsDense && !ViewModel.IsPlacing && ViewModel.SelectionCount == 1
                        && s.PathPoints is not { Count: >= 2 } && ViewModel.Shapes.Contains(s);
            SelectionAdorner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            UpdatePointHandles(s);
            if (!show || s is null) return;

            double z = Math.Max(0.05, CanvasScroller.ZoomFactor);
            double size = HandleSize / z, half = size / 2;
            Canvas.SetLeft(SelectionFrame, s.X);
            Canvas.SetTop(SelectionFrame, s.Y);
            SelectionFrame.Width = Math.Max(0, s.Width);
            SelectionFrame.Height = Math.Max(0, s.Height);
            SelectionFrame.StrokeThickness = 1 / z;
            SelectionFrame.RenderTransformOrigin = new Point(0.5, 0.5);
            SelectionFrame.RenderTransform = s.Rotation % 360 == 0 ? null : new RotateTransform { Angle = s.Rotation };
            foreach (var (h, edges) in _handles)
            {
                var (x, y) = ShapeDesignStudioViewModel.HandlePosition(s.X, s.Y, s.Width, s.Height, s.Rotation, edges);
                h.Width = h.Height = size;
                h.RenderTransformOrigin = new Point(0.5, 0.5);
                h.RenderTransform = s.Rotation % 360 == 0 ? null : new RotateTransform { Angle = s.Rotation };
                SetCursor(h, ShapeDesignStudioViewModel.HandleCursorAxis(edges, s.Rotation) switch
                {
                    0 => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
                    1 => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
                    2 => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
                    _ => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
                });
                h.StrokeThickness = 1.25 / z;
                // Edge handles hide on a shape too small to tell them from the corners.
                bool edge = edges is ShapeDesignStudioViewModel.ResizeEdges.Left or ShapeDesignStudioViewModel.ResizeEdges.Right
                    ? s.Height * z < 28 : edges is ShapeDesignStudioViewModel.ResizeEdges.Top or ShapeDesignStudioViewModel.ResizeEdges.Bottom && s.Width * z < 28;
                h.Visibility = edge ? Visibility.Collapsed : Visibility.Visible;
                Canvas.SetLeft(h, x - half);
                Canvas.SetTop(h, y - half);
            }
        }

        private void OnHandlePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Shapes.Rectangle h || _adornedShape is not { } s) return;
            CanvasScroller.Focus(FocusState.Pointer);
            _resizeEdges = _handles.First(x => ReferenceEquals(x.Handle, h)).Edges;
            _resizeFrom = (s.X, s.Y, s.Width, s.Height);
            _resizeStart = e.GetCurrentPoint(MainCanvas).Position;
            _resized = false;
            h.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnHandlePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_resizeEdges == ShapeDesignStudioViewModel.ResizeEdges.None || _adornedShape is not { } s) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            double dx = cur.X - _resizeStart.X, dy = cur.Y - _resizeStart.Y;
            if (!_resized)
            {
                if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
                _resized = true;
                ViewModel.RecordUndo();
            }
            ViewModel.ResizeShape(s, _resizeFrom, _resizeEdges, dx, dy, keepAspect: IsDown(VirtualKey.Shift));
            ViewModel.StatusMessage = $"{s.DisplayName}: {s.Width:F0} × {s.Height:F0}";
            e.Handled = true;
        }

        private void OnHandlePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_resizeEdges == ShapeDesignStudioViewModel.ResizeEdges.None) return;
            if (sender is UIElement h) h.ReleasePointerCapture(e.Pointer);
            if (_resized && _adornedShape is { } s)
                ViewModel.StatusMessage = $"Resized {s.DisplayName.ToLowerInvariant()} to {s.Width:F0} × {s.Height:F0} · Ctrl+Z to undo";
            _resizeEdges = ShapeDesignStudioViewModel.ResizeEdges.None;
            e.Handled = true;
        }

        // ---- connector ends ----
        // A selected line shows a round handle on each of its points. Dragging an end re-routes
        // the line, snapping onto the side or centre of a shape it's dropped near (that shape's
        // point is ringed while it would snap); a bend of an elbow line moves freely. Alt drops
        // it exactly where the pointer is.

        private Canvas? _pointAdorner;
        private Microsoft.UI.Xaml.Shapes.Ellipse? _snapRing;
        private readonly List<Microsoft.UI.Xaml.Shapes.Ellipse> _pointHandles = new();
        private int _pointIndex = -1;
        private bool _pointMoved;

        private void EnsurePointAdorner()
        {
            if (_pointAdorner is not null) return;
            _pointAdorner = new Canvas { Visibility = Visibility.Collapsed };
            _snapRing = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Stroke = new SolidColorBrush(AccentColor(0xFF)),
                Fill = new SolidColorBrush(AccentColor(0x40)),
            };
            _pointAdorner.Children.Add(_snapRing);
            MainCanvas.Children.Add(_pointAdorner);
        }

        private void UpdatePointHandles(ShapeCanvasItemViewModel? s)
        {
            bool show = s is not null && !ViewModel.IsDense && !ViewModel.IsPlacing && ViewModel.SelectionCount == 1
                        && s.PathPoints is { Count: >= 2 } && ViewModel.Shapes.Contains(s);
            if (!show && _pointAdorner is null) return;
            EnsurePointAdorner();
            _pointAdorner!.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show || s is null) return;

            var points = ShapeDesignStudioViewModel.ConnectorPoints(s);
            while (_pointHandles.Count < points.Count)
            {
                var h = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                    Stroke = new SolidColorBrush(AccentColor(0xFF)),
                };
                ToolTipService.SetToolTip(h, "Drag to re-route · it snaps to a shape's side or centre (Alt: no snap)");
                SetCursor(h, Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                h.PointerPressed += OnPointHandlePressed;
                h.PointerMoved += OnPointHandleMoved;
                h.PointerReleased += OnPointHandleReleased;
                h.PointerCaptureLost += (_, _) => { _pointIndex = -1; if (_snapRing is not null) _snapRing.Visibility = Visibility.Collapsed; };
                _pointAdorner.Children.Add(h);
                _pointHandles.Add(h);
            }
            double z = Math.Max(0.05, CanvasScroller.ZoomFactor);
            double size = (HandleSize + 2) / z;
            for (int i = 0; i < _pointHandles.Count; i++)
            {
                var h = _pointHandles[i];
                if (i >= points.Count) { h.Visibility = Visibility.Collapsed; continue; }
                bool end = i == 0 || i == points.Count - 1;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(h, i == 0 ? "Line start" : i == points.Count - 1 ? "Line end" : $"Bend {i}");
                h.Visibility = Visibility.Visible;
                h.Width = h.Height = end ? size : size * 0.8;
                h.StrokeThickness = 1.25 / z;
                Canvas.SetLeft(h, points[i].X - h.Width / 2);
                Canvas.SetTop(h, points[i].Y - h.Height / 2);
            }
        }

        private void OnPointHandlePressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Shapes.Ellipse h || _adornedShape is null) return;
            CanvasScroller.Focus(FocusState.Pointer);
            _pointIndex = _pointHandles.IndexOf(h);
            _pointMoved = false;
            h.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnPointHandleMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_pointIndex < 0 || _adornedShape is not { } line) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            if (!_pointMoved)
            {
                _pointMoved = true;
                ViewModel.RecordUndo();
            }
            bool snap = !IsDown(VirtualKey.Menu);
            var target = ViewModel.MoveConnectorPoint(line, _pointIndex, cur.X, cur.Y, snap);
            if (_snapRing is not null)
            {
                var pts = ShapeDesignStudioViewModel.ConnectorPoints(line);
                double z = Math.Max(0.05, CanvasScroller.ZoomFactor), r = 9 / z;
                _snapRing.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
                _snapRing.Width = _snapRing.Height = r * 2;
                _snapRing.StrokeThickness = 1.5 / z;
                if (_pointIndex < pts.Count)
                {
                    Canvas.SetLeft(_snapRing, pts[_pointIndex].X - r);
                    Canvas.SetTop(_snapRing, pts[_pointIndex].Y - r);
                }
            }
            ViewModel.StatusMessage = target is null ? "Re-routing the line" : $"Snapped to {target.DisplayName.ToLowerInvariant()}";
            e.Handled = true;
        }

        private void OnPointHandleReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_pointIndex < 0) return;
            if (sender is UIElement h) h.ReleasePointerCapture(e.Pointer);
            if (_snapRing is not null) _snapRing.Visibility = Visibility.Collapsed;
            if (_pointMoved) ViewModel.StatusMessage = "Line re-routed · Ctrl+Z to undo";
            _pointIndex = -1;
            e.Handled = true;
        }

        // ---- cursors ----
        // UIElement.ProtectedCursor is protected in WinUI 3; reflection is the usual way to set it on
        // elements we don't subclass.
        private static readonly System.Reflection.PropertyInfo? CursorProperty =
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static void SetCursor(UIElement element, Microsoft.UI.Input.InputSystemCursorShape shape)
        {
            try { CursorProperty?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape)); }
            catch { /* cosmetic only */ }
        }

        private static Windows.UI.Color AccentColor(byte alpha)
        {
            var c = Application.Current.Resources.TryGetValue("SystemAccentColor", out var v) && v is Windows.UI.Color accent
                ? accent : Windows.UI.Color.FromArgb(255, 0, 120, 212);
            return Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        // ---- inspector: fill colour picker ----

        private void OnFillFlyoutOpening(object? sender, object e)
        {
            var hex = (ViewModel.SelectedShape?.Fill ?? "0078D4").TrimStart('#');
            if (hex.Length == 6)
            {
                try
                {
                    FillPicker.Color = Windows.UI.Color.FromArgb(255,
                        Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
                }
                catch { }
            }
        }

        private void OnFillPickerApply(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is { } shape)
            {
                var c = FillPicker.Color;
                ViewModel.RecordUndo();
                shape.Fill = $"{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            FillFlyout.Hide();
        }

        // ---- export / markdown ----

        private void OnExportMenuOpening(object? sender, object e)
        {
            bool any = ViewModel.HasShapes;
            ExportDocxItem.IsEnabled = any;
            ExportDotxItem.IsEnabled = any;
            CopyMarkdownItem.IsEnabled = any;
        }

        private void OnExportDocxClick(object sender, RoutedEventArgs e) => _ = ExportAsync(template: false);

        private void OnExportDotxClick(object sender, RoutedEventArgs e) => _ = ExportAsync(template: true);

        /// <summary>Ask where to save, then export. The old export wrote a timestamped file straight
        /// to the Desktop with no choice of name or folder.</summary>
        private async System.Threading.Tasks.Task ExportAsync(bool template)
        {
            if (!ViewModel.HasShapes) return;
            try
            {
                var suggestedName = System.IO.Path.GetFileNameWithoutExtension(ShapeDesignStudioViewModel.SuggestedExportName(template));
                var filter = template
                    ? new[] { ("Word template (*.dotx)", "*.dotx") }
                    : new[] { ("Word document (*.docx)", "*.docx") };
                var defaultExt = template ? ".dotx" : ".docx";
                var path = await NativeFilePicker.PickSaveFileAsync(this, "Export", suggestedName, filter, defaultExt);
                if (string.IsNullOrEmpty(path)) return;
                await ViewModel.ExportToWordAsync(template, path);
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Export failed: {ex.Message}";
            }
        }

        private void OnCopyMarkdownClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // SnapshotComposed carries Text/TextColor too — the copy must round-trip the
                // exact same payload that export writes, or labels vanish on paste.
                var block = MarkSmith.Core.Composer.ShapeMarkdownCodec.Serialize(ViewModel.SnapshotComposed());
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(block);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                ViewModel.StatusMessage = $"Copied {ViewModel.Shapes.Count} shapes as a :::shapes Markdown block.";
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Couldn't copy: {ex.Message}";
            }
        }

        private async void OnLoadMarkdownClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dpv = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dpv.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    // Await the clipboard — a sync .GetResult() here can deadlock the UI thread
                    // against the clipboard's own async completion.
                    var text = await dpv.GetTextAsync();
                    await ViewModel.LoadMarkdownAsync(text);
                    FitCanvasAfterLayout();
                }
                else
                {
                    ViewModel.StatusMessage = "The clipboard has no text — copy a :::shapes block first.";
                }
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Couldn't load: {ex.Message}";
            }
        }

        // Clear is undoable now (Ctrl+Z), so it no longer interrupts with a confirmation dialog.
        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.HasShapes) return;
            int n = ViewModel.Shapes.Count;
            ViewModel.ClearAllCommand.Execute(null);
            ViewModel.StatusMessage = $"Cleared {n} shape{(n == 1 ? "" : "s")} · Ctrl+Z to undo";
        }

        // ---- presets ----

        private void OnPresetItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is DiagramPreset preset)
            {
                ViewModel.ApplyPreset(preset);
                FitCanvasAfterLayout();
            }
        }

        // ---- picture → vector ----

        private string? _composeImagePath;

        private void OnStrokeWidthChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (StrokeWidthReadout != null) StrokeWidthReadout.Text = e.NewValue.ToString("0.0");
        }

        private async void OnPickImageClick(object sender, RoutedEventArgs e)
        {
            var filters = new[]
            {
                ("Image files (*.png, *.jpg, *.jpeg, *.bmp, *.gif)", "*.png;*.jpg;*.jpeg;*.bmp;*.gif"),
                ("All files (*.*)", "*.*")
            };
            var path = await NativeFilePicker.PickOpenFileAsync(this, "Select Picture", filters);
            if (string.IsNullOrEmpty(path)) return;
            _composeImagePath = path;
            FuseImageLabel.Text = System.IO.Path.GetFileName(path);
            ToolTipService.SetToolTip(FuseImageLabel, path);
            PickImageButton.Content = "Change…";
            ViewModel.HasImage = true;
            try
            {
                FuseThumb.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
            }
            catch { }
            ViewModel.StatusMessage = $"Picture ready: {System.IO.Path.GetFileName(path)} — adjust the layers, then Convert.";
        }

        private async void OnFuseImageClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_composeImagePath))
            {
                ViewModel.StatusMessage = "Choose a picture first.";
                return;
            }

            int mosaicDensity = (int)FuseMosaicDensity.Value;
            int lineDensity = (int)FuseLineDensity.Value;

            if (mosaicDensity <= 0 && lineDensity <= 0)
            {
                ViewModel.StatusMessage = "Both densities are 0 — raise the shape mosaic or the line art density.";
                return;
            }

            var shapes = new List<string>();
            if (CompRoundRect.IsChecked == true) shapes.Add("roundrect");
            if (CompRect.IsChecked == true) shapes.Add("rect");
            if (CompEllipse.IsChecked == true) shapes.Add("ellipse");
            if (CompChevron.IsChecked == true) shapes.Add("chevron");
            if (CompDiamond.IsChecked == true) shapes.Add("diamond");
            if (CompHexagon.IsChecked == true) shapes.Add("hexagon");
            if (CompTriangle.IsChecked == true) shapes.Add("triangle");
            if (CompCloud.IsChecked == true) shapes.Add("cloud");

            var lineMode = (FuseModePicker.SelectedItem as ComboBoxItem)?.Tag is string tag &&
                           Enum.TryParse<MarkSmith.Core.Composer.LineTraceMode>(tag, out var parsed)
                ? parsed
                : MarkSmith.Core.Composer.LineTraceMode.CrossHatch;

            await ViewModel.ComposeHybridFusionAsync(
                _composeImagePath,
                mosaicDensity,
                lineDensity,
                shapes,
                lineMode,
                FuseMonochrome.IsChecked ?? true,
                (int)FuseEdgeSensitivity.Value,
                FuseStrokeWidth.Value);
            FitCanvasAfterLayout();
        }
    }
}
