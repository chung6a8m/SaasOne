namespace SaaS.Build;

public sealed record NativeFeature(string Id, string ExtensionId, string[] Dependencies);
public static class FeatureGraph
{
    public static IReadOnlyList<DependencyNode> Combine(IReadOnlyList<DependencyNode> projects, IEnumerable<NativeFeature> features) =>
        throw new NotImplementedException("Native Orchard feature metadata adapter is required.");
}
