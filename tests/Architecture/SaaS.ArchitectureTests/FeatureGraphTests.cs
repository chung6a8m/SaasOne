using SaaS.Build;
using Xunit;
namespace SaaS.ArchitectureTests;

public sealed class FeatureGraphTests
{
    private static DependencyNode[] Projects() =>
    [
        new("SaaS.Web", "Host", ["SaaS.Tenancy", "SaaS.Theme.App", "SaaS.Orders", "SaaS.OrderApprovals"]),
        new("SaaS.Tenancy", "Platform", []), new("SaaS.Theme.App", "Theme", []),
        new("SaaS.Orders", "Business", []), new("SaaS.OrderApprovals", "Business", []), new("Package.Bridge", "Package", [])
    ];

    [Fact]
    public void Valid_native_graph_preserves_feature_ownership_and_dependencies()
    {
        var graph = FeatureGraph.Combine(Projects(),
        [new("SaaS.Tenancy", "SaaS.Tenancy", []), new("SaaS.Theme.App", "SaaS.Theme.App", []),
         new("SaaS.Orders", "SaaS.Orders", ["SaaS.Tenancy"]), new("SaaS.OrderApprovals", "SaaS.OrderApprovals", ["SaaS.Orders"])]);
        Assert.Empty(DependencyRules.Validate(graph));
        Assert.Contains(graph, node => node.Id == "feature:SaaS.Theme.App" && node.Layer == "Theme");
        Assert.Contains("feature:SaaS.Orders", Assert.Single(graph, node => node.Id == "feature:SaaS.OrderApprovals").Dependencies);
    }

    [Theory]
    [InlineData("SaaS.Tenancy", false)]
    [InlineData("SaaS.Tenancy", true)]
    [InlineData("SaaS.Theme.App", false)]
    [InlineData("SaaS.Theme.App", true)]
    public void Native_direct_and_transitive_platform_or_theme_to_business_edges_fail(string root, bool transitive)
    {
        var graph = FeatureGraph.Combine(Projects(),
        [new(root, root, [transitive ? "Bridge.Feature" : "Orders.Feature"]),
         new("Bridge.Feature", "Package.Bridge", ["Orders.Feature"]), new("Orders.Feature", "SaaS.Orders", [])]);
        Assert.Contains(DependencyRules.Validate(graph), message => message.Contains("Forbidden dependency", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_or_duplicate_native_metadata_cannot_be_silently_ignored()
    {
        Assert.Throws<InvalidDataException>(() => FeatureGraph.Combine(Projects(), []));
        Assert.Throws<InvalidDataException>(() => FeatureGraph.Combine(Projects(), [new("Feature", "Unknown", [])]));
        Assert.Throws<InvalidDataException>(() => FeatureGraph.Combine(Projects(), [new("Feature", "SaaS.Tenancy", ["Missing"])]));
        Assert.Throws<InvalidDataException>(() => FeatureGraph.Combine(Projects(), [new("Feature", "SaaS.Tenancy", []), new("Feature", "SaaS.Theme.App", [])]));
    }
}
