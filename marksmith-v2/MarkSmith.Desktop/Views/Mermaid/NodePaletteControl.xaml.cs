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
    }

    private void OnCategoryPillClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Content is string cat)
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
