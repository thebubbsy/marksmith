using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;

namespace MdToPdf.Avalonia.Views
{
    public partial class WelcomeTour : UserControl
    {
        public event EventHandler Completed; public bool LoadSampleRequested { get; set; }

        private int _currentIndex = 0;
        private Carousel _carousel;
        private Button _backButton;
        private Button _nextButton;
        private Button _skipButton;

        public WelcomeTour()
        {
            InitializeComponent();
            _carousel = this.FindControl<Carousel>("TourCarousel");
            _backButton = this.FindControl<Button>("BackButton");
            _nextButton = this.FindControl<Button>("NextButton");
            _skipButton = this.FindControl<Button>("SkipButton");
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnSkip(object sender, RoutedEventArgs e)
        {
            Completed?.Invoke(this, EventArgs.Empty);
        }

        private void OnBack(object sender, RoutedEventArgs e)
        {
            if (_currentIndex > 0)
            {
                _currentIndex--;
                UpdateView();
            }
        }

        private void OnNext(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 4) // Assuming 5 slides
            {
                _currentIndex++;
                UpdateView();
            }
            else
            {
                Completed?.Invoke(this, EventArgs.Empty);
            }
        }

        private void UpdateView()
        {
            if (_carousel != null)
                _carousel.SelectedIndex = _currentIndex;
            
            if (_backButton != null)
                _backButton.IsVisible = _currentIndex > 0;
                
            if (_nextButton != null)
                _nextButton.Content = _currentIndex == 4 ? "Get Started" : "Next";
        }
    }
}
