namespace MarkSmith.Core.Mermaid.Routing;

/// <summary>A node to place: its size, and the group it sits in (null at the top level).</summary>
public sealed record GroupedLayoutNode(string Id, double Width, double Height, string? GroupId);

/// <summary>
/// A box that holds other nodes: a flowchart subgraph, or a composite state. A composite has a
/// <see cref="HeaderNodeId"/>, the state's own box, drawn at the top of the group and the end
/// point of transitions to and from the state. A subgraph has only a <see cref="Title"/>.
/// </summary>
public sealed record GroupedLayoutGroup(string Id, string? ParentGroupId, string? HeaderNodeId, string Title);

/// <summary>A group's frame as laid out: everything inside it, padded, with room for the title.</summary>
public sealed record GroupFrame(string GroupId, string Title, double X, double Y, double Width, double Height, int Depth);

/// <summary>
/// The Diagram Studio's layered layout with groups. Each group is laid out on its own (its
/// members ranked and ordered by <see cref="LayeredLayout"/>), then placed as one block among its
/// neighbours, so a subgraph's or a composite state's members stay together inside their frame
/// instead of scattering across the diagram. Edges into or out of a group rank the whole block.
/// </summary>
public static class GroupedLayout
{
    public const double Padding = 16, TitleBand = 26, HeaderGap = 18;

    public sealed record Options(bool Vertical, bool ReversePrimary, double PrimaryGap, double CrossGap, double Origin, double CrossCenter);

    public sealed record Result(IReadOnlyDictionary<string, (double X, double Y)> Positions, IReadOnlyList<GroupFrame> Frames);

    public static Result Compute(IReadOnlyList<GroupedLayoutNode> nodes, IReadOnlyList<GroupedLayoutGroup> groups,
        IReadOnlyList<(string From, string To)> edges, Options o)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var nodeById = nodes.GroupBy(n => n.Id, cmp).ToDictionary(g => g.Key, g => g.First(), cmp);
        var groupById = groups.GroupBy(g => g.Id, cmp).ToDictionary(g => g.Key, g => g.First(), cmp);
        var headerOf = groups.Where(g => g.HeaderNodeId is not null && nodeById.ContainsKey(g.HeaderNodeId))
                             .ToDictionary(g => g.HeaderNodeId!, g => g.Id, cmp);

        // The group a node or group sits in; a header node is the group itself, so it sits where
        // its group does.
        string? ParentOfNode(string id) => headerOf.TryGetValue(id, out var gid) ? groupById[gid].ParentGroupId : nodeById[id].GroupId;
        string? Valid(string? g) => g is not null && groupById.ContainsKey(g) ? g : null;

        // Chain of groups from the top down to (and including) the node's own group.
        List<string> Path(string nodeId)
        {
            var chain = new List<string>();
            string? g = headerOf.TryGetValue(nodeId, out var own) ? own : Valid(nodeById[nodeId].GroupId);
            var seen = new HashSet<string>(cmp);
            while (g is not null && seen.Add(g)) { chain.Add(g); g = Valid(groupById[g].ParentGroupId); }
            chain.Reverse();
            return chain;
        }
        var paths = nodeById.Keys.ToDictionary(id => id, Path, cmp);

        // Representative of a node at a level (the group block it falls in there, or itself).
        string Rep(string nodeId, string? level)
        {
            var path = paths[nodeId];
            int depth = level is null ? 0 : path.FindIndex(p => cmp.Equals(p, level)) + 1;
            if (level is not null && depth == 0) return "";
            bool isHeader = headerOf.ContainsKey(nodeId);
            // The node's own innermost group, unless it's that group's header (then it *is* the block).
            if (depth < path.Count) return "group:" + path[depth];
            return isHeader ? "" : nodeId;
        }

        var positions = new Dictionary<string, (double X, double Y)>(cmp);
        var frames = new List<GroupFrame>();
        var blockSize = new Dictionary<string, (double W, double H)>(cmp);
        var local = new Dictionary<string, Dictionary<string, (double X, double Y)>>(cmp);

        // Lay a level out (the top level, or a group's inside) in local coordinates from (0,0).
        (double W, double H, Dictionary<string, (double X, double Y)> Pos) Level(string? level)
        {
            var items = new List<string>();
            foreach (var n in nodeById.Values)
                if (!headerOf.ContainsKey(n.Id) && cmp.Equals(Valid(n.GroupId) ?? "", level ?? "")) items.Add(n.Id);
            foreach (var g in groups)
                if (cmp.Equals(Valid(g.ParentGroupId) ?? "", level ?? "") && !cmp.Equals(g.Id, level ?? "")) items.Add("group:" + g.Id);

            (double W, double H) Size(string item)
            {
                if (item.StartsWith("group:", StringComparison.Ordinal))
                {
                    var gid = item[6..];
                    if (!blockSize.ContainsKey(gid)) Block(gid);
                    return blockSize[gid];
                }
                var n = nodeById[item];
                return (n.Width, n.Height);
            }

            var levelEdges = new List<(string, string)>();
            foreach (var (from, to) in edges)
            {
                if (!nodeById.ContainsKey(from) || !nodeById.ContainsKey(to)) continue;
                var a = Rep(from, level);
                var b = Rep(to, level);
                if (a.Length == 0 || b.Length == 0 || cmp.Equals(a, b)) continue;
                levelEdges.Add((a, b));
            }

            var layered = LayeredLayout.Compute(items, levelEdges);
            var layers = new List<List<string>>();
            foreach (var layer in layered.Layers)
            {
                var list = layer.Where(items.Contains).ToList();
                if (list.Count > 0) layers.Add(list);
            }
            if (o.ReversePrimary) layers.Reverse();

            double P(string i) => o.Vertical ? Size(i).H : Size(i).W;
            double C(string i) => o.Vertical ? Size(i).W : Size(i).H;
            var spans = layers.Select(l => l.Sum(C) + (l.Count - 1) * o.CrossGap).ToList();
            double widest = spans.DefaultIfEmpty(0).Max();
            var pos = new Dictionary<string, (double X, double Y)>(cmp);
            double cursor = 0;
            for (int r = 0; r < layers.Count; r++)
            {
                double depth = layers[r].Max(P);
                double cross = (widest - spans[r]) / 2; // centre each rank on the widest
                foreach (var item in layers[r])
                {
                    double primary = cursor + (depth - P(item)) / 2;
                    pos[item] = o.Vertical ? (cross, primary) : (primary, cross);
                    cross += C(item) + o.CrossGap;
                }
                cursor += depth + o.PrimaryGap;
            }
            double primaryTotal = Math.Max(0, cursor - o.PrimaryGap);
            return o.Vertical ? (widest, primaryTotal, pos) : (primaryTotal, widest, pos);
        }

        // A group's block: title band or header box on top, then its inside, padded.
        void Block(string gid)
        {
            blockSize[gid] = (0, 0); // guards a group that (wrongly) contains itself
            var g = groupById[gid];
            var (w, h, pos) = Level(gid);
            double top = TitleBand;
            double headerW = 0;
            if (g.HeaderNodeId is { } hid && nodeById.TryGetValue(hid, out var header))
            {
                top = Padding + header.Height + HeaderGap;
                headerW = header.Width;
            }
            local[gid] = pos;
            blockSize[gid] = (Math.Max(w, headerW) + Padding * 2, top + h + Padding);
        }

        foreach (var g in groups) if (!blockSize.ContainsKey(g.Id)) Block(g.Id);
        var (_, _, rootPos) = Level(null);

        // Top-level positions: start at the origin and centre on the cross axis like the plain layout.
        double minCross = rootPos.Values.Select(p => o.Vertical ? p.X : p.Y).DefaultIfEmpty(0).Min();
        double maxCross = rootPos.Select(kv => (o.Vertical ? kv.Value.X : kv.Value.Y) + (kv.Key.StartsWith("group:", StringComparison.Ordinal)
            ? (o.Vertical ? blockSize[kv.Key[6..]].W : blockSize[kv.Key[6..]].H)
            : (o.Vertical ? nodeById[kv.Key].Width : nodeById[kv.Key].Height))).DefaultIfEmpty(0).Max();
        double crossShift = Math.Max(o.Origin, o.CrossCenter - (maxCross - minCross) / 2) - minCross;

        void Place(Dictionary<string, (double X, double Y)> pos, double ox, double oy, int depth)
        {
            foreach (var (item, p) in pos)
            {
                double x = ox + p.X, y = oy + p.Y;
                if (!item.StartsWith("group:", StringComparison.Ordinal)) { positions[item] = (x, y); continue; }
                var gid = item[6..];
                var g = groupById[gid];
                var (bw, bh) = blockSize[gid];
                frames.Add(new GroupFrame(gid, g.Title, x, y, bw, bh, depth));
                double innerTop = TitleBand;
                if (g.HeaderNodeId is { } hid && nodeById.TryGetValue(hid, out var header))
                {
                    positions[hid] = (x + Padding, y + Padding);
                    innerTop = Padding + header.Height + HeaderGap;
                }
                Place(local[gid], x + Padding, y + innerTop, depth + 1);
            }
        }
        double rootX = o.Vertical ? crossShift : o.Origin, rootY = o.Vertical ? o.Origin : crossShift;
        Place(rootPos, rootX, rootY, 0);
        return new Result(positions, frames);
    }

    /// <summary>
    /// A group's frame around where its members are now (after the user dragged some): the
    /// header box and every member, nested frames included, padded as the layout pads them.
    /// Null when nothing of the group is left.
    /// </summary>
    public static IReadOnlyList<GroupFrame> Fit(IReadOnlyList<(string Id, double X, double Y, double Width, double Height)> nodes,
        IReadOnlyList<GroupedLayoutNode> membership, IReadOnlyList<GroupedLayoutGroup> groups)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var rect = nodes.GroupBy(n => n.Id, cmp).ToDictionary(g => g.Key, g => g.First(), cmp);
        var groupById = groups.GroupBy(g => g.Id, cmp).ToDictionary(g => g.Key, g => g.First(), cmp);
        var result = new Dictionary<string, GroupFrame>(cmp);

        GroupFrame? FitGroup(string gid, int depth, HashSet<string> visiting)
        {
            if (result.TryGetValue(gid, out var done)) return done;
            if (!visiting.Add(gid)) return null;
            var g = groupById[gid];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Take(double x, double y, double w, double h)
            {
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x + w); maxY = Math.Max(maxY, y + h);
            }
            bool hasHeader = g.HeaderNodeId is { } hid && rect.ContainsKey(hid);
            foreach (var m in membership)
                if (cmp.Equals(m.GroupId ?? "", gid) && rect.TryGetValue(m.Id, out var r) && !cmp.Equals(m.Id, g.HeaderNodeId ?? ""))
                    Take(r.X, r.Y, r.Width, r.Height);
            foreach (var child in groups.Where(c => cmp.Equals(c.ParentGroupId ?? "", gid) && !cmp.Equals(c.Id, gid)))
                if (FitGroup(child.Id, depth + 1, visiting) is { } cf) Take(cf.X, cf.Y, cf.Width, cf.Height);
            if (minX == double.MaxValue && !hasHeader) return null;

            double left, top, right, bottom;
            if (hasHeader)
            {
                var h = rect[g.HeaderNodeId!];
                if (minX == double.MaxValue) { minX = h.X; minY = h.Y + h.Height + HeaderGap; maxX = h.X + h.Width; maxY = minY; }
                left = Math.Min(minX, h.X) - Padding;
                top = Math.Min(h.Y, minY) - Padding;
                right = Math.Max(maxX, h.X + h.Width) + Padding;
                bottom = maxY + Padding;
            }
            else
            {
                left = minX - Padding; top = minY - TitleBand; right = maxX + Padding; bottom = maxY + Padding;
            }
            var frame = new GroupFrame(gid, g.Title, left, top, right - left, bottom - top, depth);
            result[gid] = frame;
            return frame;
        }

        foreach (var g in groups)
        {
            int depth = 0;
            var p = g.ParentGroupId;
            var seen = new HashSet<string>(cmp);
            while (p is not null && groupById.ContainsKey(p) && seen.Add(p)) { depth++; p = groupById[p].ParentGroupId; }
            FitGroup(g.Id, depth, new HashSet<string>(cmp));
        }
        // Outermost first, so inner frames draw on top.
        return result.Values.OrderBy(f => f.Depth).ToList();
    }
}
