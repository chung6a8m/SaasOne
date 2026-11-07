using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M5RouteTests
{
    [Fact]
    public void Product_exposes_only_exact_POST_decision_routes_for_the_tenant_authority_to_gate()
    {
        var endpoints = new ProductApplicationEndpoints();
        var id = Guid.NewGuid().ToString("D");
        foreach (var root in new[] { "/orders/", "/api/v1/orders/" })
        foreach (var action in new[] { "approve", "reject" })
        {
            var path = root + id + "/" + action;
            Assert.True(endpoints.Matches(path, "POST"));
            Assert.False(endpoints.Matches(path, "GET"));
            Assert.False(endpoints.Matches(path, "DELETE"));
            Assert.False(endpoints.Matches(path + "/extra", "POST"));
            Assert.False(endpoints.Matches(root + Guid.Empty + "/" + action, "POST"));
        }
    }
}
