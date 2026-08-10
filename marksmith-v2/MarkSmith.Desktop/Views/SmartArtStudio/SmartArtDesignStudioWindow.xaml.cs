using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MarkSmith.Services;
using MarkSmith.ViewModels.SmartArtStudio;
using Windows.System;

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
            catch { /* preview unavailable — insert path still works */ }
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

        // ------------------------------------------------------------------ outline editor

        private void OnRowPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // Presses that start on a row button belong to the button (its Click must win), so
            // skip selection there — otherwise Handled=true would swallow the click.
            if (e.OriginalSource is DependencyObject src && IsInsideButton(src)) return;
            if (sender is FrameworkElement fe && fe.DataContext is StudioNodeViewModel node)
            {
                ViewModel.Select(node);
                e.Handled = true;
            }
        }

        private static bool IsInsideButton(DependencyObject source)
        {
            DependencyObject? cur = source;
            while (cur != null)
            {
                if (cur is Button) return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StudioNodeViewModel node)
            {
                BeginRenameAndFocus(node);
                e.Handled = true;
            }
        }

        private void OnRowAddChildClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is StudioNodeViewModel node)
                ViewModel.AddChildCommand.Execute(node);
        }

        private void OnRowDeleteClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is StudioNodeViewModel node)
                ViewModel.DeleteSelectedCommand.Execute(node);
        }

        private void OnRenameClick(object sender, RoutedEventArgs e)
        {
            BeginRenameAndFocus();
        }

        private void BeginRenameAndFocus(StudioNodeViewModel? node = null)
        {
            if (node != null) ViewModel.BeginRename(node);
            else ViewModel.BeginRenameCommand.Execute(null);
            // The rename box appears on the next layout pass — focus + select-all then.
            DispatcherQueue.TryEnqueue(() =>
            {
                var box = FindVisualChildren<TextBox>(OutlineScroll)
                    .FirstOrDefault(t => t.Visibility == Visibility.Visible);
                if (box != null)
                {
                    box.Focus(FocusState.Programmatic);
                    box.SelectAll();
                }
            });
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
            }
        }

        private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter) { ViewModel.CommitRename(); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { ViewModel.CancelRename(); e.Handled = true; }
        }

        private void OnRenameLostFocus(object sender, RoutedEventArgs e) => ViewModel.CommitRename();

        // Window-level shortcuts: Delete = delete node, F2 = rename, Ctrl+Z/Y = undo/redo.
        // TextBoxes (rename box, Markdown Data) keep their native keys — focus guard.
        private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (FocusManager.GetFocusedElement() is TextBox) return;
            var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                        & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (ctrl && e.Key == VirtualKey.Z) { ViewModel.UndoCommand.Execute(null); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.Y) { ViewModel.RedoCommand.Execute(null); e.Handled = true; }
            else if (e.Key == VirtualKey.Delete) { ViewModel.DeleteSelectedCommand.Execute(null); e.Handled = true; }
            else if (e.Key == VirtualKey.F2) { BeginRenameAndFocus(); e.Handled = true; }
        }
    }
}
