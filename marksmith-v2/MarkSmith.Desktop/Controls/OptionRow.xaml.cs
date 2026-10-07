using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

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

    public OptionRow()
    {
        InitializeComponent();
        Sync();
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
        NameUnnamed(Action as DependencyObject);
        NameUnnamed(Details as DependencyObject);
    }

    private void NameUnnamed(DependencyObject? control)
    {
        if (control is not Control || string.IsNullOrEmpty(Header)) return;
        if (string.IsNullOrEmpty(AutomationProperties.GetName(control)))
            AutomationProperties.SetName(control, Header);
    }

    private void SyncDetails()
    {
        DetailsPart.Content = Details;
        DetailsPart.Visibility = Details is { Visibility: Visibility.Visible } ? Visibility.Visible : Visibility.Collapsed;
    }
}
