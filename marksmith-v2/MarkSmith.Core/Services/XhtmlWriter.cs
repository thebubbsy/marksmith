using System.Text;
using System.Xml;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace MarkSmith.Services;

// Serializes HTML as well-formed XHTML, for EPUB chapters. EPUB readers parse chapters as XML, so
// one bare attribute (<input checked>), one named entity (&nbsp;) or one unclosed <br> makes a
// strict reader (Apple Books, epubcheck) reject the whole chapter. Patching the HTML string with
// regexes kept missing cases; this parses it into a real DOM and writes it back out as XML, where
// every one of those is handled by construction.
internal static class XhtmlWriter
{
    public const string XhtmlNs = "http://www.w3.org/1999/xhtml";
    private const string XLinkNs = "http://www.w3.org/1999/xlink";

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param",
        "source", "track", "wbr",
    };

    // Elements an e-book chapter can't use: scripts don't run in most readers, and a <style> or
    // <link> in the body is invalid XHTML. Their content is dropped, not unwrapped.
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "noscript", "style", "link", "meta", "base", "iframe", "object", "embed", "template",
    };

    // HTML boolean attributes written bare in HTML need a value in XML.
    private static readonly HashSet<string> BooleanAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "checked", "disabled", "selected", "readonly", "multiple", "hidden", "open", "reversed",
        "required", "autofocus", "novalidate", "controls", "loop", "muted", "default", "ismap",
    };

    public static IHtmlDocument Parse(string html) =>
        new HtmlParser().ParseDocument("<!DOCTYPE html><html><head></head><body>" + html + "</body></html>");

    /// <summary>A whole HTML fragment as one XHTML string, unsplit.</summary>
    public static string Fragment(string html) =>
        string.Concat(Chapters(Parse(html), splitOnH1: false));

    /// <summary>
    /// The body's XHTML, split before every top-level &lt;h1&gt; (content ahead of the first
    /// heading is its own chunk). Splitting on the DOM, not the string, means an h1 nested in a
    /// blockquote can never cut an element in half. <paramref name="raw"/> may return ready-made
    /// XHTML to stand in for an element (a diagram figure, an equation); null serializes it as is.
    /// </summary>
    public static List<string> Chapters(IHtmlDocument doc, bool splitOnH1 = true, Func<IElement, string?>? raw = null)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var node in doc.Body!.ChildNodes)
        {
            if (splitOnH1 && node is IElement { LocalName: "h1" } && sb.ToString().Trim().Length > 0)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            Write(sb, node, XhtmlNs, raw);
        }
        if (sb.ToString().Trim().Length > 0 || chunks.Count == 0) chunks.Add(sb.ToString());
        return chunks;
    }

    private static void Write(StringBuilder sb, INode node, string parentNs, Func<IElement, string?>? raw)
    {
        switch (node)
        {
            case IText text:
                sb.Append(Escape(text.Data, attribute: false));
                break;
            case IElement el:
                WriteElement(sb, el, parentNs, raw);
                break;
            // Comments, processing instructions and doctypes carry nothing a reader shows, and an
            // HTML comment containing "--" isn't a legal XML comment.
        }
    }

    private static void WriteElement(StringBuilder sb, IElement el, string parentNs, Func<IElement, string?>? raw)
    {
        var replacement = raw?.Invoke(el);
        if (replacement is not null) { sb.Append(replacement); return; }

        var ns = el.NamespaceUri ?? XhtmlNs;
        var isHtml = ns == XhtmlNs;
        var name = isHtml ? el.LocalName.ToLowerInvariant() : el.LocalName;
        if (isHtml && Dropped.Contains(name)) return;

        if (!IsXmlName(name))
        {
            // A made-up tag the HTML parser accepted but XML can't name: keep its content.
            foreach (var child in el.ChildNodes) Write(sb, child, parentNs, raw);
            return;
        }

        sb.Append('<').Append(name);
        if (ns != parentNs) sb.Append(" xmlns=\"").Append(ns).Append('"');

        var needsXLink = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attr in el.Attributes)
        {
            string attrName;
            if (attr.NamespaceUri == XLinkNs) { attrName = "xlink:" + attr.LocalName; needsXLink = true; }
            else if (attr.Name.StartsWith("xmlns", StringComparison.Ordinal)) continue;   // we write our own
            else attrName = isHtml ? attr.Name.ToLowerInvariant() : attr.Name;

            if (attrName.StartsWith("on", StringComparison.OrdinalIgnoreCase) && isHtml) continue; // event handlers
            // epub:type is declared on every chapter's root (xmlns:epub); any other prefixed or
            // malformed name (Vue's @click, a stray quote) would make the chapter unparseable.
            var prefixedOk = attrName.StartsWith("xlink:", StringComparison.Ordinal) || attrName == "epub:type";
            if (!prefixedOk && !IsXmlName(attrName)) continue;
            if (!seen.Add(attrName)) continue;

            var value = attr.Value;
            if (value.Length == 0 && isHtml && BooleanAttributes.Contains(attrName)) value = attrName;
            sb.Append(' ').Append(attrName).Append("=\"").Append(Escape(value, attribute: true)).Append('"');
        }
        if (needsXLink) sb.Append(" xmlns:xlink=\"").Append(XLinkNs).Append('"');

        if (isHtml && VoidElements.Contains(name))
        {
            sb.Append(" />");
            return;
        }

        sb.Append('>');
        // <template> content lives in a separate fragment; everything else is ordinary children.
        foreach (var child in el.ChildNodes) Write(sb, child, ns, raw);
        sb.Append("</").Append(name).Append('>');
    }

    private static bool IsXmlName(string name)
    {
        if (name.Length == 0 || name.Contains(':')) return false;
        try { XmlConvert.VerifyNCName(name); return true; }
        catch (XmlException) { return false; }
    }

    /// <summary>XML-escapes text and drops the characters XML 1.0 forbids outright.</summary>
    public static string Escape(string s, bool attribute)
    {
        var sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"' when attribute: sb.Append("&quot;"); break;
                case '\n' when attribute: sb.Append("&#10;"); break;
                case '\t' or '\n' or '\r': sb.Append(c); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(c).Append(s[++i]);
                    }
                    else if (c < 0x20 || char.IsSurrogate(c) || c is '￾' or '￿')
                    {
                        // not allowed in XML 1.0: drop it
                    }
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
