using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
namespace SaaS.Tenancy.Operations;

public enum ResourceCheckpoint { Claimed, GateClosed, ObligationsChecked, SchemaReady, FeaturesReady, EntitlementsCommitted, AclApplied, GatePrepared, Completed }
public sealed record ResourceResult(Guid OperationId, Guid TenantId, string Plan, long RegistryVersion, ResourceCheckpoint Checkpoint);

/// <summary>
/// Private SQL-authenticated resource operations. The same-host process lock and SQL session lock
/// remain held across every awaited resource step; cancellation never detaches a still-running step.
/// Platform transactions are short, so suspension/deletion decisions do not wait for the resource lock.
/// </summary>
public sealed partial class TenantOperations(TenancyConnections connections, IPlanReconciler reconciler, IApprovalObligationReader obligations,
    TimeProvider? clock = null, Func<ResourceCheckpoint, CancellationToken, Task>? progress = null, int drainTimeoutMilliseconds = 30000)
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public Task<ResourceResult> ChangePlanAsync(Guid tenant, Guid operation, long expectedVersion, string target, CancellationToken ct)
        => ExecuteAsync(tenant, operation, expectedVersion, target, resume: false, ct);
    public Task<ResourceResult> ResumeAsync(Guid tenant, Guid operation, long expectedVersion, CancellationToken ct)
        => ExecuteAsync(tenant, operation, expectedVersion, null, resume: true, ct);

    private async Task<ResourceResult> ExecuteAsync(Guid tenantId, Guid operationId, long expectedVersion, string? target, bool resume, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || operationId == Guid.Empty || expectedVersion < 1 || drainTimeoutMilliseconds is < 0 or > 30000
            || !resume && target is not ("Standard" or "Approval")) throw new TenantContractException("invalid_operation_request");
        var server = new SqlProvisioner(connections.Provisioner());
        await server.AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await VerifyPlatformAsync(db, lease, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new TenantContractException("tenant_missing");
        TenantDatabaseVerifier.ValidateRegistration(tenant);
        if (tenant.RetiredAt is not null || tenant.PurgedAt is not null) throw new TenantContractException("tenant_retired");
        var existing = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct);
        if (existing is not null && (existing.TenantId != tenantId || existing.RequestedBy != actor
            || !resume && existing.RequestedPlan != target)) throw new TenantContractException("operation_request_conflict");
        if (existing?.Checkpoint == ResourceCheckpoint.Completed) return Result(existing);
        if (tenant.RegistryVersion != expectedVersion) throw new TenantContractException("operation_reconciliation_required");
        if (resume)
        {
            if (existing is null || existing.Kind != OperationStatus.PlanChanging || tenant.OperationId != operationId || tenant.Operation != existing.Kind)
                throw new TenantContractException("operation_request_conflict");
            await server.AssertPriorOwnerStoppedAsync(operationId, existing.OwnerId, existing.OwnerHost, lease.HostId, ct);
        }
        else if (existing is not null || !TenantPolicy.IsOperational(tenant.Status(), Now))
            throw new TenantContractException("operation_request_conflict");

        var work = await ClaimAsync(lease, actor, tenant, existing, operationId, target, ct);
        try
        {
            await NotifyAsync(ResourceCheckpoint.Claimed, ct);
            await CloseAsync(work, ct);
            var context = new TenantContext(tenantId, work.Tenant.Slug, work.Tenant.Slug);
            await DemandClosedAsync(work, ct);
            await using var customer = await SqlOperations.OpenAsync(work.Customer, ct);
            var read = await obligations.ReadAsync(context, customer, ct);
            var selected = OperationPolicy.SelectPlan(work.Operation.SourcePlan, work.Operation.RequestedPlan, read);
            await MoveAsync(work, ResourceCheckpoint.ObligationsChecked, (_, operation) => operation.SelectedPlan = selected, ct);
            var rejected = selected != work.Operation.RequestedPlan;
            if (!rejected)
            {
                await DemandClosedAsync(work, ct);
                await reconciler.MigrateAsync(context, selected, customer, ct);
                await MoveAsync(work, ResourceCheckpoint.SchemaReady, null, ct);
                await DemandClosedAsync(work, ct);
                await reconciler.ApplyFeaturesAsync(context, selected, customer, ct);
                await MoveAsync(work, ResourceCheckpoint.FeaturesReady, null, ct);
            }
            await DemandClosedAsync(work, ct);
            if (!await reconciler.VerifyAsync(context, selected, customer, ct)) throw new TenantContractException("plan_not_ready");
            await MoveAsync(work, ResourceCheckpoint.EntitlementsCommitted, (current, _) =>
            {
                if (rejected && current.Plan != selected) throw new TenantContractException("operation_reconciliation_required");
                current.Plan = selected;
            }, ct);
            await PrepareOpenAsync(work, ct);
            return await CompleteAsync(work, ct);
        }
        catch (Exception error)
        {
            await RecordFailureAsync(work, error, ct);
            if (error is DbUpdateConcurrencyException) throw new TenantContractException("operation_reconciliation_required");
            throw;
        }
    }

    private async Task<Work> ClaimAsync(OperationLease lease, string actor, TenantRegistration expected, ResourceOperation? existing,
        Guid operationId, string? target, CancellationToken ct)
    {
        await lease.AssertHeldAsync(ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == expected.Id, ct);
        if (tenant.RegistryVersion != expected.RegistryVersion || tenant.RetiredAt is not null
            || tenant.OperationId != expected.OperationId || tenant.Operation != expected.Operation)
            throw new TenantContractException("operation_reconciliation_required");
        var operation = existing is null ? new ResourceOperation
        {
            Id = operationId, TenantId = tenant.Id, Kind = OperationStatus.PlanChanging,
            SourcePlan = tenant.Plan, RequestedPlan = target!, SelectedPlan = tenant.Plan,
            AuthorizationGeneration = tenant.AuthorizationGeneration, RequestedBy = actor, StartedAt = Now
        } : await db.ResourceOperations.SingleAsync(x => x.Id == operationId, ct);
        if (existing is null) db.ResourceOperations.Add(operation);
        if (operation.AuthorizationGeneration != tenant.AuthorizationGeneration || operation.CompletedAt is not null)
            throw new TenantContractException("operation_reconciliation_required");
        tenant.RegistryVersion = checked(tenant.RegistryVersion + 1);
        tenant.Fence = checked(tenant.Fence + 1);
        tenant.Operation = operation.Kind;
        tenant.OperationId = operation.Id;
        tenant.OwnerId = Guid.NewGuid();
        operation.OwnerId = tenant.OwnerId.Value;
        operation.OwnerHost = lease.HostId;
        operation.Fence = tenant.Fence;
        operation.ExpectedRegistryVersion = tenant.RegistryVersion;
        db.Audit.Add(Audit(actor, tenant, operation, existing is null ? "ResourceClaimed" : "ResourceResumed"));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(lease, actor, tenant, operation, TenancyConnections.ForOperation(connections.Customer(tenant.Id, "migrator"), operation.Id, operation.OwnerId));
    }

    private async Task CloseAsync(Work work, CancellationToken ct)
    {
        await DemandOwnedAsync(work, ct);
        await TenantDatabaseVerifier.VerifyIdentityAsync(work.Tenant, work.Customer, ct);
        WriteGateState gate;
        await using (var sql = await SqlOperations.OpenAsync(work.Customer, ct)) gate = await TenantWriteGate.ReadAsync(sql, null, ct);
        var closed = await TenantWriteGate.CloseAsync(work.Customer, work.Tenant.Id, gate.Version, work.Operation.Id, work.Operation.Fence, drainTimeoutMilliseconds, ct);
        await MoveAsync(work, ResourceCheckpoint.GateClosed, (_, operation) => operation.GateVersion = closed.Version, ct);
    }
    private async Task PrepareOpenAsync(Work work, CancellationToken ct)
    {
        await DemandClosedAsync(work, ct);
        await TenantDatabaseVerifier.VerifyIdentityAsync(work.Tenant, work.Customer, ct);
        var opened = await TenantWriteGate.PrepareOpenAsync(work.Customer, work.Tenant.Id, work.Operation.GateVersion, work.Operation.Id, work.Operation.Fence, ct);
        await MoveAsync(work, ResourceCheckpoint.GatePrepared, (_, operation) => operation.GateVersion = opened.Version, ct);
    }
    private async Task MoveAsync(Work work, ResourceCheckpoint stage, Action<TenantRegistration, ResourceOperation>? change, CancellationToken ct)
    {
        await work.Lease.AssertHeldAsync(ct);
        if (await AuthenticateAsync(work.Lease, ct) != work.Actor) throw new TenantContractException("operator_not_authorized");
        await using var db = TenancyConnections.PlatformContext(work.Lease.Connection);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == work.Tenant.Id, ct);
        var operation = await db.ResourceOperations.SingleAsync(x => x.Id == work.Operation.Id, ct);
        DemandOwned(tenant, operation, work.Operation.Stamp());
        change?.Invoke(tenant, operation);
        operation.Checkpoint = stage;
        tenant.RegistryVersion = checked(tenant.RegistryVersion + 1);
        operation.ExpectedRegistryVersion = tenant.RegistryVersion;
        db.Audit.Add(Audit(work.Actor, tenant, operation, "ResourceCheckpoint"));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        work.Tenant = tenant; work.Operation = operation;
        await NotifyAsync(stage, ct);
    }
    private async Task<ResourceResult> CompleteAsync(Work work, CancellationToken ct)
    {
        await DemandOwnedAsync(work, ct);
        await using (var customer = await SqlOperations.OpenAsync(work.Customer, ct))
        {
            var gate = await TenantWriteGate.ReadAsync(customer, null, ct);
            if (!gate.IsOpen || gate.TenantId != work.Tenant.Id || gate.OperationId != work.Operation.Id
                || gate.Fence != work.Operation.Fence || gate.Version != work.Operation.GateVersion)
                throw new TenantContractException("write_gate_changed");
        }
        await using var db = TenancyConnections.PlatformContext(work.Lease.Connection);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == work.Tenant.Id, ct);
        var operation = await db.ResourceOperations.SingleAsync(x => x.Id == work.Operation.Id, ct);
        DemandOwned(tenant, operation, work.Operation.Stamp());
        var completed = OperationPolicy.Complete(tenant.Status(), operation.Stamp(), operation.Kind, Now,
            operation.Checkpoint == ResourceCheckpoint.GatePrepared && tenant.Plan == operation.SelectedPlan);
        tenant.Operation = completed.Operation; tenant.OperationId = null; tenant.OwnerId = null;
        tenant.RegistryVersion = completed.RegistryVersion;
        operation.ExpectedRegistryVersion = completed.RegistryVersion;
        operation.Checkpoint = ResourceCheckpoint.Completed;
        operation.CompletedAt = Now;
        db.Audit.Add(Audit(work.Actor, tenant, operation, "ResourceCompleted"));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        work.Tenant = tenant; work.Operation = operation;
        return Result(operation);
    }
    private async Task DemandOwnedAsync(Work work, CancellationToken ct)
    {
        await work.Lease.AssertHeldAsync(ct);
        if (await AuthenticateAsync(work.Lease, ct) != work.Actor) throw new TenantContractException("operator_not_authorized");
        await using var db = TenancyConnections.PlatformContext(work.Lease.Connection);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == work.Tenant.Id, ct);
        var operation = await db.ResourceOperations.AsNoTracking().SingleAsync(x => x.Id == work.Operation.Id, ct);
        DemandOwned(tenant, operation, work.Operation.Stamp());
    }
    private static void DemandOwned(TenantRegistration tenant, ResourceOperation operation, OperationStamp stamp)
    {
        OperationPolicy.DemandOwner(tenant.Status(), stamp, operation.Kind);
        if (tenant.RetiredAt is not null || tenant.PurgedAt is not null || operation.TenantId != tenant.Id
            || operation.OwnerId != stamp.OwnerId || operation.Fence != stamp.Fence || operation.ExpectedRegistryVersion != stamp.RegistryVersion
            || operation.CompletedAt is not null || operation.AuthorizationGeneration != tenant.AuthorizationGeneration)
            throw new TenantContractException("operation_reconciliation_required");
    }
    private async Task DemandClosedAsync(Work work, CancellationToken ct)
    {
        await DemandOwnedAsync(work, ct);
        await using var sql = await SqlOperations.OpenAsync(work.Customer, ct);
        var gate = await TenantWriteGate.ReadAsync(sql, null, ct);
        if (gate.IsOpen || gate.TenantId != work.Tenant.Id || gate.OperationId != work.Operation.Id
            || gate.Fence != work.Operation.Fence || gate.Version != work.Operation.GateVersion)
            throw new TenantContractException("write_gate_changed");
    }
    private static async Task<string> AuthenticateAsync(OperationLease lease, CancellationToken ct) =>
        await SqlOperations.ScalarAsync(lease.Connection, """
            SELECT CASE WHEN DB_NAME() = 'saas_platform' AND IS_ROLEMEMBER('saas_operator') = 1
                AND IS_SRVROLEMEMBER('sysadmin') = 0 THEN ORIGINAL_LOGIN() ELSE NULL END
            """, ct) as string ?? throw new TenantContractException("operator_not_authorized");
    private static async Task VerifyPlatformAsync(PlatformDbContext db, OperationLease lease, CancellationToken ct)
    {
        await TenancyConnections.VerifySchemaAsync(db, ct);
        var bootstrap = await db.Bootstrap.AsNoTracking().SingleOrDefaultAsync(ct);
        if (bootstrap is not { Completed: true, BaselineVersion: 1 } || bootstrap.OperationHost != lease.HostId)
            throw new TenantContractException("platform_not_ready");
    }
    private Task NotifyAsync(ResourceCheckpoint stage, CancellationToken ct) => progress?.Invoke(stage, ct) ?? Task.CompletedTask;
    private static ResourceResult Result(ResourceOperation operation) => new(operation.Id, operation.TenantId, operation.SelectedPlan, operation.ExpectedRegistryVersion, operation.Checkpoint);
    private OperationalAudit Audit(string actor, TenantRegistration tenant, ResourceOperation operation, string action) => new()
    {
        Actor = actor, TenantId = tenant.Id, OperationId = operation.Id, RegistryVersion = tenant.RegistryVersion, Action = action, OccurredAt = Now,
        Details = JsonSerializer.Serialize(new { operation.Kind, operation.SourcePlan, operation.RequestedPlan, operation.SelectedPlan, operation.Checkpoint,
            operation.OwnerId, operation.Fence, operation.AuthorizationGeneration, operation.GateVersion })
    };
    private async Task RecordFailureAsync(Work work, Exception error, CancellationToken ct)
    {
        try
        {
            await using var db = TenancyConnections.PlatformContext(connections.Platform("operator"));
            var entry = Audit(work.Actor, work.Tenant, work.Operation, "ResourceBlocked");
            entry.Details = JsonSerializer.Serialize(new { code = error is TenantContractException contract ? contract.Code : "operation_dependency_failed", work.Operation.Checkpoint });
            db.Audit.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch { /* Preserve the original failure; never release a durable gate or report success. */ }
    }
    private sealed class Work(OperationLease lease, string actor, TenantRegistration tenant, ResourceOperation operation, string customer)
    {
        internal OperationLease Lease { get; } = lease;
        internal string Actor { get; } = actor;
        internal TenantRegistration Tenant { get; set; } = tenant;
        internal ResourceOperation Operation { get; set; } = operation;
        internal string Customer { get; } = customer;
    }
}
