using SkiaSharp;
using Svg.Skia;

namespace MarkSmith.Services;

// Rasterizes SVG markup to a PNG via Svg.Skia (on top of SkiaSharp). Used to produce the raster
// fallback Word requires alongside an embedded SVG — Word 2016+ renders the crisp SVG, everything
// older (and the thumbnail/print pipeline) uses the PNG. Rendered at 2x for a sharp fallback.
public static class SvgRasterizer
{
    public static byte[]? ToPng(string svg, double scale = 2.0)
    {
        try
        {
            using var skSvg = new SKSvg();
            skSvg.FromSvg(svg);
            var picture = skSvg.Picture;
            if (picture is null) return null;

            var rect = picture.CullRect;
            var baseW = rect.Width > 0 ? rect.Width : 600;
            var baseH = rect.Height > 0 ? rect.Height : 400;

            int w = Math.Max(1, (int)Math.Ceiling(baseW * scale));
            int h = Math.Max(1, (int)Math.Ceiling(baseH * scale));

            using var surface = SKSurface.Create(new SKImageInfo(w, h));
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.Scale((float)scale);
            surface.Canvas.DrawPicture(picture);
            surface.Canvas.Flush();

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }
        catch
        {
            return null; // any decode/render failure → caller falls back to the plain code block
        }
    }

    // Intrinsic pixel size of an SVG, read from width/height or viewBox — for sizing the Word
    // drawing frame without paying a full rasterization pass.
    public static (double W, double H) Dimensions(string svg)
    {
        try
        {
            var wMatch = System.Text.RegularExpressions.Regex.Match(svg, @"<svg\b[^>]*\bwidth=""([\d.]+)");
            var hMatch = System.Text.RegularExpressions.Regex.Match(svg, @"<svg\b[^>]*\bheight=""([\d.]+)");
            if (wMatch.Success && hMatch.Success &&
                double.TryParse(wMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var w) &&
                double.TryParse(hMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var h) &&
                w > 0 && h > 0)
                return (w, h);

            var vb = System.Text.RegularExpressions.Regex.Match(svg, @"viewBox=""[\d.\-]+ [\d.\-]+ ([\d.]+) ([\d.]+)""");
            if (vb.Success &&
                double.TryParse(vb.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var vw) &&
                double.TryParse(vb.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out var vh) &&
                vw > 0 && vh > 0)
                return (vw, vh);
        }
        catch { /* fall through */ }
        return (600, 400);
    }
}
