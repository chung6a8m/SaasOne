using SaaS.Build;
using Xunit;
namespace SaaS.ArchitectureTests;
public sealed class UnavailableFeatureTests
{
    private static DependencyNode[] Owners() => [new("SaaS.Theme.App", "Theme", []), new("OrchardCore.OpenId", "Package", [])];
    [Fact]
    public void Dormant_third_party_dependencies_remain_explicitly_unavailable()
    {
        var graph = FeatureGraph.Combine(Owners(), [new("OrchardCore.OpenId.Client", "OrchardCore.OpenId", ["OrchardCore.Users"])]);
        Assert.Contains(graph, node => node.Id == "feature:OrchardCore.Users" && node.Layer == "UnavailableNativeFeature");
        Assert.Contains("feature:OrchardCore.Users", Assert.Single(graph, node => node.Id == "feature:OrchardCore.OpenId.Client").Dependencies);
    }
    [Fact]
    public void An_enabled_profile_cannot_use_an_unavailable_dependency_directly_or_transitively()
    {
        Assert.Throws<InvalidDataException>(() => FeatureGraph.Combine(Owners(), [new("OrchardCore.OpenId.Client", "OrchardCore.OpenId", ["OrchardCore.Users"], true)]));
        Assert.Throws<InvalidDataException>(() => FeatureGraph.Combine(Owners(), [new("SaaS.Theme.App", "SaaS.Theme.App", ["OrchardCore.OpenId.Client"], true), new("OrchardCore.OpenId.Client", "OrchardCore.OpenId", ["OrchardCore.Users"])]));
        Assert.Throws<InvalidDataException>(() => FeatureGraph.Combine(Owners(), [new("SaaS.Theme.App", "SaaS.Theme.App", ["OrchardCore.Users"])]));
    }
}
