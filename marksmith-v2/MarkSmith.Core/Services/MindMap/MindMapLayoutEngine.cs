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
        VerticalHierarchy
    }

    public sealed class MindMapLayoutEngine
    {
        public void ApplyLayout(MindMapDocument doc, MindMapLayoutType layoutType)
        {
            if (doc.Nodes.Count == 0) return;

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
            }
        }

        public void ApplyHorizontalTreeLayout(MindMapDocument doc)
        {
            var root = doc.Nodes.FirstOrDefault(n => n.Id == doc.RootNodeId) ?? doc.Nodes[0];
            root.X = 0;
            root.Y = 0;

            var visited = new HashSet<string> { root.Id };
            LayoutHorizontalSubtree(doc, root, 0, 0, 1, visited);
        }

        private double LayoutHorizontalSubtree(MindMapDocument doc, MindMapNode node, double startX, double startY, int depth, HashSet<string> visited)
        {
            var children = node.ChildIds
                .Select(id => doc.Nodes.FirstOrDefault(n => n.Id == id))
                .Where(n => n != null && !visited.Contains(n.Id))
                .Cast<MindMapNode>()
                .ToList();

            if (children.Count == 0)
            {
                return node.Height + 24;
            }

            double horizontalGap = 240;
            double nextX = node.X + node.Width + horizontalGap;

            double totalHeight = 0;
            var heights = new List<double>();
            foreach (var child in children)
            {
                visited.Add(child.Id);
                double h = LayoutHorizontalSubtree(doc, child, nextX, 0, depth + 1, visited);
                heights.Add(h);
                totalHeight += h;
            }

            double currentY = node.Y - (totalHeight / 2.0);
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                double h = heights[i];
                child.X = nextX;
                child.Y = currentY + (h / 2.0) - (child.Height / 2.0);
                currentY += h;
            }

            return Math.Max(node.Height + 24, totalHeight);
        }

        public void ApplyRadialGalaxyLayout(MindMapDocument doc)
        {
            var root = doc.Nodes.FirstOrDefault(n => n.Id == doc.RootNodeId) ?? doc.Nodes[0];
            root.X = 0;
            root.Y = 0;

            var nonRoot = doc.Nodes.Where(n => n.Id != root.Id).ToList();
            if (nonRoot.Count == 0) return;

            // Tier nodes into orbital rings by parent depth
            var directChildren = nonRoot.Where(n => n.ParentId == root.Id).ToList();
            var secondRing = nonRoot.Where(n => n.ParentId != root.Id).ToList();

            if (directChildren.Count > 0)
            {
                double radius1 = 280;
                double angleStep1 = 2 * Math.PI / directChildren.Count;
                for (int i = 0; i < directChildren.Count; i++)
                {
                    double angle = i * angleStep1;
                    directChildren[i].X = radius1 * Math.Cos(angle) - (directChildren[i].Width / 2.0);
                    directChildren[i].Y = radius1 * Math.Sin(angle) - (directChildren[i].Height / 2.0);
                }
            }

            if (secondRing.Count > 0)
            {
                double radius2 = 540;
                double angleStep2 = 2 * Math.PI / secondRing.Count;
                for (int i = 0; i < secondRing.Count; i++)
                {
                    double angle = (i * angleStep2) + (Math.PI / secondRing.Count);
                    secondRing[i].X = radius2 * Math.Cos(angle) - (secondRing[i].Width / 2.0);
                    secondRing[i].Y = radius2 * Math.Sin(angle) - (secondRing[i].Height / 2.0);
                }
            }
        }

        public void ApplyForceDirectedLayout(MindMapDocument doc, int iterations = 100)
        {
            var nodes = doc.Nodes;
            if (nodes.Count < 2) return;

            var nodeMap = nodes.ToDictionary(n => n.Id);
            var edges = new List<(MindMapNode S, MindMapNode T)>();
            foreach (var n in nodes)
            {
                if (!string.IsNullOrEmpty(n.ParentId) && nodeMap.TryGetValue(n.ParentId, out var p))
                {
                    edges.Add((p, n));
                }
            }
            foreach (var l in doc.Links)
            {
                if (nodeMap.TryGetValue(l.SourceNodeId, out var s) && nodeMap.TryGetValue(l.TargetNodeId, out var t))
                {
                    edges.Add((s, t));
                }
            }

            double k = Math.Sqrt((1200.0 * 800.0) / nodes.Count);
            var dispX = new Dictionary<string, double>(nodes.Count);
            var dispY = new Dictionary<string, double>(nodes.Count);

            for (int iter = 0; iter < iterations; iter++)
            {
                foreach (var v in nodes)
                {
                    dispX[v.Id] = 0;
                    dispY[v.Id] = 0;

                    // Repulsion between all pairs
                    foreach (var u in nodes)
                    {
                        if (u.Id == v.Id) continue;
                        double dx = v.X - u.X;
                        double dy = v.Y - u.Y;
                        double dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.1);
                        double force = (k * k) / dist;
                        dispX[v.Id] += (dx / dist) * force;
                        dispY[v.Id] += (dy / dist) * force;
                    }
                }

                // Attraction along links and parent-child edges
                foreach (var (u, v) in edges)
                {
                    double dx = v.X - u.X;
                    double dy = v.Y - u.Y;
                    double dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.1);
                    double force = (dist * dist) / k;

                    dispX[v.Id] -= (dx / dist) * force;
                    dispY[v.Id] -= (dy / dist) * force;
                    dispX[u.Id] += (dx / dist) * force;
                    dispY[u.Id] += (dy / dist) * force;
                }

                // Apply displacements with cooling
                double temp = (iterations - iter) / (double)iterations * 15.0;
                foreach (var v in nodes)
                {
                    if (v.Id == doc.RootNodeId) continue; // Keep root anchored
                    double dLen = Math.Max(Math.Sqrt(dispX[v.Id] * dispX[v.Id] + dispY[v.Id] * dispY[v.Id]), 0.1);
                    v.X += (dispX[v.Id] / dLen) * Math.Min(dLen, temp);
                    v.Y += (dispY[v.Id] / dLen) * Math.Min(dLen, temp);
                }
            }
        }

        public void ApplyVerticalHierarchyLayout(MindMapDocument doc)
        {
            var root = doc.Nodes.FirstOrDefault(n => n.Id == doc.RootNodeId) ?? doc.Nodes[0];
            root.X = 0;
            root.Y = 0;

            var visited = new HashSet<string> { root.Id };
            LayoutVerticalSubtree(doc, root, 0, 0, 1, visited);
        }

        private double LayoutVerticalSubtree(MindMapDocument doc, MindMapNode node, double startX, double startY, int depth, HashSet<string> visited)
        {
            var children = node.ChildIds
                .Select(id => doc.Nodes.FirstOrDefault(n => n.Id == id))
                .Where(n => n != null && !visited.Contains(n.Id))
                .Cast<MindMapNode>()
                .ToList();

            if (children.Count == 0)
            {
                return node.Width + 24;
            }

            double verticalGap = 120;
            double nextY = node.Y + node.Height + verticalGap;

            double totalWidth = 0;
            var widths = new List<double>();
            foreach (var child in children)
            {
                visited.Add(child.Id);
                double w = LayoutVerticalSubtree(doc, child, 0, nextY, depth + 1, visited);
                widths.Add(w);
                totalWidth += w;
            }

            double currentX = node.X - (totalWidth / 2.0);
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                double w = widths[i];
                child.X = currentX + (w / 2.0) - (child.Width / 2.0);
                child.Y = nextY;
                currentX += w;
            }

            return Math.Max(node.Width + 24, totalWidth);
        }
    }
}
