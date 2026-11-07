using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Release;
namespace SaaS.Tenancy.Operations;

public sealed record BackupResult(Guid BackupId, Guid TenantId, string ManifestSha256, long RegistryVersion, ResourceCheckpoint Checkpoint);

public sealed partial class TenantOperations
{
    /// <summary>Backup is a gated maintenance operation, not an unfenced background SQL job.</summary>
    public async Task<BackupResult> BackupAsync(Guid tenantId, Guid backupId, long expectedVersion,
        SqlBackupStore store, bool resume, CancellationToken ct)
    {
        ValidateRecoveryRequest(tenantId, backupId, expectedVersion);
        var server = new SqlProvisioner(connections.Provisioner()); await server.AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection); await VerifyPlatformAsync(db, lease, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new TenantContractException("tenant_missing");
        TenantDatabaseVerifier.ValidateRegistration(tenant);
        var request = new ArtifactBinding(backupId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"backup-v1:{tenantId:N}:{backupId:N}"))));
        var existing = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == backupId, ct);
        if (existing is not null)
        {
            if (existing.TenantId != tenantId || existing.Kind != OperationStatus.Migrating || existing.RequestedBy != actor
                || await ReadArtifactBindingAsync(db, tenantId, backupId, "BackupSourceBound", ct) != request)
                throw new TenantContractException("operation_request_conflict");
            if (existing.Checkpoint == ResourceCheckpoint.Completed)
            {
                var sealedBinding = await ReadArtifactBindingAsync(db, tenantId, backupId, "BackupSealed", ct);
                await using var artifact = await store.OpenAsync(tenantId, backupId, ct);
                if (!Digest.EqualsSha256(sealedBinding.Sha256, artifact.ManifestSha256)) throw new TenantContractException("backup_manifest_changed");
                return new(backupId, tenantId, artifact.ManifestSha256, existing.ExpectedRegistryVersion, existing.Checkpoint);
            }
        }
        if (tenant.RegistryVersion != expectedVersion || tenant.RetiredAt is not null || tenant.PurgedAt is not null
            || tenant.Subscription != SubscriptionStatus.Active || tenant.ExpiresAt <= Now || tenant.SecurityRevalidationRequired)
            throw new TenantContractException("operation_reconciliation_required");
        Work work;
        if (resume)
        {
            if (existing is null || tenant.Operation != OperationStatus.Migrating || tenant.OperationId != backupId)
                throw new TenantContractException("operation_request_conflict");
            await server.AssertPriorOwnerStoppedAsync(backupId, existing.OwnerId, existing.OwnerHost, lease.HostId, ct);
            work = await ClaimAsync(lease, actor, tenant, existing, backupId, null, ct);
        }
        else
        {
            if (existing is not null || !TenantPolicy.IsOperational(tenant.Status(), Now)) throw new TenantContractException("operation_request_conflict");
            work = await ClaimMigrationMaintenanceAsync(lease, actor, tenant, backupId, request, "BackupSourceBound", ct);
        }
        try
        {
            await NotifyAsync(ResourceCheckpoint.Claimed, ct);
            await CloseAsync(work, ct);
            await DemandReleaseStepAsync(work, ct);
            var serverConnection = TenancyConnections.ForOperation(connections.Provisioner(), backupId, work.Operation.OwnerId);
            VerifiedBackup artifact;
            if (await db.Audit.AnyAsync(x => x.TenantId == tenantId && x.OperationId == backupId && x.Action == "BackupSealed", ct))
            {
                var sealedBinding = await ReadArtifactBindingAsync(db, tenantId, backupId, "BackupSealed", ct);
                artifact = await store.OpenAsync(tenantId, backupId, ct);
                if (!Digest.EqualsSha256(sealedBinding.Sha256, artifact.ManifestSha256))
                { await artifact.DisposeAsync(); throw new TenantContractException("backup_manifest_changed"); }
            }
            else artifact = await store.CreateAsync(tenantId, backupId, serverConnection, resume, ct);
            await using (artifact)
            {
                await store.VerifySqlAsync(artifact, serverConnection, ct);
                await DemandReleaseStepAsync(work, ct);
                await AppendArtifactEvidenceAsync(work, "BackupSealed", new(backupId, artifact.ManifestSha256), ct);
                await PrepareOpenAsync(work, ct);
                var completed = await CompleteAsync(work, ct);
                return new(backupId, tenantId, artifact.ManifestSha256, completed.RegistryVersion, completed.Checkpoint);
            }
        }
        catch (Exception error) { await RecordFailureAsync(work, error, ct); throw; }
    }

    private async Task AppendArtifactEvidenceAsync(Work work, string action, ArtifactBinding binding, CancellationToken ct)
    {
        await DemandOwnedAsync(work, ct);
        await using var db = TenancyConnections.PlatformContext(work.Lease.Connection);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == work.Tenant.Id, ct);
        var operation = await db.ResourceOperations.SingleAsync(x => x.Id == work.Operation.Id, ct);
        DemandOwned(tenant, operation, work.Operation.Stamp());
        if (await db.Audit.AnyAsync(x => x.TenantId == tenant.Id && x.OperationId == operation.Id && x.Action == action, ct))
        {
            if (await ReadArtifactBindingAsync(db, tenant.Id, operation.Id, action, ct) != binding)
                throw new TenantContractException("artifact_binding_changed");
        }
        else
        {
            tenant.RegistryVersion = checked(tenant.RegistryVersion + 1); operation.ExpectedRegistryVersion = tenant.RegistryVersion;
            var entry = Audit(work.Actor, tenant, operation, action); entry.Details = JsonSerializer.Serialize(binding);
            db.Audit.Add(entry); await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct); work.Tenant = tenant; work.Operation = operation;
    }
}
