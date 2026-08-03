namespace MarkSmith.Mermaid.Parser;

using MarkSmith.Mermaid.Ast;

public sealed class MermaidDiagnostic
{
    public string Message { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    public bool IsWarning { get; set; }
}

public sealed class MermaidParseResult
{
    public MermaidDiagramAst? Ast { get; set; }
    public List<MermaidDiagnostic> Diagnostics { get; } = new();
    public bool IsSuccess => Ast != null && !Diagnostics.Any(d => !d.IsWarning);
}
