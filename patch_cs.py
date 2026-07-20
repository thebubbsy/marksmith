import os

path = 'MdToPdf/MainWindow.xaml.cs'
with open(path, 'r', encoding='utf-8') as f:
    code = f.read()

# Insert before the last closing brace
insertion = """
    private async void OnConvertPdfClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToPdfAsync();
    }

    private async void OnConvertDocxClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToDocxAsync();
    }

    private void OnCenterViewSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ViewPreviewTab == null) return;
        var isPreview = sender.SelectedItem == ViewPreviewTab;
        if (PastePanel != null) PastePanel.Visibility = isPreview ? Visibility.Collapsed : Visibility.Visible;
        if (PreviewCard != null) PreviewCard.Visibility = isPreview ? Visibility.Visible : Visibility.Collapsed;
        if (PreviewWidthContainer != null) PreviewWidthContainer.Visibility = isPreview ? Visibility.Visible : Visibility.Collapsed;
    }
"""

last_brace = code.rfind('}')
new_code = code[:last_brace] + insertion + code[last_brace:]

with open(path, 'w', encoding='utf-8') as f:
    f.write(new_code)
