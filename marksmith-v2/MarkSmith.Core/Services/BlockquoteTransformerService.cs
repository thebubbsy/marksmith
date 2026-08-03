using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Transforms GitHub-style blockquote callouts (<c>&gt; [!NOTE]</c>, <c>&gt; [!TIP]</c>,
/// <c>&gt; [!IMPORTANT]</c>, <c>&gt; [!WARNING]</c>, <c>&gt; [!CAUTION]</c>) into themed CSS alert
/// containers (Task 50). A maximal run of <c>&gt;</c>-prefixed lines whose first content line is a
/// recognized <c>[!KIND]</c> marker becomes a <c>&lt;div class="alert alert-{kind}"&gt;</c> with a title
/// and an HTML-escaped body; ordinary blockquotes (no marker, or an unrecognized one) pass through
/// verbatim so genuine quotations are never disturbed. The output is an HTML block suitable for the
/// HTML/PDF render path to style via the alert stylesheet.
/// </summary>
public static partial class BlockquoteTransformerService
{
    // The five GitHub alert kinds -> (css suffix, display title).
    private static readonly Dictionary<string, (string Css, string Title)> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NOTE"] = ("note", "Note"),
        ["TIP"] = ("tip", "Tip"),
        ["IMPORTANT"] = ("important", "Important"),
        ["WARNING"] = ("warning", "Warning"),
        ["CAUTION"] = ("caution", "Caution"),
    };

    // First content line of a callout: [!KIND] optionally followed by trailing text that joins the body.
    [GeneratedRegex(@"^\[!([A-Za-z]+)\]\s*(.*)$")]
    private static partial Regex CalloutRe();

    /// <summary>True when <paramref name="kind"/> (e.g. "NOTE") is a recognized alert kind.</summary>
    public static bool IsCalloutKind(string? kind) =>
        kind is not null && Kinds.ContainsKey(kind);

    /// <summary>Rewrites callout blockquotes in <paramref name="markdown"/> to CSS alert containers.</summary>
    public static string Transform(string? markdown)
    {
        if (markdown is null || string.IsNullOrWhiteSpace(markdown)) return "";

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new List<string>(lines.Length);
        int i = 0;

        while (i < lines.Length)
        {
            if (!IsBlockquoteLine(lines[i])) { output.Add(lines[i]); i++; continue; }

            // Gather the maximal run of consecutive blockquote lines.
            var group = new List<string>();
            while (i < lines.Length && IsBlockquoteLine(lines[i])) group.Add(lines[i++]);

            var content = group.Select(StripQuote).ToList();
            var callout = CalloutRe().Match(content.Count > 0 ? content[0] : "");
            if (callout.Success && Kinds.TryGetValue(callout.Groups[1].Value, out var kind))
            {
                // Body = any text trailing the marker + the remaining lines.
                var body = new List<string>();
                var trailing = callout.Groups[2].Value.Trim();
                if (trailing.Length > 0) body.Add(trailing);
                body.AddRange(content.Skip(1));
                output.Add(RenderAlert(kind.Css, kind.Title, body));
            }
            else
            {
                output.AddRange(group); // ordinary blockquote — leave untouched
            }
        }

        return string.Join("\n", output);
    }

    private static string RenderAlert(string css, string title, List<string> bodyLines)
    {
        var body = WebUtility.HtmlEncode(string.Join("\n", bodyLines).Trim('\n'));
        return new StringBuilder()
            .Append($"<div class=\"alert alert-{css}\">\n")
            .Append($"<p class=\"alert-title\">{title}</p>\n")
            .Append($"<div class=\"alert-body\">{body}</div>\n")
            .Append("</div>")
            .ToString();
    }

    private static bool IsBlockquoteLine(string line) => line.TrimStart().StartsWith('>');

    // Drops the leading '>' (and the single optional space after it) from a blockquote line.
    private static string StripQuote(string line)
    {
        var gt = line.IndexOf('>');
        if (gt < 0) return line;
        var rest = line[(gt + 1)..];
        return rest.StartsWith(' ') ? rest[1..] : rest;
    }
}
