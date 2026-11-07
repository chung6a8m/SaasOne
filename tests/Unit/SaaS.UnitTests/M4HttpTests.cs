using System.Net;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM4")]
public sealed partial class M4HttpTests(M1SqlFixture fixture) : IClassFixture<M1SqlFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private M1SqlEnvironment Environment => fixture.Environment;

    [Fact]
    public async Task Orders_web_route_authenticates_before_reading_business_data()
    {
        await using var setup = await HttpAsync();
        using var browser = setup.Http.NewBrowser();
        using var response = await browser.GetAsync("/alpha/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Orders_API_route_authenticates_before_reading_business_data()
    {
        await using var setup = await HttpAsync();
        using var client = setup.Http.NewClient();
        using var response = await client.GetAsync("/alpha/api/v1/orders", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpHarness> HttpAsync()
    {
        var previous = System.Environment.GetEnvironmentVariable("SAAS_ACCESS_CONTROL_ENABLED");
        System.Environment.SetEnvironmentVariable("SAAS_ACCESS_CONTROL_ENABLED", "true");
        IdentityHttpFixture? http = null;
        try
        {
            await using (var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
            {
                var ids = Environment.Tenants.Select(x => x.Id).ToArray();
                foreach (var tenant in await db.Tenants.Where(x => ids.Contains(x.Id)).ToArrayAsync(Ct))
                {
                    tenant.Subscription = SubscriptionStatus.Active; tenant.ExpiresAt = DateTimeOffset.UtcNow.AddYears(1);
                    tenant.Operation = OperationStatus.None; tenant.OperationId = null; tenant.OwnerId = null;
                    tenant.SecurityRevalidationRequired = false; tenant.Plan = "Standard"; tenant.RegistryVersion++; tenant.Fence++;
                }
                await db.SaveChangesAsync(Ct);
            }
            http = await IdentityHttpFixture.CreateAsync(Environment, productHost: true);
            foreach (var tenant in Environment.Tenants)
            {
                await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
                var current = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.Id, Ct);
                await new AccessMaintenance(new()).InstallAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, Ct);
            }
            return new(http, previous);
        }
        catch
        {
            if (http is not null) await http.DisposeAsync();
            System.Environment.SetEnvironmentVariable("SAAS_ACCESS_CONTROL_ENABLED", previous);
            throw;
        }
    }
    private sealed class HttpHarness(IdentityHttpFixture http, string? previous) : IAsyncDisposable
    {
        public IdentityHttpFixture Http { get; } = http;
        public async ValueTask DisposeAsync()
        {
            try { await Http.DisposeAsync(); }
            finally { System.Environment.SetEnvironmentVariable("SAAS_ACCESS_CONTROL_ENABLED", previous); }
        }
    }
}
