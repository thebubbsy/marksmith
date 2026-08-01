using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.AST
{
    public static class MarkdownAstParser
    {
        public static CanonicalAst Parse(string markdownText)
        {
            var ast = new CanonicalAst();
            var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            string? layoutAlias = null;
            int nodeCounter = 1;

            var stack = new Stack<(int indent, AstNode node)>();
            stack.Push((-1, ast.Root));

            bool inFrontmatter = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine;

                if (line.Trim() == "---")
                {
                    inFrontmatter = !inFrontmatter;
                    continue;
                }

                if (inFrontmatter)
                {
                    if (line.Contains("layout:"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1)
                        {
                            layoutAlias = parts[1].Trim();
                        }
                    }
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                // Check list items
                var match = Regex.Match(line, @"^(?<indent>\s*)(?:[-*+]|\d+\.)\s+(?<content>.*)$");
                if (match.Success)
                {
                    int indentLength = match.Groups["indent"].Value.Length;
                    string content = match.Groups["content"].Value.Trim();

                    while (stack.Count > 1 && stack.Peek().indent >= indentLength)
                    {
                        stack.Pop();
                    }

                    var parent = stack.Peek().node;

                    var node = new AstNode
                    {
                        NodeId = $"node_{nodeCounter++}",
                        Depth = parent.Depth + 1,
                        ParentId = parent.NodeId,
                        Text = content
                    };

                    // Check for markdown image: ![alt](url)
                    var imgMatch = Regex.Match(content, @"^!\[(?<alt>.*?)\]\((?<url>.*?)\)$");
                    if (imgMatch.Success)
                    {
                        node.NodeType = AstNodeType.Image;
                        node.Text = imgMatch.Groups["alt"].Value;
                        node.ImagePath = imgMatch.Groups["url"].Value;
                    }

                    parent.Children.Add(node);
                    stack.Push((indentLength, node));
                }
                else if (line.TrimStart().StartsWith("#"))
                {
                    // Heading format
                    int level = 0;
                    while (level < line.Length && line[level] == '#') level++;
                    string content = line.Substring(level).Trim();

                    var node = new AstNode
                    {
                        NodeId = $"node_{nodeCounter++}",
                        Depth = level,
                        ParentId = ast.Root.NodeId,
                        Text = content
                    };

                    ast.Root.Children.Add(node);
                }
            }

            ast.RequestedLayout = layoutAlias;
            return ast;
        }
    }
}
