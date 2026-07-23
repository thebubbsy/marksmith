namespace MdToPdf.Mermaid.Parser;

using System.Text.RegularExpressions;
using MdToPdf.Mermaid.Ast;

public static class ClassDiagramParser
{
    private static readonly Regex RelationshipRegex = new(@"^(?:""([^""]+)""\s+)?([^\s""]+)\s*(?:""([^""]+)""\s*)?(<\|--|<\|\.\.|-->|\.\.>|o--|\*--|--|\.\.)\s*(?:""([^""]+)""\s*)?([^\s""]+)(?:\s+""([^""]+)""\s*)?(?:\s*:\s*(.*))?$", RegexOptions.IgnoreCase);
    private static readonly Regex InlineMemberRegex = new(@"^([^\s:]+)\s*:\s*([+\-#~])?([^\(\)]+?)(\(.*?\))?\s*([^\(\)]+)?$", RegexOptions.IgnoreCase);
    private static readonly Regex AnnotationRegex = new(@"^<<([^>]+)>>\s*([^\s]+)?$", RegexOptions.IgnoreCase);

    public static ClassDiagramAst Parse(string code)
    {
        var ast = new ClassDiagramAst();
        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList();

        ClassNode? currentClass = null;

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
            if (lower == "classdiagram")
                continue;

            if (lower.StartsWith("title "))
            {
                ast.Title = line.Substring(6).Trim();
                continue;
            }

            if (line.StartsWith("class", StringComparison.OrdinalIgnoreCase) && line.EndsWith("{") && !line.Contains("-->") && !line.Contains("<|--"))
            {
                string className = line.Substring(5, line.Length - 6).Trim();
                currentClass = GetOrCreateClass(ast, className);
                continue;
            }

            if (line == "}")
            {
                currentClass = null;
                continue;
            }

            if (currentClass != null)
            {
                var annotMatch = AnnotationRegex.Match(line);
                if (annotMatch.Success)
                {
                    currentClass.Annotation = $"<<{annotMatch.Groups[1].Value.Trim()}>>";
                    continue;
                }

                ParseClassMember(line, currentClass);
                continue;
            }

            // Standalone class declaration: `class ClassName` or `class ClassName["Label"]`
            if (line.StartsWith("class "))
            {
                string className = line.Substring(6).Trim();
                var annotMatch = AnnotationRegex.Match(className);
                if (annotMatch.Success)
                {
                    string tag = annotMatch.Groups[1].Value.Trim();
                    string target = annotMatch.Groups[2].Value.Trim();
                    var clsNode = GetOrCreateClass(ast, target);
                    clsNode.Annotation = $"<<{tag}>>";
                }
                else
                {
                    GetOrCreateClass(ast, className);
                }
                continue;
            }

            // Standalone annotation: `<<interface>> ClassName`
            var standaloneAnnot = AnnotationRegex.Match(line);
            if (standaloneAnnot.Success && standaloneAnnot.Groups[2].Success)
            {
                string tag = standaloneAnnot.Groups[1].Value.Trim();
                string target = standaloneAnnot.Groups[2].Value.Trim();
                var clsNode = GetOrCreateClass(ast, target);
                clsNode.Annotation = $"<<{tag}>>";
                continue;
            }

            // Relationship
            var relMatch = RelationshipRegex.Match(line);
            if (relMatch.Success)
            {
                string fromCard1 = relMatch.Groups[1].Value;
                string fromCls = relMatch.Groups[2].Value;
                string fromCard2 = relMatch.Groups[3].Value;
                string relOp = relMatch.Groups[4].Value;
                string toCard1 = relMatch.Groups[5].Value;
                string toCls = relMatch.Groups[6].Value;
                string toCard2 = relMatch.Groups[7].Value;
                string label = relMatch.Groups[8].Value;

                string fromCard = !string.IsNullOrEmpty(fromCard1) ? fromCard1 : fromCard2;
                string toCard = !string.IsNullOrEmpty(toCard1) ? toCard1 : toCard2;

                GetOrCreateClass(ast, fromCls);
                GetOrCreateClass(ast, toCls);

                var relType = relOp switch
                {
                    "<|--" => ClassRelationshipType.Inheritance,
                    "<|.." => ClassRelationshipType.Realization,
                    "-->" or "--" => ClassRelationshipType.Association,
                    "..>" or ".." => ClassRelationshipType.Dependency,
                    "o--" => ClassRelationshipType.Aggregation,
                    "*--" => ClassRelationshipType.Composition,
                    _ => ClassRelationshipType.Association
                };

                ast.Relationships.Add(new ClassRelationship
                {
                    FromClass = fromCls,
                    ToClass = toCls,
                    RelationshipType = relType,
                    FromCardinality = string.IsNullOrEmpty(fromCard) ? null : fromCard,
                    ToCardinality = string.IsNullOrEmpty(toCard) ? null : toCard,
                    Label = string.IsNullOrEmpty(label) ? null : label.Trim()
                });
                continue;
            }

            // Inline member: `ClassName : +String member`
            var inlineMatch = InlineMemberRegex.Match(line);
            if (inlineMatch.Success)
            {
                string clsName = inlineMatch.Groups[1].Value;
                var clsNode = GetOrCreateClass(ast, clsName);
                string memberDef = line.Substring(clsName.Length + 1).Trim();
                ParseClassMember(memberDef, clsNode);
                continue;
            }
        }

        return ast;
    }

    private static ClassNode GetOrCreateClass(ClassDiagramAst ast, string name)
    {
        name = name.Trim();
        if (!ast.Classes.TryGetValue(name, out var node))
        {
            node = new ClassNode { Name = name };
            ast.Classes[name] = node;
        }
        return node;
    }

    private static void ParseClassMember(string memberLine, ClassNode classNode)
    {
        memberLine = memberLine.Trim();
        if (string.IsNullOrEmpty(memberLine)) return;

        ClassVisibility visibility = ClassVisibility.Public;
        if (memberLine.StartsWith("+")) { visibility = ClassVisibility.Public; memberLine = memberLine.Substring(1); }
        else if (memberLine.StartsWith("-")) { visibility = ClassVisibility.Private; memberLine = memberLine.Substring(1); }
        else if (memberLine.StartsWith("#")) { visibility = ClassVisibility.Protected; memberLine = memberLine.Substring(1); }
        else if (memberLine.StartsWith("~")) { visibility = ClassVisibility.Internal; memberLine = memberLine.Substring(1); }

        bool isStatic = memberLine.EndsWith("$") || memberLine.StartsWith("$");
        memberLine = memberLine.Trim('$').Trim();

        bool isAbstract = memberLine.EndsWith("*") || memberLine.StartsWith("*");
        memberLine = memberLine.Trim('*').Trim();

        bool isMethod = memberLine.Contains("(");

        if (isMethod)
        {
            int openParen = memberLine.IndexOf('(');
            int closeParen = memberLine.IndexOf(')');
            string namePart = memberLine.Substring(0, openParen).Trim();
            string paramsPart = closeParen > openParen ? memberLine.Substring(openParen + 1, closeParen - openParen - 1).Trim() : string.Empty;
            string returnType = closeParen < memberLine.Length - 1 ? memberLine.Substring(closeParen + 1).Trim() : string.Empty;

            var method = new ClassMember
            {
                Name = namePart,
                Type = returnType,
                Visibility = visibility,
                IsMethod = true,
                IsStatic = isStatic,
                IsAbstract = isAbstract
            };

            if (!string.IsNullOrEmpty(paramsPart))
            {
                method.Parameters.AddRange(paramsPart.Split(',').Select(p => p.Trim()));
            }

            classNode.Methods.Add(method);
        }
        else
        {
            var parts = memberLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string type = string.Empty;
            string name = memberLine;

            if (parts.Length >= 2)
            {
                type = parts[0];
                name = string.Join(" ", parts.Skip(1));
            }

            classNode.Attributes.Add(new ClassMember
            {
                Name = name,
                Type = type,
                Visibility = visibility,
                IsMethod = false,
                IsStatic = isStatic,
                IsAbstract = isAbstract
            });
        }
    }
}
