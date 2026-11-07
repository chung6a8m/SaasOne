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
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M3RuntimeTests
{
    private static readonly PermissionCatalog RecoveryCatalog = new([new("Access.Manage", "AccessControl", Delegable: false), new("Sample.Read", "AccessControl")]);

    [Fact]
    public async Task Recovery_claim_revokes_credentials_and_requires_current_external_full_acl_decision()
    {
        var setup = await RecoveryReadyAsync();
        var operation = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        var closed = await service.BeginRecoveryAsync(Alpha.Id, operation, setup.Tenant.RegistryVersion, Ct);
        var tenant = await RegistrationAsync();
        Assert.Equal(ResourceCheckpoint.GateClosed, closed.Checkpoint);
        Assert.Equal(OperationStatus.Restoring, tenant.Operation);
        Assert.True(tenant.SecurityRevalidationRequired);
        Assert.Equal(setup.Tenant.AuthorizationGeneration + 1, tenant.AuthorizationGeneration);
        await using (var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
        {
            Assert.NotNull((await platform.IdentitySessions.SingleAsync(x => x.Id == setup.Session, Ct)).RevokedAt);
            Assert.True((await platform.IdentityTenantSecurity.SingleAsync(x => x.TenantId == Alpha.Id, Ct)).Generation > setup.CredentialGeneration);
        }
        Assert.False(TenantPolicy.IsOperational(tenant.Status(), DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<TenantContractException>(() => service.ResumeRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, RecoveryApplier(), Ct));
        tenant = await RegistrationAsync();
        await service.ApproveRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, Decision(AclDocument.Empty), Ct);
        tenant = await RegistrationAsync();
        var result = await service.ResumeRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, RecoveryApplier(), Ct);
        Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
        tenant = await RegistrationAsync();
        Assert.False(tenant.SecurityRevalidationRequired);
        Assert.Equal(OperationStatus.None, tenant.Operation);
        await using var access = AccessDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Empty(await access.Assignments.ToArrayAsync(Ct));
        Assert.Empty(await access.Roles.ToArrayAsync(Ct));
        Assert.Equal(tenant.AuthorizationGeneration, (await access.State.SingleAsync(Ct)).SecurityGeneration);
        await using var audit = AuditDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Equal("AclRevalidated", (await audit.Events.SingleAsync(x => x.Id == operation, Ct)).Action);
    }

    [Fact]
    public async Task Recovery_authority_cannot_be_self_registered_or_derived_from_restored_roles()
    {
        var setup = await RecoveryReadyAsync();
        var operation = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        await service.BeginRecoveryAsync(Alpha.Id, operation, setup.Tenant.RegistryVersion, Ct);
        await using var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var authority = await platform.RecoveryAuthorities.SingleAsync(x => x.Actor == "saas_platform_operator", Ct);
        authority.RegisteredAt = DateTimeOffset.UtcNow.AddDays(1);
        await platform.SaveChangesAsync(Ct);
        var tenant = await RegistrationAsync();
        Assert.Equal("recovery_authority_required", (await Assert.ThrowsAsync<TenantContractException>(() =>
            service.ApproveRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, Decision(AclDocument.Empty), Ct))).Code);
        await using var sql = await SqlOperations.OpenAsync(new TenancyConnections().Platform("operator"), Ct);
        var error = await Assert.ThrowsAsync<SqlException>(() => SqlOperations.ExecuteAsync(sql,
            "UPDATE platform.RecoveryAuthorities SET Enabled = 1, RegisteredAt = '20000101' WHERE Actor = 'saas_platform_operator'", Ct));
        Assert.Equal(229, error.Number);
        Assert.True((await RegistrationAsync()).SecurityRevalidationRequired);
    }

    [Fact]
    public async Task Recovery_outstanding_obligations_require_approved_child_upgrade_under_parent_gate()
    {
        var setup = await RecoveryReadyAsync("PendingApproval");
        var operation = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        await service.BeginRecoveryAsync(Alpha.Id, operation, setup.Tenant.RegistryVersion, Ct);
        var tenant = await RegistrationAsync();
        await service.ApproveRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, Decision(AclDocument.Empty), Ct);
        tenant = await RegistrationAsync();
        await Assert.ThrowsAsync<TenantContractException>(() => service.ResumeRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, RecoveryApplier(), Ct));
        Assert.DoesNotContain("Features:Approval", setup.Adapter.Calls);
        tenant = await RegistrationAsync();
        await service.ApproveRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, Decision(AclDocument.Empty, upgrade: true), Ct);
        tenant = await RegistrationAsync();
        var checkedChild = false;
        var resume = new TenantOperations(new(), setup.Adapter, setup.Adapter, progress: async (stage, ct) =>
        {
            var current = await RegistrationAsync();
            Assert.Equal(operation, current.OperationId);
            Assert.Equal(OperationStatus.Restoring, current.Operation);
            Assert.True(current.SecurityRevalidationRequired);
            Assert.False(TenantPolicy.IsOperational(current.Status(), DateTimeOffset.UtcNow));
            if (stage == ResourceCheckpoint.FeaturesReady) checkedChild = true;
        });
        var result = await resume.ResumeRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, RecoveryApplier(), Ct);
        Assert.True(checkedChild);
        Assert.Equal("Approval", result.Plan);
        Assert.Contains("Migrate:Approval", setup.Adapter.Calls);
        Assert.Contains("Features:Approval", setup.Adapter.Calls);
        Assert.DoesNotContain("Features:Standard", setup.Adapter.Calls);
        Assert.Equal("PendingApproval", setup.Adapter.Obligation); // Recovery never confirms a business obligation.
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        Assert.Equal(1, await db.ResourceOperations.CountAsync(x => x.Id == operation && x.Kind == OperationStatus.Restoring, Ct));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("RevokeAuthority")]
    [InlineData("AuditFailure")]
    public async Task Recovery_unknown_reader_revoked_authority_or_audit_failure_keeps_both_gates_closed(string fault)
    {
        var setup = await RecoveryReadyAsync(fault == "Unknown" ? "Unknown" : "Clear");
        var operation = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        await service.BeginRecoveryAsync(Alpha.Id, operation, setup.Tenant.RegistryVersion, Ct);
        var tenant = await RegistrationAsync();
        await service.ApproveRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, Decision(AclDocument.Empty), Ct);
        if (fault == "RevokeAuthority")
        {
            await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
            await db.RecoveryAuthorities.ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, false), Ct);
        }
        if (fault == "AuditFailure")
        {
            await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
            await SqlOperations.ExecuteAsync(sql, "ALTER TABLE audit.Events ADD CONSTRAINT CK_M3RecoveryAuditFault CHECK (Action <> 'AclRevalidated')", Ct);
        }
        try
        {
            tenant = await RegistrationAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => service.ResumeRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, RecoveryApplier(), Ct));
            tenant = await RegistrationAsync();
            Assert.True(tenant.SecurityRevalidationRequired);
            Assert.False(TenantPolicy.IsOperational(tenant.Status(), DateTimeOffset.UtcNow));
            await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
            Assert.False((await TenantWriteGate.ReadAsync(sql, null, Ct)).IsOpen);
            await using var access = AccessDbContext.Open(Environment.CustomerAdmin(Alpha));
            Assert.Equal(setup.Tenant.AuthorizationGeneration, (await access.State.SingleAsync(Ct)).SecurityGeneration);
        }
        finally
        {
            if (fault == "AuditFailure")
            {
                await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
                await SqlOperations.ExecuteAsync(sql, "ALTER TABLE audit.Events DROP CONSTRAINT CK_M3RecoveryAuditFault", Ct);
            }
        }
    }

    [Theory]
    [InlineData(SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.PendingDeletion)]
    public async Task Recovery_final_open_never_overwrites_a_newer_lifecycle_decision(SubscriptionStatus state)
    {
        var setup = await RecoveryReadyAsync();
        var operation = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        await service.BeginRecoveryAsync(Alpha.Id, operation, setup.Tenant.RegistryVersion, Ct);
        var tenant = await RegistrationAsync();
        await service.ApproveRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, Decision(AclDocument.Empty), Ct);
        tenant = await RegistrationAsync();
        var resume = new TenantOperations(new(), setup.Adapter, setup.Adapter, progress: async (stage, ct) =>
        {
            if (stage == ResourceCheckpoint.GatePrepared)
                await new IdentityOperations(new()).SetTenantAsync(Alpha.Id, state, tenant.ExpiresAt, ct);
        });
        await Assert.ThrowsAsync<TenantContractException>(() => resume.ResumeRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, RecoveryApplier(), Ct));
        var current = await RegistrationAsync();
        Assert.Equal(state, current.Subscription);
        Assert.True(current.SecurityRevalidationRequired);
        Assert.NotEqual(OperationStatus.None, current.Operation);
    }

    [Theory]
    [InlineData(ResourceCheckpoint.ObligationsChecked)]
    [InlineData(ResourceCheckpoint.SchemaReady)]
    [InlineData(ResourceCheckpoint.FeaturesReady)]
    [InlineData(ResourceCheckpoint.EntitlementsCommitted)]
    [InlineData(ResourceCheckpoint.AclApplied)]
    [InlineData(ResourceCheckpoint.GatePrepared)]
    public async Task Recovery_crash_resume_rechecks_current_decision_and_never_reapplies_historical_acl(ResourceCheckpoint crash)
    {
        var setup = await RecoveryReadyAsync();
        var operation = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        await service.BeginRecoveryAsync(Alpha.Id, operation, setup.Tenant.RegistryVersion, Ct);
        var tenant = await RegistrationAsync();
        await service.ApproveRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, Decision(AclDocument.Empty), Ct);
        tenant = await RegistrationAsync();
        var fail = new TenantOperations(new(), setup.Adapter, setup.Adapter, progress: (stage, _) =>
            stage == crash ? throw new InvalidOperationException("injected_recovery_crash") : Task.CompletedTask);
        await Assert.ThrowsAnyAsync<Exception>(() => fail.ResumeRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, RecoveryApplier(), Ct));
        var closed = await RegistrationAsync();
        Assert.True(closed.SecurityRevalidationRequired);
        var result = await service.ResumeRecoveryAsync(Alpha.Id, operation, closed.RegistryVersion, RecoveryApplier(), Ct);
        Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
        var current = await RegistrationAsync();
        Assert.True(current.Fence > closed.Fence);
        await using var access = AccessDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Empty(await access.Roles.ToArrayAsync(Ct));
        await using var audit = AuditDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Equal(1, await audit.Events.CountAsync(x => x.Id == operation, Ct));
    }

    private static RecoveryDecision Decision(AclDocument document, bool upgrade = false) =>
        new(document.CanonicalJson(), document.Hash(), "Current explicit full replacement; no historical permissions approved.", upgrade);

    private RecoveryAclApplier RecoveryApplier() => new(RecoveryCatalog, async (tenant, users, ct) =>
    {
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        foreach (var user in users)
            if (!await db.IdentityMemberships.AnyAsync(x => x.TenantId == tenant && x.GlobalUserId == user && x.Active, ct))
                throw new AccessDeniedException(403, "current_membership_required");
    });

    private async Task<(TenantRegistration Tenant, PlanFixture Adapter, Guid Session, long CredentialGeneration)> RecoveryReadyAsync(string obligation = "Clear")
    {
        var tenant = await PrepareAsync("Standard");
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var authority = await db.RecoveryAuthorities.SingleOrDefaultAsync(x => x.Actor == "saas_platform_operator", Ct);
        if (authority is null) { authority = new RecoveryAuthority { Actor = "saas_platform_operator" }; db.RecoveryAuthorities.Add(authority); }
        authority.Enabled = true; authority.CanChangePlan = true; authority.RegisteredAt = DateTimeOffset.UtcNow.AddDays(-1); authority.RegisteredBy = "fixture-administrator";
        var user = Guid.NewGuid();
        db.IdentityAccounts.Add(new IdentityAccount { Id = user, CreatedAt = DateTimeOffset.UtcNow });
        db.IdentityMemberships.Add(new IdentityMembership { TenantId = Alpha.Id, GlobalUserId = user, Active = true });
        var session = Guid.NewGuid();
        db.IdentitySessions.Add(new IdentitySession { Id = session, GlobalUserId = user, TenantId = Alpha.Id, Subject = "fixture-subject", NativeSecurityStamp = "fixture-stamp", AccountGeneration = 1,
            CreatedAt = DateTimeOffset.UtcNow, IdleExpiresAt = DateTimeOffset.UtcNow.AddHours(1), AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddHours(8) });
        await db.SaveChangesAsync(Ct);
        var credential = (await db.IdentityTenantSecurity.SingleAsync(x => x.TenantId == Alpha.Id, Ct)).Generation;
        await using var access = AccessDbContext.Open(Environment.CustomerAdmin(Alpha));
        await access.Database.MigrateAsync(Ct);
        await using var audit = AuditDbContext.Open(Environment.CustomerAdmin(Alpha));
        await audit.Database.MigrateAsync(Ct);
        await access.Assignments.ExecuteDeleteAsync(Ct); await access.RoleGrants.ExecuteDeleteAsync(Ct); await access.UserGrants.ExecuteDeleteAsync(Ct);
        await access.Roles.ExecuteDeleteAsync(Ct); await access.State.ExecuteDeleteAsync(Ct); await access.History.ExecuteDeleteAsync(Ct); await audit.Events.ExecuteDeleteAsync(Ct);
        var role = Guid.NewGuid();
        var old = new AclDocument([new(role, "Historical administrator", [new("Access.Manage", PermissionEffect.Allow), new("Sample.Read", PermissionEffect.Allow)])], [new(user, role)], []);
        access.State.Add(new AclState { TenantId = Alpha.Id, Revision = 1, SecurityGeneration = tenant.AuthorizationGeneration, AppliedOperationId = Guid.NewGuid(), AclHash = old.Hash() });
        access.Roles.Add(new RoleRow { Id = role, Name = old.Roles[0].Name });
        foreach (var grant in old.Roles[0].Permissions) access.RoleGrants.Add(new RoleGrantRow { RoleId = role, Permission = grant.Permission, Effect = grant.Effect });
        access.Assignments.Add(new UserRoleRow { UserId = user, RoleId = role });
        await access.SaveChangesAsync(Ct);
        return (tenant, new PlanFixture(Environment.CustomerAdmin(Alpha)) { Obligation = obligation }, session, credential);
    }
}
