using System;
using Microsoft.UI.Xaml.Controls;
using MdToPdf.ViewModels;

namespace MdToPdf.Views;

public sealed partial class SettingsView : UserControl
{
    public event Action? PluginsChanged;
    public MainViewModel MainViewModel => App.ViewModel;
    public SettingsViewModel SettingsVM { get; } = new();

    public SettingsView()
    {
        InitializeComponent();
        
        // Pass the plugin changed event up to the MainWindow
        SettingsVM.PluginsChanged += () => PluginsChanged?.Invoke();
    }
}

