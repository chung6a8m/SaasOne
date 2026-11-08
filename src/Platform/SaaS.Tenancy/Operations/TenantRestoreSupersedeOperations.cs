using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Release;

namespace SaaS.Tenancy.Operations;

public sealed record RestoreSupersedeDecision(string LossScope, string Reason);
public sealed record RestoreSupersedeResult(Guid PreviousOperationId, Guid OperationId, Guid TenantId,
    Guid BackupId, string ManifestSha256, long RegistryVersion, string Status);

public sealed partial class TenantOperations
{
    /// <summary>
    /// Supersede a verified physical recovery whose Approval upgrade was not authorized.
    /// This does not restore SQL or open the gate: a new source-bound restore-backup-resume
    /// and current ACL approval must complete before traffic resumes.
    /// </summary>
    public async Task<RestoreSupersedeResult> SupersedeRecoverySourceAsync(Guid tenantId, Guid previousOperationId,
        Guid newOperationId, long expectedVersion, Guid replacementBackupId, SqlBackupStore store,
        RestoreSupersedeDecision decision, CancellationToken ct)
    {
        ValidateRecoveryRequest(tenantId, previousOperationId, expectedVersion);
        ValidateRecoveryRequest(tenantId, newOperationId, expectedVersion);
        if (previousOperationId == newOperationId || replacementBackupId == Guid.Empty)
            throw new TenantContractException("recovery_supersede_invalid");
        if (decision is null || string.IsNullOrWhiteSpace(decision.LossScope) || decision.LossScope.Length > 1000
            || string.IsNullOrWhiteSpace(decision.Reason) || decision.Reason.Length > 1000)
            throw new TenantContractException("recovery_supersede_decision_invalid");

        // Validate the replacement source and SQL layout before changing platform authority.
        await using var backup = await store.OpenAsync(tenantId, replacementBackupId, ct);
        var server = new SqlProvisioner(connections.Provisioner());
        await server.AuthenticateAsync(ct);
        await store.VerifySqlAsync(backup, connections.Provisioner(), ct);

        await using var lease = await OperationLease.AcquireRestoreAsync(connections.Platform("operator"),
            "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await VerifyPlatformAsync(db, lease, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new TenantContractException("tenant_missing");
        var previous = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == previousOperationId, ct)
            ?? throw new TenantContractException("operation_missing");
        DemandSupersedeSource(tenant, previous, expectedVersion);
        if (await db.ResourceOperations.AnyAsync(x => x.Id == newOperationId, ct)
            || await db.Audit.AnyAsync(x => x.OperationId == newOperationId, ct))
            throw new TenantContractException("operation_request_conflict");
        if (!await db.Audit.AnyAsync(x => x.TenantId == tenantId && x.OperationId == previousOperationId
            && x.Action == PhysicalRestoreBarrier.Verified, ct)
            || await db.Audit.AnyAsync(x => x.TenantId == tenantId && x.OperationId == previousOperationId
            && x.Action == "RecoveryAclApplyStarted", ct))
            throw new TenantContractException("recovery_supersede_not_safe");
        var previousSource = await ReadArtifactBindingAsync(db, tenantId, previousOperationId,
            PhysicalRestoreBarrier.Verified, ct);
        if (previousSource.BackupId == replacementBackupId)
            throw new TenantContractException("recovery_supersede_same_source");
        await DemandRecoveryAuthorityAsync(db, previous, actor, false, ct);
        await server.AssertPriorOwnerStoppedAsync(previousOperationId, previous.OwnerId, previous.OwnerHost, lease.HostId, ct);

        // The prior DB must still have the registered immutable identity and a closed write gate.
        // The replacement will independently pass both identity gates again.
        var customerConnection = TenancyConnections.ForOperation(connections.Customer(tenantId, "migrator"),
            previousOperationId, previous.OwnerId);
        await TenantDatabaseVerifier.VerifyIdentityAsync(tenant, customerConnection, ct);
        await using (var customer = await SqlOperations.OpenAsync(customerConnection, ct))
        {
            var gate = await TenantWriteGate.ReadAsync(customer, null, ct);
            if (gate.IsOpen || gate.TenantId != tenantId)
                throw new TenantContractException("recovery_supersede_gate_not_closed");
            var pending = await obligations.ReadAsync(new(tenantId, tenant.Slug, tenant.Slug), customer, ct);
            if (pending is null || pending.State != ObligationState.Outstanding || pending.Count < 1)
                throw new TenantContractException("recovery_supersede_requires_outstanding_obligation");
        }

        // Switch ownership and persist the new immutable source intent atomically. The pending
        // intent blocks generic recovery before any new physical overwrite begins.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var current = await db.Tenants.SingleAsync(x => x.Id == tenantId, ct);
        var old = await db.ResourceOperations.SingleAsync(x => x.Id == previousOperationId, ct);
        DemandSupersedeSource(current, old, expectedVersion);
        await DemandRecoveryAuthorityAsync(db, old, actor, false, ct);
        if (await db.ResourceOperations.AnyAsync(x => x.Id == newOperationId, ct)
            || await db.Audit.AnyAsync(x => x.OperationId == newOperationId, ct)
            || !await db.Audit.AnyAsync(x => x.TenantId == tenantId && x.OperationId == previousOperationId
                && x.Action == PhysicalRestoreBarrier.Verified, ct)
            || await db.Audit.AnyAsync(x => x.TenantId == tenantId && x.OperationId == previousOperationId
                && x.Action == "RecoveryAclApplyStarted", ct))
            throw new TenantContractException("recovery_supersede_not_safe");
        var now = Now;
        current.RegistryVersion = checked(current.RegistryVersion + 1);
        current.Fence = checked(current.Fence + 1);
        current.AuthorizationGeneration = checked(current.AuthorizationGeneration + 1);
        current.OwnerId = Guid.NewGuid();
        current.OperationId = newOperationId;
        current.SecurityRevalidationRequired = true;
        old.CompletedAt = now; // Terminal, but never mark successful recovery.
        var next = new ResourceOperation
        {
            Id = newOperationId, TenantId = tenantId, Kind = OperationStatus.Restoring,
            SourcePlan = current.Plan, RequestedPlan = current.Plan, SelectedPlan = current.Plan,
            Checkpoint = ResourceCheckpoint.Claimed, OwnerId = current.OwnerId.Value, OwnerHost = lease.HostId,
            Fence = current.Fence, ExpectedRegistryVersion = current.RegistryVersion,
            AuthorizationGeneration = current.AuthorizationGeneration, RequestedBy = actor, StartedAt = now
        };
        db.ResourceOperations.Add(next);
        var security = await db.IdentityTenantSecurity.SingleAsync(x => x.TenantId == tenantId, ct);
        security.Generation = checked(security.Generation + 1);
        await db.IdentityFamilies.Where(x => x.TenantId == tenantId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
        await db.IdentitySessions.Where(x => x.TenantId == tenantId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);

        db.Audit.Add(new OperationalAudit
        {
            TenantId = tenantId, OperationId = previousOperationId, Actor = actor,
            Action = "RecoverySourceSuperseded", RegistryVersion = current.RegistryVersion,
            OccurredAt = now, Details = JsonSerializer.Serialize(new {
                PreviousOperationId = previousOperationId, PreviousBackupId = previousSource.BackupId,
                PreviousManifestSha256 = previousSource.Sha256, NewOperationId = newOperationId,
                NewBackupId = replacementBackupId, NewManifestSha256 = backup.ManifestSha256,
                decision.LossScope, decision.Reason, AuthorizationGeneration = current.AuthorizationGeneration
            })
        });
        db.Audit.Add(new OperationalAudit
        {
            TenantId = tenantId, OperationId = newOperationId, Actor = actor,
            Action = "RecoverySourceApproved", RegistryVersion = current.RegistryVersion,
            OccurredAt = now, Details = JsonSerializer.Serialize(new {
                PreviousOperationId = previousOperationId, BackupId = replacementBackupId,
                ManifestSha256 = backup.ManifestSha256, decision.LossScope, decision.Reason
            })
        });
        db.Audit.Add(new OperationalAudit
        {
            TenantId = tenantId, OperationId = newOperationId, Actor = actor,
            Action = PhysicalRestoreBarrier.Requested, RegistryVersion = current.RegistryVersion,
            OccurredAt = now, Details = JsonSerializer.Serialize(new ArtifactBinding(replacementBackupId, backup.ManifestSha256))
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(previousOperationId, newOperationId, tenantId, replacementBackupId,
            backup.ManifestSha256, current.RegistryVersion, "PENDING_PHYSICAL_RESTORE");
    }

    private void DemandSupersedeSource(TenantRegistration tenant, ResourceOperation old, long expectedVersion)
    {
        DemandRecovery(tenant, old, expectedVersion);
        if (tenant.Subscription != SubscriptionStatus.Active || tenant.ExpiresAt <= Now
            || old.Checkpoint != ResourceCheckpoint.GateClosed || old.ApprovedAt is null
            || string.IsNullOrWhiteSpace(old.ApprovedBy) || string.IsNullOrWhiteSpace(old.ApprovedHash)
            || old.AllowApprovalUpgrade || tenant.Plan != "Standard")
            throw new TenantContractException("recovery_supersede_requires_refused_upgrade");
    }
}
