import os
import re

def replace_in_file(path, replacements):
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    for old, new in replacements.items():
        content = content.replace(old, new)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(content)

replacements = {
    'FAInfoBar': 'InfoBar',
    'FAContentDialog': 'ContentDialog',
    'FAProgressRing': 'ProgressRing',
}

base_dir = r'MdToPdf.Avalonia\Views'

# 1. MainWindow.axaml
replace_in_file(os.path.join(base_dir, 'MainWindow.axaml'), replacements)

# 2. MainWindow.axaml.cs
replace_in_file(os.path.join(base_dir, 'MainWindow.axaml.cs'), replacements)

# 3. SettingsView.axaml
replace_in_file(os.path.join(base_dir, 'SettingsView.axaml'), replacements)

# 4. SettingsView.axaml.cs (if any)
if os.path.exists(os.path.join(base_dir, 'SettingsView.axaml.cs')):
    replace_in_file(os.path.join(base_dir, 'SettingsView.axaml.cs'), replacements)

# 5. WelcomeTour.axaml
# Replace PipsPager with a simple TextBlock for simplicity since it's failing to resolve
wt_path = os.path.join(base_dir, 'WelcomeTour.axaml')
with open(wt_path, 'r', encoding='utf-8') as f:
    wt_content = f.read()

# Find the PipsPager and replace it with a TextBlock that we can bind or just leave empty
wt_content = re.sub(
    r'<ui:PipsPager x:Name="Pips".*?/>',
    r'<TextBlock x:Name="Pips" Text="Tour Progress" HorizontalAlignment="Center" VerticalAlignment="Center" />',
    wt_content,
    flags=re.DOTALL
)
with open(wt_path, 'w', encoding='utf-8') as f:
    f.write(wt_content)

# 6. WelcomeTour.axaml.cs
wt_cs_path = os.path.join(base_dir, 'WelcomeTour.axaml.cs')
with open(wt_cs_path, 'r', encoding='utf-8') as f:
    wt_cs_content = f.read()

# Remove PipsPager code
wt_cs_content = wt_cs_content.replace(
    '''        var pips = this.FindControl<PipsPager>("Pips");
        if (pips != null && pips.SelectedPageIndex != _index) 
            pips.SelectedPageIndex = _index;''',
    '''        var pips = this.FindControl<TextBlock>("Pips");
        if (pips != null) 
            pips.Text = $"Step {_index + 1} of 7";'''
)
# Remove OnPipSelected logic
wt_cs_content = re.sub(
    r'private void OnPipSelected\(.*?\).*?\{.*?\}',
    r'private void OnPipSelected(object sender, object args) { }',
    wt_cs_content,
    flags=re.DOTALL
)

with open(wt_cs_path, 'w', encoding='utf-8') as f:
    f.write(wt_cs_content)

print("FA prefixes and PipsPager patched!")
