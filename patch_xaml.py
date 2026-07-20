import re

wt_path = r'MdToPdf.Avalonia\Views\WelcomeTour.axaml'
with open(wt_path, 'r', encoding='utf-8') as f:
    wt_content = f.read()

# Fix xmlns:ui
wt_content = wt_content.replace('xmlns:x="https://github.com/avaloniaui"\n    xmlns:ui="using:FluentAvalonia.UI.Controls"', 'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"\n    xmlns:ui="using:FluentAvalonia.UI.Controls"')
wt_content = wt_content.replace('xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"\n    xmlns:ui="using:FluentAvalonia.UI.Controls"', 'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"\n    xmlns:ui="using:FluentAvalonia.UI.Controls"')
if 'xmlns:ui=' not in wt_content:
    wt_content = wt_content.replace('xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"', 'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"\n    xmlns:ui="using:FluentAvalonia.UI.Controls"')

# Replace Visibility="Collapsed" with IsVisible="False"
wt_content = wt_content.replace('Visibility="Collapsed"', 'IsVisible="False"')
wt_content = wt_content.replace('Visibility="Visible"', 'IsVisible="True"')

# StackPanel doesn't have Padding in Avalonia, convert Padding="X" to Margin="X" (hack but works)
# Wait, if we change StackPanel Padding to Margin, it will just apply margin.
wt_content = re.sub(r'<StackPanel(.*?)Padding="(.*?)"', r'<StackPanel\1Margin="\2"', wt_content)

# Fix ui:FontIcon Selector in Styles
wt_content = wt_content.replace('Selector="ui:FontIcon.TourGlyph"', 'Selector="ui|FontIcon.TourGlyph"')
wt_content = wt_content.replace('Selector="ui:FontIcon.TourHero"', 'Selector="ui|FontIcon.TourHero"')

with open(wt_path, 'w', encoding='utf-8') as f:
    f.write(wt_content)


mw_path = r'MdToPdf.Avalonia\Views\MainWindow.axaml'
with open(mw_path, 'r', encoding='utf-8') as f:
    mw_content = f.read()

# Fix OpenUrlCommand in ExtensionTip
mw_content = mw_content.replace('Command="{Binding OpenUrlCommand}" CommandParameter="https://github.com/thebubbsy/marksmith/tree/main/extension"', 'Click="OnGetExtensionClick"')

with open(mw_path, 'w', encoding='utf-8') as f:
    f.write(mw_content)
    
print("Fixed WelcomeTour and MainWindow XAML")
