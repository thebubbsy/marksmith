using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace MarkSmith.Controls
{
    public sealed partial class ExtensionTip : UserControl
    {
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
            control.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public ExtensionTip()
        {
            this.InitializeComponent();
            this.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
