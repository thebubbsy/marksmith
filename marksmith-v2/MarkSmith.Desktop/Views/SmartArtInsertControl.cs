using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using SmartArt = MarkSmith.Services.SmartArtInsert;
using MarkSmith.Services;

namespace MarkSmith.Views;

/// <summary>
/// Insert ▸ SmartArt. A gallery of the common Word layouts (the same miniatures SmartArt Studio
/// shows), worked examples, and an indented outline that becomes the <c>:::smartart</c> block.
/// </summary>
/// <remarks>
/// This used to be a one-off XAML control outside the shared insert shell: four hand-drawn
/// buttons whose "selected" look was swapping the accent style on and off (a screen reader
/// couldn't tell which was chosen), emoji template chips, no Insert-disabled state, and every
/// line trimmed, so a hierarchy could never be built. It's now an <see cref="InsertDialogBody"/>
/// like every other insert dialog: a real single-selection GridView, the shared "Inserts" card,
/// and indentation kept (see <see cref="SmartArt.Parse"/>).
/// </remarks>
public sealed class SmartArtInsertControl : InsertDialogBody
{
    private readonly GridView _gallery;
    private readonly TextBlock _layoutCaption;
    private readonly TextBox _box;
    private readonly TextBlock _count = CountBadge();
    private readonly Grid _advice;
    private readonly TextBlock _adviceText;
    private readonly TextBlock _indentHint;
    private SmartArt.Layout _layout = SmartArt.Find(SmartArt.DefaultAlias)!;

    public SmartArtInsertControl()
        : base("A diagram drawn from an outline. The preview draws it live, and Word export makes it real, editable SmartArt.")
    {
        // ---- Layout gallery ------------------------------------------------------------------
        var examples = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var example in SmartArt.Examples)
        {
            var layoutName = SmartArt.Find(example.Alias)?.Name ?? example.Alias;
            var item = new MenuFlyoutItem
            {
                Text = example.Name,
                Icon = new FontIcon { Glyph = example.Glyph },
            };
            ToolTipService.SetToolTip(item, $"Replaces the outline and picks {layoutName}");
            item.Click += (_, _) => ApplyExample(example);
            examples.Items.Add(item);
        }
        var examplesButton = new DropDownButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new FontIcon { Glyph = "", FontSize = 14 }, new TextBlock { Text = "Examples" } },
            },
            Flyout = examples,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(examplesButton, "Start from an example");
        ToolTipService.SetToolTip(examplesButton, "Start from an example: fills in a layout and an outline that suits it");

        var galleryHeader = new Grid();
        galleryHeader.Children.Add(new TextBlock
        {
            Text = "Layout",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        galleryHeader.Children.Add(examplesButton);

        _gallery = new GridView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, -4, 0),
        };
        AutomationProperties.SetName(_gallery, "Layout");
        ScrollViewer.SetVerticalScrollBarVisibility(_gallery, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(_gallery, ScrollMode.Disabled);
        foreach (var layout in SmartArt.Layouts) _gallery.Items.Add(Tile(layout));
        _gallery.SelectionChanged += (_, _) =>
        {
            if (_gallery.SelectedItem is GridViewItem { Tag: SmartArt.Layout picked } && picked != _layout)
            {
                _layout = picked;
                Update();
            }
        };

        // The gallery shows pictures only (as Word's does), so the chosen layout's name and what
        // it's for are spelled out underneath.
        _layoutCaption = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        AddField(new StackPanel { Spacing = 6, Children = { galleryHeader, _gallery, _layoutCaption } });

        // ---- Outline -------------------------------------------------------------------------
        _box = MultilineBox("", "Plan\n  Set goals\nDo\nCheck\nAct", monospace: true);
        _box.MinHeight = 112;
        AutomationProperties.SetName(_box, "Outline");
        AutomationProperties.SetHelpText(_box, "One item per line. Alt+Shift+Right indents the line, Alt+Shift+Left outdents it.");
        _box.TextChanged += (_, _) => Update();
        // Word's own outline keys. Tab is left alone so it still moves focus out of the box.
        _box.AddHandler(KeyDownEvent, new KeyEventHandler(OnOutlineKeyDown), true);

        var outlineHeader = new Grid();
        outlineHeader.Children.Add(new TextBlock { Text = "Outline — one item per line", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        outlineHeader.Children.Add(_count);

        var indent = IndentButton("", "Indent", "Indent the line: put it under the one above (Alt+Shift+Right)", +1);
        var outdent = IndentButton("", "Outdent", "Outdent the line (Alt+Shift+Left)", -1);
        var indentRow = new Grid { ColumnSpacing = 4 };
        indentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        indentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        indentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        indentRow.Children.Add(_indentHint = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"));
        Grid.SetColumn(outdent, 1);
        Grid.SetColumn(indent, 2);
        indentRow.Children.Add(outdent);
        indentRow.Children.Add(indent);

        // Advice, not an error: it never disables Insert (only an empty outline does).
        _adviceText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _advice = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed,
        }.Themed(Grid.BackgroundProperty, "SystemFillColorAttentionBackgroundBrush");
        _advice.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _advice.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _advice.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        }.Themed(FontIcon.ForegroundProperty, "AccentTextFillColorPrimaryBrush"));
        Grid.SetColumn(_adviceText, 1);
        _advice.Children.Add(_adviceText);
        AutomationProperties.SetLiveSetting(_adviceText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        AddField(new StackPanel { Spacing = 6, Children = { outlineHeader, _box, indentRow, _advice } });

        ApplyExample(SmartArt.Examples[0]);
    }

    /// <summary>The Markdown to insert (kept under its old name for the host).</summary>
    public string GeneratedSnippet => Snippet;

    public override string Snippet => SmartArt.Build(_layout.Alias, _box.Text);

    protected override string? Problem
    {
        get
        {
            var rows = SmartArt.Parse(_box.Text);
            _count.Text = SmartArt.Describe(_layout, rows);
            return rows.Count == 0 ? "Add at least one item to the outline." : null;
        }
    }

    private void Update()
    {
        _layoutCaption.Text = $"{_layout.Name} — {_layout.Hint}";
        _indentHint.Text = SmartArt.IndentHint(_layout);
        var advice = SmartArt.Advice(_layout, SmartArt.Parse(_box.Text));
        _adviceText.Text = advice ?? "";
        _advice.Visibility = advice is null ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    private void ApplyExample(SmartArt.Example example)
    {
        var layout = SmartArt.Find(example.Alias) ?? _layout;
        _layout = layout;
        _gallery.SelectedItem = _gallery.Items.OfType<GridViewItem>().FirstOrDefault(i => Equals(i.Tag, layout));
        _box.Text = example.Text;
        Update();
    }

    private GridViewItem Tile(SmartArt.Layout layout)
    {
        // The drawing's colours are made for a white page, so the miniature sits on a light tile
        // in both themes (as in SmartArt Studio's gallery).
        var tile = new Border
        {
            Width = 64,
            Height = 44,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF8, 0xF9, 0xFA)),
            Child = new Image
            {
                Source = (ImageSource?)new Converters.SvgMarkupToImageSourceConverter().Convert(layout.ThumbnailSvg, typeof(ImageSource), "112", ""),
                Stretch = Stretch.Uniform,
            },
        };
        AutomationProperties.SetAccessibilityView(tile.Child, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var item = new GridViewItem
        {
            Tag = layout,
            Content = tile,
            Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 4, 4),
            MinWidth = 0,
            MinHeight = 0,
        };
        AutomationProperties.SetName(item, layout.Name);
        AutomationProperties.SetHelpText(item, layout.Hint);
        ToolTipService.SetToolTip(item, $"{layout.Name}\n{layout.Hint}");
        return item;
    }

    private Button IndentButton(string glyph, string name, string tip, int direction)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(8, 5, 8, 5),
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) =>
        {
            ShiftLines(direction);
            _box.Focus(FocusState.Programmatic);
        };
        return button;
    }

    private void OnOutlineKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!alt || !shift || e.Key is not (VirtualKey.Right or VirtualKey.Left)) return;
        ShiftLines(e.Key == VirtualKey.Right ? +1 : -1);
        e.Handled = true;
    }

    /// <summary>Indents (+1) or outdents (-1) every line the selection touches by two spaces,
    /// keeping the caret and selection on the same text.</summary>
    private void ShiftLines(int direction)
    {
        var text = _box.Text ?? "";
        var start = Math.Clamp(_box.SelectionStart, 0, text.Length);
        var end = Math.Clamp(start + _box.SelectionLength, 0, text.Length);
        var edit = SmartArt.ShiftLines(text, start, end, direction);
        if (edit.Text == text) return;
        _box.Text = edit.Text;
        _box.Select(edit.SelectionStart, edit.SelectionLength);
    }
}
