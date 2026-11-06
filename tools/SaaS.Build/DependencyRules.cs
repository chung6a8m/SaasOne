namespace SaaS.Build;

public sealed record DependencyNode(string Id, string Layer, IReadOnlyList<string> Dependencies);

public static class DependencyRules
{
    public static IReadOnlyList<string> Validate(IEnumerable<DependencyNode> nodes)
    {
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var graph = new Dictionary<string, DependencyNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
            if (string.IsNullOrWhiteSpace(node.Id) || !graph.TryAdd(node.Id, node))
                errors.Add($"Missing or duplicate dependency identity: {node.Id}");
        if (graph.Count == 0) errors.Add("Dependency graph is empty.");
        foreach (var node in graph.Values)
            foreach (var dependency in node.Dependencies)
                if (!graph.ContainsKey(dependency)) errors.Add($"Unresolved dependency: {node.Id} -> {dependency}");
        if (errors.Count != 0) return errors.Order().ToArray();

        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Visit(string id)
        {
            if (states.TryGetValue(id, out var state))
            {
                if (state == 1) errors.Add($"Dependency cycle at {id}.");
                return;
            }
            states[id] = 1;
            foreach (var dependency in graph[id].Dependencies) Visit(dependency);
            states[id] = 2;
        }
        foreach (var id in graph.Keys) Visit(id);

        foreach (var root in graph.Values)
        {
            var shared = root.Layer is "Platform" or "Theme";
            var orders = IsFamily(root.Id, "SaaS.Orders");
            if (!shared && !orders) continue;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Id };
            var pending = new Queue<(string Id, string Path)>();
            foreach (var dependency in root.Dependencies) pending.Enqueue((dependency, root.Id + " -> " + dependency));
            while (pending.TryDequeue(out var item))
            {
                if (!seen.Add(item.Id)) continue;
                var target = graph[item.Id];
                var business = target.Layer == "Business" || IsFamily(target.Id, "SaaS.Orders") || IsFamily(target.Id, "SaaS.OrderApprovals");
                if ((shared && business) || (orders && IsFamily(target.Id, "SaaS.OrderApprovals")))
                    errors.Add("Forbidden dependency: " + item.Path);
                foreach (var dependency in target.Dependencies) pending.Enqueue((dependency, item.Path + " -> " + dependency));
            }
        }
        return errors.Order().ToArray();
    }

    private static bool IsFamily(string id, string family) => id.Equals(family, StringComparison.OrdinalIgnoreCase)
        || id.StartsWith(family + ".", StringComparison.OrdinalIgnoreCase);
}
