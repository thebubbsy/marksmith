namespace MarkSmith.Services;

/// <summary>A node in a nested document outline: a heading plus its deeper child headings.</summary>
public sealed class OutlineNode
{
    public int Level { get; init; }            // 1 (H1) .. 6 (H6)
    public string Text { get; init; } = "";
    public string Anchor { get; init; } = "";
    public List<OutlineNode> Children { get; } = new();
}

/// <summary>
/// Filters a document's heading outline by maximum depth and prunes empty sub-trees (Task 49). Builds
/// on <see cref="TocExtractorService"/> so the anchors match the rendered preview exactly. Two shapes
/// are offered: a flat depth filter (<see cref="FilterFlat"/>) that keeps headings at or above a
/// cutoff level (e.g. H1..H3), and a nested <see cref="BuildOutline"/> that arranges those headings
/// into a parent/child forest and drops branches that end up empty. Depth is clamped to the valid
/// H1..H6 range.
/// </summary>
public static class OutlineFilterService
{
    /// <summary>Headings with <c>Level &lt;= maxDepth</c>, in document order (flat).</summary>
    public static IReadOnlyList<TocEntry> FilterFlat(string? markdown, int maxDepth)
    {
        int depth = ClampDepth(maxDepth);
        return TocExtractorService.Extract(markdown)
            .Where(e => e.Level <= depth)
            .ToList();
    }

    /// <summary>Nested outline of headings at or above <paramref name="maxDepth"/>, empty branches pruned.</summary>
    public static IReadOnlyList<OutlineNode> BuildOutline(string? markdown, int maxDepth) =>
        BuildOutline(TocExtractorService.Extract(markdown), maxDepth);

    /// <summary>Nested outline from a pre-extracted entry list (no re-parse).</summary>
    public static IReadOnlyList<OutlineNode> BuildOutline(IReadOnlyList<TocEntry> entries, int maxDepth)
    {
        int depth = ClampDepth(maxDepth);
        var tree = BuildTree(entries.Where(e => e.Level <= depth));
        Prune(tree);
        return tree;
    }

    // Folds a flat, document-ordered entry list into a forest using a level stack: each heading nests
    // under the nearest preceding heading that is strictly shallower.
    private static List<OutlineNode> BuildTree(IEnumerable<TocEntry> entries)
    {
        var roots = new List<OutlineNode>();
        var stack = new Stack<OutlineNode>();
        foreach (var e in entries)
        {
            var node = new OutlineNode { Level = e.Level, Text = e.Text, Anchor = e.Anchor };
            while (stack.Count > 0 && stack.Peek().Level >= node.Level) stack.Pop();
            if (stack.Count == 0) roots.Add(node);
            else stack.Peek().Children.Add(node);
            stack.Push(node);
        }
        return roots;
    }

    // Post-order removal of nodes that carry no text and no surviving children — the "empty sub-tree"
    // cleanup. (TocExtractor already skips text-less headings, so this is a safety net for callers
    // that hand BuildOutline a synthetic entry list.)
    private static void Prune(List<OutlineNode> nodes)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            Prune(nodes[i].Children);
            if (string.IsNullOrWhiteSpace(nodes[i].Text) && nodes[i].Children.Count == 0)
                nodes.RemoveAt(i);
        }
    }

    private static int ClampDepth(int maxDepth) => Math.Clamp(maxDepth, 1, 6);
}
