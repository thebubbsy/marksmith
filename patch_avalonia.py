import os

path = r"MdToPdf.Avalonia/Views/MainWindow.axaml"
with open(path, 'r', encoding='utf-8') as f:
    text = f.read()

old_content = """                                      <ComboBoxItem Content="Ask me each time" />
                                      <ComboBoxItem Content="Keep exact layout (Web Layout view)" />
                                      <ComboBoxItem Content="Reflow to fit the page" />
                                      <ComboBoxItem Content="Multi-page vertical downward (Print Layout)" />
                                      <ComboBoxItem Content="2x2 Grid poster (Print Layout)" />
                                      <ComboBoxItem Content="Shrink to fit one page (Print Layout)" />
                                      <ComboBoxItem Content="Compact spacing (Shrink gaps)" />
                                      <ComboBoxItem Content="Compact shapes (Shrink nodes)" />
                                      <ComboBoxItem Content="Ultra compact (Shrink both)" />"""

new_content = """                                      <ComboBoxItem Content="Ask me each time" />
                                      <ComboBoxItem Content="Keep Original Size (Web Layout)" />
                                      <ComboBoxItem Content="Gentle Shrink (Max 75%, Web Layout if needed)" />
                                      <ComboBoxItem Content="Slice Vertically (Multiple pages)" />
                                      <ComboBoxItem Content="Enlarge Page Size (2x2 Poster)" />
                                      <ComboBoxItem Content="Aggressive Shrink (Force to 1 page)" />
                                      <ComboBoxItem Content="Compress Gaps (ShapeForge native shapes)" />
                                      <ComboBoxItem Content="Compress Nodes (ShapeForge native shapes)" />
                                      <ComboBoxItem Content="Compress Both (ShapeForge native shapes)" />"""

text = text.replace(old_content, new_content)

with open(path, 'w', encoding='utf-8') as f:
    f.write(text)
