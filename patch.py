import os

with open('MdToPdf/MainWindow.xaml', 'r', encoding='utf-8') as f:
    xaml = f.read()

start_str = "        <!-- Left-to-right pipeline: 1 Source -> 2 Style -> 3 Preview & Export -->"
start_idx = xaml.find(start_str)

end_str = "    </Grid>\n</Window>"
end_idx = xaml.find(end_str)

if start_idx != -1 and end_idx != -1:
    legacy_ui = xaml[start_idx:end_idx]
    
    # Replace the legacy HistoryList name
    legacy_ui = legacy_ui.replace('x:Name="HistoryList"', 'x:Name="LegacyHistoryList"')
    
    # Wrap it in Collapsed Grid
    wrapped = '        <Grid Visibility="Collapsed" x:Name="LegacyUI_DoNotUse">\n' + legacy_ui + '        </Grid>\n'
    
    with open(r'C:\Users\Tony\.gemini\antigravity\scratch\LookingGlassViewGrid.xml', 'r', encoding='utf-8') as f:
        new_ui = f.read()
        
    final_xaml = xaml[:start_idx] + wrapped + new_ui + '\n' + end_str
    
    with open('MdToPdf/MainWindow.xaml', 'w', encoding='utf-8') as f:
        f.write(final_xaml)
    print("Successfully patched!")
else:
    print("Could not find boundaries.")
