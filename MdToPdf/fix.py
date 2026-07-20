import re

file_path = r'C:\Users\Tony\.gemini\antigravity\scratch\marksmith\MdToPdf\MainWindow.xaml.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

def replace_line(line_num, new_content):
    lines[line_num - 1] = new_content

replace_line(148, '        // ExtensionTip.IsOpen = ViewModel.ShowExtensionTip;\n')
replace_line(296, '        // AdvancedStyleSection.Visibility = \n')
replace_line(297, '        //    (ViewModel.AdvancedMode && App.License.IsPro) ? Visibility.Visible : Visibility.Collapsed;\n')
replace_line(578, '            // ApiUrlText.Text = _apiServer.IsRunning ? f\"http://127.0.0.1:{_apiServer.Port}/api/health\" : \"\";\n')
replace_line(582, '            // ApiUrlText.Text = f\"API failed to start: {ex.Message}\";\n')

replace_line(1022, '        // var isPaste = ViewModel.UsePasteSource;\n')
replace_line(1028, '        // SourceSelector.SelectedItem = isPaste ? PasteTab : FileTab;\n')
replace_line(1029, '        // FilePanel.Visibility = isPaste ? Visibility.Collapsed : Visibility.Visible;\n')
replace_line(1030, '        // PastePanel.Visibility = isPaste ? Visibility.Visible : Visibility.Collapsed;\n')

replace_line(1174, '            // LookingGlassBrush.Center = new Windows.Foundation.Point(pt.X, pt.Y);\n')
replace_line(1175, '            // LookingGlassBrush.GradientStops[0].Offset = 0.0;\n')
replace_line(1176, '            // LookingGlassBrush.GradientStops[1].Offset = 1.0;\n')
replace_line(1177, '            // LookingGlassBrush.RadiusX = 150; LookingGlassBrush.RadiusY = 150;\n')

lines.append('''
    private void OnCenterPanePointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(CenterPaneGrid).Position;
        if (LookingGlassPreviewGrid.Clip is Microsoft.UI.Xaml.Media.RectangleGeometry rect)
        {
            rect.Rect = new Windows.Foundation.Rect(pt.X - 150, pt.Y - 150, 300, 300);
        }
        else
        {
            LookingGlassPreviewGrid.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new Windows.Foundation.Rect(pt.X - 150, pt.Y - 150, 300, 300) };
        }
    }
}
''')

with open(file_path, 'w', encoding='utf-8') as f:
    f.writelines(lines[:-2]) # remove the old closing braces and append the new method which has the closing brace.
    f.write(lines[-1])
