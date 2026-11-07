using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Maintenance;
using SaaS.Web.Orchard;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M3RuntimeTests
{
    private static string V(long version) => version.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task M3_private_lifecycle_commands_compare_versions_and_require_explicit_target_purge_confirmation()
    {
        var tenant = await PurgeTargetAsync(pending: false);
        var before = await PurgeRegistrationAsync(tenant);
        var expiry = before.ExpiresAt.AddDays(30).ToString("O", CultureInfo.InvariantCulture);
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["ops-list"], Ct));
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["ops-lifecycle", tenant.Id.ToString("D"), V(before.RegistryVersion), "suspended", expiry], Ct));
        var suspended = await PurgeRegistrationAsync(tenant);
        Assert.Equal(SubscriptionStatus.Suspended, suspended.Subscription);
        Assert.Equal(before.OperationId, suspended.OperationId); Assert.Equal(before.Fence, suspended.Fence);
        Assert.Equal(1, await MaintenanceCommand.RunAsync(["ops-lifecycle", tenant.Id.ToString("D"), V(before.RegistryVersion), "active", expiry], Ct));
        Assert.Equal(suspended.RegistryVersion, (await PurgeRegistrationAsync(tenant)).RegistryVersion);
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["ops-lifecycle", tenant.Id.ToString("D"), V(suspended.RegistryVersion), "delete", expiry], Ct));
        var deleted = await PurgeRegistrationAsync(tenant);
        Assert.Equal(SubscriptionStatus.PendingDeletion, deleted.Subscription);
        var operation = Guid.NewGuid().ToString("D");
        Assert.Equal(1, await MaintenanceCommand.RunAsync(["ops-purge", tenant.Id.ToString("D"), operation, V(deleted.RegistryVersion), Alpha.Id.ToString("D")], Ct));
        Assert.Null((await PurgeRegistrationAsync(tenant)).RetiredAt);
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["ops-purge", tenant.Id.ToString("D"), operation, V(deleted.RegistryVersion), tenant.Id.ToString("D")], Ct));
        Assert.NotNull((await PurgeRegistrationAsync(tenant)).PurgedAt);
    }

    [Fact]
    public async Task M3_private_plan_commands_reconcile_manifest_and_fail_closed_on_unknown_business_tables()
    {
        await using var setup = await M3HttpAsync();
        var before = await RegistrationAsync();
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["access-install", Alpha.Id.ToString("D"), Guid.NewGuid().ToString("D"), V(before.RegistryVersion)], Ct));
        before = await RegistrationAsync();
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["ops-plan", Alpha.Id.ToString("D"), Guid.NewGuid().ToString("D"), V(before.RegistryVersion), "Approval"], Ct));
        await using (var db = TenancyConnections.CustomerContext(Environment.CustomerAdmin(Alpha)))
            Assert.Equal("Approval", (await db.ApplicationProfile.SingleAsync(Ct)).Plan);
        var operation = Guid.NewGuid().ToString("D");
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        await SqlOperations.ExecuteAsync(sql, "CREATE TABLE dbo.M3UnknownBusinessObligations (Id int NOT NULL PRIMARY KEY)", Ct);
        try
        {
            before = await RegistrationAsync();
            Assert.Equal(1, await MaintenanceCommand.RunAsync(["ops-plan", Alpha.Id.ToString("D"), operation, V(before.RegistryVersion), "Standard"], Ct));
            Assert.Equal("Approval", (await RegistrationAsync()).Plan);
            Assert.False((await TenantWriteGate.ReadAsync(sql, null, Ct)).IsOpen);
        }
        finally { await SqlOperations.ExecuteAsync(sql, "DROP TABLE dbo.M3UnknownBusinessObligations", Ct); }
        before = await RegistrationAsync();
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["ops-resume", Alpha.Id.ToString("D"), operation, V(before.RegistryVersion)], Ct));
        Assert.Equal("Standard", (await RegistrationAsync()).Plan);
    }

    [Fact]
    public async Task M3_private_install_upgrades_prior_tenancy_schema_under_the_durable_gate()
    {
        await using var setup = await M3HttpAsync();
        await using (var db = TenancyConnections.CustomerContext(Environment.CustomerAdmin(Alpha)))
            await db.GetService<IMigrator>().MigrateAsync("20261107015335_TenantWriteGateM3", Ct);
        var before = await RegistrationAsync();
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["access-install", Alpha.Id.ToString("D"), Guid.NewGuid().ToString("D"), V(before.RegistryVersion)], Ct));
        await using var current = TenancyConnections.CustomerContext(Environment.CustomerAdmin(Alpha));
        Assert.Equal(Alpha.Id, (await current.ApplicationProfile.SingleAsync(Ct)).TenantId);
        Assert.True((await current.WriteGate.SingleAsync(Ct)).IsOpen);
        Assert.Equal(OperationStatus.None, (await RegistrationAsync()).Operation);
    }

    [Fact]
    public async Task M3_private_recovery_commands_require_external_approval_and_apply_full_current_acl()
    {
        await using var setup = await M3HttpAsync();
        await using (var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
        {
            var authority = await db.RecoveryAuthorities.SingleOrDefaultAsync(x => x.Actor == "saas_platform_operator", Ct);
            if (authority is null) { authority = new RecoveryAuthority { Actor = "saas_platform_operator" }; db.RecoveryAuthorities.Add(authority); }
            authority.Enabled = true; authority.CanChangePlan = true; authority.RegisteredAt = DateTimeOffset.UtcNow.AddDays(-1); authority.RegisteredBy = "fixture-administrator";
            await db.SaveChangesAsync(Ct);
        }
        var operation = Guid.NewGuid().ToString("D");
        var before = await RegistrationAsync();
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["ops-recovery-begin", Alpha.Id.ToString("D"), operation, V(before.RegistryVersion)], Ct));
        before = await RegistrationAsync();
        Assert.Equal(1, await MaintenanceCommand.RunAsync(["ops-recovery-resume", Alpha.Id.ToString("D"), operation, V(before.RegistryVersion)], Ct));
        var file = Path.GetTempFileName();
        var oldReason = System.Environment.GetEnvironmentVariable("SAAS_RECOVERY_REASON");
        try
        {
            await File.WriteAllTextAsync(file, AclDocument.Empty.CanonicalJson(), Ct);
            System.Environment.SetEnvironmentVariable("SAAS_RECOVERY_REASON", "Reviewed current memberships and explicitly approved an empty ACL.");
            before = await RegistrationAsync();
            Assert.Equal(0, await MaintenanceCommand.RunAsync(["ops-recovery-approve", Alpha.Id.ToString("D"), operation, V(before.RegistryVersion), file, "keep-plan"], Ct));
            before = await RegistrationAsync();
            Assert.Equal(0, await MaintenanceCommand.RunAsync(["ops-recovery-resume", Alpha.Id.ToString("D"), operation, V(before.RegistryVersion)], Ct));
            var ready = await RegistrationAsync();
            Assert.False(ready.SecurityRevalidationRequired); Assert.Equal(OperationStatus.None, ready.Operation);
            await using var access = AccessDbContext.Open(Environment.CustomerAdmin(Alpha));
            Assert.Empty(await access.Assignments.ToArrayAsync(Ct)); Assert.Empty(await access.Roles.ToArrayAsync(Ct));
            Assert.Equal(ready.AuthorizationGeneration, (await access.State.SingleAsync(Ct)).SecurityGeneration);
            await using var audit = AuditDbContext.Open(Environment.CustomerAdmin(Alpha));
            Assert.Equal(1, await audit.Events.CountAsync(x => x.Id == Guid.Parse(operation) && x.Action == "AclRevalidated", Ct));
        }
        finally { File.Delete(file); System.Environment.SetEnvironmentVariable("SAAS_RECOVERY_REASON", oldReason); }
    }

    [Fact]
    public async Task M3_identity_initialization_never_recreates_retired_clients_and_cannot_enroll_retired_memberships()
    {
        await using var setup = await M3HttpAsync();
        var retired = await PurgeTargetAsync();
        var before = await PurgeRegistrationAsync(retired);
        var adapter = new PlanFixture(Environment.CustomerAdmin(retired));
        await new TenantOperations(new(), adapter, adapter).PurgeAsync(retired.Id, Guid.NewGuid(), before.RegistryVersion, Ct);
        var installer = new IdentityInstaller(new(), setup.Http.Deployment);
        await installer.InitializeAsync(Environment.InstallationId, Ct);
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        Assert.False(await db.IdentityClients.AnyAsync(x => x.TenantId == retired.Id && x.Enabled, Ct));
        await Assert.ThrowsAsync<TenantContractException>(() => new IdentityOperations(new()).SetMembershipAsync(setup.Http.UserId, retired.Id, true, Ct));
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("suspended")]
    [InlineData("expired")]
    public async Task M3_http_same_credentials_lose_access_immediately_without_affecting_other_tenants(string reason)
    {
        await using var setup = await M3HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await HttpLoginAsync(http, browser);
        var token = http.CapturedTokens["alpha"].AccessToken;
        var beta = http.CapturedTokens["beta"].AccessToken;
        await AssertHttpPairAsync(http, browser, token, HttpStatusCode.OK);
        if (reason == "membership") await http.Operations.SetMembershipAsync(http.UserId, Alpha.Id, false, Ct);
        else
        {
            var current = await RegistrationAsync();
            await http.Operations.SetTenantAsync(Alpha.Id, reason == "suspended" ? SubscriptionStatus.Suspended : SubscriptionStatus.Active,
                reason == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1) : current.ExpiresAt, Ct);
        }
        using (var api = await HttpProbeAsync(http, token)) Assert.Contains(api.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        using (var web = await browser.GetAsync("/alpha/workspace")) Assert.Contains(web.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        using (var other = await HttpProbeAsync(http, beta, "/beta/api/v1/workspace")) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using (var otherWeb = await browser.GetAsync("/beta/workspace")) Assert.Equal(HttpStatusCode.OK, otherWeb.StatusCode);
    }
}
