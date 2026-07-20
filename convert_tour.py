import re

with open(r'MdToPdf\Views\WelcomeTour.xaml', 'r', encoding='utf-8') as f:
    xaml = f.read()

# Replace namespaces
xaml = xaml.replace('xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"', 'xmlns="https://github.com/avaloniaui"')
xaml = xaml.replace('xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"', 'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"\n    xmlns:ui="using:FluentAvalonia.UI.Controls"')
xaml = xaml.replace('x:Class="MdToPdf.Views.WelcomeTour"', 'x:Class="MdToPdf.Avalonia.Views.WelcomeTour"')

# Replace ThemeResource with DynamicResource
xaml = re.sub(r'\{ThemeResource (.*?)\}', r'{DynamicResource \1}', xaml)

# We need to extract Styles from Resources and put them in <UserControl.Styles>
styles_match = re.findall(r'<Style x:Key="(.*?)" TargetType="(.*?)">(.*?)</Style>', xaml, re.DOTALL)

new_styles = ""
for key, target, content in styles_match:
    target = target.replace("FontIcon", "ui:FontIcon").replace("PipsPager", "ui:PipsPager")
    content = content.replace("<Setter Property=\"Foreground\"", "<Setter Property=\"Foreground\"").replace("<Setter Property=\"HorizontalAlignment\"", "<Setter Property=\"HorizontalAlignment\"")
    # Change Setters from Value="..." to Value="..."
    # In Avalonia 11, Setter syntax is same
    new_styles += f'    <Style Selector="{target}.{key}">\n{content}    </Style>\n'

xaml = re.sub(r'<Style x:Key="(.*?)" TargetType="(.*?)">(.*?)</Style>', '', xaml, flags=re.DOTALL)

# Insert <UserControl.Styles>
new_styles = f'<UserControl.Styles>\n{new_styles}</UserControl.Styles>\n'
xaml = xaml.replace('<UserControl.Resources>', new_styles + '<UserControl.Resources>')

# Replace Style="{StaticResource Key}" with Classes="Key"
xaml = re.sub(r'Style="\{StaticResource (.*?)\}"', r'Classes="\1"', xaml)
xaml = xaml.replace('Style="{DynamicResource AccentButtonStyle}"', 'Classes="accent"')
xaml = xaml.replace('Style="{StaticResource AccentButtonStyle}"', 'Classes="accent"')

# Replace FontIcon with ui:FontIcon
xaml = xaml.replace('<FontIcon', '<ui:FontIcon')
xaml = xaml.replace('</FontIcon>', '</ui:FontIcon>')

# Replace PipsPager with ui:PipsPager
xaml = xaml.replace('<PipsPager', '<ui:PipsPager')
xaml = xaml.replace('</PipsPager>', '</ui:PipsPager>')

# Fix TextWrapping="Wrap"
# In Avalonia TextWrapping="Wrap" is valid.

# Fix CheckBox content alignment
xaml = xaml.replace('HorizontalContentAlignment="Left"', 'HorizontalAlignment="Left"')

# In Avalonia, PipsPager is ui:PipsPager, and SelectedIndexChanged is SelectedIndexChanged event doesn't exist? In FluentAvalonia it's SelectedIndexChanged
# WinUI uses NumberOfPages, FluentAvalonia uses NumberOfPages

with open(r'MdToPdf.Avalonia\Views\WelcomeTour.axaml', 'w', encoding='utf-8') as f:
    f.write(xaml)

print("Created WelcomeTour.axaml")
