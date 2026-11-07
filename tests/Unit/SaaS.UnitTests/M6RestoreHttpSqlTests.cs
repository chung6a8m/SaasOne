using System.Net;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.OrderApprovals;
using SaaS.OrderApprovals.Data;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using SaaS.Web.Access;
using Xunit;
using static SaaS.UnitTests.M4HttpTests;
namespace SaaS.UnitTests;

public sealed partial class M6BackupSqlTests
{
    [Fact]
    public async Task Physical_restore_never_resurrects_revoked_assignment_role_permission_or_Deny_after_fresh_login()
    {
        foreach (var change in new[] { "assignment", "role-permission", "deny" })
        {
            var setupService = new M4HttpTests(fixture);
            await using var setup = await setupService.HttpAsync(); await AuthorityAsync(); var http = setup.Http;
            using var store = new SqlBackupStore(); var backup = await BackupAsync(store);
            using var oldBrowser = http.NewBrowser(); await LoginAsync(http, oldBrowser);
            var oldToken = http.CapturedTokens["alpha"].AccessToken;
            using (var authorized = await ApiAsync(http, oldToken, "/alpha/api/v1/orders")) Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
            var previousAcl = SalesAcl(http.UserId);
            var approvedAcl = change switch
            {
                "assignment" => previousAcl with { Assignments = [] },
                "role-permission" => previousAcl with { Roles = previousAcl.Roles.Select(r => r with {
                    Permissions = r.Permissions.Where(p => p.Permission is not ("Orders.ViewOwn" or "Orders.ViewOthers")).ToArray() }).ToArray() },
                _ => previousAcl with { Overrides = [new(http.UserId, "Orders.ViewOwn", PermissionEffect.Deny), new(http.UserId, "Orders.ViewOthers", PermissionEffect.Deny)] }
            };
            await setupService.SeedAsync(Alpha, approvedAcl); // Membership stays active throughout.
            var operation = Guid.NewGuid(); var maintenance = new AccessMaintenance(new());
            var restored = await maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation, (await CurrentAsync()).RegistryVersion, backup.BackupId, store, false, Ct);
            var approved = await maintenance.ApproveAsync(Alpha.Id, operation, restored.RegistryVersion, approvedAcl,
                "Use the currently authorized full ACL, never the restored historical ACL: " + change, false, Ct);
            await maintenance.ResumeRecoveryAsync(Alpha.Id, operation, approved.RegistryVersion, Ct);
            using (var old = await ApiAsync(http, oldToken, "/alpha/api/v1/orders")) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
            using (var oldCookie = await oldBrowser.GetAsync("/alpha/orders")) Assert.NotEqual(HttpStatusCode.OK, oldCookie.StatusCode);
            using var freshBrowser = http.NewBrowser(); await LoginAsync(http, freshBrowser);
            using (var fresh = await ApiAsync(http, http.CapturedTokens["alpha"].AccessToken, "/alpha/api/v1/orders"))
                Assert.Equal(HttpStatusCode.Forbidden, fresh.StatusCode);
            await using var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
            Assert.True(await platform.IdentityMemberships.AnyAsync(x => x.TenantId == Alpha.Id && x.GlobalUserId == http.UserId && x.Active, Ct));
        }
    }

    [Fact]
    public async Task Real_backup_pending_approval_restore_requires_explicit_child_upgrade_then_normal_approval()
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync(approval: true); await AuthorityAsync();
        var http = setup.Http;
        var creator = Guid.NewGuid();
        var orders = new OrderService(Environment.CustomerAdmin(Alpha), Alpha.Id, M4PolicyTests.Catalog,
            (_, _) => Task.FromResult(M4PolicyTests.Access(creator, tenant: Alpha.Id)), new OrderApprovalHandler());
        var draft = (await orders.ExecuteAsync(new(OrderAction.Create, "m6-restore-create", Draft: M4PolicyTests.Draft), Ct)).Order;
        var pending = (await orders.ExecuteAsync(M4PolicyTests.Command(draft), Ct)).Order;
        Assert.Equal(OrderStatus.PendingApproval, pending.Status);
        using var store = new SqlBackupStore(); var backup = await BackupAsync(store);
        using var browser = http.NewBrowser(); await LoginAsync(http, browser);
        var path = "/alpha/api/v1/orders/" + pending.Id + "/approve";
        using (var approved = await ApiAsync(http, http.CapturedTokens["alpha"].AccessToken, path, "m6-first-approve", Expected(pending)))
            Assert.Equal(OrderStatus.Confirmed, (await ResultAsync(approved, HttpStatusCode.OK)).Order.Status);
        var maintenance = new AccessMaintenance(new());
        await maintenance.Operations.ChangePlanAsync(Alpha.Id, Guid.NewGuid(), (await CurrentAsync()).RegistryVersion, "Standard", Ct);
        var operation = Guid.NewGuid();
        var restored = await maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation, (await CurrentAsync()).RegistryVersion, backup.BackupId, store, false, Ct);
        var decision = await maintenance.ApproveAsync(Alpha.Id, operation, restored.RegistryVersion, SalesAcl(http.UserId), "Current reviewer ACL; child plan upgrade not yet approved", false, Ct);
        await Assert.ThrowsAsync<TenantContractException>(() => maintenance.ResumeRecoveryAsync(Alpha.Id, operation, decision.RegistryVersion, Ct));
        Assert.Equal("Standard", (await CurrentAsync()).Plan);
        decision = await maintenance.ApproveAsync(Alpha.Id, operation, (await CurrentAsync()).RegistryVersion, SalesAcl(http.UserId), "Explicitly authorize Approval child upgrade for restored outstanding work", true, Ct);
        await maintenance.ResumeRecoveryAsync(Alpha.Id, operation, decision.RegistryVersion, Ct);
        Assert.Equal("Approval", (await CurrentAsync()).Plan);
        await using (var database = OrdersDbContext.Open(Environment.CustomerAdmin(Alpha)))
        {
            var historical = await database.Orders.SingleAsync(x => x.Id == pending.Id, Ct);
            Assert.Equal(OrderStatus.PendingApproval, historical.Status); Assert.Equal(pending.Revision, historical.Revision);
        }
        using var fresh = http.NewBrowser(); await LoginAsync(http, fresh);
        using (var approved = await ApiAsync(http, http.CapturedTokens["alpha"].AccessToken, path, "m6-restored-approve", Expected(pending)))
            Assert.Equal(OrderStatus.Confirmed, (await ResultAsync(approved, HttpStatusCode.OK)).Order.Status);
        await using var approvals = ApprovalsDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Single(await approvals.Decisions.Where(x => x.OrderId == pending.Id).ToArrayAsync(Ct));
    }
}
