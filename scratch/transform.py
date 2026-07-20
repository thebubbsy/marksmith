import re

with open('scratch/MainWindow_backup.xaml', 'r', encoding='utf-8') as f:
    text = f.read()

def extract_node(text, start_tag, end_tag):
    start = text.find(start_tag)
    if start == -1: return ""
    end = text.find(end_tag, start) + len(end_tag)
    return text[start:end]

def extract_by_regex(text, regex):
    m = re.search(regex, text, re.DOTALL)
    if m: return m.group(0)
    return ""

automation = extract_by_regex(text, r'<Expander x:Name="AutomationExpander".*?</Expander>')
style_card = extract_by_regex(text, r'<Border x:Name="StyleCard".*?</Border>')
history_list = extract_by_regex(text, r'<ListView x:Name="HistoryList".*?</ListView>')

header = text[:text.find('<!-- Left-to-right pipeline:')]

new_xaml = header + """
        <!-- 3-Pane Looking Glass UI -->
        <Grid Grid.Row="2" Padding="16,6,16,16" ColumnSpacing="10">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="280" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="350" />
            </Grid.ColumnDefinitions>

            <!-- Left Pane: History -->
            <Grid Grid.Column="0" RowSpacing="10">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                </Grid.RowDefinitions>
                <TextBlock Grid.Row="0" Text="History" FontWeight="SemiBold" Style="{StaticResource StepTitleStyle}" />
""" + history_list.replace('MaxHeight="420"', '').replace('Grid.Row="1"', 'Grid.Row="1" VerticalAlignment="Stretch"') + """
            </Grid>

            <!-- Center Pane: Looking Glass Editor & Preview -->
            <Grid Grid.Column="1">
                <!-- Layer 0: Live Preview -->
                <WebView2 x:Name="PreviewWebView" Margin="1" />
                
                <!-- Layer 1: The Editor -->
                <TextBox x:Name="EditorTextBox"
                         Text="{Binding PastedMarkdown, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                         AcceptsReturn="True" TextWrapping="Wrap"
                         FontFamily="Cascadia Mono, Consolas, monospace" FontSize="13"
                         PlaceholderText="Paste or type Markdown here..."
                         VerticalContentAlignment="Top" IsSpellCheckEnabled="False"
                         ScrollViewer.VerticalScrollBarVisibility="Auto"
                         Background="Transparent"
                         PointerMoved="OnEditorPointerMoved"
                         GotFocus="OnEditorGotFocus"
                         LostFocus="OnEditorLostFocus"
                         SelectionChanged="OnEditorSelectionChanged"
                         Foreground="Transparent"
                         Padding="12" />
                         
                <!-- Layer 2: Looking Glass Overlay -->
                <!-- The foreground text of the editor that is only revealed around the cursor/mouse -->
                <TextBlock x:Name="EditorOverlayText"
                           Text="{Binding PastedMarkdown}"
                           FontFamily="Cascadia Mono, Consolas, monospace" FontSize="13"
                           TextWrapping="Wrap" IsHitTestVisible="False"
                           Padding="12">
                    <TextBlock.OpacityMask>
                        <RadialGradientBrush x:Name="LookingGlassBrush" Center="0.5,0.5" GradientOrigin="0.5,0.5" RadiusX="0.2" RadiusY="0.2">
                            <GradientStop Color="#FF000000" Offset="0" />
                            <GradientStop Color="#FF000000" Offset="0.5" />
                            <GradientStop Color="#00000000" Offset="1" />
                        </RadialGradientBrush>
                    </TextBlock.OpacityMask>
                </TextBlock>

                <!-- Status Overlay -->
                <Grid x:Name="PreviewSpinner" Visibility="Collapsed" Background="#14000000">
                    <Image x:Name="SpinnerLogo" Source="ms-appx:///Assets/logo.png"
                           Width="60" Height="60" HorizontalAlignment="Center" VerticalAlignment="Center"
                           RenderTransformOrigin="0.5,0.5">
                        <Image.RenderTransform>
                            <CompositeTransform x:Name="SpinnerLogoTransform" />
                        </Image.RenderTransform>
                    </Image>
                </Grid>
                
                <!-- Ruler -->
                <Grid Height="18" Margin="2,-4,2,-2" ToolTipService.ToolTip="Width of the live preview" VerticalAlignment="Bottom">
                    <Rectangle Height="1" VerticalAlignment="Center"
                               Fill="{ThemeResource TextFillColorSecondaryBrush}" Opacity="0.35" />
                    <Rectangle Width="1" Height="7" HorizontalAlignment="Left" VerticalAlignment="Center"
                               Fill="{ThemeResource TextFillColorSecondaryBrush}" Opacity="0.55" />
                    <Rectangle Width="1" Height="7" HorizontalAlignment="Right" VerticalAlignment="Center"
                               Fill="{ThemeResource TextFillColorSecondaryBrush}" Opacity="0.55" />
                    <Border HorizontalAlignment="Center" VerticalAlignment="Center" CornerRadius="4" Padding="8,0"
                            Background="{ThemeResource SolidBackgroundFillColorBaseBrush}">
                        <TextBlock x:Name="PreviewWidthText" Text="— px" FontSize="11"
                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                    </Border>
                </Grid>
            </Grid>

            <!-- Right Pane: Options & Actions -->
            <Grid Grid.Column="2">
              <Grid.RowDefinitions>
                  <RowDefinition Height="*" />
                  <RowDefinition Height="Auto" />
              </Grid.RowDefinitions>
              
              <ScrollViewer Grid.Row="0" VerticalScrollBarVisibility="Auto">
                  <StackPanel Spacing="16">
                      <!-- Output Format Choice (Phase 2) -->
                      <ComboBox Header="Output Format" HorizontalAlignment="Stretch" SelectedIndex="{Binding OutputFormat, Mode=TwoWay}">
                          <x:String>PDF</x:String>
                          <x:String>DOCX</x:String>
                          <x:String>PPTX</x:String>
                          <x:String>EPUB</x:String>
                      </ComboBox>
                      
""" + style_card.replace('Grid.Row="1"', '') + """
                      
""" + automation.replace('Grid.Row="2"', '') + """
                  </StackPanel>
              </ScrollViewer>
              
              <StackPanel Grid.Row="1" Spacing="8" Margin="0,10,0,0">
                  <InfoBar IsOpen="True" IsClosable="False"
                           Severity="{Binding StatusSeverity, Converter={StaticResource StatusSeverityConverter}}" Message="{Binding StatusText}" />
                  <Button x:Name="GeneratePdfButton" Content="Generate PDF" Click="OnExportPdfClick" HorizontalAlignment="Stretch" Style="{StaticResource AccentButtonStyle}" />
                  <Button x:Name="ExportDocxButton" Content="Export DOCX" Click="OnExportDocxClick" HorizontalAlignment="Stretch" />
              </StackPanel>
            </Grid>
        </Grid>
    </Grid>
</Window>
"""

with open('MdToPdf/MainWindow.xaml', 'w', encoding='utf-8') as f:
    f.write(new_xaml)
