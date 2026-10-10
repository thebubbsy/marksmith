using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkSmith.Services;

// `*[HTML]: HyperText Markup Language` turns every "HTML" in the text into an AbbreviationInline.
// The HTML renderer draws it as <abbr title>, but every renderer that walks inlines itself (Word,
// email, slides, Google Docs, the TOC, table extraction...) has no case for it and silently dropped
// the word: "the HTML spec" exported as "the  spec". UsePlainAbbreviations() puts the word back as
// plain text right after parsing, so a renderer that can't show a tooltip still prints the word.
public static class PlainAbbreviations
{
    public static MarkdownPipelineBuilder UsePlainAbbreviations(this MarkdownPipelineBuilder builder)
    {
        if (!builder.Extensions.Contains<PlainAbbreviationExtension>())
            builder.Extensions.Add(new PlainAbbreviationExtension());
        return builder;
    }

    internal static void Flatten(MarkdownDocument document)
    {
        foreach (var abbr in document.Descendants<AbbreviationInline>().ToList())
            abbr.ReplaceBy(new LiteralInline(abbr.Abbreviation?.Label ?? ""));
    }

    private sealed class PlainAbbreviationExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        {
            pipeline.DocumentProcessed -= Flatten;
            pipeline.DocumentProcessed += Flatten;
        }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }
    }
}
