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
        private ShapeCanvasItemViewModel? _dragShape;
        private Point _dragStart;

        public ShapeDesignStudioWindow()
        {
            this.InitializeComponent();
            // RadioButton.IsChecked must NOT be set in XAML — WinUI 3 throws a XamlParseException
            // ("Failed to assign to property ToggleButton.IsChecked") while parsing it, crashing
            // the whole app on open. Set the initial selection here instead.
            ModeEngraved.IsChecked = true;
            ViewModel = new ShapeDesignStudioViewModel();
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            this.RootGrid.DataContext = ViewModel;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
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

        // ---- trace workflow ----

        private void OnTraceModeChecked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb)
            {
                ViewModel.TraceModeIndex =
                    rb == ModeEngraved ? 0 :
                    rb == ModeEdges ? 1 :
                    rb == ModeSilhouette ? 2 : 3;
            }
        }

        private async void OnTraceClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_composeImagePath))
            {
                ViewModel.StatusMessage = "Choose an image first.";
                return;
            }
            await ViewModel.TraceImageAsync(_composeImagePath);
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

        private void OnShapeLoaded(object sender, RoutedEventArgs e)
        {
            // Set geometry + fill from the item in code — the XamlReader-based geometry
            // builder is not reliable inside a value converter at template-load time.
            if (sender is Microsoft.UI.Xaml.Shapes.Path p && p.DataContext is ShapeCanvasItemViewModel s)
            {
                try
                {
                    p.Data = s.PathPoints is { Count: >= 2 }
                        ? ParsePathGeometry(s.PathPoints)          // curved sketch strokes
                        : MarkSmith.Converters.ShapeGeometries.For(s.Prst);
                }
                catch { }
                try
                {
                    p.Fill = s.PathPoints is { Count: >= 2 } ? null : BrushFromHex(s.Fill);
                    p.Stroke = BrushFromHex(s.Fill);
                    p.StrokeThickness = s.PathPoints is { Count: >= 2 } ? Math.Max(1, s.StrokeWidthPt) : 1.5;
                }
                catch { }

                s.PropertyChanged -= OnShapeItemChanged;
                s.PropertyChanged += OnShapeItemChanged;
            }
        }

        private static Microsoft.UI.Xaml.Media.Geometry ParsePathGeometry(System.Collections.Generic.List<(double X, double Y)> pts)
        {
            // Polyline (0..100 space) -> XAML Path.Data mini-language.
            var d = new System.Text.StringBuilder("M");
            foreach (var p in pts)
            {
                d.Append(' ').Append(p.X.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
                 .Append(' ').Append(p.Y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            }
            var path = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                $"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{d}'/>");
            return path.Data;
        }

        private void OnShapeItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not ShapeCanvasItemViewModel s) return;
            if (e.PropertyName == nameof(s.Fill) || e.PropertyName == nameof(s.Prst))
            {
                // The Path that loaded this item is its visual parent's child; find and update it.
                // Simpler: selection re-triggers Loaded for new items only — fill changes are
                // reflected on the next edit because the inspector writes to the VM, and we
                // refresh the whole canvas cheaply here:
                if (e.PropertyName == nameof(s.Prst))
                {
                    RefreshAllShapeVisuals();
                }
            }
        }

        private void RefreshAllShapeVisuals()
        {
            foreach (var item in MainCanvas.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>())
            {
                if (item.DataContext is ShapeCanvasItemViewModel s)
                {
                    try { item.Data = MarkSmith.Converters.ShapeGeometries.For(s.Prst); } catch { }
                    try { item.Fill = BrushFromHex(s.Fill); } catch { }
                }
            }
        }

        private static Microsoft.UI.Xaml.Media.SolidColorBrush BrushFromHex(string hex)
        {
            hex = (hex ?? "0078D4").Trim().TrimStart('#');
            try
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
            }
            catch
            {
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
            }
        }

        private void OnCopyMarkdownClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var block = MarkSmith.Core.Composer.ShapeMarkdownCodec.Serialize(ViewModel.Shapes.Select(s => new MarkSmith.Core.Composer.ComposedShape
                {
                    Prst = s.Prst, X = s.X, Y = s.Y, W = s.Width, H = s.Height, Fill = s.Fill, Rot = s.Rotation,
                    PathPoints = s.PathPoints, StrokeWidthPt = s.StrokeWidthPt
                }));
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

        private void OnLoadMarkdownClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dpv = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dpv.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    var text = dpv.GetTextAsync().AsTask().GetAwaiter().GetResult();
                    ViewModel.LoadMarkdown(text);
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
            ComposeImageLabel.Text = System.IO.Path.GetFileName(file.Path);
            TraceImageLabel.Text = System.IO.Path.GetFileName(file.Path);
            ViewModel.HasImage = true;
            try
            {
                TraceThumb.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file.Path));
            }
            catch { }
            ViewModel.StatusMessage = $"Composer image: {file.Path}";
        }

        private void OnComposeImageClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_composeImagePath))
            {
                ViewModel.StatusMessage = "Choose an image first.";
                return;
            }

            var shapes = new System.Collections.Generic.List<string>();
            if (CompEllipse.IsChecked == true) shapes.Add("ellipse");
            if (CompRoundRect.IsChecked == true) shapes.Add("roundrect");
            if (CompRect.IsChecked == true) shapes.Add("rect");
            if (CompChevron.IsChecked == true) shapes.Add("chevron");
            if (CompDiamond.IsChecked == true) shapes.Add("diamond");
            if (CompHexagon.IsChecked == true) shapes.Add("hexagon");
            if (CompTriangle.IsChecked == true) shapes.Add("triangle");
            if (CompParallelogram.IsChecked == true) shapes.Add("parallelogram");
            if (CompLine.IsChecked == true) shapes.Add("line");
            if (CompArc.IsChecked == true) shapes.Add("arc");
            if (CompCloud.IsChecked == true) shapes.Add("cloud");
            if (CompHeart.IsChecked == true) shapes.Add("heart");
            if (CompMoon.IsChecked == true) shapes.Add("moon");
            if (CompCircularArrow.IsChecked == true) shapes.Add("circulararrow");
            if (CompSmiley.IsChecked == true) shapes.Add("smileyface");

            if (CompSketch.IsChecked == true)
            {
                ViewModel.ComposeSketchImage(_composeImagePath, (int)ComposeDensity.Value);
                return;
            }

            ViewModel.ComposeImage(_composeImagePath, (int)ComposeDensity.Value, shapes);
        }
    }
}
