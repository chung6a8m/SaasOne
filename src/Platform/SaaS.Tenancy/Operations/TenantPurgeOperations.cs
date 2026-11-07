using System.Data;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
namespace SaaS.Tenancy.Operations;

public sealed partial class TenantOperations
{
    /// <summary>Irreversible private operation. No database or connection target is accepted from a caller.</summary>
    public async Task<ResourceResult> PurgeAsync(Guid tenantId, Guid operationId, long expectedVersion, CancellationToken ct)
    {
        ValidateRecoveryRequest(tenantId, operationId, expectedVersion);
        var server = new SqlProvisioner(connections.Provisioner());
        await server.AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await VerifyPlatformAsync(db, lease, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct) ?? throw new TenantContractException("tenant_missing");
        TenantDatabaseVerifier.ValidateRegistration(tenant);
        var operation = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct);
        if (tenant.RegistryVersion != expectedVersion || tenant.Subscription != SubscriptionStatus.PendingDeletion
            || operation is not null && (operation.TenantId != tenantId || operation.Kind != OperationStatus.Purging || operation.RequestedBy != actor))
            throw new TenantContractException("operation_reconciliation_required");
        if (tenant.PurgedAt is not null)
        {
            if (operation?.Checkpoint == ResourceCheckpoint.Completed && tenant.RetiredAt is not null) return Result(operation);
            throw new TenantContractException("tenant_retired");
        }
        if (operation is not null && (tenant.OperationId != operationId || tenant.Operation != OperationStatus.Purging))
            throw new TenantContractException("operation_reconciliation_required");
        if (tenant.RetiredAt is not null && operation is null) throw new TenantContractException("tenant_retired");
        if (tenant.OperationId is { } previousId && tenant.OwnerId is { } previousOwner)
        {
            var previous = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == previousId, ct);
            var host = previous?.OwnerHost ?? (await db.Operations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == previousId, ct))?.OwnerHost;
            await server.AssertPriorOwnerStoppedAsync(previousId, previousOwner, host, lease.HostId, ct);
        }
        var work = await ClaimPurgeAsync(lease, actor, tenant, operation, operationId, ct);
        try
        {
            await NotifyAsync(ResourceCheckpoint.Claimed, ct);
            var exists = await PurgeTargetExistsAsync(work.Tenant.DatabaseName, ct);
            if (exists)
            {
                await DemandPurgeAsync(work, ct);
                await TenantDatabaseVerifier.VerifyIdentityAsync(work.Tenant, work.Customer, ct);
                WriteGateState gate;
                await using (var sql = await SqlOperations.OpenAsync(work.Customer, ct)) gate = await TenantWriteGate.ReadAsync(sql, null, ct);
                var closed = await TenantWriteGate.CloseAsync(work.Customer, tenantId, gate.Version, operationId, work.Operation.Fence, drainTimeoutMilliseconds, ct);
                await MovePurgeAsync(work, ResourceCheckpoint.GateClosed, "PurgeGateClosed", closed.Version, ct);
                await TenantDatabaseVerifier.VerifyIdentityAsync(work.Tenant, work.Customer, ct);
                // Durable proof distinguishes our own completed DROP from a missing/unidentified target.
                await MovePurgeAsync(work, ResourceCheckpoint.SchemaReady, "PurgeTargetVerified", null, ct);
            }
            else
            {
                await using var proof = TenancyConnections.PlatformContext(lease.Connection);
                if (!await proof.Audit.AnyAsync(x => x.TenantId == tenantId && x.OperationId == operationId && x.Action == "PurgeTargetVerified", ct))
                    throw new TenantContractException("purge_target_missing_without_intent");
            }
            await DemandPurgeAsync(work, ct);
            await DeletePurgeTargetAsync(work.Tenant, ct); // Rechecks immutable identity inside the destructive SQL batch.
            await MovePurgeAsync(work, ResourceCheckpoint.FeaturesReady, "PurgeResourcesDeleted", null, ct);
            return await CompletePurgeAsync(work, ct);
        }
        catch (Exception error) { await RecordFailureAsync(work, error, ct); throw; }
    }

    private async Task<Work> ClaimPurgeAsync(OperationLease lease, string actor, TenantRegistration expected, ResourceOperation? existing, Guid id, CancellationToken ct)
    {
        await lease.AssertHeldAsync(ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == expected.Id, ct);
        if (tenant.RegistryVersion != expected.RegistryVersion || tenant.Subscription != SubscriptionStatus.PendingDeletion
            || tenant.PurgedAt is not null || tenant.OperationId != expected.OperationId || tenant.Operation != expected.Operation)
            throw new TenantContractException("operation_reconciliation_required");
        ResourceOperation operation;
        if (existing is null)
        {
            if (tenant.RetiredAt is not null) throw new TenantContractException("tenant_retired");
            tenant.RetiredAt = Now;
            tenant.AuthorizationGeneration = checked(tenant.AuthorizationGeneration + 1);
            tenant.SecurityRevalidationRequired = true;
            var security = await db.IdentityTenantSecurity.SingleOrDefaultAsync(x => x.TenantId == tenant.Id, ct);
            if (security is not null) security.Generation = checked(security.Generation + 1);
            var now = Now;
            await db.IdentityFamilies.Where(x => x.TenantId == tenant.Id && x.RevokedAt == null).ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, now), ct);
            await db.IdentitySessions.Where(x => x.TenantId == tenant.Id && x.RevokedAt == null).ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, now), ct);
            await db.IdentityClients.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.Enabled, false), ct);
            await db.IdentityMemberships.Where(x => x.TenantId == tenant.Id && x.Active)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Active, false).SetProperty(x => x.Generation, x => x.Generation + 1), ct);
            operation = new ResourceOperation { Id = id, TenantId = tenant.Id, Kind = OperationStatus.Purging,
                SourcePlan = tenant.Plan, RequestedPlan = tenant.Plan, SelectedPlan = tenant.Plan, RequestedBy = actor, StartedAt = Now,
                AuthorizationGeneration = tenant.AuthorizationGeneration };
            db.ResourceOperations.Add(operation);
        }
        else operation = await db.ResourceOperations.SingleAsync(x => x.Id == id, ct);
        tenant.Operation = OperationStatus.Purging;
        tenant.OperationId = id; tenant.OwnerId = Guid.NewGuid(); tenant.Fence = checked(tenant.Fence + 1); tenant.RegistryVersion = checked(tenant.RegistryVersion + 1);
        operation.OwnerId = tenant.OwnerId.Value; operation.OwnerHost = lease.HostId; operation.Fence = tenant.Fence; operation.ExpectedRegistryVersion = tenant.RegistryVersion;
        db.Audit.Add(Audit(actor, tenant, operation, existing is null ? "PurgeClaimed" : "PurgeResumed"));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return RecoveryWork(lease, actor, tenant, operation);
    }
    private async Task MovePurgeAsync(Work work, ResourceCheckpoint checkpoint, string action, long? gateVersion, CancellationToken ct)
    {
        await DemandPurgeAsync(work, ct);
        await using var db = TenancyConnections.PlatformContext(work.Lease.Connection);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == work.Tenant.Id, ct);
        var operation = await db.ResourceOperations.SingleAsync(x => x.Id == work.Operation.Id, ct);
        DemandPurge(tenant, operation, work.Operation.Stamp());
        tenant.RegistryVersion = checked(tenant.RegistryVersion + 1);
        operation.ExpectedRegistryVersion = tenant.RegistryVersion;
        operation.Checkpoint = checkpoint;
        if (gateVersion.HasValue) operation.GateVersion = gateVersion.Value;
        db.Audit.Add(Audit(work.Actor, tenant, operation, action));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        work.Tenant = tenant; work.Operation = operation;
        await NotifyAsync(checkpoint, ct);
    }
    private async Task<ResourceResult> CompletePurgeAsync(Work work, CancellationToken ct)
    {
        await DemandPurgeAsync(work, ct);
        if (await PurgeTargetExistsAsync(work.Tenant.DatabaseName, ct)) throw new TenantContractException("purge_resources_still_present");
        await using var db = TenancyConnections.PlatformContext(work.Lease.Connection);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == work.Tenant.Id, ct);
        var operation = await db.ResourceOperations.SingleAsync(x => x.Id == work.Operation.Id, ct);
        DemandPurge(tenant, operation, work.Operation.Stamp());
        if (operation.Checkpoint != ResourceCheckpoint.FeaturesReady) throw new TenantContractException("operation_reconciliation_required");
        tenant.PurgedAt = Now;
        tenant.Operation = OperationStatus.None; tenant.OperationId = null; tenant.OwnerId = null;
        tenant.RegistryVersion = checked(tenant.RegistryVersion + 1);
        operation.ExpectedRegistryVersion = tenant.RegistryVersion; operation.CompletedAt = Now; operation.Checkpoint = ResourceCheckpoint.Completed;
        db.Audit.Add(Audit(work.Actor, tenant, operation, "PurgeCompleted"));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result(operation);
    }
    private async Task DemandPurgeAsync(Work work, CancellationToken ct)
    {
        await work.Lease.AssertHeldAsync(ct);
        if (await AuthenticateAsync(work.Lease, ct) != work.Actor) throw new TenantContractException("operator_not_authorized");
        await using var db = TenancyConnections.PlatformContext(work.Lease.Connection);
        DemandPurge(await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == work.Tenant.Id, ct),
            await db.ResourceOperations.AsNoTracking().SingleAsync(x => x.Id == work.Operation.Id, ct), work.Operation.Stamp());
    }
    private static void DemandPurge(TenantRegistration tenant, ResourceOperation operation, OperationStamp stamp)
    {
        OperationPolicy.DemandOwner(tenant.Status(), stamp, OperationStatus.Purging);
        if (tenant.Subscription != SubscriptionStatus.PendingDeletion || tenant.RetiredAt is null || tenant.PurgedAt is not null
            || operation.Kind != OperationStatus.Purging || operation.TenantId != tenant.Id || operation.AuthorizationGeneration != tenant.AuthorizationGeneration
            || operation.OwnerId != stamp.OwnerId || operation.Fence != stamp.Fence || operation.ExpectedRegistryVersion != stamp.RegistryVersion || operation.CompletedAt is not null)
            throw new TenantContractException("operation_reconciliation_required");
    }
    private async Task<bool> PurgeTargetExistsAsync(string name, CancellationToken ct)
    {
        await using var sql = await SqlOperations.OpenAsync(connections.Provisioner(), ct);
        return await SqlOperations.ScalarAsync(sql, "SELECT DB_ID(@database)", ct, ("@database", name)) is int;
    }
    private async Task DeletePurgeTargetAsync(TenantRegistration tenant, CancellationToken ct)
    {
        TenantDatabaseVerifier.ValidateRegistration(tenant);
        await using var sql = await SqlOperations.OpenAsync(connections.Provisioner(), ct);
        // Parameters select ONLY the already validated registry target. QUOTENAME is for SQL identifiers, never interpolation.
        await SqlOperations.ExecuteAsync(sql, """
            IF DB_ID(@database) IS NOT NULL
            BEGIN
                DECLARE @verified bit = 0;
                DECLARE @check nvarchar(max) = N'SELECT @ok = CASE WHEN COUNT(*) = 1 AND MAX(CASE WHEN [Id] = 1 AND [TenantId] = @tenant AND [DatabaseNonce] = @nonce THEN 1 ELSE 0 END) = 1 THEN 1 ELSE 0 END FROM '
                    + QUOTENAME(@database) + N'.[tenancy].[Identity]';
                EXEC sys.sp_executesql @check, N'@tenant uniqueidentifier, @nonce uniqueidentifier, @ok bit OUTPUT', @tenant, @nonce, @verified OUTPUT;
                IF @verified <> 1 THROW 51000, 'Purge target identity mismatch', 1;
                DECLARE @drop nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@database) + N';';
                EXEC sys.sp_executesql @drop;
            END
            IF SUSER_ID(@runtime) IS NOT NULL
            BEGIN
                DECLARE @runtimeDrop nvarchar(max) = N'DROP LOGIN ' + QUOTENAME(@runtime);
                EXEC sys.sp_executesql @runtimeDrop;
            END
            IF SUSER_ID(@migrator) IS NOT NULL
            BEGIN
                DECLARE @migratorDrop nvarchar(max) = N'DROP LOGIN ' + QUOTENAME(@migrator);
                EXEC sys.sp_executesql @migratorDrop;
            END
            """, ct, ("@database", tenant.DatabaseName), ("@tenant", tenant.Id), ("@nonce", tenant.DatabaseNonce),
            ("@runtime", "saas_rt_" + tenant.Id.ToString("N")), ("@migrator", "saas_mg_" + tenant.Id.ToString("N")));
    }
}
