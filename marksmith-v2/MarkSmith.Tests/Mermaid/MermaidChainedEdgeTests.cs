namespace MarkSmith.Core.Tests.Mermaid;

using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Parser;
using MarkSmith.Mermaid.Sync;
using Xunit;

// QODER task 2 (Issue #7 / ISS-020): chained arrow lines must yield one edge per operator,
// and %% {"id":...} spatial comments must be strippable from editor text without loss.
public class MermaidChainedEdgeTests
{
    [Fact]
    public void Chained_labeled_edges_produce_two_edges_and_three_nodes()
    {
        var ast = FlowchartParser.Parse("flowchart TD\nA -->|Yes| B -->|No| C");

        Assert.Equal(3, ast.Nodes.Count);
        Assert.True(ast.Nodes.ContainsKey("A"));
        Assert.True(ast.Nodes.ContainsKey("B"));
        Assert.True(ast.Nodes.ContainsKey("C"));

        Assert.Equal(2, ast.Edges.Count);
        Assert.Equal("A", ast.Edges[0].FromId);
        Assert.Equal("B", ast.Edges[0].ToId);
        Assert.Equal("Yes", ast.Edges[0].Label);
        Assert.Equal("B", ast.Edges[1].FromId);
        Assert.Equal("C", ast.Edges[1].ToId);
        Assert.Equal("No", ast.Edges[1].Label);
    }

    [Fact]
    public void Chained_plain_edges_walk_the_whole_line()
    {
        var ast = FlowchartParser.Parse("graph LR\nStart --> Middle --> End2 --> Final");

        Assert.Equal(4, ast.Nodes.Count);
        Assert.Equal(3, ast.Edges.Count);
        Assert.Equal("Start", ast.Edges[0].FromId);
        Assert.Equal("Middle", ast.Edges[0].ToId);
        Assert.Equal("Middle", ast.Edges[1].FromId);
        Assert.Equal("End2", ast.Edges[1].ToId);
        Assert.Equal("End2", ast.Edges[2].FromId);
        Assert.Equal("Final", ast.Edges[2].ToId);
    }

    [Fact]
    public void Chained_edges_with_node_shapes_keep_labels_and_shapes()
    {
        var ast = FlowchartParser.Parse("flowchart TD\nA[Start] -->|Yes| B{Decide} -->|No| C(Stop)");

        Assert.Equal(3, ast.Nodes.Count);
        Assert.Equal(FlowNodeShape.RhombusDiamond, ast.Nodes["B"].Shape);
        Assert.Equal(FlowNodeShape.RoundedRectangle, ast.Nodes["C"].Shape);
        Assert.Equal(2, ast.Edges.Count);
        Assert.Equal("Yes", ast.Edges[0].Label);
        Assert.Equal("No", ast.Edges[1].Label);
    }

    [Fact]
    public void Single_edge_lines_are_unchanged_by_the_chain_walker()
    {
        var ast = FlowchartParser.Parse("flowchart TD\nA -->|Go| B\nB --> C");

        Assert.Equal(3, ast.Nodes.Count);
        Assert.Equal(2, ast.Edges.Count);
        Assert.Equal("Go", ast.Edges[0].Label);
        Assert.Null(ast.Edges[1].Label);
    }

    [Fact]
    public void No_node_id_contains_an_arrow_operator_after_chained_parse()
    {
        var ast = FlowchartParser.Parse("flowchart TD\nA -->|Yes| B -->|No| C");
        Assert.All(ast.Nodes.Keys, id => Assert.DoesNotContain("-->", id));
    }
}

public class MermaidSpatialMetadataServiceTests
{
    private const string MdWithPositions =
        "# Doc\n\n```mermaid\n%% {\"id\":\"A\", \"x\":100, \"y\":200}\n%% {\"id\":\"B\", \"x\":300, \"y\":400}\nflowchart TD\nA --> B\n```\n\ntail text\n";

    [Fact]
    public void Strip_removes_spatial_lines_and_stashes_them()
    {
        var cleaned = MermaidSpatialMetadataService.Strip(MdWithPositions, out var stash);

        Assert.DoesNotContain("%% {\"id\":", cleaned);
        Assert.Contains("flowchart TD", cleaned);
        Assert.Contains("A --> B", cleaned);
        Assert.Contains("tail text", cleaned);
        Assert.Single(stash);
        Assert.Equal(2, stash[0].Count);
    }

    [Fact]
    public void Reinject_restores_stripped_lines()
    {
        var cleaned = MermaidSpatialMetadataService.Strip(MdWithPositions, out var stash);
        var restored = MermaidSpatialMetadataService.Reinject(cleaned, stash);

        Assert.Contains("%% {\"id\":\"A\", \"x\":100, \"y\":200}", restored);
        Assert.Contains("%% {\"id\":\"B\", \"x\":300, \"y\":400}", restored);
        Assert.Contains("A --> B", restored);
    }

    [Fact]
    public void Strip_leaves_init_directives_and_plain_comments_alone()
    {
        var md = "```mermaid\n%%{init: {\"theme\":\"dark\"}}%%\n%% a plain note\nflowchart TD\nA --> B\n```";
        var cleaned = MermaidSpatialMetadataService.Strip(md, out var stash);

        Assert.Contains("%%{init:", cleaned);
        Assert.Contains("%% a plain note", cleaned);
        Assert.Empty(stash);
    }

    [Fact]
    public void Reinject_is_idempotent_when_fence_already_has_positions()
    {
        MermaidSpatialMetadataService.Strip(MdWithPositions, out var stash);
        var doubled = MermaidSpatialMetadataService.Reinject(MdWithPositions, stash);

        // Fence already carried the positions — nothing should be duplicated.
        int count = System.Text.RegularExpressions.Regex.Matches(doubled, "\"id\":\"A\"").Count;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Strip_without_metadata_returns_markdown_unchanged()
    {
        var md = "```mermaid\nflowchart TD\nA --> B\n```";
        var cleaned = MermaidSpatialMetadataService.Strip(md, out var stash);

        Assert.Empty(stash);
        Assert.Equal(md, cleaned);
    }
}
