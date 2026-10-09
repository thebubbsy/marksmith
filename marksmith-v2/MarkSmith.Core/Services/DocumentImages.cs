using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MarkSmith.Services;

/// <summary>
/// The one place that knows where a document's images live and how to write a reference to one.
///
/// Markdown files normally point at their images relative to themselves (<c>![](images/a.png)</c>),
/// but the preview only understood absolute paths and the Word and EPUB exporters looked next to
/// the APP, so a perfectly ordinary file lost every picture everywhere except email. Every
/// renderer now asks <see cref="Resolve"/>, which tries the document's folder first.
///
/// The folder is ambient (<see cref="UseFolder"/>) because the preview and the exporters are
/// reached through many call chains; the caller that knows which file is open sets it around the
/// render or export, and it flows through awaits and Task.Run.
/// </summary>
public static class DocumentImages
{
    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>The folder relative image paths resolve against, or null (pasted text).</summary>
    public static string? CurrentFolder => Ambient.Value;

    /// <summary>Makes <paramref name="folder"/> the base for relative image paths until disposed.
    /// A null or blank folder keeps whatever is already in effect.</summary>
    public static IDisposable UseFolder(string? folder)
    {
        var previous = Ambient.Value;
        if (!string.IsNullOrWhiteSpace(folder)) Ambient.Value = folder;
        return new Restore(previous);
    }

    /// <summary>The folder of <paramref name="documentPath"/>, or null when there is no file.</summary>
    public static string? FolderOf(string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath)) return null;
        try { return Path.GetDirectoryName(Path.GetFullPath(documentPath)); }
        catch { return null; }
    }

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;
        private bool _done;
        public Restore(string? previous) => _previous = previous;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Ambient.Value = _previous;
        }
    }

    /// <summary>True for web and data: sources, which are never files on disk.</summary>
    public static bool IsRemote(string? src) =>
        src is not null &&
        (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
         src.StartsWith("//", StringComparison.Ordinal));

    /// <summary>
    /// The full path of the local image <paramref name="src"/> refers to, or null when it isn't a
    /// local file or doesn't exist. Accepts file: URIs, absolute paths with either slash,
    /// percent-encoded destinations (Markdig encodes spaces as %20) and paths relative to the
    /// document folder (<paramref name="folder"/>, else the ambient one). The app folder and the
    /// working directory are kept as last resorts for bundled sample images.
    /// </summary>
    public static string? Resolve(string? src, string? folder = null)
    {
        if (string.IsNullOrWhiteSpace(src) || IsRemote(src)) return null;
        var raw = src.Trim();
        if (raw.StartsWith('<') && raw.EndsWith('>')) raw = raw[1..^1];

        // Try the destination as written first: a file really named "a%20b.png" exists too.
        foreach (var candidate in new[] { raw, SafeUnescape(raw) }.Distinct())
        {
            var found = ResolveOne(candidate, folder ?? Ambient.Value);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? ResolveOne(string value, string? folder)
    {
        try
        {
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
                value = uri.LocalPath;
            }

            // Drop a ?query or #fragment some editors append; Windows paths never contain them.
            var cut = value.IndexOfAny(new[] { '?', '#' });
            if (cut > 0) value = value[..cut];

            var native = value.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathFullyQualified(native))
                return File.Exists(native) ? Path.GetFullPath(native) : null;

            // "/images/a.png" is site-root style; on disk the nearest meaning is the document folder.
            var relative = native.TrimStart(Path.DirectorySeparatorChar);
            foreach (var root in new[] { folder, AppContext.BaseDirectory, SafeCwd() })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (File.Exists(full)) return full;
            }
        }
        catch
        {
            // Illegal characters, too long, no access: not an image we can use.
        }
        return null;
    }

    /// <summary>True when <paramref name="src"/> names a local file (relative or absolute), as
    /// opposed to a web or data: source. Says nothing about whether the file exists.</summary>
    public static bool LooksLocal(string? src) => !string.IsNullOrWhiteSpace(src) && !IsRemote(src);

    /// <summary>
    /// The Markdown destination for an image at <paramref name="source"/> in a document stored in
    /// <paramref name="documentFolder"/>: relative with forward slashes when the image is in that
    /// folder or below it (so the document and its images can move together), otherwise the full
    /// path with forward slashes. Web addresses pass through. Wrapped in &lt;…&gt; when it holds
    /// characters a bare destination can't (spaces, unbalanced brackets), so
    /// "C:\My Pictures\a.png" no longer turns into broken image Markdown.
    /// </summary>
    public static string Destination(string source, string? documentFolder = null)
    {
        var value = (source ?? "").Trim();
        if (value.Length == 0) return "image.png";
        if (!IsRemote(value) && !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var native = value.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathFullyQualified(native) && !string.IsNullOrWhiteSpace(documentFolder))
                {
                    var rel = Path.GetRelativePath(Path.GetFullPath(documentFolder), Path.GetFullPath(native));
                    if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) native = rel;
                }
                value = native.Replace('\\', '/');
            }
            catch
            {
                value = value.Replace('\\', '/');
            }
        }
        return NeedsAngleBrackets(value) ? "<" + value.Replace("<", "%3C").Replace(">", "%3E") + ">" : value;
    }

    /// <summary>Alt text from a file name: "team-photo_2024.png" reads "team photo 2024".</summary>
    public static string AltFromFileName(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "image";
        var name = source;
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                name = uri.AbsolutePath;
            name = Path.GetFileNameWithoutExtension(SafeUnescape(name.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch { name = ""; }
        var words = string.Join(' ', name.Split(new[] { '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        return words.Length == 0 ? "image" : words;
    }

    /// <summary>Escapes the characters that would end or break the [alt] part of an image.</summary>
    public static string EscapeAlt(string? alt)
    {
        var sb = new StringBuilder();
        foreach (var c in (alt ?? "").Replace("\r", " ").Replace("\n", " ").Trim())
        {
            if (c is '[' or ']' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- serving local images to the in-app web view --------------------------------------------

    // The preview and PDF pages are loaded with NavigateToString, which fails past ~2 MB, so local
    // images are inlined only up to a budget. An image past it used to keep its file path, which a
    // NavigateToString page can't load, so it silently vanished from the preview AND the PDF. The
    // WinUI host now serves them from a virtual host instead. Only files the renderer registered
    // here can be fetched; the page can't ask for an arbitrary path.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Served = new(StringComparer.Ordinal);
    private const int MaxServed = 4096;

    /// <summary>Host name the web view serves registered images from, or null when the platform
    /// has no such host (images past the inline budget then keep their original src).</summary>
    public static string? ServedHost { get; set; }

    /// <summary>A URL the in-app web view can load <paramref name="fullPath"/> from, or null when
    /// no image host is available.</summary>
    public static string? ServeUrl(string fullPath)
    {
        if (ServedHost is null || string.IsNullOrWhiteSpace(fullPath)) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
        var token = Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
        if (Served.Count >= MaxServed) Served.Clear();
        Served[token] = fullPath;
        return $"https://{ServedHost}/{token}/{Uri.EscapeDataString(Path.GetFileName(fullPath))}";
    }

    /// <summary>The registered file behind a <see cref="ServeUrl"/> address, or null.</summary>
    public static string? ServedPath(string? url)
    {
        if (ServedHost is null || url is null) return null;
        var prefix = $"https://{ServedHost}/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url[prefix.Length..];
        var slash = rest.IndexOf('/');
        var token = slash < 0 ? rest : rest[..slash];
        return Served.TryGetValue(token.ToLowerInvariant(), out var path) && File.Exists(path) ? path : null;
    }

    /// <summary>Content type for an image file, by extension.</summary>
    public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".tif" or ".tiff" => "image/tiff",
        ".avif" => "image/avif",
        _ => "image/png",
    };

    private static bool NeedsAngleBrackets(string value)
    {
        if (value.Any(char.IsWhiteSpace)) return true;
        var depth = 0;
        foreach (var c in value)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return true;
        }
        return depth != 0;
    }

    private static string SafeUnescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch { return value; }
    }

    private static string? SafeCwd()
    {
        try { return Directory.GetCurrentDirectory(); }
        catch { return null; }
    }
}
