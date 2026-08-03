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

            // Headings form their own hierarchy, nested by '#' level.
            var headingStack = new Stack<(int level, AstNode node)>();
            headingStack.Push((0, ast.Root));

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
                    // Heading format — nested by level (## A / ### B => B is a child of A).
                    int level = 0;
                    while (level < line.Length && line[level] == '#') level++;
                    string content = line.Substring(level).Trim();
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    while (headingStack.Count > 1 && headingStack.Peek().level >= level)
                    {
                        headingStack.Pop();
                    }

                    var parent = headingStack.Peek().node;

                    var node = new AstNode
                    {
                        NodeId = $"node_{nodeCounter++}",
                        Depth = level,
                        ParentId = parent.NodeId,
                        Text = content
                    };

                    parent.Children.Add(node);
                    headingStack.Push((level, node));
                }
            }

            ast.RequestedLayout = layoutAlias;
            return ast;
        }
    }
}
