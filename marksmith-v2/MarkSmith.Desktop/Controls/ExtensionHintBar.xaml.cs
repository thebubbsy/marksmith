using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
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
            control.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public ExtensionHintBar()
        {
            this.InitializeComponent();
            this.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
            HoverPolish.Track(this);
            SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
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

