using System;
using Microsoft.UI.Xaml.Controls;
using MdToPdf.ViewModels;

namespace MdToPdf.Views;

public sealed partial class WelcomeTour : UserControl
{
    public event EventHandler? Completed;

    public WelcomeTourViewModel ViewModel { get; } = new();

    public bool LoadSampleRequested => ViewModel.LoadSampleRequested;

    public WelcomeTour()
    {
        InitializeComponent();
        ViewModel.TourCompleted += () => Completed?.Invoke(this, EventArgs.Empty);
    }
}
