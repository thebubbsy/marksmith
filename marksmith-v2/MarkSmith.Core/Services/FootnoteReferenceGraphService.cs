using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public enum ReferenceNodeType
{
    FootnoteDef,
    FootnoteRef,
    CitationDef,
    CitationRef,
    Heading,
    InternalLink
}

public record ReferenceNode(string Id, string Label, ReferenceNodeType NodeType, int LineNumber);
public record ReferenceEdge(string SourceId, string TargetId, string EdgeType);

public class ReferenceGraphResult
{
    public List<ReferenceNode> Nodes { get; } = new();
    public List<ReferenceEdge> Edges { get; } = new();
    public List<string> BrokenReferences { get; } = new();
    public List<string> OrphanDefinitions { get; } = new();
    public int TotalCitations => Nodes.Count(n => n.NodeType == ReferenceNodeType.CitationRef);
    public int TotalFootnotes => Nodes.Count(n => n.NodeType == ReferenceNodeType.FootnoteRef);
}

/// <summary>
/// Scans Markdown documents to build an interconnected reference graph and detect broken or orphan citations/footnotes.
/// </summary>
public static class FootnoteReferenceGraphService
{
    private static readonly Regex FootnoteRefRegex = new(@"\[\^([^\]]+)\](?!:)", RegexOptions.Compiled);
    private static readonly Regex FootnoteDefRegex = new(@"^\[\^([^\]]+)\]:\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex CitationRefRegex = new(@"\[@([a-zA-Z0-9_\-]+)\]", RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex InternalLinkRegex = new(@"\[([^\]]+)\]\(#([a-zA-Z0-9_\-]+)\)", RegexOptions.Compiled);

    public static ReferenceGraphResult BuildGraph(string markdown)
    {
        var result = new ReferenceGraphResult();
        if (string.IsNullOrWhiteSpace(markdown))
            return result;

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var definedFootnotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedFootnotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definedHeadings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedHeadings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            // 1. Footnote Definitions: [^1]: text
            var fnDefMatch = FootnoteDefRegex.Match(line);
            if (fnDefMatch.Success)
            {
                string tag = fnDefMatch.Groups[1].Value.Trim();
                string label = fnDefMatch.Groups[2].Value.Trim();
                definedFootnotes.Add(tag);
                result.Nodes.Add(new ReferenceNode($"fn_def_{tag}", label, ReferenceNodeType.FootnoteDef, lineNum));
                continue;
            }

            // 2. Headings: # Title
            var hMatch = HeadingRegex.Match(line);
            if (hMatch.Success)
            {
                string title = hMatch.Groups[2].Value.Trim();
                string slug = Slugify(title);
                definedHeadings.Add(slug);
                result.Nodes.Add(new ReferenceNode($"h_{slug}", title, ReferenceNodeType.Heading, lineNum));
            }

            // 3. Footnote References: [^1]
            foreach (Match m in FootnoteRefRegex.Matches(line))
            {
                string tag = m.Groups[1].Value.Trim();
                referencedFootnotes.Add(tag);
                string refId = $"fn_ref_{tag}_{lineNum}_{m.Index}";
                result.Nodes.Add(new ReferenceNode(refId, $"[^{tag}]", ReferenceNodeType.FootnoteRef, lineNum));
                result.Edges.Add(new ReferenceEdge(refId, $"fn_def_{tag}", "FootnoteCall"));
            }

            // 4. Citation References: [@smith2024]
            foreach (Match m in CitationRefRegex.Matches(line))
            {
                string key = m.Groups[1].Value.Trim();
                string citeId = $"cite_{key}_{lineNum}_{m.Index}";
                result.Nodes.Add(new ReferenceNode(citeId, $"[@{key}]", ReferenceNodeType.CitationRef, lineNum));
            }

            // 5. Internal Links: [Go here](#section-name)
            foreach (Match m in InternalLinkRegex.Matches(line))
            {
                string label = m.Groups[1].Value.Trim();
                string slug = m.Groups[2].Value.Trim();
                referencedHeadings.Add(slug);
                string linkId = $"link_{slug}_{lineNum}_{m.Index}";
                result.Nodes.Add(new ReferenceNode(linkId, label, ReferenceNodeType.InternalLink, lineNum));
                result.Edges.Add(new ReferenceEdge(linkId, $"h_{slug}", "AnchorJump"));
            }
        }

        // Detect broken references (called but not defined)
        foreach (var rf in referencedFootnotes)
        {
            if (!definedFootnotes.Contains(rf))
                result.BrokenReferences.Add($"Footnote [^{rf}] is referenced but has no definition.");
        }

        foreach (var rh in referencedHeadings)
        {
            if (!definedHeadings.Contains(rh))
                result.BrokenReferences.Add($"Heading anchor [#{rh}] is referenced but does not exist.");
        }

        // Detect orphan definitions (defined but never referenced)
        foreach (var df in definedFootnotes)
        {
            if (!referencedFootnotes.Contains(df))
                result.OrphanDefinitions.Add($"Footnote [^{df}] is defined but never referenced in the document.");
        }

        return result;
    }

    private static string Slugify(string text)
    {
        return Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    }
}
