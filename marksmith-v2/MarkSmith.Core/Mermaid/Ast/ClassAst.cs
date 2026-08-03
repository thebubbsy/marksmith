namespace MarkSmith.Mermaid.Ast;

public enum ClassVisibility { Public, Private, Protected, Internal, None }

public sealed class ClassMember
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public ClassVisibility Visibility { get; set; } = ClassVisibility.Public;
    public bool IsMethod { get; set; }
    public List<string> Parameters { get; } = new();
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
}

public sealed class ClassNode
{
    public string Name { get; set; } = string.Empty;
    public string? Annotation { get; set; } // e.g. <<interface>>, <<abstract>>, <<service>>
    public List<ClassMember> Attributes { get; } = new();
    public List<ClassMember> Methods { get; } = new();
}

public enum ClassRelationshipType
{
    Inheritance,   // <|--
    Realization,   // <|..
    Association,   // --> or --
    Dependency,    // ..> or ..
    Aggregation,   // o--
    Composition    // *--
}

public sealed class ClassRelationship
{
    public string FromClass { get; set; } = string.Empty;
    public string ToClass { get; set; } = string.Empty;
    public ClassRelationshipType RelationshipType { get; set; } = ClassRelationshipType.Inheritance;
    public string? FromCardinality { get; set; } // e.g. "1", "0..*"
    public string? ToCardinality { get; set; }
    public string? Label { get; set; }
}

public sealed class ClassDiagramAst : MermaidDiagramAst
{
    public override MermaidDiagramType DiagramType => MermaidDiagramType.Class;
    public Dictionary<string, ClassNode> Classes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ClassRelationship> Relationships { get; } = new();
}
