using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace MarkSmith.Controls
{
    public sealed partial class ExtensionHintBar : UserControl
    {
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
            control.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public ExtensionHintBar()
        {
            this.InitializeComponent();
            this.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnGetExtensionClick(object sender, RoutedEventArgs e)
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/thebubbsy/MarkSmith/tree/main/extension")); }
            catch { }
            IsOpen = false;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            IsOpen = false;
        }
    }
}

