using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.OrderApprovals;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using SaaS.Web.Access;
using Xunit;
using static SaaS.UnitTests.M4HttpTests;

namespace SaaS.UnitTests;

public sealed partial class M6BackupSqlTests
{
    [Fact]
    public async Task Refused_Approval_upgrade_can_supersede_with_approved_older_backup_but_never_open_early()
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync(approval: true);
        await AuthorityAsync();
        var http = setup.Http;
        using var store = new SqlBackupStore();
        var clean = await BackupAsync(store);
        var creator = Guid.NewGuid();
        var orders = new OrderService(Environment.CustomerAdmin(Alpha), Alpha.Id, M4PolicyTests.Catalog,
            (_, _) => Task.FromResult(M4PolicyTests.Access(creator, tenant: Alpha.Id)), new OrderApprovalHandler());
        var draft = (await orders.ExecuteAsync(new(OrderAction.Create, "m6-supersede-create", Draft: M4PolicyTests.Draft), Ct)).Order;
        var pending = (await orders.ExecuteAsync(M4PolicyTests.Command(draft), Ct)).Order;
        Assert.Equal(OrderStatus.PendingApproval, pending.Status);
        var pendingBackup = await BackupAsync(store);

        using var browser = http.NewBrowser();
        await LoginAsync(http, browser);
        var path = "/alpha/api/v1/orders/" + pending.Id + "/approve";
        using (var approval = await ApiAsync(http, http.CapturedTokens["alpha"].AccessToken,
                   path, "m6-supersede-first-approve", Expected(pending)))
            Assert.Equal(OrderStatus.Confirmed, (await ResultAsync(approval, System.Net.HttpStatusCode.OK)).Order.Status);
        var maintenance = new AccessMaintenance(new());
        await maintenance.Operations.ChangePlanAsync(Alpha.Id, Guid.NewGuid(),
            (await CurrentAsync()).RegistryVersion, "Standard", Ct);

        var firstId = Guid.NewGuid();
        var first = await maintenance.Operations.RestoreBackupAsync(Alpha.Id, firstId,
            (await CurrentAsync()).RegistryVersion, pendingBackup.BackupId, store, false, Ct);
        var acl = SalesAcl(http.UserId);
        var deniedUpgrade = await maintenance.ApproveAsync(Alpha.Id, firstId, first.RegistryVersion, acl,
            "Do not authorize restoring Approval entitlement", false, Ct);
        var blocked = await Assert.ThrowsAsync<TenantContractException>(() =>
            maintenance.ResumeRecoveryAsync(Alpha.Id, firstId, deniedUpgrade.RegistryVersion, Ct));
        Assert.Equal("recovery_child_upgrade_requires_approval", blocked.Code);

        var previous = await CurrentAsync();
        Assert.Equal(OperationStatus.Restoring, previous.Operation);
        Assert.True(previous.SecurityRevalidationRequired);
        var replacementId = Guid.NewGuid();
        const string loss = "Discard pending order and all subsequent changes since the selected older full backup";
        var decision = new RestoreSupersedeDecision(loss, "Current platform authority approves the older recovery point");
        var invalid = await Assert.ThrowsAsync<TenantContractException>(() =>
            maintenance.Operations.SupersedeRecoverySourceAsync(Alpha.Id, firstId, replacementId,
                previous.RegistryVersion, clean.BackupId, store, new(" ", decision.Reason), Ct));
        Assert.Equal("recovery_supersede_decision_invalid", invalid.Code);
        var same = await Assert.ThrowsAsync<TenantContractException>(() =>
            maintenance.Operations.SupersedeRecoverySourceAsync(Alpha.Id, firstId, replacementId,
                previous.RegistryVersion, pendingBackup.BackupId, store, decision, Ct));
        Assert.Equal("recovery_supersede_same_source", same.Code);

        await using (var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
        {
            var authority = await platform.RecoveryAuthorities.SingleAsync(x => x.Actor == "saas_platform_operator", Ct);
            authority.Enabled = false;
            await platform.SaveChangesAsync(Ct);
        }
        var forbidden = await Assert.ThrowsAsync<TenantContractException>(() =>
            maintenance.Operations.SupersedeRecoverySourceAsync(Alpha.Id, firstId, replacementId,
                previous.RegistryVersion, clean.BackupId, store, decision, Ct));
        Assert.Equal("recovery_authority_required", forbidden.Code);
        Assert.Equal(previous.RegistryVersion, (await CurrentAsync()).RegistryVersion);
        await AuthorityAsync();

        var second = await maintenance.Operations.SupersedeRecoverySourceAsync(Alpha.Id, firstId, replacementId,
            previous.RegistryVersion, clean.BackupId, store, decision, Ct);
        Assert.Equal("PENDING_PHYSICAL_RESTORE", second.Status);
        var gated = await CurrentAsync();
        Assert.Equal(replacementId, gated.OperationId);
        Assert.Equal(OperationStatus.Restoring, gated.Operation);
        Assert.True(gated.SecurityRevalidationRequired);
        Assert.True(gated.AuthorizationGeneration > previous.AuthorizationGeneration);
        Assert.Equal(second.RegistryVersion, gated.RegistryVersion);
        await using (var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
        {
            var old = await platform.ResourceOperations.SingleAsync(x => x.Id == firstId, Ct);
            Assert.NotNull(old.CompletedAt);
            Assert.NotEqual(ResourceCheckpoint.Completed, old.Checkpoint);
            var superseded = await platform.Audit.SingleAsync(x => x.OperationId == firstId
                && x.Action == "RecoverySourceSuperseded", Ct);
            Assert.Contains(loss, superseded.Details);
            Assert.True(superseded.Details.Contains(clean.BackupId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        }
        var stale = await Assert.ThrowsAsync<TenantContractException>(() =>
            maintenance.ResumeRecoveryAsync(Alpha.Id, firstId, second.RegistryVersion, Ct));
        Assert.Equal("physical_restore_incomplete", stale.Code);
        var stillClosed = await Assert.ThrowsAsync<TenantContractException>(() =>
            maintenance.ResumeRecoveryAsync(Alpha.Id, replacementId, second.RegistryVersion, Ct));
        Assert.Equal("physical_restore_incomplete", stillClosed.Code);

        var restored = await maintenance.Operations.RestoreBackupAsync(Alpha.Id, replacementId,
            second.RegistryVersion, clean.BackupId, store, true, Ct);
        Assert.True((await CurrentAsync()).SecurityRevalidationRequired);
        await Assert.ThrowsAsync<TenantContractException>(() =>
            maintenance.ResumeRecoveryAsync(Alpha.Id, replacementId, restored.RegistryVersion, Ct));
        var approved = await maintenance.ApproveAsync(Alpha.Id, replacementId, (await CurrentAsync()).RegistryVersion,
            acl, "Approve current ACL after the replacement physical recovery", false, Ct);
        var completed = await maintenance.ResumeRecoveryAsync(Alpha.Id, replacementId, approved.RegistryVersion, Ct);
        Assert.Equal(ResourceCheckpoint.Completed, completed.Checkpoint);
        var ready = await CurrentAsync();
        Assert.Equal("Standard", ready.Plan);
        Assert.Equal(OperationStatus.None, ready.Operation);
        Assert.False(ready.SecurityRevalidationRequired);
        await using var database = OrdersDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.False(await database.Orders.AnyAsync(x => x.Id == pending.Id, Ct));
        Assert.True(TenantPolicy.IsOperational((await CurrentAsync(Environment.Tenants[1])).Status(), DateTimeOffset.UtcNow));
    }
}
