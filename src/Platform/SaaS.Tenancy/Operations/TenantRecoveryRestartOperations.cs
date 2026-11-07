using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
namespace SaaS.Tenancy.Operations;

public sealed partial class TenantOperations
{
    /// <summary>Supersede a blocked recovery without changing its decision or restoring revoked authority.</summary>
    public async Task<ResourceResult> RestartRecoveryAsync(Guid tenantId, Guid previousOperationId, Guid operationId,
        long expectedVersion, string reason, CancellationToken ct)
    {
        ValidateRecoveryRequest(tenantId, operationId, expectedVersion);
        if (previousOperationId == Guid.Empty || previousOperationId == operationId
            || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw new TenantContractException("invalid_operation_request");
        var server = new SqlProvisioner(connections.Provisioner());
        await server.AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await VerifyPlatformAsync(db, lease, ct);
        var expected = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new TenantContractException("tenant_missing");
        var priorOwner = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == previousOperationId, ct)
            ?? throw new TenantContractException("operation_missing");
        DemandRestartableRecovery(expected, priorOwner, expectedVersion);
        // Neither an expired lease nor a failed checkpoint proves that a worker stopped.
        await server.AssertPriorOwnerStoppedAsync(previousOperationId, priorOwner.OwnerId, priorOwner.OwnerHost, lease.HostId, ct);
        await lease.AssertHeldAsync(ct);
        if (await AuthenticateAsync(lease, ct) != actor) throw new TenantContractException("operator_not_authorized");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == tenantId, ct);
        var previous = await db.ResourceOperations.SingleAsync(x => x.Id == previousOperationId, ct);
        DemandRestartableRecovery(tenant, previous, expectedVersion);
        if (previous.OwnerId != priorOwner.OwnerId || previous.OwnerHost != priorOwner.OwnerHost || previous.Fence != priorOwner.Fence
            || await db.ResourceOperations.AnyAsync(x => x.Id == operationId, ct))
            throw new TenantContractException("operation_reconciliation_required");
        var now = Now;
        var operation = new ResourceOperation
        {
            Id = operationId, TenantId = tenantId, Kind = OperationStatus.Restoring,
            SourcePlan = tenant.Plan, RequestedPlan = tenant.Plan, SelectedPlan = tenant.Plan,
            RequestedBy = actor, StartedAt = now, OwnerId = Guid.NewGuid(), OwnerHost = lease.HostId,
            AuthorizationGeneration = checked(tenant.AuthorizationGeneration + 1),
            Fence = checked(tenant.Fence + 1), ExpectedRegistryVersion = checked(tenant.RegistryVersion + 1)
        };
        // This is a new recovery, so a newly and independently registered replacement authority
        // may act. The revoked old approver need not be re-enabled. No approval is inherited.
        await DemandRecoveryAuthorityAsync(db, operation, actor, false, ct);
        tenant.OperationId = operation.Id;
        tenant.OwnerId = operation.OwnerId;
        tenant.AuthorizationGeneration = operation.AuthorizationGeneration;
        tenant.Fence = operation.Fence;
        tenant.RegistryVersion = operation.ExpectedRegistryVersion;
        // Restoring/security-required and the current subscription/expiry stay untouched.
        // A terminal timestamp with a non-Completed checkpoint records supersession, not success.
        // The previous ACL document/hash/approver, checkpoint and generation remain immutable.
        previous.CompletedAt = now;
        db.ResourceOperations.Add(operation);
        var security = await db.IdentityTenantSecurity.SingleAsync(x => x.TenantId == tenantId, ct);
        security.Generation = checked(security.Generation + 1);
        await db.IdentityFamilies.Where(x => x.TenantId == tenantId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
        await db.IdentitySessions.Where(x => x.TenantId == tenantId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
        var superseded = RecoveryAudit(actor, tenant, previous, "RecoverySuperseded");
        superseded.Details = JsonSerializer.Serialize(new
        {
            replacementOperationId = operation.Id, reason, previous.OwnerId, previous.Fence,
            previous.AuthorizationGeneration, previous.Checkpoint, previous.ApprovedBy, previous.ApprovedAt, previous.ApprovedHash
        });
        var started = RecoveryAudit(actor, tenant, operation, "RecoveryRestarted");
        started.Details = JsonSerializer.Serialize(new
        {
            previousOperationId, reason, operation.OwnerId, operation.Fence, operation.AuthorizationGeneration, operation.SourcePlan
        });
        db.Audit.AddRange(superseded, started);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        // A crash here leaves the replacement discoverable in ops-list and traffic denied by
        // the platform. Its normal approve/resume path re-closes the gate under a fresh fence.
        var work = RecoveryWork(lease, actor, tenant, operation);
        try { await CloseAsync(work, ct); return Result(work.Operation); }
        catch (Exception error) { await RecordFailureAsync(work, error, ct); throw; }
    }

    private void DemandRestartableRecovery(TenantRegistration tenant, ResourceOperation operation, long version)
    {
        TenantDatabaseVerifier.ValidateRegistration(tenant);
        DemandRecovery(tenant, operation, version);
        if (tenant.Subscription != SubscriptionStatus.Active || tenant.ExpiresAt <= Now
            || tenant.OwnerId != operation.OwnerId || tenant.Fence != operation.Fence)
            throw new TenantContractException("operation_reconciliation_required");
    }
}
