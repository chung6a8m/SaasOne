namespace SaaS.Build;
public sealed record DependencyNode(string Id, string Layer, IReadOnlyList<string> Dependencies);
public static class DependencyRules
{
    public static IReadOnlyList<string> Validate(IEnumerable<DependencyNode> nodes) => [];
}
