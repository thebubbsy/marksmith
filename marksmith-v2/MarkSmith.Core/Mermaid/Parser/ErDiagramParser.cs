namespace MarkSmith.Mermaid.Parser;

using System.Text.RegularExpressions;
using MarkSmith.Mermaid.Ast;

public static class ErDiagramParser
{
    private static readonly Regex RelationshipRegex = new(@"^([^\s]+)\s+(\|\||\|o|\}o|\}\|)(\-\-|\.\.)(\|\||o\||o\{|\|\{|\}|o\}|\}\|)\s+([^\s]+)(?:\s*:\s*""?(.*?)""?)?$", RegexOptions.IgnoreCase);
    private static readonly Regex AttributeRegex = new(@"^([^\s]+)\s+([^\s]+)(?:\s+(PK|FK))?(?:\s+(PK|FK))?(?:\s+""([^""]+)""|""([^""]+)"")?$", RegexOptions.IgnoreCase);

    public static ErDiagramAst Parse(string code)
    {
        var ast = new ErDiagramAst();
        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList();

        ErEntity? currentEntity = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("%%"))
            {
                if (line.StartsWith("%%{"))
                    ast.Directives.Add(line);
                else
                    ast.Comments.Add(line.Substring(2).Trim());
                continue;
            }

            string lower = line.ToLowerInvariant();
            if (lower == "erdiagram")
                continue;

            if (lower.StartsWith("title "))
            {
                ast.Title = line.Substring(6).Trim();
                continue;
            }

            if (line.EndsWith("{") && !line.Contains("||") && !line.Contains("}o") && !line.Contains("o{"))
            {
                string entName = line.Substring(0, line.Length - 1).Trim();
                currentEntity = GetOrCreateEntity(ast, entName);
                continue;
            }

            if (line == "}")
            {
                currentEntity = null;
                continue;
            }

            if (currentEntity != null)
            {
                ParseAttribute(line, currentEntity);
                continue;
            }

            var relMatch = RelationshipRegex.Match(line);
            if (relMatch.Success)
            {
                string e1 = relMatch.Groups[1].Value.Trim();
                string cardStr1 = relMatch.Groups[2].Value;
                string lineStyleStr = relMatch.Groups[3].Value;
                string cardStr2 = relMatch.Groups[4].Value;
                string e2 = relMatch.Groups[5].Value.Trim();
                string relName = relMatch.Groups[6].Success ? relMatch.Groups[6].Value.Trim() : string.Empty;

                GetOrCreateEntity(ast, e1);
                GetOrCreateEntity(ast, e2);

                ast.Relationships.Add(new ErRelationship
                {
                    Entity1 = e1,
                    Entity2 = e2,
                    Cardinality1 = ParseCardinality(cardStr1),
                    Cardinality2 = ParseCardinality(cardStr2),
                    IsIdentifying = lineStyleStr == "--",
                    RelationshipName = relName
                });
                continue;
            }
        }

        return ast;
    }

    private static ErEntity GetOrCreateEntity(ErDiagramAst ast, string name)
    {
        name = name.Trim();
        if (!ast.Entities.TryGetValue(name, out var entity))
        {
            entity = new ErEntity { Name = name };
            ast.Entities[name] = entity;
        }
        return entity;
    }

    private static ErCardinality ParseCardinality(string card)
    {
        return card switch
        {
            "||" => ErCardinality.ExactlyOne,
            "|o" or "o|" => ErCardinality.ZeroOrOne,
            "}o" or "o}" or "o{" => ErCardinality.ZeroOrMore,
            "}|" or "|}" or "|{" => ErCardinality.OneOrMore,
            _ => ErCardinality.ExactlyOne
        };
    }

    private static void ParseAttribute(string line, ErEntity entity)
    {
        line = line.Trim();
        if (string.IsNullOrEmpty(line)) return;

        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;

        string type = parts[0];
        string name = parts[1];
        bool isPk = line.Contains(" PK");
        bool isFk = line.Contains(" FK");

        string? comment = null;
        int quoteStart = line.IndexOf('"');
        int quoteEnd = line.LastIndexOf('"');
        if (quoteStart >= 0 && quoteEnd > quoteStart)
        {
            comment = line.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        entity.Attributes.Add(new ErAttribute
        {
            Type = type,
            Name = name,
            IsPrimaryKey = isPk,
            IsForeignKey = isFk,
            Comment = comment
        });
    }
}
