import re
import os

base_dir = r'MdToPdf.Avalonia\Views'
mw_path = os.path.join(base_dir, 'MainWindow.axaml')
with open(mw_path, 'r', encoding='utf-8') as f:
    mw_content = f.read()

# Replace InfoBars with Borders
infobar_license = '''        <Border x:Name="LicenseBanner" IsVisible="False" Background="#FFE6E6" BorderBrush="#FF0000" BorderThickness="1" CornerRadius="4" Padding="10" Margin="0,0,0,10">
            <TextBlock Text="Unlicensed: Marksmith requires a valid license. Please visit settings to register." Foreground="#FF0000" FontWeight="Bold" />
        </Border>'''
mw_content = re.sub(r'<InfoBar x:Name="LicenseBanner".*?/>', infobar_license, mw_content, flags=re.DOTALL)

infobar_hint = '''        <Border x:Name="ExtensionHintBar" IsVisible="False" Background="#FFFFE0" BorderBrush="#CCCC00" BorderThickness="1" CornerRadius="4" Padding="10" Margin="0,0,0,10">
            <StackPanel Orientation="Horizontal" Spacing="10">
                <TextBlock Text="Wait, is that code? It looks like you pasted plain text or code rather than Markdown. Try using the VS Code extension to copy as markdown instead." VerticalAlignment="Center" />
                <Button Content="Get the extension" Click="OnGetExtensionClick" />
            </StackPanel>
        </Border>'''
mw_content = re.sub(r'<InfoBar x:Name="ExtensionHintBar".*?</InfoBar>', infobar_hint, mw_content, flags=re.DOTALL)

infobar_tip = '''                <Border x:Name="ExtensionTip" IsVisible="True" Background="#E6F2FF" BorderBrush="#0066CC" BorderThickness="1" CornerRadius="4" Padding="10" Margin="0,10,0,0">
                    <StackPanel Orientation="Horizontal" Spacing="10">
                        <TextBlock Text="Tip: Using VS Code? Install the Marksmith extension to copy code as Markdown effortlessly." VerticalAlignment="Center" />
                        <Button Content="Get it" Click="OnGetExtensionClick" />
                        <Button Content="Close" Click="OnExtensionTipClosed" />
                    </StackPanel>
                </Border>'''
mw_content = re.sub(r'<InfoBar x:Name="ExtensionTip".*?</InfoBar>', infobar_tip, mw_content, flags=re.DOTALL)

# Remove xmlns:ui
mw_content = re.sub(r'xmlns:ui="using:FluentAvalonia.UI.Controls"\s*', '', mw_content)

with open(mw_path, 'w', encoding='utf-8') as f:
    f.write(mw_content)

print("MainWindow.axaml InfoBars replaced.")

cs_path = os.path.join(base_dir, 'MainWindow.axaml.cs')
with open(cs_path, 'r', encoding='utf-8') as f:
    cs_content = f.read()

# Fix OnExtensionTipClosed signature (from InfoBar/FAInfoBar to object)
cs_content = re.sub(r'private void OnExtensionTipClosed\(.*? sender, EventArgs args\)', 'private void OnExtensionTipClosed(object sender, global::Avalonia.Interactivity.RoutedEventArgs args)', cs_content)

# Fix AskBatchFormatAsync ContentDialog
content_dialog_code = '''
        var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
        var win = new Window
        {
            Title = "Batch Conversion",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 400
        };
        var btnOk = new Button { Content = "Start converting", HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right };
        btnOk.Click += (_, __) => { win.Close(); tcs.SetResult("ok"); };
        var btnCancel = new Button { Content = "Cancel", HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right };
        btnCancel.Click += (_, __) => { win.Close(); tcs.SetResult("cancel"); };
        var sp = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right };
        sp.Children.Add(btnCancel);
        sp.Children.Add(btnOk);
        body.Children.Add(sp);
        win.Content = new Border { Padding = new global::Avalonia.Thickness(20), Child = body };
        
        await win.ShowDialog(this);
        var resultStr = await tcs.Task;
        if (resultStr == "cancel") return null;
'''
cs_content = re.sub(r'var dialog = new ContentDialog.*?var result = await dialog\.ShowAsync\(\);.*?if \(result != ContentDialogResult\.Primary\)', content_dialog_code + '        if (false)', cs_content, flags=re.DOTALL)

# Remove InfoBar properties in MainWindow.axaml.cs
cs_content = re.sub(r'ExtensionHintBar\.IsOpen = true;', 'ExtensionHintBar.IsVisible = true;', cs_content)
cs_content = re.sub(r'ExtensionHintBar\.IsOpen = false;', 'ExtensionHintBar.IsVisible = false;', cs_content)
cs_content = re.sub(r'ExtensionTip\.IsOpen = false;', 'ExtensionTip.IsVisible = false;', cs_content)

# Remove FluentAvalonia using
cs_content = cs_content.replace('using FluentAvalonia.UI.Controls;', '')

with open(cs_path, 'w', encoding='utf-8') as f:
    f.write(cs_content)

print("MainWindow.axaml.cs ContentDialog replaced.")
