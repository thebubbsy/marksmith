using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MarkSmith.Services;

namespace MarkSmith.Controls
{
    public sealed partial class ExtensionTip : UserControl
    {
        public event EventHandler? GetExtensionRequested;

        public static readonly DependencyProperty IsOpenProperty =
            DependencyProperty.Register("IsOpen", typeof(bool), typeof(ExtensionTip), new PropertyMetadata(true, OnIsOpenChanged));

        public event EventHandler? Closed;

        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty);
            set => SetValue(IsOpenProperty, value);
        }

        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (ExtensionTip)d;
            var open = (bool)e.NewValue;
            control.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open)
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, "Extension tip: Install the MarkSmith Connector browser extension to send AI replies straight here.");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(control, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            }
        }

        public ExtensionTip()
        {
            this.InitializeComponent();
            this.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
            AddHandler(KeyDownEvent, new KeyEventHandler(OnTipKeyDown), true);
            HoverPolish.Track(this);
        }

        private void OnTipKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape && IsOpen)
            {
                IsOpen = false;
                Closed?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        // Opens the in-app setup guide (MainWindow.ShowExtensionSetupAsync), which points Load
        // unpacked at the extension folder MarkSmith ships. It used to open the GitHub source folder.
        private void OnGetExtensionClick(object sender, RoutedEventArgs e)
        {
            GetExtensionRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
