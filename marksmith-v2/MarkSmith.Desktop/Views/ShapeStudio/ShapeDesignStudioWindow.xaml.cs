using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
            ViewModel = new ShapeDesignStudioViewModel();
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            this.RootGrid.DataContext = ViewModel;
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

        // ---- actions ----

        private void OnExportDocxClick(object sender, RoutedEventArgs e) => ViewModel.ExportDocxCommand.Execute(null);

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
            ViewModel.StatusMessage = $"Composer image: {file.Path}";
        }

        private void OnComposeImageClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_composeImagePath))
            {
                ViewModel.StatusMessage = "Choose an image first.";
                return;
            }
            ViewModel.ComposeImage(_composeImagePath, (int)ComposeDensity.Value);
        }
    }
}
