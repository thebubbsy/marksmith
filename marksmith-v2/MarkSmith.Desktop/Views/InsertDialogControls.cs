using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Snippets = MarkSmith.Services.InsertSnippetBuilder;
using MarkSmith.Services;

namespace MarkSmith.Views;

// Small parameterized user controls hosted inside the Insert-menu ContentDialogs (the default,
// ProMode-off experience). Each control collects values and builds its own Markdown via
// Services.InsertSnippetBuilder; the host inserts InsertDialogBody.Snippet after the user confirms.
// Quick insert (the ProMode setting) bypasses every one of these — see the On*Click handlers in MainWindow.xaml.cs.
// They are built in code (no .xaml) because they are parameterized — constructor arguments don't
// compose with XAML user controls — and they only stack standard WinUI primitives.

/// <summary>
/// Shared shell for every Insert dialog: a one-line description of the block, the fields, and a
/// live "Inserts" card showing the exact Markdown that will land in the document (the pattern the
/// SmartArt dialog already had). A control that can't produce a usable block says why and reports
/// <see cref="IsValid"/> false so the host can disable Insert.
/// </summary>
public abstract class InsertDialogBody : UserControl
{
    public const double BodyWidth = 460;

    private readonly StackPanel _fields = new() { Spacing = 12 };
    private readonly TextBlock _preview;
    private readonly TextBlock _problem;
    private bool _valid = true;
    private bool _opened;

    protected InsertDialogBody(string description)
    {
        _preview = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _problem = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        }.Themed(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");

        var previewCard = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 10),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Inserts", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.65 },
                    new ScrollViewer { Content = _preview, MaxHeight = 132, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                },
            },
        }.Themed(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush").Themed(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");

        Content = new StackPanel
        {
            Width = BodyWidth,
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"),
                _fields,
                _problem,
                previewCard,
            },
        };

        // Until the dialog has opened, a problem is the starting state, not a mistake: it shows in
        // the secondary text colour (Insert stays disabled) and turns red only once the person edits.
        Loaded += (_, _) =>
        {
            Refresh();
            _opened = true;
        };
        Services.HoverPolish.Track(this);
    }

    /// <summary>The Markdown this dialog inserts with its current values.</summary>
    public abstract string Snippet { get; }

    /// <summary>Whether the current values make a block worth inserting.</summary>
    public bool IsValid => _valid;

    /// <summary>Raised when <see cref="IsValid"/> flips, so the host can enable/disable Insert.</summary>
    public event Action<bool>? ValidityChanged;

    /// <summary>Why the values can't be inserted, or null when they can. Drives the red hint.</summary>
    protected virtual string? Problem => null;

    protected void AddField(UIElement element) => _fields.Children.Add(element);

    /// <summary>Re-renders the preview card and re-checks validity. Call from every field change.</summary>
    protected void Refresh()
    {
        var problem = Problem;
        _problem.Text = problem ?? "";
        _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        _problem.Foreground = (Brush)Application.Current.Resources[_opened ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        // An empty card read as broken; say plainly that nothing has been chosen yet.
        var snippet = Snippet.Trim('\n');
        _preview.Text = snippet.Length == 0 ? "Nothing yet" : snippet;
        _preview.Opacity = snippet.Length == 0 ? 0.6 : 1;
        var valid = problem is null;
        if (valid != _valid)
        {
            _valid = valid;
            ValidityChanged?.Invoke(valid);
        }
    }

    /// <summary>Puts the caret in the first field and selects its text, so typing replaces the
    /// sample values instead of appending to them. Called by the host once the dialog is open.</summary>
    public void FocusFirstField()
    {
        var first = FirstInput(_fields);
        if (first is null) return;
        first.Focus(FocusState.Programmatic);
        if (first is TextBox box) box.SelectAll();
    }

    private static Control? FirstInput(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            switch (child)
            {
                case TextBox or NumberBox or ComboBox or RadioButtons: return (Control)child;
                case Panel inner when FirstInput(inner) is { } found: return found;
            }
        }
        return null;
    }

    protected static NumberBox MakeNumberBox(string header, double value, double min, double max) => new()
    {
        Header = header,
        Value = value,
        Minimum = min,
        Maximum = max,
        SmallChange = 1,
        LargeChange = 5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>Integer value of a NumberBox, clamped to its range; a cleared box reads as
    /// <paramref name="fallback"/>.</summary>
    protected static int IntOf(NumberBox box, double fallback)
    {
        var v = box.Value;
        if (double.IsNaN(v)) v = fallback;
        return (int)Math.Round(Math.Clamp(v, box.Minimum, box.Maximum));
    }

    protected static TextBlock CountBadge() => new()
    {
        FontSize = 12,
        Opacity = 0.6,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    protected static TextBox MultilineBox(string text, string placeholder, bool monospace = false)
    {
        // AcceptsReturn must be set before Text: a single-line TextBox keeps only the first line of
        // whatever it is given, so the multi-line samples used to open as just "Step 1".
        var box = new TextBox
        {
            AcceptsReturn = true,
            Text = text,
            PlaceholderText = placeholder,
            TextWrapping = monospace ? TextWrapping.NoWrap : TextWrapping.Wrap,
            MinHeight = 120,
            MaxHeight = 220,
            FontSize = 13,
            IsSpellCheckEnabled = false,
        };
        if (monospace) box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        return box;
    }

    // A WinUI TextBox separates lines with a bare '\r', so it counts as a line break here, not
    // noise to strip (stripping it ran "Step 1Step 2Step 3" into one entry).
    protected static IReadOnlyList<string> NonEmptyLines(string text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}

/// <summary>Multiline "one entry per line" collector (workflow steps, timeline entries, tabs, …).</summary>
public sealed class LinesInsertControl : InsertDialogBody
{
    private readonly TextBox _box;
    private readonly TextBlock _count = CountBadge();
    private readonly Func<IReadOnlyList<string>, string> _build;
    private readonly string _noun;
    private readonly int _minimum;
    private readonly Func<string, bool>? _lineIsValid;
    private readonly string? _lineHint;

    /// <param name="noun">Singular name of one entry ("step"), used in the count and the hints.</param>
    /// <param name="minimum">Entries needed before Insert is enabled.</param>
    /// <param name="lineIsValid">Optional per-line shape check; failing lines are named in the hint.</param>
    public LinesInsertControl(string description, string header, string defaultText,
        Func<IReadOnlyList<string>, string> build, string noun, int minimum = 1,
        string placeholder = "", Func<string, bool>? lineIsValid = null, string? lineHint = null)
        : base(description)
    {
        _build = build;
        _noun = noun;
        _minimum = minimum;
        _lineIsValid = lineIsValid;
        _lineHint = lineHint;
        _box = MultilineBox(defaultText, placeholder);
        _box.TextChanged += (_, _) => Refresh();

        var headerRow = new Grid();
        headerRow.Children.Add(new TextBlock { Text = header, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        headerRow.Children.Add(_count);
        AddField(new StackPanel { Spacing = 6, Children = { headerRow, _box } });
    }

    /// <summary>Non-empty lines, trimmed, in order.</summary>
    public IReadOnlyList<string> Lines => NonEmptyLines(_box.Text);

    public override string Snippet => _build(Lines);

    protected override string? Problem
    {
        get
        {
            var lines = Lines;
            _count.Text = $"{lines.Count} {_noun}{(lines.Count == 1 ? "" : "s")}";
            if (lines.Count < _minimum)
                return _minimum == 1 ? $"Add at least one {_noun}." : $"Add at least {_minimum} {_noun}s.";
            if (_lineIsValid is not null)
            {
                var bad = lines.Select((l, i) => (l, i)).Where(x => !_lineIsValid(x.l)).Select(x => x.i + 1).ToList();
                if (bad.Count > 0)
                    return $"Line{(bad.Count == 1 ? "" : "s")} {string.Join(", ", bad.Take(5))}{(bad.Count > 5 ? "…" : "")}: {_lineHint}";
            }
            return null;
        }
    }
}

/// <summary>Bar / line / pie picker + "label,value" rows (native chart).</summary>
public sealed class ChartInsertControl : InsertDialogBody
{
    private static readonly (string Label, string Id)[] Types = { ("Bar", "bar"), ("Line", "line"), ("Pie", "pie") };

    private readonly RadioButtons _type;
    private readonly TextBox _box;
    private readonly TextBlock _count = CountBadge();

    public ChartInsertControl()
        : base("A chart drawn from your numbers — in the preview, the PDF and as a native, editable Word chart.")
    {
        _type = new RadioButtons { Header = "Chart type", MaxColumns = 3, SelectedIndex = 0 };
        foreach (var (label, _) in Types) _type.Items.Add(label);
        _type.SelectionChanged += (_, _) => Refresh();

        _box = MultilineBox("Q1,10\nQ2,25\nQ3,15", "Label,Value");
        _box.TextChanged += (_, _) => Refresh();
        var headerRow = new Grid();
        headerRow.Children.Add(new TextBlock { Text = "Data — one label,value per line", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        headerRow.Children.Add(_count);

        AddField(_type);
        AddField(new StackPanel { Spacing = 6, Children = { headerRow, _box } });
    }

    public string SelectedType => Types[Math.Max(0, _type.SelectedIndex)].Id;
    public IReadOnlyList<string> Lines => NonEmptyLines(_box.Text);
    public override string Snippet => Snippets.Chart(SelectedType, Lines);

    private static bool IsPoint(string line)
    {
        var comma = line.LastIndexOf(',');
        return comma > 0 && double.TryParse(line[(comma + 1)..].Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    protected override string? Problem
    {
        get
        {
            var lines = Lines;
            var points = lines.Count(IsPoint);
            _count.Text = $"{points} point{(points == 1 ? "" : "s")}";
            if (points == 0) return "Add at least one row like Q1,10 — a label, a comma, then a number.";
            // A first line that isn't data is read as a header row, the same rule the renderers use.
            var bad = lines.Select((l, i) => (l, i)).Skip(1).Where(x => !IsPoint(x.l)).Select(x => x.i + 1).ToList();
            return bad.Count == 0 ? null
                : $"Line{(bad.Count == 1 ? "" : "s")} {string.Join(", ", bad.Take(5))} {(bad.Count == 1 ? "isn't" : "aren't")} label,number and would be skipped.";
        }
    }
}

/// <summary>Rows × columns + "include header row" checkbox (pipe tables).</summary>
public sealed class TableInsertControl : InsertDialogBody
{
    private readonly NumberBox _rows;
    private readonly NumberBox _cols;
    private readonly CheckBox _header;

    public TableInsertControl()
        : base("A Markdown table with placeholder cells. Type over them once it's in the document.")
    {
        _rows = MakeNumberBox("Body rows", 2, 1, 50);
        _cols = MakeNumberBox("Columns", 2, 1, 20);
        _header = new CheckBox { Content = "Include header row", IsChecked = true };
        _rows.ValueChanged += (_, _) => Refresh();
        _cols.ValueChanged += (_, _) => Refresh();
        _header.Click += (_, _) => Refresh();

        var grid = new Grid { ColumnSpacing = 12, Children = { _rows, _cols } };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_cols, 1);
        AddField(grid);
        AddField(_header);
    }

    public int Rows => IntOf(_rows, 2);
    public int Columns => IntOf(_cols, 2);
    public bool IncludeHeaderRow => _header.IsChecked == true;
    public override string Snippet => Snippets.Table(Rows, Columns, IncludeHeaderRow);
}

/// <summary>Link text + URL.</summary>
public sealed class LinkInsertControl : InsertDialogBody
{
    private readonly TextBox _text;
    private readonly TextBox _url;

    public LinkInsertControl(string initialText = "")
        : base("A clickable link. Leave the text empty to type it in the document instead.")
    {
        _text = new TextBox { Header = "Link text", PlaceholderText = "Read the docs", Text = initialText };
        _url = new TextBox { Header = "URL", PlaceholderText = "https://example.com", IsSpellCheckEnabled = false, InputScope = UrlScope() };
        _text.TextChanged += (_, _) => Refresh();
        _url.TextChanged += (_, _) => Refresh();
        AddField(_text);
        AddField(_url);
    }

    public string Text => _text.Text;
    public string Url => _url.Text;
    public override string Snippet => Snippets.Link(string.IsNullOrWhiteSpace(Text) ? "" : Text, Url);

    // Relative links (#heading, ./other.md) are legitimate; only a scheme-less web address
    // ("example.com") is flagged, because it would resolve as a relative file path.
    protected override string? Problem
    {
        get
        {
            var url = Url.Trim();
            if (url.Length == 0 || url.Contains("://") || url.StartsWith('#') || url.StartsWith('.') || url.StartsWith('/') || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return null;
            return url.Contains('.') && !url.Contains(' ') && !url.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? $"Add https:// — without it “{url}” opens as a file next to the document."
                : null;
        }
    }

    internal static Microsoft.UI.Xaml.Input.InputScope UrlScope()
    {
        var scope = new Microsoft.UI.Xaml.Input.InputScope();
        scope.Names.Add(new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Url));
        return scope;
    }
}

/// <summary>Language picker (free text allowed) + optional code body.</summary>
public sealed class CodeBlockInsertControl : InsertDialogBody
{
    // Display name → fence id. The picker shows the names; typing a name or an id both work.
    private static readonly (string Name, string Id)[] Languages =
    {
        ("Plain text", ""), ("C#", "csharp"), ("JavaScript", "javascript"), ("TypeScript", "typescript"),
        ("Python", "python"), ("Java", "java"), ("Go", "go"), ("Rust", "rust"), ("C++", "cpp"),
        ("SQL", "sql"), ("JSON", "json"), ("XML", "xml"), ("HTML", "html"), ("CSS", "css"),
        ("Bash", "bash"), ("PowerShell", "powershell"), ("YAML", "yaml"), ("Markdown", "markdown"),
        ("Mermaid diagram", "mermaid"),
    };

    private readonly ComboBox _lang;
    private readonly TextBox _body;

    public CodeBlockInsertControl()
        : base("A fenced code block with syntax highlighting. Leave the code empty to type it inside the fence.")
    {
        _lang = new ComboBox
        {
            Header = "Language",
            ItemsSource = Languages.Select(l => l.Name).ToList(),
            SelectedIndex = 0,
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _lang.SelectionChanged += (_, _) => Refresh();
        _lang.TextSubmitted += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        _body = MultilineBox("", "Paste or type code (optional)", monospace: true);
        _body.Header = "Code";
        _body.TextChanged += (_, _) => Refresh();
        AddField(_lang);
        AddField(_body);
    }

    /// <summary>Fence language id: a picked or typed display name maps to its id; anything else
    /// typed is used as-is (lowercased, spaces removed) so uncommon languages still work.</summary>
    public string SelectedLanguage
    {
        get
        {
            var text = (_lang.SelectedItem as string ?? _lang.Text ?? "").Trim();
            var hit = Languages.FirstOrDefault(l => l.Name.Equals(text, StringComparison.OrdinalIgnoreCase)
                                                 || (l.Id.Length > 0 && l.Id.Equals(text, StringComparison.OrdinalIgnoreCase)));
            return hit.Name is not null ? hit.Id : text.ToLowerInvariant().Replace(" ", "");
        }
    }

    public string Body => _body.Text;
    public override string Snippet => Snippets.CodeBlock(SelectedLanguage, Body.Replace("\r", "\n"));
}

/// <summary>Provider picker + URL (web embeds). Pasting a known URL picks its provider.</summary>
public sealed class EmbedInsertControl : InsertDialogBody
{
    private static readonly string[] Providers = { "youtube", "vimeo", "loom", "codepen", "bilibili" };

    private readonly ComboBox _provider;
    private readonly TextBox _url;

    public EmbedInsertControl()
        : base("A video card that opens the video. Documents and PDFs can't play video, so readers click through.")
    {
        _provider = new ComboBox
        {
            Header = "Provider",
            ItemsSource = Providers.Select(Services.ContainerBlockParsers.ProviderDisplayName).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _provider.SelectionChanged += (_, _) => Refresh();
        _url = new TextBox
        {
            Header = "Video URL",
            PlaceholderText = "https://www.youtube.com/watch?v=…",
            IsSpellCheckEnabled = false,
            InputScope = LinkInsertControl.UrlScope(),
        };
        _url.TextChanged += (_, _) =>
        {
            var detected = Services.ContainerBlockParsers.DetectProvider(_url.Text);
            var index = detected is null ? -1 : Array.IndexOf(Providers, detected);
            if (index >= 0 && index != _provider.SelectedIndex) _provider.SelectedIndex = index;   // refreshes
            else Refresh();
        };
        AddField(_url);
        AddField(_provider);
    }

    public string Provider => Providers[Math.Max(0, _provider.SelectedIndex)];
    public string Url => _url.Text.Trim();
    public override string Snippet => Snippets.Embed(Provider, Url);

    protected override string? Problem
    {
        get
        {
            if (Url.Length == 0) return "Paste the video's link.";
            return Uri.TryCreate(Url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http")
                ? null
                : "That isn't a full web link — it should start with https://";
        }
    }
}

/// <summary>id / author / title / year (bibliography entry).</summary>
public sealed class ReferencesInsertControl : InsertDialogBody
{
    private readonly TextBox _id;
    private readonly TextBox _author;
    private readonly TextBox _title;
    private readonly TextBox _year;

    public ReferencesInsertControl()
        : base("A bibliography entry, listed under a Bibliography heading. Empty fields get placeholders to fill in later.")
    {
        _author = new TextBox { Header = "Author", PlaceholderText = "Surname, Initial." };
        _title = new TextBox { Header = "Title", PlaceholderText = "Publication title" };
        _year = new TextBox { Header = "Year", PlaceholderText = DateTime.Now.Year.ToString(), IsSpellCheckEnabled = false, InputScope = NumberScope() };
        _id = new TextBox { Header = "Citation id", PlaceholderText = AutoIdHint, IsSpellCheckEnabled = false };
        foreach (var box in new[] { _author, _title, _year, _id }) box.TextChanged += (_, _) => Refresh();
        // The empty id box shows the id it will get, so its hint never disagrees with the preview.
        _author.TextChanged += (_, _) => _id.PlaceholderText = DerivedId is { Length: > 0 } d ? d : AutoIdHint;
        _year.TextChanged += (_, _) => _id.PlaceholderText = DerivedId is { Length: > 0 } d ? d : AutoIdHint;

        var row = new Grid { ColumnSpacing = 12, Children = { _year, _id } };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetColumn(_id, 1);
        AddField(_author);
        AddField(_title);
        AddField(row);
    }

    /// <summary>The typed id, or one made from the author's surname and the year ("smith2024").</summary>
    public string Id
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_id.Text)) return _id.Text.Trim().TrimStart('@');
            return DerivedId;
        }
    }

    private const string AutoIdHint = "Made from author + year";

    private string DerivedId
    {
        get
        {
            var surname = new string((_author.Text ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Where(char.IsLetterOrDigit).ToArray() ?? Array.Empty<char>()).ToLowerInvariant();
            return surname.Length == 0 ? "" : surname + Year.Trim();
        }
    }

    public string Author => _author.Text;
    public string Title => _title.Text;
    public string Year => _year.Text;
    public override string Snippet => Snippets.References(Id, Author, Title, Year);

    private static Microsoft.UI.Xaml.Input.InputScope NumberScope()
    {
        var scope = new Microsoft.UI.Xaml.Input.InputScope();
        scope.Names.Add(new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Number));
        return scope;
    }
}

/// <summary>One or more labelled number fields (column count, canvas size, grid size).</summary>
public sealed class NumbersInsertControl : InsertDialogBody
{
    private readonly List<(NumberBox Box, double Default)> _boxes = new();
    private readonly Func<int[], string> _build;

    public NumbersInsertControl(string description, Func<int[], string> build,
        params (string Label, double Value, double Min, double Max)[] fields)
        : base(description)
    {
        _build = build;
        var grid = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < fields.Length; i++)
        {
            var (label, value, min, max) = fields[i];
            var box = MakeNumberBox($"{label} ({min:0}–{max:0})", value, min, max);
            box.ValueChanged += (_, _) => Refresh();
            _boxes.Add((box, value));
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(box, i);
            grid.Children.Add(box);
        }
        AddField(grid);
    }

    public int Value(int index) => IntOf(_boxes[index].Box, _boxes[index].Default);
    public override string Snippet => _build(_boxes.Select((_, i) => Value(i)).ToArray());
}
