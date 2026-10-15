using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MarkSmith.Services;

namespace MarkSmith.Controls
{
    public sealed partial class ExtensionHintBar : UserControl
    {
        public event EventHandler? GetExtensionRequested;

        private bool _isHovered;

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
            RightTapped += OnBarRightTapped;
            HintBorder.PointerEntered += (_, _) => { _isHovered = true; RefreshBorderBrush(); };
            HintBorder.PointerExited += (_, _) => { _isHovered = false; RefreshBorderBrush(); };
            HintBorder.PointerCanceled += (_, _) => { _isHovered = false; RefreshBorderBrush(); };
            GotFocus += (_, _) => RefreshBorderBrush();
            LostFocus += (_, _) => DispatcherQueue?.TryEnqueue(RefreshBorderBrush);
            HoverPolish.Track(this);
            SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        }

        private void RefreshBorderBrush()
        {
            var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            var hasFocusInside = focused is not null && IsInside(focused, this);
            ThemeBrush.Set(HintBorder, Border.BorderBrushProperty,
                hasFocusInside ? "AccentFillColorDefaultBrush"
                : _isHovered ? "ControlStrongStrokeColorDefaultBrush"
                : "CardStrokeColorDefaultBrush");
        }

        private static bool IsInside(DependencyObject element, DependencyObject ancestor)
        {
            for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
                if (ReferenceEquals(d, ancestor)) return true;
            return false;
        }

        private void OnBarKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (!IsOpen) return;

            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (e.Key == Windows.System.VirtualKey.Application || (shift && !ctrl && !alt && e.Key == Windows.System.VirtualKey.F10))
            {
                ShowHintContextMenu(null);
                e.Handled = true;
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Dismiss();
                e.Handled = true;
                return;
            }

            if (ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.C)
            {
                CopyHintInfo();
                e.Handled = true;
                return;
            }

            var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            if (!ctrl && !shift && !alt && focused is not null)
            {
                if ((e.Key == Windows.System.VirtualKey.Right || e.Key == Windows.System.VirtualKey.Up)
                    && IsInside(focused, GetExtensionButton))
                {
                    DismissButton.Focus(FocusState.Keyboard);
                    e.Handled = true;
                }
                else if ((e.Key == Windows.System.VirtualKey.Left || e.Key == Windows.System.VirtualKey.Down)
                         && IsInside(focused, DismissButton))
                {
                    GetExtensionButton.Focus(FocusState.Keyboard);
                    e.Handled = true;
                }
            }
        }

        private void OnBarRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ShowHintContextMenu(e.GetPosition(this));
        }

        private void ShowHintContextMenu(Windows.Foundation.Point? position)
        {
            var menu = new MenuFlyout();
            var getItem = new MenuFlyoutItem
            {
                Text = "Get the extension…",
                Icon = new FontIcon { Glyph = "\uEA86" }
            };
            getItem.Click += OnGetExtensionClick;
            menu.Items.Add(getItem);

            var copyInfoItem = new MenuFlyoutItem
            {
                Text = "Copy hint info",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                KeyboardAcceleratorTextOverride = "Ctrl+C"
            };
            copyInfoItem.Click += (_, _) => CopyHintInfo();
            menu.Items.Add(copyInfoItem);

            var dismissItem = new MenuFlyoutItem
            {
                Text = "Dismiss hint (Esc)",
                Icon = new FontIcon { Glyph = "\uE711" }
            };
            dismissItem.Click += OnCloseClick;
            menu.Items.Add(dismissItem);

            if (position.HasValue)
                menu.ShowAt(this, position.Value);
            else
                menu.ShowAt(this);
        }

        private void OnGetExtensionButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ShowHintContextMenu(e.GetPosition(this));
        }

        private void OnDismissButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ShowHintContextMenu(e.GetPosition(this));
        }

        private void CopyHintInfo()
        {
            const string info = "MarkSmith Connector: That paste looks like plain text. Use the MarkSmith Connector's 'Copy as Markdown' button in your AI chat to keep full formatting.";
            _ = Services.ClipboardWriter.TrySetTextAsync(info);
            if (GetExtensionButton is not null)
            {
                Services.CopyFeedback.ApplyButtonTick(GetExtensionButton, null, "\u2713 Copied");
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
            Dismiss();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Dismiss();
        }

        private void Dismiss()
        {
            var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            var hadFocusInside = focused is not null && IsInside(focused, this);
            IsOpen = false;
            if (hadFocusInside)
            {
                HoverPolish.RestoreFocus(XamlRoot, null);
            }
        }
    }
}

