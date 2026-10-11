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
            if (StatusBarText != null && !string.IsNullOrEmpty(ViewModel.StatusMessage))
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusBarText, ViewModel.StatusMessage);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(StatusBarText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            }
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.StatusMessage) && StatusBarText != null && !string.IsNullOrEmpty(ViewModel.StatusMessage))
                {
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusBarText, ViewModel.StatusMessage);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(StatusBarText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
                }
                else if (e.PropertyName == nameof(ViewModel.SelectedLayout) && ViewModel.SelectedLayout is { } layout)
                {
                    LayoutList.ScrollIntoView(layout, ScrollIntoViewAlignment.Leading);
                }
            };
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

        private void OnLayoutSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key is VirtualKey.Down or VirtualKey.Enter)
            {
                if (LayoutList.Items.Count > 0)
                {
                    if (LayoutList.SelectedIndex < 0) LayoutList.SelectedIndex = 0;
                    LayoutList.Focus(FocusState.Keyboard);
                    if (ViewModel.SelectedLayout is { } layout)
                        LayoutList.ScrollIntoView(layout, ScrollIntoViewAlignment.Leading);
                    e.Handled = true;
                }
            }
            else if (e.Key == VirtualKey.Escape)
            {
                if (!string.IsNullOrEmpty(LayoutSearch.Text))
                {
                    LayoutSearch.Text = string.Empty;
                    e.Handled = true;
                }
                else
                {
                    OutlineScroll.Focus(FocusState.Keyboard);
                    e.Handled = true;
                }
            }
        }

        private void OnLayoutCategoryComboKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape && !LayoutCategoryCombo.IsDropDownOpen)
            {
                OutlineScroll.Focus(FocusState.Keyboard);
                e.Handled = true;
            }
        }

        private void OnMarkdownTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                EditorTabs.SelectedItem = OutlineTab;
                OutlineScroll.Focus(FocusState.Keyboard);
                e.Handled = true;
            }
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
            if (e.Key == VirtualKey.Application || (e.Key == VirtualKey.F10 && Down(VirtualKey.Shift)))
            {
                if (ViewModel.SelectedNode is { } selected)
                    ShowRowContextMenu(selected, OutlineScroll, new Windows.Foundation.Point(48, 36));
                else
                    ShowOutlineAreaContextMenu(OutlineScroll, new Windows.Foundation.Point(48, 36));
                e.Handled = true;
                return;
            }
            SmartArtDesignStudioViewModel.OutlineKey? key = e.Key switch
            {
                VirtualKey.Up => SmartArtDesignStudioViewModel.OutlineKey.Up,
                VirtualKey.Down => SmartArtDesignStudioViewModel.OutlineKey.Down,
                VirtualKey.Home => SmartArtDesignStudioViewModel.OutlineKey.Home,
                VirtualKey.End => SmartArtDesignStudioViewModel.OutlineKey.End,
                VirtualKey.Tab => SmartArtDesignStudioViewModel.OutlineKey.Tab,
                VirtualKey.Enter when !Down(VirtualKey.Control) => SmartArtDesignStudioViewModel.OutlineKey.Enter,
                VirtualKey.Insert => SmartArtDesignStudioViewModel.OutlineKey.Insert,
                VirtualKey.Delete or VirtualKey.Back => SmartArtDesignStudioViewModel.OutlineKey.Delete,
                VirtualKey.F2 => SmartArtDesignStudioViewModel.OutlineKey.F2,
                _ => null,
            };
            if (key is not { } k) return;
            if (!ViewModel.HandleOutlineKey(k, shift: Down(VirtualKey.Shift), alt: Down(VirtualKey.Menu))) return;
            e.Handled = true;
            if (k is SmartArtDesignStudioViewModel.OutlineKey.Enter or SmartArtDesignStudioViewModel.OutlineKey.Insert or SmartArtDesignStudioViewModel.OutlineKey.F2)
                BeginRenameAndFocus(ViewModel.SelectedNode);
        }

        private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b) ThemeBrush.Set(b, Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        }

        private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
            {
                ThemeBrush.Clear(b, Border.BackgroundProperty);
                b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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

        private void OnToolbarAddNodeClick(object sender, RoutedEventArgs e)
        {
            FocusRenameBox();
        }

        private void OnRowAddChildClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is StudioNodeViewModel node)
            {
                ViewModel.AddChildCommand.Execute(node);
                FocusRenameBox();
            }
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

        // ------------------------------------------------------------------ context menus

        private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StudioNodeViewModel node)
            {
                ViewModel.Select(node);
                OutlineScroll.Focus(FocusState.Pointer);
                ShowRowContextMenu(node, fe, e.GetPosition(fe));
                e.Handled = true;
            }
        }

        private void OnOutlineAreaRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.Handled) return;
            ShowOutlineAreaContextMenu(OutlineScroll, e.GetPosition(OutlineScroll));
            e.Handled = true;
        }

        private void ShowRowContextMenu(StudioNodeViewModel node, UIElement target, Windows.Foundation.Point position)
        {
            var menu = new MenuFlyout();

            var renameItem = new MenuFlyoutItem
            {
                Text = "Rename",
                Icon = new FontIcon { Glyph = "\uE70F" },
                KeyboardAcceleratorTextOverride = "F2"
            };
            renameItem.Click += (_, _) => BeginRenameAndFocus(node);
            menu.Items.Add(renameItem);

            var duplicateItem = new MenuFlyoutItem
            {
                Text = "Duplicate",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                KeyboardAcceleratorTextOverride = "Ctrl+D"
            };
            duplicateItem.Click += (_, _) => ViewModel.DuplicateSelected(node);
            menu.Items.Add(duplicateItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var addChildItem = new MenuFlyoutItem
            {
                Text = "Add child",
                Icon = new FontIcon { Glyph = "\uE710" },
                KeyboardAcceleratorTextOverride = "Insert"
            };
            addChildItem.Click += (_, _) => { ViewModel.AddChild(node); FocusRenameBox(); };
            menu.Items.Add(addChildItem);

            var addSiblingItem = new MenuFlyoutItem
            {
                Text = "Add sibling",
                Icon = new FontIcon { Glyph = "\uE710" },
                KeyboardAcceleratorTextOverride = "Enter"
            };
            addSiblingItem.Click += (_, _) => { ViewModel.AddSibling(node); FocusRenameBox(); };
            menu.Items.Add(addSiblingItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var moveUpItem = new MenuFlyoutItem
            {
                Text = "Move up",
                Icon = new FontIcon { Glyph = "\uE74A" },
                KeyboardAcceleratorTextOverride = "Alt+Up",
                IsEnabled = ViewModel.CanMoveUp
            };
            moveUpItem.Click += (_, _) => ViewModel.MoveUp();
            menu.Items.Add(moveUpItem);

            var moveDownItem = new MenuFlyoutItem
            {
                Text = "Move down",
                Icon = new FontIcon { Glyph = "\uE74B" },
                KeyboardAcceleratorTextOverride = "Alt+Down",
                IsEnabled = ViewModel.CanMoveDown
            };
            moveDownItem.Click += (_, _) => ViewModel.MoveDown();
            menu.Items.Add(moveDownItem);

            var promoteItem = new MenuFlyoutItem
            {
                Text = "Promote",
                Icon = new FontIcon { Glyph = "\uE76B" },
                KeyboardAcceleratorTextOverride = "Shift+Tab",
                IsEnabled = ViewModel.CanPromote
            };
            promoteItem.Click += (_, _) => ViewModel.Promote();
            menu.Items.Add(promoteItem);

            var demoteItem = new MenuFlyoutItem
            {
                Text = "Demote",
                Icon = new FontIcon { Glyph = "\uE76C" },
                KeyboardAcceleratorTextOverride = "Tab",
                IsEnabled = ViewModel.CanDemote
            };
            demoteItem.Click += (_, _) => ViewModel.Demote();
            menu.Items.Add(demoteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem
            {
                Text = "Delete",
                Icon = new FontIcon { Glyph = "\uE74D" },
                KeyboardAcceleratorTextOverride = "Del"
            };
            deleteItem.Click += (_, _) => ViewModel.DeleteSelected(node);
            menu.Items.Add(deleteItem);

            menu.ShowAt(target, position);
        }

        private void ShowOutlineAreaContextMenu(UIElement target, Windows.Foundation.Point position)
        {
            var menu = new MenuFlyout();

            var addRootItem = new MenuFlyoutItem
            {
                Text = ViewModel.IsOutlineEmpty ? "Add first item" : "Add root item",
                Icon = new FontIcon { Glyph = "\uE710" },
                KeyboardAcceleratorTextOverride = "Insert"
            };
            addRootItem.Click += (_, _) => { ViewModel.AddChild(null); FocusRenameBox(); };
            menu.Items.Add(addRootItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyOutlineItem = new MenuFlyoutItem
            {
                Text = "Copy outline as Markdown",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyOutlineItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyOutlineItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy SmartArt Markdown block",
                Icon = new FontIcon { Glyph = "\uE943" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            menu.ShowAt(target, position);
        }

        private void OnLayoutListRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            StudioLayoutItem? item = null;
            for (var d = e.OriginalSource as DependencyObject; d != null && !ReferenceEquals(d, LayoutList); d = VisualTreeHelper.GetParent(d))
            {
                if (d is FrameworkElement { DataContext: StudioLayoutItem candidate })
                {
                    item = candidate;
                    break;
                }
            }
            item ??= ViewModel.SelectedLayout;
            if (item == null) return;

            ViewModel.SelectedLayout = item;
            var menu = new MenuFlyout();

            var useItem = new MenuFlyoutItem
            {
                Text = $"Use \"{item.DisplayName}\"",
                Icon = new FontIcon { Glyph = "\uE73E" }
            };
            useItem.Click += (_, _) =>
            {
                ViewModel.SelectedLayout = item;
                OutlineScroll.Focus(FocusState.Keyboard);
            };
            menu.Items.Add(useItem);

            if (!string.Equals(ViewModel.SelectedCategory, item.Category, StringComparison.Ordinal))
            {
                var filterCatItem = new MenuFlyoutItem
                {
                    Text = $"Filter to {item.Category}",
                    Icon = new FontIcon { Glyph = "\uE71C" }
                };
                filterCatItem.Click += (_, _) => ViewModel.SelectedCategory = item.Category;
                menu.Items.Add(filterCatItem);
            }
            else if (!string.Equals(ViewModel.SelectedCategory, "All", StringComparison.Ordinal))
            {
                var showAllItem = new MenuFlyoutItem
                {
                    Text = "Show all categories",
                    Icon = new FontIcon { Glyph = "\uE71C" }
                };
                showAllItem.Click += (_, _) => ViewModel.SelectedCategory = "All";
                menu.Items.Add(showAllItem);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy SmartArt Markdown block",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var insertItem = new MenuFlyoutItem
            {
                Text = "Insert into document",
                Icon = new FontIcon { Glyph = "\uE8A5" },
                KeyboardAcceleratorTextOverride = "Ctrl+Enter",
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            insertItem.Click += (_, _) => ViewModel.InsertIntoDocumentCommand.Execute(null);
            menu.Items.Add(insertItem);

            menu.ShowAt(LayoutList, e.GetPosition(LayoutList));
            e.Handled = true;
        }

        private void CopyOutlineMarkdown()
        {
            string md = (ViewModel.MarkdownText ?? "").Trim();
            if (string.IsNullOrEmpty(md)) return;
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(md);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                ViewModel.StatusMessage = "Copied outline bullets to clipboard.";
            }
            catch { }
        }

        private void CopySmartArtBlock()
        {
            if (!ViewModel.TryBuildSmartArtBlock(out string block, out string name, out string err))
            {
                ViewModel.StatusMessage = err;
                return;
            }
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(block.Trim());
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                ViewModel.StatusMessage = $"Copied {name} SmartArt Markdown block to clipboard.";
            }
            catch { }
        }

        // Window-level shortcuts: Delete/Backspace = delete node, F2 = rename, Ctrl+D = duplicate,
        // Ctrl+Z/Y = undo/redo, Ctrl+F = focus layout search, Ctrl+Enter = insert into document.
        // TextBoxes (rename box, Markdown Data) keep their native keys — focus guard.
        private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                        & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (ctrl && e.Key == VirtualKey.F)
            {
                LayoutSearch.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == VirtualKey.Enter)
            {
                if (ViewModel.InsertIntoDocumentCommand.CanExecute(null))
                {
                    ViewModel.InsertIntoDocumentCommand.Execute(null);
                    e.Handled = true;
                }
                return;
            }

            if ((sender as UIElement)?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox) return;

            if (ctrl && e.Key == VirtualKey.Z) { ViewModel.UndoCommand.Execute(null); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.Y) { ViewModel.RedoCommand.Execute(null); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.D) { ViewModel.DuplicateSelectedCommand.Execute(null); e.Handled = true; }
            else if (e.Key is VirtualKey.Delete or VirtualKey.Back) { ViewModel.DeleteSelectedCommand.Execute(null); e.Handled = true; }
            else if (e.Key == VirtualKey.F2) { BeginRenameAndFocus(); e.Handled = true; }
        }
    }
}
