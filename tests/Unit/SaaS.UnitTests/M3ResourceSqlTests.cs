using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM3")]
public sealed partial class M3RuntimeTests(M1SqlFixture fixture) : IClassFixture<M1SqlFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private M1SqlEnvironment Environment => fixture.Environment;
    private TestTenant Alpha => Environment.Tenants[0];

    [Fact]
    public async Task Plan_upgrade_drains_then_migrates_before_features_and_opens_only_after_reconciliation()
    {
        var tenant = await PrepareAsync("Standard");
        var adapter = new PlanFixture(Environment.CustomerAdmin(Alpha));
        var operation = Guid.NewGuid();
        var result = await new TenantOperations(new(), adapter, adapter).ChangePlanAsync(Alpha.Id, operation, tenant.RegistryVersion, "Approval", Ct);
        Assert.Equal("Approval", result.Plan);
        Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
        Assert.Equal(["Read", "Migrate:Approval", "Features:Approval", "Verify:Approval"], adapter.Calls);
        var current = await RegistrationAsync();
        Assert.Equal(OperationStatus.None, current.Operation);
        Assert.True(current.RegistryVersion > tenant.RegistryVersion);
        Assert.True(TenantPolicy.IsOperational(current.Status(), DateTimeOffset.UtcNow));
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        Assert.True((await TenantWriteGate.ReadAsync(sql, null, Ct)).IsOpen);
    }

    [Theory]
    [InlineData("PendingApproval")]
    [InlineData("Rejected")]
    [InlineData("DraftRework")]
    public async Task Outstanding_obligation_rejects_downgrade_without_toggling_features(string state)
    {
        var tenant = await PrepareAsync("Approval");
        var adapter = new PlanFixture(Environment.CustomerAdmin(Alpha)) { Obligation = state };
        var result = await new TenantOperations(new(), adapter, adapter).ChangePlanAsync(Alpha.Id, Guid.NewGuid(), tenant.RegistryVersion, "Standard", Ct);
        Assert.Equal("Approval", result.Plan);
        Assert.DoesNotContain("Features:Standard", adapter.Calls);
        Assert.Equal(OperationStatus.None, (await RegistrationAsync()).Operation);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.PendingDeletion)]
    public async Task Lifecycle_decision_immediately_before_open_is_never_overwritten(SubscriptionStatus subscription)
    {
        var tenant = await PrepareAsync("Standard");
        var adapter = new PlanFixture(Environment.CustomerAdmin(Alpha));
        var service = new TenantOperations(new(), adapter, adapter, progress: async (stage, ct) =>
        {
            if (stage == ResourceCheckpoint.GatePrepared)
                await new IdentityOperations(new()).SetTenantAsync(Alpha.Id, subscription, tenant.ExpiresAt, ct);
        });
        await Assert.ThrowsAsync<TenantContractException>(() => service.ChangePlanAsync(Alpha.Id, Guid.NewGuid(), tenant.RegistryVersion, "Approval", Ct));
        var current = await RegistrationAsync();
        Assert.Equal(subscription, current.Subscription);
        Assert.NotEqual(OperationStatus.None, current.Operation);
        Assert.False(TenantPolicy.IsOperational(current.Status(), DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(ResourceCheckpoint.GateClosed)]
    [InlineData(ResourceCheckpoint.ObligationsChecked)]
    [InlineData(ResourceCheckpoint.SchemaReady)]
    [InlineData(ResourceCheckpoint.FeaturesReady)]
    [InlineData(ResourceCheckpoint.EntitlementsCommitted)]
    [InlineData(ResourceCheckpoint.GatePrepared)]
    public async Task Crash_at_checkpoint_keeps_closed_and_explicit_retry_reconciles_under_new_fence(ResourceCheckpoint crash)
    {
        var tenant = await PrepareAsync("Standard");
        var adapter = new PlanFixture(Environment.CustomerAdmin(Alpha));
        var operation = Guid.NewGuid();
        var service = new TenantOperations(new(), adapter, adapter, progress: (stage, _) => stage == crash ? throw new InvalidOperationException("injected_crash") : Task.CompletedTask);
        await Assert.ThrowsAnyAsync<Exception>(() => service.ChangePlanAsync(Alpha.Id, operation, tenant.RegistryVersion, "Approval", Ct));
        var closed = await RegistrationAsync();
        Assert.NotEqual(OperationStatus.None, closed.Operation);
        Assert.False(TenantPolicy.IsOperational(closed.Status(), DateTimeOffset.UtcNow));
        var result = await new TenantOperations(new(), adapter, adapter).ResumeAsync(Alpha.Id, operation, closed.RegistryVersion, Ct);
        var complete = await RegistrationAsync();
        Assert.True(complete.Fence > closed.Fence);
        Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
        Assert.Equal("Approval", complete.Plan);
        Assert.Equal(OperationStatus.None, complete.Operation);
    }

    [Fact]
    public async Task Resource_lock_is_shared_with_provision_and_unknown_obligations_keep_closed()
    {
        var tenant = await PrepareAsync("Approval");
        var adapter = new PlanFixture(Environment.CustomerAdmin(Alpha)) { Obligation = "Unknown" };
        var service = new TenantOperations(new(), adapter, adapter);
        await using (var held = await OperationLease.AcquireAsync(new TenancyConnections().Platform("operator"), "tenant-" + Alpha.Id.ToString("N"), Ct))
            Assert.Equal("operation_busy", (await Assert.ThrowsAsync<TenantContractException>(() => service.ChangePlanAsync(Alpha.Id, Guid.NewGuid(), tenant.RegistryVersion, "Standard", Ct))).Code);
        await Assert.ThrowsAsync<TenantContractException>(() => service.ChangePlanAsync(Alpha.Id, Guid.NewGuid(), tenant.RegistryVersion, "Standard", Ct));
        var closed = await RegistrationAsync();
        Assert.Equal("Approval", closed.Plan);
        Assert.NotEqual(OperationStatus.None, closed.Operation);
        Assert.Equal(["Read"], adapter.Calls);
    }

    private async Task<TenantRegistration> RegistrationAsync()
    {
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        return await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == Alpha.Id, Ct);
    }
    private async Task<TenantRegistration> PrepareAsync(string plan)
    {
        // Fixture-only reset between sequential cases. Never an application recovery path.
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == Alpha.Id, Ct);
        tenant.Plan = plan; tenant.Subscription = SubscriptionStatus.Active; tenant.ExpiresAt = DateTimeOffset.UtcNow.AddYears(1);
        tenant.Operation = OperationStatus.None; tenant.OperationId = null; tenant.OwnerId = null; tenant.SecurityRevalidationRequired = false;
        tenant.RegistryVersion++; tenant.Fence++;
        if (!await db.IdentityTenantSecurity.AnyAsync(x => x.TenantId == tenant.Id, Ct)) db.IdentityTenantSecurity.Add(new() { TenantId = tenant.Id });
        await db.SaveChangesAsync(Ct);
        await using var customer = TenancyConnections.CustomerContext(Environment.CustomerAdmin(Alpha));
        var gate = await customer.WriteGate.SingleAsync(Ct);
        gate.IsOpen = true; gate.OperationId = Guid.NewGuid(); gate.Fence = tenant.Fence; gate.Version++;
        await customer.SaveChangesAsync(Ct);
        return tenant;
    }
    private sealed class PlanFixture(string connection) : IPlanReconciler, IApprovalObligationReader
    {
        public string Obligation { get; init; } = "Clear";
        public List<string> Calls { get; } = [];
        private async Task ClosedAsync(CancellationToken ct)
        {
            await using var sql = await SqlOperations.OpenAsync(connection, ct);
            Assert.False((await TenantWriteGate.ReadAsync(sql, null, ct)).IsOpen);
        }
        public async Task<ObligationResult> ReadAsync(TenantContext tenant, System.Data.Common.DbConnection sql, CancellationToken ct)
        {
            await ClosedAsync(ct); Calls.Add("Read");
            return Obligation switch { "Clear" => new(ObligationState.Clear, 0), "Unknown" => new(ObligationState.Unknown, 0), _ => new(ObligationState.Outstanding, 1) };
        }
        public async Task MigrateAsync(TenantContext tenant, string target, System.Data.Common.DbConnection ownedConnection, CancellationToken ct) { await ClosedAsync(ct); Assert.StartsWith("SaaS.Op.", new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ownedConnection.ConnectionString).ApplicationName, StringComparison.Ordinal); Calls.Add("Migrate:" + target); }
        public async Task ApplyFeaturesAsync(TenantContext tenant, string target, System.Data.Common.DbConnection ownedConnection, CancellationToken ct) { await ClosedAsync(ct); Assert.Contains("Migrate:" + target, Calls); Calls.Add("Features:" + target); }
        public async Task<bool> VerifyAsync(TenantContext tenant, string target, System.Data.Common.DbConnection ownedConnection, CancellationToken ct) { await ClosedAsync(ct); Calls.Add("Verify:" + target); return true; }
    }
}
