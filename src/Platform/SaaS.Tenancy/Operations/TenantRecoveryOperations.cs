using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
namespace SaaS.Tenancy.Operations;

public sealed record RecoveryDecision(string AclJson, string AclHash, string Reason, bool AllowApprovalUpgrade = false);

/// <summary>M3 rehearses security reconciliation only. SQL backup/restore belongs to the M6 operator workflow.</summary>
public sealed partial class TenantOperations
{
    public async Task<ResourceResult> BeginRecoveryAsync(Guid tenantId, Guid operationId, long expectedVersion, CancellationToken ct)
    {
        ValidateRecoveryRequest(tenantId, operationId, expectedVersion);
        var server = new SqlProvisioner(connections.Provisioner());
        await server.AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await VerifyPlatformAsync(db, lease, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var tenant = await db.Tenants.SingleOrDefaultAsync(x => x.Id == tenantId, ct) ?? throw new TenantContractException("tenant_missing");
        TenantDatabaseVerifier.ValidateRegistration(tenant);
        if (tenant.RegistryVersion != expectedVersion || tenant.RetiredAt is not null || tenant.PurgedAt is not null
            || !TenantPolicy.IsOperational(tenant.Status(), Now) || await db.ResourceOperations.AnyAsync(x => x.Id == operationId, ct))
            throw new TenantContractException("operation_reconciliation_required");
        tenant.AuthorizationGeneration = checked(tenant.AuthorizationGeneration + 1);
        tenant.RegistryVersion = checked(tenant.RegistryVersion + 1);
        tenant.Fence = checked(tenant.Fence + 1);
        tenant.Operation = OperationStatus.Restoring;
        tenant.OperationId = operationId;
        tenant.OwnerId = Guid.NewGuid();
        tenant.SecurityRevalidationRequired = true;
        var operation = new ResourceOperation
        {
            Id = operationId, TenantId = tenantId, Kind = OperationStatus.Restoring, SourcePlan = tenant.Plan,
            RequestedPlan = tenant.Plan, SelectedPlan = tenant.Plan, RequestedBy = actor, StartedAt = Now,
            AuthorizationGeneration = tenant.AuthorizationGeneration, OwnerId = tenant.OwnerId.Value, OwnerHost = lease.HostId,
            Fence = tenant.Fence, ExpectedRegistryVersion = tenant.RegistryVersion
        };
        db.ResourceOperations.Add(operation);
        // The generation and revocation live outside the restored customer database.
        var security = await db.IdentityTenantSecurity.SingleAsync(x => x.TenantId == tenantId, ct);
        security.Generation = checked(security.Generation + 1);
        var now = Now;
        await db.IdentityFamilies.Where(x => x.TenantId == tenantId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
        await db.IdentitySessions.Where(x => x.TenantId == tenantId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
        db.Audit.Add(RecoveryAudit(actor, tenant, operation, "RecoveryStarted"));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var work = RecoveryWork(lease, actor, tenant, operation);
        try { await CloseAsync(work, ct); return Result(work.Operation); }
        catch (Exception error) { await RecordFailureAsync(work, error, ct); throw; }
    }

    public async Task<ResourceResult> ApproveRecoveryAsync(Guid tenantId, Guid operationId, long expectedVersion, RecoveryDecision decision, CancellationToken ct)
    {
        ValidateRecoveryRequest(tenantId, operationId, expectedVersion);
        if (string.IsNullOrWhiteSpace(decision.AclJson) || decision.AclJson.Length > 16 * 1024 * 1024
            || string.IsNullOrWhiteSpace(decision.Reason) || decision.Reason.Length > 1000
            || Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(decision.AclJson))) != decision.AclHash)
            throw new TenantContractException("recovery_decision_invalid");
        var server = new SqlProvisioner(connections.Provisioner());
        await server.AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await VerifyPlatformAsync(db, lease, ct);
        var previous = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct)
            ?? throw new TenantContractException("operation_missing");
        await server.AssertPriorOwnerStoppedAsync(operationId, previous.OwnerId, previous.OwnerHost, lease.HostId, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == tenantId, ct);
        var operation = await db.ResourceOperations.SingleAsync(x => x.Id == operationId, ct);
        DemandRecovery(tenant, operation, expectedVersion);
        await DemandRecoveryAuthorityAsync(db, operation, actor, decision.AllowApprovalUpgrade, ct);
        // Once replacement could have committed, its document is immutable. A new decision needs a new operation/generation.
        if ((operation.Checkpoint >= ResourceCheckpoint.AclApplied
            || await db.Audit.AnyAsync(x => x.TenantId == tenantId && x.OperationId == operationId && x.Action == "RecoveryAclApplyStarted", ct))
            && (operation.ApprovedHash != decision.AclHash || operation.ApprovedAcl != decision.AclJson || operation.ApprovedBy != actor
                || operation.AllowApprovalUpgrade != decision.AllowApprovalUpgrade || operation.ApprovalReason != decision.Reason))
            throw new TenantContractException("recovery_decision_already_applied");
        operation.ApprovedAcl = decision.AclJson;
        operation.ApprovedHash = decision.AclHash;
        operation.ApprovedBy = actor;
        operation.ApprovedAt = Now;
        operation.ApprovalReason = decision.Reason;
        operation.AllowApprovalUpgrade = decision.AllowApprovalUpgrade;
        tenant.RegistryVersion = checked(tenant.RegistryVersion + 1);
        operation.ExpectedRegistryVersion = tenant.RegistryVersion;
        db.Audit.Add(RecoveryAudit(actor, tenant, operation, "RecoveryExplicitlyApproved"));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result(operation);
    }

    public async Task<ResourceResult> ResumeRecoveryAsync(Guid tenantId, Guid operationId, long expectedVersion, IRecoveryAclApplier applier, CancellationToken ct)
    {
        ValidateRecoveryRequest(tenantId, operationId, expectedVersion);
        var server = new SqlProvisioner(connections.Provisioner());
        await server.AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await VerifyPlatformAsync(db, lease, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct) ?? throw new TenantContractException("tenant_missing");
        var operation = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct) ?? throw new TenantContractException("operation_missing");
        DemandRecovery(tenant, operation, expectedVersion);
        await DemandApprovedAsync(db, operation, ct);
        await server.AssertPriorOwnerStoppedAsync(operationId, operation.OwnerId, operation.OwnerHost, lease.HostId, ct);
        var work = await ClaimAsync(lease, actor, tenant, operation, operationId, null, ct);
        try
        {
            // Re-close even a previously prepared gate. A fresh fence prevents an old worker from reopening it.
            await CloseAsync(work, ct);
            var context = new TenantContext(tenantId, tenant.Slug, tenant.Slug);
            await using var customer = await SqlOperations.OpenAsync(work.Customer, ct);
            await DemandClosedAsync(work, ct);
            var read = await obligations.ReadAsync(context, customer, ct);
            _ = OperationPolicy.SelectPlan(work.Operation.SourcePlan, work.Operation.SourcePlan, read); // validates unknown/count/error contract
            var selected = work.Tenant.Plan;
            if (selected == "Standard" && read.State == ObligationState.Outstanding)
            {
                await DemandCurrentApprovalAsync(work, Approval(work), ct);
                if (!work.Operation.AllowApprovalUpgrade) throw new TenantContractException("recovery_child_upgrade_requires_approval");
                selected = "Approval";
            }
            // This is a child step of the restore operation, never an independent operation or traffic-opening path.
            await MoveAsync(work, ResourceCheckpoint.ObligationsChecked, (_, current) => current.SelectedPlan = selected, ct);
            await DemandClosedAsync(work, ct);
            await DemandCurrentApprovalAsync(work, Approval(work), ct);
            await reconciler.MigrateAsync(context, selected, customer, ct);
            await MoveAsync(work, ResourceCheckpoint.SchemaReady, null, ct);
            await DemandClosedAsync(work, ct);
            await reconciler.ApplyFeaturesAsync(context, selected, customer, ct);
            await MoveAsync(work, ResourceCheckpoint.FeaturesReady, null, ct);
            await DemandClosedAsync(work, ct);
            if (!await reconciler.VerifyAsync(context, selected, customer, ct)) throw new TenantContractException("plan_not_ready");
            await MoveAsync(work, ResourceCheckpoint.EntitlementsCommitted, (current, _) => current.Plan = selected, ct);
            await FreezeRecoveryDecisionAsync(work, ct);
            var approval = Approval(work);
            var marker = await applier.ApplyAsync(customer, approval, (value, token) => DemandCurrentApprovalAsync(work, value, token), ct);
            DemandMarker(marker, approval);
            await MoveAsync(work, ResourceCheckpoint.AclApplied, null, ct);
            await applier.VerifyAsync(customer, Approval(work), (value, token) => DemandCurrentApprovalAsync(work, value, token), ct);
            await PrepareOpenAsync(work, ct);
            approval = Approval(work);
            marker = await applier.VerifyAsync(customer, approval, (value, token) => DemandCurrentApprovalAsync(work, value, token), ct);
            DemandMarker(marker, approval);
            return await CompleteRecoveryAsync(work, applier.RequiredMemberships(approval), ct);
        }
        catch (Exception error) { await RecordFailureAsync(work, error, ct); throw; }
    }

    private async Task FreezeRecoveryDecisionAsync(Work work, CancellationToken ct)
    {
        await DemandOwnedAsync(work, ct);
        await using var db = TenancyConnections.PlatformContext(work.Lease.Connection);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == work.Tenant.Id, ct);
        var operation = await db.ResourceOperations.SingleAsync(x => x.Id == work.Operation.Id, ct);
        DemandOwned(tenant, operation, work.Operation.Stamp());
        await DemandApprovedAsync(db, operation, ct);
        if (!await db.Audit.AnyAsync(x => x.TenantId == tenant.Id && x.OperationId == operation.Id && x.Action == "RecoveryAclApplyStarted", ct))
        {
            db.Audit.Add(RecoveryAudit(work.Actor, tenant, operation, "RecoveryAclApplyStarted"));
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task<ResourceResult> CompleteRecoveryAsync(Work work, IReadOnlyCollection<Guid> members, CancellationToken ct)
    {
        await DemandOwnedAsync(work, ct);
        await using var db = TenancyConnections.PlatformContext(work.Lease.Connection);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == work.Tenant.Id, ct);
        var operation = await db.ResourceOperations.SingleAsync(x => x.Id == work.Operation.Id, ct);
        DemandOwned(tenant, operation, work.Operation.Stamp());
        await DemandApprovedAsync(db, operation, ct);
        // These are current platform rows, locked through the final CAS; restored membership lists are never trusted.
        foreach (var user in members.Distinct())
            if (!await db.IdentityMemberships.AnyAsync(x => x.TenantId == tenant.Id && x.GlobalUserId == user && x.Active, ct)
                || !await db.IdentityAccounts.AnyAsync(x => x.Id == user && x.Enabled, ct))
                throw new TenantContractException("current_membership_required");
        var completed = OperationPolicy.Complete(tenant.Status() with { SecurityRevalidationRequired = false }, operation.Stamp(), OperationStatus.Restoring, Now,
            tenant.SecurityRevalidationRequired && operation.Checkpoint == ResourceCheckpoint.GatePrepared && tenant.Plan == operation.SelectedPlan);
        tenant.SecurityRevalidationRequired = false;
        tenant.Operation = completed.Operation; tenant.OperationId = null; tenant.OwnerId = null; tenant.RegistryVersion = completed.RegistryVersion;
        operation.ExpectedRegistryVersion = tenant.RegistryVersion;
        operation.Checkpoint = ResourceCheckpoint.Completed;
        operation.CompletedAt = Now;
        db.Audit.Add(RecoveryAudit(work.Actor, tenant, operation, "RecoveryCompleted"));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result(operation);
    }

    private async Task DemandCurrentApprovalAsync(Work work, RecoveryApproval approval, CancellationToken ct)
    {
        await DemandOwnedAsync(work, ct);
        await using var db = TenancyConnections.PlatformContext(work.Lease.Connection);
        var operation = await db.ResourceOperations.AsNoTracking().SingleAsync(x => x.Id == work.Operation.Id, ct);
        await DemandApprovedAsync(db, operation, ct);
        if (approval != Approval(work) || operation.ApprovedAcl != approval.AclJson || operation.ApprovedHash != approval.AclHash
            || operation.ApprovedBy != approval.ApprovedBy || operation.ApprovalReason != approval.Reason)
            throw new TenantContractException("recovery_decision_changed");
    }
    private static async Task DemandApprovedAsync(PlatformDbContext db, ResourceOperation operation, CancellationToken ct)
    {
        if (operation.ApprovedAt is null || operation.ApprovedAt < operation.StartedAt || string.IsNullOrWhiteSpace(operation.ApprovedAcl)
            || string.IsNullOrWhiteSpace(operation.ApprovedHash) || string.IsNullOrWhiteSpace(operation.ApprovalReason) || string.IsNullOrWhiteSpace(operation.ApprovedBy))
            throw new TenantContractException("recovery_approval_required");
        await DemandRecoveryAuthorityAsync(db, operation, operation.ApprovedBy, operation.AllowApprovalUpgrade, ct);
    }
    private static async Task DemandRecoveryAuthorityAsync(PlatformDbContext db, ResourceOperation operation, string actor, bool upgrade, CancellationToken ct)
    {
        var authority = await db.RecoveryAuthorities.AsNoTracking().SingleOrDefaultAsync(x => x.Actor == actor, ct);
        if (authority is null || authority.Actor != actor || !authority.Enabled || authority.RegisteredAt > operation.StartedAt || upgrade && !authority.CanChangePlan)
            throw new TenantContractException("recovery_authority_required");
    }
    private static void DemandRecovery(TenantRegistration tenant, ResourceOperation operation, long version)
    {
        if (tenant.RegistryVersion != version || tenant.Operation != OperationStatus.Restoring || !tenant.SecurityRevalidationRequired
            || tenant.OperationId != operation.Id || operation.TenantId != tenant.Id || operation.Kind != OperationStatus.Restoring
            || tenant.RetiredAt is not null || tenant.PurgedAt is not null || tenant.AuthorizationGeneration != operation.AuthorizationGeneration
            || operation.CompletedAt is not null)
            throw new TenantContractException("operation_reconciliation_required");
    }
    private OperationalAudit RecoveryAudit(string actor, TenantRegistration tenant, ResourceOperation operation, string action)
    {
        var entry = Audit(actor, tenant, operation, action);
        entry.Details = JsonSerializer.Serialize(new { operation.Kind, operation.OwnerId, operation.Fence, operation.AuthorizationGeneration,
            operation.ApprovedBy, operation.ApprovedAt, operation.ApprovedHash, operation.ApprovalReason, operation.AllowApprovalUpgrade,
            operation.SourcePlan, operation.SelectedPlan, operation.Checkpoint });
        return entry;
    }
    private static void DemandMarker(RecoveryMarker marker, RecoveryApproval approval)
    {
        if (marker.TenantId != approval.TenantId || marker.OperationId != approval.OperationId || marker.Generation != approval.Generation
            || marker.AclHash != approval.AclHash || marker.Revision < 1) throw new TenantContractException("recovery_marker_mismatch");
    }
    private static RecoveryApproval Approval(Work work) => new(work.Tenant.Id, work.Operation.Id, work.Operation.AuthorizationGeneration,
        work.Operation.GateVersion, work.Operation.Fence, work.Operation.ApprovedAcl!, work.Operation.ApprovedHash!, work.Operation.ApprovedBy!, work.Operation.ApprovalReason!);
    private Work RecoveryWork(OperationLease lease, string actor, TenantRegistration tenant, ResourceOperation operation) =>
        new(lease, actor, tenant, operation, TenancyConnections.ForOperation(connections.Customer(tenant.Id, "migrator"), operation.Id, operation.OwnerId));
    private void ValidateRecoveryRequest(Guid tenant, Guid operation, long version)
    {
        if (tenant == Guid.Empty || operation == Guid.Empty || version < 1 || drainTimeoutMilliseconds is < 0 or > 30000)
            throw new TenantContractException("invalid_operation_request");
    }
}
