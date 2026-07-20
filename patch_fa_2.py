import re
import os

base_dir = r'MdToPdf.Avalonia\Views'

# 1. Fix MainWindow.axaml
mw_path = os.path.join(base_dir, 'MainWindow.axaml')
with open(mw_path, 'r', encoding='utf-8') as f:
    mw_content = f.read()

# Replace <ui:FANumberBox ... /> and <ui:FANumberBox> ... </ui:FANumberBox>
# with <NumericUpDown ... /> and remove Header="...", SpinButtonPlacementMode="..."
mw_content = mw_content.replace('<ui:FANumberBox', '<NumericUpDown')
mw_content = mw_content.replace('</ui:FANumberBox>', '</NumericUpDown>')
mw_content = re.sub(r'SpinButtonPlacementMode=".*?"', '', mw_content)
mw_content = re.sub(r'Header="(.*?)"', r'ToolTip.Tip="\1"', mw_content)

with open(mw_path, 'w', encoding='utf-8') as f:
    f.write(mw_content)

# 2. Fix SettingsView.axaml
sw_path = os.path.join(base_dir, 'SettingsView.axaml')
if os.path.exists(sw_path):
    with open(sw_path, 'r', encoding='utf-8') as f:
        sw_content = f.read()
    sw_content = sw_content.replace('<ui:FANumberBox', '<NumericUpDown')
    sw_content = sw_content.replace('</ui:FANumberBox>', '</NumericUpDown>')
    sw_content = re.sub(r'SpinButtonPlacementMode=".*?"', '', sw_content)
    sw_content = re.sub(r'Header="(.*?)"', r'ToolTip.Tip="\1"', sw_content)
    with open(sw_path, 'w', encoding='utf-8') as f:
        f.write(sw_content)

# 3. Fix WelcomeTour.axaml
wt_path = os.path.join(base_dir, 'WelcomeTour.axaml')
with open(wt_path, 'r', encoding='utf-8') as f:
    wt_content = f.read()

wt_content = re.sub(r'<Grid(.*?)Padding="(.*?)"', r'<Grid\1Margin="\2"', wt_content)
wt_content = wt_content.replace('Visibility="Collapsed"', 'IsVisible="False"')
wt_content = wt_content.replace('Visibility="Visible"', 'IsVisible="True"')
with open(wt_path, 'w', encoding='utf-8') as f:
    f.write(wt_content)

print("FANumberBox and Grid padding patched.")
