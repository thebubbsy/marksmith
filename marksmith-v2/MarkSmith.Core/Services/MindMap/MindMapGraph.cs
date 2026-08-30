using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MarkSmith.Models.MindMap;

namespace MarkSmith.Services.MindMap
{
    /// <summary>
    /// Structural operations over a <see cref="MindMapDocument"/>: repairing a map that was
    /// hand-edited, half-written or produced by an older build, and answering the topology
    /// questions the studio asks ("what is this document connected to", "what is floating free").
    ///
    /// Every entry point here is defensive on purpose. A .msmap is a plain JSON file on the user's
    /// disk that they are invited to edit, sync and merge; nothing downstream (layout, canvas,
    /// DOCX export) should have to re-check that a link's endpoints exist or that the parent/child
    /// pointers agree with each other.
    /// </summary>
    public static class MindMapGraph
    {
        public const double MinNodeWidth = 90;
        public const double MaxNodeWidth = 620;
        public const double MinNodeHeight = 34;
        public const double MaxNodeHeight = 420;

        /// <summary>
        /// Repairs a document in place and reports what it had to fix. Idempotent: normalizing an
        /// already-clean document changes nothing and reports an empty result.
        /// </summary>
        public static MindMapRepairReport Normalize(MindMapDocument doc)
        {
            var report = new MindMapRepairReport();
            if (doc == null) return report;

            doc.Nodes ??= new List<MindMapNode>();
            doc.Links ??= new List<MindMapLink>();
            doc.Theme ??= new MindMapTheme();

            // 1. Nodes must have unique, non-empty ids — everything else keys off them.
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in doc.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id) || !seenIds.Add(node.Id))
                {
                    string oldId = node.Id;
                    node.Id = Guid.NewGuid().ToString("N");
                    seenIds.Add(node.Id);
                    Reparent(doc, oldId, node.Id);
                    report.RepairedNodeIds++;
                }

                node.Title = string.IsNullOrWhiteSpace(node.Title) ? "Untitled" : node.Title.Trim();
                node.ColorHex = NormalizeHex(node.ColorHex, "#FF7C4D");
                node.Progress = Math.Clamp(node.Progress, 0, 100);
                node.WordCount = Math.Max(0, node.WordCount);
                node.Width = ClampOrDefault(node.Width, MinNodeWidth, MaxNodeWidth, 180);
                node.Height = ClampOrDefault(node.Height, MinNodeHeight, MaxNodeHeight, 56);
                node.X = Finite(node.X);
                node.Y = Finite(node.Y);
                node.Tags = NormalizeTags(node.Tags);
                node.ChildIds ??= new List<string>();
            }

            var byId = doc.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

            // 2. Parent/child pointers have to agree in both directions, and a node cannot be its
            //    own ancestor — a cycle here sends every recursive walk (layout, Mermaid export,
            //    subtree delete) into infinite recursion.
            foreach (var node in doc.Nodes)
            {
                if (node.ParentId != null && (node.ParentId == node.Id || !byId.ContainsKey(node.ParentId)))
                {
                    node.ParentId = null;
                    report.ClearedDanglingParents++;
                }
            }

            foreach (var node in doc.Nodes)
            {
                if (CreatesAncestorCycle(byId, node))
                {
                    node.ParentId = null;
                    report.BrokenCycles++;
                }
            }

            // ParentId is the authority: rebuild every ChildIds list from it so the two can never
            // drift (a child listed under two parents used to be drawn twice, once per parent).
            // The author's existing ordering is preserved for children that were already listed;
            // anything only ParentId knew about is appended.
            var childrenByParent = doc.Nodes
                .Where(n => n.ParentId != null)
                .GroupBy(n => n.ParentId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(n => n.Id).ToList(), StringComparer.Ordinal);

            foreach (var node in doc.Nodes)
            {
                var truth = childrenByParent.TryGetValue(node.Id, out var kids)
                    ? new HashSet<string>(kids, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);

                var ordered = new List<string>(truth.Count);
                var placed = new HashSet<string>(StringComparer.Ordinal);
                foreach (string id in node.ChildIds)
                {
                    if (truth.Contains(id) && placed.Add(id)) ordered.Add(id);
                }
                foreach (string id in childrenByParent.TryGetValue(node.Id, out var all) ? all : Enumerable.Empty<string>())
                {
                    if (placed.Add(id)) ordered.Add(id);
                }

                // Only a genuine membership change counts as a repair, so normalizing an
                // already-clean document reports nothing.
                if (!truth.SetEquals(node.ChildIds) || node.ChildIds.Count != ordered.Count)
                {
                    report.RepairedChildLists++;
                }
                node.ChildIds = ordered;
            }

            // 3. Links: drop dangling and self edges, collapse duplicates keeping the strongest.
            var keptLinks = new List<MindMapLink>(doc.Links.Count);
            var byPair = new Dictionary<(string, string), MindMapLink>();
            foreach (var link in doc.Links)
            {
                if (string.IsNullOrWhiteSpace(link.SourceNodeId) || string.IsNullOrWhiteSpace(link.TargetNodeId) ||
                    !byId.ContainsKey(link.SourceNodeId) || !byId.ContainsKey(link.TargetNodeId))
                {
                    report.DroppedDanglingLinks++;
                    continue;
                }

                if (link.SourceNodeId == link.TargetNodeId)
                {
                    report.DroppedSelfLinks++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(link.Id)) link.Id = Guid.NewGuid().ToString("N");
                link.ColorHex = NormalizeHex(link.ColorHex, "#7C4DFF");
                link.StrokeThickness = ClampOrDefault(link.StrokeThickness, 0.5, 12, 2.0);
                link.Weight = double.IsFinite(link.Weight) && link.Weight > 0 ? link.Weight : 1.0;

                var key = PairKey(link.SourceNodeId, link.TargetNodeId);
                if (byPair.TryGetValue(key, out var existing))
                {
                    report.MergedDuplicateLinks++;
                    if (MindMapLinkKindRank.Of(link.Kind) > MindMapLinkKindRank.Of(existing.Kind))
                    {
                        keptLinks[keptLinks.IndexOf(existing)] = link;
                        byPair[key] = link;
                    }
                    continue;
                }

                byPair[key] = link;
                keptLinks.Add(link);
            }
            doc.Links = keptLinks;

            // 4. A map always needs a root to hang tree layouts and Mermaid export off.
            if (string.IsNullOrWhiteSpace(doc.RootNodeId) || !byId.ContainsKey(doc.RootNodeId))
            {
                var newRoot = doc.Nodes.FirstOrDefault(n => n.ParentId == null && n.NodeType == MindMapNodeType.Project)
                              ?? doc.Nodes.FirstOrDefault(n => n.ParentId == null)
                              ?? doc.Nodes.FirstOrDefault();
                if (newRoot != null)
                {
                    doc.RootNodeId = newRoot.Id;
                    newRoot.ParentId = null;
                    report.ReassignedRoot = true;
                }
            }

            doc.ZoomLevel = ClampOrDefault(doc.ZoomLevel, 0.15, 4.0, 1.0);
            doc.ViewportOffsetX = Finite(doc.ViewportOffsetX);
            doc.ViewportOffsetY = Finite(doc.ViewportOffsetY);
            if (string.IsNullOrWhiteSpace(doc.Title)) doc.Title = "Document Galaxy";

            return report;
        }

        private static void Reparent(MindMapDocument doc, string oldId, string newId)
        {
            if (string.IsNullOrEmpty(oldId)) return;
            foreach (var n in doc.Nodes)
            {
                if (n.ParentId == oldId) n.ParentId = newId;
                for (int i = 0; i < n.ChildIds.Count; i++)
                {
                    if (n.ChildIds[i] == oldId) n.ChildIds[i] = newId;
                }
            }
            if (doc.RootNodeId == oldId) doc.RootNodeId = newId;
        }

        private static bool CreatesAncestorCycle(Dictionary<string, MindMapNode> byId, MindMapNode node)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            string? cursor = node.ParentId;
            while (cursor != null && byId.TryGetValue(cursor, out var parent))
            {
                if (!seen.Add(cursor)) return true;
                cursor = parent.ParentId;
            }
            return false;
        }

        public static (string, string) PairKey(string a, string b) =>
            string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

        private static double Finite(double v) => double.IsFinite(v) ? v : 0;

        private static double ClampOrDefault(double value, double min, double max, double fallback) =>
            double.IsFinite(value) && value > 0 ? Math.Clamp(value, min, max) : fallback;

        /// <summary>
        /// Compares two tags ignoring the leading "#" and case, so a map that mixes "api" and
        /// "#api" — which any hand-edited or imported file will — still treats them as one tag.
        /// </summary>
        public static bool TagEquals(string? a, string? b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.Trim().TrimStart('#'), b.Trim().TrimStart('#'), StringComparison.OrdinalIgnoreCase);
        }

        public static List<string> NormalizeTags(IEnumerable<string>? tags)
        {
            var result = new List<string>();
            if (tags == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in tags)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string t = raw.Trim().TrimStart('#').Trim();
                if (t.Length == 0 || t.Length > 48) continue;
                t = "#" + t;
                if (seen.Add(t)) result.Add(t);
            }
            return result;
        }

        /// <summary>
        /// Validates and canonicalizes a colour to "#RRGGBB". Anything that is not real hex falls
        /// back — an unchecked value reached the DOCX writer as a raw srgbClr val and produced a
        /// package Word refuses to open.
        /// </summary>
        public static string NormalizeHex(string? hex, string fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            string s = hex.Trim().TrimStart('#');
            if (s.Length == 3)
            {
                s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
            }
            if (s.Length == 8) s = s[2..]; // #AARRGGBB → drop alpha
            if (s.Length != 6) return fallback;
            foreach (char c in s)
            {
                if (!Uri.IsHexDigit(c)) return fallback;
            }
            return "#" + s.ToUpperInvariant();
        }

        // ---- Topology queries ----

        /// <summary>Every node directly reachable from <paramref name="nodeId"/>: parent, children
        /// and both ends of any cross-link. This is the "constellation" the studio lights up when a
        /// document is selected.</summary>
        public static HashSet<string> NeighborsOf(MindMapDocument doc, string nodeId)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (doc == null || string.IsNullOrEmpty(nodeId)) return result;

            var node = doc.Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node == null) return result;

            if (node.ParentId != null) result.Add(node.ParentId);
            foreach (var c in node.ChildIds) result.Add(c);
            foreach (var l in doc.Links)
            {
                if (l.SourceNodeId == nodeId) result.Add(l.TargetNodeId);
                else if (l.TargetNodeId == nodeId) result.Add(l.SourceNodeId);
            }
            result.Remove(nodeId);
            return result;
        }

        /// <summary>Total edge count per node (hierarchy edges plus cross-links).</summary>
        public static Dictionary<string, int> DegreeMap(MindMapDocument doc)
        {
            var degrees = doc.Nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
            void Bump(string id)
            {
                if (degrees.ContainsKey(id)) degrees[id]++;
            }

            foreach (var n in doc.Nodes)
            {
                if (n.ParentId != null && degrees.ContainsKey(n.ParentId))
                {
                    Bump(n.Id);
                    Bump(n.ParentId);
                }
            }
            foreach (var l in doc.Links)
            {
                Bump(l.SourceNodeId);
                Bump(l.TargetNodeId);
            }
            return degrees;
        }

        /// <summary>Connected components over the undirected graph — the "islands" of a vault.</summary>
        public static List<List<string>> ConnectedComponents(MindMapDocument doc)
        {
            var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var n in doc.Nodes) adjacency[n.Id] = new List<string>();

            void Join(string a, string b)
            {
                if (!adjacency.ContainsKey(a) || !adjacency.ContainsKey(b) || a == b) return;
                adjacency[a].Add(b);
                adjacency[b].Add(a);
            }

            foreach (var n in doc.Nodes)
            {
                if (n.ParentId != null) Join(n.Id, n.ParentId);
            }
            foreach (var l in doc.Links) Join(l.SourceNodeId, l.TargetNodeId);

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var components = new List<List<string>>();
            foreach (var n in doc.Nodes)
            {
                if (!visited.Add(n.Id)) continue;
                var component = new List<string>();
                var stack = new Stack<string>();
                stack.Push(n.Id);
                while (stack.Count > 0)
                {
                    string id = stack.Pop();
                    component.Add(id);
                    foreach (string next in adjacency[id])
                    {
                        if (visited.Add(next)) stack.Push(next);
                    }
                }
                components.Add(component);
            }
            return components;
        }

        /// <summary>The headline numbers the studio shows: size, density, hubs and floaters.</summary>
        public static MindMapInsights Analyze(MindMapDocument doc)
        {
            var insights = new MindMapInsights();
            if (doc == null || doc.Nodes.Count == 0) return insights;

            var degrees = DegreeMap(doc);
            insights.NodeCount = doc.Nodes.Count;
            insights.LinkCount = doc.Links.Count;
            insights.HierarchyEdgeCount = doc.Nodes.Count(n => n.ParentId != null);

            insights.IsolatedNodeIds = degrees.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();

            insights.Hubs = degrees
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(kv => (kv.Key, doc.Nodes.First(n => n.Id == kv.Key).Title, kv.Value))
                .ToList();

            var components = ConnectedComponents(doc);
            insights.ClusterCount = components.Count;
            insights.LargestClusterSize = components.Count == 0 ? 0 : components.Max(c => c.Count);

            int fileNodes = doc.Nodes.Count(n => !string.IsNullOrWhiteSpace(n.FilePath));
            insights.LinkedFileCount = fileNodes;
            insights.TotalWordCount = doc.Nodes.Sum(n => (long)n.WordCount);

            insights.FormatBreakdown = doc.Nodes
                .Select(n => string.IsNullOrWhiteSpace(n.FileExtension)
                    ? n.NodeType.ToString().ToLowerInvariant()
                    : n.FileExtension.TrimStart('.').ToLowerInvariant())
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            insights.TopTags = doc.Nodes
                .SelectMany(n => n.Tags)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(g => (g.Key, g.Count()))
                .ToList();

            return insights;
        }

        /// <summary>Deep-copies a document through its public shape, for undo snapshots.</summary>
        public static MindMapDocument DeepCopy(MindMapDocument doc)
        {
            var copy = new MindMapDocument
            {
                Title = doc.Title,
                Version = doc.Version,
                LastSaved = doc.LastSaved,
                RootNodeId = doc.RootNodeId,
                ViewportOffsetX = doc.ViewportOffsetX,
                ViewportOffsetY = doc.ViewportOffsetY,
                ZoomLevel = doc.ZoomLevel,
                IsTutorial = doc.IsTutorial,
                SourceDirectory = doc.SourceDirectory,
                Theme = new MindMapTheme
                {
                    Name = doc.Theme.Name,
                    BackgroundColorHex = doc.Theme.BackgroundColorHex,
                    CardBackgroundHex = doc.Theme.CardBackgroundHex,
                    CardBorderHex = doc.Theme.CardBorderHex,
                    TextColorHex = doc.Theme.TextColorHex,
                    PrimaryAccentHex = doc.Theme.PrimaryAccentHex,
                    BranchColors = new List<string>(doc.Theme.BranchColors)
                }
            };

            foreach (var n in doc.Nodes)
            {
                copy.Nodes.Add(new MindMapNode
                {
                    Id = n.Id,
                    Title = n.Title,
                    FilePath = n.FilePath,
                    FileExtension = n.FileExtension,
                    NodeType = n.NodeType,
                    X = n.X,
                    Y = n.Y,
                    Width = n.Width,
                    Height = n.Height,
                    ColorHex = n.ColorHex,
                    Icon = n.Icon,
                    Progress = n.Progress,
                    Tags = new List<string>(n.Tags),
                    MarkdownContent = n.MarkdownContent,
                    CreatedDate = n.CreatedDate,
                    ModifiedDate = n.ModifiedDate,
                    IsCollapsed = n.IsCollapsed,
                    ParentId = n.ParentId,
                    ChildIds = new List<string>(n.ChildIds),
                    WordCount = n.WordCount,
                    IsTutorial = n.IsTutorial
                });
            }

            foreach (var l in doc.Links)
            {
                copy.Links.Add(new MindMapLink
                {
                    Id = l.Id,
                    SourceNodeId = l.SourceNodeId,
                    TargetNodeId = l.TargetNodeId,
                    Label = l.Label,
                    ColorHex = l.ColorHex,
                    Style = l.Style,
                    Direction = l.Direction,
                    StrokeThickness = l.StrokeThickness,
                    Kind = l.Kind,
                    Weight = l.Weight
                });
            }

            return copy;
        }
    }

    public sealed class MindMapRepairReport
    {
        public int RepairedNodeIds { get; set; }
        public int ClearedDanglingParents { get; set; }
        public int BrokenCycles { get; set; }
        public int RepairedChildLists { get; set; }
        public int DroppedDanglingLinks { get; set; }
        public int DroppedSelfLinks { get; set; }
        public int MergedDuplicateLinks { get; set; }
        public bool ReassignedRoot { get; set; }

        public bool HasRepairs =>
            RepairedNodeIds > 0 || ClearedDanglingParents > 0 || BrokenCycles > 0 ||
            RepairedChildLists > 0 || DroppedDanglingLinks > 0 || DroppedSelfLinks > 0 ||
            MergedDuplicateLinks > 0 || ReassignedRoot;

        public string Summarize()
        {
            if (!HasRepairs) return "";
            var parts = new List<string>();
            if (DroppedDanglingLinks > 0) parts.Add($"{DroppedDanglingLinks} broken link(s)");
            if (DroppedSelfLinks > 0) parts.Add($"{DroppedSelfLinks} self-link(s)");
            if (MergedDuplicateLinks > 0) parts.Add($"{MergedDuplicateLinks} duplicate link(s)");
            if (ClearedDanglingParents > 0) parts.Add($"{ClearedDanglingParents} orphaned parent ref(s)");
            if (BrokenCycles > 0) parts.Add($"{BrokenCycles} parent cycle(s)");
            if (RepairedChildLists > 0) parts.Add($"{RepairedChildLists} child list(s)");
            if (RepairedNodeIds > 0) parts.Add($"{RepairedNodeIds} duplicate id(s)");
            if (ReassignedRoot) parts.Add("a missing root");
            return "Repaired " + string.Join(", ", parts) + ".";
        }
    }

    public sealed class MindMapInsights
    {
        public int NodeCount { get; set; }
        public int LinkCount { get; set; }
        public int HierarchyEdgeCount { get; set; }
        public int LinkedFileCount { get; set; }
        public long TotalWordCount { get; set; }
        public int ClusterCount { get; set; }
        public int LargestClusterSize { get; set; }
        public List<string> IsolatedNodeIds { get; set; } = new();
        public List<(string Id, string Title, int Degree)> Hubs { get; set; } = new();
        public Dictionary<string, int> FormatBreakdown { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Tag, int Count)> TopTags { get; set; } = new();

        /// <summary>Edges per node — how densely woven the vault is.</summary>
        public double Density => NodeCount == 0 ? 0 : Math.Round((LinkCount + HierarchyEdgeCount) / (double)NodeCount, 2);

        public string HeadlineSummary()
        {
            if (NodeCount == 0) return "Empty galaxy — import a folder or add your first document node.";
            string hub = Hubs.Count > 0 ? $" · busiest: {Hubs[0].Title} ({Hubs[0].Degree} links)" : "";
            string floaters = IsolatedNodeIds.Count > 0 ? $" · {IsolatedNodeIds.Count} unconnected" : "";
            return $"{NodeCount} documents · {LinkCount + HierarchyEdgeCount} connections · " +
                   $"{ClusterCount} cluster{(ClusterCount == 1 ? "" : "s")} · density {Density.ToString("0.##", CultureInfo.InvariantCulture)}{hub}{floaters}";
        }
    }
}
