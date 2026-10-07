using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace MarkSmith.Converters;

/// <summary>SVG markup (the SmartArt gallery's miniatures) to an image source. Sources are cached by
/// markup, so the 176 gallery rows share the 25 family drawings and scrolling never re-parses one.
/// ConverterParameter is the pixel width to rasterize at (default 96).</summary>
public class SvgMarkupToImageSourceConverter : IValueConverter
{
    private static readonly Dictionary<string, SvgImageSource> Cache = new();

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string svg || svg.Length == 0) return null;
        string key = (parameter as string ?? "") + "|" + svg;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        double width = parameter is string p && double.TryParse(p, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 96;
        var source = new SvgImageSource { RasterizePixelWidth = width };
        Cache[key] = source;
        _ = LoadAsync(source, svg);
        return source;
    }

    private static async System.Threading.Tasks.Task LoadAsync(SvgImageSource source, string svg)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteString(svg);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await source.SetSourceAsync(stream);
        }
        catch { /* a miniature is decoration: an empty tile is better than a crash */ }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
