using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarkSmith.Models.MindMap
{
    public enum MindMapNodeType
    {
        Document,
        Project,
        Concept,
        Milestone,
        Note,
        Task
    }

    public enum MindMapLinkStyle
    {
        CurvedBezier,
        Straight,
        Dashed,
        SynapseGlow
    }

    public enum MindMapLinkDirection
    {
        None,
        SourceToTarget,
        TargetToSource,
        Bidirectional
    }

    public sealed class MindMapNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "New Document Node";
        public string? FilePath { get; set; }
        public string? FileExtension { get; set; }
        public MindMapNodeType NodeType { get; set; } = MindMapNodeType.Document;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 180;
        public double Height { get; set; } = 56;
        public string ColorHex { get; set; } = "#FF7C4D";
        public string? Icon { get; set; } = "📄";
        public int Progress { get; set; } = 0; // 0..100
        public List<string> Tags { get; set; } = new();
        public string? MarkdownContent { get; set; }
        public string? CreatedDate { get; set; }
        public string? ModifiedDate { get; set; }
        public bool IsCollapsed { get; set; } = false;
        public string? ParentId { get; set; }
        public List<string> ChildIds { get; set; } = new();

        public MindMapNode Clone()
        {
            return new MindMapNode
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = Title,
                FilePath = FilePath,
                FileExtension = FileExtension,
                NodeType = NodeType,
                X = X + 40,
                Y = Y + 40,
                Width = Width,
                Height = Height,
                ColorHex = ColorHex,
                Icon = Icon,
                Progress = Progress,
                Tags = new List<string>(Tags),
                MarkdownContent = MarkdownContent,
                CreatedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                ModifiedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                IsCollapsed = false,
                ParentId = ParentId,
                ChildIds = new List<string>()
            };
        }
    }

    public sealed class MindMapLink
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string SourceNodeId { get; set; } = string.Empty;
        public string TargetNodeId { get; set; } = string.Empty;
        public string? Label { get; set; } // e.g. "spawned during", "depends on", "reference"
        public string ColorHex { get; set; } = "#7C4DFF";
        public MindMapLinkStyle Style { get; set; } = MindMapLinkStyle.CurvedBezier;
        public MindMapLinkDirection Direction { get; set; } = MindMapLinkDirection.SourceToTarget;
        public double StrokeThickness { get; set; } = 2.0;
    }

    public sealed class MindMapTheme
    {
        public string Name { get; set; } = "Midnight Galaxy";
        public string BackgroundColorHex { get; set; } = "#12131C";
        public string CardBackgroundHex { get; set; } = "#1C1C28";
        public string CardBorderHex { get; set; } = "#2E2E42";
        public string TextColorHex { get; set; } = "#E8E8F0";
        public string PrimaryAccentHex { get; set; } = "#FF7C4D";
        public List<string> BranchColors { get; set; } = new()
        {
            "#FF7C4D", // Vivid Tangerine
            "#22D3EE", // Bright Cyan
            "#34D399", // Emerald Green
            "#3B82F6", // Royal Blue
            "#A855F7", // Amethyst Purple
            "#EC4899", // Rose Pink
            "#FBBF24"  // Amber Gold
        };
    }

    public sealed class MindMapDocument
    {
        public string Title { get; set; } = "Document Galaxy Library";
        public string Version { get; set; } = "1.0";
        public string LastSaved { get; set; } = DateTime.Now.ToString("o");
        public string RootNodeId { get; set; } = string.Empty;
        public double ViewportOffsetX { get; set; } = 0;
        public double ViewportOffsetY { get; set; } = 0;
        public double ZoomLevel { get; set; } = 1.0;
        public MindMapTheme Theme { get; set; } = new();
        public List<MindMapNode> Nodes { get; set; } = new();
        public List<MindMapLink> Links { get; set; } = new();
    }
}
