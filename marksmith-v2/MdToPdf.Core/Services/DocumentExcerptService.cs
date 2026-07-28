using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MdToPdf.Services;

/// <summary>
/// Generates a concise plain-text excerpt (1-2 sentences by default) from the opening prose of a
/// Markdown document (Task 46). Parses with Markdig and walks only top-level paragraph blocks, so
/// headings, fenced code, tables, blockquotes and list scaffolding never leak into the summary.
/// Inline markup is flattened to its visible text: emphasis/strong/strikethrough drop their markers,
/// links keep their label (not the URL), inline code keeps its literal text, and images are removed
/// entirely (they're media, not prose). The result is whitespace-collapsed, sentence-bounded, and
/// optionally length-capped with an ellipsis — suitable for a card preview, RSS description, or
/// search-result snippet.
/// </summary>
public static partial class DocumentExcerptService
{
    // Mirrors the preview pipeline so inline parsing (links, emphasis, emoji) matches what renders.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley(enableSmileys: false)
        .Build();

    // A sentence ends at . ! or ? followed by whitespace. Splitting on the lookbehind keeps the
    // terminator attached to its sentence. Deliberately simple — abbreviation edge cases (e.g.
    // "e.g. ") are acceptable for a short preview snippet.
    [GeneratedRegex(@"(?<=[\.!?])\s+")]
    private static partial Regex SentenceBoundary();

    /// <summary>
    /// Returns up to <paramref name="maxSentences"/> sentences of flattened opening prose. When
    /// <paramref name="maxLength"/> is &gt; 0 the result is trimmed to a word boundary and suffixed
    /// with an ellipsis. Empty/whitespace-only input yields an empty string.
    /// </summary>
    public static string GenerateExcerpt(string? markdown, int maxSentences = 2, int maxLength = 0)
    {
        if (string.IsNullOrWhiteSpace(markdown) || maxSentences <= 0) return "";

        var doc = Markdown.Parse(markdown, Pipeline);
        var sb = new StringBuilder();

        // Accumulate prose from top-level paragraphs until we have enough sentence material. Skipping
        // non-paragraph blocks (headings, code, quotes, lists, tables) is what keeps the excerpt clean.
        foreach (var block in doc)
        {
            if (block is not ParagraphBlock para) continue;
            AppendPlainText(para.Inline, sb);
            sb.Append(' ');
            if (SentenceBoundary().Count(sb.ToString()) >= maxSentences) break;
        }

        var text = CollapseWhitespace(sb.ToString());
        if (text.Length == 0) return "";

        text = TakeSentences(text, maxSentences);
        return maxLength > 0 ? Truncate(text, maxLength) : text;
    }

    // Keeps the first n sentences (re-joined with single spaces).
    private static string TakeSentences(string text, int maxSentences)
    {
        var sentences = SentenceBoundary().Split(text);
        if (sentences.Length <= maxSentences) return text;
        return string.Join(" ", sentences.Take(maxSentences)).Trim();
    }

    // Trims to the last whole word at/under maxLength and appends an ellipsis. A string already short
    // enough is returned untouched.
    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var cut = text.LastIndexOf(' ', maxLength);
        if (cut <= 0) cut = maxLength;
        return text[..cut].TrimEnd() + "…";
    }

    private static string CollapseWhitespace(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>Flattens an inline tree to visible text, dropping images and link URLs.</summary>
    private static void AppendPlainText(ContainerInline? inline, StringBuilder sb)
    {
        if (inline is null) return;
        var node = inline.FirstChild;
        while (node is not null)
        {
            switch (node)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LinkInline link when link.IsImage:
                    break; // media — contributes nothing to a prose excerpt
                case LinkInline link:
                    AppendPlainText(link, sb); // keep the label, drop the destination
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case ContainerInline container:
                    AppendPlainText(container, sb); // emphasis, strong, strike, etc.
                    break;
            }
            node = node.NextSibling;
        }
    }
}
