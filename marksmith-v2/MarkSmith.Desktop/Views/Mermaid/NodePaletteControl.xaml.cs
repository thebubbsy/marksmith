using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MarkSmith.Services;

namespace MarkSmith.Views.Mermaid;

public sealed partial class NodePaletteControl : UserControl
{
    private string _selectedCategory = "All";

    /// <summary>A palette row was clicked: the host adds that shape in view (dragging places it).</summary>
    public event EventHandler<ViewModels.Mermaid.MermaidPaletteItem>? ShapeRequested;

    // Row hover: the card's own border lights up in the studio cyan (the ListViewItem's hover fill
    // sits behind the card, so on its own a row gave no sign it was clickable).
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush RowRestBorder = new(Windows.UI.Color.FromArgb(255, 0x3D, 0x40, 0x5B));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush RowHoverBorder = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xC9, 0xF0));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush RowRestFill = new(Windows.UI.Color.FromArgb(255, 0x2B, 0x2D, 0x42));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush RowHoverFill = new(Windows.UI.Color.FromArgb(255, 0x33, 0x36, 0x50));

    private void OnRowPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border row) { row.BorderBrush = RowHoverBorder; row.Background = RowHoverFill; }
    }

    private void OnRowPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border row) { row.BorderBrush = RowRestBorder; row.Background = RowRestFill; }
    }

    private void OnPaletteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ViewModels.Mermaid.MermaidPaletteItem item) ShapeRequested?.Invoke(this, item);
    }

    public NodePaletteControl()
    {
        InitializeComponent();
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
    }

    private void OnCategoryPillClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is ToggleButton btn) SelectPill(btn);
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
                NoMatchesText.Text = string.IsNullOrEmpty(searchText)
                    ? $"No {selectedCat} shapes."
                    : selectedCat == "All" ? $"No shapes match “{SearchTextBox.Text.Trim()}”." : $"No {selectedCat} shapes match “{SearchTextBox.Text.Trim()}”.";
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
