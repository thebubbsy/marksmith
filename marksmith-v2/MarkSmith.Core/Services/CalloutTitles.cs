using System.Text;
using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkSmith.Services;

// A callout's own title ("Pro tip" in `:::tip Pro tip`, `> [!tip] Pro tip` or `!!! tip "Pro tip"`).
// GitHub alerts have no title slot, so AdmonitionNormalizer writes the title as the alert's first
// paragraph wrapped in a marker span:
//
//   > [!TIP]
//   > <span class="md-callout-title">Pro tip</span>
//   >
//   > Body.
//
// UseCalloutTitles() lifts that paragraph off the AlertBlock right after parsing and keeps the text
// on the block, so every renderer shows it IN the label ("💡 Pro tip") instead of a "Tip" label
// over a bold first line. The HTML renderer is hooked here; Word, email, slides and plain text read
// CalloutTitles.Get(alert) and fall back to the kind's own label when it's null.
public static class CalloutTitles
{
    internal const string MarkerOpen = "<span class=\"md-callout-title\">";
    private const string MarkerClose = "</span>";
    private static readonly object DataKey = typeof(CalloutTitles);

    public static MarkdownPipelineBuilder UseCalloutTitles(this MarkdownPipelineBuilder builder)
    {
        if (!builder.Extensions.Contains<CalloutTitleExtension>())
            builder.Extensions.Add(new CalloutTitleExtension());
        return builder;
    }

    /// <summary>The author's title for this callout, or null for the kind's default label.</summary>
    public static string? Get(AlertBlock alert) => alert.GetData(DataKey) as string;

    /// <summary>The label to print: the author's title, else "Note", "Tip", "Important"...</summary>
    public static string Label(AlertBlock alert) => Get(alert) ?? DefaultLabel(alert.Kind.ToString());

    public static string DefaultLabel(string kind) =>
        kind.Length == 0 ? "Note" : char.ToUpperInvariant(kind[0]) + kind[1..].ToLowerInvariant();

    /// <summary>Markdown line (without the "> ") that carries <paramref name="title"/>.</summary>
    internal static string MarkerLine(string title) =>
        MarkerOpen + System.Net.WebUtility.HtmlEncode(title.Trim()) + MarkerClose;

    internal static void Lift(MarkdownDocument document)
    {
        foreach (var alert in document.Descendants<AlertBlock>().ToList())
        {
            if (alert.Count == 0 || alert[0] is not ParagraphBlock { Inline: { } inline } para) continue;
            if (inline.FirstChild is not HtmlInline { Tag: MarkerOpen } open) continue;
            if (inline.LastChild is not HtmlInline { Tag: MarkerClose } close || close == open) continue;

            var sb = new StringBuilder();
            for (var node = open.NextSibling; node is not null && node != close; node = node.NextSibling)
                AppendText(sb, node);
            var title = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (title.Length == 0) continue;

            alert.SetData(DataKey, title);
            alert.Remove(para);
        }
    }

    private static void AppendText(StringBuilder sb, Inline node)
    {
        switch (node)
        {
            case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
            case CodeInline code: sb.Append(code.Content); break;
            case HtmlEntityInline ent: sb.Append(ent.Transcoded.ToString()); break;
            case LineBreakInline: sb.Append(' '); break;
            case ContainerInline c:
                for (var child = c.FirstChild; child is not null; child = child.NextSibling) AppendText(sb, child);
                break;
        }
    }

    private sealed class CalloutTitleExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        {
            pipeline.DocumentProcessed -= Lift;
            pipeline.DocumentProcessed += Lift;
        }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not HtmlRenderer html || html.ObjectRenderers.FindExact<AlertBlockRenderer>() is not { } alerts)
                return;
            // Markdig writes the label through RenderKind(renderer, kind), which never sees the block,
            // so a TryWriter (runs first, returns false so the stock Write still runs) hands it over.
            AlertBlock? current = null;
            alerts.TryWriters.Add((_, block) => { current = block; return false; });
            var stock = alerts.RenderKind;
            alerts.RenderKind = (r, kind) =>
            {
                var title = current is { } a ? Get(a) : null;
                current = null;
                if (title is null) { stock(r, kind); return; }
                r.Write(IconOnly(kind.ToString(), stock)).WriteEscape(title).WriteLine("</p>");
            };
        }

        // The stock label is `<p class="markdown-alert-title"><svg ...></svg>Tip</p>`: keep up to
        // the icon, the caller writes the title and closes the paragraph.
        private static readonly Dictionary<string, string> Icons = new(StringComparer.OrdinalIgnoreCase);

        private static string IconOnly(string kind, Action<HtmlRenderer, StringSlice> stock)
        {
            lock (Icons)
            {
                if (Icons.TryGetValue(kind, out var icon)) return icon;
                var sw = new StringWriter();
                stock(new HtmlRenderer(sw), new StringSlice(kind));
                var s = sw.ToString();
                var end = s.LastIndexOf("</svg>", StringComparison.Ordinal);
                return Icons[kind] = end < 0 ? "" : s[..(end + 6)];
            }
        }
    }
}
