using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

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

    public SettingsCard()
    {
        InitializeComponent();
        Sync();
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

        // The row's accessible name is its title; the control inside carries its own name.
        AutomationProperties.SetName(this, Header ?? "");
    }

    private void SyncDetails()
    {
        DetailsPart.Content = Details;
        DetailsPart.Visibility = Details is { Visibility: Visibility.Visible } ? Visibility.Visible : Visibility.Collapsed;
    }
}
