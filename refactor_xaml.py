import xml.etree.ElementTree as ET
import re

with open('MdToPdf/MainWindow.original.txt', 'r', encoding='utf-8') as f:
    xaml = f.read()

# Add xmlns:controls if missing
if 'xmlns:controls="using:CommunityToolkit.WinUI.Controls"' not in xaml:
    xaml = xaml.replace('xmlns:conv="using:MdToPdf.Converters"', 'xmlns:conv="using:MdToPdf.Converters"\n    xmlns:controls="using:CommunityToolkit.WinUI.Controls"')

parts1 = xaml.split('<!-- ============ 1 · SOURCE ============ -->')
before_1 = parts1[0]
parts2 = parts1[1].split('<!-- ============ 2 · STYLE ============ -->')
source_xml = parts2[0]
parts3 = parts2[1].split('<!-- ============ 3 · PREVIEW & EXPORT ============ -->')
style_xml = parts3[0]
preview_xml = parts3[1]

# Strip off the <FontIcon> that are between the columns in the original
source_xml = re.sub(r'<FontIcon Grid.Column="1" Style="\{StaticResource FlowChevronStyle\}" />\s*$', '', source_xml.strip())
style_xml = re.sub(r'<FontIcon Grid.Column="3" Style="\{StaticResource FlowChevronStyle\}" />\s*$', '', style_xml.strip())

# The preview_xml ends with </Grid>\s*</Grid>\s*</Window>
preview_xml_match = re.search(r'(.*?)</Grid>\s*</Grid>\s*</Window>', preview_xml, re.DOTALL)
preview_xml_inner = preview_xml_match.group(1).strip() if preview_xml_match else preview_xml

new_grid = """
        <!-- 3-Pane Looking Glass Layout -->
        <Grid Grid.Row="2" Padding="16,6,16,16" ColumnSpacing="10">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="320" MinWidth="250" /> <!-- Left: Source/Files -->
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" MinWidth="400" /> <!-- Center: Looking Glass (Editor/Preview) -->
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="380" MinWidth="250" /> <!-- Right: Style/Export -->
            </Grid.ColumnDefinitions>

            <!-- LEFT PANE -->
            __LEFT_PANE__

            <controls:GridSplitter Grid.Column="1" Width="16" Background="Transparent" ResizeBehavior="BasedOnAlignment" ResizeDirection="Auto" />

            <!-- CENTER PANE: LOOKING GLASS -->
            __CENTER_PANE__

            <controls:GridSplitter Grid.Column="3" Width="16" Background="Transparent" ResizeBehavior="BasedOnAlignment" ResizeDirection="Auto" />

            <!-- RIGHT PANE -->
            __RIGHT_PANE__
        </Grid>
"""

# LEFT PANE
left_pane = source_xml.replace('Grid.Column="0"', 'Grid.Column="0"')
left_pane = re.sub(r'<SelectorBarItem x:Name="PasteTab".*?</SelectorBarItem>', '', left_pane, flags=re.DOTALL)
left_pane = re.sub(r'<!-- Paste mode -->.*?</Grid>', '', left_pane, flags=re.DOTALL)

# CENTER PANE
paste_panel_match = re.search(r'(<!-- Paste mode -->.*?<!-- Automated ingest)', source_xml, re.DOTALL)
if paste_panel_match:
    paste_panel = paste_panel_match.group(1)
    paste_panel = paste_panel[:paste_panel.rfind('</Grid>') + 7]
    paste_panel = paste_panel.replace('Grid.Row="1"', 'Grid.Row="1"').replace('Visibility="Collapsed"', 'Visibility="Visible"')
else:
    paste_panel = ""

preview_card_match = re.search(r'(<Border x:Name="PreviewCard".*?</Border>)', preview_xml_inner, re.DOTALL)
preview_card = preview_card_match.group(1) if preview_card_match else ""
preview_card = preview_card.replace('Grid.Row="1"', 'Grid.Row="1" Visibility="Collapsed"')

preview_width_match = re.search(r'(<!-- Live width ruler.*?</Grid>)', preview_xml_inner, re.DOTALL)
preview_width = preview_width_match.group(1) if preview_width_match else ""

center_pane = f"""
            <Grid Grid.Column="2" RowSpacing="10">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>

                <Grid Grid.Row="0">
                    <StackPanel Orientation="Horizontal" Spacing="10">
                        <Border Style="{{StaticResource StepBadgeStyle}}">
                            <TextBlock Text="2" Style="{{StaticResource StepBadgeTextStyle}}" />
                        </Border>
                        <StackPanel VerticalAlignment="Center">
                            <TextBlock Text="Editor / Preview" Style="{{StaticResource StepTitleStyle}}" />
                            <TextBlock Text="Looking Glass Layer" Style="{{StaticResource StepCaptionStyle}}" />
                        </StackPanel>
                    </StackPanel>
                    
                    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right" VerticalAlignment="Center">
                        <Border x:Name="DetectedBadge" Visibility="Collapsed" CornerRadius="12" Padding="10,4"
                                Background="{{ThemeResource AccentFillColorSecondaryBrush}}" VerticalAlignment="Center">
                            <StackPanel Orientation="Horizontal" Spacing="6">
                                <FontIcon Glyph="&#xE945;" FontSize="12"
                                          Foreground="{{ThemeResource TextOnAccentFillColorPrimaryBrush}}" />
                                <TextBlock Text="{{Binding DetectedSourceText}}" FontSize="12"
                                           Foreground="{{ThemeResource TextOnAccentFillColorPrimaryBrush}}" />
                            </StackPanel>
                        </Border>
                        <SelectorBar x:Name="CenterViewSelector" SelectionChanged="OnCenterViewSelectorChanged">
                            <SelectorBarItem x:Name="ViewCodeTab" Text="Code" IsSelected="True" />
                            <SelectorBarItem x:Name="ViewPreviewTab" Text="Preview" />
                        </SelectorBar>
                    </StackPanel>
                </Grid>

                <Grid Grid.Row="1">
                    {paste_panel}
                    {preview_card}
                </Grid>
                
                <Grid Grid.Row="2" Visibility="Collapsed" x:Name="PreviewWidthContainer">
                    {preview_width}
                </Grid>
            </Grid>
"""

# RIGHT PANE
style_xml = style_xml.replace('Grid.Column="2"', 'Grid.Column="4"')
style_xml = re.sub(r'<TextBlock Text="2"', '<TextBlock Text="3"', style_xml)
style_xml = style_xml.replace('Grid.Row="1"', 'Grid.Row="1"')

export_grid_match = re.search(r'(<Grid Grid\.Row="4".*?</Grid>\s*<Grid Grid\.Row="5".*?</Grid>)', preview_xml_inner, re.DOTALL)
export_controls = export_grid_match.group(1) if export_grid_match else ""

buttons_match = re.search(r'(<Button ToolTipService\.ToolTip="Export history".*?<Button x:Name="SettingsButton".*?</Button>)', preview_xml_inner, re.DOTALL)
header_buttons = buttons_match.group(1) if buttons_match else ""

right_pane = style_xml[:style_xml.rfind('</Grid>')]
right_pane = re.sub(r'<Grid.RowDefinitions>.*?</Grid.RowDefinitions>', '''<Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>''', right_pane, flags=re.DOTALL)

# Replaces the whole first grid/stackpanel
right_pane = re.sub(r'<StackPanel Grid.Row="0".*?</StackPanel>', f'''
                <Grid Grid.Row="0">
                    <StackPanel Orientation="Horizontal" Spacing="10">
                        <Border Style="{{StaticResource StepBadgeStyle}}">
                            <TextBlock Text="3" Style="{{StaticResource StepBadgeTextStyle}}" />
                        </Border>
                        <StackPanel VerticalAlignment="Center">
                            <TextBlock Text="Style &amp; Export" Style="{{StaticResource StepTitleStyle}}" />
                            <TextBlock Text="Finish the document" Style="{{StaticResource StepCaptionStyle}}" />
                        </StackPanel>
                    </StackPanel>
                    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right" VerticalAlignment="Center">
                        {header_buttons}
                    </StackPanel>
                </Grid>''', right_pane, count=1, flags=re.DOTALL)

# Let's fix the dangling StackPanel issue by using python string manipulation instead of regex on the unknown structure.
# Wait, the original source is:
# <StackPanel Grid.Row="0" Orientation="Horizontal" Spacing="10"> ... </StackPanel>
# We can just replace the whole `<StackPanel Grid.Row="0" ...> ... </StackPanel>` by matching from `<StackPanel Grid.Row="0"` to its balancing `</StackPanel>`
# Actually, re.sub with `count=1` and `.*?` will fail if there are nested stackpanels.
# Let's just find the exact string to replace.
exact_header = """<StackPanel Grid.Row="0" Orientation="Horizontal" Spacing="10">
                    <Border Style="{StaticResource StepBadgeStyle}">
                        <TextBlock Text="3" Style="{StaticResource StepBadgeTextStyle}" />
                    </Border>
                    <StackPanel VerticalAlignment="Center">
                        <TextBlock Text="Style" Style="{StaticResource StepTitleStyle}" />
                        <TextBlock Text="Theme and page layout" Style="{StaticResource StepCaptionStyle}" />
                    </StackPanel>
                </StackPanel>"""

if exact_header in right_pane:
    right_pane = right_pane.replace(exact_header, f'''
                <Grid Grid.Row="0">
                    <StackPanel Orientation="Horizontal" Spacing="10">
                        <Border Style="{{StaticResource StepBadgeStyle}}">
                            <TextBlock Text="3" Style="{{StaticResource StepBadgeTextStyle}}" />
                        </Border>
                        <StackPanel VerticalAlignment="Center">
                            <TextBlock Text="Style &amp; Export" Style="{{StaticResource StepTitleStyle}}" />
                            <TextBlock Text="Finish the document" Style="{{StaticResource StepCaptionStyle}}" />
                        </StackPanel>
                    </StackPanel>
                    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right" VerticalAlignment="Center">
                        {header_buttons}
                    </StackPanel>
                </Grid>''')

export_controls = export_controls.replace('Grid.Row="4"', 'Grid.Row="2"').replace('Grid.Row="5"', 'Grid.Row="3"')
right_pane += f"\n{export_controls}\n            </Grid>"

new_grid = new_grid.replace('__LEFT_PANE__', left_pane)
new_grid = new_grid.replace('__CENTER_PANE__', center_pane)
new_grid = new_grid.replace('__RIGHT_PANE__', right_pane)

new_xaml = before_1 + new_grid + "\n        </Grid>\n    </Grid>\n</Window>"

with open('MdToPdf/MainWindow.xaml', 'w', encoding='utf-8') as f:
    f.write(new_xaml)
