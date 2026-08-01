using System;
using System.Collections.Generic;

namespace MarkSmith.Core.AST
{
    public class AstNode
    {
        public string NodeId { get; set; } = Guid.NewGuid().ToString("N");
        public int Depth { get; set; } = 0;
        public string? ParentId { get; set; }
        public AstNodeType NodeType { get; set; } = AstNodeType.Text;
        public string Text { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? ImagePath { get; set; }
        public List<string> SemanticTags { get; set; } = new List<string>();
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
        public List<AstNode> Children { get; set; } = new List<AstNode>();
    }
}
