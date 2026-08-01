using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MdToPdf.Views.SmartArt;

public sealed partial class SmartArtPaletteControl : UserControl
{
    private string _selectedCategory = "All";

    public SmartArtPaletteControl()
    {
        InitializeComponent();
    }

    private void OnCategoryPillClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Content is string cat)
        {
            _selectedCategory = cat;
            CatAll.IsChecked = btn == CatAll;
            CatBasic.IsChecked = btn == CatBasic;
            CatSmartArt.IsChecked = btn == CatSmartArt;
            CatSpecial.IsChecked = btn == CatSpecial;

            FilterItems();
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        FilterItems();
    }

    private void FilterItems()
    {
        if (DataContext is ViewModels.SmartArt.SmartArtDesignerViewModel vm)
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
        if (e.Items.FirstOrDefault() is ViewModels.SmartArt.SmartArtPaletteItem item)
        {
            e.Data.SetData("SmartArtCategory", item.Category);
            e.Data.SetData("SmartArtShapeType", item.ShapeType);
            e.Data.SetData("SmartArtText", item.DefaultText);
            e.Data.SetData("SmartArtColor", item.Color);
        }
    }
}
