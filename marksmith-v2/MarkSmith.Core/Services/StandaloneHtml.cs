using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Makes rendered HTML work outside the app. The preview loads mermaid, KaTeX and highlight.js from
/// <see cref="WebAssets.Base"/>, a virtual host that only exists inside MarkSmith's own WebView — so an
/// exported .html opened in a browser showed diagrams as raw code, maths as <c>$…$</c> source and code
/// without highlighting. This swaps each of those references for the bundled file itself.
/// </summary>
public static class StandaloneHtml
{
    /// <summary>Where the bundled web assets live on disk (the WinUI build copies them here).</summary>
    public static string DefaultAssetFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "web");

    private static readonly Regex StylesheetLink = new(
        @"<link\s+rel=""stylesheet""\s+href=""(?<url>[^""]+)""\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScriptSrc = new(
        @"(?<pre><script\b[^>]*?\bsrc="")(?<url>[^""]+)(?<post>"")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ServedImgSrc = new(
        @"(?<pre><img\b[^>]*?\bsrc="")(?<url>https://[^""/]+/[0-9a-f]{24}/[^""]*)(?<post>"")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CssUrl = new(
        @"url\((?<q>['""]?)(?<path>[^'"")]+)\k<q>\)",
        RegexOptions.Compiled);

    public static string Inline(string html, string? assetFolder = null)
    {
        if (string.IsNullOrEmpty(html)) return html;
        var folder = assetFolder ?? DefaultAssetFolder;
        if (!Directory.Exists(folder)) return html;
        var prefix = WebAssets.Base.TrimEnd('/') + "/";

        // Stylesheets become <style> blocks (a data: stylesheet would break KaTeX's relative font
        // URLs), with those fonts embedded in turn.
        html = StylesheetLink.Replace(html, m =>
        {
            var file = LocalFile(m.Groups["url"].Value, prefix, folder);
            if (file is null) return m.Value;
            var cssDir = Path.GetDirectoryName(file)!;
            var css = CssUrl.Replace(File.ReadAllText(file), u =>
            {
                var path = u.Groups["path"].Value;
                if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || path.Contains("://")) return u.Value;
                var font = Path.GetFullPath(Path.Combine(cssDir, path.Split('?', '#')[0]));
                if (!File.Exists(font)) return u.Value;
                return $"url(data:{MimeFor(font)};base64,{Convert.ToBase64String(File.ReadAllBytes(font))})";
            });
            return "<style>\n" + css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase) + "\n</style>";
        });

        // Scripts keep their tag and attributes (defer, onload) and only swap src for a data: URL,
        // so they still run in exactly the same order and KaTeX's onload hook still fires.
        html = ScriptSrc.Replace(html, m =>
        {
            var file = LocalFile(m.Groups["url"].Value, prefix, folder);
            if (file is null) return m.Value;
            var data = "data:text/javascript;base64," + Convert.ToBase64String(File.ReadAllBytes(file));
            return m.Groups["pre"].Value + data + m.Groups["post"].Value;
        });

        // Images the preview served from the in-app image host (too big to inline) go in as data:
        // URLs, so the saved page shows them too.
        if (DocumentImages.ServedHost is not null)
        {
            html = ServedImgSrc.Replace(html, m =>
            {
                var file = DocumentImages.ServedPath(m.Groups["url"].Value);
                if (file is null) return m.Value;
                try { return m.Groups["pre"].Value + $"data:{DocumentImages.MimeFor(file)};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}" + m.Groups["post"].Value; }
                catch { return m.Value; }
            });
        }

        return html;
    }

    private static string? LocalFile(string url, string prefix, string folder)
    {
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = url[prefix.Length..].Split('?', '#')[0].Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(folder);
        var full = Path.GetFullPath(Path.Combine(root, rel));
        // Never read outside the asset folder, whatever the HTML asks for.
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    /// <summary>True while anything in <paramref name="html"/> still points at the in-app asset host.</summary>
    public static bool ReferencesAppAssets(string html) =>
        html.Contains(WebAssets.Base, StringComparison.OrdinalIgnoreCase);
}
