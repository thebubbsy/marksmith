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
                AnnounceLiveRegion(StatusBarText, ViewModel.StatusMessage, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite, raiseEvent: false);
            }
            if (LayoutCountLabel != null && !string.IsNullOrEmpty(ViewModel.LayoutCountText))
            {
                AnnounceLiveRegion(LayoutCountLabel, ViewModel.LayoutCountText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite, raiseEvent: false);
            }
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.StatusMessage) && StatusBarText != null && !string.IsNullOrEmpty(ViewModel.StatusMessage))
                {
                    AnnounceLiveRegion(StatusBarText, ViewModel.StatusMessage, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
                }
                else if (e.PropertyName == nameof(ViewModel.LayoutCountText) && LayoutCountLabel != null)
                {
                    AnnounceLiveRegion(LayoutCountLabel, ViewModel.LayoutCountText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
                }
                else if (e.PropertyName == nameof(ViewModel.HasNoLayoutMatches) && ViewModel.HasNoLayoutMatches && NoMatchesTitleText != null)
                {
                    AnnounceLiveRegion(NoMatchesTitleText, "No layouts match", Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
                }
                else if (e.PropertyName == nameof(ViewModel.PreviewHint) && ViewModel.HasPreviewHint && PreviewHintBar != null)
                {
                    AnnounceLiveRegion(PreviewHintBar, ViewModel.PreviewHint, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
                }
                else if (e.PropertyName == nameof(ViewModel.SelectedLayout) && ViewModel.SelectedLayout is { } layout)
                {
                    LayoutList.ScrollIntoView(layout, ScrollIntoViewAlignment.Leading);
                }
                else if (e.PropertyName == nameof(ViewModel.SelectedNode))
                {
                    ScrollSelectedOutlineRowIntoView();
                }
            };
            this.Activated += OnWindowActivated;
            // Transparent page so the preview card's theme brush shows through in Light and Dark
            // (the rendered SmartArt is a self-contained light card either way).
            PreviewWebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
            HoverPolish.Track(this.RootGrid);
            this.RootGrid.ActualThemeChanged += (_, _) => RefreshWebView();
            // Open in the layout search (typing filters at once), not on the first title-bar button.
            this.RootGrid.Loaded += (_, _) =>
            {
                LayoutSearch.Focus(FocusState.Programmatic);
                if (ViewModel.SelectedLayout is { } layout) LayoutList.ScrollIntoView(layout, ScrollIntoViewAlignment.Leading);
            };
        }

        private static void AnnounceLiveRegion(
            FrameworkElement? element,
            string? text,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting setting,
            bool raiseEvent = true)
        {
            if (element == null || string.IsNullOrWhiteSpace(text)) return;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(element, setting);
            if (!raiseEvent) return;
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(element)
                       ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }

        private void ScrollSelectedOutlineRowIntoView()
        {
            if (ViewModel.SelectedNode is not { } selected) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var border in FindVisualChildren<Border>(OutlineScroll))
                {
                    if (ReferenceEquals(border.DataContext, selected))
                    {
                        border.StartBringIntoView();
                        break;
                    }
                }
            });
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

        private void OnLayoutSearchDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (FindDescendant<TextBox>(LayoutSearch) is { } tb)
            {
                tb.SelectAll();
            }
        }

        private void OnLayoutSearchRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();

            var copyQueryItem = new MenuFlyoutItem
            {
                Text = "Copy search query",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrEmpty(LayoutSearch.Text)
            };
            copyQueryItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(LayoutSearch.Text);
                ViewModel.StatusMessage = $"Copied search query: {LayoutSearch.Text}";
            };
            menu.Items.Add(copyQueryItem);

            var pasteQueryItem = new MenuFlyoutItem
            {
                Text = "Paste search query",
                Icon = new FontIcon { Glyph = "\uE77F" }
            };
            pasteQueryItem.Click += async (_, _) =>
            {
                try
                {
                    var pkg = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (pkg.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    {
                        var text = await pkg.GetTextAsync();
                        if (!string.IsNullOrEmpty(text))
                        {
                            LayoutSearch.Text = text.Trim();
                            LayoutSearch.Focus(FocusState.Programmatic);
                            ViewModel.StatusMessage = $"Pasted search query: {LayoutSearch.Text}";
                        }
                    }
                }
                catch
                {
                    // Clipboard may be unavailable or locked
                }
            };
            menu.Items.Add(pasteQueryItem);

            var clearItem = new MenuFlyoutItem
            {
                Text = "Clear search (Esc)",
                Icon = new FontIcon { Glyph = "\uE894" },
                IsEnabled = !string.IsNullOrEmpty(LayoutSearch.Text)
            };
            clearItem.Click += (_, _) =>
            {
                LayoutSearch.Text = string.Empty;
                LayoutSearch.Focus(FocusState.Programmatic);
            };
            menu.Items.Add(clearItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var categorySubmenu = new MenuFlyoutSubItem
            {
                Text = "Filter by category",
                Icon = new FontIcon { Glyph = "\uE8EC" }
            };
            foreach (var category in ViewModel.Categories)
            {
                var catItem = new MenuFlyoutItem
                {
                    Text = category == ViewModel.SelectedCategory ? $"{category} ✓" : category,
                    Icon = new FontIcon { Glyph = "\uE8EC" }
                };
                catItem.Click += (_, _) =>
                {
                    ViewModel.SelectedCategory = category;
                    ViewModel.StatusMessage = $"Switched category filter to {category}.";
                };
                categorySubmenu.Items.Add(catItem);
            }
            menu.Items.Add(categorySubmenu);

            var focusListItem = new MenuFlyoutItem
            {
                Text = "Browse layouts (Down / Enter)",
                Icon = new FontIcon { Glyph = "\uE8B3" }
            };
            focusListItem.Click += (_, _) =>
            {
                LayoutList.Focus(FocusState.Keyboard);
                if (ViewModel.SelectedLayout is { } layout)
                    LayoutList.ScrollIntoView(layout, ScrollIntoViewAlignment.Leading);
            };
            menu.Items.Add(focusListItem);

            var focusOutlineItem = new MenuFlyoutItem
            {
                Text = "Focus outline editor (F6)",
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            focusOutlineItem.Click += (_, _) => OutlineScroll.Focus(FocusState.Keyboard);
            menu.Items.Add(focusOutlineItem);

            menu.ShowAt(LayoutSearch, e.GetPosition(LayoutSearch));
        }

        private void OnLayoutCategoryComboKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                if (LayoutCategoryCombo.IsDropDownOpen)
                {
                    LayoutCategoryCombo.IsDropDownOpen = false;
                }
                else if (ViewModel.SelectedCategory != "All")
                {
                    ViewModel.SelectedCategory = "All";
                    ViewModel.StatusMessage = "Reset layout category filter to All.";
                }
                else
                {
                    LayoutSearch?.Focus(FocusState.Keyboard);
                }
                e.Handled = true;
            }
            else if (e.Key is VirtualKey.Enter or VirtualKey.Down && !LayoutCategoryCombo.IsDropDownOpen)
            {
                LayoutList?.Focus(FocusState.Keyboard);
                e.Handled = true;
            }
        }

        private void OnLayoutCategoryComboDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            CycleNextLayoutCategory();
        }

        private void CycleNextLayoutCategory()
        {
            var cats = ViewModel.Categories;
            if (cats.Count <= 1) return;
            int idx = cats.IndexOf(ViewModel.SelectedCategory);
            int nextIdx = (idx + 1) % cats.Count;
            ViewModel.SelectedCategory = cats[nextIdx];
            ViewModel.StatusMessage = $"Filtered layouts by {cats[nextIdx]}.";
        }

        private void CyclePreviousLayoutCategory()
        {
            var cats = ViewModel.Categories;
            if (cats.Count <= 1) return;
            int idx = cats.IndexOf(ViewModel.SelectedCategory);
            int prevIdx = (idx - 1 + cats.Count) % cats.Count;
            ViewModel.SelectedCategory = cats[prevIdx];
            ViewModel.StatusMessage = $"Filtered layouts by {cats[prevIdx]}.";
        }

        private void OnLayoutCategoryComboRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (LayoutCategoryCombo is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            foreach (var category in ViewModel.Categories)
            {
                var glyph = category switch
                {
                    "All" => "\uE71D",
                    "Hierarchy" => "\uF003",
                    "Process" => "\uE81E",
                    "Cycle" => "\uE777",
                    "Matrix" => "\uE80A",
                    "Pyramid" => "\uE7AD",
                    "Venn" => "\uE707",
                    "Relationship" => "\uE71B",
                    "Picture List" => "\uEB9F",
                    "List" => "\uE8A5",
                    _ => "\uE71D"
                };
                bool isSelected = string.Equals(ViewModel.SelectedCategory, category, StringComparison.OrdinalIgnoreCase);
                var item = new MenuFlyoutItem
                {
                    Text = isSelected ? $"{category} (active)" : category,
                    Icon = new FontIcon { Glyph = glyph }
                };
                string cat = category;
                item.Click += (_, _) =>
                {
                    ViewModel.SelectedCategory = cat;
                    ViewModel.StatusMessage = $"Filtered layouts by {cat}.";
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            var nextItem = new MenuFlyoutItem
            {
                Text = "Next category (Ctrl+Tab)",
                Icon = new FontIcon { Glyph = "\uE76C" }
            };
            nextItem.Click += (_, _) => CycleNextLayoutCategory();
            menu.Items.Add(nextItem);

            var prevItem = new MenuFlyoutItem
            {
                Text = "Previous category (Ctrl+Shift+Tab)",
                Icon = new FontIcon { Glyph = "\uE76B" }
            };
            prevItem.Click += (_, _) => CyclePreviousLayoutCategory();
            menu.Items.Add(prevItem);

            var copyItem = new MenuFlyoutItem
            {
                Text = $"Copy category name ({ViewModel.SelectedCategory})",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyItem.Click += (_, _) =>
            {
                _ = MarkSmith.Services.ClipboardWriter.TrySetTextAsync(ViewModel.SelectedCategory);
                ViewModel.StatusMessage = $"Copied category name '{ViewModel.SelectedCategory}'.";
            };
            menu.Items.Add(copyItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var resetItem = new MenuFlyoutItem
            {
                Text = "Reset category to All",
                Icon = new FontIcon { Glyph = "\uE72C" }
            };
            resetItem.Click += (_, _) =>
            {
                ViewModel.SelectedCategory = "All";
                ViewModel.StatusMessage = "Reset layout filter to All categories.";
            };
            menu.Items.Add(resetItem);

            var focusSearchItem = new MenuFlyoutItem
            {
                Text = "Focus layout search",
                Icon = new FontIcon { Glyph = "\uE721" }
            };
            focusSearchItem.Click += (_, _) => LayoutSearch?.Focus(FocusState.Keyboard);
            menu.Items.Add(focusSearchItem);

            var focusListItem = new MenuFlyoutItem
            {
                Text = "Focus layout list",
                Icon = new FontIcon { Glyph = "\uE8B3" }
            };
            focusListItem.Click += (_, _) => LayoutList?.Focus(FocusState.Keyboard);
            menu.Items.Add(focusListItem);

            menu.ShowAt(LayoutCategoryCombo, e.GetPosition(LayoutCategoryCombo));
        }

        private void OnNoMatchesPanelRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();

            var clearSearchItem = new MenuFlyoutItem
            {
                Text = "Clear search filter",
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            clearSearchItem.Click += (_, _) =>
            {
                LayoutSearch.Text = string.Empty;
                ViewModel.StatusMessage = "Cleared layout search.";
            };
            menu.Items.Add(clearSearchItem);

            var showAllItem = new MenuFlyoutItem
            {
                Text = "Set category to All",
                Icon = new FontIcon { Glyph = "\uE71D" }
            };
            showAllItem.Click += (_, _) =>
            {
                ViewModel.SelectedCategory = "All";
                ViewModel.StatusMessage = "Category filter set to All.";
            };
            menu.Items.Add(showAllItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var focusSearchItem = new MenuFlyoutItem
            {
                Text = "Focus search box",
                Icon = new FontIcon { Glyph = "\uE721" }
            };
            focusSearchItem.Click += (_, _) => LayoutSearch?.Focus(FocusState.Programmatic);
            menu.Items.Add(focusSearchItem);

            var anchor = (sender as FrameworkElement) ?? LayoutSearch;
            menu.ShowAt(anchor, e.GetPosition(anchor));
        }

        private void OnLayoutListKeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool shiftDown = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                              & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (e.Key == VirtualKey.Enter)
            {
                if (ViewModel.SelectedNode == null && ViewModel.OutlineRows.Count > 0)
                {
                    ViewModel.Select(ViewModel.OutlineRows[0]);
                }
                OutlineScroll.Focus(FocusState.Keyboard);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                LayoutSearch.Focus(FocusState.Keyboard);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Application || (e.Key == VirtualKey.F10 && shiftDown))
            {
                if (ViewModel.SelectedLayout is { } item)
                {
                    ShowLayoutContextMenu(item, LayoutList, new Windows.Foundation.Point(48, 36));
                    e.Handled = true;
                }
            }
        }

        private void OnLayoutTilePointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid g && g.Children.FirstOrDefault() is Border thumb)
            {
                thumb.Scale = new System.Numerics.Vector3(1.05f, 1.05f, 1f);
            }
        }

        private void OnLayoutTilePointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid g && g.Children.FirstOrDefault() is Border thumb)
            {
                thumb.Scale = new System.Numerics.Vector3(1f, 1f, 1f);
            }
        }

        private void OnLayoutTilePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid g && g.Children.FirstOrDefault() is Border thumb)
            {
                thumb.Scale = new System.Numerics.Vector3(0.96f, 0.96f, 1f);
            }
        }

        private void OnLayoutTilePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid g && g.Children.FirstOrDefault() is Border thumb)
            {
                thumb.Scale = new System.Numerics.Vector3(1.05f, 1.05f, 1f);
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
                bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
                string html = BuildWrapperHtml(ViewModel.PreviewHtml, isDark);
                PreviewWebView.CoreWebView2.NavigateToString(html);
            }
        }

        // The renderer emits a card sized for documents (800 wide, as tall as the layout needs) with
        // a "Layout: …" caption. In the studio the card fills the pane and the drawing scales to fit
        // it either way (the SVG's default xMidYMid meet), and the caption is dropped because the
        // pane header names the layout already.
        private static string BuildWrapperHtml(string body, bool isDarkTheme = false)
        {
            string cardBg = isDarkTheme ? "#F8FAFC" : "#FFFFFF";
            string cardBorder = isDarkTheme ? "rgba(148,163,184,0.38)" : "rgba(15,23,42,0.10)";
            string cardShadow = isDarkTheme ? "0 4px 16px rgba(0,0,0,0.28)" : "0 2px 8px rgba(15,23,42,0.05)";
            return $@"<!DOCTYPE html>
<html><head><meta charset=""utf-8""/>
<style>
  html,body{{height:100%;margin:0}}
  body{{padding:16px;box-sizing:border-box;background:transparent;display:flex}}
  .smartart-container{{width:100%!important;max-width:none!important;height:100%!important;display:flex;flex-direction:column;padding:12px;box-sizing:border-box;background:{cardBg}!important;border:1px solid {cardBorder}!important;border-radius:8px!important;box-shadow:{cardShadow}!important}}
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
                VirtualKey.Left => SmartArtDesignStudioViewModel.OutlineKey.Left,
                VirtualKey.Right => SmartArtDesignStudioViewModel.OutlineKey.Right,
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

        private void OnRowCommitRenameClick(object sender, RoutedEventArgs e)
        {
            ViewModel.CommitRename();
            OutlineScroll.Focus(FocusState.Keyboard);
        }

        private void OnRowCancelRenameClick(object sender, RoutedEventArgs e)
        {
            ViewModel.CancelRename();
            OutlineScroll.Focus(FocusState.Keyboard);
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

        private void OnOutlineEmptyPanelRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();

            var addFirstItem = new MenuFlyoutItem
            {
                Text = "Add first item",
                Icon = new FontIcon { Glyph = "\uE710" },
                KeyboardAcceleratorTextOverride = "Insert"
            };
            addFirstItem.Click += OnAddFirstNodeClick;
            menu.Items.Add(addFirstItem);

            var switchMarkdownItem = new MenuFlyoutItem
            {
                Text = "Switch to Markdown editor",
                Icon = new FontIcon { Glyph = "\uE943" },
                KeyboardAcceleratorTextOverride = "Ctrl+2"
            };
            switchMarkdownItem.Click += (_, _) =>
            {
                if (EditorTabs != null && MarkdownTab != null) EditorTabs.SelectedItem = MarkdownTab;
            };
            menu.Items.Add(switchMarkdownItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var pasteItem = new MenuFlyoutItem
            {
                Text = "Paste bullets from clipboard",
                Icon = new FontIcon { Glyph = "\uE77F" },
                KeyboardAcceleratorTextOverride = "Ctrl+V"
            };
            pasteItem.Click += async (_, _) =>
            {
                try
                {
                    var pkg = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (pkg.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    {
                        var text = await pkg.GetTextAsync();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            ViewModel.MarkdownText = text;
                            ViewModel.StatusMessage = "Pasted outline Markdown from clipboard.";
                        }
                    }
                }
                catch { }
            };
            menu.Items.Add(pasteItem);

            var copySmartArtItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C"
            };
            copySmartArtItem.Click += OnCopySmartArtBlockClick;
            menu.Items.Add(copySmartArtItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var focusSearchItem = new MenuFlyoutItem
            {
                Text = "Search layouts",
                Icon = new FontIcon { Glyph = "\uE721" },
                KeyboardAcceleratorTextOverride = "Ctrl+F"
            };
            focusSearchItem.Click += (_, _) =>
            {
                LayoutSearch?.Focus(FocusState.Programmatic);
            };
            menu.Items.Add(focusSearchItem);

            if (sender is FrameworkElement fe)
                menu.ShowAt(fe, e.GetPosition(fe));
            else if (OutlineEmptyPanel is not null)
                menu.ShowAt(OutlineEmptyPanel, e.GetPosition(OutlineEmptyPanel));
            else
                menu.ShowAt(this.Content, e.GetPosition(this.Content));
        }

        private void OnUndoButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (UndoStudioButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var undoItem = new MenuFlyoutItem
            {
                Text = "Undo",
                Icon = new FontIcon { Glyph = "\uE7A7" },
                KeyboardAcceleratorTextOverride = "Ctrl+Z",
                IsEnabled = ViewModel.CanUndo
            };
            undoItem.Click += (_, _) => { if (ViewModel.CanUndo) ViewModel.UndoCommand.Execute(null); };
            menu.Items.Add(undoItem);

            var redoItem = new MenuFlyoutItem
            {
                Text = "Redo",
                Icon = new FontIcon { Glyph = "\uE7A6" },
                KeyboardAcceleratorTextOverride = "Ctrl+Y",
                IsEnabled = ViewModel.CanRedo
            };
            redoItem.Click += (_, _) => { if (ViewModel.CanRedo) ViewModel.RedoCommand.Execute(null); };
            menu.Items.Add(redoItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                KeyboardAcceleratorTextOverride = "Ctrl+C"
            };
            copyBulletsItem.Click += OnCopyOutlineMarkdownClick;
            menu.Items.Add(copyBulletsItem);

            var resetItem = new MenuFlyoutItem
            {
                Text = "Reset layout filter",
                Icon = new FontIcon { Glyph = "\uE72C" }
            };
            resetItem.Click += (_, _) =>
            {
                ViewModel.SelectedCategory = "All";
                if (LayoutSearch != null) LayoutSearch.Text = "";
                ViewModel.StatusMessage = "Reset layout filters.";
            };
            menu.Items.Add(resetItem);

            menu.ShowAt(UndoStudioButton, e.GetPosition(UndoStudioButton));
        }

        private void OnRedoButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (RedoStudioButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var redoItem = new MenuFlyoutItem
            {
                Text = "Redo",
                Icon = new FontIcon { Glyph = "\uE7A6" },
                KeyboardAcceleratorTextOverride = "Ctrl+Y",
                IsEnabled = ViewModel.CanRedo
            };
            redoItem.Click += (_, _) => { if (ViewModel.CanRedo) ViewModel.RedoCommand.Execute(null); };
            menu.Items.Add(redoItem);

            var undoItem = new MenuFlyoutItem
            {
                Text = "Undo",
                Icon = new FontIcon { Glyph = "\uE7A7" },
                KeyboardAcceleratorTextOverride = "Ctrl+Z",
                IsEnabled = ViewModel.CanUndo
            };
            undoItem.Click += (_, _) => { if (ViewModel.CanUndo) ViewModel.UndoCommand.Execute(null); };
            menu.Items.Add(undoItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                KeyboardAcceleratorTextOverride = "Ctrl+C"
            };
            copyBulletsItem.Click += OnCopyOutlineMarkdownClick;
            menu.Items.Add(copyBulletsItem);

            menu.ShowAt(RedoStudioButton, e.GetPosition(RedoStudioButton));
        }

        private void OnInsertIntoDocumentButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (InsertIntoDocumentButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var insertItem = new MenuFlyoutItem
            {
                Text = "Insert into document",
                Icon = new FontIcon { Glyph = "\uE8A5" },
                KeyboardAcceleratorTextOverride = "Ctrl+Enter"
            };
            insertItem.Click += (_, _) => ViewModel.InsertIntoDocumentCommand.Execute(null);
            menu.Items.Add(insertItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C"
            };
            copyBlockItem.Click += OnCopySmartArtBlockClick;
            menu.Items.Add(copyBlockItem);

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                KeyboardAcceleratorTextOverride = "Ctrl+C"
            };
            copyBulletsItem.Click += OnCopyOutlineMarkdownClick;
            menu.Items.Add(copyBulletsItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var searchLayoutsItem = new MenuFlyoutItem
            {
                Text = "Search layouts",
                Icon = new FontIcon { Glyph = "\uE721" },
                KeyboardAcceleratorTextOverride = "Ctrl+F"
            };
            searchLayoutsItem.Click += (_, _) => LayoutSearch?.Focus(FocusState.Programmatic);
            menu.Items.Add(searchLayoutsItem);

            menu.ShowAt(InsertIntoDocumentButton, e.GetPosition(InsertIntoDocumentButton));
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

        private void OnOutlineToolbarRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (OutlineToolbar is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var addChildItem = new MenuFlyoutItem
            {
                Text = "Add child (Insert)",
                Icon = new FontIcon { Glyph = "\uE710" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            addChildItem.Click += (_, _) => { ViewModel.AddChildCommand.Execute(null); FocusRenameBox(); };
            menu.Items.Add(addChildItem);

            var addSiblingItem = new MenuFlyoutItem
            {
                Text = "Add sibling (Enter)",
                Icon = new FontIcon { Glyph = "\uE710" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            addSiblingItem.Click += (_, _) => { ViewModel.AddSiblingCommand.Execute(null); FocusRenameBox(); };
            menu.Items.Add(addSiblingItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var renameItem = new MenuFlyoutItem
            {
                Text = "Rename (F2)",
                Icon = new FontIcon { Glyph = "\uE70F" },
                IsEnabled = ViewModel.HasSelection
            };
            renameItem.Click += OnRenameClick;
            menu.Items.Add(renameItem);

            var duplicateItem = new MenuFlyoutItem
            {
                Text = "Duplicate (Ctrl+D)",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = ViewModel.HasSelection
            };
            duplicateItem.Click += (_, _) => ViewModel.DuplicateSelectedCommand.Execute(null);
            menu.Items.Add(duplicateItem);

            var deleteItem = new MenuFlyoutItem
            {
                Text = "Delete (Del)",
                Icon = new FontIcon { Glyph = "\uE74D" },
                IsEnabled = ViewModel.HasSelection
            };
            deleteItem.Click += (_, _) => ViewModel.DeleteSelectedCommand.Execute(null);
            menu.Items.Add(deleteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var moveUpItem = new MenuFlyoutItem
            {
                Text = "Move up (Alt+Up)",
                Icon = new FontIcon { Glyph = "\uE74A" },
                IsEnabled = ViewModel.CanMoveUp
            };
            moveUpItem.Click += (_, _) => ViewModel.MoveUpCommand.Execute(null);
            menu.Items.Add(moveUpItem);

            var moveDownItem = new MenuFlyoutItem
            {
                Text = "Move down (Alt+Down)",
                Icon = new FontIcon { Glyph = "\uE74B" },
                IsEnabled = ViewModel.CanMoveDown
            };
            moveDownItem.Click += (_, _) => ViewModel.MoveDownCommand.Execute(null);
            menu.Items.Add(moveDownItem);

            var promoteItem = new MenuFlyoutItem
            {
                Text = "Promote (Shift+Tab)",
                Icon = new FontIcon { Glyph = "\uE76B" },
                IsEnabled = ViewModel.CanPromote
            };
            promoteItem.Click += (_, _) => ViewModel.PromoteCommand.Execute(null);
            menu.Items.Add(promoteItem);

            var demoteItem = new MenuFlyoutItem
            {
                Text = "Demote (Tab)",
                Icon = new FontIcon { Glyph = "\uE76C" },
                IsEnabled = ViewModel.CanDemote
            };
            demoteItem.Click += (_, _) => ViewModel.DemoteCommand.Execute(null);
            menu.Items.Add(demoteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets (Ctrl+C)",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            menu.ShowAt(OutlineToolbar, e.GetPosition(OutlineToolbar));
        }

        private void OnCopyOutlineMarkdownButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (CopyOutlineMarkdownButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets (Ctrl+C)",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var editMarkdownItem = new MenuFlyoutItem
            {
                Text = "Switch to Markdown editor (Ctrl+2)",
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            editMarkdownItem.Click += (_, _) =>
            {
                if (MarkdownTab is not null && EditorTabs is not null) EditorTabs.SelectedItem = MarkdownTab;
            };
            menu.Items.Add(editMarkdownItem);

            menu.ShowAt(CopyOutlineMarkdownButton, e.GetPosition(CopyOutlineMarkdownButton));
        }

        private void OnCopySmartArtBlockButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (CopySmartArtBlockButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets (Ctrl+C)",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            var copyTitleItem = new MenuFlyoutItem
            {
                Text = "Copy layout title",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrWhiteSpace(ViewModel.PreviewTitle)
            };
            copyTitleItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(ViewModel.PreviewTitle ?? "");
                ViewModel.StatusMessage = "Copied layout title to clipboard";
            };
            menu.Items.Add(copyTitleItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var refreshItem = new MenuFlyoutItem
            {
                Text = "Refresh preview (F5)",
                Icon = new FontIcon { Glyph = "\uE72C" }
            };
            refreshItem.Click += (_, _) =>
            {
                RefreshWebView();
                ViewModel.StatusMessage = "Preview refreshed.";
            };
            menu.Items.Add(refreshItem);

            menu.ShowAt(CopySmartArtBlockButton, e.GetPosition(CopySmartArtBlockButton));
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

            var copyTextItem = new MenuFlyoutItem
            {
                Text = "Copy item text",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                KeyboardAcceleratorTextOverride = "Ctrl+C",
                IsEnabled = !string.IsNullOrWhiteSpace(node.Text)
            };
            copyTextItem.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(node.Text))
                {
                    _ = Services.ClipboardWriter.TrySetTextAsync(node.Text);
                    ViewModel.StatusMessage = $"Copied \"{node.Text}\" to clipboard.";
                }
            };
            menu.Items.Add(copyTextItem);

            var copyBulletItem = new MenuFlyoutItem
            {
                Text = "Copy item as bullet",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !string.IsNullOrWhiteSpace(node.Text)
            };
            copyBulletItem.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(node.Text))
                {
                    var indent = new string(' ', Math.Max(0, node.Depth) * 2);
                    _ = Services.ClipboardWriter.TrySetTextAsync($"{indent}* {node.Text}");
                    ViewModel.StatusMessage = $"Copied \"{node.Text}\" bullet to clipboard.";
                }
            };
            menu.Items.Add(copyBulletItem);

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

            menu.Items.Add(new MenuFlyoutSeparator());

            var insertItem = new MenuFlyoutItem
            {
                Text = "Insert into document",
                Icon = new FontIcon { Glyph = "\uE8A5" },
                KeyboardAcceleratorTextOverride = "Ctrl+Enter",
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            insertItem.Click += (_, _) => ViewModel.InsertIntoDocumentCommand.Execute(null);
            menu.Items.Add(insertItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var refreshItem = new MenuFlyoutItem
            {
                Text = "Refresh preview (F5)",
                Icon = new FontIcon { Glyph = "\uE72C" }
            };
            refreshItem.Click += (_, _) =>
            {
                RefreshWebView();
                ViewModel.StatusMessage = "Preview refreshed.";
            };
            menu.Items.Add(refreshItem);

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

            ShowLayoutContextMenu(item, LayoutList, e.GetPosition(LayoutList));
            e.Handled = true;
        }

        private void ShowLayoutContextMenu(StudioLayoutItem item, UIElement target, Windows.Foundation.Point position)
        {
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

            menu.ShowAt(target, position);
        }

        private void OnPreviewPaneRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not UIElement target) return;
            var menu = new MenuFlyout();

            var refreshItem = new MenuFlyoutItem
            {
                Text = "Refresh preview (F5)",
                Icon = new FontIcon { Glyph = "\uE72C" }
            };
            refreshItem.Click += (_, _) =>
            {
                RefreshWebView();
                ViewModel.StatusMessage = "Preview refreshed.";
            };
            menu.Items.Add(refreshItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy SmartArt Markdown block (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var copyOutlineItem = new MenuFlyoutItem
            {
                Text = "Copy outline as Markdown (Ctrl+C)",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyOutlineItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyOutlineItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var insertItem = new MenuFlyoutItem
            {
                Text = "Insert into document (Ctrl+Enter)",
                Icon = new FontIcon { Glyph = "\uE8A5" },
                KeyboardAcceleratorTextOverride = "Ctrl+Enter",
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            insertItem.Click += (_, _) => ViewModel.InsertIntoDocumentCommand.Execute(null);
            menu.Items.Add(insertItem);

            menu.Items.Add(new MenuFlyoutSeparator());
            AddPaletteSubmenu(menu);

            menu.ShowAt(target, e.GetPosition(target));
            e.Handled = true;
        }

        private void OnCopyOutlineMarkdownClick(object sender, RoutedEventArgs e) => CopyOutlineMarkdown();

        private void OnCopySmartArtBlockClick(object sender, RoutedEventArgs e) => CopySmartArtBlock();

        private void CopyOutlineMarkdown()
        {
            string md = (ViewModel.MarkdownText ?? "").Trim();
            if (string.IsNullOrEmpty(md)) return;
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(md);
                Services.ClipboardWriter.Set(dp);
                ViewModel.StatusMessage = "Copied outline bullets to clipboard.";
                if (CopyOutlineMarkdownButton is not null)
                {
                    Services.CopyFeedback.ApplyButtonTick(CopyOutlineMarkdownButton, null);
                }
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
                Services.ClipboardWriter.Set(dp);
                ViewModel.StatusMessage = $"Copied {name} SmartArt Markdown block to clipboard.";
                if (CopySmartArtBlockButton is not null)
                {
                    Services.CopyFeedback.ApplyButtonTick(CopySmartArtBlockButton, null);
                }
            }
            catch { }
        }

        // Window-level shortcuts: Delete/Backspace = delete node, F2 = rename, Ctrl+D = duplicate,
        // Ctrl+Z/Y = undo/redo, Ctrl+F = focus layout search, Ctrl+Enter = insert into document,
        // Ctrl+Shift+C = copy SmartArt block, Ctrl+C = copy outline bullets, Ctrl+1/2 = switch tabs.
        // TextBoxes (rename box, Markdown Data) keep their native keys — focus guard.
        private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                        & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
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
            if (ctrl && shift && e.Key == VirtualKey.C)
            {
                CopySmartArtBlock();
                e.Handled = true;
                return;
            }
            if (ctrl && (e.Key is VirtualKey.Number1 or VirtualKey.NumberPad1))
            {
                EditorTabs.SelectedItem = OutlineTab;
                e.Handled = true;
                return;
            }
            if (ctrl && (e.Key is VirtualKey.Number2 or VirtualKey.NumberPad2))
            {
                EditorTabs.SelectedItem = MarkdownTab;
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.F5)
            {
                RefreshWebView();
                ViewModel.StatusMessage = "Preview refreshed.";
                e.Handled = true;
                return;
            }

            if ((sender as UIElement)?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox) return;

            if (ctrl && e.Key == VirtualKey.C)
            {
                if (ViewModel.SelectedNode is { } selected && !string.IsNullOrWhiteSpace(selected.Text))
                {
                    _ = Services.ClipboardWriter.TrySetTextAsync(selected.Text);
                    ViewModel.StatusMessage = $"Copied \"{selected.Text}\" to clipboard.";
                    e.Handled = true;
                    return;
                }
                CopyOutlineMarkdown();
                e.Handled = true;
            }
            else if (ctrl && e.Key == VirtualKey.Z) { ViewModel.UndoCommand.Execute(null); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.Y) { ViewModel.RedoCommand.Execute(null); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.D) { ViewModel.DuplicateSelectedCommand.Execute(null); e.Handled = true; }
            else if (e.Key is VirtualKey.Delete or VirtualKey.Back) { ViewModel.DeleteSelectedCommand.Execute(null); e.Handled = true; }
            else if (e.Key == VirtualKey.F2) { BeginRenameAndFocus(); e.Handled = true; }
        }

        private void OnMarkdownTextBoxRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (MarkdownTextBox is null) return;
            if (!string.IsNullOrEmpty(MarkdownTextBox.SelectedText)) return;
            e.Handled = true;
            ShowMarkdownTextBoxContextMenu(e.GetPosition(MarkdownTextBox));
        }

        private void ShowMarkdownTextBoxContextMenu(Windows.Foundation.Point? position)
        {
            if (MarkdownTextBox is null) return;
            var menu = new MenuFlyout();

            var insertItem = new MenuFlyoutItem
            {
                Text = "Insert into document (Ctrl+Enter)",
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            insertItem.Click += (_, _) =>
            {
                if (ViewModel.InsertIntoDocumentCommand.CanExecute(null))
                    ViewModel.InsertIntoDocumentCommand.Execute(null);
            };
            menu.Items.Add(insertItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets (Ctrl+C)",
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var pasteItem = new MenuFlyoutItem
            {
                Text = "Paste bullets (Ctrl+V)",
                Icon = new FontIcon { Glyph = "\uE77F" }
            };
            pasteItem.Click += async (_, _) =>
            {
                try
                {
                    var text = await Windows.ApplicationModel.DataTransfer.Clipboard.GetContent().GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        MarkdownTextBox.SelectedText = text;
                    }
                }
                catch { }
            };
            menu.Items.Add(pasteItem);

            var selectAllItem = new MenuFlyoutItem
            {
                Text = "Select all (Ctrl+A)",
                Icon = new FontIcon { Glyph = "\uE8B3" }
            };
            selectAllItem.Click += (_, _) => MarkdownTextBox.SelectAll();
            menu.Items.Add(selectAllItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var switchToOutlineItem = new MenuFlyoutItem
            {
                Text = "Switch to Outline view (Ctrl+1)",
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            switchToOutlineItem.Click += (_, _) =>
            {
                EditorTabs.SelectedItem = OutlineTab;
            };
            menu.Items.Add(switchToOutlineItem);

            if (position.HasValue) menu.ShowAt(MarkdownTextBox, position.Value);
            else menu.ShowAt(MarkdownTextBox);
        }

        private void OnStatusBarDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var summary = $"{ViewModel.StatusMessage} — {ViewModel.OutlineSummaryText}";
            _ = Services.ClipboardWriter.TrySetTextAsync(summary);
            ViewModel.StatusMessage = "Copied status & outline summary to clipboard";
        }

        private void OnStatusBarRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var target = sender as FrameworkElement ?? RootGrid;
            ShowStatusBarContextMenu(target, e.GetPosition(target));
        }

        private void ShowStatusBarContextMenu(FrameworkElement target, Windows.Foundation.Point? position)
        {
            var menu = new MenuFlyout();

            var copyStatusItem = new MenuFlyoutItem
            {
                Text = "Copy status message",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyStatusItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(ViewModel.StatusMessage ?? "");
                ViewModel.StatusMessage = "Copied status message to clipboard";
            };
            menu.Items.Add(copyStatusItem);

            var copySummaryItem = new MenuFlyoutItem
            {
                Text = "Copy outline summary",
                Icon = new FontIcon { Glyph = "\uE946" }
            };
            copySummaryItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(ViewModel.OutlineSummaryText ?? "");
                ViewModel.StatusMessage = "Copied outline summary to clipboard";
            };
            menu.Items.Add(copySummaryItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var insertItem = new MenuFlyoutItem
            {
                Text = "Insert into document (Ctrl+Enter)",
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            insertItem.Click += (_, _) =>
            {
                if (ViewModel.InsertIntoDocumentCommand.CanExecute(null))
                    ViewModel.InsertIntoDocumentCommand.Execute(null);
            };
            menu.Items.Add(insertItem);

            if (position.HasValue) menu.ShowAt(target, position.Value);
            else menu.ShowAt(target);
        }

        private void OnLayoutCountLabelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(LayoutSearch?.Text))
            {
                LayoutSearch.Text = string.Empty;
                LayoutSearch.Focus(FocusState.Programmatic);
                ViewModel.StatusMessage = "Cleared layout search filter.";
            }
            else
            {
                var txt = ViewModel.LayoutCountText ?? string.Empty;
                _ = Services.ClipboardWriter.TrySetTextAsync(txt);
                ViewModel.StatusMessage = $"Copied layout count: {txt}";
            }
        }

        private void OnLayoutCountLabelRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (LayoutCountLabel is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var countText = ViewModel.LayoutCountText ?? string.Empty;
            var copyCountItem = new MenuFlyoutItem
            {
                Text = !string.IsNullOrEmpty(countText) ? $"Copy layout count ({countText})" : "Copy layout count",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrEmpty(countText)
            };
            copyCountItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(countText);
                ViewModel.StatusMessage = $"Copied layout count: {countText}";
            };
            menu.Items.Add(copyCountItem);

            var focusSearchItem = new MenuFlyoutItem
            {
                Text = "Focus layout search (Ctrl+F)",
                Icon = new FontIcon { Glyph = "\uE721" }
            };
            focusSearchItem.Click += (_, _) =>
            {
                LayoutSearch?.Focus(FocusState.Programmatic);
            };
            menu.Items.Add(focusSearchItem);

            var resetSearchItem = new MenuFlyoutItem
            {
                Text = "Reset search & filter",
                Icon = new FontIcon { Glyph = "\uE894" },
                IsEnabled = !string.IsNullOrEmpty(LayoutSearch?.Text) || ViewModel.SelectedCategory != "All"
            };
            resetSearchItem.Click += (_, _) =>
            {
                if (LayoutSearch != null) LayoutSearch.Text = string.Empty;
                ViewModel.SelectedCategory = "All";
                ViewModel.StatusMessage = "Reset layout search and category filter.";
            };
            menu.Items.Add(resetSearchItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var pickRandomItem = new MenuFlyoutItem
            {
                Text = "Pick random layout",
                Icon = new FontIcon { Glyph = "\uE8B1" },
                IsEnabled = ViewModel.Layouts.Count > 0
            };
            pickRandomItem.Click += (_, _) =>
            {
                if (ViewModel.Layouts.Count > 0)
                {
                    var rng = new Random();
                    var randomLayout = ViewModel.Layouts[rng.Next(ViewModel.Layouts.Count)];
                    ViewModel.SelectedLayout = randomLayout;
                    LayoutList?.ScrollIntoView(randomLayout);
                    ViewModel.StatusMessage = $"Selected random layout: {randomLayout.DisplayName}";
                }
            };
            menu.Items.Add(pickRandomItem);

            menu.ShowAt(LayoutCountLabel, e.GetPosition(LayoutCountLabel));
        }

        private void OnOutlineSummaryLabelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            CopyOutlineMarkdown();
        }

        private void OnOutlineSummaryLabelRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (OutlineSummaryLabel is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var summaryText = ViewModel.OutlineSummaryText ?? string.Empty;
            var copySummaryItem = new MenuFlyoutItem
            {
                Text = !string.IsNullOrEmpty(summaryText) ? $"Copy summary ({summaryText})" : "Copy outline summary",
                Icon = new FontIcon { Glyph = "\uE946" },
                IsEnabled = !string.IsNullOrEmpty(summaryText)
            };
            copySummaryItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(summaryText);
                ViewModel.StatusMessage = $"Copied outline summary: {summaryText}";
            };
            menu.Items.Add(copySummaryItem);

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets (Ctrl+C)",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var addRootItem = new MenuFlyoutItem
            {
                Text = "Add root item (Insert)",
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            addRootItem.Click += (_, _) => { ViewModel.AddChild(null); FocusRenameBox(); };
            menu.Items.Add(addRootItem);

            var switchToEditorItem = new MenuFlyoutItem
            {
                Text = "Switch to Markdown editor (Ctrl+2)",
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            switchToEditorItem.Click += (_, _) =>
            {
                EditorTabs.SelectedItem = MarkdownTab;
            };
            menu.Items.Add(switchToEditorItem);

            menu.ShowAt(OutlineSummaryLabel, e.GetPosition(OutlineSummaryLabel));
        }

        private void OnPreviewTitleLabelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (!ViewModel.IsOutlineEmpty)
            {
                CopySmartArtBlock();
            }
            else if (!string.IsNullOrEmpty(ViewModel.PreviewTitle))
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(ViewModel.PreviewTitle);
                ViewModel.StatusMessage = $"Copied layout title: {ViewModel.PreviewTitle}";
            }
        }

        private void OnPreviewTitleLabelRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (PreviewTitleLabel is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var title = ViewModel.PreviewTitle ?? string.Empty;
            var copyNameItem = new MenuFlyoutItem
            {
                Text = !string.IsNullOrEmpty(title) ? $"Copy layout title ({title})" : "Copy layout title",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrEmpty(title)
            };
            copyNameItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(title);
                ViewModel.StatusMessage = $"Copied layout title: {title}";
            };
            menu.Items.Add(copyNameItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets (Ctrl+C)",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var refreshItem = new MenuFlyoutItem
            {
                Text = "Refresh preview",
                Icon = new FontIcon { Glyph = "\uE72C" }
            };
            refreshItem.Click += (_, _) =>
            {
                RefreshWebView();
                ViewModel.StatusMessage = "Refreshed diagram preview.";
            };
            menu.Items.Add(refreshItem);

            var switchEditorItem = new MenuFlyoutItem
            {
                Text = "Switch to Markdown editor (Ctrl+2)",
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            switchEditorItem.Click += (_, _) =>
            {
                EditorTabs.SelectedItem = MarkdownTab;
            };
            menu.Items.Add(switchEditorItem);

            menu.Items.Add(new MenuFlyoutSeparator());
            AddPaletteSubmenu(menu);

            menu.ShowAt(PreviewTitleLabel, e.GetPosition(PreviewTitleLabel));
        }

        private void OnEditorTabsRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (EditorTabs is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var outlineItem = new MenuFlyoutItem
            {
                Text = "Outline editor (Ctrl+1)",
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            outlineItem.Click += (_, _) => EditorTabs.SelectedItem = OutlineTab;
            menu.Items.Add(outlineItem);

            var markdownItem = new MenuFlyoutItem
            {
                Text = "Markdown source (Ctrl+2)",
                Icon = new FontIcon { Glyph = "\uE943" }
            };
            markdownItem.Click += (_, _) => EditorTabs.SelectedItem = MarkdownTab;
            menu.Items.Add(markdownItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets (Ctrl+C)",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block (Ctrl+Shift+C)",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var addRootItem = new MenuFlyoutItem
            {
                Text = "Add root item (Insert)",
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            addRootItem.Click += (_, _) => { ViewModel.AddChild(null); FocusRenameBox(); };
            menu.Items.Add(addRootItem);

            menu.ShowAt(EditorTabs, e.GetPosition(EditorTabs));
        }

        private void AddPaletteSubmenu(MenuFlyout menu)
        {
            var paletteSubmenu = new MenuFlyoutSubItem
            {
                Text = $"Color palette ({ViewModel.SelectedPalette})",
                Icon = new FontIcon { Glyph = "\uEA3B" }
            };

            foreach (var palette in ViewModel.Palettes)
            {
                bool isSelected = string.Equals(palette, ViewModel.SelectedPalette, StringComparison.OrdinalIgnoreCase);
                var pItem = new MenuFlyoutItem
                {
                    Text = isSelected ? $"{palette} ✓" : palette,
                    Icon = new FontIcon { Glyph = isSelected ? "\uE73E" : "\uEA3B" }
                };
                pItem.Click += (_, _) =>
                {
                    ViewModel.SelectedPalette = palette;
                    RefreshWebView();
                    ViewModel.StatusMessage = $"Applied SmartArt color palette: {palette}";
                };
                paletteSubmenu.Items.Add(pItem);
            }

            paletteSubmenu.Items.Add(new MenuFlyoutSeparator());

            var resetPaletteItem = new MenuFlyoutItem
            {
                Text = "Reset to Office Blue",
                Icon = new FontIcon { Glyph = "\uE72C" }
            };
            resetPaletteItem.Click += (_, _) =>
            {
                ViewModel.SelectedPalette = "Office Blue";
                RefreshWebView();
                ViewModel.StatusMessage = "Reset color palette to Office Blue.";
            };
            paletteSubmenu.Items.Add(resetPaletteItem);

            var copyPaletteItem = new MenuFlyoutItem
            {
                Text = "Copy palette name",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyPaletteItem.Click += (_, _) =>
            {
                _ = Services.ClipboardWriter.TrySetTextAsync(ViewModel.SelectedPalette);
                ViewModel.StatusMessage = $"Copied palette: {ViewModel.SelectedPalette}";
            };
            paletteSubmenu.Items.Add(copyPaletteItem);

            menu.Items.Add(paletteSubmenu);
        }

        private static T? FindDescendant<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                if (FindDescendant<T>(child) is { } found) return found;
            }
            return null;
        }
    }
}
