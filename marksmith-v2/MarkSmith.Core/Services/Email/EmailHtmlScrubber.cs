using System.Text.RegularExpressions;

namespace MarkSmith.Services.Email;

/// <summary>Makes HTML we didn't write ourselves (raw HTML in the Markdown, flattened container
/// blocks) safe to put in an email: the preview sanitizer first (scripts, handlers, javascript:
/// URLs), then everything mail clients drop or mangle — style sheets, SVG, form controls, class
/// hooks, data: images and flex/grid layout.</summary>
public static partial class EmailHtmlScrubber
{
    [GeneratedRegex(@"<(style|svg|button|select|textarea|template|noscript|canvas|video|audio)\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex DroppedElements();

    [GeneratedRegex(@"<(input|link|meta|base|source|track|svg)\b[^>]*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex DroppedVoidElements();

    [GeneratedRegex(@"<!--(?!\[if)[\s\S]*?-->")]
    private static partial Regex Comments();

    [GeneratedRegex(@"\s(class|id|role|tabindex|contenteditable|draggable|data-[\w-]+|aria-[\w-]+)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HookAttributes();

    [GeneratedRegex(@"<img\b[^>]*\ssrc\s*=\s*[""']?\s*data:[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DataImages();

    [GeneratedRegex(@"display\s*:\s*(?:inline-)?(?:flex|grid)\s*;?", RegexOptions.IgnoreCase)]
    private static partial Regex FlexGrid();

    [GeneratedRegex(@"(?:position\s*:\s*(?:absolute|fixed|sticky)|transform\s*:[^;""']*|box-shadow\s*:[^;""']*)\s*;?", RegexOptions.IgnoreCase)]
    private static partial Regex Unsupported();

    public static string Scrub(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        html = HtmlSanitizer.Apply(html);
        html = DroppedElements().Replace(html, "");
        html = DroppedVoidElements().Replace(html, "");
        html = Comments().Replace(html, "");
        html = DataImages().Replace(html, "");
        html = HookAttributes().Replace(html, "");
        html = FlexGrid().Replace(html, "");
        html = Unsupported().Replace(html, "");
        return html.Trim();
    }

    /// <summary>The first complete <c>&lt;svg&gt;…&lt;/svg&gt;</c> element in <paramref name="html"/>
    /// (outermost: nested SVGs stay inside it), or null.</summary>
    public static string? FirstSvg(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        int start = html.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        int depth = 0, i = start;
        while (i < html.Length)
        {
            int open = html.IndexOf("<svg", i, StringComparison.OrdinalIgnoreCase);
            int close = html.IndexOf("</svg>", i, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return null;
            if (open >= 0 && open < close) { depth++; i = open + 4; continue; }
            depth--;
            i = close + 6;
            if (depth == 0) return html[start..i];
        }
        return null;
    }
}
