using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MarkSmith.Models.MindMap;
using MarkSmith.Services;

namespace MarkSmith.Services.MindMap
{
    public sealed class MindMapStorageService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string GetDefaultLibraryStoragePath()
        {
            // Flow through AppPaths.ConfigDir so MARKSMITH_CONFIG_DIR can redirect the whole
            // config surface — hardcoding %LOCALAPPDATA% here breaks test isolation (#27 convention).
            string dir = Path.Combine(AppPaths.ConfigDir, "MindMaps");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "library.msmap");
        }

        public async Task SaveAsync(MindMapDocument doc, string filePath)
        {
            doc.LastSaved = DateTime.Now.ToString("o");
            string json = JsonSerializer.Serialize(doc, JsonOpts);
            AtomicFile.WriteAllText(filePath, json);
            await Task.CompletedTask;
        }

        public async Task<MindMapDocument> LoadAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return CreateDefaultGalaxy();
            }

            string json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            var doc = JsonSerializer.Deserialize<MindMapDocument>(json, JsonOpts);
            return doc ?? CreateDefaultGalaxy();
        }

        public static MindMapDocument CreateDefaultGalaxy()
        {
            var doc = new MindMapDocument
            {
                Title = "Document Galaxy Vault",
                LastSaved = DateTime.Now.ToString("o")
            };

            var root = new MindMapNode
            {
                Title = "MarkSmith Document Galaxy",
                NodeType = MindMapNodeType.Project,
                X = 0,
                Y = 0,
                Width = 220,
                Height = 60,
                ColorHex = "#FF7C4D",
                Icon = "🌌",
                Progress = 100,
                Tags = new() { "#vault", "#root" },
                MarkdownContent = "# Document Galaxy Vault\nCentral hub connecting all project documents, research, and ideas across PDF, MD, and DOCX."
            };
            doc.RootNodeId = root.Id;
            doc.Nodes.Add(root);

            var doc1 = new MindMapNode
            {
                Title = "Core Architecture (MD)",
                NodeType = MindMapNodeType.Document,
                FileExtension = ".md",
                X = 320,
                Y = -120,
                Width = 190,
                Height = 56,
                ColorHex = "#22D3EE",
                Icon = "📝",
                Progress = 75,
                Tags = new() { "#architecture", "#core" },
                MarkdownContent = "## Core Architecture\nHigh performance OpenXML DrawingML compilation and live preview pipeline.",
                ParentId = root.Id
            };
            root.ChildIds.Add(doc1.Id);
            doc.Nodes.Add(doc1);

            var doc2 = new MindMapNode
            {
                Title = "Executive Proposal (DOCX)",
                NodeType = MindMapNodeType.Document,
                FileExtension = ".docx",
                X = 320,
                Y = 20,
                Width = 200,
                Height = 56,
                ColorHex = "#34D399",
                Icon = "📄",
                Progress = 90,
                Tags = new() { "#proposal", "#executive" },
                MarkdownContent = "## Executive Proposal\nComprehensive proposal and roadmap for enterprise document workflows.",
                ParentId = root.Id
            };
            root.ChildIds.Add(doc2.Id);
            doc.Nodes.Add(doc2);

            var doc3 = new MindMapNode
            {
                Title = "Technical Spec (PDF)",
                NodeType = MindMapNodeType.Document,
                FileExtension = ".pdf",
                X = 320,
                Y = 160,
                Width = 180,
                Height = 56,
                ColorHex = "#A855F7",
                Icon = "📑",
                Progress = 40,
                Tags = new() { "#specification", "#pdf" },
                MarkdownContent = "## Technical Specification\nAPI and binary compression format specifications.",
                ParentId = root.Id
            };
            root.ChildIds.Add(doc3.Id);
            doc.Nodes.Add(doc3);

            var subDoc1 = new MindMapNode
            {
                Title = "DrawingML Solver",
                NodeType = MindMapNodeType.Concept,
                X = 600,
                Y = -160,
                Width = 160,
                Height = 50,
                ColorHex = "#3B82F6",
                Icon = "📐",
                Progress = 100,
                Tags = new() { "#drawingml", "#math" },
                ParentId = doc1.Id
            };
            doc1.ChildIds.Add(subDoc1.Id);
            doc.Nodes.Add(subDoc1);

            var subDoc2 = new MindMapNode
            {
                Title = "Vector Studio Mosaic",
                NodeType = MindMapNodeType.Concept,
                X = 600,
                Y = -80,
                Width = 170,
                Height = 50,
                ColorHex = "#EC4899",
                Icon = "🎨",
                Progress = 85,
                Tags = new() { "#vector", "#mosaic" },
                ParentId = doc1.Id
            };
            doc1.ChildIds.Add(subDoc2.Id);
            doc.Nodes.Add(subDoc2);

            // Cross-link: Vector Studio Mosaic spawned a need for Executive Proposal
            doc.Links.Add(new MindMapLink
            {
                SourceNodeId = subDoc2.Id,
                TargetNodeId = doc2.Id,
                Label = "spawned during project",
                ColorHex = "#EC4899",
                Style = MindMapLinkStyle.CurvedBezier,
                Direction = MindMapLinkDirection.SourceToTarget
            });

            // Cross-link: Core Architecture references Technical Spec
            doc.Links.Add(new MindMapLink
            {
                SourceNodeId = doc1.Id,
                TargetNodeId = doc3.Id,
                Label = "references spec",
                ColorHex = "#22D3EE",
                Style = MindMapLinkStyle.Dashed,
                Direction = MindMapLinkDirection.Bidirectional
            });

            return doc;
        }

        public static string ExportToMermaid(MindMapDocument doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("mindmap");
            var root = doc.Nodes.FirstOrDefault(n => n.Id == doc.RootNodeId) ?? doc.Nodes.FirstOrDefault();
            if (root != null)
            {
                AppendMermaidNode(sb, doc, root, 1);
            }
            return sb.ToString();
        }

        private static void AppendMermaidNode(StringBuilder sb, MindMapDocument doc, MindMapNode node, int depth)
        {
            string indent = new(' ', depth * 2);
            string title = (node.Title ?? "Node").Replace("\"", "'");
            string icon = string.IsNullOrEmpty(node.Icon) ? "" : $"{node.Icon} ";
            sb.AppendLine($"{indent}root(({icon}{title}))");

            foreach (string childId in node.ChildIds)
            {
                var child = doc.Nodes.FirstOrDefault(n => n.Id == childId);
                if (child != null)
                {
                    AppendMermaidChild(sb, doc, child, depth + 1);
                }
            }
        }

        private static void AppendMermaidChild(StringBuilder sb, MindMapDocument doc, MindMapNode node, int depth)
        {
            string indent = new(' ', depth * 2);
            string title = (node.Title ?? "Node").Replace("\"", "'");
            string icon = string.IsNullOrEmpty(node.Icon) ? "" : $"{node.Icon} ";
            sb.AppendLine($"{indent}{icon}{title}");

            foreach (string childId in node.ChildIds)
            {
                var child = doc.Nodes.FirstOrDefault(n => n.Id == childId);
                if (child != null)
                {
                    AppendMermaidChild(sb, doc, child, depth + 1);
                }
            }
        }
    }
}
