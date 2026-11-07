using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Maintenance;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M3RuntimeTests
{
    [Fact]
    public async Task Purge_is_target_scoped_preserves_global_accounts_and_leaves_an_irreversible_tombstone()
    {
        var tenant = await PurgeTargetAsync();
        var user = Guid.NewGuid();
        var beta = Environment.Tenants[1];
        await using (var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
        {
            db.IdentityAccounts.Add(new IdentityAccount { Id = user, CreatedAt = DateTimeOffset.UtcNow });
            db.IdentityMemberships.AddRange(new IdentityMembership { TenantId = tenant.Id, GlobalUserId = user, Active = true },
                new IdentityMembership { TenantId = beta.Id, GlobalUserId = user, Active = true });
            await db.SaveChangesAsync(Ct);
        }
        var adapter = new PlanFixture(Environment.CustomerAdmin(tenant));
        var operation = Guid.NewGuid();
        var service = new TenantOperations(new(), adapter, adapter);
        var before = await PurgeRegistrationAsync(tenant);
        var result = await service.PurgeAsync(tenant.Id, operation, before.RegistryVersion, Ct);
        Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
        await using var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var current = await platform.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.Id, Ct);
        Assert.NotNull(current.RetiredAt); Assert.NotNull(current.PurgedAt);
        Assert.Equal(SubscriptionStatus.PendingDeletion, current.Subscription);
        Assert.True(await platform.IdentityAccounts.AnyAsync(x => x.Id == user && x.Enabled, Ct));
        Assert.True(await platform.IdentityMemberships.AnyAsync(x => x.TenantId == beta.Id && x.GlobalUserId == user && x.Active, Ct));
        await using var server = await SqlOperations.OpenAsync(Environment.Admin, Ct);
        Assert.IsType<DBNull>(await SqlOperations.ScalarAsync(server, "SELECT DB_ID(@name)", Ct, ("@name", current.DatabaseName)));
        Assert.IsType<int>(await SqlOperations.ScalarAsync(server, "SELECT DB_ID(@name)", Ct, ("@name", TenancyConnections.TenantDatabase(beta.Id))));
        Assert.IsType<int>(await SqlOperations.ScalarAsync(server, "SELECT DB_ID('saas_platform')", Ct));
        Assert.Equal(0, await SqlOperations.ScalarAsync(server, "SELECT COUNT(*) FROM sys.server_principals WHERE name IN (@runtime,@migrator)", Ct,
            ("@runtime", "saas_rt_" + tenant.Id.ToString("N")), ("@migrator", "saas_mg_" + tenant.Id.ToString("N"))));
        Assert.Equal(result, await service.PurgeAsync(tenant.Id, operation, current.RegistryVersion, Ct));
        await Assert.ThrowsAsync<TenantContractException>(() => service.ChangePlanAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, "Approval", Ct));
        Assert.Equal(1, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(tenant), Ct));
        await Assert.ThrowsAsync<TenantContractException>(() => new IdentityOperations(new()).SetTenantAsync(tenant.Id, SubscriptionStatus.Active, current.ExpiresAt, Ct));
    }

    [Fact]
    public async Task Purge_requires_pending_deletion_and_the_same_exclusive_resource_lock()
    {
        var tenant = await PurgeTargetAsync(pending: false);
        var current = await PurgeRegistrationAsync(tenant);
        var adapter = new PlanFixture(Environment.CustomerAdmin(tenant));
        var service = new TenantOperations(new(), adapter, adapter);
        await Assert.ThrowsAsync<TenantContractException>(() => service.PurgeAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, Ct));
        await MarkPendingAsync(tenant);
        current = await PurgeRegistrationAsync(tenant);
        await using (var held = await OperationLease.AcquireAsync(new TenancyConnections().Platform("operator"), "tenant-" + tenant.Id.ToString("N"), Ct))
            Assert.Equal("operation_busy", (await Assert.ThrowsAsync<TenantContractException>(() => service.PurgeAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, Ct))).Code);
        Assert.Null((await PurgeRegistrationAsync(tenant)).RetiredAt);
    }

    [Fact]
    public async Task Purge_rejects_a_wrong_immutable_database_identity_without_deleting_any_database()
    {
        var tenant = await PurgeTargetAsync();
        await using (var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
            await db.Tenants.Where(x => x.Id == tenant.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.DatabaseNonce, Guid.NewGuid()), Ct);
        var current = await PurgeRegistrationAsync(tenant);
        var adapter = new PlanFixture(Environment.CustomerAdmin(tenant));
        await Assert.ThrowsAsync<TenantContractException>(() => new TenantOperations(new(), adapter, adapter).PurgeAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, Ct));
        await using var server = await SqlOperations.OpenAsync(Environment.Admin, Ct);
        Assert.IsType<int>(await SqlOperations.ScalarAsync(server, "SELECT DB_ID(@name)", Ct, ("@name", current.DatabaseName)));
        Assert.Null((await PurgeRegistrationAsync(tenant)).PurgedAt);
    }

    [Fact]
    public async Task Purge_requires_actual_previous_worker_quiescence_not_just_a_new_fence()
    {
        var tenant = await PurgeTargetAsync(pending: false);
        var current = await PurgeRegistrationAsync(tenant);
        var adapter = new PlanFixture(Environment.CustomerAdmin(tenant));
        var oldOperation = Guid.NewGuid();
        var interrupted = new TenantOperations(new(), adapter, adapter, progress: (stage, _) =>
            stage == ResourceCheckpoint.GateClosed ? throw new InvalidOperationException("stopped_checkpoint") : Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.ChangePlanAsync(tenant.Id, oldOperation, current.RegistryVersion, "Approval", Ct));
        current = await PurgeRegistrationAsync(tenant);
        await using (var oldWorker = await SqlOperations.OpenAsync(TenancyConnections.ForOperation(Environment.Migrator(tenant), oldOperation, current.OwnerId!.Value), Ct))
        {
            await MarkPendingAsync(tenant);
            current = await PurgeRegistrationAsync(tenant);
            Assert.Equal("prior_owner_still_active", (await Assert.ThrowsAsync<TenantContractException>(() =>
                new TenantOperations(new(), adapter, adapter).PurgeAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, Ct))).Code);
            Assert.Null((await PurgeRegistrationAsync(tenant)).RetiredAt);
        }
        current = await PurgeRegistrationAsync(tenant);
        var result = await new TenantOperations(new(), adapter, adapter).PurgeAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, Ct);
        Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
        current = await PurgeRegistrationAsync(tenant);
        await Assert.ThrowsAsync<TenantContractException>(() => new TenantOperations(new(), adapter, adapter).ResumeAsync(tenant.Id, oldOperation, current.RegistryVersion, Ct));
    }

    [Theory]
    [InlineData(ResourceCheckpoint.Claimed)]
    [InlineData(ResourceCheckpoint.GateClosed)]
    [InlineData(ResourceCheckpoint.SchemaReady)]
    [InlineData(ResourceCheckpoint.FeaturesReady)]
    public async Task Purge_retry_reconciles_durable_identity_verified_intent_and_absent_target(ResourceCheckpoint crash)
    {
        var tenant = await PurgeTargetAsync();
        var current = await PurgeRegistrationAsync(tenant);
        var operation = Guid.NewGuid();
        var adapter = new PlanFixture(Environment.CustomerAdmin(tenant));
        var interrupted = new TenantOperations(new(), adapter, adapter, progress: (stage, _) =>
            stage == crash ? throw new InvalidOperationException("purge_checkpoint_crash") : Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.PurgeAsync(tenant.Id, operation, current.RegistryVersion, Ct));
        var closed = await PurgeRegistrationAsync(tenant);
        Assert.NotNull(closed.RetiredAt); Assert.Null(closed.PurgedAt);
        Assert.Equal(OperationStatus.Purging, closed.Operation);
        var result = await new TenantOperations(new(), adapter, adapter).PurgeAsync(tenant.Id, operation, closed.RegistryVersion, Ct);
        Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
        var complete = await PurgeRegistrationAsync(tenant);
        Assert.NotNull(complete.PurgedAt);
        Assert.True(complete.Fence > closed.Fence);
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        Assert.Equal(1, await db.Audit.CountAsync(x => x.OperationId == operation && x.Action == "PurgeCompleted", Ct));
    }

    private async Task<TestTenant> PurgeTargetAsync(bool pending = true)
    {
        var tenant = Environment.NewTenant("purge-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(tenant), Ct));
        await using (var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
        {
            db.IdentityTenantSecurity.Add(new() { TenantId = tenant.Id });
            await db.SaveChangesAsync(Ct);
        }
        if (pending) await MarkPendingAsync(tenant);
        return tenant;
    }
    private async Task MarkPendingAsync(TestTenant tenant)
    {
        var current = await PurgeRegistrationAsync(tenant);
        await new IdentityOperations(new()).SetTenantAsync(tenant.Id, SubscriptionStatus.PendingDeletion, current.ExpiresAt, Ct);
    }
    private async Task<TenantRegistration> PurgeRegistrationAsync(TestTenant tenant)
    {
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        return await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.Id, Ct);
    }
}
