using SaaS.Build;
using Xunit;
namespace SaaS.ArchitectureTests;

public sealed class MixedFeatureGraphTests
{
    [Theory]
    [InlineData("SaaS.Tenancy", "Platform", "SaaS.Orders", false)]
    [InlineData("SaaS.Tenancy", "Platform", "SaaS.Orders", true)]
    [InlineData("SaaS.Theme.App", "Theme", "SaaS.Orders", false)]
    [InlineData("SaaS.Theme.App", "Theme", "SaaS.Orders", true)]
    [InlineData("SaaS.Orders", "Business", "SaaS.OrderApprovals", false)]
    [InlineData("SaaS.Orders", "Business", "SaaS.OrderApprovals", true)]
    public void Native_feature_to_owning_package_dependencies_cannot_bypass_boundaries(string root, string layer, string forbidden, bool transitive)
    {
        // Only the composition root references the bridge. The shared module reaches it via a feature.
        DependencyNode[] projects =
        [
            new("SaaS.Web", "Host", [root, "Package.Bridge", forbidden]),
            new(root, layer, []),
            new("Package.Bridge", "Package", [transitive ? "Package.Inner" : forbidden]),
            new("Package.Inner", "Package", [forbidden]),
            new(forbidden, "Business", [])
        ];
        var graph = FeatureGraph.Combine(projects,
        [new(root, root, ["Bridge.Feature"]), new("Bridge.Feature", "Package.Bridge", [])]);
        var errors = DependencyRules.Validate(graph);
        Assert.Contains(errors, message => message.Contains("Forbidden dependency", StringComparison.Ordinal)
            && message.Contains(root, StringComparison.Ordinal) && message.Contains(forbidden, StringComparison.Ordinal));
        Assert.DoesNotContain(errors, message => message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Same_owner_native_feature_chain_does_not_create_an_artificial_cycle()
    {
        DependencyNode[] projects =
        [
            new("SaaS.Web", "Host", ["SaaS.Tenancy"]),
            new("SaaS.Tenancy", "Platform", ["Safe.Package"]),
            new("Safe.Package", "Package", [])
        ];
        var graph = FeatureGraph.Combine(projects,
        [new("SaaS.Tenancy.Main", "SaaS.Tenancy", ["SaaS.Tenancy.Helper"]), new("SaaS.Tenancy.Helper", "SaaS.Tenancy", [])]);
        Assert.Empty(DependencyRules.Validate(graph));
    }
}
