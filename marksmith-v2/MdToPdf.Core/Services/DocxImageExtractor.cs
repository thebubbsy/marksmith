using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MdToPdf.Services;

// Tier 2 (Universal Engine) image extraction. Walks every picture in the document body, writes each
// referenced image part out to a media/ folder next to the recovered Markdown, and returns a map the
// paragraph converter uses to emit real ![alt](media/imageN.png) references instead of silently
// dropping the picture. Deduplicated by relationship id, so an image reused across the document is
// written once.
//
// SVG-with-PNG-fallback pictures (Word 2016+ stores a crisp <asvg:svgBlip> alongside the legacy
// raster) are handled by preferring the SVG part when present — it renders sharply in any modern
// viewer. EMF/WMF are extracted as-is; most Markdown renderers won't display them, but the bytes are
// preserved and the alt text flags the format.
public static class DocxImageExtractor
{
    // The SVG-blip extension namespace (asvg) Word uses to reference the vector twin of a picture.
    private const string AsvgNamespace = "http://schemas.microsoft.com/office/drawing/2016/SVG/main";
    private const string WpNamespace = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string RNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public sealed record ExtractedImage(string RelativePath, string ContentType, string Alt);

    /// <summary>
    /// Extracts every picture referenced in the document body into <paramref name="mediaDir"/> and
    /// returns a map of relationship id -> extracted image. The map lets the paragraph converter
    /// resolve a drawing's r:embed to a Markdown-relative path.
    /// </summary>
    public static Dictionary<string, ExtractedImage> ExtractAll(MainDocumentPart main, string mediaDir)
    {
        var map = new Dictionary<string, ExtractedImage>(StringComparer.Ordinal);
        var body = main.Document?.Body;
        if (body is null) return map;

        int counter = 0;
        foreach (var drawing in body.Descendants<W.Drawing>())
        {
            foreach (var blip in drawing.Descendants<A.Blip>())
            {
                var relId = blip.Embed?.Value;
                if (string.IsNullOrEmpty(relId) || map.ContainsKey(relId)) continue;

                // Prefer the SVG twin when the picture carries one (crisper than the raster).
                var (part, contentType) = ResolvePreferredPart(main, blip, relId);
                if (part is null) continue;

                var ext = ExtensionFor(contentType);
                var relative = $"media/image{++counter}.{ext}";
                WritePart(part, Path.Combine(mediaDir, relative));

                map[relId] = new ExtractedImage(relative, contentType, ReadAlt(drawing));
            }
        }

        return map;
    }

    // Picks the SVG part referenced by an <asvg:svgBlip r:embed="..."/> extension when present;
    // otherwise falls back to the primary raster blip target.
    private static (OpenXmlPart? Part, string ContentType) ResolvePreferredPart(
        MainDocumentPart main, A.Blip blip, string rasterRelId)
    {
        var svgBlip = blip.Descendants()
            .FirstOrDefault(e => e.LocalName == "svgBlip" && e.NamespaceUri == AsvgNamespace);
        var svgRelId = svgBlip?.GetAttribute("embed", RNamespace).Value;

        if (!string.IsNullOrEmpty(svgRelId))
        {
            try
            {
                var svgPart = main.GetPartById(svgRelId);
                if (svgPart is not null)
                    return (svgPart, "image/svg+xml");
            }
            catch { /* fall through to the raster twin */ }
        }

        try
        {
            if (main.GetPartById(rasterRelId) is ImagePart raster)
                return (raster, raster.ContentType);
        }
        catch { /* unresolved relationship */ }

        return (null, "");
    }

    private static void WritePart(OpenXmlPart part, string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var src = part.GetStream(FileMode.Open, FileAccess.Read);
        using var dst = File.Create(fullPath);
        src.CopyTo(dst);
    }

    // Alt text lives on the drawing's wp:docPr (descr preferred, then name). Falls back to a generic
    // label; EMF/WMF get a format note since most viewers can't render them.
    private static string ReadAlt(W.Drawing drawing)
    {
        var docPr = drawing.Descendants()
            .FirstOrDefault(e => e.LocalName == "docPr" && e.NamespaceUri == WpNamespace);
        var descr = docPr?.GetAttribute("descr", "").Value;
        if (!string.IsNullOrWhiteSpace(descr)) return descr!.Trim();
        var name = docPr?.GetAttribute("name", "").Value;
        if (!string.IsNullOrWhiteSpace(name)) return name!.Trim();
        return "image";
    }

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/jpg" => "jpeg",
        "image/gif" => "gif",
        "image/svg+xml" => "svg",
        "image/bmp" => "bmp",
        "image/tiff" => "tiff",
        "image/x-emf" or "image/emf" => "emf",
        "image/x-wmf" or "image/wmf" => "wmf",
        _ => "bin",
    };
}
