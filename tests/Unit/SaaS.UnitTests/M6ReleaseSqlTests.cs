using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using SaaS.Web.Release;
using Xunit;
namespace SaaS.UnitTests;

[CollectionDefinition("M6 SQL", DisableParallelization = true)]
public sealed class M6SqlCollection : ICollectionFixture<M1SqlFixture> { }

[Collection("M6 SQL")]
[Trait("Category", "SqlM6")]
public sealed class M6ReleaseSqlTests(M1SqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private M1SqlEnvironment Environment => fixture.Environment;
    private TestTenant Alpha => Environment.Tenants[0];
    private async Task<TenantRegistration> CurrentAsync(TestTenant tenant)
    {
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        return await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.Id, Ct);
    }
    internal static Task<ReleasePackage> PackageAsync() => ReleasePackage.OpenAsync(
        TenancyConnections.Required("SAAS_M6_RELEASE_PACKAGE"), TenancyConnections.Required("SAAS_RELEASE_MANIFEST_SHA256"), Ct);

    [Fact]
    public async Task Release_runs_bundles_and_verifies_real_Orchard_and_all_EF_streams()
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync();
        await using var package = await PackageAsync();
        var current = await CurrentAsync(Alpha); var operation = Guid.NewGuid();
        var service = new ReleaseMaintenance(new(), package);
        var result = await service.ApplyAsync(Alpha.Id, operation, current.RegistryVersion, false, Ct);
        Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
        await service.VerifyAsync(Alpha.Id, Ct);
        Assert.Equal(result, await service.ApplyAsync(Alpha.Id, operation, result.RegistryVersion, true, Ct));
        Assert.True(TenantPolicy.IsOperational((await CurrentAsync(Alpha)).Status(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Failed_tenant_stays_closed_other_tenant_releases_and_checkpoint_resume_is_idempotent()
    {
        foreach (var fault in new[] { ResourceCheckpoint.Claimed, ResourceCheckpoint.GateClosed,
            ResourceCheckpoint.ObligationsChecked, ResourceCheckpoint.SchemaReady, ResourceCheckpoint.FeaturesReady,
            ResourceCheckpoint.EntitlementsCommitted, ResourceCheckpoint.GatePrepared })
        {
            await using var setup = await new M4HttpTests(fixture).HttpAsync();
            await using var package = await PackageAsync();
            var before = await CurrentAsync(Alpha); var operation = Guid.NewGuid();
            var failing = new ReleaseMaintenance(new(), package, (stage, _) =>
                stage == fault ? throw new IOException("M6 injected checkpoint failure") : Task.CompletedTask);
            await Assert.ThrowsAsync<IOException>(() => failing.ApplyAsync(Alpha.Id, operation, before.RegistryVersion, false, Ct));
            var closed = await CurrentAsync(Alpha);
            Assert.Equal(OperationStatus.Migrating, closed.Operation);
            Assert.False(TenantPolicy.IsOperational(closed.Status(), DateTimeOffset.UtcNow));
            var normal = new ReleaseMaintenance(new(), package);
            var beta = Environment.Tenants[1]; var other = await CurrentAsync(beta);
            Assert.Equal(ResourceCheckpoint.Completed, (await normal.ApplyAsync(beta.Id, Guid.NewGuid(), other.RegistryVersion, false, Ct)).Checkpoint);
            await Assert.ThrowsAsync<TenantContractException>(() => normal.ApplyAsync(Alpha.Id, operation, before.RegistryVersion, true, Ct));
            var result = await normal.ApplyAsync(Alpha.Id, operation, closed.RegistryVersion, true, Ct);
            Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
            Assert.Equal(result, await normal.ApplyAsync(Alpha.Id, operation, result.RegistryVersion, true, Ct));
        }
    }

    [Fact]
    public async Task Release_completion_cannot_overwrite_suspend_delete_or_expiry()
    {
        foreach (var state in new[] { SubscriptionStatus.Suspended, SubscriptionStatus.PendingDeletion, SubscriptionStatus.Active })
        {
            await using var setup = await new M4HttpTests(fixture).HttpAsync();
            await using var package = await PackageAsync();
            var current = await CurrentAsync(Alpha); var operation = Guid.NewGuid();
            var expiry = state == SubscriptionStatus.Active ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddYears(1);
            var service = new ReleaseMaintenance(new(), package, async (stage, token) =>
            {
                if (stage != ResourceCheckpoint.GatePrepared) return;
                var latest = await CurrentAsync(Alpha);
                await new IdentityOperations(new()).SetTenantAsync(Alpha.Id, state, expiry, token, latest.RegistryVersion);
            });
            try
            {
                await Assert.ThrowsAsync<TenantContractException>(() => service.ApplyAsync(Alpha.Id, operation, current.RegistryVersion, false, Ct));
                var after = await CurrentAsync(Alpha);
                Assert.Equal(state, after.Subscription); Assert.Equal(expiry, after.ExpiresAt);
                Assert.Equal(OperationStatus.Migrating, after.Operation);
                Assert.False(TenantPolicy.IsOperational(after.Status(), DateTimeOffset.UtcNow));
                if (state == SubscriptionStatus.PendingDeletion)
                {
                    // Production lifecycle must not resurrect a pending-deletion tenant.
                    var denied = await Assert.ThrowsAsync<TenantContractException>(() =>
                        new IdentityOperations(new()).SetTenantAsync(Alpha.Id, SubscriptionStatus.Active,
                            current.ExpiresAt, Ct, after.RegistryVersion));
                    Assert.Equal("deletion_recovery_requires_separate_operation", denied.Code);
                }
            }
            finally
            {
                if (state == SubscriptionStatus.PendingDeletion)
                {
                    // Disposable SQL fixture cleanup only. Its HTTP teardown restores the
                    // pre-test lifecycle through the production API, which correctly refuses
                    // PendingDeletion -> Active. Revert only this fixture's lifecycle fields
                    // using the same privileged test-admin boundary as M4HttpTests.HttpAsync.
                    await using var reset = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
                    var row = await reset.Tenants.SingleAsync(x => x.Id == Alpha.Id, Ct);
                    if (row.Subscription == SubscriptionStatus.PendingDeletion)
                    {
                        row.Subscription = current.Subscription;
                        row.ExpiresAt = current.ExpiresAt;
                        row.RegistryVersion = checked(row.RegistryVersion + 1);
                        await reset.SaveChangesAsync(Ct);
                    }
                }
            }
        }
    }
}
