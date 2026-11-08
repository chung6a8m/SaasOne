using System.Reflection;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M7TransportContractTests
{
    [Fact]
    public void Load_fixture_must_offer_an_explicit_real_network_transport_without_changing_existing_tests()
    {
        var factory = typeof(IdentityHttpFixture).GetMethod("CreateAsync", BindingFlags.Public | BindingFlags.Static)!;
        var network = factory.GetParameters().SingleOrDefault(x => x.Name == "network");
        Assert.True(network is not null, "M7 requires real HTTPS transport; TestServer measurements are not network load evidence.");
        Assert.True(network.HasDefaultValue && Equals(false, network.DefaultValue));
        Assert.NotNull(typeof(IdentityHttpFixture).GetProperty("NetworkPort"));
    }
}
