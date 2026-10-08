using System;
using System.Linq;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>
/// Loading a diagram into Diagram Studio and saving it used to: empty composite states and drop
/// state notes (a multi-line note's body was even read as states); drop class methods and notes
/// and strip the indent off members; and write ER "many" ends as o} / |}, which Mermaid reads as
/// something else. Each pins what a save must keep.
/// </summary>
public class StudioSaveKeepsDiagramTests
{
    private static string Save(string code)
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(code);
        return string.Join("\n", vm.GenerateMermaidCode().Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("%%")));
    }

    private const string State =
        "stateDiagram-v2\n    [*] --> Idle\n    Idle --> Running : start\n    state Running {\n        [*] --> Fast\n        Fast --> Slow\n    }\n" +
        "    note right of Idle : waiting\n    note left of Running\n        busy here\n    end note\n    Running --> [*]\n";

    [Fact]
    public void Composite_states_keep_their_contents()
    {
        var code = Save(State);
        Assert.Contains("state Running {", code);
        Assert.Contains("Fast --> Slow", code);
    }

    [Fact]
    public void State_notes_survive_and_a_note_body_is_not_a_state()
    {
        var code = Save(State);
        Assert.Contains("note right of Idle : waiting", code);
        Assert.Contains("note left of Running\n        busy here\n    end note", code);
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(State);
        Assert.DoesNotContain(vm.Nodes, n => n.Id.Contains("busy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_state_note_goes_with_its_state()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(State);
        vm.SelectNode(vm.Nodes.Single(n => n.Id == "Idle"));
        vm.DeleteSelected();
        Assert.DoesNotContain("note right of Idle", vm.GenerateMermaidCode());
    }

    private const string Class =
        "classDiagram\n    class Animal {\n        <<abstract>>\n        +String name\n        -int age$\n        +eat(food) void\n    }\n" +
        "    Animal <|-- Dog\n    note for Dog \"good boy\"\n    note \"all animals\"\n";

    [Fact]
    public void Class_methods_visibility_and_flags_survive_with_their_indent()
    {
        var code = Save(Class);
        Assert.Contains("class Animal {\n        <<abstract>>\n        +String name\n        -int age$\n        +eat(food) void\n    }", code);
    }

    [Fact]
    public void Class_notes_survive_and_follow_their_class()
    {
        var code = Save(Class);
        Assert.Contains("note for Dog \"good boy\"", code);
        Assert.Contains("note \"all animals\"", code);

        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(Class);
        vm.SelectNode(vm.Nodes.Single(n => n.Id == "Dog"));
        vm.DeleteSelected();
        code = vm.GenerateMermaidCode();
        Assert.DoesNotContain("note for Dog", code);
        Assert.Contains("note \"all animals\"", code);
    }

    [Theory]
    [InlineData("||--o{")]
    [InlineData("||--|{")]
    [InlineData("}o--||")]
    [InlineData("|o--o|")]
    public void Er_cardinalities_are_written_the_way_mermaid_reads_them(string op)
    {
        Assert.Contains($"A {op} B", Save($"erDiagram\n    A {op} B : has\n"));
    }

    [Fact]
    public void Each_saved_diagram_reads_back_to_the_same_code()
    {
        foreach (var code in new[] { State, Class, "erDiagram\n    A ||--o{ B : has\n" })
        {
            var once = Save(code);
            Assert.Equal(once, Save(once));
        }
    }
}
