using System.Net;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Product_routing_readiness_uses_one_fresh_connection_per_database_per_request()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment, productHost: true);
        using var client = http.NewClient();
        // Native activation is not part of the steady-state request budget.
        foreach (var path in new[] { "/health/ready", "/alpha/health/ready" })
        {
            using var warm = await client.GetAsync(path, ct);
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        }
        for (var attempt = 0; attempt < 2; attempt++)
        foreach (var (path, expectedOpens) in new[] { ("/health/ready", 1), ("/alpha/health/ready", 2) })
        {
            using var profile = new M7SqlProfile();
            using var response = await client.GetAsync(path, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var measured = profile.Snapshot();
            Assert.Equal(expectedOpens, measured.Open.Count);
            Assert.Equal(0, measured.Open.Errors);
            Assert.True(measured.Command.Count >= 8, "Readiness must execute fresh source checks, not a cached ready result.");
            Assert.Equal(0, measured.Pending);
        }
    }

    [Fact]
    public async Task Product_routing_does_not_reuse_ready_connections_after_runtime_login_disable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment, productHost: true);
        using var client = http.NewClient();
        using (var ready = await client.GetAsync("/alpha/health/ready", ct)) Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var tenantLogin = "saas_rt_" + http.Tenant("alpha").Id.ToString("N");
        await using var admin = await SqlOperations.OpenAsync(http.Environment.Admin, ct);
        foreach (var login in new[] { tenantLogin, "saas_platform_runtime" })
        {
            // Both identifiers are fixture-owned, never input from an HTTP request.
            await SqlOperations.ExecuteAsync(admin, $"ALTER LOGIN [{login}] DISABLE", ct);
            try
            {
                using var blocked = await client.GetAsync("/alpha/health/ready", ct);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            }
            finally { await SqlOperations.ExecuteAsync(admin, $"ALTER LOGIN [{login}] ENABLE", CancellationToken.None); }
            using var recovered = await client.GetAsync("/alpha/health/ready", ct);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        }
    }
}
