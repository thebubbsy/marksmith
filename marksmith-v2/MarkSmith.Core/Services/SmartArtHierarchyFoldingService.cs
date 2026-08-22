using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Node representation in a hierarchical SmartArt tree structure.
/// </summary>
public class SmartArtTreeNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public int Level { get; set; }
    public List<SmartArtTreeNode> Children { get; } = new();
    public bool IsCollapsed { get; set; }
}

/// <summary>
/// Service that enhances rendered SmartArt hierarchy trees with interactive collapsible sub-branches in SVG/HTML.
/// </summary>
public static class SmartArtHierarchyFoldingService
{
    private static readonly Regex BulletRegex = new(@"^(\s*)(?:[-*+]|\d+\.)\s+(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Parses an indented Markdown list into a hierarchical tree structure.
    /// </summary>
    public static SmartArtTreeNode ParseHierarchyTree(string listMarkdown, string rootTitle = "Organization")
    {
        var root = new SmartArtTreeNode { Title = rootTitle, Level = 0 };
        if (string.IsNullOrWhiteSpace(listMarkdown))
            return root;

        var lines = listMarkdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<(int indent, SmartArtTreeNode node)>();
        stack.Push((-1, root));

        foreach (var line in lines)
        {
            var match = BulletRegex.Match(line);
            if (!match.Success) continue;

            int indent = match.Groups[1].Value.Length;
            string text = match.Groups[2].Value.Trim();

            var newNode = new SmartArtTreeNode { Title = text };

            while (stack.Count > 1 && stack.Peek().indent >= indent)
            {
                stack.Pop();
            }

            var parent = stack.Peek().node;
            newNode.Level = parent.Level + 1;
            parent.Children.Add(newNode);
            stack.Push((indent, newNode));
        }

        return root;
    }

    /// <summary>
    /// Injects interactive collapsible triggers and data attributes into generated SVG markup.
    /// </summary>
    public static string InjectCollapsibleSvgInteractivity(string svgMarkup)
    {
        if (string.IsNullOrWhiteSpace(svgMarkup) || !svgMarkup.Contains("<svg"))
            return svgMarkup;

        // Injects an inline SVG script and style for interactive tree collapse/expand
        const string scriptSnippet = """
            <style>
                .smartart-toggle-btn { cursor: pointer; transition: transform 0.15s ease; fill: #58a6ff; }
                .smartart-toggle-btn:hover { fill: #79c0ff; transform: scale(1.15); }
                .smartart-branch-collapsed { display: none !important; opacity: 0; }
            </style>
            <script>
                function toggleSmartArtBranch(evt, branchId) {
                    evt.stopPropagation();
                    const el = document.getElementById(branchId);
                    if (!el) return;
                    const isCollapsed = el.classList.toggle('smartart-branch-collapsed');
                    const target = evt.currentTarget;
                    if (target) {
                        const txt = target.querySelector('text') || target;
                        txt.textContent = isCollapsed ? '+' : '-';
                    }
                }
            </script>
            """;

        int insertPos = svgMarkup.IndexOf('>');
        if (insertPos >= 0)
        {
            return svgMarkup.Insert(insertPos + 1, scriptSnippet);
        }

        return svgMarkup;
    }
}
