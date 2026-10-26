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
    public sealed partial class ExtensionTip : UserControl
    {
        public event EventHandler? GetExtensionRequested;

        private bool _isHovered;

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
                LiveAnnounce.Raise(control);
            }
        }

        public ExtensionTip()
        {
            this.InitializeComponent();
            // "Right-click for options" menus: open them from Shift+F10 / the Menu key too.
            KeyboardContextMenu.BridgeAll(
                (DismissButton, OnDismissButtonRightTapped),
                (GetExtensionButton, OnGetExtensionButtonRightTapped));
            this.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
            AddHandler(KeyDownEvent, new KeyEventHandler(OnTipKeyDown), true);
            RightTapped += OnTipRightTapped;
            TipBorder.PointerEntered += (_, _) => { _isHovered = true; RefreshBorderBrush(); };
            TipBorder.PointerExited += (_, _) => { _isHovered = false; RefreshBorderBrush(); };
            TipBorder.PointerCanceled += (_, _) => { _isHovered = false; RefreshBorderBrush(); };
            GotFocus += (_, _) => RefreshBorderBrush();
            LostFocus += (_, _) => DispatcherQueue?.TryEnqueue(RefreshBorderBrush);
            HoverPolish.Track(this);
        }

        private void RefreshBorderBrush()
        {
            var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            var hasFocusInside = focused is not null && IsInside(focused, this);
            ThemeBrush.Set(TipBorder, Border.BorderBrushProperty,
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

        private void OnTipKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
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
                // Get extension / Dismiss have their own menus; don't open a second one over them.
                if (KeyboardContextMenu.IsFromBridgedControl(e)) return;
                ShowTipContextMenu(null);
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
                CopyExtensionInfo();
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

        private void OnTipRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ShowTipContextMenu(KeyboardContextMenu.PointIn(e, this));
        }

        private void ShowTipContextMenu(Windows.Foundation.Point? position)
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
                Text = "Copy extension info",
                Icon = new FontIcon { Glyph = "\uE8C8" },
                KeyboardAcceleratorTextOverride = "Ctrl+C"
            };
            copyInfoItem.Click += (_, _) => CopyExtensionInfo();
            menu.Items.Add(copyInfoItem);

            var dismissItem = new MenuFlyoutItem
            {
                Text = "Dismiss tip",
                KeyboardAcceleratorTextOverride = "Esc",
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
            ShowTipContextMenu(KeyboardContextMenu.PointIn(e, this));
        }

        private void OnDismissButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ShowTipContextMenu(KeyboardContextMenu.PointIn(e, this));
        }

        private async void CopyExtensionInfo()
        {
            const string info = "MarkSmith Connector: Install the browser extension in Edge or Chrome to send ChatGPT, Gemini and Claude replies straight here.";
            var error = await Services.ClipboardWriter.TrySetTextAsync(info);
            if (GetExtensionButton is not null)
            {
                Services.CopyFeedback.ApplyButtonTick(GetExtensionButton, error, "\u2713 Copied");
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
            Dismiss();
        }

        private void Dismiss()
        {
            var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            var hadFocusInside = focused is not null && IsInside(focused, this);
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
            if (hadFocusInside)
            {
                HoverPolish.RestoreFocus(XamlRoot, null);
            }
        }
    }
}
