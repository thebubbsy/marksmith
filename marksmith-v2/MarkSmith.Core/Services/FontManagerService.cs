using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MarkSmith.Services;

/// <summary>
/// Curated typography presets plus custom-font resolution and embedding helpers (Task 16).
/// Presets map a friendly name to a CSS <c>font-family</c> stack; the embedding helpers turn a
/// local TTF/OTF/WOFF file into an <c>@font-face</c> rule (base64 data URI) so Chromium's print
/// pipeline renders the PDF with the exact font even when it is not installed system-wide.
/// </summary>
public static class FontManagerService
{
    /// <summary>Stable id used when no typography preset is selected.</summary>
    public const string SystemPresetId = "System";

    /// <summary>Default CSS stack used when no preset / custom font applies.</summary>
    public const string DefaultStack = "-apple-system, \"Segoe UI\", sans-serif";

    /// <summary>A selectable typography preset.</summary>
    public sealed record FontPreset(string Id, string DisplayName, string CssStack);

    /// <summary>Built-in presets, in display order.</summary>
    public static IReadOnlyList<FontPreset> Presets { get; } = new[]
    {
        new FontPreset(SystemPresetId, "System Default", DefaultStack),
        new FontPreset("Serif", "Serif", "\"Cambria\", \"Georgia\", \"Times New Roman\", serif"),
        new FontPreset("Sans-Serif", "Sans-Serif", "\"Segoe UI\", \"Helvetica Neue\", \"Arial\", sans-serif"),
        new FontPreset("Monospace", "Monospace", "\"Cascadia Code\", \"Consolas\", \"Courier New\", monospace"),
        new FontPreset("Dyslexic-friendly", "Dyslexic-friendly", "\"Comic Sans MS\", \"Trebuchet MS\", \"Verdana\", sans-serif"),
    };

    /// <summary>Case-insensitive preset lookup by id; null when not a known preset.</summary>
    public static FontPreset? FindPreset(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Presets.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves a selection to a CSS font-family stack. Known preset ids return the preset stack;
    /// anything else is treated as a custom font name (quoted, with system fallbacks appended).
    /// </summary>
    public static string ResolveCssStack(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection)) return DefaultStack;
        var preset = FindPreset(selection);
        if (preset != null) return preset.CssStack;
        var name = selection.Trim().Replace("\"", "");
        return $"\"{name}\", {DefaultStack}";
    }

    /// <summary>True when <paramref name="path"/> points at an existing, embeddable font file.</summary>
    public static bool IsEmbeddableFontFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf" or ".woff" or ".woff2")) return false;
            return File.Exists(path);
        }
        catch { return false; }
    }

    /// <summary>Derives the CSS font-family name for a font file (file name without extension).</summary>
    public static string GetFontFamilyName(string fontPath)
        => Path.GetFileNameWithoutExtension(fontPath);

    /// <summary>
    /// Builds an <c>@font-face</c> rule embedding the font as a base64 data URI, or null when the
    /// file is missing / not a supported font type / unreadable.
    /// </summary>
    public static string? BuildFontFaceCss(string? fontPath)
    {
        if (!IsEmbeddableFontFile(fontPath)) return null;
        try
        {
            var ext = Path.GetExtension(fontPath!).ToLowerInvariant();
            var (mime, format) = ext switch
            {
                ".ttf" => ("font/ttf", "truetype"),
                ".otf" => ("font/otf", "opentype"),
                ".woff" => ("font/woff", "woff"),
                ".woff2" => ("font/woff2", "woff2"),
                _ => ("application/octet-stream", "truetype"),
            };
            var bytes = File.ReadAllBytes(fontPath!);
            var b64 = Convert.ToBase64String(bytes);
            var family = GetFontFamilyName(fontPath!);
            return $"@font-face {{ font-family: \"{family}\"; src: url(data:{mime};base64,{b64}) format(\"{format}\"); }}";
        }
        catch { return null; }
    }
}
