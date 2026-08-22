using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Typography;

public record EpigraphBlock(string QuoteText, string? Author, string? Source, int LineNumber);
public record MarginaliaNote(int NoteId, string NoteText, int LineNumber);

/// <summary>
/// Service for parsing literary epigraphs, block quotes, and Edward Tufte-style marginalia sidenotes.
/// </summary>
public static class EpigraphMarginaliaService
{
    private static readonly Regex EpigraphRegex = new(
        @":::epigraph(?:\s+author=""([^""]+)"")?(?:\s+source=""([^""]+)"")?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex SidenoteRegex = new(
        @"\^\[sidenote:\s*([^\]]+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Transforms all epigraphs and sidenotes in Markdown into accessible HTML typography components.
    /// </summary>
    public static string TransformTypography(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        // 1. Transform Epigraphs
        string res = EpigraphRegex.Replace(markdown, m =>
        {
            string? author = m.Groups[1].Success ? m.Groups[1].Value : null;
            string? source = m.Groups[2].Success ? m.Groups[2].Value : null;
            string quote = m.Groups[3].Value.Trim();

            string citeHtml = "";
            if (!string.IsNullOrEmpty(author) || !string.IsNullOrEmpty(source))
            {
                string citeText = !string.IsNullOrEmpty(author) && !string.IsNullOrEmpty(source)
                    ? $"&mdash; {System.Net.WebUtility.HtmlEncode(author)}, <cite>{System.Net.WebUtility.HtmlEncode(source)}</cite>"
                    : $"&mdash; {System.Net.WebUtility.HtmlEncode(author ?? source)}";
                citeHtml = $"<footer class=\"ms-epigraph-cite\">{citeText}</footer>";
            }

            return $"""
                <div class="ms-epigraph">
                  <blockquote class="ms-epigraph-quote">{System.Net.WebUtility.HtmlEncode(quote)}</blockquote>
                  {citeHtml}
                </div>
                """;
        });

        // 2. Transform Sidenotes
        int noteCounter = 1;
        res = SidenoteRegex.Replace(res, m =>
        {
            int id = noteCounter++;
            string noteText = m.Groups[1].Value.Trim();
            return $"<label for=\"sn-{id}\" class=\"ms-margin-toggle ms-sidenote-number\"></label><input type=\"checkbox\" id=\"sn-{id}\" class=\"ms-margin-toggle\"/><span class=\"ms-sidenote\">{System.Net.WebUtility.HtmlEncode(noteText)}</span>";
        });

        return res;
    }
}
