using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Release;
namespace SaaS.Tenancy.Operations;

public enum PhysicalRestoreCheckpoint { Requested, RecoveryBegun, Bound, SourceVerified, DatabaseRestored, IdentityVerified }

public sealed partial class TenantOperations
{
    /// <summary>
    /// Restores only a bound full backup into its registered tenant. Returning does NOT open
    /// traffic: the current-platform explicit ACL approval/recovery protocol must still finish.
    /// </summary>
    public async Task<ResourceResult> RestoreBackupAsync(Guid tenantId, Guid operationId, long expectedVersion,
        Guid backupId, SqlBackupStore store, bool resume, CancellationToken ct,
        Func<PhysicalRestoreCheckpoint, CancellationToken, Task>? physicalProgress = null)
    {
        ValidateRecoveryRequest(tenantId, operationId, expectedVersion);
        await using var backup = await store.OpenAsync(tenantId, backupId, ct);
        var server = new SqlProvisioner(connections.Provisioner()); await server.AuthenticateAsync(ct);
        // Wrong source, changed bytes, mixed sets and unsafe file layouts fail before registry
        // changes, credential revocation, SINGLE_USER or any SQL overwrite.
        await store.VerifySqlAsync(backup, connections.Provisioner(), ct);
        var binding = new ArtifactBinding(backupId, backup.ManifestSha256);
        var intent = await RequestPhysicalRestoreAsync(tenantId, operationId, expectedVersion, binding, resume, ct);
        expectedVersion = intent.Version;
        if (intent.Begin)
        {
            if (physicalProgress is not null) await physicalProgress(PhysicalRestoreCheckpoint.Requested, ct);
            var begun = await BeginRecoveryAsync(tenantId, operationId, expectedVersion, ct);
            expectedVersion = begun.RegistryVersion; // Exact result of our own CAS, never a blind reload/retry.
            if (physicalProgress is not null) await physicalProgress(PhysicalRestoreCheckpoint.RecoveryBegun, ct);
        }
        await using var lease = await OperationLease.AcquireRestoreAsync(connections.Platform("operator"), "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection); await VerifyPlatformAsync(db, lease, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new TenantContractException("tenant_missing");
        TenantDatabaseVerifier.ValidateRegistration(tenant);
        var operation = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct)
            ?? throw new TenantContractException("operation_missing");
        if (operation.TenantId != tenantId || operation.RequestedBy != actor || operation.Kind != OperationStatus.Restoring)
            throw new TenantContractException("operation_request_conflict");
        var bound = await db.Audit.AnyAsync(x => x.TenantId == tenantId && x.OperationId == operationId && x.Action == PhysicalRestoreBarrier.Bound, ct);
        var verified = await db.Audit.AnyAsync(x => x.TenantId == tenantId && x.OperationId == operationId && x.Action == PhysicalRestoreBarrier.Verified, ct);
        if (bound && await ReadArtifactBindingAsync(db, tenantId, operationId, PhysicalRestoreBarrier.Bound, ct) != binding
            || verified && (!bound || await ReadArtifactBindingAsync(db, tenantId, operationId, PhysicalRestoreBarrier.Verified, ct) != binding))
            throw new TenantContractException("backup_manifest_changed");
        if (operation.Checkpoint == ResourceCheckpoint.Completed)
        {
            if (!verified) throw new TenantContractException("physical_restore_incomplete");
            return Result(operation); // Never replay physical SQL over a completed/newly written database.
        }
        DemandRecovery(tenant, operation, expectedVersion);
        if (tenant.Subscription != SubscriptionStatus.Active || tenant.ExpiresAt <= Now)
            throw new TenantContractException("operation_reconciliation_required");
        await server.AssertPriorOwnerStoppedAsync(operationId, operation.OwnerId, operation.OwnerHost, lease.HostId, ct);
        if (verified)
        {
            await TenantDatabaseVerifier.VerifyIdentityAsync(tenant, connections.Customer(tenantId, "migrator"), ct);
            return Result(operation); // Physical step is immutable; ACL resume is a distinct operation phase.
        }
        if (operation.Checkpoint >= ResourceCheckpoint.AclApplied || await db.Audit.AnyAsync(x =>
            x.TenantId == tenantId && x.OperationId == operationId && x.Action == "RecoveryAclApplyStarted", ct))
            throw new TenantContractException("physical_restore_after_acl_forbidden");
        var work = await ClaimAsync(lease, actor, tenant, operation, operationId, null, ct);
        try
        {
            if (!bound)
            {
                // Before the FIRST overwrite, registry target identity and write drain are proved.
                await CloseAsync(work, ct);
                await AppendArtifactEvidenceAsync(work, PhysicalRestoreBarrier.Bound, binding, ct);
            }
            // A retry may find SQL RESTORING/SINGLE_USER or a historical gate. Its prior bound
            // checkpoint proves initial identity/drain; do not seed or depend on that damaged DB.
            await PhysicalStepAsync(work, PhysicalRestoreCheckpoint.Bound, physicalProgress, ct);
            var serverConnection = TenancyConnections.ForOperation(connections.Provisioner(), operationId, work.Operation.OwnerId);
            await store.VerifySqlAsync(backup, serverConnection, ct);
            await PhysicalStepAsync(work, PhysicalRestoreCheckpoint.SourceVerified, physicalProgress, ct);
            await store.RestoreAsync(backup, serverConnection, ct);
            await PhysicalStepAsync(work, PhysicalRestoreCheckpoint.DatabaseRestored, physicalProgress, ct);
            // Immutable identity is checked BEFORE EF migrations, Orchard warm-up or ACL replacement.
            await TenantDatabaseVerifier.VerifyIdentityAsync(work.Tenant, work.Customer, ct);
            await CloseAsync(work, ct); // Re-close the historical restored gate using the current platform fence.
            await AppendArtifactEvidenceAsync(work, PhysicalRestoreBarrier.Verified, binding, ct);
            await PhysicalStepAsync(work, PhysicalRestoreCheckpoint.IdentityVerified, physicalProgress, ct);
            return Result(work.Operation);
        }
        catch (Exception error) { await RecordFailureAsync(work, error, ct); throw; }
    }

    private async Task<(long Version, bool Begin)> RequestPhysicalRestoreAsync(Guid tenantId, Guid operationId,
        long expectedVersion, ArtifactBinding binding, bool resume, CancellationToken ct)
    {
        // Persist source intent BEFORE the security-generation claim. As soon as the claim sets
        // OperationId, every generic recovery entry sees this barrier, including after a crash.
        await using var lease = await OperationLease.AcquireRestoreAsync(connections.Platform("operator"), "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await VerifyPlatformAsync(db, lease, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var tenant = await db.Tenants.SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new TenantContractException("tenant_missing");
        TenantDatabaseVerifier.ValidateRegistration(tenant);
        var requested = await db.Audit.Where(x => x.OperationId == operationId && x.Action == PhysicalRestoreBarrier.Requested).ToArrayAsync(ct);
        var operation = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct);
        if (requested.Length != 0)
        {
            if (!resume || requested.Length != 1 || requested[0].TenantId != tenantId || requested[0].Actor != actor
                || await ReadArtifactBindingAsync(db, tenantId, operationId, PhysicalRestoreBarrier.Requested, ct) != binding)
                throw new TenantContractException("backup_manifest_changed");
            if (operation is not null) return (expectedVersion, false); // Remaining checks use the supplied CAS version.
        }
        else if (resume || operation is not null)
            throw new TenantContractException("physical_restore_intent_required");
        if (tenant.RegistryVersion != expectedVersion || tenant.RetiredAt is not null || tenant.PurgedAt is not null
            || !TenantPolicy.IsOperational(tenant.Status(), Now))
            throw new TenantContractException("operation_reconciliation_required");
        await TenantDatabaseVerifier.VerifyIdentityAsync(tenant, connections.Customer(tenantId, "migrator"), ct);
        if (requested.Length == 0)
        {
            tenant.RegistryVersion = checked(tenant.RegistryVersion + 1);
            db.Audit.Add(new OperationalAudit { TenantId = tenantId, OperationId = operationId, Actor = actor,
                Action = PhysicalRestoreBarrier.Requested, RegistryVersion = tenant.RegistryVersion, OccurredAt = Now,
                Details = JsonSerializer.Serialize(binding) });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return (tenant.RegistryVersion, true);
    }

    private async Task PhysicalStepAsync(Work work, PhysicalRestoreCheckpoint checkpoint,
        Func<PhysicalRestoreCheckpoint, CancellationToken, Task>? callback, CancellationToken ct)
    {
        await DemandOwnedAsync(work, ct);
        if (work.Tenant.Subscription != SubscriptionStatus.Active || work.Tenant.ExpiresAt <= Now || !work.Tenant.SecurityRevalidationRequired)
            throw new TenantContractException("operation_reconciliation_required");
        if (callback is not null) await callback(checkpoint, ct);
        // A hook or concurrent lifecycle transaction may have changed authority while we waited.
        await DemandOwnedAsync(work, ct);
    }
}
