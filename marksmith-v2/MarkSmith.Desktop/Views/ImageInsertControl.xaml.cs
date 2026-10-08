using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MarkSmith.Services;

namespace MarkSmith.Views;

// Interactive image picker behind Insert ▸ Image: drag & drop a file onto the zone, browse with
// the native file picker, or paste a web URL. Raises ImagePicked with the chosen source (a local
// path or a URL) and the host closes the dialog + inserts the markdown. Quick insert never shows this.
public sealed partial class ImageInsertControl : UserControl
{
    public event Action<string>? ImagePicked;

    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".tif", ".tiff", ".avif" };

    private Brush? _restBorderBrush;

    public ImageInsertControl()
    {
        InitializeComponent();
        _restBorderBrush = DropZone.BorderBrush;
        HoverPolish.Track(this);
    }

    // ---- drag & drop ---------------------------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            HighlightZone(true);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            HighlightZone(false);
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e) => HighlightZone(false);

    private async void OnDrop(object sender, DragEventArgs e)
    {
        HighlightZone(false);
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault(IsImageFile);
            if (file is not null)
            {
                ImagePicked?.Invoke(file.Path);
                return;
            }
            ShowStatus("That doesn't look like an image — drop a PNG, JPG, GIF, WebP or SVG file.");
        }
        catch
        {
            ShowStatus("Couldn't read the dropped file — try Browse instead.");
        }
    }

    private void HighlightZone(bool active)
    {
        DropZone.BorderBrush = active
            ? (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"]
            : _restBorderBrush ?? DropZone.BorderBrush;
        DropTitle.Text = active ? "Release to add the image" : "Drag & drop an image here";
    }

    // ---- file picker ---------------------------------------------------------------------------

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var spec = string.Join(";", ImageExtensions.Select(e => "*" + e));
        var filters = new[]
        {
            ($"Image files ({string.Join(", ", ImageExtensions)})", spec),
            ("All files (*.*)", "*.*")
        };
        var path = await MarkSmith.Services.NativeFilePicker.PickOpenFileAsync(App.MainAppWindow, "Insert Image", filters);
        if (!string.IsNullOrEmpty(path)) ImagePicked?.Invoke(path);
    }

    // ---- URL paste -----------------------------------------------------------------------------

    private void OnUrlInsertClick(object sender, RoutedEventArgs e) => TryPickUrl();

    private void OnUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) TryPickUrl();
    }

    private void TryPickUrl()
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            ShowStatus("Paste a web address first — e.g. https://example.com/chart.png");
            return;
        }
        ImagePicked?.Invoke(url);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static bool IsImageFile(StorageFile f) =>
        ImageExtensions.Contains(f.FileType.ToLowerInvariant());

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }
}
