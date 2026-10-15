using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MarkSmith.Controls;

/// <summary>
/// One row of the Settings window: icon, title and description on the left, the control (toggle,
/// combo, button) right-aligned beside them, and optional <see cref="Details"/> — extra fields that
/// belong to this setting — underneath. Every Settings page is built from these so the rows share
/// one rhythm; the old pages each hand-rolled a Grid, and toggles landed wherever their column
/// happened to fall. Usable from XAML (children become <see cref="Action"/>) and from code (the
/// Plugins page builds its cards at runtime).
/// </summary>
[ContentProperty(Name = nameof(Action))]
public sealed partial class SettingsCard : UserControl
{
    public static readonly DependencyProperty GlyphProperty = Register(nameof(Glyph), typeof(string), "");
    public static readonly DependencyProperty HeaderProperty = Register(nameof(Header), typeof(string), "");
    public static readonly DependencyProperty DescriptionProperty = Register(nameof(Description), typeof(string), "");
    public static readonly DependencyProperty ActionProperty = Register(nameof(Action), typeof(object), null);
    public static readonly DependencyProperty DetailsProperty = Register(nameof(Details), typeof(UIElement), null);

    private static DependencyProperty Register(string name, System.Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(SettingsCard),
            new PropertyMetadata(defaultValue, (d, _) => ((SettingsCard)d).Sync()));

    /// <summary>Segoe Fluent Icons code point for the leading icon; empty for none.</summary>
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    /// <summary>The setting's control, right-aligned beside the text. The XAML content property.</summary>
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }
    /// <summary>Extra fields shown under the header row, aligned with the title.</summary>
    public UIElement? Details { get => (UIElement?)GetValue(DetailsProperty); set => SetValue(DetailsProperty, value); }

    private long _detailsVisibilityToken;
    private UIElement? _watchedDetails;

    private bool _pointerOver;
    private bool _pointerPressed;

    public SettingsCard()
    {
        InitializeComponent();
        PointerEntered += (_, _) => { _pointerOver = true; UpdateState(); };
        PointerExited += (_, _) => { _pointerOver = false; _pointerPressed = false; UpdateState(); };
        PointerCanceled += (_, _) => { _pointerOver = false; _pointerPressed = false; UpdateState(); };
        PointerCaptureLost += (_, _) => { _pointerOver = false; _pointerPressed = false; UpdateState(); };
        PointerPressed += OnCardPointerPressed;
        PointerReleased += (_, _) => { _pointerPressed = false; UpdateState(); };
        Tapped += OnCardTapped;
        RightTapped += OnCardRightTapped;
        KeyDown += OnCardKeyDown;
        SizeChanged += (_, e) => UpdateActionPlacement(e.NewSize.Width);
        // IsEnabled already reaches the control inside; this greys the row's own text to match.
        IsEnabledChanged += (_, _) => UpdateState();
        Loaded += (_, _) =>
        {
            UpdateState(useTransitions: false);
            UpdateActionPlacement(ActualWidth);
        };
        Sync();
    }

    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        if (e.OriginalSource is DependencyObject source && (IsInside(source, ActionPart) || IsInside(source, DetailsPart)))
            return;
        _pointerPressed = true;
        UpdateState();
    }

    private void UpdateState(bool useTransitions = true)
    {
        var state = !IsEnabled ? "Disabled" : _pointerPressed ? "Pressed" : _pointerOver ? "PointerOver" : "Normal";
        VisualStateManager.GoToState(this, state, useTransitions);
    }

    // Clicking anywhere on the card toggles its switch or checkbox, opens its combo box, or
    // focuses its primary input, matching Windows 11 Settings.
    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        if (e.OriginalSource is DependencyObject source && (IsInside(source, ActionPart) || IsInside(source, DetailsPart)))
            return;

        if (Action is ToggleSwitch { IsEnabled: true } toggle)
        {
            toggle.IsOn = !toggle.IsOn;
            e.Handled = true;
        }
        else if (Action is CheckBox { IsEnabled: true } check)
        {
            check.IsChecked = !(check.IsChecked ?? false);
            e.Handled = true;
        }
        else if (Action is ComboBox { IsEnabled: true } combo)
        {
            combo.Focus(FocusState.Programmatic);
            combo.IsDropDownOpen = !combo.IsDropDownOpen;
            e.Handled = true;
        }
        else if (Action is NumberBox { IsEnabled: true } numberBox)
        {
            numberBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (FindFirstInput(Details as DependencyObject) is Control { IsEnabled: true } input)
        {
            input.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void OnCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        if (e.OriginalSource is DependencyObject source && (IsInside(source, ActionPart) || IsInside(source, DetailsPart)))
            return;
        ShowCardContextMenu(e.GetPosition(this));
        e.Handled = true;
    }

    private void OnCardKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                   & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (e.Key == VirtualKey.Application || (shift && !ctrl && !alt && e.Key == VirtualKey.F10))
        {
            ShowCardContextMenu(null);
            e.Handled = true;
        }
    }

    private void ShowCardContextMenu(Windows.Foundation.Point? position)
    {
        var menu = new MenuFlyout();

        if (Action is ToggleSwitch { IsEnabled: true } toggle)
        {
            var item = new MenuFlyoutItem
            {
                Text = toggle.IsOn ? "Turn off" : "Turn on",
                Icon = new FontIcon { Glyph = "\uE7E8" }
            };
            item.Click += (_, _) => toggle.IsOn = !toggle.IsOn;
            menu.Items.Add(item);
        }
        else if (Action is CheckBox { IsEnabled: true } check)
        {
            var item = new MenuFlyoutItem
            {
                Text = check.IsChecked == true ? "Uncheck" : "Check",
                Icon = new FontIcon { Glyph = "\uE73E" }
            };
            item.Click += (_, _) => check.IsChecked = !(check.IsChecked ?? false);
            menu.Items.Add(item);
        }
        else if (Action is ComboBox { IsEnabled: true } combo)
        {
            var item = new MenuFlyoutItem
            {
                Text = "Open options dropdown",
                Icon = new FontIcon { Glyph = "\uE70D" }
            };
            item.Click += (_, _) =>
            {
                combo.Focus(FocusState.Programmatic);
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
            };
            menu.Items.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(Header))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());

            var copyNameItem = new MenuFlyoutItem
            {
                Text = "Copy setting name",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyNameItem.Click += (_, _) => _ = Services.ClipboardWriter.TrySetTextAsync(Header);
            menu.Items.Add(copyNameItem);
        }

        if (!string.IsNullOrWhiteSpace(Description))
        {
            var copyDescItem = new MenuFlyoutItem
            {
                Text = "Copy setting description",
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            copyDescItem.Click += (_, _) => _ = Services.ClipboardWriter.TrySetTextAsync(Description);
            menu.Items.Add(copyDescItem);
        }

        if (menu.Items.Count == 0) return;

        if (position.HasValue)
        {
            menu.ShowAt(this, position.Value);
        }
        else
        {
            menu.ShowAt(this, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
        }
    }

    private static Control? FindFirstInput(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is TextBox or PasswordBox or ComboBox or NumberBox)
            return (Control)root;
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
                if (FindFirstInput(child) is { } found) return found;
        }
        else if (root is Border border)
        {
            return FindFirstInput(border.Child);
        }
        else if (root is ContentControl cc)
        {
            return FindFirstInput(cc.Content as DependencyObject);
        }
        return null;
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private void UpdateActionPlacement(double width)
    {
        if (ActionPart is null) return;
        var compactSwitch = Action is null or ToggleSwitch or CheckBox;
        var wrapBelow = !compactSwitch && width is > 0 and < 420;
        if (wrapBelow)
        {
            Grid.SetRow(ActionPart, 1);
            Grid.SetColumn(ActionPart, 1);
            Grid.SetColumnSpan(ActionPart, 2);
            ActionPart.Margin = new Thickness(0, 10, 0, 0);
            ActionPart.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            Grid.SetRow(ActionPart, 0);
            Grid.SetColumn(ActionPart, 2);
            Grid.SetColumnSpan(ActionPart, 1);
            ActionPart.Margin = new Thickness(16, 0, 0, 0);
            ActionPart.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void Sync()
    {
        if (IconPart is null) return; // property set before InitializeComponent

        IconPart.Glyph = Glyph ?? "";
        IconPart.Visibility = string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        HeaderPart.Text = Header ?? "";
        DescriptionPart.Text = Description ?? "";
        DescriptionPart.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        ActionPart.Content = Action;
        ActionPart.Visibility = Action is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionPlacement(ActualWidth);

        if (!ReferenceEquals(_watchedDetails, Details))
        {
            // Details often has its own Visibility binding (e.g. the password fields only while
            // encryption is on). Follow it, so a hidden body doesn't leave its 12px gap behind.
            _watchedDetails?.UnregisterPropertyChangedCallback(VisibilityProperty, _detailsVisibilityToken);
            _watchedDetails = Details;
            if (Details is not null)
                _detailsVisibilityToken = Details.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SyncDetails());
        }
        SyncDetails();

        // The row's accessible name is its title; the control inside carries its own name and help text.
        AutomationProperties.SetName(this, Header ?? "");
        AutomationProperties.SetHelpText(this, Description ?? "");
        ToolTipService.SetToolTip(this, !string.IsNullOrWhiteSpace(Description) ? $"{Header}: {Description}" : Header);
        NameUnnamed(Action as DependencyObject);
        NameUnnamed(Details as DependencyObject);
    }

    private void NameUnnamed(DependencyObject? control)
    {
        if (control is null || string.IsNullOrEmpty(Header)) return;
        if (control is Control c)
        {
            var inlineHeader = c switch
            {
                TextBox tb => tb.Header as string,
                PasswordBox pb => pb.Header as string,
                ComboBox cb => cb.Header as string,
                NumberBox nb => nb.Header as string,
                CheckBox chk => chk.Content as string,
                _ => null,
            };
            if (string.IsNullOrEmpty(AutomationProperties.GetName(c)))
                AutomationProperties.SetName(c, !string.IsNullOrEmpty(inlineHeader) ? inlineHeader : Header);
            if (!string.IsNullOrEmpty(Description) && string.IsNullOrEmpty(AutomationProperties.GetHelpText(c)))
                AutomationProperties.SetHelpText(c, Description);
            return;
        }

        if (control is Panel panel)
        {
            foreach (var child in panel.Children)
                NameUnnamed(child);
        }
        else if (control is Border border)
        {
            NameUnnamed(border.Child);
        }
    }

    private void SyncDetails()
    {
        DetailsPart.Content = Details;
        DetailsPart.Visibility = Details is { Visibility: Visibility.Visible } ? Visibility.Visible : Visibility.Collapsed;
    }
}
