using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MdToPdf.Avalonia.Controls
{
    public enum ContentDialogResult
    {
        None,
        Primary,
        Secondary
    }

    public enum ContentDialogButton
    {
        Primary,
        Secondary,
        Close
    }

    public enum InfoBarSeverity
    {
        Informational,
        Success,
        Warning,
        Error
    }

    public class ContentDialog : Window
    {
        public new string Title { get; set; }
        public new object Content { get; set; }
        public string PrimaryButtonText { get; set; }
        public string SecondaryButtonText { get; set; }
        public string CloseButtonText { get; set; }
        public ContentDialogButton DefaultButton { get; set; }
        public new global::Avalonia.Controls.ResourceDictionary Resources { get; set; } = new global::Avalonia.Controls.ResourceDictionary();

        public ContentDialog()
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 300;
        }

        public Task<ContentDialogResult> ShowAsync()
        {
            var tcs = new TaskCompletionSource<ContentDialogResult>();

            var sp = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
            if (Title != null)
                sp.Children.Add(new TextBlock { Text = Title, FontWeight = FontWeight.Bold, FontSize = 18 });
            
            if (Content is Control c)
                sp.Children.Add(c);
            else if (Content != null)
                sp.Children.Add(new TextBlock { Text = Content.ToString() });

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };

            if (!string.IsNullOrEmpty(SecondaryButtonText))
            {
                var btn = new Button { Content = SecondaryButtonText };
                btn.Click += (_, __) => { Close(); tcs.SetResult(ContentDialogResult.Secondary); };
                btnPanel.Children.Add(btn);
            }

            if (!string.IsNullOrEmpty(PrimaryButtonText))
            {
                var btn = new Button { Content = PrimaryButtonText };
                btn.Click += (_, __) => { Close(); tcs.SetResult(ContentDialogResult.Primary); };
                btnPanel.Children.Add(btn);
            }

            if (!string.IsNullOrEmpty(CloseButtonText))
            {
                var btn = new Button { Content = CloseButtonText };
                btn.Click += (_, __) => { Close(); tcs.SetResult(ContentDialogResult.None); };
                btnPanel.Children.Add(btn);
            }

            sp.Children.Add(btnPanel);
            
            this.Content = sp;

            // Find main window to use as owner
            Window owner = null;
            if (Application.Current.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                owner = desktop.MainWindow;
            }

            if (owner != null)
            {
                Dispatcher.UIThread.InvokeAsync(async () => {
                    await this.ShowDialog(owner);
                    if (!tcs.Task.IsCompleted)
                        tcs.SetResult(ContentDialogResult.None);
                });
            }
            else
            {
                this.Show();
            }

            return tcs.Task;
        }
    }

    public class InfoBar : ContentControl
    {
        public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<InfoBar, bool>("IsOpen");
        public static readonly StyledProperty<InfoBarSeverity> SeverityProperty = AvaloniaProperty.Register<InfoBar, InfoBarSeverity>("Severity");
        public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<InfoBar, string>("Title");
        public static readonly StyledProperty<string> MessageProperty = AvaloniaProperty.Register<InfoBar, string>("Message");
        public static readonly StyledProperty<bool> IsClosableProperty = AvaloniaProperty.Register<InfoBar, bool>("IsClosable");
        public static readonly StyledProperty<object> ActionButtonProperty = AvaloniaProperty.Register<InfoBar, object>("ActionButton");

        public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
        public InfoBarSeverity Severity { get => GetValue(SeverityProperty); set => SetValue(SeverityProperty, value); }
        public new string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
        public bool IsClosable { get => GetValue(IsClosableProperty); set => SetValue(IsClosableProperty, value); }
        public object ActionButton { get => GetValue(ActionButtonProperty); set => SetValue(ActionButtonProperty, value); }

        public event EventHandler<RoutedEventArgs> CloseButtonClick;

        public InfoBar()
        {
            this.Bind(IsVisibleProperty, this.GetObservable(IsOpenProperty));
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsOpenProperty || change.Property == TitleProperty || change.Property == MessageProperty || change.Property == ActionButtonProperty)
            {
                UpdateContent();
            }
        }

        private void UpdateContent()
        {
            var sp = new StackPanel { Spacing = 5, Margin = new Thickness(10) };
            if (!string.IsNullOrEmpty(Title))
                sp.Children.Add(new TextBlock { Text = Title, FontWeight = FontWeight.Bold });
            if (!string.IsNullOrEmpty(Message))
                sp.Children.Add(new TextBlock { Text = Message, TextWrapping = TextWrapping.Wrap });
            
            if (ActionButton is Control c)
            {
                if (c.Parent is global::Avalonia.Controls.Panel p) p.Children.Remove(c);
                else if (c.Parent is global::Avalonia.Controls.ContentControl cc) cc.Content = null;
                sp.Children.Add(c);
            }

            if (IsClosable)
            {
                var btn = new Button { Content = "Close" };
                btn.Click += (s, e) => {
                    IsOpen = false;
                    CloseButtonClick?.Invoke(this, e);
                };
                sp.Children.Add(btn);
            }

            var border = new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = sp
            };
            this.Content = border;
        }
    }

    public class SymbolIcon : global::Avalonia.Controls.TextBlock
    {
        public static readonly StyledProperty<string> SymbolProperty = AvaloniaProperty.Register<SymbolIcon, string>("Symbol");
        public string Symbol { get => GetValue(SymbolProperty); set { SetValue(SymbolProperty, value); Text = value; } }
    }

    public class FontIcon : global::Avalonia.Controls.TextBlock
    {
        public static readonly StyledProperty<string> GlyphProperty = AvaloniaProperty.Register<FontIcon, string>("Glyph");
        public string Glyph { get => GetValue(GlyphProperty); set { SetValue(GlyphProperty, value); Text = value; } }
    }

        public class ProgressRing : global::Avalonia.Controls.ProgressBar
    {
        public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<ProgressRing, bool>("IsActive");
        public bool IsActive
        {
            get => GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsActiveProperty)
            {
                IsIndeterminate = IsActive;
                IsVisible = IsActive;
            }
        }
    }
}
