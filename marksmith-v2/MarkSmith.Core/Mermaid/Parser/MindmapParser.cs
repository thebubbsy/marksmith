namespace MarkSmith.Mermaid.Parser;

using System.Text.RegularExpressions;
using MarkSmith.Mermaid.Ast;

public static class MindmapParser
{
    public static MindmapAst Parse(string code)
    {
        var ast = new MindmapAst();
        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        Stack<(MindmapNode Node, int Indent)> nodeStack = new();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            int indent = 0;
            while (indent < rawLine.Length && rawLine[indent] == ' ') indent++;

            string trimmed = rawLine.Trim();

            if (trimmed.StartsWith("%%"))
            {
                if (trimmed.StartsWith("%%{"))
                    ast.Directives.Add(trimmed);
                else
                    ast.Comments.Add(trimmed.Substring(2).Trim());
                continue;
            }

            if (trimmed.Equals("mindmap", StringComparison.OrdinalIgnoreCase))
                continue;

            if (trimmed.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
            {
                ast.Title = trimmed.Substring(6).Trim();
                continue;
            }

            if (trimmed.StartsWith("::icon("))
            {
                if (nodeStack.Count > 0)
                {
                    nodeStack.Peek().Node.Icon = trimmed;
                }
                continue;
            }

            var node = ParseNode(trimmed, indent);

            if (ast.Root == null)
            {
                ast.Root = node;
                nodeStack.Push((node, indent));
            }
            else
            {
                while (nodeStack.Count > 0 && nodeStack.Peek().Indent >= indent)
                {
                    nodeStack.Pop();
                }

                if (nodeStack.Count > 0)
                {
                    nodeStack.Peek().Node.Children.Add(node);
                }
                else
                {
                    ast.Root.Children.Add(node);
                }

                nodeStack.Push((node, indent));
            }
        }

        return ast;
    }

    private static MindmapNode ParseNode(string text, int indent)
    {
        text = text.Trim();
        MindmapNodeShape shape = MindmapNodeShape.Default;
        string content = text;
        string? icon = null;

        // Check icon suffix inline: `node text ::icon(...)`
        int iconIdx = text.IndexOf("::icon(", StringComparison.OrdinalIgnoreCase);
        if (iconIdx > 0)
        {
            icon = text.Substring(iconIdx).Trim();
            content = text.Substring(0, iconIdx).Trim();
        }

        if (content.StartsWith("((") && content.EndsWith("))"))
        {
            shape = MindmapNodeShape.Circle;
            content = content.Substring(2, content.Length - 4);
        }
        else if (content.StartsWith("))") && content.EndsWith("(("))
        {
            shape = MindmapNodeShape.Bang;
            content = content.Substring(2, content.Length - 4);
        }
        else if (content.StartsWith(")") && content.EndsWith("("))
        {
            shape = MindmapNodeShape.Cloud;
            content = content.Substring(1, content.Length - 2);
        }
        else if (content.StartsWith("[") && content.EndsWith("]"))
        {
            shape = MindmapNodeShape.Square;
            content = content.Substring(1, content.Length - 2);
        }
        else if (content.StartsWith("(") && content.EndsWith(")"))
        {
            shape = MindmapNodeShape.Rounded;
            content = content.Substring(1, content.Length - 2);
        }

        return new MindmapNode
        {
            Text = content.Trim(),
            Shape = shape,
            Icon = icon,
            IndentLevel = indent
        };
    }
}
