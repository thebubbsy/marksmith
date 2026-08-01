using System.Text.Json;
using MdToPdf.Models;

namespace MdToPdf.Services;

// The "Copy as Markdown" browser-extension button writes an HTML clipboard entry alongside the
// plain-text Markdown, carrying the source AI-chat page's metadata in a leading HTML comment so it
// survives the copy→paste round-trip that would otherwise strip all context. Plain-text-only paste
// targets never see it (it's an HTML-format alternative); Marksmith's clipboard watchers parse it
// back into an OutputOverride and apply it — same field set the extension's REST API sends. See
// extension/copybutton.js (writer) and the clipboard watchers in each UI shell (readers).
//
// Marker format, newest first:
//   <!--marksmith-meta:{urlencoded JSON}-->   full metadata { font, source, model, title, lang, dir, accent }
//   <!--marksmith-font:{urlencoded value}-->  legacy font-only marker (still parsed for safety)
public static class ClipboardSourceMeta
{
    private static readonly System.Text.RegularExpressions.Regex MetaRe =
        new("<!--marksmith-meta:([^>]*)-->", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex FontRe =
        new("<!--marksmith-font:([^>]*)-->", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Compact wire shape the extension embeds (short keys keep the clipboard marker small); mapped
    // onto OutputOverride's Source* fields here so the rest of the app sees one carrier type.
    private sealed record Wire(
        string? Font, string? Source, string? Model, string? Title, string? Lang, string? Dir, string? Accent);

    // Returns an OutputOverride carrying whatever source metadata the marker held, or null if the
    // clipboard HTML has no Marksmith marker (an ordinary rich-text copy from elsewhere).
    public static OutputOverride? Extract(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        var meta = MetaRe.Match(html);
        if (meta.Success)
        {
            try
            {
                var json = Uri.UnescapeDataString(meta.Groups[1].Value);
                var w = JsonSerializer.Deserialize<Wire>(json, JsonOpts);
                if (w is null) return null;
                var o = new OutputOverride
                {
                    SourceFontFamily = Clean(w.Font),
                    SourceId = Clean(w.Source),
                    SourceModel = Clean(w.Model),
                    SourceTitle = Clean(w.Title),
                    SourceLanguage = Clean(w.Lang),
                    SourceDirection = Clean(w.Dir),
                    SourceAccentColor = Clean(w.Accent),
                };
                return HasAny(o) ? o : null;
            }
            catch (JsonException) { /* malformed marker — fall through to the legacy font marker */ }
            catch (UriFormatException) { }
        }

        var font = FontRe.Match(html);
        if (font.Success)
        {
            try
            {
                var value = Clean(Uri.UnescapeDataString(font.Groups[1].Value));
                if (value is not null) return new OutputOverride { SourceFontFamily = value };
            }
            catch (UriFormatException) { }
        }

        return null;
    }

    private static string? Clean(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static bool HasAny(OutputOverride o) =>
        o.SourceFontFamily is not null || o.SourceId is not null || o.SourceModel is not null ||
        o.SourceTitle is not null || o.SourceLanguage is not null || o.SourceDirection is not null ||
        o.SourceAccentColor is not null;
}
