using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MarkSmith.Services;

namespace MarkSmith.Controls
{
    public sealed partial class ExtensionHintBar : UserControl
    {
        public event EventHandler? GetExtensionRequested;

        public static readonly DependencyProperty IsOpenProperty =
            DependencyProperty.Register("IsOpen", typeof(bool), typeof(ExtensionHintBar), new PropertyMetadata(false, OnIsOpenChanged));

        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty);
            set => SetValue(IsOpenProperty, value);
        }

        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (ExtensionHintBar)d;
            var open = (bool)e.NewValue;
            control.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open)
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, "That paste looks like plain text. Use the MarkSmith Connector to keep full formatting.");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(control, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            }
        }

        public ExtensionHintBar()
        {
            this.InitializeComponent();
            this.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
            AddHandler(KeyDownEvent, new KeyEventHandler(OnBarKeyDown), true);
            HoverPolish.Track(this);
            SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        }

        private void OnBarKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape && IsOpen)
            {
                IsOpen = false;
                e.Handled = true;
            }
        }

        // Wide: [icon | text | CTA | ×] on one row. Narrow: the CTA drops under the text so the
        // body never gets squeezed into a sliver beside a fixed-width button.
        private void ApplyLayout(double width)
        {
            var narrow = width < 640;
            Grid.SetRow(GetExtensionButton, narrow ? 1 : 0);
            Grid.SetColumn(GetExtensionButton, narrow ? 1 : 2);
            GetExtensionButton.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }

        // Opens the in-app setup guide (MainWindow.ShowExtensionSetupAsync), which points Load
        // unpacked at the extension folder MarkSmith ships. It used to open the GitHub source folder.
        private void OnGetExtensionClick(object sender, RoutedEventArgs e)
        {
            GetExtensionRequested?.Invoke(this, EventArgs.Empty);
            IsOpen = false;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            IsOpen = false;
        }
    }
}

