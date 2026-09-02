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
        Task,
        Folder
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

    /// <summary>
    /// Why a link exists. The map is meant to replace foldering, so an edge the author actually
    /// wrote ([[wikilink]], a relative link) carries far more meaning than one the linker merely
    /// inferred from two documents happening to share tags. <see cref="MindMapLinkKindRank.Of"/>
    /// turns that into a precedence order: a stronger reason may overwrite a weaker link between
    /// the same pair, never the other way round.
    /// </summary>
    public enum MindMapLinkKind
    {
        SharedTag = 0,
        Folder = 1,
        CrossReference = 2,
        WikiLink = 3,
        Embed = 4,
        Manual = 5
    }

    public static class MindMapLinkKindRank
    {
        /// <summary>Higher wins. Manual (a human drew it) outranks everything inferred.</summary>
        public static int Of(MindMapLinkKind kind) => (int)kind;

        public static string Describe(MindMapLinkKind kind) => kind switch
        {
            MindMapLinkKind.WikiLink => "wikilink",
            MindMapLinkKind.CrossReference => "cross-reference",
            MindMapLinkKind.Embed => "embeds",
            MindMapLinkKind.SharedTag => "shared tags",
            MindMapLinkKind.Folder => "same folder",
            _ => "linked"
        };
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
        public string? Icon { get; set; } = "\uE8A5";
        public int Progress { get; set; } = 0; // 0..100
        public List<string> Tags { get; set; } = new();
        public string? MarkdownContent { get; set; }
        public string? CreatedDate { get; set; }
        public string? ModifiedDate { get; set; }
        public bool IsCollapsed { get; set; } = false;
        public string? ParentId { get; set; }
        public List<string> ChildIds { get; set; } = new();

        /// <summary>Approximate word count of the linked document, for sizing and "biggest note" reporting.</summary>
        public int WordCount { get; set; }

        /// <summary>Marks the nodes of the first-run tutorial galaxy so the UI can offer to clear them.</summary>
        public bool IsTutorial { get; set; }

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
                WordCount = WordCount,
                CreatedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                ModifiedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                IsCollapsed = false,
                // A clone is a sibling of the original, so it keeps the parent — but the parent's
                // ChildIds is the caller's to update, and the clone starts with no children of its
                // own (copying ChildIds would give two nodes the same children).
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

        /// <summary>Why this edge exists. Defaults to Manual so links saved before this field
        /// existed keep the highest precedence and are never overwritten by an inferred one.</summary>
        public MindMapLinkKind Kind { get; set; } = MindMapLinkKind.Manual;

        /// <summary>Evidence count behind an inferred link (e.g. how many tags two documents share).</summary>
        public double Weight { get; set; } = 1.0;
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

        /// <summary>True while this is the generated first-run tour rather than the user's own map.
        /// The studio refuses to overwrite a real saved library with the tour, and offers to clear it.</summary>
        public bool IsTutorial { get; set; }

        /// <summary>Directory this map was built from, so "rescan" knows where to look.</summary>
        public string? SourceDirectory { get; set; }
    }
}
