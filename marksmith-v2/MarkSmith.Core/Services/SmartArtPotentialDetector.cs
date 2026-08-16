using System;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Core.AST;

namespace MarkSmith.Services;

/// <summary>The kind of SmartArt a pasted document suggests.</summary>
public enum SmartArtKind { None, Hierarchy, Process }

/// <summary>The outcome of a SmartArt-potential scan on pasted content.</summary>
public sealed record SmartArtSuggestion(SmartArtKind Kind, int Score, string Reason)
{
    public bool IsOffered => Kind != SmartArtKind.None;

    /// <summary>The SmartArt layout alias the studio should preload for this kind.</summary>
    public string LayoutAlias => Kind switch
    {
        SmartArtKind.Hierarchy => "hierarchy",
        SmartArtKind.Process => "process",
        _ => string.Empty,
    };
}

/// <summary>
/// Detects whether pasted content (ChatGPT tables, org structures, step lists) has the shape of a
/// SmartArt diagram, so the app can non-invasively offer a SmartArt preview. Conservative by
/// design: it must be genuinely structured before it suggests anything, so it never nags on
/// prose, bare tables, or small lists.
/// </summary>
public static class SmartArtPotentialDetector
{
    // Compiled once — CountOrderedSteps matched this interpreted pattern on every line of a paste.
    private static readonly Regex OrderedMarker = new(@"^(\d+)[.)]\s+", RegexOptions.Compiled);

    /// <summary>Minimum total nodes for a hierarchy suggestion (a tiny 3-item list is not an org
    /// chart, a 12-row org tree is).</summary>
    private const int HierarchyMinNodes = 4;

    /// <summary>Minimum consecutive ordered steps for a process suggestion.</summary>
    private const int ProcessMinSteps = 4;

    public static SmartArtSuggestion Detect(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new SmartArtSuggestion(SmartArtKind.None, 0, string.Empty);

        var cleaned = StripCodeBlocks(markdown);
        var ast = MarkdownAstParser.Parse(cleaned);

        // Hierarchy: a real branching tree — at least two top-level items AND at least one item
        // with two or more children — with enough total nodes to be chart-worthy. The parser nests
        // a single top-level bullet/heading under Root, so mirror the studio's convention: the
        // effective top level is Root's children, or Root's only child's children when Root has
        // exactly one.
        var (depth, total) = Measure(ast.Root);
        var topLevel = ast.Root.Children.Count >= 2
            ? ast.Root.Children
            : ast.Root.Children.Count == 1
                ? ast.Root.Children[0].Children
                : new System.Collections.Generic.List<AstNode>();
        bool branching = topLevel.Count >= 2 && topLevel.Any(c => c.Children.Count >= 2);
        if (branching && depth >= 2 && total >= HierarchyMinNodes)
        {
            return new SmartArtSuggestion(
                SmartArtKind.Hierarchy,
                depth * 10 + Math.Min(total, 20),
                "This pasted content has a nested structure — it could be shown as a SmartArt hierarchy or org chart.");
        }

        // Process: a run of at least 4 sequential ordered steps (1. 2. 3. 4. …).
        int steps = CountOrderedSteps(cleaned);
        if (steps >= ProcessMinSteps)
        {
            return new SmartArtSuggestion(
                SmartArtKind.Process,
                steps * 10,
                "This reads as a sequence of steps — it could be shown as a SmartArt process diagram.");
        }

        return new SmartArtSuggestion(SmartArtKind.None, 0, string.Empty);
    }

    private static string StripCodeBlocks(string md) =>
        Regex.Replace(md, @"```[\s\S]*?```|`[^`\n]*`", string.Empty);

    /// <summary>Pathological pastes (10k+ progressively nested lines) must never crash the app:
    /// the AST depth is attacker-controlled, so measurement is iterative with a hard depth cap
    /// instead of recursion (a recursive walk would overflow the UI-thread stack).</summary>
    private const int MaxDepth = 1024;

    private static (int depth, int total) Measure(AstNode root)
    {
        int depth = 0;
        int total = 0;
        var stack = new System.Collections.Generic.Stack<(AstNode Node, int Level)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, level) = stack.Pop();
            total++;
            if (level > depth) depth = level;
            if (level >= MaxDepth) continue; // bail below the cap — depth only matters up to ~chart size
            foreach (var child in node.Children) stack.Push((child, level + 1));
        }
        return (depth, total);
    }

    /// <summary>Longest consecutive run of ordered markers (1., 2., 3., …) — a numbered list that
    /// restarts, or one with bullets mixed in, breaks the run so a half-list never suggests.</summary>
    private static int CountOrderedSteps(string md)
    {
        int best = 0;
        int run = 0;
        int expected = 1;
        foreach (var rawLine in md.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) { run = 0; expected = 1; continue; }
            var m = OrderedMarker.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            {
                run = n == expected ? run + 1 : 1;
                expected = n + 1;
                best = Math.Max(best, run);
            }
            else
            {
                run = 0;
                expected = 1;
            }
        }
        return best;
    }
}

/// <summary>
/// Decides whether the SmartArt offer should be shown for a given document, remembering what has
/// already been offered so it never re-nags on unchanged content. "Not now" and plain dismissal
/// are the same thing: the content hash is remembered, so the offer stays quiet until the user
/// actually changes the document.
/// </summary>
public sealed class SmartArtOfferGate
{
    private readonly System.Collections.Generic.HashSet<string> _offered = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldOffer(string markdown, SmartArtSuggestion suggestion)
    {
        if (!suggestion.IsOffered) return false;
        return _offered.Add(Hash(markdown));
    }

    private static string Hash(string markdown)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(markdown ?? string.Empty);
        return Convert.ToHexString(sha.ComputeHash(bytes)).Substring(0, 16);
    }
}
