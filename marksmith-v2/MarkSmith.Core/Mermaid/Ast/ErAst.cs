namespace MarkSmith.Mermaid.Ast;

public enum ErCardinality
{
    ExactlyOne, // ||
    ZeroOrOne,  // |o
    ZeroOrMore, // }o
    OneOrMore   // }|
}

public sealed class ErAttribute
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsPrimaryKey { get; set; } // PK
    public bool IsForeignKey { get; set; } // FK
    public string? Comment { get; set; }
}

public sealed class ErEntity
{
    public string Name { get; set; } = string.Empty;
    public List<ErAttribute> Attributes { get; } = new();
}

public sealed class ErRelationship
{
    public string Entity1 { get; set; } = string.Empty;
    public string Entity2 { get; set; } = string.Empty;
    public ErCardinality Cardinality1 { get; set; } = ErCardinality.ExactlyOne;
    public ErCardinality Cardinality2 { get; set; } = ErCardinality.ZeroOrMore;
    public bool IsIdentifying { get; set; } = true;
    public string RelationshipName { get; set; } = string.Empty;
}

public sealed class ErDiagramAst : MermaidDiagramAst
{
    public override MermaidDiagramType DiagramType => MermaidDiagramType.Er;
    public Dictionary<string, ErEntity> Entities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ErRelationship> Relationships { get; } = new();
}
