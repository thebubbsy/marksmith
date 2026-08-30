using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Models.MindMap;

namespace MarkSmith.Services.MindMap
{
    public enum MindMapLayoutType
    {
        HorizontalTree,
        RadialGalaxy,
        ForceDirected,
        VerticalHierarchy,
        /// <summary>Each connected component laid out on its own, then packed into a grid so
        /// separate islands of a vault never land on top of each other.</summary>
        ConstellationClusters
    }

    public sealed class MindMapLayoutEngine
    {
        private const double HorizontalGap = 200;
        private const double VerticalGap = 110;
        private const double SiblingGap = 26;

        public void ApplyLayout(MindMapDocument doc, MindMapLayoutType layoutType)
        {
            if (doc == null || doc.Nodes.Count == 0) return;

            switch (layoutType)
            {
                case MindMapLayoutType.HorizontalTree:
                    ApplyHorizontalTreeLayout(doc);
                    break;
                case MindMapLayoutType.RadialGalaxy:
                    ApplyRadialGalaxyLayout(doc);
                    break;
                case MindMapLayoutType.ForceDirected:
                    ApplyForceDirectedLayout(doc);
                    break;
                case MindMapLayoutType.VerticalHierarchy:
                    ApplyVerticalHierarchyLayout(doc);
                    break;
                case MindMapLayoutType.ConstellationClusters:
                    ApplyConstellationClusterLayout(doc);
                    break;
            }
        }

        // ---- Tree layouts ----

        public void ApplyHorizontalTreeLayout(MindMapDocument doc) => ApplyTreeLayout(doc, vertical: false);

        public void ApplyVerticalHierarchyLayout(MindMapDocument doc) => ApplyTreeLayout(doc, vertical: true);

        /// <summary>
        /// Classic tidy-tree placement. A node is positioned BEFORE its subtree is walked, because
        /// each level's offset is measured from its parent's final coordinate — laying the parent
        /// out afterwards (as this once did) meant every node below depth 2 was placed relative to
        /// wherever its parent happened to be sitting from the previous layout.
        /// </summary>
        private void ApplyTreeLayout(MindMapDocument doc, bool vertical)
        {
            var byId = Index(doc);
            var roots = RootsOf(doc, byId);
            var visited = new HashSet<string>(StringComparer.Ordinal);

            double cursor = 0;
            foreach (var root in roots)
            {
                if (!visited.Add(root.Id)) continue;

                if (vertical)
                {
                    root.X = cursor;
                    root.Y = 0;
                    double span = MeasureSubtree(byId, root, vertical: true, new HashSet<string>(visited, StringComparer.Ordinal));
                    PlaceSubtree(byId, root, vertical: true, visited);
                    cursor += span + HorizontalGap;
                }
                else
                {
                    root.X = 0;
                    root.Y = cursor;
                    double span = MeasureSubtree(byId, root, vertical: false, new HashSet<string>(visited, StringComparer.Ordinal));
                    PlaceSubtree(byId, root, vertical: false, visited);
                    cursor += span + VerticalGap;
                }
            }

            CenterOnOrigin(doc);
        }

        /// <summary>Total cross-axis extent a subtree needs, so siblings can be stacked without overlap.</summary>
        private static double MeasureSubtree(Dictionary<string, MindMapNode> byId, MindMapNode node, bool vertical, HashSet<string> seen)
        {
            double own = vertical ? node.Width : node.Height;
            var children = ChildrenOf(byId, node, seen);
            if (children.Count == 0) return own + SiblingGap;

            double total = 0;
            foreach (var child in children)
            {
                // Mark as we descend: a node reachable twice (a hand-edited map can list one child
                // under two parents) is measured once, and a cycle terminates instead of recursing
                // until the stack dies.
                if (!seen.Add(child.Id)) continue;
                total += MeasureSubtree(byId, child, vertical, seen);
            }
            return Math.Max(own + SiblingGap, total);
        }

        private static void PlaceSubtree(Dictionary<string, MindMapNode> byId, MindMapNode node, bool vertical, HashSet<string> visited)
        {
            var children = ChildrenOf(byId, node, visited);
            if (children.Count == 0) return;

            // Measure with a private "seen" set so sizing does not consume the shared visited set
            // that placement still needs.
            var spans = children
                .Select(c => MeasureSubtree(byId, c, vertical, new HashSet<string>(visited, StringComparer.Ordinal) { c.Id }))
                .ToList();
            double totalSpan = spans.Sum();

            if (vertical)
            {
                double nextY = node.Y + node.Height + VerticalGap;
                double cursor = node.X + (node.Width / 2.0) - (totalSpan / 2.0);
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    child.X = cursor + (spans[i] / 2.0) - (child.Width / 2.0);
                    child.Y = nextY;
                    cursor += spans[i];
                    visited.Add(child.Id);
                }
            }
            else
            {
                double nextX = node.X + node.Width + HorizontalGap;
                double cursor = node.Y + (node.Height / 2.0) - (totalSpan / 2.0);
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    child.X = nextX;
                    child.Y = cursor + (spans[i] / 2.0) - (child.Height / 2.0);
                    cursor += spans[i];
                    visited.Add(child.Id);
                }
            }

            foreach (var child in children)
            {
                PlaceSubtree(byId, child, vertical, visited);
            }
        }

        // ---- Radial ----

        /// <summary>
        /// Rings by real tree depth, with each subtree confined to the angular wedge its parent
        /// occupies — so a branch stays visually together instead of being scattered around the
        /// circle, and the ring radius grows with how crowded that ring actually is.
        /// </summary>
        public void ApplyRadialGalaxyLayout(MindMapDocument doc)
        {
            var byId = Index(doc);
            var roots = RootsOf(doc, byId);
            var root = roots.FirstOrDefault();
            if (root == null) return;

            root.X = -root.Width / 2.0;
            root.Y = -root.Height / 2.0;

            var visited = new HashSet<string>(StringComparer.Ordinal) { root.Id };
            PlaceRadial(byId, root, 0, 2 * Math.PI, 1, visited);

            // Anything not under the primary root (other roots, islands) goes on an outer ring so
            // it stays visible rather than piling up at the origin.
            var leftovers = doc.Nodes.Where(n => !visited.Contains(n.Id)).ToList();
            if (leftovers.Count > 0)
            {
                double radius = 340 + (MaxDepth(byId, root) * 260);
                double step = 2 * Math.PI / leftovers.Count;
                for (int i = 0; i < leftovers.Count; i++)
                {
                    double angle = i * step;
                    leftovers[i].X = radius * Math.Cos(angle) - (leftovers[i].Width / 2.0);
                    leftovers[i].Y = radius * Math.Sin(angle) - (leftovers[i].Height / 2.0);
                    visited.Add(leftovers[i].Id);
                }
            }
        }

        private static void PlaceRadial(Dictionary<string, MindMapNode> byId, MindMapNode node, double angleStart, double angleEnd, int depth, HashSet<string> visited)
        {
            var children = ChildrenOf(byId, node, visited);
            if (children.Count == 0) return;

            // Radius scales with the widest possible ring occupancy so crowded levels spread out
            // instead of overlapping.
            double radius = 300 * depth + (children.Count > 8 ? (children.Count - 8) * 14 : 0);
            double sweep = (angleEnd - angleStart) / children.Count;

            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                double childStart = angleStart + (i * sweep);
                double angle = childStart + (sweep / 2.0);

                child.X = radius * Math.Cos(angle) - (child.Width / 2.0);
                child.Y = radius * Math.Sin(angle) - (child.Height / 2.0);
                visited.Add(child.Id);
            }

            for (int i = 0; i < children.Count; i++)
            {
                double childStart = angleStart + (i * sweep);
                PlaceRadial(byId, children[i], childStart, childStart + sweep, depth + 1, visited);
            }
        }

        private static int MaxDepth(Dictionary<string, MindMapNode> byId, MindMapNode root)
        {
            int max = 0;
            var stack = new Stack<(MindMapNode Node, int Depth)>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { root.Id };
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                max = Math.Max(max, depth);
                foreach (var child in ChildrenOf(byId, node, seen))
                {
                    seen.Add(child.Id);
                    stack.Push((child, depth + 1));
                }
            }
            return max;
        }

        // ---- Force directed ----

        public void ApplyForceDirectedLayout(MindMapDocument doc, int iterations = 240)
        {
            ForceDirect(doc, doc.Nodes.ToList(), iterations, anchorRootId: doc.RootNodeId);
            CenterOnOrigin(doc);
        }

        /// <summary>
        /// Fruchterman-Reingold with the three fixes this needed to be usable: coincident nodes are
        /// jittered apart first (an imported vault arrives with every node at 0,0, where the
        /// repulsion vector is exactly zero and the simulation does nothing at all), the jitter
        /// comes from a fixed seed so the same map always lays out the same way, and repulsion
        /// accounts for node size so big cards do not end up on top of small ones.
        /// </summary>
        private static void ForceDirect(MindMapDocument doc, List<MindMapNode> nodes, int iterations, string? anchorRootId)
        {
            if (nodes.Count < 2) return;

            var index = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var rng = new Random(17);

            SeedPositions(nodes, rng);

            var edges = new List<(MindMapNode S, MindMapNode T)>();
            var seenEdges = new HashSet<(string, string)>();
            foreach (var n in nodes)
            {
                if (n.ParentId != null && index.TryGetValue(n.ParentId, out var p) && seenEdges.Add(MindMapGraph.PairKey(n.Id, p.Id)))
                {
                    edges.Add((p, n));
                }
            }
            foreach (var l in doc.Links)
            {
                if (index.TryGetValue(l.SourceNodeId, out var s) && index.TryGetValue(l.TargetNodeId, out var t) &&
                    seenEdges.Add(MindMapGraph.PairKey(s.Id, t.Id)))
                {
                    edges.Add((s, t));
                }
            }

            double area = 1600.0 * 1100.0 * Math.Max(1, nodes.Count / 24.0);
            double k = Math.Sqrt(area / nodes.Count);

            var dispX = new Dictionary<string, double>(nodes.Count);
            var dispY = new Dictionary<string, double>(nodes.Count);

            for (int iter = 0; iter < iterations; iter++)
            {
                foreach (var v in nodes)
                {
                    dispX[v.Id] = 0;
                    dispY[v.Id] = 0;
                }

                for (int i = 0; i < nodes.Count; i++)
                {
                    var v = nodes[i];
                    for (int j = i + 1; j < nodes.Count; j++)
                    {
                        var u = nodes[j];
                        double dx = CenterX(v) - CenterX(u);
                        double dy = CenterY(v) - CenterY(u);
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist < 0.01)
                        {
                            dx = (rng.NextDouble() - 0.5) * 2;
                            dy = (rng.NextDouble() - 0.5) * 2;
                            dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.01);
                        }

                        // Subtract the two half-diagonals so the force is measured edge-to-edge:
                        // two wide cards need more room between their centres than two small ones.
                        double padding = (Radius(v) + Radius(u)) * 0.9;
                        double effective = Math.Max(dist - padding, 1.0);
                        double force = (k * k) / effective;

                        double fx = (dx / dist) * force;
                        double fy = (dy / dist) * force;
                        dispX[v.Id] += fx;
                        dispY[v.Id] += fy;
                        dispX[u.Id] -= fx;
                        dispY[u.Id] -= fy;
                    }
                }

                foreach (var (u, v) in edges)
                {
                    double dx = CenterX(v) - CenterX(u);
                    double dy = CenterY(v) - CenterY(u);
                    double dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.01);
                    double force = (dist * dist) / k;

                    dispX[v.Id] -= (dx / dist) * force;
                    dispY[v.Id] -= (dy / dist) * force;
                    dispX[u.Id] += (dx / dist) * force;
                    dispY[u.Id] += (dy / dist) * force;
                }

                // Gravity. Without it the only force acting on a weakly-connected node is
                // repulsion, so it accelerates away from the cloud and never comes back — a map
                // with a loose branch would spread until the canvas was useless.
                double gravity = 0.08;
                foreach (var v in nodes)
                {
                    dispX[v.Id] -= CenterX(v) * gravity;
                    dispY[v.Id] -= CenterY(v) * gravity;
                }

                double temp = (iterations - iter) / (double)iterations * (k / 6.0);
                foreach (var v in nodes)
                {
                    if (v.Id == anchorRootId) continue;
                    double dLen = Math.Sqrt(dispX[v.Id] * dispX[v.Id] + dispY[v.Id] * dispY[v.Id]);
                    if (dLen < 0.0001) continue;
                    v.X += (dispX[v.Id] / dLen) * Math.Min(dLen, temp);
                    v.Y += (dispY[v.Id] / dLen) * Math.Min(dLen, temp);
                }
            }

            PlaceIsolatedNodes(nodes, edges);
            RelaxOverlaps(nodes);
        }

        /// <summary>
        /// Rings the nodes that no edge touches around everything else. A simulation cannot place
        /// them meaningfully — with nothing pulling on them their position is decided entirely by
        /// repulsion, so wherever they started they simply drift further out. Putting them in a
        /// deliberate ring keeps them on screen and reads as what they are: documents not yet
        /// connected to anything.
        /// </summary>
        private static void PlaceIsolatedNodes(List<MindMapNode> nodes, List<(MindMapNode S, MindMapNode T)> edges)
        {
            var connected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (s, t) in edges)
            {
                connected.Add(s.Id);
                connected.Add(t.Id);
            }

            var isolated = nodes.Where(n => !connected.Contains(n.Id)).ToList();
            if (isolated.Count == 0) return;

            var anchored = nodes.Where(n => connected.Contains(n.Id)).ToList();
            double centerX = 0, centerY = 0, radius = 260;
            if (anchored.Count > 0)
            {
                double minX = anchored.Min(CenterX), maxX = anchored.Max(CenterX);
                double minY = anchored.Min(CenterY), maxY = anchored.Max(CenterY);
                centerX = (minX + maxX) / 2.0;
                centerY = (minY + maxY) / 2.0;
                radius = Math.Max(260, Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2)) / 2.0 + 220);
            }

            double step = 2 * Math.PI / isolated.Count;
            for (int i = 0; i < isolated.Count; i++)
            {
                double angle = i * step;
                isolated[i].X = centerX + radius * Math.Cos(angle) - (isolated[i].Width / 2.0);
                isolated[i].Y = centerY + radius * Math.Sin(angle) - (isolated[i].Height / 2.0);
            }
        }

        private static void SeedPositions(List<MindMapNode> nodes, Random rng)
        {
            // A freshly imported map has every node at the origin; a phyllotaxis spiral gives an
            // even, deterministic starting cloud for the simulation to pull apart.
            bool degenerate = nodes.Select(n => (Math.Round(n.X, 1), Math.Round(n.Y, 1))).Distinct().Count() <= Math.Max(1, nodes.Count / 4);
            if (!degenerate) return;

            const double golden = 2.39996322972865332;
            double spacing = 60 + nodes.Count * 0.6;
            for (int i = 0; i < nodes.Count; i++)
            {
                double angle = i * golden;
                double radius = spacing * Math.Sqrt(i + 1);
                nodes[i].X = radius * Math.Cos(angle) + (rng.NextDouble() - 0.5);
                nodes[i].Y = radius * Math.Sin(angle) + (rng.NextDouble() - 0.5);
            }
        }

        /// <summary>Final pass that pushes any still-overlapping pair apart, so no card is hidden
        /// behind another whatever the simulation converged to.</summary>
        private static void RelaxOverlaps(List<MindMapNode> nodes, int passes = 24)
        {
            const double gap = 18;
            for (int pass = 0; pass < passes; pass++)
            {
                bool moved = false;
                for (int i = 0; i < nodes.Count; i++)
                {
                    for (int j = i + 1; j < nodes.Count; j++)
                    {
                        var a = nodes[i];
                        var b = nodes[j];
                        double overlapX = ((a.Width + b.Width) / 2.0 + gap) - Math.Abs(CenterX(a) - CenterX(b));
                        double overlapY = ((a.Height + b.Height) / 2.0 + gap) - Math.Abs(CenterY(a) - CenterY(b));
                        if (overlapX <= 0 || overlapY <= 0) continue;

                        moved = true;
                        // Separate along the cheaper axis only — pushing both ways turns a tidy
                        // layout into mush.
                        if (overlapX < overlapY)
                        {
                            double shift = (overlapX / 2.0) + 0.5;
                            if (CenterX(a) <= CenterX(b)) { a.X -= shift; b.X += shift; }
                            else { a.X += shift; b.X -= shift; }
                        }
                        else
                        {
                            double shift = (overlapY / 2.0) + 0.5;
                            if (CenterY(a) <= CenterY(b)) { a.Y -= shift; b.Y += shift; }
                            else { a.Y += shift; b.Y -= shift; }
                        }
                    }
                }
                if (!moved) return;
            }
        }

        // ---- Cluster packing ----

        /// <summary>
        /// Lays out each connected component independently, then packs the components into a grid.
        /// This is the layout that makes an imported vault legible: unrelated islands stop fighting
        /// for the same space, and you can see at a glance how many separate bodies of work exist.
        /// </summary>
        public void ApplyConstellationClusterLayout(MindMapDocument doc)
        {
            var components = MindMapGraph.ConnectedComponents(doc);
            if (components.Count == 0) return;

            var byId = Index(doc);
            var placed = new List<(List<MindMapNode> Nodes, double W, double H)>();

            foreach (var component in components.OrderByDescending(c => c.Count))
            {
                var nodes = component.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                if (nodes.Count == 0) continue;

                if (nodes.Count == 1)
                {
                    nodes[0].X = 0;
                    nodes[0].Y = 0;
                }
                else
                {
                    ForceDirect(doc, nodes, iterations: 200, anchorRootId: null);
                }

                NormalizeToTopLeft(nodes);
                double w = nodes.Max(n => n.X + n.Width) - nodes.Min(n => n.X);
                double h = nodes.Max(n => n.Y + n.Height) - nodes.Min(n => n.Y);
                placed.Add((nodes, Math.Max(w, 1), Math.Max(h, 1)));
            }

            // Shelf packing: fill a row until it is wider than the target, then start a new one.
            double targetRowWidth = Math.Max(1400, Math.Sqrt(placed.Sum(p => p.W * p.H)) * 1.4);
            const double clusterGap = 180;

            double cursorX = 0, cursorY = 0, rowHeight = 0;
            foreach (var (nodes, w, h) in placed)
            {
                if (cursorX > 0 && cursorX + w > targetRowWidth)
                {
                    cursorX = 0;
                    cursorY += rowHeight + clusterGap;
                    rowHeight = 0;
                }

                foreach (var n in nodes)
                {
                    n.X += cursorX;
                    n.Y += cursorY;
                }

                cursorX += w + clusterGap;
                rowHeight = Math.Max(rowHeight, h);
            }

            CenterOnOrigin(doc);
        }

        // ---- Shared helpers ----

        private static Dictionary<string, MindMapNode> Index(MindMapDocument doc)
        {
            var byId = new Dictionary<string, MindMapNode>(doc.Nodes.Count, StringComparer.Ordinal);
            foreach (var n in doc.Nodes) byId[n.Id] = n;
            return byId;
        }

        /// <summary>The declared root first, then every other parentless node. Orphans created by
        /// deleting a parent are roots too, so no node is ever left un-laid-out at its stale
        /// coordinates.</summary>
        private static List<MindMapNode> RootsOf(MindMapDocument doc, Dictionary<string, MindMapNode> byId)
        {
            var roots = new List<MindMapNode>();
            if (!string.IsNullOrEmpty(doc.RootNodeId) && byId.TryGetValue(doc.RootNodeId, out var declared))
            {
                roots.Add(declared);
            }
            foreach (var n in doc.Nodes)
            {
                if (roots.Count > 0 && n.Id == roots[0].Id) continue;
                if (n.ParentId == null || !byId.ContainsKey(n.ParentId)) roots.Add(n);
            }
            return roots;
        }

        private static List<MindMapNode> ChildrenOf(Dictionary<string, MindMapNode> byId, MindMapNode node, HashSet<string> visited)
        {
            var children = new List<MindMapNode>(node.ChildIds.Count);
            foreach (string id in node.ChildIds)
            {
                if (visited.Contains(id)) continue;
                if (byId.TryGetValue(id, out var child)) children.Add(child);
            }
            return children;
        }

        private static double CenterX(MindMapNode n) => n.X + (n.Width / 2.0);
        private static double CenterY(MindMapNode n) => n.Y + (n.Height / 2.0);
        private static double Radius(MindMapNode n) => Math.Sqrt(n.Width * n.Width + n.Height * n.Height) / 2.0;

        private static void NormalizeToTopLeft(List<MindMapNode> nodes)
        {
            if (nodes.Count == 0) return;
            double minX = nodes.Min(n => n.X);
            double minY = nodes.Min(n => n.Y);
            foreach (var n in nodes)
            {
                n.X -= minX;
                n.Y -= minY;
            }
        }

        /// <summary>Recentres the whole map on the world origin, which is where the canvas camera
        /// starts — otherwise "auto-layout" can leave the result off-screen.</summary>
        private static void CenterOnOrigin(MindMapDocument doc)
        {
            if (doc.Nodes.Count == 0) return;
            double minX = doc.Nodes.Min(n => n.X);
            double maxX = doc.Nodes.Max(n => n.X + n.Width);
            double minY = doc.Nodes.Min(n => n.Y);
            double maxY = doc.Nodes.Max(n => n.Y + n.Height);

            double dx = -(minX + maxX) / 2.0;
            double dy = -(minY + maxY) / 2.0;
            foreach (var n in doc.Nodes)
            {
                n.X += dx;
                n.Y += dy;
            }
        }
    }
}
