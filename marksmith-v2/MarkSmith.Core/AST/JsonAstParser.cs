using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MarkSmith.Core.AST
{
    public static class JsonAstParser
    {
        public static CanonicalAst Parse(string jsonText)
        {
            var ast = new CanonicalAst();
            using var doc = JsonDocument.Parse(jsonText);
            var rootElem = doc.RootElement;

            if (rootElem.TryGetProperty("layout", out var layoutProp))
            {
                ast.RequestedLayout = layoutProp.GetString();
            }

            int nodeCounter = 1;

            if (rootElem.TryGetProperty("root", out var rootNodeElem))
            {
                ParseNode(rootNodeElem, ast.Root, ref nodeCounter);
            }
            else if (rootElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rootElem.EnumerateArray())
                {
                    var childNode = new AstNode
                    {
                        NodeId = $"node_{nodeCounter++}",
                        Depth = 1,
                        ParentId = ast.Root.NodeId
                    };
                    ParseNode(item, childNode, ref nodeCounter);
                    ast.Root.Children.Add(childNode);
                }
            }

            return ast;
        }

        private static void ParseNode(JsonElement elem, AstNode node, ref int counter)
        {
            if (elem.TryGetProperty("id", out var idProp)) node.NodeId = idProp.GetString() ?? node.NodeId;
            if (elem.TryGetProperty("text", out var textProp)) node.Text = textProp.GetString() ?? string.Empty;
            if (elem.TryGetProperty("description", out var descProp)) node.Description = descProp.GetString() ?? string.Empty;
            if (elem.TryGetProperty("image", out var imgProp))
            {
                node.ImagePath = imgProp.GetString();
                node.NodeType = AstNodeType.Image;
            }

            if (elem.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsProp.EnumerateArray())
                {
                    if (tag.GetString() is string tagStr) node.SemanticTags.Add(tagStr);
                }
            }

            if (elem.TryGetProperty("children", out var childrenProp) && childrenProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var childElem in childrenProp.EnumerateArray())
                {
                    var childNode = new AstNode
                    {
                        NodeId = $"node_{counter++}",
                        Depth = node.Depth + 1,
                        ParentId = node.NodeId
                    };
                    ParseNode(childElem, childNode, ref counter);
                    node.Children.Add(childNode);
                }
            }
        }
    }
}
