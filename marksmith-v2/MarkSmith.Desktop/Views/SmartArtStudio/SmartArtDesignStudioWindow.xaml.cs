using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using MarkSmith.Services;
using MarkSmith.ViewModels.SmartArtStudio;

namespace MarkSmith.Views.SmartArtStudio
{
    public sealed partial class SmartArtDesignStudioWindow : Window
    {
        public SmartArtDesignStudioViewModel ViewModel { get; }
        private bool _isWebViewReady;

        /// <summary>Raised with the full <c>:::smartart</c> markdown block when the user chooses
        /// "Insert into document" — the MainWindow inserts it at the editor caret.</summary>
        public event EventHandler<string>? InsertToDocumentRequested;

        public SmartArtDesignStudioWindow()
        {
            this.InitializeComponent();
            ViewModel = new SmartArtDesignStudioViewModel();

            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            this.RootGrid.DataContext = ViewModel;

            ViewModel.PreviewHtmlChanged += (s, e) => RefreshWebView();
            ViewModel.InsertToDocumentRequested += (s, block) => InsertToDocumentRequested?.Invoke(this, block);
            this.Activated += OnWindowActivated;
        }

        private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            this.Activated -= OnWindowActivated;
            try
            {
                var env = await WebView2EnvironmentFactory.CreateAsync();
                await PreviewWebView.EnsureCoreWebView2Async(env);
                _isWebViewReady = true;
                RefreshWebView();
            }
            catch { /* preview unavailable — export path still works */ }
        }

        private void RefreshWebView()
        {
            if (_isWebViewReady && PreviewWebView.CoreWebView2 != null)
            {
                string html = BuildWrapperHtml(ViewModel.PreviewHtml);
                PreviewWebView.CoreWebView2.NavigateToString(html);
            }
        }

        private static string BuildWrapperHtml(string body)
        {
            return $@"<!DOCTYPE html>
<html><head><meta charset=""utf-8""/></head>
<body style=""margin:0;padding:12px;background:#18181c;display:flex;justify-content:center;align-items:center;min-height:90vh;"">
  {body}
</body></html>";
        }

        private void OnInsertIntoDocumentClick(object sender, RoutedEventArgs e)
        {
            ViewModel.InsertIntoDocumentCommand.Execute(null);
        }

        private void OnAddRootClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AddRootNodeCommand.Execute(null);
        }

        private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
        {
            ViewModel.DeleteSelectedNodeCommand.Execute(null);
        }

        private void OnNodePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StudioNodeViewModel node)
            {
                foreach (var n in ViewModel.DisplayNodes)
                {
                    n.IsSelected = n == node;
                }
                e.Handled = true;
            }
        }
    }
}
