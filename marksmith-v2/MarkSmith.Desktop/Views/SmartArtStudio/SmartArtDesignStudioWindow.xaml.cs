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
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            SizeForDisplay();
            ViewModel = new SmartArtDesignStudioViewModel();

            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            TitleBarInsets.Reserve(this, AppTitleBar); // keep the action buttons clear of min/max/close
            CaptionButtons.Follow(this, AppTitleBar);
            CloseGuard.Attach(this, () => ViewModel.HasUnkeptWork, () =>
            {
                ViewModel.InsertIntoDocument();
                return !ViewModel.HasUnkeptWork; // false when the layout couldn't be resolved
            }, "SmartArt graphic");
            this.RootGrid.DataContext = ViewModel;

            ViewModel.PreviewHtmlChanged += (s, e) => RefreshWebView();
            ViewModel.InsertToDocumentRequested += (s, block) => InsertToDocumentRequested?.Invoke(this, block);
            this.Activated += OnWindowActivated;
            // Transparent page so the preview card's theme brush shows through in Light and Dark
            // (the rendered SmartArt is a self-contained light card either way).
            PreviewWebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
            HoverPolish.Track(this.RootGrid);
            // Open in the layout search (typing filters at once), not on the first title-bar button.
            this.RootGrid.Loaded += (_, _) =>
            {
                LayoutSearch.Focus(FocusState.Programmatic);
                if (ViewModel.SelectedLayout is { } layout) LayoutList.ScrollIntoView(layout, ScrollIntoViewAlignment.Leading);
            };
        }

        // Sized in DIPs (AppWindow sizes are physical pixels, so the default opened cramped on a
        // scaled display) with a floor that keeps the gallery, the outline and a readable preview on
        // screen; without one the window squeezed until the preview pane vanished.
        private void SizeForDisplay()
        {
            try
            {
                var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
                if (scale <= 0) scale = 1;
                var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
                    Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
                AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    Math.Min((int)(1360 * scale), work.Width), Math.Min((int)(840 * scale), work.Height)));
                if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                {
                    presenter.PreferredMinimumWidth = Math.Min((int)(1120 * scale), work.Width);
                    presenter.PreferredMinimumHeight = Math.Min((int)(600 * scale), work.Height);
                }
            }
            catch { /* best effort: the default size still works */ }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private void OnEditorTabsChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            bool outline = sender.SelectedItem == OutlineTab;
            OutlinePanel.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
            MarkdownPanel.Visibility = outline ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnLayoutSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            ViewModel.SearchQuery = sender.Text;
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

        // The renderer emits a card sized for documents (800 wide, as tall as the layout needs) with
        // a "Layout: …" caption. In the studio the card fills the pane and the drawing scales to fit
        // it either way (the SVG's default xMidYMid meet), and the caption is dropped because the
        // pane header names the layout already.
        private static string BuildWrapperHtml(string body)
        {
            return $@"<!DOCTYPE html>
<html><head><meta charset=""utf-8""/>
<style>
  html,body{{height:100%;margin:0}}
  body{{padding:16px;box-sizing:border-box;background:transparent;display:flex}}
  .smartart-container{{width:100%!important;max-width:none!important;height:100%!important;display:flex;flex-direction:column;padding:12px;box-sizing:border-box}}
  .smartart-container > .smartart-caption{{display:none}}
  .smartart-container > svg{{flex:1;min-height:0;width:100%!important;height:100%!important}}
</style></head>
<body>
  {body}
<script>
  // Frame the drawing itself rather than its 800-wide document canvas, so a three-box process
  // fills the pane like an org chart does — but never zoom a lone shape up to poster size.
  (function () {{
    var s = document.querySelector('.smartart-container > svg');
    if (!s || !s.getBBox) return;
    var b = s.getBBox(), pad = 20;
    if (!b.width || !b.height) return;
    var w = Math.max(b.width + pad * 2, 560), h = Math.max(b.height + pad * 2, 340);
    var cx = b.x + b.width / 2, cy = b.y + b.height / 2;
    s.setAttribute('viewBox', (cx - w / 2) + ' ' + (cy - h / 2) + ' ' + w + ' ' + h);
  }})();
</script>
</body></html>";
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
                // The outline takes keyboard focus, so its keys (↑/↓, Tab, Enter…) work next.
                OutlineScroll.Focus(FocusState.Pointer);
                e.Handled = true;
            }
        }

        // The outline's keys, only while it has focus (Tab elsewhere still moves focus). The model
        // is the VM's HandleOutlineKey; this maps the keys and re-focuses the rename box it opens.
        // PreviewKeyDown, because the ScrollViewer itself takes Up/Down/Home/End to scroll before
        // a KeyDown handler would see them.
        private void OnOutlineKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Only when the outline itself has focus: a row's own buttons keep Tab for moving on.
            // A desktop window has to name its XamlRoot; the parameterless call returns null.
            if (OutlineScroll.XamlRoot is not { } root || !ReferenceEquals(FocusManager.GetFocusedElement(root), OutlineScroll)) return;
            bool Down(VirtualKey k) => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k)
                                        & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            SmartArtDesignStudioViewModel.OutlineKey? key = e.Key switch
            {
                VirtualKey.Up => SmartArtDesignStudioViewModel.OutlineKey.Up,
                VirtualKey.Down => SmartArtDesignStudioViewModel.OutlineKey.Down,
                VirtualKey.Home => SmartArtDesignStudioViewModel.OutlineKey.Home,
                VirtualKey.End => SmartArtDesignStudioViewModel.OutlineKey.End,
                VirtualKey.Tab => SmartArtDesignStudioViewModel.OutlineKey.Tab,
                VirtualKey.Enter => SmartArtDesignStudioViewModel.OutlineKey.Enter,
                VirtualKey.Insert => SmartArtDesignStudioViewModel.OutlineKey.Insert,
                _ => null,
            };
            if (key is not { } k) return;
            if (!ViewModel.HandleOutlineKey(k, shift: Down(VirtualKey.Shift), alt: Down(VirtualKey.Menu))) return;
            e.Handled = true;
            if (k is SmartArtDesignStudioViewModel.OutlineKey.Enter or SmartArtDesignStudioViewModel.OutlineKey.Insert)
                BeginRenameAndFocus(ViewModel.SelectedNode);
        }

        private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b && Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var brush) && brush is Brush hover)
                b.Background = hover;
        }

        private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b) b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
            FocusRenameBox();
        }

        private void OnAddFirstNodeClick(object sender, RoutedEventArgs e)
        {
            // AddChild with no selection creates a root node already in inline-rename mode.
            ViewModel.AddChildCommand.Execute(null);
            FocusRenameBox();
        }

        private void FocusRenameBox()
        {
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
            // Back to the outline after either, so typing carries on from the keyboard (the box
            // collapses, and focus used to be left on nothing).
            if (e.Key == VirtualKey.Enter) { ViewModel.CommitRename(); e.Handled = true; OutlineScroll.Focus(FocusState.Keyboard); }
            else if (e.Key == VirtualKey.Escape) { ViewModel.CancelRename(); e.Handled = true; OutlineScroll.Focus(FocusState.Keyboard); }
        }

        private void OnRenameLostFocus(object sender, RoutedEventArgs e) => ViewModel.CommitRename();

        // Window-level shortcuts: Delete = delete node, F2 = rename, Ctrl+Z/Y = undo/redo.
        // TextBoxes (rename box, Markdown Data) keep their native keys — focus guard.
        private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if ((sender as UIElement)?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox) return;
            var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                        & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (ctrl && e.Key == VirtualKey.Z) { ViewModel.UndoCommand.Execute(null); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.Y) { ViewModel.RedoCommand.Execute(null); e.Handled = true; }
            else if (e.Key == VirtualKey.Delete) { ViewModel.DeleteSelectedCommand.Execute(null); e.Handled = true; }
            else if (e.Key == VirtualKey.F2) { BeginRenameAndFocus(); e.Handled = true; }
        }
    }
}
