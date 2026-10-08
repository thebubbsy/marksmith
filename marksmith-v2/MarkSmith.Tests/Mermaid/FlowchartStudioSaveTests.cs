using System;
using System.Linq;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>
/// A Diagram Studio save turned classDef/class/style/click/linkStyle lines into nodes (labelled
/// with the whole line, which is invalid Mermaid), dropped every subgraph, and wrote two-way
/// arrows as one-way. These pin a lossless round trip.
/// </summary>
public class FlowchartStudioSaveTests
{
    private const string Styled =
        "flowchart LR\n" +
        "    subgraph one [Group One]\n" +
        "        A[Start] --> B{Ok?}\n" +
        "    end\n" +
        "    B -->|yes| C((Done))\n" +
        "    B -.->|no| A\n" +
        "    C <--> D\n" +
        "    classDef hot fill:#f96\n" +
        "    class C,D hot\n" +
        "    style A stroke:#333\n" +
        "    click A \"https://example.com\"\n" +
        "    linkStyle 3 stroke:#f00\n";

    private static string Body(string code) =>
        string.Join("\n", code.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("%%")));

    private static MermaidStudioViewModel Load(string code)
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(code);
        return vm;
    }

    [Fact]
    public void Style_lines_are_kept_verbatim_not_read_as_nodes()
    {
        var ast = Assert.IsType<FlowchartDiagramAst>(MermaidParser.Parse(Styled).Ast);
        Assert.Equal(new[] { "A", "B", "C", "D" }, ast.Nodes.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(5, ast.StyleLines.Count);
        var code = MermaidCodeGenerator.Generate(ast);
        Assert.Contains("    classDef hot fill:#f96\n", code.Replace("\r\n", "\n"));
        Assert.DoesNotContain("[\"classDef", code);
    }

    [Fact]
    public void Studio_save_keeps_subgraphs_styles_and_two_way_arrows()
    {
        var code = Body(Load(Styled).GenerateMermaidCode());
        Assert.Contains("subgraph one [\"Group One\"]", code);
        int sub = code.IndexOf("subgraph", StringComparison.Ordinal), end = code.IndexOf("end", sub, StringComparison.Ordinal);
        Assert.Contains("A[Start]", code.Substring(sub, end - sub));
        Assert.Contains("<-->", code);
        foreach (var line in new[] { "classDef hot fill:#f96", "class C,D hot", "style A stroke:#333", "click A \"https://example.com\"", "linkStyle 3 stroke:#f00" })
            Assert.Contains(line, code);
        Assert.Equal(4, Load(code).Nodes.Count); // and it reads back as the same four nodes
    }

    [Fact]
    public void Lines_for_deleted_nodes_or_edges_go_with_them()
    {
        var vm = Load(Styled);
        vm.SelectNode(vm.Nodes.Single(n => n.Id == "D"));
        vm.DeleteSelected(); // also removes the C <--> D edge, and its linkStyle with it
        var code = Body(vm.GenerateMermaidCode());
        Assert.Contains("class C hot", code);
        Assert.DoesNotContain("class C,D", code);
        Assert.DoesNotContain("linkStyle", code);

        vm.SelectNode(vm.Nodes.Single(n => n.Id == "A"));
        vm.DeleteSelected();
        code = Body(vm.GenerateMermaidCode());
        Assert.DoesNotContain("style A", code);
        Assert.DoesNotContain("click A", code);
        Assert.Contains("subgraph one", code); // B is still in it

        vm.SelectNode(vm.Nodes.Single(n => n.Id == "B"));
        vm.DeleteSelected();
        Assert.DoesNotContain("subgraph one", Body(vm.GenerateMermaidCode())); // now empty: gone
    }

    [Fact]
    public void LinkStyle_numbers_follow_their_edge_when_an_earlier_edge_is_deleted()
    {
        var vm = Load(Styled);
        vm.SelectedConnector = vm.Connectors[0]; // A --> B
        vm.DeleteSelected();
        Assert.Contains("linkStyle 2 stroke:#f00", Body(vm.GenerateMermaidCode())); // still C <--> D, now the third edge
    }

    [Fact]
    public void Round_trip_is_stable()
    {
        var once = Body(Load(Styled).GenerateMermaidCode());
        Assert.Equal(once, Body(Load(once).GenerateMermaidCode()));
    }
}
