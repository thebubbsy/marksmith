using System.Text.RegularExpressions;

namespace MdToPdf.Services;

// The "Copy as Markdown" browser-extension button writes an HTML clipboard entry alongside the
// plain-text Markdown, carrying the source AI-chat page's computed font-family in a leading HTML
// comment: <!--marksmith-font:encodeURIComponent(fontFamily)-->. Plain-text-only paste targets
// never see it; Marksmith's clipboard watchers do, and apply it as BrandFontFamily so the
// preview/export use the same font the reply was actually shown in. See extension/copybutton.js.
public static class ClipboardFontMarker
{
    private static readonly Regex MarkerRe = new("<!--marksmith-font:([^>]*)-->", RegexOptions.Compiled);

    public static string? Extract(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var m = MarkerRe.Match(html);
        if (!m.Success) return null;
        try
        {
            var decoded = Uri.UnescapeDataString(m.Groups[1].Value).Trim();
            return decoded.Length > 0 ? decoded : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
