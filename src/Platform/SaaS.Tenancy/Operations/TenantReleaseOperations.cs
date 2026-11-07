using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Release;
namespace SaaS.Tenancy.Operations;

public sealed partial class TenantOperations
{
    public async Task<ResourceResult> ReleaseAsync(Guid tenantId, Guid operationId, long expectedVersion,
        IReleaseInstaller installer, bool resume, CancellationToken ct)
    {
        ValidateRecoveryRequest(tenantId, operationId, expectedVersion);
        if (!Digest.IsSha256(installer.ManifestSha256)) throw new TenantContractException("release_manifest_invalid");
        var server = new SqlProvisioner(connections.Provisioner());
        await server.AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "tenant-" + tenantId.ToString("N"), ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await VerifyPlatformAsync(db, lease, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new TenantContractException("tenant_missing");
        TenantDatabaseVerifier.ValidateRegistration(tenant);
        var existing = await db.ResourceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct);
        if (existing is not null)
        {
            if (existing.TenantId != tenantId || existing.Kind != OperationStatus.Migrating || existing.RequestedBy != actor)
                throw new TenantContractException("operation_request_conflict");
            var binding = await ReadArtifactBindingAsync(db, tenantId, operationId, "ReleaseManifestBound", ct);
            if (!Digest.EqualsSha256(binding.Sha256, installer.ManifestSha256))
                throw new TenantContractException("release_manifest_changed");
            if (existing.Checkpoint == ResourceCheckpoint.Completed)
            {
                if (!TenantPolicy.IsOperational(tenant.Status(), Now) || tenant.Plan != existing.SelectedPlan)
                    throw new TenantContractException("operation_reconciliation_required");
                await installer.VerifyAsync(new(tenantId, tenant.Slug, tenant.Slug), tenant.Plan, connections.Customer(tenantId, "migrator"), ct);
                return Result(existing);
            }
        }
        if (tenant.RegistryVersion != expectedVersion || tenant.RetiredAt is not null || tenant.PurgedAt is not null
            || tenant.Subscription != SubscriptionStatus.Active || tenant.ExpiresAt <= Now || tenant.SecurityRevalidationRequired)
            throw new TenantContractException("operation_reconciliation_required");
        Work work;
        if (resume)
        {
            if (existing is null || tenant.OperationId != operationId || tenant.Operation != OperationStatus.Migrating)
                throw new TenantContractException("operation_request_conflict");
            await server.AssertPriorOwnerStoppedAsync(operationId, existing.OwnerId, existing.OwnerHost, lease.HostId, ct);
            work = await ClaimAsync(lease, actor, tenant, existing, operationId, null, ct);
        }
        else
        {
            if (existing is not null || !TenantPolicy.IsOperational(tenant.Status(), Now))
                throw new TenantContractException("operation_request_conflict");
            work = await ClaimMigrationMaintenanceAsync(lease, actor, tenant, operationId, new(Guid.Empty, installer.ManifestSha256), "ReleaseManifestBound", ct);
        }
        try
        {
            await NotifyAsync(ResourceCheckpoint.Claimed, ct);
            await CloseAsync(work, ct);
            var context = new TenantContext(tenantId, tenant.Slug, tenant.Slug);
            await DemandReleaseStepAsync(work, ct);
            await using (var customer = await SqlOperations.OpenAsync(work.Customer, ct))
            {
                var read = await obligations.ReadAsync(context, customer, ct);
                // Release never changes plans or erases an obligation; unknown source data is closed.
                _ = OperationPolicy.SelectPlan(work.Operation.SourcePlan, work.Operation.SourcePlan, read);
            }
            await MoveAsync(work, ResourceCheckpoint.ObligationsChecked, null, ct);
            // No customer SQL connection is held while a bundle child runs. Its process and SQL
            // sessions must both stop before the next resource step or a subsequent owner.
            await installer.ApplyAsync(context, work.Tenant.Plan, work.Customer, operationId, work.Operation.OwnerId,
                token => DemandReleaseStepAsync(work, token), ct);
            await MoveAsync(work, ResourceCheckpoint.SchemaReady, null, ct);
            await DemandReleaseStepAsync(work, ct);
            await using (var customer = await SqlOperations.OpenAsync(work.Customer, ct))
                await reconciler.ApplyFeaturesAsync(context, work.Tenant.Plan, customer, ct);
            await MoveAsync(work, ResourceCheckpoint.FeaturesReady, null, ct);
            await DemandReleaseStepAsync(work, ct);
            await installer.VerifyAsync(context, work.Tenant.Plan, work.Customer, ct);
            await using (var customer = await SqlOperations.OpenAsync(work.Customer, ct))
                if (!await reconciler.VerifyAsync(context, work.Tenant.Plan, customer, ct))
                    throw new TenantContractException("release_not_ready");
            await MoveAsync(work, ResourceCheckpoint.EntitlementsCommitted, (current, operation) =>
            {
                if (current.Plan != operation.SourcePlan || current.Plan != operation.SelectedPlan)
                    throw new TenantContractException("operation_reconciliation_required");
            }, ct);
            await PrepareOpenAsync(work, ct);
            return await CompleteAsync(work, ct);
        }
        catch (Exception error) { await RecordFailureAsync(work, error, ct); throw; }
    }

    private async Task<Work> ClaimMigrationMaintenanceAsync(OperationLease lease, string actor, TenantRegistration expected,
        Guid operationId, ArtifactBinding artifact, string bindingAction, CancellationToken ct)
    {
        await lease.AssertHeldAsync(ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == expected.Id, ct);
        if (tenant.RegistryVersion != expected.RegistryVersion || tenant.RetiredAt is not null || tenant.PurgedAt is not null
            || !TenantPolicy.IsOperational(tenant.Status(), Now) || await db.ResourceOperations.AnyAsync(x => x.Id == operationId, ct))
            throw new TenantContractException("operation_reconciliation_required");
        tenant.RegistryVersion = checked(tenant.RegistryVersion + 1);
        tenant.Fence = checked(tenant.Fence + 1);
        tenant.Operation = OperationStatus.Migrating; tenant.OperationId = operationId; tenant.OwnerId = Guid.NewGuid();
        var operation = new ResourceOperation
        {
            Id = operationId, TenantId = tenant.Id, Kind = OperationStatus.Migrating,
            SourcePlan = tenant.Plan, RequestedPlan = tenant.Plan, SelectedPlan = tenant.Plan,
            AuthorizationGeneration = tenant.AuthorizationGeneration, RequestedBy = actor, StartedAt = Now,
            OwnerId = tenant.OwnerId.Value, OwnerHost = lease.HostId, Fence = tenant.Fence,
            ExpectedRegistryVersion = tenant.RegistryVersion
        };
        db.ResourceOperations.Add(operation);
        db.Audit.Add(Audit(actor, tenant, operation, bindingAction == "ReleaseManifestBound" ? "ReleaseClaimed" : "BackupClaimed"));
        var binding = Audit(actor, tenant, operation, bindingAction);
        binding.Details = JsonSerializer.Serialize(artifact);
        db.Audit.Add(binding);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(lease, actor, tenant, operation, TenancyConnections.ForOperation(
            connections.Customer(tenant.Id, "migrator"), operation.Id, operation.OwnerId));
    }

    private async Task DemandReleaseStepAsync(Work work, CancellationToken ct)
    {
        await DemandClosedAsync(work, ct);
        if (work.Tenant.Subscription != SubscriptionStatus.Active || work.Tenant.ExpiresAt <= Now)
            throw new TenantContractException("operation_reconciliation_required");
    }

    private sealed record ArtifactBinding(Guid BackupId, string Sha256);
    private static async Task<ArtifactBinding> ReadArtifactBindingAsync(PlatformDbContext db, Guid tenantId,
        Guid operationId, string action, CancellationToken ct)
    {
        var entries = await db.Audit.AsNoTracking().Where(x => x.TenantId == tenantId && x.OperationId == operationId && x.Action == action)
            .Select(x => x.Details).ToArrayAsync(ct);
        if (entries.Length != 1) throw new TenantContractException("artifact_binding_missing");
        try
        {
            var binding = JsonSerializer.Deserialize<ArtifactBinding>(entries[0]);
            return binding is not null && Digest.IsSha256(binding.Sha256) ? binding : throw new TenantContractException("artifact_binding_invalid");
        }
        catch (JsonException) { throw new TenantContractException("artifact_binding_invalid"); }
    }
}
