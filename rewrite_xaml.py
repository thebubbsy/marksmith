import re

with open('MdToPdf/MainWindow.xaml', 'r', encoding='utf-8') as f:
    xaml = f.read()

# Extract sections
source_match = re.search(r'(<!-- ============ 1 · SOURCE ============ -->.*?)<FontIcon', xaml, re.DOTALL)
source_xml = source_match.group(1).strip()

style_match = re.search(r'(<!-- ============ 2 · STYLE ============ -->.*?)<FontIcon', xaml, re.DOTALL)
style_xml = style_match.group(1).strip()

preview_match = re.search(r'(<!-- ============ 3 · PREVIEW & EXPORT ============ -->.*?)</Grid>\s*</Grid>\s*</Window>', xaml, re.DOTALL)
preview_xml = preview_match.group(1).strip()

# Now we construct the new Grid layout
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

            <FontIcon Grid.Column="1" Width="16" Background="Transparent" ResizeBehavior="BasedOnAlignment" ResizeDirection="Auto" />

            <!-- CENTER PANE: LOOKING GLASS -->
            __CENTER_PANE__

            <FontIcon Grid.Column="3" Width="16" Background="Transparent" ResizeBehavior="BasedOnAlignment" ResizeDirection="Auto" />

            <!-- RIGHT PANE -->
            __RIGHT_PANE__
        </Grid>
"""

# Modify Left Pane (Col 0)
left_pane = source_xml.replace('Grid.Column="0"', 'Grid.Column="0"')
# We want to remove PasteTab and PastePanel from Left Pane
left_pane = re.sub(r'<SelectorBarItem x:Name="PasteTab".*?</SelectorBarItem>', '', left_pane, flags=re.DOTALL)
left_pane = re.sub(r'<!-- Paste mode -->.*?</Grid>', '', left_pane, flags=re.DOTALL)

# Modify Center Pane (Col 2)
# We need to extract the PastePanel grid and PreviewCard
paste_panel_match = re.search(r'(<!-- Paste mode -->.*?</Grid>)', source_xml, re.DOTALL)
paste_panel = paste_panel_match.group(1) if paste_panel_match else ""
paste_panel = paste_panel.replace('Grid.Row="1"', 'Grid.Row="1"').replace('Visibility="Collapsed"', 'Visibility="Visible"')

preview_card_match = re.search(r'(<Border x:Name="PreviewCard".*?</Border>)', preview_xml, re.DOTALL)
preview_card = preview_card_match.group(1) if preview_card_match else ""
# In the center pane, the PreviewCard needs to overlap the PastePanel. We put them in the same row.
# But wait, we want them to toggle visibility based on the Segmented control (or SelectorBar).
# We'll just stack them in the same Grid.Row="1".
preview_card = preview_card.replace('Grid.Row="1"', 'Grid.Row="1" Visibility="Collapsed"')

preview_width_match = re.search(r'(<!-- Live width ruler.*?</Grid>)', preview_xml, re.DOTALL)
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
center_pane = center_pane.replace('Grid.Row="2"', 'Grid.Row="2"')

# Modify Right Pane (Col 4)
style_xml = style_xml.replace('Grid.Column="2"', 'Grid.Column="4"')
style_xml = re.sub(r'<TextBlock Text="2"', '<TextBlock Text="3"', style_xml)
style_xml = style_xml.replace('Grid.Row="1"', 'Grid.Row="1"')

export_grid_match = re.search(r'(<Grid Grid\.Row="4".*?</Grid>\s*<Grid Grid\.Row="5".*?</Grid>)', preview_xml, re.DOTALL)
export_controls = export_grid_match.group(1) if export_grid_match else ""

buttons_match = re.search(r'(<Button ToolTipService\.ToolTip="Export history".*?<Button x:Name="SettingsButton".*?</Button>)', preview_xml, re.DOTALL)
header_buttons = buttons_match.group(1) if buttons_match else ""

right_pane = style_xml[:style_xml.rfind('</Grid>')] # open the Grid
# Fix Row definitions for Right Pane
right_pane = re.sub(r'<Grid.RowDefinitions>.*?</Grid.RowDefinitions>', '''<Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>''', right_pane, flags=re.DOTALL)

# Replace the top header in Style
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
                </Grid>
''', right_pane, flags=re.DOTALL)

export_controls = export_controls.replace('Grid.Row="4"', 'Grid.Row="2"').replace('Grid.Row="5"', 'Grid.Row="3"')
right_pane += f"\n{export_controls}\n            </Grid>"

# Now combine
new_grid = new_grid.replace('__LEFT_PANE__', left_pane)
new_grid = new_grid.replace('__CENTER_PANE__', center_pane)
new_grid = new_grid.replace('__RIGHT_PANE__', right_pane)

# Replace in full document
match = re.search(r'<Grid Grid.Row="2".*?</Grid>\s*</Grid>\s*</Window>', xaml, flags=re.DOTALL)
new_xaml = xaml[:match.start()] + new_grid + "\n        </Grid>\n    </Grid>\n</Window>"

with open('MdToPdf/MainWindow_new.xaml', 'w', encoding='utf-8') as f:
    f.write(new_xaml)
