using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MdToPdf.ViewModels;

namespace MdToPdf.Avalonia.Views
{
    public partial class SettingsView : UserControl
    {
        public void OnActivateLicense(object sender, global::Avalonia.Interactivity.RoutedEventArgs e) {}
        public void OnBuyPro(object sender, global::Avalonia.Interactivity.RoutedEventArgs e) {}
        public void OnDeactivateLicense(object sender, global::Avalonia.Interactivity.RoutedEventArgs e) {}
        public void OnCheckForUpdates(object sender, global::Avalonia.Interactivity.RoutedEventArgs e) {}

        public SettingsView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
