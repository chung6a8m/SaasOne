using System.Net;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.AccessControl.Data;
using SaaS.Orders;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM4")]
public sealed partial class M4HttpTests(M1SqlFixture fixture) : IClassFixture<M1SqlFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private M1SqlEnvironment Environment => fixture.Environment;
    private TestTenant Alpha => Environment.Tenants.Single(x => x.Slug == "alpha");
    private static readonly Guid SalesRole = Guid.Parse("d34d777e-ec25-413b-bdba-083e4ff18eab");
    internal static AclDocument SalesAcl(Guid actor) => new([new(SalesRole, "Orders operator",
        OrderPermissions.Definitions.Select(x => new PermissionGrant(x.Code, PermissionEffect.Allow)).ToArray())], [new(actor, SalesRole)], []);

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
    internal async Task<HttpHarness> HttpAsync(bool grant = true, bool approval = false)
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
            // Fixture administrator cleanup is deliberately not exposed as an application path.
            foreach (var tenant in Environment.Tenants)
            {
                await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(tenant), Ct);
                if (Convert.ToInt32(await SqlOperations.ScalarAsync(sql, "SELECT CASE WHEN OBJECT_ID('approvals.Requests') IS NULL THEN 0 ELSE 1 END", Ct)) == 1)
                    await SqlOperations.ExecuteAsync(sql, "DELETE FROM [approvals].[Decisions]; DELETE FROM [approvals].[Requests];", Ct);
                if (Convert.ToInt32(await SqlOperations.ScalarAsync(sql, "SELECT CASE WHEN OBJECT_ID('orders.Orders') IS NULL THEN 0 ELSE 1 END", Ct)) == 1)
                    await SqlOperations.ExecuteAsync(sql, "DELETE FROM [orders].[Receipts]; DELETE FROM [orders].[Revisions]; DELETE FROM [orders].[Orders]; DELETE FROM [audit].[Events] WHERE [Action] LIKE 'Orders.%';", Ct);
                if (Convert.ToInt32(await SqlOperations.ScalarAsync(sql, "SELECT CASE WHEN OBJECT_ID('access.State') IS NULL THEN 0 ELSE 1 END", Ct)) == 1)
                    await SeedAsync(tenant, AclDocument.Empty);
            }
            http = await IdentityHttpFixture.CreateAsync(Environment, productHost: true);
            foreach (var tenant in Environment.Tenants)
            {
                await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
                var current = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.Id, Ct);
                var maintenance = new AccessMaintenance(new());
                if (approval && tenant.Slug == "alpha")
                    await maintenance.Operations.ChangePlanAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, "Approval", Ct);
                else await maintenance.InstallAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, Ct);
                await SeedAsync(tenant, grant && tenant.Slug is "alpha" or "beta" ? SalesAcl(http.UserId) : AclDocument.Empty);
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
    internal async Task SeedAsync(TestTenant tenant, AclDocument document)
    {
        await using var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var current = await platform.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.Id, Ct);
        await using var db = AccessDbContext.Open(Environment.CustomerAdmin(tenant));
        await db.Assignments.ExecuteDeleteAsync(Ct); await db.RoleGrants.ExecuteDeleteAsync(Ct); await db.UserGrants.ExecuteDeleteAsync(Ct);
        await db.Roles.ExecuteDeleteAsync(Ct);
        var state = await db.State.SingleAsync(Ct);
        state.Revision++; state.SecurityGeneration = current.AuthorizationGeneration; state.AppliedOperationId = Guid.NewGuid(); state.AclHash = document.Hash();
        foreach (var role in document.Roles)
        {
            db.Roles.Add(new RoleRow { Id = role.Id, Name = role.Name });
            foreach (var value in role.Permissions) db.RoleGrants.Add(new RoleGrantRow { RoleId = role.Id, Permission = value.Permission, Effect = value.Effect });
        }
        foreach (var value in document.Assignments) db.Assignments.Add(new UserRoleRow { UserId = value.UserId, RoleId = value.RoleId });
        foreach (var value in document.Overrides) db.UserGrants.Add(new UserGrantRow { UserId = value.UserId, Permission = value.Permission, Effect = value.Effect });
        await db.SaveChangesAsync(Ct);
    }
    internal sealed class HttpHarness(IdentityHttpFixture http, string? previous) : IAsyncDisposable
    {
        public IdentityHttpFixture Http { get; } = http;
        public async ValueTask DisposeAsync()
        {
            try { await Http.DisposeAsync(); }
            finally { System.Environment.SetEnvironmentVariable("SAAS_ACCESS_CONTROL_ENABLED", previous); }
        }
    }
}
