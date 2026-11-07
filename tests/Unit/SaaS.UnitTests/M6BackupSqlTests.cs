using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.AccessControl.Data;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M6 SQL")]
[Trait("Category", "SqlM6")]
public sealed partial class M6BackupSqlTests(M1SqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private M1SqlEnvironment Environment => fixture.Environment;
    private TestTenant Alpha => Environment.Tenants[0];
    private async Task<TenantRegistration> CurrentAsync(TestTenant? tenant = null)
    {
        var id = (tenant ?? Alpha).Id;
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        return await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }
    private async Task AuthorityAsync()
    {
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var authority = await db.RecoveryAuthorities.SingleOrDefaultAsync(x => x.Actor == "saas_platform_operator", Ct);
        if (authority is null) { authority = new RecoveryAuthority { Actor = "saas_platform_operator" }; db.RecoveryAuthorities.Add(authority); }
        authority.Enabled = true; authority.CanChangePlan = true; authority.RegisteredAt = DateTimeOffset.UtcNow.AddDays(-1);
        authority.RegisteredBy = "disposable-M6-fixture";
        await db.SaveChangesAsync(Ct);
    }
    private async Task<BackupResult> BackupAsync(SqlBackupStore store)
    {
        var current = await CurrentAsync();
        return await new AccessMaintenance(new()).Operations.BackupAsync(Alpha.Id, Guid.NewGuid(), current.RegistryVersion, store, false, Ct);
    }

    [Fact]
    public async Task Authenticated_Alpha_backup_cannot_overwrite_Delta_or_change_its_registry()
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync();
        using var store = new SqlBackupStore(); var backup = await BackupAsync(store);
        var delta = Environment.Tenants.Single(x => x.Slug == "delta"); var before = await CurrentAsync(delta);
        await Assert.ThrowsAsync<TenantContractException>(() => new AccessMaintenance(new()).Operations.RestoreBackupAsync(
            delta.Id, Guid.NewGuid(), before.RegistryVersion, backup.BackupId, store, false, Ct));
        var after = await CurrentAsync(delta);
        Assert.Equal(before.RegistryVersion, after.RegistryVersion); Assert.Equal(before.AuthorizationGeneration, after.AuthorizationGeneration);
        await TenantDatabaseVerifier.VerifyIdentityAsync(after, Environment.Migrator(delta), Ct);
        Assert.True(TenantPolicy.IsOperational(after.Status(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Real_restore_keeps_platform_current_requires_new_ACL_and_leaves_other_tenants_operational()
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync(); await AuthorityAsync();
        using var store = new SqlBackupStore(); var backup = await BackupAsync(store);
        await new M4HttpTests(fixture).SeedAsync(Alpha, AclDocument.Empty);
        var before = await CurrentAsync(); var operation = Guid.NewGuid();
        var beta = await CurrentAsync(Environment.Tenants[1]);
        var maintenance = new AccessMaintenance(new());
        var restored = await maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation, before.RegistryVersion, backup.BackupId, store, false, Ct);
        var gated = await CurrentAsync();
        Assert.Equal(OperationStatus.Restoring, gated.Operation); Assert.True(gated.SecurityRevalidationRequired);
        Assert.True(gated.AuthorizationGeneration > before.AuthorizationGeneration);
        Assert.Equal(beta.RegistryVersion, (await CurrentAsync(Environment.Tenants[1])).RegistryVersion);
        Assert.True(TenantPolicy.IsOperational((await CurrentAsync(Environment.Tenants[1])).Status(), DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<TenantContractException>(() => maintenance.ResumeRecoveryAsync(Alpha.Id, operation, restored.RegistryVersion, Ct));
        var approved = await maintenance.ApproveAsync(Alpha.Id, operation, (await CurrentAsync()).RegistryVersion,
            AclDocument.Empty, "Current authority removes historical access; membership remains active.", false, Ct);
        var result = await maintenance.ResumeRecoveryAsync(Alpha.Id, operation, approved.RegistryVersion, Ct);
        Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
        await using var access = AccessDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Empty(await access.Roles.ToArrayAsync(Ct)); Assert.Empty(await access.Assignments.ToArrayAsync(Ct));
        Assert.Empty(await access.RoleGrants.ToArrayAsync(Ct)); Assert.Empty(await access.UserGrants.ToArrayAsync(Ct));
        await using var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        Assert.True(await platform.IdentityMemberships.AnyAsync(x => x.TenantId == Alpha.Id && x.GlobalUserId == setup.Http.UserId && x.Active, Ct));
    }

    [Fact]
    public async Task Pending_physical_restore_cannot_be_skipped_by_generic_resume_restart_or_plan_change()
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync(); await AuthorityAsync();
        using var store = new SqlBackupStore(); var backup = await BackupAsync(store);
        var operation = Guid.NewGuid(); var maintenance = new AccessMaintenance(new()); var before = await CurrentAsync();
        await Assert.ThrowsAsync<IOException>(() => maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation,
            before.RegistryVersion, backup.BackupId, store, false, Ct, (stage, _) =>
                stage == PhysicalRestoreCheckpoint.SourceVerified ? throw new IOException("M6 physical checkpoint fault") : Task.CompletedTask));
        var closed = await CurrentAsync();
        foreach (var attempt in new Func<Task>[]
        {
            () => maintenance.ResumeRecoveryAsync(Alpha.Id, operation, closed.RegistryVersion, Ct),
            () => maintenance.Operations.RestartRecoveryAsync(Alpha.Id, operation, Guid.NewGuid(), closed.RegistryVersion, "Cannot bypass physical restore", Ct),
            () => maintenance.Operations.ChangePlanAsync(Alpha.Id, Guid.NewGuid(), closed.RegistryVersion, "Approval", Ct)
        })
            Assert.Equal("physical_restore_incomplete", (await Assert.ThrowsAsync<TenantContractException>(attempt)).Code);
        var restored = await maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation, closed.RegistryVersion, backup.BackupId, store, true, Ct);
        var approved = await maintenance.ApproveAsync(Alpha.Id, operation, restored.RegistryVersion, AclDocument.Empty, "Explicit current replacement", false, Ct);
        Assert.Equal(ResourceCheckpoint.Completed, (await maintenance.ResumeRecoveryAsync(Alpha.Id, operation, approved.RegistryVersion, Ct)).Checkpoint);
    }
}
