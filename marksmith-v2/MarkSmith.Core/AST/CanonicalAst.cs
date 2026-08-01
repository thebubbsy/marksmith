using System;
using System.Collections.Generic;

namespace MarkSmith.Core.AST
{
    public class CanonicalAst
    {
        public string? RequestedLayout { get; set; }
        public AstNode Root { get; set; } = new AstNode { NodeId = "root_0", Text = "Document Root" };

        public IEnumerable<AstNode> GetAllNodes()
        {
            var queue = new Queue<AstNode>();
            queue.Enqueue(Root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                yield return current;
                foreach (var child in current.Children)
                {
                    queue.Enqueue(child);
                }
            }
        }
    }
}
