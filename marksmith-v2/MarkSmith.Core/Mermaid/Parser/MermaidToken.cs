namespace MdToPdf.Mermaid.Parser;

public enum MermaidTokenType
{
    Identifier,
    Keyword,
    StringLiteral,
    Symbol,
    Arrow,
    Colon,
    Newline,
    Indent,
    Comment,
    Eof
}

public sealed class MermaidToken
{
    public MermaidTokenType Type { get; set; }
    public string Value { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    public int IndentLevel { get; set; }
}
