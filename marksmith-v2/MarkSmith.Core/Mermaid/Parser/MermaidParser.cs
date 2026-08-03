namespace MarkSmith.Mermaid.Parser;

using MarkSmith.Mermaid.Ast;

public static class MermaidParser
{
    public static MermaidParseResult Parse(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Failure("Source code is empty.");

        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("%%"))
                        .ToList();

        if (lines.Count == 0)
            return Failure("No active diagram code found.");

        string firstLine = lines[0].ToLowerInvariant();
        string header = firstLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];

        try
        {
            MermaidDiagramAst ast = header switch
            {
                "flowchart" or "graph" => FlowchartParser.Parse(code),
                "sequencediagram" => SequenceParser.Parse(code),
                "classdiagram" => ClassDiagramParser.Parse(code),
                "statediagram" or "statediagram-v2" => StateDiagramParser.Parse(code),
                "gantt" => GanttParser.Parse(code),
                "erdiagram" => ErDiagramParser.Parse(code),
                "mindmap" => MindmapParser.Parse(code),
                _ => throw new FormatException($"Unsupported diagram header '{header}'.")
            };

            return new MermaidParseResult { Ast = ast };
        }
        catch (Exception ex)
        {
            return Failure($"Parsing error: {ex.Message}");
        }
    }

    private static MermaidParseResult Failure(string error)
    {
        var res = new MermaidParseResult();
        res.Diagnostics.Add(new MermaidDiagnostic { Message = error, Line = 1, Column = 1 });
        return res;
    }
}
