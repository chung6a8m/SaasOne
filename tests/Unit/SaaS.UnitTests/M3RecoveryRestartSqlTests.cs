using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
using SaaS.Web.Maintenance;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M3RuntimeTests
{
    [Theory]
    [InlineData("membership-before-apply")]
    [InlineData("membership-after-apply")]
    [InlineData("account-after-apply")]
    [InlineData("authority-after-apply")]
    public async Task Recovery_restart_replaces_frozen_decision_without_restoring_revoked_rights(string fault)
    {
        var setup = await RecoveryReadyAsync();
        Guid user;
        await using (var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
            user = (await db.IdentitySessions.SingleAsync(x => x.Id == setup.Session, Ct)).GlobalUserId;
        var role = Guid.NewGuid();
        var original = Decision(new AclDocument([new(role, "Current reader", [new("Sample.Read", PermissionEffect.Allow)])], [new(user, role)], []));
        var previous = Guid.NewGuid(); var replacement = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        await service.BeginRecoveryAsync(Alpha.Id, previous, setup.Tenant.RegistryVersion, Ct);
        var current = await RegistrationAsync();
        await service.ApproveRecoveryAsync(Alpha.Id, previous, current.RegistryVersion, original, Ct);
        async Task RevokeAsync(CancellationToken ct)
        {
            if (fault.StartsWith("membership", StringComparison.Ordinal)) await new IdentityOperations(new()).SetMembershipAsync(user, Alpha.Id, false, ct);
            else if (fault.StartsWith("account", StringComparison.Ordinal)) await new IdentityOperations(new()).SetAccountEnabledAsync(user, false, ct);
            else
            {
                await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
                await db.RecoveryAuthorities.Where(x => x.Actor == "saas_platform_operator").ExecuteUpdateAsync(set => set.SetProperty(x => x.Enabled, false), ct);
            }
        }
        if (fault == "membership-before-apply") await RevokeAsync(Ct);
        var resume = new TenantOperations(new(), setup.Adapter, setup.Adapter, progress: async (stage, ct) =>
        {
            if (stage == ResourceCheckpoint.GatePrepared && fault != "membership-before-apply") await RevokeAsync(ct);
        });
        current = await RegistrationAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => resume.ResumeRecoveryAsync(Alpha.Id, previous, current.RegistryVersion, RecoveryApplier(), Ct));
        var blocked = await RegistrationAsync();
        Assert.True(blocked.SecurityRevalidationRequired);
        Assert.Equal(OperationStatus.Restoring, blocked.Operation);
        await using (var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
            Assert.True(await db.Audit.AnyAsync(x => x.OperationId == previous && x.Action == "RecoveryAclApplyStarted", Ct));
        await Assert.ThrowsAsync<TenantContractException>(() => service.ApproveRecoveryAsync(Alpha.Id, previous, blocked.RegistryVersion, Decision(AclDocument.Empty), Ct));
        using var successor = fault == "authority-after-apply" ? await UseSuccessorRecoveryOperatorAsync() : null;
        Assert.Equal(0, await RestartRecoveryCommandAsync(previous, replacement, blocked.RegistryVersion));
        current = await RegistrationAsync();
        Assert.Equal(replacement, current.OperationId);
        Assert.True(current.SecurityRevalidationRequired);
        Assert.False(TenantPolicy.IsOperational(current.Status(), DateTimeOffset.UtcNow));
        Assert.Equal(blocked.AuthorizationGeneration + 1, current.AuthorizationGeneration);
        Assert.True(current.Fence > blocked.Fence);
        await using (var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct))
        {
            var gate = await TenantWriteGate.ReadAsync(sql, null, Ct);
            Assert.False(gate.IsOpen); Assert.Equal(replacement, gate.OperationId); Assert.Equal(current.Fence, gate.Fence);
        }
        await Assert.ThrowsAsync<TenantContractException>(() => service.ResumeRecoveryAsync(Alpha.Id, previous, current.RegistryVersion, RecoveryApplier(), Ct));
        await Assert.ThrowsAsync<TenantContractException>(() => service.ResumeRecoveryAsync(Alpha.Id, replacement, current.RegistryVersion, RecoveryApplier(), Ct));
        await service.ApproveRecoveryAsync(Alpha.Id, replacement, current.RegistryVersion, Decision(AclDocument.Empty), Ct);
        current = await RegistrationAsync();
        var completed = await service.ResumeRecoveryAsync(Alpha.Id, replacement, current.RegistryVersion, RecoveryApplier(), Ct);
        Assert.Equal(ResourceCheckpoint.Completed, completed.Checkpoint);
        var ready = await RegistrationAsync();
        Assert.True(TenantPolicy.IsOperational(ready.Status(), DateTimeOffset.UtcNow));
        await using var access = AccessDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Empty(await access.Assignments.ToArrayAsync(Ct)); Assert.Empty(await access.Roles.ToArrayAsync(Ct));
        var marker = await access.State.SingleAsync(Ct);
        Assert.Equal(replacement, marker.AppliedOperationId); Assert.Equal(ready.AuthorizationGeneration, marker.SecurityGeneration);
        await using var audit = AuditDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Equal(1, await audit.Events.CountAsync(x => x.Id == replacement && x.Action == "AclRevalidated", Ct));
        await using var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var old = await platform.ResourceOperations.SingleAsync(x => x.Id == previous, Ct);
        Assert.NotNull(old.CompletedAt); Assert.NotEqual(ResourceCheckpoint.Completed, old.Checkpoint);
        Assert.Equal(original.AclJson, old.ApprovedAcl); Assert.Equal(original.AclHash, old.ApprovedHash);
        Assert.Equal("saas_platform_operator", old.ApprovedBy);
        Assert.True(await platform.Audit.AnyAsync(x => x.OperationId == previous && x.Action == "RecoverySuperseded", Ct));
        Assert.NotNull((await platform.IdentitySessions.SingleAsync(x => x.Id == setup.Session, Ct)).RevokedAt);
        if (fault.StartsWith("membership", StringComparison.Ordinal)) Assert.False((await platform.IdentityMemberships.SingleAsync(x => x.TenantId == Alpha.Id && x.GlobalUserId == user, Ct)).Active);
        if (fault.StartsWith("account", StringComparison.Ordinal)) Assert.False((await platform.IdentityAccounts.SingleAsync(x => x.Id == user, Ct)).Enabled);
        if (fault.StartsWith("authority", StringComparison.Ordinal))
        {
            Assert.False((await platform.RecoveryAuthorities.SingleAsync(x => x.Actor == "saas_platform_operator", Ct)).Enabled);
            Assert.NotEqual(old.ApprovedBy, (await platform.ResourceOperations.SingleAsync(x => x.Id == replacement, Ct)).ApprovedBy);
        }
    }

    [Fact]
    public async Task Recovery_restart_requires_current_authority_version_target_and_prior_worker_quiescence()
    {
        var setup = await RecoveryReadyAsync();
        var previous = Guid.NewGuid(); var replacement = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        await service.BeginRecoveryAsync(Alpha.Id, previous, setup.Tenant.RegistryVersion, Ct);
        var current = await RegistrationAsync();
        Assert.Equal(1, await RestartRecoveryCommandAsync(previous, replacement, current.RegistryVersion - 1));
        Assert.Equal(1, await RestartRecoveryCommandAsync(Guid.NewGuid(), replacement, current.RegistryVersion));
        Assert.Equal(1, await RestartRecoveryCommandAsync(previous, previous, current.RegistryVersion));
        await using (var lease = await OperationLease.AcquireAsync(new TenancyConnections().Platform("operator"), "tenant-" + Alpha.Id.ToString("N"), Ct))
            Assert.Equal(1, await RestartRecoveryCommandAsync(previous, replacement, current.RegistryVersion));
        await using (var worker = await SqlOperations.OpenAsync(TenancyConnections.ForOperation(Environment.CustomerAdmin(Alpha), previous, current.OwnerId!.Value), Ct))
            Assert.Equal(1, await RestartRecoveryCommandAsync(previous, replacement, current.RegistryVersion));
        await using (var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
        {
            await db.RecoveryAuthorities.Where(x => x.Actor == "saas_platform_operator").ExecuteUpdateAsync(set => set.SetProperty(x => x.Enabled, false), Ct);
            Assert.Equal(1, await RestartRecoveryCommandAsync(previous, replacement, current.RegistryVersion));
            Assert.False(await db.ResourceOperations.AnyAsync(x => x.Id == replacement, Ct));
        }
        Assert.Equal(current.RegistryVersion, (await RegistrationAsync()).RegistryVersion);
        using var successor = await UseSuccessorRecoveryOperatorAsync();
        Assert.Equal(0, await RestartRecoveryCommandAsync(previous, replacement, current.RegistryVersion));
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("delete")]
    [InlineData("expired")]
    public async Task Recovery_restart_never_overwrites_current_lifecycle(string state)
    {
        var setup = await RecoveryReadyAsync();
        var previous = Guid.NewGuid(); var replacement = Guid.NewGuid();
        await new TenantOperations(new(), setup.Adapter, setup.Adapter).BeginRecoveryAsync(Alpha.Id, previous, setup.Tenant.RegistryVersion, Ct);
        var current = await RegistrationAsync();
        await new IdentityOperations(new()).SetTenantAsync(Alpha.Id,
            state == "suspended" ? SubscriptionStatus.Suspended : state == "delete" ? SubscriptionStatus.PendingDeletion : SubscriptionStatus.Active,
            state == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1) : current.ExpiresAt, Ct);
        current = await RegistrationAsync();
        Assert.Equal(1, await RestartRecoveryCommandAsync(previous, replacement, current.RegistryVersion));
        var after = await RegistrationAsync();
        Assert.Equal(current.RegistryVersion, after.RegistryVersion); Assert.Equal(current.Subscription, after.Subscription);
        Assert.Equal(previous, after.OperationId); Assert.True(after.SecurityRevalidationRequired);
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        Assert.False(await db.ResourceOperations.AnyAsync(x => x.Id == replacement, Ct));
    }

    [Fact]
    public async Task Recovery_restart_customer_failure_keeps_new_generation_closed_and_resumable()
    {
        var setup = await RecoveryReadyAsync();
        var previous = Guid.NewGuid(); var replacement = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        await service.BeginRecoveryAsync(Alpha.Id, previous, setup.Tenant.RegistryVersion, Ct);
        var current = await RegistrationAsync();
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        var gate = await TenantWriteGate.ReadAsync(sql, null, Ct);
        // A DENY is not a reliable fault against the privileged migrator. Reject the
        // exact replacement at the SQL constraint boundary, and first prove the fault is armed.
        await SqlOperations.ExecuteAsync(sql, $"ALTER TABLE tenancy.WriteGate ADD CONSTRAINT CK_M3RestartGateFault CHECK (OperationId <> '{replacement:D}')", Ct);
        try
        {
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => TenantWriteGate.CloseAsync(
                new TenancyConnections().Customer(Alpha.Id, "migrator"), Alpha.Id, gate.Version, replacement, current.Fence + 1, 5000, Ct))).Number);
            Assert.Equal(1, await RestartRecoveryCommandAsync(previous, replacement, current.RegistryVersion));
            var unchanged = await TenantWriteGate.ReadAsync(sql, null, Ct);
            Assert.Equal(gate, unchanged);
        }
        finally { await SqlOperations.ExecuteAsync(sql, "ALTER TABLE tenancy.WriteGate DROP CONSTRAINT CK_M3RestartGateFault", Ct); }
        var interrupted = await RegistrationAsync();
        Assert.Equal(replacement, interrupted.OperationId); Assert.True(interrupted.SecurityRevalidationRequired);
        Assert.Equal(current.AuthorizationGeneration + 1, interrupted.AuthorizationGeneration);
        await service.ApproveRecoveryAsync(Alpha.Id, replacement, interrupted.RegistryVersion, Decision(AclDocument.Empty), Ct);
        current = await RegistrationAsync();
        Assert.Equal(ResourceCheckpoint.Completed, (await service.ResumeRecoveryAsync(Alpha.Id, replacement, current.RegistryVersion, RecoveryApplier(), Ct)).Checkpoint);
    }

    private async Task<int> RestartRecoveryCommandAsync(Guid previous, Guid replacement, long version)
    {
        var reason = System.Environment.GetEnvironmentVariable("SAAS_RECOVERY_REASON");
        try
        {
            System.Environment.SetEnvironmentVariable("SAAS_RECOVERY_REASON", "Supersede stale decision without restoring revoked rights.");
            return await MaintenanceCommand.RunAsync(["ops-recovery-restart", Alpha.Id.ToString("D"), previous.ToString("D"), replacement.ToString("D"), V(version)], Ct);
        }
        finally { System.Environment.SetEnvironmentVariable("SAAS_RECOVERY_REASON", reason); }
    }

    private async Task<IDisposable> UseSuccessorRecoveryOperatorAsync()
    {
        var actor = "saas_recovery_" + Guid.NewGuid().ToString("N");
        var credential = new SqlConnectionStringBuilder(Environment.PlatformAdmin) { UserID = actor, Pooling = false };
        credential[nameof(SqlConnectionStringBuilder.Password)] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) + "!aA1";
        var admin = new SqlConnectionStringBuilder(Environment.PlatformAdmin) { InitialCatalog = "master" };
        await new SqlProvisioner(admin.ConnectionString).GrantOperatorAsync(credential.ConnectionString, Ct);
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        db.RecoveryAuthorities.Add(new RecoveryAuthority { Actor = actor, Enabled = true, CanChangePlan = true,
            RegisteredAt = DateTimeOffset.UtcNow, RegisteredBy = "fixture-independent-administrator" });
        await db.SaveChangesAsync(Ct);
        return new RecoveryOperatorScope(actor, credential.ConnectionString);
    }
    private sealed class RecoveryOperatorScope : IDisposable
    {
        private readonly string? login = System.Environment.GetEnvironmentVariable("SAAS_PLATFORM_OPERATOR_LOGIN");
        private readonly string? connection = System.Environment.GetEnvironmentVariable("SAAS_PLATFORM_OPERATOR_CONNECTION");
        public RecoveryOperatorScope(string actor, string credential)
        {
            System.Environment.SetEnvironmentVariable("SAAS_PLATFORM_OPERATOR_LOGIN", actor);
            System.Environment.SetEnvironmentVariable("SAAS_PLATFORM_OPERATOR_CONNECTION", credential);
        }
        public void Dispose()
        {
            System.Environment.SetEnvironmentVariable("SAAS_PLATFORM_OPERATOR_LOGIN", login);
            System.Environment.SetEnvironmentVariable("SAAS_PLATFORM_OPERATOR_CONNECTION", connection);
        }
    }
}
