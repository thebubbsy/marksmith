using System;
using Microsoft.UI.Xaml;
using MarkSmith.Services;
using MarkSmith.ViewModels.SmartArt;

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
    }
}
