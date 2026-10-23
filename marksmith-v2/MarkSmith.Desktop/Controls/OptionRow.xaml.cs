using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Controls;

/// <summary>
/// One option in the main window's side panels (Style &amp; Export, Automation). It is
/// <see cref="SettingsCard"/>'s shape without the card or icon: title and description on the left,
/// the toggle right-aligned (<see cref="Action"/>), and wider controls such as combos, number boxes
/// and path pickers underneath (<see cref="Details"/>). The panels used to stack "Header / switch /
/// On / caption" with negative margins, mixed check boxes in among the toggles, and wrote the same
/// kind of label in three different cases; every option now has one rhythm.
/// </summary>
[ContentProperty(Name = nameof(Action))]
public sealed partial class OptionRow : UserControl
{
    public static readonly DependencyProperty HeaderProperty = Register(nameof(Header), typeof(string), "");
    public static readonly DependencyProperty DescriptionProperty = Register(nameof(Description), typeof(string), "");
    public static readonly DependencyProperty ActionProperty = Register(nameof(Action), typeof(object), null);
    public static readonly DependencyProperty DetailsProperty = Register(nameof(Details), typeof(UIElement), null);

    private static DependencyProperty Register(string name, System.Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(OptionRow),
            new PropertyMetadata(defaultValue, (d, _) => ((OptionRow)d).Sync()));

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    /// <summary>The option's control, right-aligned beside the text. The XAML content property.</summary>
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }
    /// <summary>A full-width control (or extra fields) under the title.</summary>
    public UIElement? Details { get => (UIElement?)GetValue(DetailsProperty); set => SetValue(DetailsProperty, value); }

    private long _detailsVisibilityToken;
    private UIElement? _watchedDetails;
    private bool _pointerOver;
    private bool _pointerPressed;

    public OptionRow()
    {
        InitializeComponent();
        HeaderRow.PointerEntered += (_, _) => { _pointerOver = true; UpdateState(); };
        HeaderRow.PointerExited += (_, _) => { _pointerOver = false; _pointerPressed = false; UpdateState(); };
        HeaderRow.PointerCanceled += (_, _) => { _pointerOver = false; _pointerPressed = false; UpdateState(); };
        HeaderRow.PointerCaptureLost += (_, _) => { _pointerOver = false; _pointerPressed = false; UpdateState(); };
        HeaderRow.PointerPressed += OnHeaderPointerPressed;
        HeaderRow.PointerReleased += (_, _) => { _pointerPressed = false; UpdateState(); };
        HeaderRow.Tapped += OnHeaderTapped;
        HeaderRow.RightTapped += OnHeaderRightTapped;
        HeaderRow.KeyDown += OnHeaderKeyDown;
        // IsEnabled already reaches the control inside; this greys the row's own text to match.
        IsEnabledChanged += (_, _) => UpdateState();
        Loaded += (_, _) => UpdateState(useTransitions: false);
        Sync();
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
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

    // Clicking an option's title or description flips its switch or check box, opens its combo box,
    // or focuses its input: the whole highlighted line is the target, not just the control at its end.
    private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        if (e.OriginalSource is DependencyObject source && (IsInside(source, ActionPart) || IsInside(source, DetailsPart))) return;
        switch (Action)
        {
            case ToggleSwitch { IsEnabled: true } toggle:
                toggle.IsOn = !toggle.IsOn;
                e.Handled = true;
                break;
            case CheckBox { IsEnabled: true } check:
                check.IsChecked = !(check.IsChecked ?? false);
                e.Handled = true;
                break;
            case ComboBox { IsEnabled: true } combo:
                combo.Focus(FocusState.Programmatic);
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
                e.Handled = true;
                break;
            case NumberBox { IsEnabled: true } numberBox:
                numberBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                break;
            case null when FindFirstInput(Details as DependencyObject) is Control { IsEnabled: true } detailsInput:
                detailsInput.Focus(FocusState.Programmatic);
                if (detailsInput is ComboBox detailsCombo)
                    detailsCombo.IsDropDownOpen = !detailsCombo.IsDropDownOpen;
                e.Handled = true;
                break;
        }
    }

    private void OnHeaderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        if (e.OriginalSource is DependencyObject source && IsInsideTextInput(source)) return;
        e.Handled = true;
        ShowOptionContextMenu(e.GetPosition(HeaderRow));
    }

    private static bool IsInsideTextInput(DependencyObject? element)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is TextBox or PasswordBox) return true;
        return false;
    }

    private void OnHeaderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        if (e.Key == Windows.System.VirtualKey.Application ||
            (e.Key == Windows.System.VirtualKey.F10 && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)))
        {
            e.Handled = true;
            ShowOptionContextMenu(null);
        }
    }

    private void ShowOptionContextMenu(Windows.Foundation.Point? position)
    {
        var menu = new MenuFlyout();

        if (Action is ToggleSwitch { IsEnabled: true } toggle)
        {
            var toggleItem = new MenuFlyoutItem
            {
                Text = toggle.IsOn ? "Turn off" : "Turn on",
                KeyboardAcceleratorTextOverride = "Space",
                Icon = new FontIcon { Glyph = toggle.IsOn ? "\uE73A" : "\uE739" }
            };
            toggleItem.Click += (_, _) => toggle.IsOn = !toggle.IsOn;
            menu.Items.Add(toggleItem);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        else if (Action is CheckBox { IsEnabled: true } check)
        {
            var checkItem = new MenuFlyoutItem
            {
                Text = (check.IsChecked ?? false) ? "Uncheck" : "Check",
                KeyboardAcceleratorTextOverride = "Space",
                Icon = new FontIcon { Glyph = (check.IsChecked ?? false) ? "\uE73A" : "\uE739" }
            };
            checkItem.Click += (_, _) => check.IsChecked = !(check.IsChecked ?? false);
            menu.Items.Add(checkItem);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        else if (Action is ComboBox { IsEnabled: true } combo)
        {
            var openComboItem = new MenuFlyoutItem
            {
                Text = "Open options dropdown",
                Icon = new FontIcon { Glyph = "\uE70D" }
            };
            openComboItem.Click += (_, _) =>
            {
                combo.Focus(FocusState.Programmatic);
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
            };
            menu.Items.Add(openComboItem);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        else if (Action is Button { IsEnabled: true } actionBtn)
        {
            var btnText = actionBtn.Content is string s && !string.IsNullOrWhiteSpace(s) ? s : "Run action";
            var runItem = new MenuFlyoutItem
            {
                Text = btnText,
                Icon = new FontIcon { Glyph = "\uE768" }
            };
            runItem.Click += (_, _) =>
            {
                if (actionBtn.Command?.CanExecute(actionBtn.CommandParameter) ?? true)
                    actionBtn.Command?.Execute(actionBtn.CommandParameter);
            };
            menu.Items.Add(runItem);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        if (!string.IsNullOrWhiteSpace(Header))
        {
            var copyHeaderItem = new MenuFlyoutItem
            {
                Text = "Copy setting name",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyHeaderItem.Click += async (_, _) =>
            {
                await MarkSmith.Services.ClipboardWriter.TrySetTextAsync(Header);
            };
            menu.Items.Add(copyHeaderItem);
        }

        if (!string.IsNullOrWhiteSpace(Description))
        {
            var copyDescItem = new MenuFlyoutItem
            {
                Text = "Copy description",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyDescItem.Click += async (_, _) =>
            {
                await MarkSmith.Services.ClipboardWriter.TrySetTextAsync(Description);
            };
            menu.Items.Add(copyDescItem);
        }

        if (menu.Items.Count == 0) return;

        if (position.HasValue)
            menu.ShowAt(HeaderRow, position.Value);
        else
            menu.ShowAt(HeaderRow);
    }

    private static Control? FindFirstInput(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ComboBox or TextBox or PasswordBox or NumberBox)
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

    private void Sync()
    {
        if (HeaderPart is null) return; // property set before InitializeComponent

        HeaderPart.Text = Header ?? "";
        HeaderPart.Visibility = string.IsNullOrEmpty(Header) ? Visibility.Collapsed : Visibility.Visible;
        DescriptionPart.Text = Description ?? "";
        DescriptionPart.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        ActionPart.Content = Action;
        ActionPart.Visibility = Action is null ? Visibility.Collapsed : Visibility.Visible;

        if (!ReferenceEquals(_watchedDetails, Details))
        {
            // Follow the details' own Visibility (e.g. the custom em-dash box), so a hidden body
            // doesn't leave its 8px gap behind.
            _watchedDetails?.UnregisterPropertyChangedCallback(VisibilityProperty, _detailsVisibilityToken);
            _watchedDetails = Details;
            if (Details is not null)
                _detailsVisibilityToken = Details.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SyncDetails());
        }
        SyncDetails();

        // The control inside carries the option's name for screen readers when it has none of
        // its own: a bare ToggleSwitch or ComboBox in a row would otherwise be announced unnamed.
        AutomationProperties.SetName(this, Header ?? "");
        AutomationProperties.SetHelpText(this, Description ?? "");
        if (!string.IsNullOrEmpty(Description))
            ToolTipService.SetToolTip(HeaderRow, Description);
        else if (!string.IsNullOrEmpty(Header))
            ToolTipService.SetToolTip(HeaderRow, Header);
        else
            ToolTipService.SetToolTip(HeaderRow, null);

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
