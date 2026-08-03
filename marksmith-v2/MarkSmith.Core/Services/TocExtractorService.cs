using System.Collections.Generic;
using System.Text;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkSmith.Services;

/// <summary>A single node in a document outline: a heading's level, visible text, and anchor id.</summary>
/// <param name="Level">Heading level, 1 (H1) through 6 (H6).</param>
/// <param name="Text">The heading's plain-text content (inline markup stripped).</param>
/// <param name="Anchor">The element id Markdig's AutoIdentifiers assigned — the same id the rendered
/// preview uses, so a flyout can scroll to it with <c>getElementById(anchor)</c>.</param>
public sealed record TocEntry(int Level, string Text, string Anchor);

/// <summary>
/// Builds a structured table-of-contents outline from a Markdown document (Task 17). Parses with the
/// same Markdig extension set the preview renders with, so the anchors it returns are exactly the ids
/// the preview's headings carry — an outline flyout can click-to-scroll without re-deriving slugs.
/// Headings inside fenced code blocks are ignored automatically (they never become HeadingBlocks).
/// </summary>
public static class TocExtractorService
{
    // Mirrors MarkdownHtmlService.Pipeline's id-relevant extensions. AutoIdentifiers (part of
    // UseAdvancedExtensions) assigns each heading the same id the HTML renderer emits; the emoji
    // extension is included so a shortcode in a heading produces the same id here as in the preview.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley(enableSmileys: false)
        .Build();

    /// <summary>Extracts H1–H6 headings in document order; empty when there is no content.</summary>
    public static IReadOnlyList<TocEntry> Extract(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return System.Array.Empty<TocEntry>();

        var doc = Markdown.Parse(markdown, Pipeline);
        var entries = new List<TocEntry>();
        foreach (var heading in doc.Descendants<HeadingBlock>())
        {
            var text = GetText(heading.Inline);
            if (text.Length == 0) continue;
            var anchor = heading.TryGetAttributes()?.Id ?? "";
            entries.Add(new TocEntry(heading.Level, text, anchor));
        }
        return entries;
    }

    /// <summary>Collects the visible text of a heading's inline tree (literals + code spans).</summary>
    private static string GetText(ContainerInline? inline)
    {
        if (inline == null) return "";
        var sb = new StringBuilder();
        Collect(inline.FirstChild, sb);
        return sb.ToString().Trim();
    }

    private static void Collect(Inline? node, StringBuilder sb)
    {
        while (node != null)
        {
            switch (node)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case ContainerInline container:
                    Collect(container.FirstChild, sb);
                    break;
            }
            node = node.NextSibling;
        }
    }
}
