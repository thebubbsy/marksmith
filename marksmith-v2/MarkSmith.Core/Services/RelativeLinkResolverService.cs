using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Resolves relative file links (<c>./doc.md</c>, <c>../img.png</c>) against a document's root
/// directory into normalized absolute paths (Task 47). External URLs (<c>https://</c>, <c>mailto:</c>),
/// in-document anchors (<c>#section</c>), and already-absolute paths are passed through untouched, so
/// only genuine relative file references are rewritten. Normalization collapses <c>.</c>/<c>..</c>
/// segments via <see cref="Path.GetFullPath"/> and emits forward slashes for portable, diff-friendly
/// links. A trailing <c>#fragment</c> on a link is preserved across resolution.
/// </summary>
public static partial class RelativeLinkResolverService
{
    // A URI scheme prefix ("http:", "mailto:", "ftp:"). A single leading letter followed by a colon
    // is a Windows drive ("C:"), NOT a scheme — so only schemes longer than one char count as external.
    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+.\-]*):")]
    private static partial Regex SchemeRe();

    // Inline Markdown link/image target: ](target). The target run excludes whitespace and ')', which
    // covers the vast majority of authored links (titled links and <spaced> targets are left as-is).
    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex InlineLinkRe();

    /// <summary>True when the link is a relative file reference (not external, anchor, or absolute).</summary>
    public static bool IsRelativeFileLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return false;
        var trimmed = link.Trim();
        if (trimmed.StartsWith('#')) return false;          // in-document anchor
        if (IsExternal(trimmed)) return false;              // http(s)/mailto/ftp/...
        // Strip a fragment before testing rootedness: "C:\a\b.md#x" is absolute, "doc.md#x" is relative.
        var hash = trimmed.IndexOf('#');
        var pathPart = hash >= 0 ? trimmed[..hash] : trimmed;
        return pathPart.Length > 0 && !Path.IsPathRooted(pathPart);
    }

    /// <summary>True when the link carries a non-drive URI scheme (http, https, mailto, ftp, ...).</summary>
    public static bool IsExternal(string link)
    {
        var m = SchemeRe().Match(link);
        return m.Success && m.Groups[1].Value.Length > 1;
    }

    /// <summary>
    /// Resolves <paramref name="link"/> against <paramref name="rootDirectory"/>. Relative paths are
    /// combined with the root and normalized to an absolute, forward-slashed path; external URLs,
    /// anchors, and empty input are returned unchanged.
    /// </summary>
    public static string Resolve(string? link, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(link)) return link ?? "";
        var trimmed = link.Trim();
        if (trimmed.StartsWith('#') || IsExternal(trimmed)) return trimmed;

        // Preserve a trailing #fragment so "doc.md#heading" keeps its anchor after normalization.
        string fragment = "";
        var hash = trimmed.IndexOf('#');
        var pathPart = hash >= 0 ? trimmed[..hash] : trimmed;
        if (hash >= 0) fragment = trimmed[hash..];
        if (pathPart.Length == 0) return trimmed;

        try
        {
            var rooted = Path.IsPathRooted(pathPart)
                ? pathPart
                : Path.Combine(rootDirectory, pathPart.Replace('/', Path.DirectorySeparatorChar));
            return Normalize(rooted) + fragment;
        }
        catch
        {
            return trimmed; // invalid path chars etc. — leave the link as authored
        }
    }

    /// <summary>
    /// Rewrites every relative inline link/image target in <paramref name="markdown"/> to an absolute
    /// path rooted at <paramref name="rootDirectory"/>. External URLs and anchors are left intact.
    /// </summary>
    public static string ResolveMarkdown(string? markdown, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown ?? "";
        return InlineLinkRe().Replace(markdown, m => "](" + Resolve(m.Groups[1].Value, rootDirectory) + ")");
    }

    // Absolute, directory-separator-normalized, forward-slashed form of a path.
    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');
}
