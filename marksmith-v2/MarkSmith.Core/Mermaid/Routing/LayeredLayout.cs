namespace MarkSmith.Core.Mermaid.Routing;

/// <summary>
/// The rank (layer) and in-layer order of every node for the Diagram Studio's layered layout
/// (flowchart, class, state, ER), in the Sugiyama style:
/// <list type="number">
/// <item>Break cycles: a depth-first walk from the sources marks each edge that closes a loop as a
/// back edge and leaves it out of ranking. (Ranking used to relax around cycles until a cap of
/// "node count + 5", so any loop — every state machine has one — stretched the diagram into a
/// tangle of far-apart layers.)</item>
/// <item>Longest-path ranks over the remaining edges.</item>
/// <item>Order each layer by the average position of its neighbours, sweeping down and up a few
/// times, so edges cross as little as possible.</item>
/// </list>
/// Pure and deterministic: the same diagram always lays out the same way.
/// </summary>
public static class LayeredLayout
{
    public sealed record Result(IReadOnlyDictionary<string, int> Ranks, IReadOnlyList<IReadOnlyList<string>> Layers, IReadOnlySet<(string From, string To)> BackEdges);

    public static Result Compute(IReadOnlyList<string> nodeIds, IReadOnlyList<(string From, string To)> edges)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var ids = nodeIds.Distinct(cmp).ToList();
        var index = new Dictionary<string, int>(cmp);
        for (int i = 0; i < ids.Count; i++) index[ids[i]] = i;

        var valid = edges.Where(e => index.ContainsKey(e.From) && index.ContainsKey(e.To)).ToList();
        var outgoing = ids.ToDictionary(id => id, _ => new List<string>(), cmp);
        var incoming = ids.ToDictionary(id => id, _ => new List<string>(), cmp);
        foreach (var (from, to) in valid)
        {
            outgoing[from].Add(to);
            incoming[to].Add(from);
        }

        // 1. Back edges by DFS, sources first (so a start point heads the diagram), then anything
        // left unvisited (a pure cycle) in declaration order. Self-loops are always back edges.
        var backEdges = new HashSet<(string, string)>(new EdgeComparer());
        var state = new Dictionary<string, int>(cmp); // 0 new, 1 on stack, 2 done
        var dfsOrder = new List<string>();
        void Visit(string root)
        {
            var stack = new Stack<(string Node, int Next)>();
            stack.Push((root, 0));
            state[root] = 1;
            dfsOrder.Add(root);
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                var outs = outgoing[node];
                if (next < outs.Count)
                {
                    stack.Push((node, next + 1));
                    var to = outs[next];
                    int st = state.GetValueOrDefault(to);
                    if (st == 1) backEdges.Add((node, to));
                    else if (st == 0)
                    {
                        state[to] = 1;
                        dfsOrder.Add(to);
                        stack.Push((to, 0));
                    }
                }
                else state[node] = 2;
            }
        }
        foreach (var id in ids.Where(id => incoming[id].Count == 0)) if (!state.ContainsKey(id)) Visit(id);
        foreach (var id in ids) if (!state.ContainsKey(id)) Visit(id);

        // 2. Longest-path ranks over forward edges (Kahn's order on the now-acyclic graph).
        var forward = valid.Where(e => !backEdges.Contains(e)).ToList();
        var indeg = ids.ToDictionary(id => id, _ => 0, cmp);
        foreach (var (_, to) in forward) indeg[to]++;
        var ranks = ids.ToDictionary(id => id, _ => 0, cmp);
        var fwdOut = ids.ToDictionary(id => id, _ => new List<string>(), cmp);
        foreach (var (from, to) in forward) fwdOut[from].Add(to);
        var queue = new Queue<string>(ids.Where(id => indeg[id] == 0));
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            foreach (var v in fwdOut[u])
            {
                ranks[v] = Math.Max(ranks[v], ranks[u] + 1);
                if (--indeg[v] == 0) queue.Enqueue(v);
            }
        }

        // 3. Layers in DFS order, then barycentre sweeps.
        int maxRank = ranks.Count == 0 ? 0 : ranks.Values.Max();
        var layers = Enumerable.Range(0, maxRank + 1).Select(_ => new List<string>()).ToList();
        foreach (var id in dfsOrder) layers[ranks[id]].Add(id);

        var pos = new Dictionary<string, double>(cmp);
        void Index() { foreach (var layer in layers) for (int i = 0; i < layer.Count; i++) pos[layer[i]] = i; }
        Index();
        for (int sweep = 0; sweep < 4; sweep++)
        {
            bool down = sweep % 2 == 0;
            var order = down ? Enumerable.Range(1, layers.Count - 1) : Enumerable.Range(0, Math.Max(0, layers.Count - 1)).Reverse();
            foreach (int r in order)
            {
                var layer = layers[r];
                var keyed = layer.Select((id, i) =>
                {
                    var neighbours = (down ? incoming[id] : outgoing[id]).Where(n => ranks[n] == r + (down ? -1 : 1)).ToList();
                    double key = neighbours.Count == 0 ? pos[id] : neighbours.Average(n => pos[n]);
                    return (id, key, i);
                }).OrderBy(t => t.key).ThenBy(t => t.i).Select(t => t.id).ToList();
                layers[r] = keyed;
                for (int i = 0; i < keyed.Count; i++) pos[keyed[i]] = i;
            }
        }

        return new Result(ranks, layers, backEdges);
    }

    private sealed class EdgeComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) a, (string, string) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) e) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(e.Item1), StringComparer.OrdinalIgnoreCase.GetHashCode(e.Item2));
    }
}
