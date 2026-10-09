using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using MarkSmith.Services;
using Snippets = MarkSmith.Services.InsertSnippetBuilder;

namespace MarkSmith.Views;

/// <summary>
/// Insert ▸ Image: drop a file on the zone, Browse, or type/paste a file path or web address.
/// Shows a thumbnail of what will be inserted, prefills the alt text from the file name (until the
/// person types their own), and inserts on the dialog's Insert button like every other insert
/// dialog. The Markdown comes from <see cref="InsertSnippetBuilder.Image"/>, which writes paths
/// relative to the document and survives spaces in folder names.
/// </summary>
public sealed class ImageInsertControl : InsertDialogBody
{
    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".tif", ".tiff", ".avif" };

    private readonly string? _documentFolder;
    private readonly Border _zone;
    private readonly TextBlock _zoneTitle;
    private readonly TextBlock _zoneHint;
    private readonly FontIcon _zoneIcon;
    private readonly Image _thumb;
    private readonly TextBox _source;
    private readonly TextBox _alt;
    // The alt text this dialog last filled in. While the box still holds it, the person hasn't
    // written their own, so a new source may replace it. (TextChanged is raised asynchronously,
    // so a flag set around the assignment can't tell our edits from theirs.)
    private string _autoAlt = "";
    private string _shownThumb = "";

    /// <param name="documentFolder">Folder of the open file (images in it are written relative to
    /// it), or null for pasted text.</param>
    public ImageInsertControl(string? documentFolder)
        : base(documentFolder is null
            ? "A picture from this PC or the web. Pasted text has no folder of its own, so a file is linked by its full path."
            : "A picture from this PC or the web. Files in the document's folder are linked relative to it, so they move together.")
    {
        _documentFolder = documentFolder;

        _zoneIcon = new FontIcon { Glyph = "\uE8B9", FontSize = 28, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center };
        _zoneTitle = new TextBlock { Text = "Drag an image here", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        _zoneHint = new TextBlock { Text = "PNG · JPG · GIF · WebP · SVG", FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _thumb = new Image { MaxHeight = 88, MaxWidth = 400, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center };
        _thumb.ImageFailed += (_, _) => ShowThumb(null);

        var browse = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new FontIcon { Glyph = "\uE8E5", FontSize = 14 }, new TextBlock { Text = "Browse…" } },
            },
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        ToolTipService.SetToolTip(browse, "Pick an image file on this PC");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(browse, "Browse for an image");
        browse.Click += OnBrowseClick;

        _zone = new Border
        {
            AllowDrop = true,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 18, 20, 16),
            Height = 214, // fixed, so the dialog does not jump when a thumbnail replaces the icon
            Child = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _zoneIcon, _thumb, _zoneTitle, _zoneHint, browse },
            },
        }.Themed(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush").Themed(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        _zone.DragOver += OnDragOver;
        _zone.DragLeave += (_, _) => Highlight(false);
        _zone.Drop += OnDrop;

        _source = new TextBox
        {
            Header = "File or web address",
            PlaceholderText = @"C:\Pictures\chart.png or https://…",
            IsSpellCheckEnabled = false,
            InputScope = LinkInsertControl.UrlScope(),
        };
        _source.TextChanged += (_, _) => OnSourceChanged();

        _alt = new TextBox
        {
            Header = "Alt text",
            PlaceholderText = "What the picture shows",
        };
        ToolTipService.SetToolTip(_alt, "Read aloud by screen readers, and shown wherever the picture can't load");
        _alt.TextChanged += (_, _) => Refresh();

        AddField(_zone);
        AddField(_source);
        AddField(_alt);
    }

    public string Source => Clean(_source.Text);
    public string AltText => _alt.Text.Trim();
    public override string Snippet => Source.Length == 0 ? "" : Snippets.Image(AltText, Source, _documentFolder ?? "");

    protected override string? Problem
    {
        get
        {
            var src = Source;
            if (src.Length == 0) return "Drop an image, browse for one, or paste a file path or web address.";
            if (IsWeb(src))
                return Uri.TryCreate(src, UriKind.Absolute, out var u) && u.Host.Length > 0 ? null : "That web address isn't complete.";
            if (src.Contains("://")) return "Only files on this PC and http(s) web addresses can be inserted.";
            if (!ImageExtensions.Contains(Path.GetExtension(src).ToLowerInvariant()))
                return "That isn't an image file — use a PNG, JPG, GIF, WebP or SVG.";
            if (DocumentImages.Resolve(src, _documentFolder) is not null) return null;
            // "example.com/a.png" is a web address missing its scheme, not a missing file.
            var first = src.Split('/', '\\')[0];
            return !src.Contains(':') && src.Contains('/') && first.Contains('.') && !first.StartsWith('.')
                ? $"Add https:// — without it “{src}” is read as a file next to the document."
                : "Can't find that file on this PC.";
        }
    }

    // ---- source → thumbnail and alt ------------------------------------------------------------

    private void OnSourceChanged()
    {
        var src = Source;
        if (_alt.Text == _autoAlt || _alt.Text.Length == 0)
        {
            _autoAlt = src.Length == 0 ? "" : DocumentImages.AltFromFileName(src);
            _alt.Text = _autoAlt;
        }

        string? thumb = null;
        if (IsWeb(src) && Uri.TryCreate(src, UriKind.Absolute, out _)) thumb = src;
        else if (src.Length > 0 && DocumentImages.Resolve(src, _documentFolder) is { } local
                 && ImageExtensions.Contains(Path.GetExtension(local).ToLowerInvariant())) thumb = local;
        ShowThumb(thumb);
        Refresh();
    }

    private void ShowThumb(string? source)
    {
        if (source == _shownThumb) return;
        _shownThumb = source ?? "";
        if (source is null)
        {
            _thumb.Source = null;
            _thumb.Visibility = Visibility.Collapsed;
            _zoneIcon.Visibility = Visibility.Visible;
            _zoneTitle.Text = "Drag an image here";
            _zoneHint.Text = "PNG · JPG · GIF · WebP · SVG";
            return;
        }

        try
        {
            var uri = new Uri(source);
            _thumb.Source = source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? new SvgImageSource(uri)
                : new BitmapImage(uri) { DecodePixelHeight = 240 };
        }
        catch
        {
            ShowThumb(null);
            return;
        }
        _thumb.Visibility = Visibility.Visible;
        _zoneIcon.Visibility = Visibility.Collapsed;
        _zoneTitle.Text = IsWeb(source) ? "From the web" : Path.GetFileName(source);
        _zoneHint.Text = IsWeb(source) ? new Uri(source).Host : SizeOf(source);
    }

    private static string SizeOf(string path)
    {
        try
        {
            var bytes = new FileInfo(path).Length;
            return bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
        }
        catch { return ""; }
    }

    // ---- drag & drop ---------------------------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = ok ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (ok && e.DragUIOverride is not null) e.DragUIOverride.Caption = "Use this image";
        Highlight(ok);
    }

    private void Highlight(bool active)
    {
        ThemeBrush.Set(_zone, Border.BorderBrushProperty, active ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
        if (_thumb.Visibility != Visibility.Visible) _zoneTitle.Text = active ? "Release to use this image" : "Drag an image here";
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        Highlight(false);
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault(f => ImageExtensions.Contains(f.FileType.ToLowerInvariant()))
                       ?? items.OfType<StorageFile>().FirstOrDefault();
            if (file is not null) _source.Text = file.Path;   // the problem line explains a non-image
        }
        catch
        {
            // Unreadable drop (e.g. a virtual item): the field stays as it was.
        }
    }

    // ---- browse --------------------------------------------------------------------------------

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var path = await NativeFilePicker.PickOpenFileAsync(
            this, "Insert an image", NativeFilePicker.Purpose.Images,
            new[] { Models.FileType.Of("Images", ImageExtensions) },
            okLabel: "Choose", start: StartFolder.Pictures);
        if (!string.IsNullOrEmpty(path))
        {
            _source.Text = path;
            _alt.Focus(FocusState.Programmatic);
            _alt.SelectAll();
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static bool IsWeb(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // Explorer's "Copy as path" wraps the path in quotes; a pasted file: URI becomes its path.
    private static string Clean(string? text)
    {
        var s = (text ?? "").Trim().Trim('"').Trim();
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.IsFile)
            s = u.LocalPath;
        return s;
    }
}
