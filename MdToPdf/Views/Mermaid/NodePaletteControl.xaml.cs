using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MdToPdf.Views.Mermaid;

public sealed partial class NodePaletteControl : UserControl
{
    private string _selectedCategory = "All";

    public NodePaletteControl()
    {
        InitializeComponent();
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
