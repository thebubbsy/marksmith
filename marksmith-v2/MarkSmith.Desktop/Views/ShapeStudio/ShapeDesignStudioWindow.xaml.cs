using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using MarkSmith.ViewModels.ShapeStudio;

namespace MarkSmith.Views.ShapeStudio
{
    public sealed partial class ShapeDesignStudioWindow : Window
    {
        public ShapeDesignStudioViewModel ViewModel { get; }
        public event EventHandler<string>? InsertToDocumentRequested;
        private ShapeCanvasItemViewModel? _dragShape;
        private Point _dragStart;

        public ShapeDesignStudioWindow()
        {
            this.InitializeComponent();
            ViewModel = new ShapeDesignStudioViewModel();
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            this.RootGrid.DataContext = ViewModel;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.InsertToDocumentRequested += (s, block) => InsertToDocumentRequested?.Invoke(this, block);
        }

        private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ShapeDesignStudioViewModel.PreviewPng)) return;
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

        // ---- palette ----

        private void OnPaletteItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string prst)
            {
                ViewModel.ActiveTool = prst;
                ViewModel.StatusMessage = $"Tool: {prst} — click the canvas to place.";
            }
        }

        // ---- canvas ----

        private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(MainCanvas).Position;
            ViewModel.AddShapeAt(ViewModel.ActiveTool, Math.Max(0, pos.X - 45), Math.Max(0, pos.Y - 30));
            e.Handled = true;
        }

        private void OnShapePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ShapeCanvasItemViewModel shape)
            {
                ViewModel.SelectedShape = shape;
                _dragShape = shape;
                _dragStart = e.GetCurrentPoint(MainCanvas).Position;
                fe.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void OnShapePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_dragShape != null)
            {
                var cur = e.GetCurrentPoint(MainCanvas).Position;
                double dx = cur.X - _dragStart.X;
                double dy = cur.Y - _dragStart.Y;
                _dragShape.X = Math.Max(0, _dragShape.X + dx);
                _dragShape.Y = Math.Max(0, _dragShape.Y + dy);
                _dragStart = cur;
                e.Handled = true;
            }
        }

        private void OnShapePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_dragShape != null)
            {
                if (sender is FrameworkElement fe) fe.ReleasePointerCapture(e.Pointer);
                _dragShape = null;
                e.Handled = true;
            }
        }

        // item -> the Path its template loaded. Paths live INSIDE the ItemsControl item
        // templates (never as direct children of MainCanvas), so this map is the only way to
        // reach a shape's visual for live updates (fill/preset edits repaint immediately).
        private readonly System.Collections.Generic.Dictionary<ShapeCanvasItemViewModel, Microsoft.UI.Xaml.Shapes.Path> _shapePaths = new();

        private void OnShapeLoaded(object sender, RoutedEventArgs e)
        {
            // Set geometry + fill from the item in code — the XamlReader-based geometry
            // builder is not reliable inside a value converter at template-load time.
            if (sender is Microsoft.UI.Xaml.Shapes.Path p && p.DataContext is ShapeCanvasItemViewModel s)
            {
                ApplyShapeVisual(p, s);

                s.PropertyChanged -= OnShapeItemChanged;
                s.PropertyChanged += OnShapeItemChanged;
                _shapePaths[s] = p;
                p.Unloaded -= OnShapePathUnloaded;
                p.Unloaded += OnShapePathUnloaded;
            }
        }

        private void OnShapeDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Microsoft.UI.Xaml.Shapes.Path p && args.NewValue is ShapeCanvasItemViewModel s)
            {
                ApplyShapeVisual(p, s);

                s.PropertyChanged -= OnShapeItemChanged;
                s.PropertyChanged += OnShapeItemChanged;
                _shapePaths[s] = p;
                p.Unloaded -= OnShapePathUnloaded;
                p.Unloaded += OnShapePathUnloaded;
            }
        }

        private void OnShapePathUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Shapes.Path p)
            {
                p.Unloaded -= OnShapePathUnloaded;
                if (p.DataContext is ShapeCanvasItemViewModel s)
                {
                    _shapePaths.Remove(s);
                    s.PropertyChanged -= OnShapeItemChanged;
                }
            }
        }

        /// <summary>(Re)paint one shape's Path from its item — geometry, fill, stroke.</summary>
        private static void ApplyShapeVisual(Microsoft.UI.Xaml.Shapes.Path p, ShapeCanvasItemViewModel s)
        {
            bool isLine = s.PathPoints is { Count: >= 2 };
            try
            {
                p.Data = isLine ? BuildPolylineGeometry(s.PathPoints!) : MarkSmith.Converters.ShapeGeometries.For(s.Prst);
            }
            catch { }
            try
            {
                p.Fill = isLine ? null : BrushFromHex(s.Fill);
                p.Stroke = BrushFromHex(s.Fill);
                p.StrokeThickness = isLine ? Math.Max(1, s.StrokeWidthPt) : 1.5;
            }
            catch { }
        }

        private void OnShapeItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not ShapeCanvasItemViewModel s) return;
            if ((e.PropertyName == nameof(s.Fill) || e.PropertyName == nameof(s.Prst)) &&
                _shapePaths.TryGetValue(s, out var path))
            {
                ApplyShapeVisual(path, s);
            }
        }

        private static Microsoft.UI.Xaml.Media.Geometry BuildPolylineGeometry(System.Collections.Generic.List<(double X, double Y)> pts)
        {
            return MakePolylineGeometry(pts);
        }

        private static Microsoft.UI.Xaml.Media.PathGeometry MakePolylineGeometry(System.Collections.Generic.List<(double X, double Y)> pts)
        {
            if (pts == null || pts.Count == 0) return new Microsoft.UI.Xaml.Media.PathGeometry();
            var figure = new Microsoft.UI.Xaml.Media.PathFigure { StartPoint = new Point(pts[0].X, pts[0].Y), IsFilled = false };
            for (int i = 1; i < pts.Count; i++)
            {
                figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Point(pts[i].X, pts[i].Y) });
            }
            var geo = new Microsoft.UI.Xaml.Media.PathGeometry();
            geo.Figures.Add(figure);
            return geo;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Microsoft.UI.Xaml.Media.SolidColorBrush> BrushCache = new();

        private static Microsoft.UI.Xaml.Media.SolidColorBrush BrushFromHex(string hex)
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
                    return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
                }
                catch
                {
                    return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
                }
            });
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
                ViewModel.StatusMessage = $"Copied {ViewModel.Shapes.Count} shapes as a :::shapes markdown block.";
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Copy error: {ex.Message}";
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
                    ViewModel.StatusMessage = "Clipboard has no text.";
                }
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Load error: {ex.Message}";
            }
        }

        // ---- actions ----

        private void OnExportDocxClick(object sender, RoutedEventArgs e) => ViewModel.ExportDocxCommand.Execute(null);

        private void OnExportDotxClick(object sender, RoutedEventArgs e) => ViewModel.ExportDotxCommand.Execute(null);

        private void OnClearClick(object sender, RoutedEventArgs e) => ViewModel.ClearAllCommand.Execute(null);

        private void OnDeleteShapeClick(object sender, RoutedEventArgs e) => ViewModel.RemoveSelectedCommand.Execute(null);

        private string? _composeImagePath;

        private void OnPresetItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is DiagramPreset preset)
            {
                ViewModel.ApplyPreset(preset);
            }
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
            string fileName = System.IO.Path.GetFileName(file.Path);
            if (FuseImageLabel != null) FuseImageLabel.Text = fileName;
            ViewModel.HasImage = true;
            try
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file.Path));
                if (FuseThumb != null) FuseThumb.Source = bmp;
            }
            catch { }
            ViewModel.StatusMessage = $"Selected image: {file.Path}";
        }

        private async void OnFuseImageClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_composeImagePath))
            {
                ViewModel.StatusMessage = "Choose an image first.";
                return;
            }

            int mosaicDensity = (int)(FuseMosaicDensity?.Value ?? 0);
            int lineDensity = (int)(FuseLineDensity?.Value ?? 0);

            if (mosaicDensity <= 0 && lineDensity <= 0)
            {
                ViewModel.StatusMessage = "Set Shape Density or Line Density above 0.";
                return;
            }

            var shapes = new System.Collections.Generic.List<string>();
            if (CompRoundRect?.IsChecked == true) shapes.Add("roundrect");
            if (CompRect?.IsChecked == true) shapes.Add("rect");
            if (CompEllipse?.IsChecked == true) shapes.Add("ellipse");
            if (CompChevron?.IsChecked == true) shapes.Add("chevron");
            if (CompDiamond?.IsChecked == true) shapes.Add("diamond");
            if (CompHexagon?.IsChecked == true) shapes.Add("hexagon");
            if (CompTriangle?.IsChecked == true) shapes.Add("triangle");
            if (CompCloud?.IsChecked == true) shapes.Add("cloud");

            var lineMode = MarkSmith.Core.Composer.LineTraceMode.CrossHatch;
            if (FuseModeTopographic?.IsChecked == true) lineMode = MarkSmith.Core.Composer.LineTraceMode.TopographicWaves;
            else if (FuseModeCalligraphic?.IsChecked == true) lineMode = MarkSmith.Core.Composer.LineTraceMode.Calligraphic;
            else if (FuseModeEdges?.IsChecked == true) lineMode = MarkSmith.Core.Composer.LineTraceMode.Edges;
            else if (FuseModeEngraved?.IsChecked == true) lineMode = MarkSmith.Core.Composer.LineTraceMode.Engraved;
            else if (FuseModeScanlines?.IsChecked == true) lineMode = MarkSmith.Core.Composer.LineTraceMode.Scanlines;
            else if (FuseModeSilhouette?.IsChecked == true) lineMode = MarkSmith.Core.Composer.LineTraceMode.Silhouette;

            bool monochrome = FuseMonochrome?.IsChecked ?? true;
            int edgeThreshold = (int)(FuseEdgeSensitivity?.Value ?? 30);
            double strokeWidth = FuseStrokeWidth?.Value ?? 1.5;

            await ViewModel.ComposeHybridFusionAsync(
                _composeImagePath,
                mosaicDensity,
                lineDensity,
                shapes,
                lineMode,
                monochrome,
                edgeThreshold,
                strokeWidth);
        }
    }
}
