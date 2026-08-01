namespace MdToPdf.Mermaid.Parser;

using System.Text.RegularExpressions;

public sealed class MermaidLexer
{
    public static List<MermaidToken> Tokenize(string code)
    {
        var tokens = new List<MermaidToken>();
        if (string.IsNullOrWhiteSpace(code))
            return tokens;

        string[] rawLines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        for (int lineIdx = 0; lineIdx < rawLines.Length; lineIdx++)
        {
            int lineNumber = lineIdx + 1;
            string line = rawLines[lineIdx];

            // Count leading spaces for indentation
            int indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (trimmed.StartsWith("%%"))
            {
                tokens.Add(new MermaidToken
                {
                    Type = MermaidTokenType.Comment,
                    Value = trimmed[2..].Trim(),
                    Line = lineNumber,
                    Column = indent + 1,
                    IndentLevel = indent
                });
                continue;
            }

            // Lex content
            tokens.Add(new MermaidToken
            {
                Type = MermaidTokenType.Identifier,
                Value = trimmed,
                Line = lineNumber,
                Column = indent + 1,
                IndentLevel = indent
            });
        }

        tokens.Add(new MermaidToken
        {
            Type = MermaidTokenType.Eof,
            Value = string.Empty,
            Line = rawLines.Length + 1,
            Column = 1,
            IndentLevel = 0
        });

        return tokens;
    }
}
