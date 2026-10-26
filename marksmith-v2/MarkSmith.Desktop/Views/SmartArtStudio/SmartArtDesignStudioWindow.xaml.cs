using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using MarkSmith.Services;
using MarkSmith.ViewModels.SmartArtStudio;
using Windows.System;

namespace MarkSmith.Views.SmartArtStudio
{
    public sealed partial class SmartArtDesignStudioWindow : Window
    {
        // Reports an outline edit's result on the status line. Edits (reverse, deduplicate, split,
        // number, structure, balance, normalise quadrants) used to copy their summary too, which
        // silently replaced whatever the user had on the clipboard; Copy commands and the
        // Diagnostics reports still copy. A richer line the VM already set is kept.
        private void ReportEdit(string? summary)
        {
            if (ViewModel is null) return;
            var line = (summary ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0].Trim();
            if (string.IsNullOrWhiteSpace(line)) return;
            if (ViewModel.StatusMessage?.Contains(line, StringComparison.Ordinal) == true) return;
            ViewModel.StatusMessage = line.StartsWith("✓", StringComparison.Ordinal) ? line : $"✓ {line}";
        }

        // Copies text and reports it on the status line. These copies used to discard ClipboardWriter's
        // result, so a busy clipboard still said "Copied ...". With no message, the command has already
        // put its result on the status line; a failed copy is added to it. A tick button shows a tick
        // or "Busy" from the real result (it used to tick even when nothing was copied).
        private async void CopyAndReport(string? text, string? message = null, Button? tickButton = null, string? tickLabel = null)
        {
            var error = await MarkSmith.Services.ClipboardWriter.TrySetTextAsync(text ?? string.Empty);
            if (ViewModel is null) return;
            if (error is null)
            {
                // A result line (not a "Copied …" message) now says it went to the clipboard too.
                ViewModel.StatusMessage = MarkSmith.Services.CopyStatusText.Succeeded(message ?? ViewModel.StatusMessage);
            }
            else
            {
                ViewModel.StatusMessage = MarkSmith.Services.CopyStatusText.Failed(message ?? ViewModel.StatusMessage, error);
            }
            if (tickButton is null) return;
            if (tickLabel is null) MarkSmith.Services.CopyFeedback.ApplyButtonTick(tickButton, error);
            else MarkSmith.Services.CopyFeedback.ApplyButtonTick(tickButton, error, tickLabel);
        }

        public SmartArtDesignStudioViewModel ViewModel { get; }
        private bool _isWebViewReady;
        private string? _customHexOverride;

        public sealed class SmartArtPaletteChoice
        {
            public string Name { get; init; } = "";
            public List<SolidColorBrush> Swatches { get; init; } = new();
            public bool IsSelected { get; set; }
        }

        /// <summary>Raised with the full <c>:::smartart</c> markdown block when the user chooses
        /// "Insert into document" — the MainWindow inserts it at the editor caret.</summary>
        public event EventHandler<string>? InsertToDocumentRequested;

        public SmartArtDesignStudioWindow()
        {
            this.InitializeComponent();
            // A long status line is cut off at the window edge; hovering it shows the whole message.
            TrimmedTextToolTip.Attach(StatusBarText);
            // The custom accent's verdict ("Valid hex color · Enter applies it", "Applied #…") was
            // marked live but never raised; it now reads out once typing settles.
            LiveAnnounce.Track(CustomPaletteStatusText);
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            SizeForDisplay();
            ViewModel = new SmartArtDesignStudioViewModel();

            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            TitleBarInsets.Reserve(this, AppTitleBar); // keep the action buttons clear of min/max/close
            CaptionButtons.Follow(this, AppTitleBar);
            // Every right-click menu on a toolbar button also opens from Shift+F10 and the Menu key;
            // none of them did, so the studio's options were mouse-only.
            KeyboardContextMenu.BridgeAll(
                (LayoutCountButton, OnLayoutCountButtonRightTapped),
                (PreviewTitleButton, OnPreviewTitleButtonRightTapped),
                (OutlineSummaryButton, OnOutlineSummaryButtonRightTapped),
                (UndoStudioButton, OnUndoButtonRightTapped),
                (RedoStudioButton, OnRedoButtonRightTapped),
                (InsertIntoDocumentButton, OnInsertIntoDocumentButtonRightTapped),
                (LayoutCategoryCombo, OnLayoutCategoryComboRightTapped),
                (EditorTabs, OnEditorTabsRightTapped),
                (RenameButton, OnRenameButtonRightTapped),
                (DuplicateButton, OnDuplicateButtonRightTapped),
                (DeleteButton, OnDeleteButtonRightTapped),
                (MoveUpButton, OnMoveUpButtonRightTapped),
                (MoveDownButton, OnMoveDownButtonRightTapped),
                (PromoteButton, OnPromoteButtonRightTapped),
                (DemoteButton, OnDemoteButtonRightTapped),
                (PaletteButton, OnPaletteButtonRightTapped),
                (CopyOutlineMarkdownButton, OnCopyOutlineMarkdownButtonRightTapped),
                (CopySmartArtBlockButton, OnCopySmartArtBlockButtonRightTapped),
                (ExportSvgButton, OnExportSvgButtonRightTapped),
                (DiagramBgButton, OnDiagramBgButtonRightTapped),
                (NodeGeometryButton, OnNodeGeometryButtonRightTapped),
                (AspectRatioButton, OnAspectRatioButtonRightTapped),
                (PreviewZoomResetBtn, OnPreviewZoomResetRightTapped),
                (OutlineStatsPill, OnOutlineStatsPillRightTapped),
                (OutlineMetricsPill, OnOutlineMetricsPillRightTapped));
            // Move up at the top, Promote at the root, Delete with the last selection or Undo at the
            // start of history disabled the button holding focus and dropped the keyboard out of
            // the window; it now lands on the next button still available, else the outline.
            FocusRescue.Attach(() => OutlineScroll, UndoStudioButton, RedoStudioButton);
            FocusRescue.Attach(() => OutlineScroll, RenameButton, DuplicateButton, DeleteButton,
                MoveUpButton, MoveDownButton, PromoteButton, DemoteButton);
            // Options menus that keep Cut / Copy / Paste over a selection and open from Shift+F10;
            // their RightTapped handlers replaced the text menu and were mouse only.
            TextBoxMenu.Attach(MarkdownTextBox, FillMarkdownTextBoxMenu);
            TextBoxMenu.AttachInner(LayoutSearch, FillLayoutSearchMenu);
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
                    SyncSvgSelectedNode();
                }
                else if (e.PropertyName == nameof(ViewModel.MarkdownText) || e.PropertyName == nameof(ViewModel.SelectedLayout) || e.PropertyName == nameof(ViewModel.PreviewTitle))
                {
                    UpdateOutlineStats();
                }
            };
            ViewModel.OutlineRows.CollectionChanged += (_, _) => UpdateOutlineStats();
            UpdateOutlineStats();
            this.Activated += OnWindowActivated;
            // Transparent page so the preview card's theme brush shows through in Light and Dark
            // (the rendered SmartArt is a self-contained light card either way).
            PreviewWebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
            PreviewWebView.NavigationCompleted += (_, _) =>
            {
                if (PreviewWebView.CoreWebView2 != null && Math.Abs(_previewZoomFactor - 1.0) > 0.001)
                {
                    try
                    {
                        var zoomStr = _previewZoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        _ = PreviewWebView.CoreWebView2.ExecuteScriptAsync($"document.body.style.zoom = '{zoomStr}';");
                    }
                    catch { }
                }
            };
            HoverPolish.Track(this.RootGrid);
            // The palette flyout lives in the popup layer, outside the tracked tree: its Apply /
            // Reset / harmony buttons had no hover lift.
            HoverPolish.AttachOnOpen(PaletteFlyout);
            this.RootGrid.ActualThemeChanged += (_, _) => RefreshWebView();
            // Open in the layout search (typing filters at once), not on the first title-bar button.
            this.RootGrid.Loaded += (_, _) =>
            {
                LayoutSearch.Focus(FocusState.Programmatic);
                if (ViewModel.SelectedLayout is { } layout) LayoutList.ScrollIntoView(layout, ScrollIntoViewAlignment.Leading);
            };
            PreviewTitleLabel.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdatePreviewHeaderLayout());
        }

        private void OnPreviewHeaderSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewHeaderLayout();

        /// <summary>
        /// Below about 120 px of room the layout name trimmed to "O…", so the tools drop to their own
        /// row and the name gets the full width. Measures a detached probe, never the live label.
        /// </summary>
        private void UpdatePreviewHeaderLayout()
        {
            if (PreviewHeaderGrid is null || PreviewHeaderGrid.ActualWidth <= 0) return;
            var probe = new TextBlock { Text = PreviewTitleLabel.Text ?? string.Empty, FontSize = PreviewTitleLabel.FontSize };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double titleNeeds = probe.DesiredSize.Width + PreviewTitleButton.Padding.Left + PreviewTitleButton.Padding.Right;
            var pad = PreviewHeaderGrid.Padding;
            double room = PreviewHeaderGrid.ActualWidth - pad.Left - pad.Right - PreviewHeaderLabel.ActualWidth
                          - PreviewHeaderTools.ActualWidth - 2 * PreviewHeaderGrid.ColumnSpacing;
            bool stacked = room < Math.Min(titleNeeds, PreviewTitleMinimumRoom);
            Grid.SetRow(PreviewHeaderTools, stacked ? 1 : 0);
            Grid.SetColumn(PreviewHeaderTools, stacked ? 0 : 2);
            Grid.SetColumnSpan(PreviewHeaderTools, stacked ? 3 : 1);
            PreviewHeaderTools.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            PreviewHeaderGrid.RowSpacing = stacked ? 4 : 0;
        }

        private const double PreviewTitleMinimumRoom = 120;

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
            LiveAnnounce.Raise(element);
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

        private void FillLayoutSearchMenu(MenuFlyout menu)
        {
            var copyQueryItem = new MenuFlyoutItem
            {
                Text = "Copy search query",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !string.IsNullOrEmpty(LayoutSearch.Text)
            };
            copyQueryItem.Click += (_, _) =>
            {
                CopyAndReport(LayoutSearch.Text, $"Copied search query: {LayoutSearch.Text}");
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
                Text = "Clear search",
                KeyboardAcceleratorTextOverride = "Esc",
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
                Text = "Browse layouts",
                KeyboardAcceleratorTextOverride = "Down / Enter",
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
                Text = "Focus outline editor",
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            focusOutlineItem.Click += (_, _) => OutlineScroll.Focus(FocusState.Keyboard);
            menu.Items.Add(focusOutlineItem);
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
            // Shift+F10 with the list open: close it first so the menu is not drawn over it.
            if (LayoutCategoryCombo.IsDropDownOpen) LayoutCategoryCombo.IsDropDownOpen = false;
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
                Text = "Next category",
                Icon = new FontIcon { Glyph = "\uE76C" }
            };
            nextItem.Click += (_, _) => CycleNextLayoutCategory();
            menu.Items.Add(nextItem);

            var prevItem = new MenuFlyoutItem
            {
                Text = "Previous category",
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
                CopyAndReport(ViewModel.SelectedCategory, $"Copied category name '{ViewModel.SelectedCategory}'.");
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

            menu.ShowAt(LayoutCategoryCombo, KeyboardContextMenu.PointIn(e, LayoutCategoryCombo));
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

        // An outline command (sort, reverse, normalise, a template) rewrites the box's text, which
        // empties the TextBox's own undo, so Ctrl+Z here did nothing although the studio's Undo
        // could revert the command. Hand Ctrl+Z / Ctrl+Y to the studio only when the box has no
        // typing of its own to undo or redo; otherwise the TextBox keeps them.
        private void OnMarkdownTextBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (!ctrl) return;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var undo = e.Key == VirtualKey.Z && !shift;
            var redo = e.Key == VirtualKey.Y || (e.Key == VirtualKey.Z && shift);
            if (undo && !MarkdownTextBox.CanUndo && ViewModel.CanUndo)
            {
                ViewModel.UndoCommand.Execute(null);
                e.Handled = true;
            }
            else if (redo && !MarkdownTextBox.CanRedo && ViewModel.CanRedo)
            {
                ViewModel.RedoCommand.Execute(null);
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
                PreviewWebView.CoreWebView2.WebMessageReceived += OnPreviewWebMessageReceived;
                PreviewWebView.CoreWebView2.NavigationCompleted += (_, _) => SyncSvgSelectedNode();
                RefreshWebView();
            }
            catch { /* preview unavailable — insert path still works */ }
        }

        private enum DiagramPreviewBgMode
        {
            DefaultCard,
            DarkSlate,
            Checkerboard,
            DotGrid
        }

        public enum SmartArtNodeGeometryMode
        {
            Default,
            PillRounded,
            SharpBox,
            ModernSoft,
            ContrastBorder
        }

        public enum SmartArtAspectRatioMode
        {
            Default,
            Widescreen16x9,
            Standard4x3,
            Square1x1,
            Ultrawide21x9
        }

        public enum SmartArtZoomMode
        {
            Fit,
            Z50,
            Z75,
            Z100,
            Z125,
            Z150,
            Z200
        }

        private DiagramPreviewBgMode _currentDiagramBgMode = DiagramPreviewBgMode.DefaultCard;
        private SmartArtNodeGeometryMode _currentNodeGeomMode = SmartArtNodeGeometryMode.Default;
        private SmartArtAspectRatioMode _currentAspectRatioMode = SmartArtAspectRatioMode.Default;
        private SmartArtZoomMode _currentZoomMode = SmartArtZoomMode.Fit;

        private void RefreshWebView()
        {
            if (_isWebViewReady && PreviewWebView.CoreWebView2 != null)
            {
                bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
                string body = ViewModel.PreviewHtml;
                if (!string.IsNullOrEmpty(_customHexOverride))
                {
                    body = ApplyCustomHexToSvgHtml(body, _customHexOverride);
                }
                string html = BuildWrapperHtml(body, isDark, _currentDiagramBgMode, _currentNodeGeomMode, _currentAspectRatioMode, _currentZoomMode);
                PreviewWebView.CoreWebView2.NavigateToString(html);
            }
        }

        private string ApplyCustomHexToSvgHtml(string html, string hex)
        {
            if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(hex)) return html;
            var (accents, _) = MarkSmith.Core.Preview.HtmlPreviewRenderer.GetPaletteColors(ViewModel.SelectedPalette);
            string targetHex = "#" + hex.Trim().TrimStart('#').ToLowerInvariant();
            if (accents.Length > 0 && !string.Equals(accents[0], targetHex, StringComparison.OrdinalIgnoreCase))
            {
                html = System.Text.RegularExpressions.Regex.Replace(html, System.Text.RegularExpressions.Regex.Escape(accents[0]), targetHex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            return html;
        }

        private void SyncSvgSelectedNode()
        {
            if (PreviewWebView?.CoreWebView2 == null) return;
            string text = ViewModel.SelectedNode?.Text ?? "";
            string esc = text.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");
            _ = PreviewWebView.CoreWebView2.ExecuteScriptAsync($"if(window.__msHighlightSmartArtNode) window.__msHighlightSmartArtNode('{esc}');");
        }

        // The renderer emits a card sized for documents (800 wide, as tall as the layout needs) with
        // a "Layout: …" caption. In the studio the card fills the pane and the drawing scales to fit
        // it either way (the SVG's default xMidYMid meet), and the caption is dropped because the
        // pane header names the layout already.
        private static string BuildWrapperHtml(string body, bool isDarkTheme = false, DiagramPreviewBgMode bgMode = DiagramPreviewBgMode.DefaultCard, SmartArtNodeGeometryMode geomMode = SmartArtNodeGeometryMode.Default, SmartArtAspectRatioMode aspectMode = SmartArtAspectRatioMode.Default, SmartArtZoomMode zoomMode = SmartArtZoomMode.Fit)
        {
            string cardBg;
            string cardBorder;
            string cardShadow;
            string customPatternCss = "";

            switch (bgMode)
            {
                case DiagramPreviewBgMode.DarkSlate:
                    cardBg = "#0F172A";
                    cardBorder = "rgba(51,65,85,0.8)";
                    cardShadow = "0 6px 20px rgba(0,0,0,0.5)";
                    customPatternCss = " .smartart-container text { fill: #F8FAFC !important; }";
                    break;
                case DiagramPreviewBgMode.Checkerboard:
                    cardBg = "#FFFFFF";
                    cardBorder = "rgba(15,23,42,0.15)";
                    cardShadow = "0 2px 8px rgba(15,23,42,0.08)";
                    customPatternCss = " background-image: linear-gradient(45deg, #e2e8f0 25%, transparent 25%), linear-gradient(-45deg, #e2e8f0 25%, transparent 25%), linear-gradient(45deg, transparent 75%, #e2e8f0 75%), linear-gradient(-45deg, transparent 75%, #e2e8f0 75%) !important; background-size: 16px 16px !important; background-position: 0 0, 0 8px, 8px -8px, -8px 0px !important;";
                    break;
                case DiagramPreviewBgMode.DotGrid:
                    cardBg = "#F8FAFC";
                    cardBorder = "rgba(15,23,42,0.15)";
                    cardShadow = "0 2px 8px rgba(15,23,42,0.08)";
                    customPatternCss = " background-image: radial-gradient(#cbd5e1 1.2px, transparent 1.2px) !important; background-size: 16px 16px !important;";
                    break;
                default:
                    cardBg = isDarkTheme ? "#F8FAFC" : "#FFFFFF";
                    cardBorder = isDarkTheme ? "rgba(148,163,184,0.38)" : "rgba(15,23,42,0.10)";
                    cardShadow = isDarkTheme ? "0 4px 16px rgba(0,0,0,0.28)" : "0 2px 8px rgba(15,23,42,0.05)";
                    break;
            }

            string geomCss = geomMode switch
            {
                SmartArtNodeGeometryMode.PillRounded => " .smartart-container svg rect { rx: 20px !important; ry: 20px !important; }",
                SmartArtNodeGeometryMode.SharpBox => " .smartart-container svg rect { rx: 0px !important; ry: 0px !important; }",
                SmartArtNodeGeometryMode.ModernSoft => " .smartart-container svg rect, .smartart-container svg path { filter: drop-shadow(0 3px 6px rgba(0,0,0,0.14)) !important; }",
                SmartArtNodeGeometryMode.ContrastBorder => " .smartart-container svg rect, .smartart-container svg path { stroke-width: 2.5px !important; stroke: #0F172A !important; }",
                _ => ""
            };

            string aspectCss = aspectMode switch
            {
                SmartArtAspectRatioMode.Widescreen16x9 => " max-width: 960px !important; max-height: 100% !important; height: auto !important; aspect-ratio: 16 / 9 !important; margin: auto !important; ",
                SmartArtAspectRatioMode.Standard4x3 => " max-width: 800px !important; max-height: 100% !important; height: auto !important; aspect-ratio: 4 / 3 !important; margin: auto !important; ",
                SmartArtAspectRatioMode.Square1x1 => " max-width: 600px !important; max-height: 100% !important; height: auto !important; aspect-ratio: 1 / 1 !important; margin: auto !important; ",
                SmartArtAspectRatioMode.Ultrawide21x9 => " max-width: 1100px !important; max-height: 100% !important; height: auto !important; aspect-ratio: 21 / 9 !important; margin: auto !important; ",
                _ => ""
            };

            string zoomCss = zoomMode switch
            {
                SmartArtZoomMode.Z50 => " transform: scale(0.5); transform-origin: center center; ",
                SmartArtZoomMode.Z75 => " transform: scale(0.75); transform-origin: center center; ",
                SmartArtZoomMode.Z100 => " transform: scale(1.0); transform-origin: center center; ",
                SmartArtZoomMode.Z125 => " transform: scale(1.25); transform-origin: center center; ",
                SmartArtZoomMode.Z150 => " transform: scale(1.5); transform-origin: center center; ",
                SmartArtZoomMode.Z200 => " transform: scale(2.0); transform-origin: center center; ",
                _ => ""
            };

            return $@"<!DOCTYPE html>
<html><head><meta charset=""utf-8""/>
<style>
  html,body{{height:100%;margin:0}}
  body{{padding:16px;box-sizing:border-box;background:transparent;display:flex;align-items:center;justify-content:center;overflow:auto}}
  .smartart-container{{width:100%!important;max-width:none!important;height:100%!important;display:flex;flex-direction:column;padding:12px;box-sizing:border-box;background:{cardBg}!important;border:1px solid {cardBorder}!important;border-radius:8px!important;box-shadow:{cardShadow}!important;{customPatternCss}{geomCss}{aspectCss}{zoomCss}}}
  .smartart-container > .smartart-caption{{display:none}}
  .smartart-container > svg{{flex:1;min-height:0;width:100%!important;height:100%!important}}
  .smartart-container svg g {{ transition: filter 0.12s ease, opacity 0.15s ease; }}
  .smartart-container svg g:hover {{ filter: brightness(1.08); }}
  .smartart-selected {{
    outline: 2.5px solid #0078D4 !important;
    outline-offset: 3px !important;
    filter: drop-shadow(0 0 8px rgba(0,120,212,0.65)) !important;
  }}
  .smartart-drop-target {{
    outline: 2.5px dashed #107C41 !important;
    outline-offset: 4px !important;
    filter: drop-shadow(0 0 10px rgba(16, 124, 65, 0.75)) !important;
  }}
  .smartart-dragging {{
    opacity: 0.55 !important;
    filter: grayscale(40%) !important;
  }}
  #smartart-drag-ghost {{
    position: fixed;
    pointer-events: none;
    z-index: 9999;
    padding: 6px 12px;
    background: rgba(15, 23, 42, 0.90);
    color: #ffffff;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    font-size: 12px;
    font-weight: 600;
    border-radius: 6px;
    box-shadow: 0 4px 12px rgba(0,0,0,0.3);
    display: none;
    transform: translate(12px, 12px);
  }}
</style></head>
<body>
  {body}
<script>
  // Frame the drawing itself rather than its 800-wide document canvas, so a three-box process
  // fills the pane like an org chart does — but never zoom a lone shape up to poster size.
  (function () {{
    var s = document.querySelector('.smartart-container > svg');
    if (!s) return;
    if (s.getBBox) {{
      var b = s.getBBox(), pad = 20;
      if (b.width && b.height) {{
        var w = Math.max(b.width + pad * 2, 560), h = Math.max(b.height + pad * 2, 340);
        var cx = b.x + b.width / 2, cy = b.y + b.height / 2;
        s.setAttribute('viewBox', (cx - w / 2) + ' ' + (cy - h / 2) + ' ' + w + ' ' + h);
      }}
    }}
    s.style.cursor = 'pointer';

    // Drag ghost element
    var ghost = document.getElementById('smartart-drag-ghost');
    if (!ghost) {{
      ghost = document.createElement('div');
      ghost.id = 'smartart-drag-ghost';
      document.body.appendChild(ghost);
    }}

    var dragSourceEl = null;
    var dragSourceText = '';
    var isDragging = false;
    var startX = 0, startY = 0;
    var currentDropTarget = null;
    var suppressClickUntil = 0;

    s.addEventListener('mousedown', function(e) {{
      if (e.button !== 0) return;
      var el = e.target.closest('g, text, rect, circle, polygon, path');
      if (!el) return;
      var parentG = el.closest('g') || el;
      var text = (parentG.textContent || '').trim();
      if (!text) return;

      dragSourceEl = parentG;
      dragSourceText = text;
      startX = e.clientX;
      startY = e.clientY;
      isDragging = false;
    }});

    window.addEventListener('mousemove', function(e) {{
      if (!dragSourceEl) return;
      var dx = e.clientX - startX;
      var dy = e.clientY - startY;
      if (!isDragging && (Math.abs(dx) > 6 || Math.abs(dy) > 6)) {{
        isDragging = true;
        dragSourceEl.classList.add('smartart-dragging');
        ghost.style.display = 'block';
        ghost.textContent = 'Move ' + dragSourceText + ' \u2192 (drop on item to nest)';
      }}
      if (isDragging) {{
        ghost.style.left = e.clientX + 'px';
        ghost.style.top = e.clientY + 'px';

        ghost.style.display = 'none';
        var hit = document.elementFromPoint(e.clientX, e.clientY);
        ghost.style.display = 'block';

        var targetG = null;
        if (hit && s.contains(hit)) {{
          var hitEl = hit.closest('g, text, rect, circle, polygon, path');
          if (hitEl) {{
            var candidateG = hitEl.closest('g') || hitEl;
            var cText = (candidateG.textContent || '').trim();
            if (candidateG !== dragSourceEl && cText && cText !== dragSourceText) {{
              targetG = candidateG;
            }}
          }}
        }}

        if (targetG !== currentDropTarget) {{
          if (currentDropTarget) {{
            currentDropTarget.classList.remove('smartart-drop-target');
          }}
          currentDropTarget = targetG;
          if (currentDropTarget) {{
            currentDropTarget.classList.add('smartart-drop-target');
            var tgtText = (currentDropTarget.textContent || '').trim();
            ghost.textContent = 'Nest ' + dragSourceText + ' under ' + tgtText;
          }} else {{
            ghost.textContent = 'Move ' + dragSourceText + ' \u2192 (drop on item to nest)';
          }}
        }}
      }}
    }});

    window.addEventListener('mouseup', function(e) {{
      if (!dragSourceEl) return;
      if (isDragging) {{
        suppressClickUntil = Date.now() + 250;
        if (currentDropTarget) {{
          var tgtText = (currentDropTarget.textContent || '').trim();
          if (tgtText && tgtText !== dragSourceText) {{
            try {{
              window.chrome.webview.postMessage(JSON.stringify({{
                type: 'node-drag-reparent',
                source: dragSourceText,
                target: tgtText
              }}));
            }} catch(err) {{}}
          }}
        }}
      }}
      if (dragSourceEl) {{
        dragSourceEl.classList.remove('smartart-dragging');
      }}
      if (currentDropTarget) {{
        currentDropTarget.classList.remove('smartart-drop-target');
      }}
      ghost.style.display = 'none';
      dragSourceEl = null;
      dragSourceText = '';
      currentDropTarget = null;
      isDragging = false;
    }}, true);

    s.addEventListener('click', function(e) {{
      if (Date.now() < suppressClickUntil) {{
        e.stopPropagation();
        return;
      }}
      var el = e.target.closest('g, text, rect, circle, polygon, path');
      if (!el) return;
      var text = (el.textContent || '').trim();
      if (text) {{
        try {{ window.chrome.webview.postMessage(JSON.stringify({{ type: 'node-select', text: text }})); }} catch(err) {{}}
      }}
    }});
    s.addEventListener('dblclick', function(e) {{
      var el = e.target.closest('g, text, rect, circle, polygon, path');
      if (el) {{
        var text = (el.textContent || '').trim();
        if (text) {{
          try {{ window.chrome.webview.postMessage(JSON.stringify({{ type: 'node-edit', text: text }})); }} catch(err) {{}}
          return;
        }}
      }}
      if (s.getBBox) {{
        var b = s.getBBox(), pad = 20;
        if (b.width && b.height) {{
          var w = Math.max(b.width + pad * 2, 560), h = Math.max(b.height + pad * 2, 340);
          var cx = b.x + b.width / 2, cy = b.y + b.height / 2;
          s.setAttribute('viewBox', (cx - w / 2) + ' ' + (cy - h / 2) + ' ' + w + ' ' + h);
        }}
      }}
    }});

    window.__msHighlightSmartArtNode = function(text) {{
      var prevs = document.querySelectorAll('.smartart-selected');
      for (var p = 0; p < prevs.length; p++) {{
        prevs[p].classList.remove('smartart-selected');
      }}
      if (!text) return;
      var targets = s.querySelectorAll('g, text, rect, circle, polygon, path');
      for (var i = 0; i < targets.length; i++) {{
        var t = (targets[i].textContent || '').trim();
        if (t && (t === text || t.indexOf(text) !== -1 || text.indexOf(t) !== -1)) {{
          var parentG = targets[i].closest('g') || targets[i];
          parentG.classList.add('smartart-selected');
          break;
        }}
      }}
    }};
  }})();
</script>
</body></html>";
        }

        private void OnPreviewWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();

                if (type == "node-drag-reparent")
                {
                    if (root.TryGetProperty("source", out var srcProp) && root.TryGetProperty("target", out var tgtProp))
                    {
                        var srcText = srcProp.GetString()?.Trim();
                        var tgtText = tgtProp.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(srcText) && !string.IsNullOrEmpty(tgtText))
                        {
                            var sourceNode = ViewModel.OutlineRows.FirstOrDefault(r => string.Equals(r.Text.Trim(), srcText, StringComparison.OrdinalIgnoreCase))
                                          ?? ViewModel.OutlineRows.FirstOrDefault(r => r.Text.Contains(srcText, StringComparison.OrdinalIgnoreCase));
                            var targetNode = ViewModel.OutlineRows.FirstOrDefault(r => string.Equals(r.Text.Trim(), tgtText, StringComparison.OrdinalIgnoreCase))
                                          ?? ViewModel.OutlineRows.FirstOrDefault(r => r.Text.Contains(tgtText, StringComparison.OrdinalIgnoreCase));
                            if (sourceNode != null && targetNode != null && sourceNode != targetNode)
                            {
                                ReparentOutlineNode(sourceNode, targetNode);
                            }
                        }
                    }
                    return;
                }

                if (root.TryGetProperty("text", out var textProp))
                {
                    var text = textProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        var match = ViewModel.OutlineRows.FirstOrDefault(r => string.Equals(r.Text.Trim(), text, StringComparison.OrdinalIgnoreCase))
                                 ?? ViewModel.OutlineRows.FirstOrDefault(r => r.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                        {
                            ViewModel.Select(match);
                            SyncSvgSelectedNode();
                            if (type == "node-edit")
                            {
                                BeginRenameAndFocus(match);
                            }
                            ViewModel.StatusMessage = type == "node-edit"
                                ? $"Editing '{match.Text}' in outline (Enter to commit)"
                                : $"Selected '{match.Text}' in outline";
                        }
                    }
                }
            }
            catch { }
        }

        private void ReparentOutlineNode(StudioNodeViewModel source, StudioNodeViewModel target)
        {
            if (source == target) return;
            // Prevent cyclic ancestry
            for (var p = target.Parent; p != null; p = p.Parent)
            {
                if (p == source)
                {
                    ViewModel.StatusMessage = $"Cannot nest '{source.Text}' under its own child '{target.Text}'.";
                    return;
                }
            }

            ViewModel.PushUndo();

            if (source.Parent == null) ViewModel.RootNodes.Remove(source);
            else source.Parent.Children.Remove(source);

            source.Parent = target;
            target.Children.Add(source);

            ViewModel.Select(source);
            ViewModel.SyncTreeToMarkdown();
            ViewModel.StatusMessage = $"Reparented '{source.Text}' under '{target.Text}' · Ctrl+Z to undo";
        }

        private StudioNodeViewModel? _draggedOutlineNode;

        private void OnRowDragStarting(UIElement sender, DragStartingEventArgs args)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StudioNodeViewModel node)
            {
                _draggedOutlineNode = node;
                args.Data.SetText(node.Text);
                args.DragUI.SetContentFromDataPackage();
            }
        }

        private void OnRowDragOver(object sender, DragEventArgs e)
        {
            if (_draggedOutlineNode == null) return;
            if (sender is FrameworkElement fe && fe.DataContext is StudioNodeViewModel targetNode)
            {
                if (targetNode == _draggedOutlineNode)
                {
                    e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                    return;
                }
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.DragUIOverride.Caption = $"Move {_draggedOutlineNode.Text} near {targetNode.Text}";
                e.DragUIOverride.IsCaptionVisible = true;

                var pos = e.GetPosition(fe);
                bool topHalf = pos.Y < (fe.ActualHeight / 2.0);
                if (fe is Border border)
                {
                    border.BorderBrush = ThemeBrush.For(border, "AccentFillColorDefaultBrush");
                    border.BorderThickness = topHalf ? new Thickness(0, 2.5, 0, 0) : new Thickness(0, 0, 0, 2.5);
                }
            }
        }

        private void OnRowDragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border border)
            {
                border.BorderThickness = new Thickness(0);
                border.BorderBrush = null;
            }
        }

        private void OnRowDrop(object sender, DragEventArgs e)
        {
            if (sender is Border border)
            {
                border.BorderThickness = new Thickness(0);
                border.BorderBrush = null;
            }

            if (_draggedOutlineNode == null) return;
            if (sender is FrameworkElement fe && fe.DataContext is StudioNodeViewModel targetNode)
            {
                if (targetNode == _draggedOutlineNode) return;

                var pos = e.GetPosition(fe);
                bool topHalf = pos.Y < (fe.ActualHeight / 2.0);

                ReorderOutlineNodes(_draggedOutlineNode, targetNode, insertBefore: topHalf);
            }
            _draggedOutlineNode = null;
        }

        private void ReorderOutlineNodes(StudioNodeViewModel source, StudioNodeViewModel target, bool insertBefore)
        {
            ViewModel.PushUndo();

            if (source.Parent == target.Parent)
            {
                var siblings = source.Parent == null ? ViewModel.RootNodes : source.Parent.Children;
                int oldIdx = siblings.IndexOf(source);
                int targetIdx = siblings.IndexOf(target);
                if (oldIdx >= 0 && targetIdx >= 0)
                {
                    int newIdx = insertBefore ? (oldIdx < targetIdx ? targetIdx - 1 : targetIdx) : (oldIdx < targetIdx ? targetIdx : targetIdx + 1);
                    siblings.Move(oldIdx, Math.Clamp(newIdx, 0, siblings.Count - 1));
                }
            }
            else
            {
                if (source.Parent == null) ViewModel.RootNodes.Remove(source);
                else source.Parent.Children.Remove(source);

                source.Parent = target.Parent;
                var targetSiblings = target.Parent == null ? ViewModel.RootNodes : target.Parent.Children;
                int targetIdx = targetSiblings.IndexOf(target);
                int insertIdx = insertBefore ? Math.Max(0, targetIdx) : Math.Min(targetSiblings.Count, targetIdx + 1);
                targetSiblings.Insert(insertIdx, source);
            }

            ViewModel.Select(source);
            ViewModel.SyncTreeToMarkdown();
            ViewModel.StatusMessage = $"Moved '{source.Text}' {(insertBefore ? "before" : "after")} '{target.Text}'";
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
            if (Down(VirtualKey.Menu))
            {
                if (e.Key == VirtualKey.Home && ViewModel.CanMoveUp)
                {
                    while (ViewModel.CanMoveUp) ViewModel.MoveUpCommand.Execute(null);
                    ViewModel.StatusMessage = "Moved item to top of outline · Alt+Up to adjust";
                    e.Handled = true;
                    return;
                }
                if (e.Key == VirtualKey.End && ViewModel.CanMoveDown)
                {
                    while (ViewModel.CanMoveDown) ViewModel.MoveDownCommand.Execute(null);
                    ViewModel.StatusMessage = "Moved item to bottom of outline · Alt+Down to adjust";
                    e.Handled = true;
                    return;
                }
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
            if (sender is Border b)
            {
                ThemeBrush.Set(b, Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                if (b.Tag as string != "DragWired")
                {
                    b.Tag = "DragWired";
                    b.CanDrag = true;
                    b.AllowDrop = true;
                    b.DragStarting += OnRowDragStarting;
                    b.DragOver += OnRowDragOver;
                    b.DragLeave += OnRowDragLeave;
                    b.Drop += OnRowDrop;
                }
            }
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

        private static bool IsDown(VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        private void OnRowPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not StudioNodeViewModel node) return;
            if (node.IsEditing) return; // Don't interrupt active typing

            bool alt = IsDown(VirtualKey.Menu);
            bool shift = IsDown(VirtualKey.Shift);
            if (!alt && !shift) return; // Allow ordinary vertical scrolling when neither modifier is held

            var pt = e.GetCurrentPoint(fe);
            int delta = pt.Properties.MouseWheelDelta;
            if (Math.Abs(delta) < 60) return;

            e.Handled = true;
            ViewModel.Select(node);

            if (alt)
            {
                // Alt+Wheel moves item Up or Down in outline
                if (delta > 0 && ViewModel.CanMoveUp)
                {
                    ViewModel.MoveUp();
                    ViewModel.StatusMessage = $"Moved \"{node.Text}\" up · Alt+Wheel to reorder";
                }
                else if (delta < 0 && ViewModel.CanMoveDown)
                {
                    ViewModel.MoveDown();
                    ViewModel.StatusMessage = $"Moved \"{node.Text}\" down · Alt+Wheel to reorder";
                }
            }
            else if (shift)
            {
                // Shift+Wheel indents (demote) or outdents (promote)
                if (delta > 0 && ViewModel.CanPromote)
                {
                    ViewModel.Promote();
                    ViewModel.StatusMessage = $"Promoted \"{node.Text}\" (level {node.Depth}) · Shift+Wheel to indent";
                }
                else if (delta < 0 && ViewModel.CanDemote)
                {
                    ViewModel.Demote();
                    ViewModel.StatusMessage = $"Demoted \"{node.Text}\" (level {node.Depth}) · Shift+Wheel to indent";
                }
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
            if ((sender as FrameworkElement)?.DataContext is not StudioNodeViewModel node) return;
            // The row and this button go with the item, so a keyboard press would leave focus on
            // nothing (WinUI then throws it to the window's first tab stop). The outline keeps
            // the new selection's Up / Down / Delete keys working.
            var state = (sender as Control)?.FocusState ?? FocusState.Unfocused;
            ViewModel.DeleteSelectedCommand.Execute(node);
            if (state != FocusState.Unfocused)
                OutlineScroll.Focus(state == FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard);
        }

        private void OnMoveUpButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (!ViewModel.CanMoveUp) return;
            int count = 0;
            while (ViewModel.CanMoveUp)
            {
                ViewModel.MoveUpCommand.Execute(null);
                count++;
            }
            ViewModel.StatusMessage = $"Moved item to top of outline ({CountText.Of(count, "step")}) · Alt+Up to adjust";
        }

        private void OnMoveUpButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();
            var moveToTopItem = new MenuFlyoutItem
            {
                Text = "Move to top",
                KeyboardAcceleratorTextOverride = "Alt+Home",
                Icon = new FontIcon { Glyph = "\uE898" },
                IsEnabled = ViewModel.CanMoveUp
            };
            moveToTopItem.Click += (_, _) =>
            {
                int c = 0;
                while (ViewModel.CanMoveUp) { ViewModel.MoveUpCommand.Execute(null); c++; }
                ViewModel.StatusMessage = $"Moved item to top of outline ({CountText.Of(c, "step")})";
            };
            menu.Items.Add(moveToTopItem);

            var moveUpItem = new MenuFlyoutItem
            {
                Text = "Move up one",
                KeyboardAcceleratorTextOverride = "Alt+Up",
                Icon = new FontIcon { Glyph = "\uE74A" },
                IsEnabled = ViewModel.CanMoveUp
            };
            moveUpItem.Click += (_, _) => ViewModel.MoveUpCommand.Execute(null);
            menu.Items.Add(moveUpItem);

            if (sender is FrameworkElement fe) menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else if (MoveUpButton is not null) menu.ShowAt(MoveUpButton, KeyboardContextMenu.PointIn(e, MoveUpButton));
        }

        private void OnMoveDownButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (!ViewModel.CanMoveDown) return;
            int count = 0;
            while (ViewModel.CanMoveDown)
            {
                ViewModel.MoveDownCommand.Execute(null);
                count++;
            }
            ViewModel.StatusMessage = $"Moved item to bottom of outline ({CountText.Of(count, "step")}) · Alt+Down to adjust";
        }

        private void OnMoveDownButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();
            var moveToBottomItem = new MenuFlyoutItem
            {
                Text = "Move to bottom",
                KeyboardAcceleratorTextOverride = "Alt+End",
                Icon = new FontIcon { Glyph = "\uE896" },
                IsEnabled = ViewModel.CanMoveDown
            };
            moveToBottomItem.Click += (_, _) =>
            {
                int c = 0;
                while (ViewModel.CanMoveDown) { ViewModel.MoveDownCommand.Execute(null); c++; }
                ViewModel.StatusMessage = $"Moved item to bottom of outline ({CountText.Of(c, "step")})";
            };
            menu.Items.Add(moveToBottomItem);

            var moveDownItem = new MenuFlyoutItem
            {
                Text = "Move down one",
                KeyboardAcceleratorTextOverride = "Alt+Down",
                Icon = new FontIcon { Glyph = "\uE74B" },
                IsEnabled = ViewModel.CanMoveDown
            };
            moveDownItem.Click += (_, _) => ViewModel.MoveDownCommand.Execute(null);
            menu.Items.Add(moveDownItem);

            if (sender is FrameworkElement fe) menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else if (MoveDownButton is not null) menu.ShowAt(MoveDownButton, KeyboardContextMenu.PointIn(e, MoveDownButton));
        }

        private void OnPromoteButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (!ViewModel.CanPromote) return;
            int count = 0;
            while (ViewModel.CanPromote)
            {
                ViewModel.PromoteCommand.Execute(null);
                count++;
            }
            ViewModel.StatusMessage = "Promoted item to root level (depth 0) · Shift+Tab to adjust";
        }

        private void OnPromoteButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();
            var promoteRootItem = new MenuFlyoutItem
            {
                Text = "Promote to root level (depth 0)",
                Icon = new FontIcon { Glyph = "\uE897" },
                IsEnabled = ViewModel.CanPromote
            };
            promoteRootItem.Click += (_, _) =>
            {
                while (ViewModel.CanPromote) ViewModel.PromoteCommand.Execute(null);
                ViewModel.StatusMessage = "Promoted item to root level (depth 0)";
            };
            menu.Items.Add(promoteRootItem);

            var promoteOneItem = new MenuFlyoutItem
            {
                Text = "Promote one level out",
                KeyboardAcceleratorTextOverride = "Shift+Tab",
                Icon = new FontIcon { Glyph = "\uE76B" },
                IsEnabled = ViewModel.CanPromote
            };
            promoteOneItem.Click += (_, _) => ViewModel.PromoteCommand.Execute(null);
            menu.Items.Add(promoteOneItem);

            if (sender is FrameworkElement fe) menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else if (PromoteButton is not null) menu.ShowAt(PromoteButton, KeyboardContextMenu.PointIn(e, PromoteButton));
        }

        private void OnDemoteButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (!ViewModel.CanDemote) return;
            int count = 0;
            while (ViewModel.CanDemote)
            {
                ViewModel.DemoteCommand.Execute(null);
                count++;
            }
            ViewModel.StatusMessage = $"Demoted item to deepest child level ({CountText.Of(count, "level")} in) · Tab to adjust";
        }

        private void OnDemoteButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();
            var demoteDeepestItem = new MenuFlyoutItem
            {
                Text = "Demote to deepest child level",
                Icon = new FontIcon { Glyph = "\uE895" },
                IsEnabled = ViewModel.CanDemote
            };
            demoteDeepestItem.Click += (_, _) =>
            {
                while (ViewModel.CanDemote) ViewModel.DemoteCommand.Execute(null);
                ViewModel.StatusMessage = "Demoted item to deepest child level";
            };
            menu.Items.Add(demoteDeepestItem);

            var demoteOneItem = new MenuFlyoutItem
            {
                Text = "Demote one level in",
                KeyboardAcceleratorTextOverride = "Tab",
                Icon = new FontIcon { Glyph = "\uE76C" },
                IsEnabled = ViewModel.CanDemote
            };
            demoteOneItem.Click += (_, _) => ViewModel.DemoteCommand.Execute(null);
            menu.Items.Add(demoteOneItem);

            if (sender is FrameworkElement fe) menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else if (DemoteButton is not null) menu.ShowAt(DemoteButton, KeyboardContextMenu.PointIn(e, DemoteButton));
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

        private void OnRenameButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();
            var renameItem = new MenuFlyoutItem { Text = "Rename inline", KeyboardAcceleratorTextOverride = "F2", Icon = new FontIcon { Glyph = "\uE70F" } };
            renameItem.Click += OnRenameClick;
            menu.Items.Add(renameItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            var copyItem = new MenuFlyoutItem { Text = "Copy item text", Icon = new FontIcon { Glyph = "\uE8C8" } };
            copyItem.Click += (_, _) =>
            {
                if (ViewModel.SelectedNode is { } node)
                {
                    CopyAndReport(node.Text ?? "", $"Copied: {node.Text}");
                }
            };
            menu.Items.Add(copyItem);
            if (sender is FrameworkElement fe) menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
        }

        private void OnDuplicateButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();
            var dupItem = new MenuFlyoutItem { Text = "Duplicate", KeyboardAcceleratorTextOverride = "Ctrl+D", Icon = new FontIcon { Glyph = "\uE8C8" } };
            dupItem.Click += (_, _) => ViewModel.DuplicateSelectedCommand.Execute(null);
            menu.Items.Add(dupItem);
            var dupSiblingItem = new MenuFlyoutItem { Text = "Duplicate as sibling", Icon = new FontIcon { Glyph = "\uE8C8" } };
            dupSiblingItem.Click += (_, _) =>
            {
                if (ViewModel.SelectedNode is { } node)
                {
                    string txt = node.Text ?? "";
                    ViewModel.AddSiblingCommand.Execute(node);
                    if (ViewModel.SelectedNode is { } newNode)
                    {
                        newNode.Text = txt;
                        ViewModel.CommitRename();
                    }
                    ViewModel.StatusMessage = $"Duplicated as sibling: {txt}";
                }
            };
            menu.Items.Add(dupSiblingItem);
            if (sender is FrameworkElement fe) menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
        }

        private void OnDeleteButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();
            var delItem = new MenuFlyoutItem { Text = "Delete selected", KeyboardAcceleratorTextOverride = "Del", Icon = new FontIcon { Glyph = "\uE74D" } };
            delItem.Click += (_, _) => ViewModel.DeleteSelectedCommand.Execute(null);
            menu.Items.Add(delItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            var clearItem = new MenuFlyoutItem { Text = "Clear entire outline", Icon = new FontIcon { Glyph = "\uE894" } };
            clearItem.Click += (_, _) =>
            {
                while (ViewModel.HasSelection || ViewModel.OutlineRows.Count > 0)
                {
                    if (ViewModel.OutlineRows.Count == 0) break;
                    var first = ViewModel.OutlineRows[0];
                    ViewModel.DeleteSelectedCommand.Execute(first);
                }
                ViewModel.StatusMessage = "Cleared outline";
            };
            menu.Items.Add(clearItem);
            if (sender is FrameworkElement fe) menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
        }

        private void OnRowBulletDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StudioNodeViewModel node)
            {
                e.Handled = true;
                ViewModel.Select(node);
                if (ViewModel.CanDemote)
                {
                    ViewModel.DemoteCommand.Execute(node);
                    ViewModel.StatusMessage = $"Demoted item to level {node.Depth + 1}";
                }
                else if (ViewModel.CanPromote)
                {
                    ViewModel.PromoteCommand.Execute(node);
                    ViewModel.StatusMessage = $"Promoted item to level {node.Depth + 1}";
                }
            }
        }

        private void OnRowBulletRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StudioNodeViewModel node)
            {
                e.Handled = true;
                ViewModel.Select(node);
                ShowRowContextMenu(node, fe, e.GetPosition(fe));
            }
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

        private void OnOutlineEmptyPanelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            OnAddFirstNodeClick(sender, new RoutedEventArgs());
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

            var starterSub = new MenuFlyoutSubItem
            {
                Text = "Starter diagram structure",
                Icon = new FontIcon { Glyph = "\uE8B3" }
            };

            var starterSingle = new MenuFlyoutItem
            {
                Text = "Single root item",
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            starterSingle.Click += OnAddFirstNodeClick;
            starterSub.Items.Add(starterSingle);

            var starterProcess = new MenuFlyoutItem
            {
                Text = "3-Step Process (Discover → Build → Deliver)",
                Icon = new FontIcon { Glyph = "\uE80A" }
            };
            starterProcess.Click += (_, _) =>
            {
                ViewModel.ReplaceOutline("- Step 1: Discover\n- Step 2: Build\n- Step 3: Deliver");
                ViewModel.StatusMessage = "Applied 3-Step Process starter template.";
            };
            starterSub.Items.Add(starterProcess);

            var starterHierarchy = new MenuFlyoutItem
            {
                Text = "Hierarchy / Org Chart (Lead → Teams)",
                Icon = new FontIcon { Glyph = "\uF246" }
            };
            starterHierarchy.Click += (_, _) =>
            {
                ViewModel.ReplaceOutline("- Executive Lead\n  - Engineering Team\n  - Product Design\n  - Operations");
                ViewModel.StatusMessage = "Applied Hierarchy starter template.";
            };
            starterSub.Items.Add(starterHierarchy);

            var starterCycle = new MenuFlyoutItem
            {
                Text = "Cycle (Plan → Do → Check → Act)",
                Icon = new FontIcon { Glyph = "\uEC26" }
            };
            starterCycle.Click += (_, _) =>
            {
                ViewModel.ReplaceOutline("- Plan\n- Do\n- Check\n- Act");
                ViewModel.StatusMessage = "Applied 4-Stage Cycle starter template.";
            };
            starterSub.Items.Add(starterCycle);

            var starterComparison = new MenuFlyoutItem
            {
                Text = "Comparison (Pros & Cons)",
                Icon = new FontIcon { Glyph = "\uE8D3" }
            };
            starterComparison.Click += (_, _) =>
            {
                ViewModel.ReplaceOutline("- Option Alpha\n  - Advantage 1\n  - Consideration 1\n- Option Beta\n  - Advantage 2\n  - Consideration 2");
                ViewModel.StatusMessage = "Applied Comparison starter template.";
            };
            starterSub.Items.Add(starterComparison);

            menu.Items.Add(starterSub);

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
                            ViewModel.ReplaceOutline(text);
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

            menu.ShowAt(UndoStudioButton, KeyboardContextMenu.PointIn(e, UndoStudioButton));
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

            menu.ShowAt(RedoStudioButton, KeyboardContextMenu.PointIn(e, RedoStudioButton));
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

            menu.ShowAt(InsertIntoDocumentButton, KeyboardContextMenu.PointIn(e, InsertIntoDocumentButton));
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

        private static bool IsWithinTextBox(DependencyObject? element)
        {
            for (; element is not null; element = VisualTreeHelper.GetParent(element))
                if (element is TextBox) return true;
            return false;
        }

        private void OnRenameTextBoxDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                tb.SelectAll();
                e.Handled = true;
            }
        }

        // Rename boxes live in the row template, so each gets its menu as it loads; TextBoxMenu
        // ignores the repeat Loaded of a recycled row.
        private void OnRenameTextBoxLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb) TextBoxMenu.Attach(tb, menu => FillRenameTextBoxMenu(tb, menu));
        }

        private void FillRenameTextBoxMenu(TextBox tb, MenuFlyout menu)
        {
            string text = tb.Text ?? "";
            bool hasText = !string.IsNullOrEmpty(text);

            var copyItem = new MenuFlyoutItem { Text = "Copy text", Icon = new FontIcon { Glyph = "\uE8C8" }, IsEnabled = hasText };
            copyItem.Click += (_, _) =>
            {
                CopyAndReport(text, $"Copied: {text}");
            };
            menu.Items.Add(copyItem);

            var pasteItem = new MenuFlyoutItem { Text = "Paste text", Icon = new FontIcon { Glyph = "\uE77F" } };
            pasteItem.Click += async (_, _) =>
            {
                try
                {
                    var pkg = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (pkg.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    {
                        var t = await pkg.GetTextAsync();
                        if (!string.IsNullOrEmpty(t))
                        {
                            tb.Text = t.Trim();
                            ViewModel.StatusMessage = "Pasted text";
                        }
                    }
                }
                catch { }
            };
            menu.Items.Add(pasteItem);

            var clearItem = new MenuFlyoutItem { Text = "Clear text", Icon = new FontIcon { Glyph = "\uE894" }, IsEnabled = hasText };
            clearItem.Click += (_, _) =>
            {
                tb.Text = "";
                ViewModel.StatusMessage = "Cleared text";
            };
            menu.Items.Add(clearItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var upperItem = new MenuFlyoutItem { Text = "UPPERCASE", Icon = new FontIcon { Glyph = "\uE8AC" }, IsEnabled = hasText };
            upperItem.Click += (_, _) =>
            {
                tb.Text = text.ToUpperInvariant();
                ViewModel.StatusMessage = "Converted to UPPERCASE";
            };
            menu.Items.Add(upperItem);

            var lowerItem = new MenuFlyoutItem { Text = "lowercase", Icon = new FontIcon { Glyph = "\uE8AD" }, IsEnabled = hasText };
            lowerItem.Click += (_, _) =>
            {
                tb.Text = text.ToLowerInvariant();
                ViewModel.StatusMessage = "Converted to lowercase";
            };
            menu.Items.Add(lowerItem);

            var titleItem = new MenuFlyoutItem { Text = "Title Case", Icon = new FontIcon { Glyph = "\uE8B0" }, IsEnabled = hasText };
            titleItem.Click += (_, _) =>
            {
                tb.Text = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower());
                ViewModel.StatusMessage = "Converted to Title Case";
            };
            menu.Items.Add(titleItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var selectAllItem = new MenuFlyoutItem { Text = "Select all", Icon = new FontIcon { Glyph = "\uE8B3" }, IsEnabled = hasText };
            selectAllItem.Click += (_, _) => tb.SelectAll();
            menu.Items.Add(selectAllItem);
        }

        // ------------------------------------------------------------------ context menus

        private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // A right-click in the rename box is the box's own (text or casing) menu.
            if (e.OriginalSource is DependencyObject source && IsWithinTextBox(source)) return;
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
            if (e.OriginalSource is DependencyObject source && IsWithinTextBox(source)) return;
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
                Text = "Add child",
                KeyboardAcceleratorTextOverride = "Insert",
                Icon = new FontIcon { Glyph = "\uE710" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            addChildItem.Click += (_, _) => { ViewModel.AddChildCommand.Execute(null); FocusRenameBox(); };
            menu.Items.Add(addChildItem);

            var addSiblingItem = new MenuFlyoutItem
            {
                Text = "Add sibling",
                KeyboardAcceleratorTextOverride = "Enter",
                Icon = new FontIcon { Glyph = "\uE710" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            addSiblingItem.Click += (_, _) => { ViewModel.AddSiblingCommand.Execute(null); FocusRenameBox(); };
            menu.Items.Add(addSiblingItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var renameItem = new MenuFlyoutItem
            {
                Text = "Rename",
                KeyboardAcceleratorTextOverride = "F2",
                Icon = new FontIcon { Glyph = "\uE70F" },
                IsEnabled = ViewModel.HasSelection
            };
            renameItem.Click += OnRenameClick;
            menu.Items.Add(renameItem);

            var duplicateItem = new MenuFlyoutItem
            {
                Text = "Duplicate",
                KeyboardAcceleratorTextOverride = "Ctrl+D",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = ViewModel.HasSelection
            };
            duplicateItem.Click += (_, _) => ViewModel.DuplicateSelectedCommand.Execute(null);
            menu.Items.Add(duplicateItem);

            var deleteItem = new MenuFlyoutItem
            {
                Text = "Delete",
                KeyboardAcceleratorTextOverride = "Del",
                Icon = new FontIcon { Glyph = "\uE74D" },
                IsEnabled = ViewModel.HasSelection
            };
            deleteItem.Click += (_, _) => ViewModel.DeleteSelectedCommand.Execute(null);
            menu.Items.Add(deleteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var moveToTopItem = new MenuFlyoutItem
            {
                Text = "Move to top",
                KeyboardAcceleratorTextOverride = "Alt+Home",
                Icon = new FontIcon { Glyph = "\uE898" },
                IsEnabled = ViewModel.CanMoveUp
            };
            moveToTopItem.Click += (_, _) =>
            {
                int c = 0;
                while (ViewModel.CanMoveUp) { ViewModel.MoveUpCommand.Execute(null); c++; }
                ViewModel.StatusMessage = $"Moved item to top of outline ({CountText.Of(c, "step")})";
            };
            menu.Items.Add(moveToTopItem);

            var moveUpItem = new MenuFlyoutItem
            {
                Text = "Move up",
                KeyboardAcceleratorTextOverride = "Alt+Up",
                Icon = new FontIcon { Glyph = "\uE74A" },
                IsEnabled = ViewModel.CanMoveUp
            };
            moveUpItem.Click += (_, _) => ViewModel.MoveUpCommand.Execute(null);
            menu.Items.Add(moveUpItem);

            var moveDownItem = new MenuFlyoutItem
            {
                Text = "Move down",
                KeyboardAcceleratorTextOverride = "Alt+Down",
                Icon = new FontIcon { Glyph = "\uE74B" },
                IsEnabled = ViewModel.CanMoveDown
            };
            moveDownItem.Click += (_, _) => ViewModel.MoveDownCommand.Execute(null);
            menu.Items.Add(moveDownItem);

            var moveToBottomItem = new MenuFlyoutItem
            {
                Text = "Move to bottom",
                KeyboardAcceleratorTextOverride = "Alt+End",
                Icon = new FontIcon { Glyph = "\uE896" },
                IsEnabled = ViewModel.CanMoveDown
            };
            moveToBottomItem.Click += (_, _) =>
            {
                int c = 0;
                while (ViewModel.CanMoveDown) { ViewModel.MoveDownCommand.Execute(null); c++; }
                ViewModel.StatusMessage = $"Moved item to bottom of outline ({CountText.Of(c, "step")})";
            };
            menu.Items.Add(moveToBottomItem);

            var promoteItem = new MenuFlyoutItem
            {
                Text = "Promote",
                KeyboardAcceleratorTextOverride = "Shift+Tab",
                Icon = new FontIcon { Glyph = "\uE76B" },
                IsEnabled = ViewModel.CanPromote
            };
            promoteItem.Click += (_, _) => ViewModel.PromoteCommand.Execute(null);
            menu.Items.Add(promoteItem);

            var demoteItem = new MenuFlyoutItem
            {
                Text = "Demote",
                KeyboardAcceleratorTextOverride = "Tab",
                Icon = new FontIcon { Glyph = "\uE76C" },
                IsEnabled = ViewModel.CanDemote
            };
            demoteItem.Click += (_, _) => ViewModel.DemoteCommand.Execute(null);
            menu.Items.Add(demoteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets",
                KeyboardAcceleratorTextOverride = "Ctrl+C",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
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
                Text = "Copy outline bullets",
                KeyboardAcceleratorTextOverride = "Ctrl+C",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var editMarkdownItem = new MenuFlyoutItem
            {
                Text = "Switch to Markdown editor",
                KeyboardAcceleratorTextOverride = "Ctrl+2",
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            editMarkdownItem.Click += (_, _) =>
            {
                if (MarkdownTab is not null && EditorTabs is not null) EditorTabs.SelectedItem = MarkdownTab;
            };
            menu.Items.Add(editMarkdownItem);

            menu.ShowAt(CopyOutlineMarkdownButton, KeyboardContextMenu.PointIn(e, CopyOutlineMarkdownButton));
        }

        private void OnCopySmartArtBlockButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (CopySmartArtBlockButton is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets",
                KeyboardAcceleratorTextOverride = "Ctrl+C",
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
                CopyAndReport(ViewModel.PreviewTitle ?? "", "Copied layout title to clipboard");
            };
            menu.Items.Add(copyTitleItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var refreshItem = new MenuFlyoutItem
            {
                Text = "Refresh preview",
                KeyboardAcceleratorTextOverride = "F5",
                Icon = new FontIcon { Glyph = "\uE72C" }
            };
            refreshItem.Click += (_, _) =>
            {
                RefreshWebView();
                ViewModel.StatusMessage = "Preview refreshed.";
            };
            menu.Items.Add(refreshItem);

            menu.ShowAt(CopySmartArtBlockButton, KeyboardContextMenu.PointIn(e, CopySmartArtBlockButton));
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
                    CopyAndReport(node.Text, $"Copied \"{node.Text}\" to clipboard.");
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
                    CopyAndReport($"{indent}* {node.Text}", $"Copied \"{node.Text}\" bullet to clipboard.");
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

            var moveToTopItem = new MenuFlyoutItem
            {
                Text = "Move to top",
                Icon = new FontIcon { Glyph = "\uE898" },
                KeyboardAcceleratorTextOverride = "Alt+Home",
                IsEnabled = ViewModel.CanMoveUp
            };
            moveToTopItem.Click += (_, _) =>
            {
                int c = 0;
                while (ViewModel.CanMoveUp) { ViewModel.MoveUp(); c++; }
                ViewModel.StatusMessage = $"Moved \"{node.Text}\" to top of outline ({CountText.Of(c, "step")})";
            };
            menu.Items.Add(moveToTopItem);

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

            var moveToBottomItem = new MenuFlyoutItem
            {
                Text = "Move to bottom",
                Icon = new FontIcon { Glyph = "\uE896" },
                KeyboardAcceleratorTextOverride = "Alt+End",
                IsEnabled = ViewModel.CanMoveDown
            };
            moveToBottomItem.Click += (_, _) =>
            {
                int c = 0;
                while (ViewModel.CanMoveDown) { ViewModel.MoveDown(); c++; }
                ViewModel.StatusMessage = $"Moved \"{node.Text}\" to bottom of outline ({CountText.Of(c, "step")})";
            };
            menu.Items.Add(moveToBottomItem);

            var promoteToRootItem = new MenuFlyoutItem
            {
                Text = "Promote to root",
                Icon = new FontIcon { Glyph = "\uE898" },
                IsEnabled = ViewModel.CanPromote
            };
            promoteToRootItem.Click += (_, _) =>
            {
                int c = 0;
                while (ViewModel.CanPromote) { ViewModel.Promote(); c++; }
                ViewModel.StatusMessage = $"Promoted \"{node.Text}\" to root level ({CountText.Of(c, "step")})";
            };
            menu.Items.Add(promoteToRootItem);

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
                Text = "Refresh preview",
                KeyboardAcceleratorTextOverride = "F5",
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
                Text = "Refresh preview",
                KeyboardAcceleratorTextOverride = "F5",
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
                Text = "Copy SmartArt Markdown block",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var copyOutlineItem = new MenuFlyoutItem
            {
                Text = "Copy outline as Markdown",
                KeyboardAcceleratorTextOverride = "Ctrl+C",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyOutlineItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyOutlineItem);

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
            AddPaletteSubmenu(menu);

            menu.ShowAt(target, e.GetPosition(target));
            e.Handled = true;
        }

        private void OnCopyOutlineMarkdownClick(object sender, RoutedEventArgs e) => CopyOutlineMarkdown();

        private void OnCopySmartArtBlockClick(object sender, RoutedEventArgs e) => CopySmartArtBlock();

        private void CopyOutlineMarkdown()
        {
            string md = (ViewModel.OutlineText).Trim();
            if (string.IsNullOrEmpty(md))
            {
                // Ctrl+C and the Copy button did nothing on an empty outline; say why.
                ViewModel.StatusMessage = "Nothing to copy: the outline is empty.";
                return;
            }
            CopyAndReport(md, "Copied outline bullets to clipboard.", CopyOutlineMarkdownButton);
        }

        private void CopySmartArtBlock()
        {
            if (!ViewModel.TryBuildSmartArtBlock(out string block, out string name, out string err))
            {
                ViewModel.StatusMessage = err;
                return;
            }
            CopyAndReport(block.Trim(), $"Copied {name} SmartArt Markdown block to clipboard.", CopySmartArtBlockButton);
        }

        private void OnExportSvgButtonClick(object sender, RoutedEventArgs e)
        {
            ExportSvgFlyout?.ShowAt(sender as FrameworkElement ?? ExportSvgButton);
        }

        private void OnExportSvgButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ExportSvgFlyout?.ShowAt(sender as FrameworkElement ?? ExportSvgButton);
        }

        private void OnDiagramBgButtonClick(object sender, RoutedEventArgs e)
        {
            DiagramBgFlyout?.ShowAt(sender as FrameworkElement ?? DiagramBgButton);
        }

        private void OnDiagramBgButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            DiagramBgFlyout?.ShowAt(sender as FrameworkElement ?? DiagramBgButton);
        }

        private void OnBgDefaultPaperClick(object sender, RoutedEventArgs e) => SetDiagramBgMode(DiagramPreviewBgMode.DefaultCard);
        private void OnBgDarkSlateClick(object sender, RoutedEventArgs e) => SetDiagramBgMode(DiagramPreviewBgMode.DarkSlate);
        private void OnBgCheckerboardClick(object sender, RoutedEventArgs e) => SetDiagramBgMode(DiagramPreviewBgMode.Checkerboard);
        private void OnBgDotGridClick(object sender, RoutedEventArgs e) => SetDiagramBgMode(DiagramPreviewBgMode.DotGrid);

        private void SetDiagramBgMode(DiagramPreviewBgMode mode)
        {
            _currentDiagramBgMode = mode;
            RefreshWebView();
            if (DiagramBgButton is not null) Services.CopyFeedback.ApplyButtonTick(DiagramBgButton, null);
        }

        private void OnNodeGeometryButtonClick(object sender, RoutedEventArgs e)
        {
            NodeGeometryFlyout?.ShowAt(sender as FrameworkElement ?? NodeGeometryButton);
        }

        private void OnNodeGeometryButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            NodeGeometryFlyout?.ShowAt(sender as FrameworkElement ?? NodeGeometryButton);
        }

        private void OnGeomDefaultClick(object sender, RoutedEventArgs e) => SetNodeGeometryMode(SmartArtNodeGeometryMode.Default);
        private void OnGeomPillRoundedClick(object sender, RoutedEventArgs e) => SetNodeGeometryMode(SmartArtNodeGeometryMode.PillRounded);
        private void OnGeomSharpBoxClick(object sender, RoutedEventArgs e) => SetNodeGeometryMode(SmartArtNodeGeometryMode.SharpBox);
        private void OnGeomModernSoftClick(object sender, RoutedEventArgs e) => SetNodeGeometryMode(SmartArtNodeGeometryMode.ModernSoft);
        private void OnGeomContrastBorderClick(object sender, RoutedEventArgs e) => SetNodeGeometryMode(SmartArtNodeGeometryMode.ContrastBorder);

        private void SetNodeGeometryMode(SmartArtNodeGeometryMode mode)
        {
            _currentNodeGeomMode = mode;
            RefreshWebView();
            string label = mode switch
            {
                SmartArtNodeGeometryMode.PillRounded => "Pill Rounded (20px)",
                SmartArtNodeGeometryMode.SharpBox => "Sharp Box (0px)",
                SmartArtNodeGeometryMode.ModernSoft => "Modern Soft Shadow",
                SmartArtNodeGeometryMode.ContrastBorder => "Bold Contrast Borders",
                _ => "Default Geometry"
            };
            ViewModel.StatusMessage = $"Diagram node geometry set to: {label}";
            if (NodeGeometryButton is not null) Services.CopyFeedback.ApplyButtonTick(NodeGeometryButton, null);
        }

        private void OnAspectRatioButtonClick(object sender, RoutedEventArgs e)
        {
            AspectRatioFlyout?.ShowAt(sender as FrameworkElement ?? AspectRatioButton);
        }

        private void OnAspectRatioButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            AspectRatioFlyout?.ShowAt(sender as FrameworkElement ?? AspectRatioButton);
        }

        private void OnAspectDefaultClick(object sender, RoutedEventArgs e) => SetAspectRatioMode(SmartArtAspectRatioMode.Default);
        private void OnAspect16x9Click(object sender, RoutedEventArgs e) => SetAspectRatioMode(SmartArtAspectRatioMode.Widescreen16x9);
        private void OnAspect4x3Click(object sender, RoutedEventArgs e) => SetAspectRatioMode(SmartArtAspectRatioMode.Standard4x3);
        private void OnAspect1x1Click(object sender, RoutedEventArgs e) => SetAspectRatioMode(SmartArtAspectRatioMode.Square1x1);
        private void OnAspect21x9Click(object sender, RoutedEventArgs e) => SetAspectRatioMode(SmartArtAspectRatioMode.Ultrawide21x9);

        private void SetAspectRatioMode(SmartArtAspectRatioMode mode)
        {
            _currentAspectRatioMode = mode;
            RefreshWebView();
            string label = mode switch
            {
                SmartArtAspectRatioMode.Widescreen16x9 => "16:9 Widescreen Slide (PowerPoint)",
                SmartArtAspectRatioMode.Standard4x3 => "4:3 Standard Document Figure",
                SmartArtAspectRatioMode.Square1x1 => "1:1 Square Card",
                SmartArtAspectRatioMode.Ultrawide21x9 => "21:9 Ultrawide Banner",
                _ => "Default Fluid Canvas"
            };
            ViewModel.StatusMessage = $"Diagram preview aspect ratio set to: {label}";
            if (AspectRatioButton is not null) Services.CopyFeedback.ApplyButtonTick(AspectRatioButton, null);
        }

        private void OnZoomButtonClick(object sender, RoutedEventArgs e)
        {
            ZoomFlyout?.ShowAt(sender as FrameworkElement ?? ZoomButton);
        }

        private void OnZoomButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            SetZoomMode(_currentZoomMode == SmartArtZoomMode.Fit ? SmartArtZoomMode.Z100 : SmartArtZoomMode.Fit);
        }

        private void OnZoomFitClick(object sender, RoutedEventArgs e) => SetZoomMode(SmartArtZoomMode.Fit);
        private void OnZoom50Click(object sender, RoutedEventArgs e) => SetZoomMode(SmartArtZoomMode.Z50);
        private void OnZoom75Click(object sender, RoutedEventArgs e) => SetZoomMode(SmartArtZoomMode.Z75);
        private void OnZoom100Click(object sender, RoutedEventArgs e) => SetZoomMode(SmartArtZoomMode.Z100);
        private void OnZoom125Click(object sender, RoutedEventArgs e) => SetZoomMode(SmartArtZoomMode.Z125);
        private void OnZoom150Click(object sender, RoutedEventArgs e) => SetZoomMode(SmartArtZoomMode.Z150);
        private void OnZoom200Click(object sender, RoutedEventArgs e) => SetZoomMode(SmartArtZoomMode.Z200);

        private void SetZoomMode(SmartArtZoomMode mode)
        {
            _currentZoomMode = mode;
            RefreshWebView();
            string label = mode switch
            {
                SmartArtZoomMode.Z50 => "50% Compact",
                SmartArtZoomMode.Z75 => "75%",
                SmartArtZoomMode.Z100 => "100% Actual Size",
                SmartArtZoomMode.Z125 => "125%",
                SmartArtZoomMode.Z150 => "150%",
                SmartArtZoomMode.Z200 => "200% High Detail",
                _ => "Fit to View (Auto)"
            };
            ViewModel.StatusMessage = $"Diagram preview zoom set to: {label}";
            if (ZoomButton is not null) Services.CopyFeedback.ApplyButtonTick(ZoomButton, null);
        }

        private void OnCopySvg1xClick(object sender, RoutedEventArgs e) => CopySvgToClipboard(1.0, "1x SVG");
        private void OnCopySvg2xClick(object sender, RoutedEventArgs e) => CopySvgToClipboard(2.0, "2x Retina SVG");
        private void OnCopySvg4xClick(object sender, RoutedEventArgs e) => CopySvgToClipboard(4.0, "4x Ultra HD SVG");
        private void OnCopySvgPrintClick(object sender, RoutedEventArgs e) => CopySvgToClipboard(3.125, "Print (300 DPI) SVG");

        private void OnCopySvgDataUriClick(object sender, RoutedEventArgs e)
        {
            string svg = GetExportSvg(1.0);
            if (!HasExportSvg(svg)) return;
            string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
            string uri = "data:image/svg+xml;base64," + b64;
            CopyAndReport(uri, $"Copied SVG Data URI to clipboard ({MarkSmith.Services.CountText.Of(uri.Length, "character")}).", ExportSvgButton);
        }

        private async void OnSaveSvgFileClick(object sender, RoutedEventArgs e)
        {
            string svg = GetExportSvg(2.0);
            if (!HasExportSvg(svg)) return;
            try
            {
                string suggestedName = $"{ViewModel.SelectedLayout?.Name ?? "smartart"}-diagram.svg";
                var path = await MarkSmith.Services.NativeFilePicker.PickSaveFileAsync(
                    this, "Save SVG diagram", MarkSmith.Services.NativeFilePicker.Purpose.Exports,
                    suggestedName, new[] { MarkSmith.Models.FileType.Of("SVG image", ".svg") }, okLabel: "Save");
                if (!string.IsNullOrEmpty(path))
                {
                    await System.IO.File.WriteAllTextAsync(path, svg, System.Text.Encoding.UTF8);
                    ViewModel.StatusMessage = $"Saved SVG diagram to {System.IO.Path.GetFileName(path)}.";
                }
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Failed to save SVG: {ex.Message}";
            }
        }

        private string GetExportSvg(double scale = 1.0)
        {
            string html = ViewModel.PreviewHtml ?? "";
            if (!string.IsNullOrEmpty(_customHexOverride))
            {
                html = ApplyCustomHexToSvgHtml(html, _customHexOverride);
            }
            int start = html.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";
            int end = html.IndexOf("</svg>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return "";
            string svg = html.Substring(start, (end + 6) - start);

            if (Math.Abs(scale - 1.0) > 0.01)
            {
                var wMatch = System.Text.RegularExpressions.Regex.Match(svg, @"width=""(\d+(?:\.\d+)?)""");
                var hMatch = System.Text.RegularExpressions.Regex.Match(svg, @"height=""(\d+(?:\.\d+)?)""");
                if (wMatch.Success && double.TryParse(wMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double w))
                {
                    svg = svg.Replace(wMatch.Value, $"width=\"{(w * scale):F0}\"");
                }
                if (hMatch.Success && double.TryParse(hMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double h))
                {
                    svg = svg.Replace(hMatch.Value, $"height=\"{(h * scale):F0}\"");
                }
            }
            return svg;
        }

        private void CopySvgToClipboard(double scale, string label)
        {
            string svg = GetExportSvg(scale);
            if (!HasExportSvg(svg)) return;
            // svg.Length counts characters, not bytes (it said "bytes" before).
            CopyAndReport(svg, $"Copied {label} to clipboard ({MarkSmith.Services.CountText.Of(svg.Length, "character")}).", ExportSvgButton);
        }

        // The SVG copies and Save SVG did nothing while the preview had no diagram yet, and copied the
        // empty-state placeholder SVG for an empty outline; say why instead.
        private bool HasExportSvg(string? svg)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.OutlineText))
            {
                ViewModel.StatusMessage = SmartArtDesignStudioViewModel.EmptyOutlineMessage;
                return false;
            }
            if (!string.IsNullOrWhiteSpace(svg)) return true;
            ViewModel.StatusMessage = "No diagram preview to export yet: wait for the preview to finish drawing.";
            return false;
        }

        // Outline commands on an empty outline returned without a word; returns the outline, or null after saying why.
        private string? OutlineForCommand()
        {
            string markdown = ViewModel.OutlineText;
            if (!string.IsNullOrWhiteSpace(markdown)) return markdown;
            ViewModel.StatusMessage = SmartArtDesignStudioViewModel.EmptyOutlineMessage;
            return null;
        }

        // Window-level shortcuts: Delete/Backspace = delete node, F2 = rename, Ctrl+D = duplicate,
        // Ctrl+Z/Y = undo/redo, Ctrl+F = focus layout search, Ctrl+Enter = insert into document,
        // Ctrl+Shift+C = copy SmartArt block, Ctrl+C = copy outline bullets, Ctrl+1/2 = switch tabs.
        // TextBoxes (rename box, Markdown Data) keep their native keys — focus guard.
        private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Keys pressed inside an open menu bubble here too. Delete / Backspace in a node's menu deleted
            // the selected node behind it, and F2 / Ctrl+Z / Ctrl+D acted behind it as well.
            if (PopupKeys.FocusIsOutside(RootGrid)) return;
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

            if (ctrl && (e.Key is VirtualKey.Add || (int)e.Key == 187))
            {
                SetPreviewZoom(_previewZoomFactor + 0.15);
                e.Handled = true;
                return;
            }
            if (ctrl && (e.Key is VirtualKey.Subtract || (int)e.Key == 189))
            {
                SetPreviewZoom(_previewZoomFactor - 0.15);
                e.Handled = true;
                return;
            }
            if (ctrl && (e.Key is VirtualKey.Number0 or VirtualKey.NumberPad0))
            {
                SetPreviewZoom(1.0);
                e.Handled = true;
                return;
            }

            if ((sender as UIElement)?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox) return;

            if (ctrl && e.Key == VirtualKey.C)
            {
                if (ViewModel.SelectedNode is { } selected && !string.IsNullOrWhiteSpace(selected.Text))
                {
                    CopyAndReport(selected.Text, $"Copied \"{selected.Text}\" to clipboard.");
                    e.Handled = true;
                    return;
                }
                CopyOutlineMarkdown();
                e.Handled = true;
            }
            else if (ctrl && e.Key == VirtualKey.Z) { ViewModel.UndoCommand.Execute(null); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.Y) { ViewModel.RedoCommand.Execute(null); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.D) { ViewModel.DuplicateSelectedCommand.Execute(null); e.Handled = true; }
            // Backspace on a layout in the gallery, a colour scheme or the quick layout strip deleted the
            // selected outline item; those lists leave it alone now (the outline handles its own keys).
            else if ((e.Key is VirtualKey.Delete or VirtualKey.Back) && !ChoiceKeys.FocusIsInChoiceControl(RootGrid.XamlRoot))
            { ViewModel.DeleteSelectedCommand.Execute(null); e.Handled = true; }
            else if (e.Key == VirtualKey.F2) { BeginRenameAndFocus(); e.Handled = true; }
        }

        private void OnMarkdownTextBoxDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            MarkdownTextBox?.SelectAll();
        }

        private void FillMarkdownTextBoxMenu(MenuFlyout menu)
        {
            if (MarkdownTextBox is null) return;
            var insertItem = new MenuFlyoutItem
            {
                Text = "Insert into document",
                KeyboardAcceleratorTextOverride = "Ctrl+Enter",
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
                Text = "Copy :::smartart block",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets",
                KeyboardAcceleratorTextOverride = "Ctrl+C",
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var starterTemplatesSub = new MenuFlyoutSubItem
            {
                Text = "Insert starter template",
                Icon = new FontIcon { Glyph = "\uE8B3" }
            };

            var tmplProcess = new MenuFlyoutItem
            {
                Text = "3-Step Process (Discover → Build → Deliver)",
                Icon = new FontIcon { Glyph = "\uE80A" }
            };
            tmplProcess.Click += (_, _) =>
            {
                ViewModel.ReplaceOutline("- Step 1: Discover\n- Step 2: Build\n- Step 3: Deliver");
                ViewModel.StatusMessage = "Applied 3-Step Process template.";
            };
            starterTemplatesSub.Items.Add(tmplProcess);

            var tmplHierarchy = new MenuFlyoutItem
            {
                Text = "Hierarchy / Org Chart (Lead → Teams)",
                Icon = new FontIcon { Glyph = "\uF246" }
            };
            tmplHierarchy.Click += (_, _) =>
            {
                ViewModel.ReplaceOutline("- Executive Lead\n  - Engineering Team\n  - Product Design\n  - Operations");
                ViewModel.StatusMessage = "Applied Hierarchy template.";
            };
            starterTemplatesSub.Items.Add(tmplHierarchy);

            var tmplCycle = new MenuFlyoutItem
            {
                Text = "Cycle (Plan → Do → Check → Act)",
                Icon = new FontIcon { Glyph = "\uEC26" }
            };
            tmplCycle.Click += (_, _) =>
            {
                ViewModel.ReplaceOutline("- Plan\n- Do\n- Check\n- Act");
                ViewModel.StatusMessage = "Applied 4-Stage Cycle template.";
            };
            starterTemplatesSub.Items.Add(tmplCycle);

            var tmplComparison = new MenuFlyoutItem
            {
                Text = "Comparison (Pros & Cons)",
                Icon = new FontIcon { Glyph = "\uE8D3" }
            };
            tmplComparison.Click += (_, _) =>
            {
                ViewModel.ReplaceOutline("- Option Alpha\n  - Advantage 1\n  - Consideration 1\n- Option Beta\n  - Advantage 2\n  - Consideration 2");
                ViewModel.StatusMessage = "Applied Comparison template.";
            };
            starterTemplatesSub.Items.Add(tmplComparison);

            menu.Items.Add(starterTemplatesSub);

            menu.Items.Add(new MenuFlyoutSeparator());

            var pasteItem = new MenuFlyoutItem
            {
                Text = "Paste bullets",
                KeyboardAcceleratorTextOverride = "Ctrl+V",
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
                Text = "Select all",
                KeyboardAcceleratorTextOverride = "Ctrl+A",
                Icon = new FontIcon { Glyph = "\uE8B3" }
            };
            selectAllItem.Click += (_, _) => MarkdownTextBox.SelectAll();
            menu.Items.Add(selectAllItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var switchToOutlineItem = new MenuFlyoutItem
            {
                Text = "Switch to Outline view",
                KeyboardAcceleratorTextOverride = "Ctrl+1",
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            switchToOutlineItem.Click += (_, _) =>
            {
                EditorTabs.SelectedItem = OutlineTab;
            };
            menu.Items.Add(switchToOutlineItem);
        }

        private void OnStatusBarDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var summary = $"{ViewModel.StatusMessage} — {ViewModel.OutlineSummaryText}";
            CopyAndReport(summary, "Copied status & outline summary to clipboard");
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
                CopyAndReport(ViewModel.StatusMessage ?? "", "Copied status message to clipboard");
            };
            menu.Items.Add(copyStatusItem);

            var copySummaryItem = new MenuFlyoutItem
            {
                Text = "Copy outline summary",
                Icon = new FontIcon { Glyph = "\uE946" }
            };
            copySummaryItem.Click += (_, _) =>
            {
                CopyAndReport(ViewModel.OutlineSummaryText ?? "", "Copied outline summary to clipboard");
            };
            menu.Items.Add(copySummaryItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var insertItem = new MenuFlyoutItem
            {
                Text = "Insert into document",
                KeyboardAcceleratorTextOverride = "Ctrl+Enter",
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

        private void OnLayoutCountButtonClick(object sender, RoutedEventArgs e) =>
            KeyboardContextMenu.Invoke(LayoutCountButton, OnLayoutCountButtonRightTapped);

        private void OnLayoutCountButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (LayoutCountButton is null) return;
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
                CopyAndReport(countText, $"Copied layout count: {countText}");
            };
            menu.Items.Add(copyCountItem);

            var focusSearchItem = new MenuFlyoutItem
            {
                Text = "Focus layout search",
                KeyboardAcceleratorTextOverride = "Ctrl+F",
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

            menu.ShowAt(LayoutCountButton, KeyboardContextMenu.PointIn(e, LayoutCountButton));
        }

        // Two clicks already opened the menu; don't also run the status bar's double-click copy.
        private void OnOutlineSummaryButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

        private void OnOutlineSummaryButtonClick(object sender, RoutedEventArgs e) =>
            KeyboardContextMenu.Invoke(OutlineSummaryButton, OnOutlineSummaryButtonRightTapped);

        private void OnOutlineSummaryButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (OutlineSummaryButton is null) return;
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
                CopyAndReport(summaryText, $"Copied outline summary: {summaryText}");
            };
            menu.Items.Add(copySummaryItem);

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets",
                KeyboardAcceleratorTextOverride = "Ctrl+C",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var addRootItem = new MenuFlyoutItem
            {
                Text = "Add root item",
                KeyboardAcceleratorTextOverride = "Insert",
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            addRootItem.Click += (_, _) => { ViewModel.AddChild(null); FocusRenameBox(); };
            menu.Items.Add(addRootItem);

            var switchToEditorItem = new MenuFlyoutItem
            {
                Text = "Switch to Markdown editor",
                KeyboardAcceleratorTextOverride = "Ctrl+2",
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            switchToEditorItem.Click += (_, _) =>
            {
                EditorTabs.SelectedItem = MarkdownTab;
            };
            menu.Items.Add(switchToEditorItem);

            menu.ShowAt(OutlineSummaryButton, KeyboardContextMenu.PointIn(e, OutlineSummaryButton));
        }

        private void OnPreviewTitleButtonClick(object sender, RoutedEventArgs e) =>
            KeyboardContextMenu.Invoke(PreviewTitleButton, OnPreviewTitleButtonRightTapped);

        private void OnPreviewTitleButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (PreviewTitleButton is null) return;
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
                CopyAndReport(title, $"Copied layout title: {title}");
            };
            menu.Items.Add(copyNameItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets",
                KeyboardAcceleratorTextOverride = "Ctrl+C",
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
                Text = "Switch to Markdown editor",
                KeyboardAcceleratorTextOverride = "Ctrl+2",
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            switchEditorItem.Click += (_, _) =>
            {
                EditorTabs.SelectedItem = MarkdownTab;
            };
            menu.Items.Add(switchEditorItem);

            menu.Items.Add(new MenuFlyoutSeparator());
            AddPaletteSubmenu(menu);

            menu.ShowAt(PreviewTitleButton, KeyboardContextMenu.PointIn(e, PreviewTitleButton));
        }

        private void OnEditorTabsRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (EditorTabs is null) return;
            e.Handled = true;
            var menu = new MenuFlyout();

            var outlineItem = new MenuFlyoutItem
            {
                Text = "Outline editor",
                KeyboardAcceleratorTextOverride = "Ctrl+1",
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            outlineItem.Click += (_, _) => EditorTabs.SelectedItem = OutlineTab;
            menu.Items.Add(outlineItem);

            var markdownItem = new MenuFlyoutItem
            {
                Text = "Markdown source",
                KeyboardAcceleratorTextOverride = "Ctrl+2",
                Icon = new FontIcon { Glyph = "\uE943" }
            };
            markdownItem.Click += (_, _) => EditorTabs.SelectedItem = MarkdownTab;
            menu.Items.Add(markdownItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var copyBulletsItem = new MenuFlyoutItem
            {
                Text = "Copy outline bullets",
                KeyboardAcceleratorTextOverride = "Ctrl+C",
                Icon = new FontIcon { Glyph = "\uE8FD" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBulletsItem.Click += (_, _) => CopyOutlineMarkdown();
            menu.Items.Add(copyBulletsItem);

            var copyBlockItem = new MenuFlyoutItem
            {
                Text = "Copy :::smartart block",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                IsEnabled = !ViewModel.IsOutlineEmpty
            };
            copyBlockItem.Click += (_, _) => CopySmartArtBlock();
            menu.Items.Add(copyBlockItem);

            var addRootItem = new MenuFlyoutItem
            {
                Text = "Add root item",
                KeyboardAcceleratorTextOverride = "Insert",
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            addRootItem.Click += (_, _) => { ViewModel.AddChild(null); FocusRenameBox(); };
            menu.Items.Add(addRootItem);

            menu.ShowAt(EditorTabs, KeyboardContextMenu.PointIn(e, EditorTabs));
        }

        private void AddPaletteSubmenu(MenuFlyout menu)
        {
            var paletteSubmenu = new MenuFlyoutSubItem
            {
                Text = $"Color palette ({ViewModel.SelectedPalette})",
                Icon = new FontIcon { Glyph = "\uE790" }
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
                CopyAndReport(ViewModel.SelectedPalette, $"Copied palette: {ViewModel.SelectedPalette}");
            };
            paletteSubmenu.Items.Add(copyPaletteItem);

            menu.Items.Add(paletteSubmenu);
        }

        private void OnPaletteButtonClick(object sender, RoutedEventArgs e)
        {
            // Click opens the Flyout automatically
        }

        private void OnPaletteButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var menu = new MenuFlyout();
            AddPaletteSubmenu(menu);
            if (PaletteButton is not null)
                menu.ShowAt(PaletteButton, KeyboardContextMenu.PointIn(e, PaletteButton));
            else if (sender is FrameworkElement fe)
                menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
        }

        private readonly List<SmartArtPaletteChoice> _smartArtPaletteChoices = new();
        private bool _syncingSmartArtPalette;

        private static SolidColorBrush BrushFromHex(string hex)
        {
            if (hex.StartsWith('#')) hex = hex.Substring(1);
            if (hex.Length != 6) return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        }

        private void BuildSmartArtPaletteChoices()
        {
            _smartArtPaletteChoices.Clear();
            foreach (var palette in ViewModel.Palettes)
            {
                var (accents, _) = MarkSmith.Core.Preview.HtmlPreviewRenderer.GetPaletteColors(palette);
                var swatches = accents.Select(BrushFromHex).ToList();
                bool isSel = string.Equals(palette, ViewModel.SelectedPalette, StringComparison.OrdinalIgnoreCase);
                _smartArtPaletteChoices.Add(new SmartArtPaletteChoice
                {
                    Name = palette,
                    Swatches = swatches,
                    IsSelected = isSel
                });
            }
            if (PaletteList != null)
            {
                _syncingSmartArtPalette = true;
                try
                {
                    PaletteList.ItemsSource = null;
                    PaletteList.ItemsSource = _smartArtPaletteChoices;
                    PaletteList.SelectedItem = _smartArtPaletteChoices.FirstOrDefault(p => p.IsSelected);
                }
                finally { _syncingSmartArtPalette = false; }
            }
            if (CustomPaletteHexBox != null && !string.IsNullOrEmpty(_customHexOverride))
            {
                CustomPaletteHexBox.Text = "#" + _customHexOverride;
                if (CustomPaletteSwatch != null)
                {
                    ThemeBrush.Clear(CustomPaletteSwatch, Border.BackgroundProperty);
                    CustomPaletteSwatch.Background = BrushFromHex(_customHexOverride);
                }
            }
        }

        private string _activeHarmonyMode = "mono";

        private void OnPaletteFlyoutOpening(object? sender, object e)
        {
            BuildSmartArtPaletteChoices();
            GenerateHarmonySwatches(_activeHarmonyMode);
        }

        private void OnHarmonyMonoClick(object sender, RoutedEventArgs e)
        {
            _activeHarmonyMode = "mono";
            GenerateHarmonySwatches("mono");
        }

        private void OnHarmonyAnalogousClick(object sender, RoutedEventArgs e)
        {
            _activeHarmonyMode = "analogous";
            GenerateHarmonySwatches("analogous");
        }

        private void OnHarmonyTriadicClick(object sender, RoutedEventArgs e)
        {
            _activeHarmonyMode = "triadic";
            GenerateHarmonySwatches("triadic");
        }

        private void GenerateHarmonySwatches(string mode)
        {
            if (HarmonySwatchesPanel == null) return;
            HarmonySwatchesPanel.Children.Clear();

            if (HarmonyModeLabel != null)
            {
                HarmonyModeLabel.Text = mode switch
                {
                    "analogous" => "Analogous",
                    "triadic" => "Triadic",
                    _ => "Monochromatic"
                };
            }
            // Only the label said which mode was on. Re-checking here also keeps the active toggle
            // pressed when it is clicked again (a ToggleButton unchecks itself before Click).
            if (HarmonyMonoBtn != null)
            {
                HarmonyMonoBtn.IsChecked = mode is not ("analogous" or "triadic");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(HarmonyMonoBtn, HarmonyMonoBtn.IsChecked == true ? "Active" : "");
            }
            if (HarmonyAnalogousBtn != null)
            {
                HarmonyAnalogousBtn.IsChecked = mode == "analogous";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(HarmonyAnalogousBtn, HarmonyAnalogousBtn.IsChecked == true ? "Active" : "");
            }
            if (HarmonyTriadicBtn != null)
            {
                HarmonyTriadicBtn.IsChecked = mode == "triadic";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(HarmonyTriadicBtn, HarmonyTriadicBtn.IsChecked == true ? "Active" : "");
            }

            string baseHex = _customHexOverride ?? "0078D4";
            var textHex = CustomPaletteHexBox?.Text?.Trim() ?? "";
            if (textHex.StartsWith('#')) textHex = textHex.Substring(1);
            if (textHex.Length == 6 && System.Text.RegularExpressions.Regex.IsMatch(textHex, "^[0-9a-fA-F]{6}$"))
            {
                baseHex = textHex.ToUpperInvariant();
            }

            byte r = Convert.ToByte(baseHex.Substring(0, 2), 16);
            byte g = Convert.ToByte(baseHex.Substring(2, 2), 16);
            byte b = Convert.ToByte(baseHex.Substring(4, 2), 16);

            var (h, s, l) = RgbToHsl(r, g, b);

            var hexColors = new List<string>();
            switch (mode.ToLowerInvariant())
            {
                case "analogous":
                    hexColors.Add(HslToHex(h - 30, s, l));
                    hexColors.Add(HslToHex(h - 15, s, l));
                    hexColors.Add(baseHex);
                    hexColors.Add(HslToHex(h + 15, s, l));
                    hexColors.Add(HslToHex(h + 30, s, l));
                    break;
                case "triadic":
                    hexColors.Add(baseHex);
                    hexColors.Add(HslToHex(h + 120, s, l));
                    hexColors.Add(HslToHex(h + 240, s, l));
                    hexColors.Add(HslToHex(h + 60, s, Math.Min(1.0, l * 1.15)));
                    hexColors.Add(HslToHex(h + 180, s, Math.Max(0.2, l * 0.85)));
                    break;
                default: // mono
                    hexColors.Add(HslToHex(h, s, 0.25));
                    hexColors.Add(HslToHex(h, s, 0.40));
                    hexColors.Add(baseHex);
                    hexColors.Add(HslToHex(h, s, 0.70));
                    hexColors.Add(HslToHex(h, s, 0.85));
                    break;
            }

            foreach (var hex in hexColors)
            {
                var brush = BrushFromHex(hex);
                // A button (the colour drawn inside it, so hover keeps the colour) rather than a
                // tapped Border: the swatches were unreachable from the keyboard and unnamed for
                // screen readers.
                var swatch = new Button
                {
                    Width = 38,
                    Height = 22,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Content = new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = brush,
                        BorderThickness = new Thickness(1),
                    }.Themed(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush"),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, $"Apply harmony colour #{hex.ToUpperInvariant()}");
                ToolTipService.SetToolTip(swatch, $"Apply #{hex.ToUpperInvariant()} to the preview");
                swatch.Click += (s, args) =>
                {
                    _customHexOverride = hex.ToUpperInvariant();
                    if (CustomPaletteHexBox != null) CustomPaletteHexBox.Text = "#" + _customHexOverride;
                    if (CustomPaletteSwatch != null)
                    {
                        ThemeBrush.Clear(CustomPaletteSwatch, Border.BackgroundProperty);
                        CustomPaletteSwatch.Background = brush;
                    }
                    RefreshWebView();
                    SetCustomPaletteStatus($"Applied #{_customHexOverride}", "SystemFillColorSuccessBrush");
                    ViewModel.StatusMessage = $"Applied harmony color: #{_customHexOverride}";
                    if (PaletteButton is not null) MarkSmith.Services.CopyFeedback.ApplyButtonTick(PaletteButton, null);
                };
                HarmonySwatchesPanel.Children.Add(swatch);
            }
            // The panel lives in the palette flyout's popup, outside the window's tracked tree.
            HoverPolish.Apply(HarmonySwatchesPanel);
        }

        private static (double H, double S, double L) RgbToHsl(byte r, byte g, byte b)
        {
            double rd = r / 255.0;
            double gd = g / 255.0;
            double bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double h = 0, s = 0, l = (max + min) / 2.0;

            if (Math.Abs(max - min) > 0.0001)
            {
                double d = max - min;
                s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
                if (Math.Abs(max - rd) < 0.0001)
                    h = (gd - bd) / d + (gd < bd ? 6.0 : 0.0);
                else if (Math.Abs(max - gd) < 0.0001)
                    h = (bd - rd) / d + 2.0;
                else
                    h = (rd - gd) / d + 4.0;
                h *= 60.0;
            }
            return (h, s, l);
        }

        private static string HslToHex(double h, double s, double l)
        {
            h = (h % 360.0 + 360.0) % 360.0;
            s = Math.Clamp(s, 0.0, 1.0);
            l = Math.Clamp(l, 0.0, 1.0);

            double r, g, b;
            if (s < 0.0001)
            {
                r = g = b = l;
            }
            else
            {
                double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
                double p = 2.0 * l - q;
                r = HueToRgb(p, q, h / 360.0 + 1.0 / 3.0);
                g = HueToRgb(p, q, h / 360.0);
                b = HueToRgb(p, q, h / 360.0 - 1.0 / 3.0);
            }
            byte rb = (byte)Math.Clamp((int)Math.Round(r * 255.0), 0, 255);
            byte gb = (byte)Math.Clamp((int)Math.Round(g * 255.0), 0, 255);
            byte bb = (byte)Math.Clamp((int)Math.Round(b * 255.0), 0, 255);
            return $"{rb:X2}{gb:X2}{bb:X2}";
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1.0;
            if (t > 1) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        private void OnPaletteListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSmartArtPalette || PaletteList?.SelectedItem is not SmartArtPaletteChoice choice) return;
            ViewModel.SelectedPalette = choice.Name;
            RefreshWebView();
            ViewModel.StatusMessage = $"Applied SmartArt color palette: {choice.Name}";
            if (PaletteButton is not null) MarkSmith.Services.CopyFeedback.ApplyButtonTick(PaletteButton, null);
            PaletteFlyout?.Hide();
        }

        private void OnCustomPaletteHexBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            var hex = CustomPaletteHexBox?.Text?.Trim() ?? "";
            if (hex.StartsWith('#')) hex = hex.Substring(1);
            bool isValid = hex.Length == 6 && System.Text.RegularExpressions.Regex.IsMatch(hex, "^[0-9a-fA-F]{6}$");
            if (isValid && CustomPaletteSwatch != null)
            {
                ThemeBrush.Clear(CustomPaletteSwatch, Border.BackgroundProperty);
                CustomPaletteSwatch.Background = BrushFromHex(hex);
                // A harmony swatch fills the box after applying its colour, and this runs after
                // that: keep its "Applied" rather than asking for an Enter that would do nothing.
                SetCustomPaletteStatus(string.Equals(hex, _customHexOverride, StringComparison.OrdinalIgnoreCase)
                    ? $"Applied #{_customHexOverride}"
                    : "Valid hex color · Enter applies it", "SystemFillColorSuccessBrush");
            }
            else if (CustomPaletteStatusText != null)
            {
                if (string.IsNullOrEmpty(hex))
                {
                    CustomPaletteStatusText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SetCustomPaletteStatus("Enter 6 hex characters (#RRGGBB)", "SystemFillColorCautionBrush");
                }
            }
        }

        /// <summary>The custom accent's one-line verdict, in a status colour that follows the theme
        /// (an Application resource keeps the colour of the theme the app started in).</summary>
        private void SetCustomPaletteStatus(string text, string brushKey)
        {
            if (CustomPaletteStatusText == null) return;
            CustomPaletteStatusText.Text = text;
            ThemeBrush.Set(CustomPaletteStatusText, TextBlock.ForegroundProperty, brushKey);
            CustomPaletteStatusText.Visibility = Visibility.Visible;
        }

        private void OnCustomPaletteHexBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                OnApplyCustomPaletteClick(sender, new RoutedEventArgs());
            }
        }

        private void OnApplyCustomPaletteClick(object sender, RoutedEventArgs e)
        {
            var hex = CustomPaletteHexBox?.Text?.Trim() ?? "";
            if (hex.StartsWith('#')) hex = hex.Substring(1);
            if (hex.Length != 6 || !System.Text.RegularExpressions.Regex.IsMatch(hex, "^[0-9a-fA-F]{6}$"))
            {
                SetCustomPaletteStatus(hex.Length == 0
                    ? "Type a hex color first, e.g. #0078D4"
                    : "Invalid hex color: use 6 hex characters (#RRGGBB)", "SystemFillColorCriticalBrush");
                CustomPaletteHexBox?.Focus(FocusState.Programmatic);
                return;
            }

            _customHexOverride = hex.ToUpperInvariant();
            RefreshWebView();
            SetCustomPaletteStatus($"Applied #{_customHexOverride}", "SystemFillColorSuccessBrush");
            ViewModel.StatusMessage = $"Applied custom accent color: #{_customHexOverride}";
            if (PaletteButton is not null) MarkSmith.Services.CopyFeedback.ApplyButtonTick(PaletteButton, null);
            PaletteFlyout?.Hide();
        }

        private void OnResetCustomPaletteClick(object sender, RoutedEventArgs e)
        {
            _customHexOverride = null;
            if (CustomPaletteHexBox != null) CustomPaletteHexBox.Text = "";
            if (CustomPaletteSwatch != null) ThemeBrush.Set(CustomPaletteSwatch, Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            if (CustomPaletteStatusText != null) CustomPaletteStatusText.Visibility = Visibility.Collapsed;
            ViewModel.StatusMessage = "Custom accent cleared: the preview uses the palette's own colours";
            RefreshWebView();
            ViewModel.StatusMessage = $"Reset to default palette: {ViewModel.SelectedPalette}";
            if (PaletteButton is not null) MarkSmith.Services.CopyFeedback.ApplyButtonTick(PaletteButton, null);
            PaletteFlyout?.Hide();
        }

        private void OnCarouselPrevClick(object sender, RoutedEventArgs e)
        {
            var sv = FindDescendant<ScrollViewer>(QuickLayoutCarouselList);
            if (sv is null) return;
            sv.ChangeView(Math.Max(0, sv.HorizontalOffset - 210), null, null);
        }

        private void OnCarouselNextClick(object sender, RoutedEventArgs e)
        {
            var sv = FindDescendant<ScrollViewer>(QuickLayoutCarouselList);
            if (sv is null) return;
            sv.ChangeView(sv.HorizontalOffset + 210, null, null);
        }

        private void OnCarouselLayoutItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StudioLayoutItem layout)
            {
                ViewModel.SelectedLayout = layout;
                RefreshWebView();
                ViewModel.StatusMessage = $"Selected SmartArt layout: {layout.DisplayName}";
            }
        }

        private void CyclePalette()
        {
            var palettes = ViewModel.Palettes;
            if (palettes.Count == 0) return;
            int idx = palettes.IndexOf(ViewModel.SelectedPalette);
            int next = (idx + 1) % palettes.Count;
            ViewModel.SelectedPalette = palettes[next];
            RefreshWebView();
            ViewModel.StatusMessage = $"Color palette: {palettes[next]} (Double-click to cycle)";
            if (PaletteButton is not null) MarkSmith.Services.CopyFeedback.ApplyButtonTick(PaletteButton, null);
        }

        private void OnLayoutListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var layout = (e.OriginalSource as FrameworkElement)?.DataContext as StudioLayoutItem
                         ?? ViewModel.SelectedLayout;
            if (layout is not null)
            {
                e.Handled = true;
                ViewModel.SelectedLayout = layout;
                if (ViewModel.InsertIntoDocumentCommand.CanExecute(null))
                {
                    ViewModel.InsertIntoDocumentCommand.Execute(null);
                    ViewModel.StatusMessage = $"Inserted {layout.DisplayName} SmartArt into document";
                }
            }
        }

        private void OnEditorTabsDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (EditorTabs is null) return;
            e.Handled = true;
            if (EditorTabs.SelectedItem == OutlineTab)
            {
                EditorTabs.SelectedItem = MarkdownTab;
                ViewModel.StatusMessage = "Switched to Markdown editor (Ctrl+2)";
            }
            else
            {
                EditorTabs.SelectedItem = OutlineTab;
                ViewModel.StatusMessage = "Switched to Outline editor (Ctrl+1)";
            }
        }

        #region Preview Zoom Controls

        private double _previewZoomFactor = 1.0;

        private void SetPreviewZoom(double factor)
        {
            factor = Math.Clamp(factor, 0.25, 3.0);
            _previewZoomFactor = factor;
            if (PreviewZoomFactorText != null)
                PreviewZoomFactorText.Text = $"{(int)Math.Round(factor * 100)}%";
            if (PreviewWebView?.CoreWebView2 != null)
            {
                try
                {
                    var zoomStr = factor.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _ = PreviewWebView.CoreWebView2.ExecuteScriptAsync($"document.body.style.zoom = '{zoomStr}';");
                }
                catch { /* best effort */ }
            }
            ViewModel.StatusMessage = $"Diagram preview zoom: {(int)Math.Round(factor * 100)}%";
        }

        private void OnPreviewZoomInClick(object sender, RoutedEventArgs e) => SetPreviewZoom(_previewZoomFactor + 0.15);
        private void OnPreviewZoomOutClick(object sender, RoutedEventArgs e) => SetPreviewZoom(_previewZoomFactor - 0.15);
        private void OnPreviewZoomResetClick(object sender, RoutedEventArgs e) => SetPreviewZoom(1.0);

        private void OnPreviewZoomResetRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            PreviewZoomFlyout?.ShowAt(PreviewZoomResetBtn);
        }

        private void OnPreviewZoomPresetClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
            {
                SetPreviewZoom(z);
            }
        }

        private void OnPreviewZoomFitWidthClick(object sender, RoutedEventArgs e)
        {
            SetPreviewZoom(1.0);
            ViewModel.StatusMessage = "Fit diagram to preview width.";
        }

        #endregion

        #region Outline Hierarchy Stats & Quick Navigation

        private void UpdateOutlineStats()
        {
            if (OutlineStatsText == null) return;
            int count = ViewModel.OutlineRows.Count;
            int maxDepth = count > 0 ? ViewModel.OutlineRows.Max(r => r.Depth) : 0;
            string text = $"{CountText.Of(count, "node")} · Depth {maxDepth}";
            OutlineStatsText.Text = text;
            if (OutlineStatsPill != null)
            {
                string layoutName = ViewModel.SelectedLayout?.DisplayName ?? "SmartArt";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OutlineStatsPill, $"Outline stats: {text}");
                ToolTipService.SetToolTip(OutlineStatsPill, $"Outline hierarchy: {CountText.Of(count, "node")}, max depth {maxDepth} ({layoutName}) · Click for navigation and outline tools, Right-click or Shift+F10 too");
            }

            int totalWords = 0;
            int totalChars = 0;
            foreach (var row in ViewModel.OutlineRows)
            {
                var txt = row.Text ?? "";
                totalChars += txt.Length;
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    var words = txt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    totalWords += words.Length;
                }
            }
            if (OutlineMetricsText != null)
            {
                OutlineMetricsText.Text = $"{CountText.Of(totalWords, "word")} · {CountText.Of(totalChars, "char")}";
            }
            if (OutlineMetricsPill != null)
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OutlineMetricsPill, $"Outline text volume: {CountText.Of(totalWords, "word")}, {CountText.Of(totalChars, "character")}");
                ToolTipService.SetToolTip(OutlineMetricsPill, $"Outline metrics: {CountText.Of(totalWords, "word")}, {CountText.Of(totalChars, "character")} across {CountText.Of(count, "item")} · Click to copy the counts or the plain text");
            }
        }

        private void OnOutlineMetricsPillRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            OutlineMetricsFlyout?.ShowAt(OutlineMetricsPill, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, OutlineMetricsPill) });
        }

        private void OnCopyOutlineMetricsClick(object sender, RoutedEventArgs e)
        {
            int totalWords = 0;
            int totalChars = 0;
            foreach (var row in ViewModel.OutlineRows)
            {
                var txt = row.Text ?? "";
                totalChars += txt.Length;
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    var words = txt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    totalWords += words.Length;
                }
            }
            string summary = $"{CountText.Of(totalWords, "word")}, {CountText.Of(totalChars, "character")}, {CountText.Of(ViewModel.OutlineRows.Count, "node")}";
            CopyAndReport(summary, $"Copied outline metrics: {summary}");
        }

        private void OnCopyPlainOutlineTextClick(object sender, RoutedEventArgs e)
        {
            // An empty outline used to put an empty string on the clipboard and report "0 lines".
            if (ViewModel.OutlineRows.Count == 0)
            {
                ViewModel.StatusMessage = "The outline is empty: nothing to copy. Add a node, or type bullet points on the Markdown tab.";
                return;
            }
            var plainLines = ViewModel.OutlineRows.Select(r => new string(' ', r.Depth * 2) + (r.Text ?? "")).ToList();
            string plainText = string.Join(Environment.NewLine, plainLines);
            CopyAndReport(plainText, $"Copied plain text outline ({CountText.Of(plainLines.Count, "line")}).");
        }

        private void OnOutlineStatsPillRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            OutlineStatsFlyout?.ShowAt(OutlineStatsPill, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, OutlineStatsPill) });
        }

        private void OnSelectRootNodeClick(object sender, RoutedEventArgs e)
        {
            var root = ViewModel.OutlineRows.FirstOrDefault(r => r.Depth == 0) ?? ViewModel.OutlineRows.FirstOrDefault();
            if (root != null)
            {
                ViewModel.Select(root);
                ViewModel.StatusMessage = $"Selected root node: {root.Text}";
            }
        }

        private void OnSelectDeepestNodeClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.OutlineRows.Count > 0)
            {
                int maxDepth = ViewModel.OutlineRows.Max(r => r.Depth);
                var deepest = ViewModel.OutlineRows.FirstOrDefault(r => r.Depth == maxDepth);
                if (deepest != null)
                {
                    ViewModel.Select(deepest);
                    ViewModel.StatusMessage = $"Selected deepest node (depth {deepest.Depth}): {deepest.Text}";
                }
            }
        }

        private void OnCopyOutlineSummaryClick(object sender, RoutedEventArgs e) => CopyOutlineSummary();

        private void CopyOutlineSummary()
        {
            int count = ViewModel.OutlineRows.Count;
            int maxDepth = count > 0 ? ViewModel.OutlineRows.Max(r => r.Depth) : 0;
            string layoutName = ViewModel.SelectedLayout?.DisplayName ?? "SmartArt";
            string summary = $"{CountText.Of(count, "node")} (max depth {maxDepth}) · {layoutName}";
            CopyAndReport(summary, $"Copied outline summary: {summary}");
        }

        private void OnPromoteAllClick(object sender, RoutedEventArgs e)
        {
            if (OutlineForCommand() is not string markdown) return;
            var lines = markdown.Split('\n');
            var sb = new System.Text.StringBuilder();
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                bool hasCr = line.EndsWith('\r');
                string trimmed = hasCr ? line.Substring(0, line.Length - 1) : line;

                if (trimmed.StartsWith("  "))
                {
                    trimmed = trimmed.Substring(2);
                    changed = true;
                }
                else if (trimmed.StartsWith("\t"))
                {
                    trimmed = trimmed.Substring(1);
                    changed = true;
                }
                else if (trimmed.StartsWith(" "))
                {
                    trimmed = trimmed.Substring(1);
                    changed = true;
                }

                sb.Append(trimmed);
                if (hasCr) sb.Append('\r');
                if (i < lines.Length - 1) sb.Append('\n');
            }

            if (changed)
            {
                ViewModel.ReplaceOutline(sb.ToString());
                UpdateOutlineStats();
                ViewModel.StatusMessage = "Promoted all outline items out 1 level · Ctrl+Z to undo";
            }
            else
            {
                // Nothing indented: this used to do nothing at all.
                ViewModel.StatusMessage = "Every outline item is already at the root level.";
            }
        }

        private void OnDemoteAllClick(object sender, RoutedEventArgs e)
        {
            if (OutlineForCommand() is not string markdown) return;
            var lines = markdown.Split('\n');
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                bool hasCr = line.EndsWith('\r');
                string trimmed = hasCr ? line.Substring(0, line.Length - 1) : line;
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    sb.Append("  ");
                }
                sb.Append(trimmed);
                if (hasCr) sb.Append('\r');
                if (i < lines.Length - 1) sb.Append('\n');
            }
            ViewModel.ReplaceOutline(sb.ToString());
            UpdateOutlineStats();
            ViewModel.StatusMessage = "Demoted all outline items in 1 level · Ctrl+Z to undo";
        }

        private void OnFlattenAllClick(object sender, RoutedEventArgs e)
        {
            if (OutlineForCommand() is not string markdown) return;
            var lines = markdown.Split('\n');
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                bool hasCr = line.EndsWith('\r');
                string trimmed = hasCr ? line.Substring(0, line.Length - 1) : line;
                string noIndent = trimmed.TrimStart();
                if (!noIndent.StartsWith("- ") && !noIndent.StartsWith("* ") && !string.IsNullOrWhiteSpace(noIndent))
                {
                    noIndent = "- " + noIndent;
                }
                sb.Append(noIndent);
                if (hasCr) sb.Append('\r');
                if (i < lines.Length - 1) sb.Append('\n');
            }
            if (!ViewModel.ReplaceOutline(sb.ToString()))
            {
                ViewModel.StatusMessage = "Every outline item is already at the root level.";
                return;
            }
            UpdateOutlineStats();
            ViewModel.StatusMessage = "Flattened all outline items to root level · Ctrl+Z to undo";
        }

        private void OnReverseOutlineClick(object sender, RoutedEventArgs e)
        {
            if (OutlineForCommand() is not string markdown) return;
            // An unchanged result (one item) still claimed "Reversed"; ReplaceOutline adds no undo step then.
            if (!ViewModel.ReplaceOutline(SmartArtDesignStudioViewModel.ReverseOutline(markdown)))
            {
                ViewModel.StatusMessage = "Reversing doesn't change this outline: it has only one line.";
                return;
            }
            UpdateOutlineStats();
            ViewModel.StatusMessage = "Reversed outline items order · Ctrl+Z to undo";
        }

        private void OnSortOutlineAscendingClick(object sender, RoutedEventArgs e)
        {
            if (OutlineForCommand() is not string markdown) return;
            if (!ViewModel.ReplaceOutline(SmartArtDesignStudioViewModel.SortOutlineAlphabetically(markdown, ascending: true)))
            {
                ViewModel.StatusMessage = "The outline is already in A to Z order.";
                return;
            }
            UpdateOutlineStats();
            ViewModel.StatusMessage = "Sorted outline items alphabetically (A to Z) · Ctrl+Z to undo";
        }

        private void OnSortOutlineDescendingClick(object sender, RoutedEventArgs e)
        {
            if (OutlineForCommand() is not string markdown) return;
            if (!ViewModel.ReplaceOutline(SmartArtDesignStudioViewModel.SortOutlineAlphabetically(markdown, ascending: false)))
            {
                ViewModel.StatusMessage = "The outline is already in Z to A order.";
                return;
            }
            UpdateOutlineStats();
            ViewModel.StatusMessage = "Sorted outline items reverse alphabetically (Z to A) · Ctrl+Z to undo";
        }

        private void OnTrimTrailingWhitespaceClick(object sender, RoutedEventArgs e)
        {
            if (OutlineForCommand() is not string markdown) return;
            var trimmed = SmartArtDesignStudioViewModel.TrimTrailingWhitespace(markdown);
            if (trimmed != markdown)
            {
                ViewModel.ReplaceOutline(trimmed);
                UpdateOutlineStats();
                ViewModel.StatusMessage = "Trimmed trailing whitespace from outline lines.";
            }
            else
            {
                ViewModel.StatusMessage = "No trailing whitespace found in outline.";
            }
        }

        private void OnStandardiseBulletsClick(object sender, RoutedEventArgs e)
        {
            if (OutlineForCommand() is not string markdown) return;
            var standardised = SmartArtDesignStudioViewModel.StandardiseBullets(markdown, '-');
            if (standardised != markdown)
            {
                ViewModel.ReplaceOutline(standardised);
                UpdateOutlineStats();
                ViewModel.StatusMessage = "Standardised outline bullet markers to '-'.";
            }
            else
            {
                ViewModel.StatusMessage = "All bullet points are already standardised.";
            }
        }

        private void OnNormalizeIndentationClick(object sender, RoutedEventArgs e)
        {
            if (OutlineForCommand() is not string markdown) return;
            var normalized = SmartArtDesignStudioViewModel.NormalizeIndentation(markdown, 2);
            if (normalized != markdown)
            {
                ViewModel.ReplaceOutline(normalized);
                UpdateOutlineStats();
                ViewModel.StatusMessage = "Normalized outline indentation to 2 spaces per level.";
            }
            else
            {
                ViewModel.StatusMessage = "Outline indentation is already uniform (2 spaces per level).";
            }
        }

        private void OnCopyHierarchyMetricsClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var (_, _, _, _, summary) = SmartArtDesignStudioViewModel.AnalyzeHierarchyMetrics(markdown);
            CopyAndReport(summary, $"✓ {summary}");
        }

        private void OnCopyOutlineRootsOnlyClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var rootsOnly = SmartArtDesignStudioViewModel.FormatOutlineAtMaxDepth(markdown, 1);
            if (!string.IsNullOrWhiteSpace(rootsOnly))
            {
                CopyAndReport(rootsOnly, "✓ Copied top-level outline roots to clipboard.");
            }
            else
            {
                ViewModel.StatusMessage = "No outline roots found to copy.";
            }
        }

        private void OnAuditOutlineLineLengthsClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var summary = SmartArtDesignStudioViewModel.FormatOutlineLineLengthAuditSummary(markdown, 40);
            CopyAndReport(summary, $"✓ Copied line length audit: {summary}");
        }

        private void OnCopyOutlineHierarchyBreakdownClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var summary = SmartArtDesignStudioViewModel.FormatOutlineHierarchyBreakdown(markdown);
            CopyAndReport(summary, $"✓ Copied hierarchy breakdown: {summary}");
        }

        private void OnAuditOutlineBranchingClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var summary = SmartArtDesignStudioViewModel.FormatOutlineBranchingSummary(markdown);
            CopyAndReport(summary, $"✓ Copied branching audit: {summary}");
        }

        private void OnAuditOutlineTreeBalanceClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var summary = SmartArtDesignStudioViewModel.FormatOutlineBalanceSummary(markdown);
            CopyAndReport(summary, $"✓ Copied balance audit: {summary}");
        }

        private void OnAuditOutlineDepthDistributionClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var summary = SmartArtDesignStudioViewModel.FormatOutlineDepthDistributionSummary(markdown);
            CopyAndReport(summary, $"✓ Copied depth audit: {summary}");
        }

        private void OnReverseHierarchyOutlineClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var (reversed, blockCount, lineCount) = SmartArtDesignStudioViewModel.ReverseOutlineHierarchically(markdown);
            if (blockCount <= 1)
            {
                ViewModel.StatusMessage = "At least 2 top-level outline blocks required to reverse.";
                return;
            }
            ViewModel.ReplaceOutline(reversed);
            var summary = SmartArtDesignStudioViewModel.FormatHierarchyReverseSummary(blockCount, lineCount);
            ReportEdit(summary);
        }

        private void OnDeduplicateOutlineClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var (deduped, removedCount, survivingCount) = SmartArtDesignStudioViewModel.DeduplicateOutlineNodes(markdown);
            var summary = SmartArtDesignStudioViewModel.FormatOutlineDeduplicationSummary(removedCount, survivingCount);
            if (removedCount > 0)
            {
                ViewModel.ReplaceOutline(deduped);
            }
            ReportEdit(summary);
        }

        private void OnSplitLongOutlineClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var (splitMd, splitCount, resultingCount) = SmartArtDesignStudioViewModel.SplitLongOutlineItems(markdown, 45);
            var summary = SmartArtDesignStudioViewModel.FormatLongItemSplitSummary(splitCount, resultingCount, 45);
            if (splitCount > 0)
            {
                ViewModel.ReplaceOutline(splitMd);
            }
            ReportEdit(summary);
        }

        private void OnAutoNumberOutlineClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var (numberedMd, numberedNodes, maxDepth) = SmartArtDesignStudioViewModel.ApplyHierarchicalNumbering(markdown, stripExistingOnly: false);
            bool stripped = false;
            if (numberedNodes > 0 && string.Equals(numberedMd, markdown, StringComparison.Ordinal))
            {
                // Toggle off if already hierarchically numbered
                (numberedMd, numberedNodes, maxDepth) = SmartArtDesignStudioViewModel.ApplyHierarchicalNumbering(markdown, stripExistingOnly: true);
                stripped = true;
            }
            if (numberedNodes > 0 && !string.Equals(numberedMd, markdown, StringComparison.Ordinal))
            {
                ViewModel.ReplaceOutline(numberedMd);
            }
            var summary = SmartArtDesignStudioViewModel.FormatOutlineNumberingSummary(numberedNodes, maxDepth, stripped);
            ReportEdit(summary);
        }

        private void OnRecommendLayoutsOutlineClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var summary = SmartArtDesignStudioViewModel.FormatLayoutRecommendationSummary(markdown);
            CopyAndReport(summary, $"✓ {summary}");
        }

        private void OnStructureByDelimiterOutlineClick(object sender, RoutedEventArgs e)
        {
            string markdown = ViewModel.OutlineText;
            var (structured, parents, items, summary) = SmartArtDesignStudioViewModel.StructureFlatOutlineByDelimiter(markdown);
            if (items > 0)
            {
                ViewModel.ReplaceOutline(structured);
            }
            ReportEdit(summary);
        }

        private void OnBalanceOutlineDepthClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (_, adjusted, summary) = ViewModel.BalanceOutlineNestingDepth(maxDepth: 2, mergeIntoParent: false);
            ReportEdit(summary);
        }

        private void OnAuditTokenDensityClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditOutlineTokenDensity();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditAspectRatioFitClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditLayoutAspectRatio();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditCharBudgetsClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditNodeCharacterBudgets();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnRecommendIconsOutlineClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.RecommendIconsForOutline();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditBranchingFactorClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditHierarchyBranchingFactor();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditContainerDensityClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditContainerDensityFit();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditSiblingVarianceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditSiblingTextLengthVariance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditLinearFlowMonotonicityClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditLinearFlowMonotonicity();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditCyclicFlowContinuityClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditCyclicFlowContinuity();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditMatrixQuadrantBalanceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditMatrixQuadrantBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnNormalizeMatrixQuadrantsClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.NormalizeMatrixQuadrantHeaders();
            ReportEdit(result.Summary);
        }

        private void OnAuditPyramidProportionalityClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditPyramidStageProportionality();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditVennCompletenessClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditVennDiagramCompleteness();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditTimelineChronologyClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditTimelineMilestoneChronology();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditCyclicConvergenceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditCyclicProcessConvergence();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditHierarchySpanClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditHierarchySpanOfControl();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditTypographyFontFitClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditLayoutTypographyAndFontFit();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditColorHierarchyClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditColorHierarchyAndGradients();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditLinearFlowRhythmClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditLinearFlowChevronRhythm();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditRadialCycleBalanceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditRadialCycleOrbitalBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditPyramidTaperingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditPyramidTaperingAndMassBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditMatrixQuadrantSymmetryClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditMatrixQuadrantSymmetry();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditVennOverlapClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditVennDiagramOverlapAndSetBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditProcessSequencePacingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditProcessSequencePacing();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnAuditHierarchyBranchingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditHierarchyAspectAndBranching();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditTimelineChronologyClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditTimelineMilestoneChronologyAndSpacing();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditCycleIntervalClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditCycleIntervalAndRadialChords();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditQuadrantGridBalanceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditQuadrantGridBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditFunnelPyramidProgressionClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditFunnelPyramidProgression();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditVennRadialBalanceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditVennRadialIntersectionBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditListTextDensityBalanceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditListTextDensityBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditProcessStepFlowClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditProcessStepFlowAndTransitions();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditHierarchyBalanceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditHierarchyIndentationAndChildBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditCycleSequenceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditCycleSequenceAndClosure();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditMatrixQuadrantClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditMatrixQuadrantBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditFunnelPyramidClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditFunnelPyramidVolumeProgression();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditTimelineIntervalsClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditTimelineChronologyAndIntervals();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditRadialSymmetryClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditRadialHubSpokeSymmetry();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditChevronProcessFlowClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditChevronProcessFlowAndSpacing();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditFunnelPyramidProportionsClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditFunnelPyramidProportions();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditMatrixCellSymmetryClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditMatrixCellSymmetry();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditHierarchyOrgChartBalanceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditHierarchyOrgChartBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditAlternatingFlowClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditAlternatingFlowAndRhythm();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditRadialGearCycleClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditRadialGearCycleEngagement();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditFunnelPyramidTaperClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditFunnelPyramidVolumeTapering();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditMatrixQuadrantBalanceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditMatrixQuadrantSymmetryAndBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditVennOverlapSymmetryClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditVennDiagramOverlapSymmetry();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditHierarchyBranchingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditHierarchyTreeDepthAndBranchingBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnOutlineStatsAuditCycleRadialAngularClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditCycleRadialAngularEquidistance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }


    private void OnOutlineStatsAuditChevronUniformityClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditProcessFlowChevronUniformity();
        CopyAndReport(result.Summary, $"✓ {result.Summary}");
    }

    private void OnOutlineStatsAuditTargetBullseyeProgressionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditTargetBullseyeRadiusProgression();
        CopyAndReport(result.Summary, $"✓ {result.Summary}");
    }



    private void OnOutlineStatsAuditPyramidProportionalityClick(object sender, RoutedEventArgs e)

    {

        if (ViewModel == null) return;

        var result = ViewModel.AuditPyramidLayerProportionality();

        CopyAndReport(result.Summary, $"✓ {result.Summary}");

    }


        #endregion


        #region Outline Real-Time Search & Match Navigation

        private readonly List<StudioNodeViewModel> _outlineSearchMatches = new();
        private int _currentOutlineMatchIndex = -1;

        private void OnOutlineStatsAuditFunnelStageProportionalityClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditFunnelStageProportionality();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }
    private void OnOutlineStatsAuditPyramidHierarchyGeometryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditPyramidHierarchyGeometry();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditChevronProcessStrideGeometryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditChevronProcessStrideGeometry();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditTargetBullseyeRingGeometryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditTargetBullseyeRingGeometry();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditMatrixCentroidBalanceClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditMatrixAxisLabelAndCentroidBalance();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditCycleProcessDirectionalityClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditCycleProcessDirectionality();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditMatrixQuadrantCoordinateParityClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditMatrixQuadrantCoordinateParity();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditVennDiagramSetIntersectionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditVennDiagramSetIntersection();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditChevronProcessMilestoneProgressionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditChevronProcessMilestoneProgression();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditHierarchyTreeFanOutRatioClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditHierarchyTreeFanOutRatio();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditSegmentedCycleAngleSlicingClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditSegmentedCycleAngleSlicing();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditMatrixQuadrantDistributionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditMatrixQuadrantDistribution();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditPyramidFunnelHierarchyInversionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditPyramidFunnelHierarchyInversion();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }



    private void OnOutlineStatsAuditPyramidApexProportionalityClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditPyramidApexProportionality();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditGearMechanismGeometryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditGearMechanismGeometry();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }


    private void OnOutlineStatsAuditChevronGeometryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditChevronProcessGeometry();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditMatrixQuadrantGeometryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditMatrixQuadrantGeometry();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineStatsAuditVennGeometryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditVennDiagramGeometry();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnOutlineSearchBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            PerformOutlineSearch(OutlineSearchBox?.Text ?? "");
        }

        private void OnOutlineSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                if (shift) StepOutlineMatch(-1);
                else StepOutlineMatch(1);
            }
            else if (e.Key == Windows.System.VirtualKey.F3)
            {
                e.Handled = true;
                bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                if (shift) StepOutlineMatch(-1);
                else StepOutlineMatch(1);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                if (OutlineSearchBox != null) OutlineSearchBox.Text = "";
                PerformOutlineSearch("");
            }
        }

        private void OnOutlinePrevMatchClick(object sender, RoutedEventArgs e) => StepOutlineMatch(-1);
        private void OnOutlineNextMatchClick(object sender, RoutedEventArgs e) => StepOutlineMatch(1);

        private void PerformOutlineSearch(string query)
        {
            _outlineSearchMatches.Clear();
            _currentOutlineMatchIndex = -1;
            query = query.Trim();

            if (string.IsNullOrEmpty(query))
            {
                SetOutlineMatchCount("", "");
                return;
            }

            foreach (var node in ViewModel.OutlineRows)
            {
                if (node.Text != null && node.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    _outlineSearchMatches.Add(node);
                }
            }

            if (_outlineSearchMatches.Count > 0)
            {
                _currentOutlineMatchIndex = 0;
                SelectAndFocusOutlineMatch(_outlineSearchMatches[0]);
                SetOutlineMatchCount($"1/{_outlineSearchMatches.Count}", $"Match 1 of {_outlineSearchMatches.Count}");
            }
            else
            {
                SetOutlineMatchCount("No match", $"No node matches {query}");
                // The status line kept the previous search's "Selected matching node" line.
                ViewModel.StatusMessage = $"No node matches “{query}”.";
            }
        }

        private void StepOutlineMatch(int direction)
        {
            if (_outlineSearchMatches.Count == 0) return;
            _currentOutlineMatchIndex = (_currentOutlineMatchIndex + direction + _outlineSearchMatches.Count) % _outlineSearchMatches.Count;
            var match = _outlineSearchMatches[_currentOutlineMatchIndex];
            SelectAndFocusOutlineMatch(match);
            SetOutlineMatchCount($"{_currentOutlineMatchIndex + 1}/{_outlineSearchMatches.Count}",
                $"Match {_currentOutlineMatchIndex + 1} of {_outlineSearchMatches.Count}");
        }

        // The compact "2/5" count, with a spoken form for the polite live region so a screen reader
        // hears each step ("Match 2 of 5"); the outline search used to be silent.
        private void SetOutlineMatchCount(string text, string spoken)
        {
            if (OutlineMatchCountLabel == null) return;
            OutlineMatchCountLabel.Text = text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OutlineMatchCountLabel, spoken);
            if (spoken.Length > 0)
                LiveAnnounce.Raise(OutlineMatchCountLabel);
        }

        private void SelectAndFocusOutlineMatch(StudioNodeViewModel node)
        {
            ViewModel.Select(node);
            ViewModel.StatusMessage = $"Selected matching node: “{node.Text}” (level {node.Depth})";
        }

        #endregion

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
