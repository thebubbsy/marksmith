using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MdToPdf.Views;

// Small parameterized user controls hosted inside the Insert-menu ContentDialogs (the default,
// ProMode-off experience). Each control only collects values; after the user confirms, the host
// reads the exposed properties and builds the Markdown via Services.InsertSnippetBuilder.
// Pro mode bypasses every one of these — see the On*Click handlers in MainWindow.xaml.cs.
// They are built in code (no .xaml) because they are parameterized — constructor arguments don't
// compose with XAML user controls — and they only stack standard WinUI primitives.

/// <summary>Multiline "one entry per line" collector (workflow steps, timeline entries, …).</summary>
public sealed class LinesInsertControl : UserControl
{
    private readonly TextBox _box;

    public LinesInsertControl(string caption, string defaultText, string placeholder = "")
    {
        _box = new TextBox
        {
            Text = defaultText,
            PlaceholderText = placeholder,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 130,
            MaxHeight = 240,
            FontSize = 13,
            IsSpellCheckEnabled = false,
        };
        Content = new StackPanel
        {
            Width = 420,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 },
                _box,
            },
        };
    }

    /// <summary>Non-empty lines, trimmed, in order.</summary>
    public IReadOnlyList<string> Lines =>
        _box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}

/// <summary>Dropdown type + multiline entries (charts, SmartArt).</summary>
public sealed class TypeAndLinesInsertControl : UserControl
{
    private readonly ComboBox _combo;
    private readonly LinesInsertControl _lines;

    public TypeAndLinesInsertControl(string typeLabel, IReadOnlyList<string> types, string defaultType,
        string linesCaption, string defaultLines)
    {
        _combo = new ComboBox
        {
            Header = typeLabel,
            ItemsSource = types,
            SelectedItem = defaultType,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _lines = new LinesInsertControl(linesCaption, defaultLines);
        Content = new StackPanel { Width = 420, Spacing = 10, Children = { _combo, _lines } };
    }

    public string SelectedType => _combo.SelectedItem as string ?? "";
    public IReadOnlyList<string> Lines => _lines.Lines;
}

/// <summary>Rows × columns + "include header row" checkbox (pipe tables).</summary>
public sealed class TableInsertControl : UserControl
{
    private readonly NumberBox _rows;
    private readonly NumberBox _cols;
    private readonly CheckBox _header;

    public TableInsertControl()
    {
        _rows = MakeNumberBox("Body rows", 2, 1, 50);
        _cols = MakeNumberBox("Columns", 2, 1, 20);
        _header = new CheckBox { Content = "Include header row", IsChecked = true };

        var grid = new Grid { ColumnSpacing = 12, Children = { _rows, _cols } };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_cols, 1);

        Content = new StackPanel { Width = 380, Spacing = 12, Children = { grid, _header } };
    }

    public int Rows => ClampedInt(_rows);
    public int Columns => ClampedInt(_cols);
    public bool IncludeHeaderRow => _header.IsChecked == true;

    private static NumberBox MakeNumberBox(string header, double value, double min, double max) => new()
    {
        Header = header,
        Value = value,
        Minimum = min,
        Maximum = max,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static int ClampedInt(NumberBox box)
    {
        var v = box.Value;
        if (double.IsNaN(v)) v = box.Minimum;
        return (int)Math.Round(Math.Clamp(v, box.Minimum, box.Maximum));
    }
}

/// <summary>Link text + URL.</summary>
public sealed class LinkInsertControl : UserControl
{
    private readonly TextBox _text;
    private readonly TextBox _url;

    public LinkInsertControl()
    {
        _text = new TextBox { Header = "Link text", PlaceholderText = "Read the docs" };
        _url = new TextBox { Header = "URL", PlaceholderText = "https://example.com", IsSpellCheckEnabled = false };
        Content = new StackPanel { Width = 420, Spacing = 12, Children = { _text, _url } };
    }

    public string Text => _text.Text;
    public string Url => _url.Text;
}

/// <summary>Language dropdown (free text allowed) + optional code body.</summary>
public sealed class CodeBlockInsertControl : UserControl
{
    private static readonly string[] Languages =
    {
        "csharp", "javascript", "typescript", "python", "java", "go", "rust", "sql", "json",
        "xml", "html", "css", "bash", "powershell", "yaml", "markdown",
    };

    private readonly ComboBox _lang;
    private readonly TextBox _body;

    public CodeBlockInsertControl()
    {
        _lang = new ComboBox
        {
            Header = "Language",
            ItemsSource = Languages,
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _body = new TextBox
        {
            Header = "Code (optional — you can also type inside the inserted fence)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 130,
            MaxHeight = 240,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 13,
            IsSpellCheckEnabled = false,
        };
        Content = new StackPanel { Width = 460, Spacing = 12, Children = { _lang, _body } };
    }

    public string SelectedLanguage => _lang.Text?.Trim() ?? "";
    public string Body => _body.Text;
}

/// <summary>Provider dropdown + URL (web embeds).</summary>
public sealed class EmbedInsertControl : UserControl
{
    private static readonly string[] Providers = { "youtube", "vimeo", "loom", "codepen", "bilibili" };

    private readonly ComboBox _provider;
    private readonly TextBox _url;

    public EmbedInsertControl()
    {
        _provider = new ComboBox
        {
            Header = "Provider",
            ItemsSource = Providers,
            SelectedItem = "youtube",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _url = new TextBox
        {
            Header = "Video / embed URL",
            PlaceholderText = "https://www.youtube.com/watch?v=…",
            IsSpellCheckEnabled = false,
        };
        Content = new StackPanel { Width = 420, Spacing = 12, Children = { _provider, _url } };
    }

    public string Provider => _provider.SelectedItem as string ?? "youtube";
    public string Url => _url.Text;
}

/// <summary>id / author / title / year (bibliography entry).</summary>
public sealed class ReferencesInsertControl : UserControl
{
    private readonly TextBox _id;
    private readonly TextBox _author;
    private readonly TextBox _title;
    private readonly TextBox _year;

    public ReferencesInsertControl()
    {
        _id = new TextBox { Header = "Citation id", PlaceholderText = "paper-id", IsSpellCheckEnabled = false };
        _author = new TextBox { Header = "Author", PlaceholderText = "Author Name" };
        _title = new TextBox { Header = "Title", PlaceholderText = "Publication Title" };
        _year = new TextBox { Header = "Year", PlaceholderText = "2026", IsSpellCheckEnabled = false };
        Content = new StackPanel { Width = 420, Spacing = 10, Children = { _id, _author, _title, _year } };
    }

    public string Id => _id.Text;
    public string Author => _author.Text;
    public string Title => _title.Text;
    public string Year => _year.Text;
}

/// <summary>One or more labelled number fields (column count, canvas size).</summary>
public sealed class NumbersInsertControl : UserControl
{
    private readonly List<(NumberBox Box, double Default)> _boxes = new();

    public NumbersInsertControl(params (string Label, double Value, double Min, double Max)[] fields)
    {
        var panel = new StackPanel { Width = 360, Spacing = 12 };
        foreach (var (label, value, min, max) in fields)
        {
            var box = new NumberBox
            {
                Header = label,
                Value = value,
                Minimum = min,
                Maximum = max,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            };
            _boxes.Add((box, value));
            panel.Children.Add(box);
        }
        Content = panel;
    }

    public int Value(int index)
    {
        var (box, def) = _boxes[index];
        var v = box.Value;
        if (double.IsNaN(v)) v = def;
        return (int)Math.Round(Math.Clamp(v, box.Minimum, box.Maximum));
    }
}
