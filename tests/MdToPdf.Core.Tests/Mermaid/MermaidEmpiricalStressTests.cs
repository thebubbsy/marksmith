namespace MdToPdf.Core.Tests.Mermaid;

using System;
using System.Linq;
using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Generator;
using MdToPdf.Mermaid.Parser;
using Xunit;
using Xunit.Abstractions;

public class MermaidEmpiricalStressTests
{
    private readonly ITestOutputHelper _output;

    public MermaidEmpiricalStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    // 1. Flowchart Node Shapes
    [InlineData("flowchart TD\n    A[Rectangle Node]\n    B(Rounded Node)\n    C([Stadium Node])\n    D[[Subroutine Node]]\n    E[(Database Node)]\n    F((Circle Node))\n    G>Asymmetric Node]\n    H{Rhombus Node}\n    I{{Hexagon Node}}\n    J[/Parallelogram Node/]\n    K[/Trapezoid Node\\]\n", "Flowchart Node Shapes")]
    // 2. Flowchart Arrow Variations
    [InlineData("flowchart LR\n    A --> B\n    C --- D\n    E ==> F\n    G === H\n    I -.- J\n    K -.-> L\n    M <--> N\n    O x--x P\n    Q o--o R\n", "Flowchart Arrow Variations")]
    // 3. Flowchart Labeled Edges
    [InlineData("flowchart TD\n    A -- Label 1 --> B\n    C == Label 2 ==> D\n    E -.- Label 3 -.-> F\n    G --|Pipe Label|--> H\n", "Flowchart Labeled Edges")]
    // 4. Flowchart Subgraphs
    [InlineData("flowchart TD\n    subgraph SG1 [\"Subgraph One\"]\n        A[Node A]\n        B[Node B]\n    end\n    subgraph SG2 [\"Subgraph Two\"]\n        C[Node C]\n    end\n    A --> C\n", "Flowchart Subgraphs")]
    // 5. Sequence Diagram Participants, Messages & Activations
    [InlineData("sequenceDiagram\n    autonumber\n    title Sequence Test\n    actor Alice\n    participant Bob as Robert\n    Alice->>+Bob: Request Data\n    Bob-->>-Alice: Return Data\n    Alice->Bob: Open Message\n    Alice-->Bob: Open Dashed\n    Alice-xBob: Cross Message\n", "Sequence Diagram")]
    // 6. Sequence Diagram Blocks & Notes
    [InlineData("sequenceDiagram\n    participant A\n    participant B\n    Note left of A: Left Note\n    Note right of B: Right Note\n    Note over A,B: Over Note\n    loop Check Status\n        A->>B: Ping\n    end\n    alt OK\n        A->>B: Proceed\n    else Failed\n        A->>B: Abort\n    end\n", "Sequence Blocks and Notes")]
    // 7. Class Diagram Members and Visibility
    [InlineData("classDiagram\n    class Animal {\n        +String name$\n        -int id\n        #void eat()*\n        ~bool isAlive\n    }\n    class Dog {\n        +void bark()\n    }\n    Dog --|> Animal\n", "Class Members and Visibility")]
    // 8. Class Diagram Relationships & Cardinalities
    [InlineData("classDiagram\n    Company \"1\" *-- \"0..*\" Employee : employs\n    Student \"*\" --o \"1\" Team : belongsTo\n    Car ..> Engine : dependsOn\n    Window ..|> IWidget : implements\n", "Class Relationships")]
    // 9. State Diagram States & Choice/Fork/Join
    [InlineData("stateDiagram-v2\n    [*] --> Idle\n    state c <<choice>>\n    state f <<fork>>\n    state j <<join>>\n    Idle --> c : Evaluate\n    c --> Processing : Valid\n    c --> Error : Invalid\n", "State Diagram Choice/Fork/Join")]
    // 10. State Diagram Composite States
    [InlineData("stateDiagram-v2\n    state Active {\n        [*] --> Sub1\n        Sub1 --> Sub2 : Next\n    }\n    Active --> Off : PowerOff\n", "State Diagram Composite")]
    // 11. Gantt Chart Tasks, Flags & Sections
    [InlineData("gantt\n    title Project Timeline\n    dateFormat YYYY-MM-DD\n    axisFormat %Y-%m-%d\n    section Phase 1\n    Design task :active, t1, 2026-01-01, 10d\n    Coding task :done, crit, t2, after t1, 20d\n    Release milestone :milestone, m1, 2026-02-01, 0d\n", "Gantt Chart")]
    // 12. ER Diagram Entities, Attributes & Cardinality
    [InlineData("erDiagram\n    CUSTOMER {\n        string name\n        int id PK\n        string email FK \"User email\"\n    }\n    ORDER {\n        int orderId PK\n    }\n    CUSTOMER ||--o{ ORDER : places\n", "ER Diagram")]
    // 13. Mindmap Hierarchy & Shapes
    [InlineData("mindmap\n    root((Main Root))\n        [Square Branch]\n            (Rounded Child)\n        )Cloud Branch(\n            ))Bang Child((\n", "Mindmap Diagram")]
    public void StressTest_RoundtripFidelity(string inputCode, string category)
    {
        _output.WriteLine($"=== Testing Category: {category} ===");

        // Step 1: Parse original code
        var parse1 = MermaidParser.Parse(inputCode);
        Assert.True(parse1.IsSuccess, $"[Parser 1 Failed] Category: {category}. Diagnostics: {string.Join(", ", parse1.Diagnostics.Select(d => d.Message))}");
        Assert.NotNull(parse1.Ast);

        // Step 2: Generate Mermaid code from AST 1
        string code1 = MermaidCodeGenerator.Generate(parse1.Ast!);
        _output.WriteLine($"--- Generated Code 1 ---\n{code1}\n------------------------");

        // Step 3: Parse generated code 1 -> AST 2
        var parse2 = MermaidParser.Parse(code1);
        Assert.True(parse2.IsSuccess, $"[Parser 2 Failed] Category: {category}. Diagnostics: {string.Join(", ", parse2.Diagnostics.Select(d => d.Message))}");
        Assert.NotNull(parse2.Ast);

        // Step 4: Generate Mermaid code from AST 2
        string code2 = MermaidCodeGenerator.Generate(parse2.Ast!);
        _output.WriteLine($"--- Generated Code 2 ---\n{code2}\n------------------------");

        // Step 5: Verify Idempotency (code1 == code2)
        Assert.Equal(code1, code2);

        // Step 6: Verify AST 1 vs AST 2 structural equality
        AssertEqualAst(parse1.Ast!, parse2.Ast!, category);
    }

    private void AssertEqualAst(MermaidDiagramAst ast1, MermaidDiagramAst ast2, string category)
    {
        Assert.Equal(ast1.DiagramType, ast2.DiagramType);
        Assert.Equal(ast1.Title, ast2.Title);

        switch (ast1)
        {
            case FlowchartDiagramAst flow1 when ast2 is FlowchartDiagramAst flow2:
                Assert.Equal(flow1.Direction, flow2.Direction);
                Assert.Equal(flow1.Nodes.Count, flow2.Nodes.Count);
                foreach (var kvp in flow1.Nodes)
                {
                    Assert.True(flow2.Nodes.TryGetValue(kvp.Key, out var n2), $"Missing node '{kvp.Key}' in AST2 for {category}");
                    Assert.Equal(kvp.Value.Text, n2.Text);
                    Assert.Equal(kvp.Value.Shape, n2.Shape);
                }
                Assert.Equal(flow1.Edges.Count, flow2.Edges.Count);
                for (int i = 0; i < flow1.Edges.Count; i++)
                {
                    Assert.Equal(flow1.Edges[i].FromId, flow2.Edges[i].FromId);
                    Assert.Equal(flow1.Edges[i].ToId, flow2.Edges[i].ToId);
                    Assert.Equal(flow1.Edges[i].Label, flow2.Edges[i].Label);
                    Assert.Equal(flow1.Edges[i].LineStyle, flow2.Edges[i].LineStyle);
                    Assert.Equal(flow1.Edges[i].StartHead, flow2.Edges[i].StartHead);
                    Assert.Equal(flow1.Edges[i].EndHead, flow2.Edges[i].EndHead);
                }
                break;

            case SequenceDiagramAst seq1 when ast2 is SequenceDiagramAst seq2:
                Assert.Equal(seq1.AutoNumber, seq2.AutoNumber);
                Assert.Equal(seq1.Participants.Count, seq2.Participants.Count);
                for (int i = 0; i < seq1.Participants.Count; i++)
                {
                    Assert.Equal(seq1.Participants[i].Id, seq2.Participants[i].Id);
                    Assert.Equal(seq1.Participants[i].Alias, seq2.Participants[i].Alias);
                    Assert.Equal(seq1.Participants[i].Type, seq2.Participants[i].Type);
                }
                Assert.Equal(seq1.Messages.Count, seq2.Messages.Count);
                for (int i = 0; i < seq1.Messages.Count; i++)
                {
                    Assert.Equal(seq1.Messages[i].FromId, seq2.Messages[i].FromId);
                    Assert.Equal(seq1.Messages[i].ToId, seq2.Messages[i].ToId);
                    Assert.Equal(seq1.Messages[i].Text, seq2.Messages[i].Text);
                    Assert.Equal(seq1.Messages[i].MessageType, seq2.Messages[i].MessageType);
                    Assert.Equal(seq1.Messages[i].ActivateTarget, seq2.Messages[i].ActivateTarget);
                    Assert.Equal(seq1.Messages[i].DeactivateTarget, seq2.Messages[i].DeactivateTarget);
                }
                break;

            case ClassDiagramAst class1 when ast2 is ClassDiagramAst class2:
                Assert.Equal(class1.Classes.Count, class2.Classes.Count);
                foreach (var kvp in class1.Classes)
                {
                    Assert.True(class2.Classes.TryGetValue(kvp.Key, out var c2), $"Missing class '{kvp.Key}' in AST2");
                    Assert.Equal(kvp.Value.Attributes.Count, c2.Attributes.Count);
                    Assert.Equal(kvp.Value.Methods.Count, c2.Methods.Count);
                }
                Assert.Equal(class1.Relationships.Count, class2.Relationships.Count);
                for (int i = 0; i < class1.Relationships.Count; i++)
                {
                    Assert.Equal(class1.Relationships[i].FromClass, class2.Relationships[i].FromClass);
                    Assert.Equal(class1.Relationships[i].ToClass, class2.Relationships[i].ToClass);
                    Assert.Equal(class1.Relationships[i].RelationshipType, class2.Relationships[i].RelationshipType);
                    Assert.Equal(class1.Relationships[i].FromCardinality, class2.Relationships[i].FromCardinality);
                    Assert.Equal(class1.Relationships[i].ToCardinality, class2.Relationships[i].ToCardinality);
                }
                break;

            case StateDiagramAst state1 when ast2 is StateDiagramAst state2:
                Assert.Equal(state1.IsV2, state2.IsV2);
                Assert.Equal(state1.States.Count, state2.States.Count);
                Assert.Equal(state1.Transitions.Count, state2.Transitions.Count);
                break;

            case GanttChartAst gantt1 when ast2 is GanttChartAst gantt2:
                Assert.Equal(gantt1.DateFormat, gantt2.DateFormat);
                Assert.Equal(gantt1.AxisFormat, gantt2.AxisFormat);
                Assert.Equal(gantt1.Sections.Count, gantt2.Sections.Count);
                break;

            case ErDiagramAst er1 when ast2 is ErDiagramAst er2:
                Assert.Equal(er1.Entities.Count, er2.Entities.Count);
                Assert.Equal(er1.Relationships.Count, er2.Relationships.Count);
                break;

            case MindmapAst mm1 when ast2 is MindmapAst mm2:
                Assert.NotNull(mm1.Root);
                Assert.NotNull(mm2.Root);
                Assert.Equal(mm1.Root!.Text, mm2.Root!.Text);
                Assert.Equal(mm1.Root.Shape, mm2.Root.Shape);
                break;
        }
    }
}
