using SaaS.Build;
using Xunit;
namespace SaaS.ArchitectureTests;
public sealed class DependencyRulesTests
{
    [Fact]
    public void Accepts_host_composition_and_approval_to_orders_contract()
    {
        DependencyNode[] nodes = [new("SaaS.Web", "Host", ["SaaS.Orders", "SaaS.OrderApprovals"]), new("SaaS.Orders", "Business", ["SaaS.Abstractions"]), new("SaaS.OrderApprovals", "Business", ["SaaS.Orders"]), new("SaaS.Abstractions", "Platform", [])];
        Assert.Empty(DependencyRules.Validate(nodes));
    }
    [Theory]
    [InlineData("Platform", "SaaS.Orders")]
    [InlineData("Platform", "SaaS.OrderApprovals")]
    [InlineData("Theme", "SaaS.Orders")]
    public void Rejects_forbidden_direct_dependencies(string layer, string target)
    {
        Assert.NotEmpty(DependencyRules.Validate([new("SaaS.Shared", layer, [target]), new(target, "Business", [])]));
    }
    [Fact]
    public void Rejects_transitive_dependency_through_neutral_library()
    {
        Assert.NotEmpty(DependencyRules.Validate([new("SaaS.Tenancy", "Platform", ["Bridge"]), new("Bridge", "Other", ["SaaS.Orders"]), new("SaaS.Orders", "Business", [])]));
    }
    [Fact]
    public void Orders_must_not_depend_on_approvals_implementation()
    {
        Assert.NotEmpty(DependencyRules.Validate([new("SaaS.Orders", "Business", ["SaaS.OrderApprovals"]), new("SaaS.OrderApprovals", "Business", [])]));
    }
    [Fact]
    public void Empty_graph_is_not_a_passing_architecture_check() => Assert.NotEmpty(DependencyRules.Validate([]));
    [Fact]
    public void Dangling_references_fail_closed() => Assert.NotEmpty(DependencyRules.Validate([new("SaaS.Tenancy", "Platform", ["Missing"])]));
    [Fact]
    public void Dependency_cycles_are_rejected() => Assert.NotEmpty(DependencyRules.Validate([new("A", "Other", ["B"]), new("B", "Other", ["A"])]));
}
