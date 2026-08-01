using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarkSmith.Core.Glox.Builder;

namespace MarkSmith.Cli
{
    public class JsonLayoutDef
    {
        public string UniqueId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Desc { get; set; } = string.Empty;
        public string Category { get; set; } = "process";
        public JsonLayoutNode RootNode { get; set; } = new JsonLayoutNode();
    }

    public class JsonLayoutNode
    {
        public string Name { get; set; } = string.Empty;
        public string? Algorithm { get; set; }
        public string? Shape { get; set; }
        public JsonForEach? ForEach { get; set; }
        public JsonLayoutNode[] Children { get; set; } = Array.Empty<JsonLayoutNode>();
    }

    public class JsonForEach
    {
        public string Axis { get; set; } = "ch";
        public string RefNode { get; set; } = string.Empty;
        public JsonLayoutNode? Child { get; set; }
    }

    public class JsonLayoutParser
    {
        public static GloxLayoutDefinition Parse(string jsonContent)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var def = JsonSerializer.Deserialize<JsonLayoutDef>(jsonContent, options);
            
            if (def == null) throw new Exception("Invalid JSON layout definition.");

            var builder = new GloxBuilder()
                .WithMetadata(def.UniqueId, def.Title, def.Desc, def.Category)
                .ConfigureRoot(root => MapNode(root, def.RootNode));

            return builder.Build();
        }

        private static void MapNode(GloxLayoutNode coreNode, JsonLayoutNode jsonNode)
        {
            coreNode.Name = string.IsNullOrEmpty(jsonNode.Name) ? "node" : jsonNode.Name;
            
            if (!string.IsNullOrEmpty(jsonNode.Algorithm))
            {
                coreNode.SetAlgorithm(jsonNode.Algorithm);
            }

            if (!string.IsNullOrEmpty(jsonNode.Shape))
            {
                coreNode.SetShape(jsonNode.Shape);
            }

            if (jsonNode.ForEach != null)
            {
                coreNode.AddForEach(jsonNode.ForEach.Axis, jsonNode.ForEach.RefNode, child => 
                {
                    if (jsonNode.ForEach.Child != null)
                    {
                        MapNode(child, jsonNode.ForEach.Child);
                    }
                });
            }

            foreach (var childJson in jsonNode.Children)
            {
                coreNode.AddChild(childJson.Name ?? "child", child => MapNode(child, childJson));
            }
        }
    }
}
