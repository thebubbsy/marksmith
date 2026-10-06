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

        // Shape drag (moves the whole selection)
        private ShapeCanvasItemViewModel? _dragShape;
        private Point _dragStart;
        private bool _dragMoved;

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
            // Open with the canvas focused (no focus ring) so Ctrl+Z / Del work at once and the
            // first title-bar button doesn't come up wearing a keyboard-focus rectangle.
            this.RootGrid.Loaded += (_, _) => CanvasScroller.Focus(FocusState.Programmatic);
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
                    return;
                case nameof(ShapeDesignStudioViewModel.SelectedPaletteName):
                    SyncPaletteUi();
                    return;
                case nameof(ShapeDesignStudioViewModel.ArmedTool):
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
                // Select tool: empty canvas clears the selection.
                ViewModel.ClearSelection();
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
            if (!_drawing) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            MainCanvas.ReleasePointerCapture(e.Pointer);
            FinishDraw(cur);
            e.Handled = true;
        }

        private void OnCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (!_drawing) return;
            _drawing = false;
            DrawGhost.Visibility = Visibility.Collapsed;
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
            ViewModel.NudgeSelection(dx, dy);
            _dragStart = cur;
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
            try
            {
                // Lines are drawn at their real pixel size with no stretch. Stretch="Fill" scales the
                // geometry's bounds to the element, and a straight connector's bounds are zero-wide
                // (or zero-tall) — the degenerate scale threw horizontal/vertical connectors off
                // their boxes (the org chart's tree lines floated across the canvas).
                // Rounded rectangles are also built at real size: stretching a 100×100 template
                // squashed the corners into a pillow on any wide shape. Word's roundRect corner is
                // 1/6 of the shorter side, so that is what the canvas draws too.
                p.Stretch = isLine || isRoundRect ? Stretch.None : Stretch.Fill;
                p.Data = isLine ? BuildPolylineGeometry(s.PathPoints!, s.Width, s.Height)
                       : isRoundRect ? BuildRoundRectGeometry(s.Width, s.Height)
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
            bool realSizeGeometry = s.PathPoints is { Count: >= 2 } || string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase);
            if ((e.PropertyName == nameof(s.Fill) || e.PropertyName == nameof(s.Prst) ||
                 e.PropertyName == nameof(s.IsSelected) || (sized && realSizeGeometry)) &&
                _shapePaths.TryGetValue(s, out var path))
            {
                ApplyShapeVisual(path, s, hovered: ReferenceEquals(s, _hoverShape));
            }
        }

        private static Geometry BuildRoundRectGeometry(double width, double height)
        {
            double w = Math.Max(1, width), h = Math.Max(1, height);
            double r = Math.Min(w, h) / 6.0;
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
                var picker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(ShapeDesignStudioViewModel.SuggestedExportName(template)),
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                };
                if (template) picker.FileTypeChoices.Add("Word template", new List<string> { ".dotx" });
                else picker.FileTypeChoices.Add("Word document", new List<string> { ".docx" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                await ViewModel.ExportToWordAsync(template, file.Path);
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
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".gif");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            _composeImagePath = file.Path;
            FuseImageLabel.Text = System.IO.Path.GetFileName(file.Path);
            ToolTipService.SetToolTip(FuseImageLabel, file.Path);
            PickImageButton.Content = "Change…";
            ViewModel.HasImage = true;
            try
            {
                FuseThumb.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file.Path));
            }
            catch { }
            ViewModel.StatusMessage = $"Picture ready: {System.IO.Path.GetFileName(file.Path)} — adjust the layers, then Convert.";
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
        }
    }
}
