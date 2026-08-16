using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MarkSmith.Models.MindMap;

namespace MarkSmith.Services.MindMap
{
    public sealed class MindMapAutoLinker
    {
        private static readonly Regex WikiLinkRegex = new(@"\[\[(.*?)\]\]", RegexOptions.Compiled);
        private static readonly Regex MdLinkRegex = new(@"\[(.*?)\]\(((?!http:\/\/|https:\/\/).*?)\)", RegexOptions.Compiled);
        private static readonly Regex TagRegex = new(@"#([a-zA-Z0-9_\-]+)", RegexOptions.Compiled);
        private static readonly Regex HeadingRegex = new(@"^#\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

        public async Task<MindMapDocument> BuildGalaxyFromDirectoryAsync(string directoryPath, string? vaultName = null)
        {
            var doc = new MindMapDocument
            {
                Title = vaultName ?? Path.GetFileName(directoryPath) ?? "Document Galaxy Vault",
                LastSaved = DateTime.Now.ToString("o")
            };

            var root = new MindMapNode
            {
                Title = doc.Title,
                NodeType = MindMapNodeType.Project,
                X = 0,
                Y = 0,
                Width = 220,
                Height = 60,
                ColorHex = "#FF7C4D",
                Icon = "📁",
                Progress = 100,
                Tags = new() { "#vault", "#root" }
            };
            doc.RootNodeId = root.Id;
            doc.Nodes.Add(root);

            if (!Directory.Exists(directoryPath)) return doc;

            var files = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext is ".md" or ".markdown" or ".docx" or ".pdf" or ".txt";
                })
                .Take(100)
                .ToList();

            var fileNodes = new Dictionary<string, MindMapNode>(StringComparer.OrdinalIgnoreCase);
            var nameToNode = new Dictionary<string, MindMapNode>(StringComparer.OrdinalIgnoreCase);

            string[] palette = doc.Theme.BranchColors.ToArray();
            int colorIdx = 0;

            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                string baseName = Path.GetFileNameWithoutExtension(file);
                string title = baseName;
                string? markdownContent = null;
                var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (ext is ".md" or ".markdown" or ".txt")
                {
                    try
                    {
                        markdownContent = await File.ReadAllTextAsync(file);
                        var hMatch = HeadingRegex.Match(markdownContent);
                        if (hMatch.Success && !string.IsNullOrWhiteSpace(hMatch.Groups[1].Value))
                        {
                            title = hMatch.Groups[1].Value.Trim();
                        }

                        foreach (Match tm in TagRegex.Matches(markdownContent))
                        {
                            tags.Add("#" + tm.Groups[1].Value.Trim());
                        }
                    }
                    catch { /* file read best effort */ }
                }

                string icon = ext switch
                {
                    ".md" or ".markdown" => "📝",
                    ".docx" => "📄",
                    ".pdf" => "📑",
                    _ => "📄"
                };

                var node = new MindMapNode
                {
                    Title = title,
                    FilePath = file,
                    FileExtension = ext,
                    NodeType = MindMapNodeType.Document,
                    ColorHex = palette[colorIdx % palette.Length],
                    Icon = icon,
                    Tags = tags.ToList(),
                    MarkdownContent = markdownContent,
                    CreatedDate = File.GetCreationTime(file).ToString("yyyy-MM-dd"),
                    ModifiedDate = File.GetLastWriteTime(file).ToString("yyyy-MM-dd"),
                    ParentId = root.Id
                };
                colorIdx++;

                root.ChildIds.Add(node.Id);
                doc.Nodes.Add(node);
                fileNodes[file] = node;
                nameToNode[baseName] = node;
                nameToNode[title] = node;
            }

            // Cross-linking pass: Scan markdown content for wikilinks and relative file links
            foreach (var kvp in fileNodes)
            {
                var sourceNode = kvp.Value;
                if (string.IsNullOrEmpty(sourceNode.MarkdownContent)) continue;

                // 1. [[WikiLinks]]
                foreach (Match m in WikiLinkRegex.Matches(sourceNode.MarkdownContent))
                {
                    string targetName = m.Groups[1].Value.Trim();
                    if (nameToNode.TryGetValue(targetName, out var targetNode) && targetNode.Id != sourceNode.Id)
                    {
                        if (!doc.Links.Any(l => (l.SourceNodeId == sourceNode.Id && l.TargetNodeId == targetNode.Id) ||
                                                (l.SourceNodeId == targetNode.Id && l.TargetNodeId == sourceNode.Id)))
                        {
                            doc.Links.Add(new MindMapLink
                            {
                                SourceNodeId = sourceNode.Id,
                                TargetNodeId = targetNode.Id,
                                Label = "wikilink",
                                ColorHex = sourceNode.ColorHex,
                                Style = MindMapLinkStyle.CurvedBezier,
                                Direction = MindMapLinkDirection.SourceToTarget
                            });
                        }
                    }
                }

                // 2. Relative Markdown Links [text](./relative/path.md)
                foreach (Match m in MdLinkRegex.Matches(sourceNode.MarkdownContent))
                {
                    string linkTarget = m.Groups[2].Value.Trim();
                    string targetName = Path.GetFileNameWithoutExtension(linkTarget);
                    if (nameToNode.TryGetValue(targetName, out var targetNode) && targetNode.Id != sourceNode.Id)
                    {
                        if (!doc.Links.Any(l => (l.SourceNodeId == sourceNode.Id && l.TargetNodeId == targetNode.Id) ||
                                                (l.SourceNodeId == targetNode.Id && l.TargetNodeId == sourceNode.Id)))
                        {
                            doc.Links.Add(new MindMapLink
                            {
                                SourceNodeId = sourceNode.Id,
                                TargetNodeId = targetNode.Id,
                                Label = "cross-reference",
                                ColorHex = sourceNode.ColorHex,
                                Style = MindMapLinkStyle.Dashed,
                                Direction = MindMapLinkDirection.SourceToTarget
                            });
                        }
                    }
                }

                // 3. Shared Tag Connections
                foreach (var otherKvp in fileNodes)
                {
                    var otherNode = otherKvp.Value;
                    if (otherNode.Id == sourceNode.Id) continue;
                    var sharedTags = sourceNode.Tags.Intersect(otherNode.Tags, StringComparer.OrdinalIgnoreCase).ToList();
                    if (sharedTags.Count >= 2)
                    {
                        if (!doc.Links.Any(l => (l.SourceNodeId == sourceNode.Id && l.TargetNodeId == otherNode.Id) ||
                                                (l.SourceNodeId == otherNode.Id && l.TargetNodeId == sourceNode.Id)))
                        {
                            doc.Links.Add(new MindMapLink
                            {
                                SourceNodeId = sourceNode.Id,
                                TargetNodeId = otherNode.Id,
                                Label = $"shared {string.Join(", ", sharedTags)}",
                                ColorHex = "#7C4DFF",
                                Style = MindMapLinkStyle.SynapseGlow,
                                Direction = MindMapLinkDirection.Bidirectional
                            });
                        }
                    }
                }
            }

            return doc;
        }
    }
}
