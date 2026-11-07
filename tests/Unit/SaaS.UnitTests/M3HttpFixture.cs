using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M3RuntimeTests
{
    private const string M3Flag = "SAAS_ACCESS_CONTROL_ENABLED";
    private static readonly Guid ManagerRole = Guid.Parse("d34d777e-ec25-413b-bdba-083e4ff18eaa");
    private static AclDocument ManagerAcl(Guid user) => new([new(ManagerRole, "Application administrator", [
        new("Access.Manage", PermissionEffect.Allow), new("Workspace.View", PermissionEffect.Allow),
        new("Workspace.Approval", PermissionEffect.Allow), new("Profile.ReadOwn", PermissionEffect.Allow)])], [new(user, ManagerRole)], []);

    private async Task<M3HttpHarness> M3HttpAsync(bool grant = true)
    {
        var previous = System.Environment.GetEnvironmentVariable(M3Flag);
        System.Environment.SetEnvironmentVariable(M3Flag, "true");
        IdentityHttpFixture? http = null;
        try
        {
            // Independent tests reset ONLY their four disposable fixtures, never an application recovery path.
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
            foreach (var tenant in Environment.Tenants)
            {
                await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(tenant), Ct);
                if (await SqlOperations.ScalarAsync(sql, "SELECT CASE WHEN OBJECT_ID('access.State') IS NULL THEN 0 ELSE 1 END", Ct) is int present && present == 1)
                    await SeedHttpAclAsync(tenant, AclDocument.Empty);
            }
            http = await IdentityHttpFixture.CreateAsync(Environment, productHost: true);
            foreach (var tenant in Environment.Tenants)
            {
                await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
                var current = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.Id, Ct);
                await new AccessMaintenance(new()).InstallAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, Ct);
                await SeedHttpAclAsync(tenant, grant && tenant.Slug is "alpha" or "beta" ? ManagerAcl(http.UserId) : AclDocument.Empty);
            }
            return new(http, previous);
        }
        catch
        {
            if (http is not null) await http.DisposeAsync();
            System.Environment.SetEnvironmentVariable(M3Flag, previous);
            throw;
        }
    }

    private async Task SeedHttpAclAsync(TestTenant tenant, AclDocument document)
    {
        await using var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var current = await platform.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.Id, Ct);
        await using var db = AccessDbContext.Open(Environment.CustomerAdmin(tenant));
        await db.Assignments.ExecuteDeleteAsync(Ct); await db.RoleGrants.ExecuteDeleteAsync(Ct); await db.UserGrants.ExecuteDeleteAsync(Ct);
        await db.Roles.ExecuteDeleteAsync(Ct);
        var state = await db.State.SingleOrDefaultAsync(Ct);
        if (state is null) { state = new AclState { TenantId = tenant.Id, Revision = 1 }; db.State.Add(state); }
        else state.Revision++;
        state.SecurityGeneration = current.AuthorizationGeneration; state.AppliedOperationId = Guid.NewGuid(); state.AclHash = document.Hash();
        foreach (var role in document.Roles)
        {
            db.Roles.Add(new RoleRow { Id = role.Id, Name = role.Name });
            foreach (var value in role.Permissions) db.RoleGrants.Add(new RoleGrantRow { RoleId = role.Id, Permission = value.Permission, Effect = value.Effect });
        }
        foreach (var value in document.Assignments) db.Assignments.Add(new UserRoleRow { UserId = value.UserId, RoleId = value.RoleId });
        foreach (var value in document.Overrides) db.UserGrants.Add(new UserGrantRow { UserId = value.UserId, Permission = value.Permission, Effect = value.Effect });
        await db.SaveChangesAsync(Ct);
    }
    private async Task<Guid> HttpTargetMemberAsync()
    {
        var user = Guid.NewGuid();
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        db.IdentityAccounts.Add(new IdentityAccount { Id = user, CreatedAt = DateTimeOffset.UtcNow });
        db.IdentityMemberships.Add(new IdentityMembership { GlobalUserId = user, TenantId = Alpha.Id, Active = true });
        await db.SaveChangesAsync(Ct);
        return user;
    }
    private sealed class M3HttpHarness(IdentityHttpFixture http, string? previous) : IAsyncDisposable
    {
        public IdentityHttpFixture Http { get; } = http;
        public async ValueTask DisposeAsync()
        {
            try { await Http.DisposeAsync(); }
            finally { System.Environment.SetEnvironmentVariable(M3Flag, previous); }
        }
    }
}
