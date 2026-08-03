using System;
using Microsoft.UI.Xaml;
using MarkSmith.Services;
using MarkSmith.ViewModels.SmartArt;
using MarkSmith.Core.Composer;

namespace MarkSmith.Views.SmartArt
{
    public sealed partial class SmartArtDesignerWindow : Window
    {
        public SmartArtDesignerViewModel ViewModel { get; }
        private bool _isWebViewReady = false;

        public SmartArtDesignerWindow()
        {
            this.InitializeComponent();
            ViewModel = new SmartArtDesignerViewModel();
            
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            
            this.RootGrid.DataContext = ViewModel;

            ViewModel.PreviewHtmlChanged += (s, e) => RefreshWebView();

            this.Activated += OnWindowActivated;
        }

        private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            this.Activated -= OnWindowActivated;
            await InitializeWebViewAsync();
        }

        private async System.Threading.Tasks.Task InitializeWebViewAsync()
        {
            try
            {
                var env = await WebView2EnvironmentFactory.CreateAsync();
                await PreviewWebView.EnsureCoreWebView2Async(env);
                _isWebViewReady = true;
                RefreshWebView();
            }
            catch
            {
                // CoreWebView2 fallback
            }
        }

        private void RefreshWebView()
        {
            if (_isWebViewReady && PreviewWebView.CoreWebView2 != null)
            {
                string html = BuildWrapperHtml(ViewModel.PreviewHtml);
                PreviewWebView.CoreWebView2.NavigateToString(html);
            }
        }

        private string BuildWrapperHtml(string bodyContent)
        {
            return $@"<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8""/>
  <style>
    * {{ box-sizing: border-box; margin: 0; padding: 0; }}
    body {{ 
      margin: 0; 
      padding: 16px; 
      background: #18181c; 
      color: #f3f4f6; 
      font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; 
      display: flex; 
      justify-content: center; 
      align-items: center; 
      min-height: 90vh; 
    }}
    .smartart-container, .word-fidelity-container {{
      box-shadow: 0 8px 24px rgba(0,0,0,0.4) !important;
    }}
  </style>
</head>
<body>
  {bodyContent}
</body>
</html>";
        }

        private void OnWordFidelityToggleClick(object sender, RoutedEventArgs e)
        {
            ViewModel.IsWordFidelityMode = WordFidelityToggle.IsChecked == true;
        }

        private void OnRenderFidelityClick(object sender, RoutedEventArgs e)
        {
            WordFidelityToggle.IsChecked = !(WordFidelityToggle.IsChecked == true);
            ViewModel.IsWordFidelityMode = WordFidelityToggle.IsChecked == true;
        }

        private void OnExportDocxClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ExportDocxCommand.Execute(null);
        }

        private void OnExportGloxClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ExportGloxCommand.Execute(null);
        }

        private void OnGenerateMosaicClick(object sender, RoutedEventArgs e)
        {
            ViewModel.MarkdownText = "- [Mosaic Node 1]\n  - [Mosaic Child 1A]\n  - [Mosaic Child 1B]\n- [Mosaic Node 2]";
        }

        // Import a user-authored .glox package into the shared catalog so it appears in the
        // gallery and is usable for preview + DOCX export immediately.
        private async void OnImportGloxClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".glox");
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                using var stream = await file.OpenStreamForReadAsync();
                var pkg = MarkSmith.Core.Glox.GloxExtractor.ExtractFromZip(stream);
                MarkSmith.Core.Glox.SmartArtLayoutCatalog.Shared.RegisterPackage(pkg);
                ViewModel.LoadLayouts();
                ViewModel.StatusMessage = $"✓ Imported layout: {pkg.Title} ({pkg.UniqueId})";
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Import error: {ex.Message}";
            }
        }

        private void OnCompileGloxClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ExportGloxCommand.Execute(null);
        }

        // ---- Image → Shapes Composer ----

        private string? _composeImagePath;

        private async void OnPickComposeImageClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".gif");
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                _composeImagePath = file.Path;
                ComposeImageLabel.Text = System.IO.Path.GetFileName(file.Path);
                ViewModel.StatusMessage = $"Composer image: {file.Path}";
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Image pick error: {ex.Message}";
            }
        }

        private void OnComposeImageClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_composeImagePath))
            {
                ViewModel.StatusMessage = "Composer: choose an image first.";
                return;
            }

            try
            {
                var shapes = new System.Collections.Generic.List<string>();
                if (ShapeEllipse.IsChecked == true) shapes.Add("ellipse");
                if (ShapeRoundRect.IsChecked == true) shapes.Add("roundrect");
                if (ShapeRect.IsChecked == true) shapes.Add("rect");
                if (ShapeChevron.IsChecked == true) shapes.Add("chevron");
                if (ShapeDiamond.IsChecked == true) shapes.Add("diamond");
                if (ShapeHexagon.IsChecked == true) shapes.Add("hexagon");
                if (ShapeTriangle.IsChecked == true) shapes.Add("triangle");
                if (ShapeParallelogram.IsChecked == true) shapes.Add("parallelogram");
                if (ShapeLine.IsChecked == true) shapes.Add("line");
                if (shapes.Count == 0) shapes.Add("ellipse");

                var options = new ShapeComposerOptions
                {
                    Grid = (int)ComposeDensity.Value,
                    Shapes = shapes,
                    Dither = ComposeDither.IsChecked == true
                };

                var composed = ImageShapeComposer.Compose(_composeImagePath, options);

                double width = ImageShapeComposer.DefaultCanvasWidthInches;
                double height = width * 0.75; // aspect is recomputed inside Compose; close enough for canvas
                string svg = ImageShapeComposer.RenderSvg(composed, width, height);
                ViewModel.PreviewHtml = $"<div class=\"smartart-container\" style=\"width:100%;height:100%;background:#fff;border-radius:8px;overflow:hidden;\">{svg}</div>";
                RefreshWebView();

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string outPath = System.IO.Path.Combine(desktop,
                    $"Composed_{System.IO.Path.GetFileNameWithoutExtension(_composeImagePath)}_{string.Join("_", shapes)}.docx");
                ShapeComposerDocxWriter.WriteDocx(outPath, composed, width, height,
                    MarkSmith.Core.Glox.SmartArtLayoutCatalog.Shared.ThemeXml);

                ViewModel.StatusMessage = $"✓ Composed {composed.Count} {string.Join("/", shapes)} shapes → {outPath}";
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Compose error: {ex.Message}";
            }
        }
    }
}
