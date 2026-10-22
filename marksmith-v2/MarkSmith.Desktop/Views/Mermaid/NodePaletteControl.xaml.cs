using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MarkSmith.Services;

namespace MarkSmith.Views.Mermaid;

public sealed partial class NodePaletteControl : UserControl
{
    private string _selectedCategory = "All";

    /// <summary>A palette row was clicked: the host adds that shape in view (dragging places it).</summary>
    public event EventHandler<ViewModels.Mermaid.MermaidPaletteItem>? ShapeRequested;

    /// <summary>Raised when the user presses Esc in an already-empty search box to return focus to the canvas.</summary>
    public event EventHandler? ReturnFocusToCanvasRequested;

    public void FocusSearchBox()
    {
        SearchTextBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        SearchTextBox.SelectAll();
    }

    // Row hover: the card's own border lights up in the studio cyan (the ListViewItem's hover fill
    // sits behind the card, so on its own a row gave no sign it was clickable).
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush RowRestBorder = new(Windows.UI.Color.FromArgb(255, 0x3D, 0x40, 0x5B));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush RowHoverBorder = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xC9, 0xF0));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush RowRestFill = new(Windows.UI.Color.FromArgb(255, 0x2B, 0x2D, 0x42));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush RowHoverFill = new(Windows.UI.Color.FromArgb(255, 0x33, 0x36, 0x50));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush PlusRestBrush = new(Windows.UI.Color.FromArgb(255, 0x8D, 0x99, 0xAE));

    private void OnRowPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border row)
        {
            row.BorderBrush = RowHoverBorder;
            row.Background = RowHoverFill;
            row.ScaleTransition ??= new Microsoft.UI.Xaml.Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
            row.CenterPoint = new System.Numerics.Vector3((float)row.ActualWidth / 2f, (float)row.ActualHeight / 2f, 0);
            row.Scale = new System.Numerics.Vector3(1.02f, 1.02f, 1f);
            if (row.Child is Grid g && g.Children.OfType<FontIcon>().FirstOrDefault() is { } plus)
                plus.Foreground = RowHoverBorder;
        }
    }

    private void OnRowPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border row)
        {
            row.ScaleTransition ??= new Microsoft.UI.Xaml.Vector3Transition { Duration = TimeSpan.FromMilliseconds(90) };
            row.CenterPoint = new System.Numerics.Vector3((float)row.ActualWidth / 2f, (float)row.ActualHeight / 2f, 0);
            row.Scale = new System.Numerics.Vector3(0.98f, 0.98f, 1f);
        }
    }

    private void OnRowPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border row)
            row.Scale = new System.Numerics.Vector3(1.02f, 1.02f, 1f);
    }

    private void OnRowPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border row)
        {
            row.BorderBrush = RowRestBorder;
            row.Background = RowRestFill;
            row.Scale = System.Numerics.Vector3.One;
            if (row.Child is Grid g && g.Children.OfType<FontIcon>().FirstOrDefault() is { } plus)
                plus.Foreground = PlusRestBrush;
        }
    }

    private void OnRowRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.FrameworkElement { DataContext: ViewModels.Mermaid.MermaidPaletteItem item } fe)
        {
            ShowPaletteItemContextMenu(fe, item, KeyboardContextMenu.PointIn(e, fe));
            e.Handled = true;
        }
    }

    private void ShowPaletteItemContextMenu(Microsoft.UI.Xaml.FrameworkElement target, ViewModels.Mermaid.MermaidPaletteItem item, Windows.Foundation.Point? position = null)
    {
        var flyout = new MenuFlyout();

        var addItem = new MenuFlyoutItem
        {
            Text = $"Add {item.DisplayName}",
            Icon = new FontIcon { Glyph = "\uE710" },
            KeyboardAcceleratorTextOverride = "Enter"
        };
        addItem.Click += (_, _) => ShapeRequested?.Invoke(this, item);
        flyout.Items.Add(addItem);

        if (DataContext is ViewModels.Mermaid.MermaidStudioViewModel { SelectedNode: { CanQuickAdd: true } selected } vm)
        {
            var connectItem = new MenuFlyoutItem
            {
                Text = $"Connect to '{ViewModels.Mermaid.MermaidStudioViewModel.DisplayName(selected)}'",
                Icon = new FontIcon { Glyph = "\uE8AB" }
            };
            connectItem.Click += (_, _) =>
            {
                var added = vm.QuickAddNode(selected, "Right");
                added.Shape = item.ShapeType;
                added.Category = item.Category;
                added.LabelText = item.DefaultText;
                if (added.IsPseudoState) added.Width = added.Height = 28;
                vm.UpdateConnectedConnectors(added);
            };
            flyout.Items.Add(connectItem);
        }

        if (position is { } pt)
            flyout.ShowAt(target, new FlyoutShowOptions { Position = pt });
        else
            flyout.ShowAt(target);
    }

    private void OnPaletteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ViewModels.Mermaid.MermaidPaletteItem item) ShapeRequested?.Invoke(this, item);
    }

    public NodePaletteControl()
    {
        InitializeComponent();
        // "Right-click for options" menus: open them from Shift+F10 / the Menu key too.
        KeyboardContextMenu.BridgeAll(
            (CatAll, OnCategoryPillRightTapped),
            (CatFlowchart, OnCategoryPillRightTapped),
            (CatSequence, OnCategoryPillRightTapped),
            (CatClass, OnCategoryPillRightTapped),
            (CatState, OnCategoryPillRightTapped),
            (CatGantt, OnCategoryPillRightTapped),
            (CatER, OnCategoryPillRightTapped),
            (CatMindmap, OnCategoryPillRightTapped));
        HoverPolish.Track(this);
        DataContextChanged += (_, _) => WatchDiagramType();
    }

    private ViewModels.Mermaid.MermaidStudioViewModel? _watchedVm;

    // The palette follows the diagram: opening a sequence diagram shows the sequence shapes (a
    // flowchart box dropped into it has nowhere sensible to go). Any pill can still be picked.
    private void WatchDiagramType()
    {
        if (_watchedVm != null) _watchedVm.PropertyChanged -= OnVmPropertyChanged;
        _watchedVm = DataContext as ViewModels.Mermaid.MermaidStudioViewModel;
        if (_watchedVm == null) return;
        _watchedVm.PropertyChanged += OnVmPropertyChanged;
        SelectCategoryFor(_watchedVm.SelectedDiagramType);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.Mermaid.MermaidStudioViewModel.SelectedDiagramType) && _watchedVm != null)
            SelectCategoryFor(_watchedVm.SelectedDiagramType);
    }

    private void SelectCategoryFor(MarkSmith.Mermaid.Ast.MermaidDiagramType type)
    {
        var pill = type switch
        {
            MarkSmith.Mermaid.Ast.MermaidDiagramType.Sequence => CatSequence,
            MarkSmith.Mermaid.Ast.MermaidDiagramType.Class => CatClass,
            MarkSmith.Mermaid.Ast.MermaidDiagramType.State => CatState,
            MarkSmith.Mermaid.Ast.MermaidDiagramType.Gantt => CatGantt,
            MarkSmith.Mermaid.Ast.MermaidDiagramType.Er => CatER,
            MarkSmith.Mermaid.Ast.MermaidDiagramType.Mindmap => CatMindmap,
            _ => CatFlowchart,
        };
        SelectPill(pill);
    }

    private void OnClearSearchClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        SelectPill(CatAll);
        SearchTextBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void OnSearchTextBoxDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        SearchTextBox.SelectAll();
        SearchTextBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void OnSearchTextBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (!string.IsNullOrEmpty(SearchTextBox.Text))
            {
                SearchTextBox.Text = string.Empty;
            }
            else
            {
                ReturnFocusToCanvasRequested?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Down)
        {
            if (PaletteListView.Items.Count > 0)
            {
                PaletteListView.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
                e.Handled = true;
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (PaletteListView.Items.Count == 1 && PaletteListView.Items[0] is ViewModels.Mermaid.MermaidPaletteItem single)
            {
                ShapeRequested?.Invoke(this, single);
                e.Handled = true;
            }
            else if (PaletteListView.Items.Count > 0)
            {
                PaletteListView.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
                e.Handled = true;
            }
        }
    }

    private void OnPaletteListViewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            SearchTextBox.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
            e.Handled = true;
            return;
        }

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == Windows.System.VirtualKey.Application || (e.Key == Windows.System.VirtualKey.F10 && shift))
        {
            if (e.OriginalSource is Microsoft.UI.Xaml.FrameworkElement { DataContext: ViewModels.Mermaid.MermaidPaletteItem ctxItem } fe)
            {
                ShowPaletteItemContextMenu(fe, ctxItem);
                e.Handled = true;
            }
            return;
        }

        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            if (e.OriginalSource is Microsoft.UI.Xaml.FrameworkElement { DataContext: ViewModels.Mermaid.MermaidPaletteItem item })
            {
                ShapeRequested?.Invoke(this, item);
                e.Handled = true;
            }
        }
    }

    private void OnCategoryPillClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is ToggleButton btn) SelectPill(btn);
    }

    private void OnCategoryPillRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is ToggleButton btn && btn.Content is string cat)
        {
            var menu = new MenuFlyout();

            var filterItem = new MenuFlyoutItem
            {
                Text = cat == "All" ? "Show all shapes" : $"Filter to {cat} shapes",
                Icon = new FontIcon { Glyph = "\uE71C" }
            };
            filterItem.Click += (_, _) => SelectPill(btn);
            menu.Items.Add(filterItem);

            if (cat != "All")
            {
                var showAllItem = new MenuFlyoutItem
                {
                    Text = "Show all shapes",
                    Icon = new FontIcon { Glyph = "\uE8A5" }
                };
                showAllItem.Click += (_, _) => SelectPill(CatAll);
                menu.Items.Add(showAllItem);
            }

            if (!string.IsNullOrEmpty(SearchTextBox.Text))
            {
                var clearSearchItem = new MenuFlyoutItem
                {
                    Text = "Clear search query (Esc)",
                    Icon = new FontIcon { Glyph = "\uE894" }
                };
                clearSearchItem.Click += (_, _) => OnClearSearchClick(this, new Microsoft.UI.Xaml.RoutedEventArgs());
                menu.Items.Add(clearSearchItem);
            }

            var focusSearchItem = new MenuFlyoutItem
            {
                Text = "Focus search box",
                KeyboardAcceleratorTextOverride = "Ctrl+F",
                Icon = new FontIcon { Glyph = "\uE721" }
            };
            focusSearchItem.Click += (_, _) => FocusSearchBox();
            menu.Items.Add(focusSearchItem);

            menu.ShowAt(btn, KeyboardContextMenu.PointIn(e, btn));
        }
    }

    private void OnSearchTextBoxRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        if (!string.IsNullOrEmpty(SearchTextBox.Text))
        {
            var clearItem = new MenuFlyoutItem
            {
                Text = "Clear search query (Esc)",
                Icon = new FontIcon { Glyph = "\uE894" }
            };
            clearItem.Click += (_, _) =>
            {
                SearchTextBox.Text = string.Empty;
                SearchTextBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            };
            menu.Items.Add(clearItem);
        }

        var showAllItem = new MenuFlyoutItem
        {
            Text = "Show all shapes",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        showAllItem.Click += (_, _) =>
        {
            SearchTextBox.Text = string.Empty;
            SelectPill(CatAll);
        };
        menu.Items.Add(showAllItem);

        var copySearchItem = new MenuFlyoutItem
        {
            Text = "Select all search text",
            KeyboardAcceleratorTextOverride = "Ctrl+A",
            Icon = new FontIcon { Glyph = "\uE8D2" }
        };
        copySearchItem.Click += (_, _) => SearchTextBox.SelectAll();
        menu.Items.Add(copySearchItem);

        menu.ShowAt(SearchTextBox, KeyboardContextMenu.PointIn(e, SearchTextBox));
    }

    private void SelectPill(ToggleButton btn)
    {
        if (btn.Content is string cat)
        {
            _selectedCategory = cat;
            CatAll.IsChecked = btn == CatAll;
            CatFlowchart.IsChecked = btn == CatFlowchart;
            CatSequence.IsChecked = btn == CatSequence;
            CatClass.IsChecked = btn == CatClass;
            CatState.IsChecked = btn == CatState;
            CatGantt.IsChecked = btn == CatGantt;
            CatER.IsChecked = btn == CatER;
            CatMindmap.IsChecked = btn == CatMindmap;

            FilterItems();
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        FilterItems();
    }

    private void FilterItems()
    {
        if (DataContext is ViewModels.Mermaid.MermaidStudioViewModel vm)
        {
            string selectedCat = _selectedCategory;
            string searchText = SearchTextBox.Text?.Trim().ToLowerInvariant() ?? string.Empty;

            var filtered = vm.PaletteItems.Where(item =>
            {
                bool matchesCat = selectedCat == "All" || item.Category.Equals(selectedCat, StringComparison.OrdinalIgnoreCase);
                bool matchesSearch = string.IsNullOrEmpty(searchText) ||
                                      item.DisplayName.ToLowerInvariant().Contains(searchText) ||
                                      item.Category.ToLowerInvariant().Contains(searchText);
                return matchesCat && matchesSearch;
            }).ToList();

            PaletteListView.ItemsSource = filtered;

            NoMatchesPanel.Visibility = filtered.Count == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            if (filtered.Count == 0)
            {
                string rawQuery = SearchTextBox.Text?.Trim() ?? string.Empty;
                NoMatchesText.Text = string.IsNullOrEmpty(searchText)
                    ? $"No {selectedCat} shapes."
                    : selectedCat == "All" ? $"No shapes match “{rawQuery}”." : $"No {selectedCat} shapes match “{rawQuery}”.";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NoMatchesText, NoMatchesText.Text);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(NoMatchesText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
                Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(NoMatchesText)
                    ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
            }
        }
    }

    private void OnPaletteDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is ViewModels.Mermaid.MermaidPaletteItem item)
        {
            e.Data.SetData("MermaidCategory", item.Category);
            e.Data.SetData("MermaidShapeType", item.ShapeType);
            e.Data.SetData("MermaidText", item.DefaultText);
        }
    }
}
