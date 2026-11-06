using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
namespace SaaS.Tenancy.Operations;

public sealed class TenancyProvisioning(TenancyConnections connections, IOrchardMaintenance orchard)
{
    public async Task BootstrapAsync(Guid installationId, CancellationToken cancellationToken)
    {
        if (installationId == Guid.Empty) throw new TenantContractException("invalid_installation_id");
        var server = new SqlProvisioner(connections.Provisioner());
        var actor = await server.AuthenticateAsync(cancellationToken);
        await using var lease = await OperationLease.AcquireAsync(connections.Provisioner(), "platform-bootstrap", cancellationToken);
        await server.AssertPriorOwnerStoppedAsync(installationId, installationId, lease.HostId, lease.HostId, cancellationToken);
        var taggedServer = new SqlProvisioner(TenancyConnections.ForOperation(connections.Provisioner(), installationId, installationId));
        var migrator = TenancyConnections.ForOperation(connections.Platform("migrator"), installationId, installationId);
        var runtime = connections.Platform("runtime");
        var created = await taggedServer.CreateDatabaseAsync(TenancyConnections.PlatformDatabase, true, cancellationToken);
        if (!created)
        {
            // Never adopt an existing platform database without its original installation marker.
            await using var existing = TenancyConnections.PlatformContext(migrator);
            var record = await existing.Bootstrap.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (record is null || record.InstallationId != installationId || record.OperationHost != lease.HostId)
                throw new TenantContractException("platform_reconciliation_required");
            if (record.Completed) throw new TenantContractException("bootstrap_already_completed");
        }
        await taggedServer.EnsurePrincipalAsync(migrator, true, cancellationToken);
        await using (var database = TenancyConnections.PlatformContext(migrator))
        {
            await database.Database.MigrateAsync(cancellationToken);
            if (created)
            {
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                database.Bootstrap.Add(new PlatformBootstrap { Id = 1, InstallationId = installationId, OperationHost = lease.HostId });
                database.Audit.Add(Audit(null, installationId, actor, "BootstrapStarted", 0));
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        await lease.AssertHeldAsync(cancellationToken);
        await orchard.InitializeAsync("Default", string.Empty, installationId, migrator, cancellationToken);
        await taggedServer.GrantOperatorAsync(connections.Platform("operator"), cancellationToken);
        await taggedServer.GrantRuntimeAsync(runtime, true, cancellationToken);
        await orchard.VerifyAsync("Default", string.Empty, installationId, runtime, cancellationToken);
        await using (var runtimeDatabase = TenancyConnections.PlatformContext(runtime))
            await TenancyConnections.VerifySchemaAsync(runtimeDatabase, cancellationToken);
        await lease.AssertHeldAsync(cancellationToken);
        await using (var database = TenancyConnections.PlatformContext(migrator))
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var record = await database.Bootstrap.SingleAsync(x => x.Id == 1, cancellationToken);
            if (record.Completed || record.InstallationId != installationId || record.OperationHost != lease.HostId)
                throw new TenantContractException("platform_reconciliation_required");
            record.BaselineVersion = 1;
            record.Completed = true;
            database.Audit.Add(Audit(null, installationId, actor, "BootstrapCompleted", 0));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task ProvisionAsync(ProvisionRequest request, CancellationToken cancellationToken)
    {
        // Re-validate public service callers as well as CLI input.
        _ = TenantSlug.Parse(request.Slug);
        if (request.TenantId == Guid.Empty || request.OperationId == Guid.Empty || request.Plan is not ("Standard" or "Approval") || request.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new TenantContractException("invalid_provision_request");
        var server = new SqlProvisioner(connections.Provisioner());
        await server.AuthenticateAsync(cancellationToken);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "tenant-" + request.TenantId.ToString("N"), cancellationToken);
        var actor = await SqlOperations.ScalarAsync(lease.Connection,
            "SELECT CASE WHEN IS_ROLEMEMBER('saas_operator') = 1 AND IS_SRVROLEMEMBER('sysadmin') = 0 THEN ORIGINAL_LOGIN() ELSE NULL END", cancellationToken) as string
            ?? throw new TenantContractException("operator_not_authorized");
        await using var database = TenancyConnections.PlatformContext(lease.Connection);
        var bootstrap = await database.Bootstrap.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (bootstrap is null || !bootstrap.Completed || bootstrap.BaselineVersion != 1 || bootstrap.OperationHost != lease.HostId)
            throw new TenantContractException("platform_not_ready");
        await TenancyConnections.VerifySchemaAsync(database, cancellationToken);
        var existing = await database.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.TenantId, cancellationToken);
        var previous = await database.Operations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.OperationId, cancellationToken);
        if (existing is not null)
        {
            TenantDatabaseVerifier.ValidateRegistration(existing);
            if (previous is null || previous.TenantId != request.TenantId || previous.RequestHash != request.Hash
                || existing.Slug != request.Slug || existing.ExpiresAt != request.ExpiresAt || existing.Plan != request.Plan)
                throw new TenantContractException("operation_request_conflict");
            if (previous.Checkpoint == ProvisioningCheckpoint.Completed)
            {
                if (!TenantPolicy.IsOperational(existing.Status(), DateTimeOffset.UtcNow)) throw new TenantContractException("tenant_not_operational");
                await VerifyRuntimeAsync(existing, cancellationToken);
                return;
            }
            if (existing.OperationId != request.OperationId || existing.Operation is not (OperationStatus.Provisioning or OperationStatus.Failed))
                throw new TenantContractException("operation_request_conflict");
            await server.AssertPriorOwnerStoppedAsync(request.OperationId, previous.OwnerId, previous.OwnerHost, lease.HostId, cancellationToken);
        }
        else if (previous is not null) throw new TenantContractException("operation_request_conflict");

        var owner = Guid.NewGuid();
        OperationStamp stamp;
        TenantRegistration registration;
        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            registration = await database.Tenants.SingleOrDefaultAsync(x => x.Id == request.TenantId, cancellationToken) ?? new TenantRegistration
            {
                Id = request.TenantId, Slug = request.Slug, DatabaseName = request.DatabaseName, DatabaseNonce = Guid.NewGuid(),
                RuntimeSecret = TenancyConnections.TenantSecret(request.TenantId, "runtime"), MigratorSecret = TenancyConnections.TenantSecret(request.TenantId, "migrator"),
                Plan = request.Plan, ExpiresAt = request.ExpiresAt, Subscription = SubscriptionStatus.Active,
                Operation = OperationStatus.Provisioning, OperationId = request.OperationId
            };
            DemandEligible(registration);
            var operation = await database.Operations.SingleOrDefaultAsync(x => x.Id == request.OperationId, cancellationToken);
            if (operation is null)
            {
                database.Tenants.Add(registration);
                operation = new ProvisioningOperation { Id = request.OperationId, TenantId = request.TenantId, RequestHash = request.Hash, StartedAt = DateTimeOffset.UtcNow };
                database.Operations.Add(operation);
            }
            else if (operation.RequestHash != request.Hash || registration.OperationId != request.OperationId
                || registration.Operation is not (OperationStatus.Provisioning or OperationStatus.Failed))
                throw new TenantContractException("operation_request_conflict");
            registration.RegistryVersion = checked(registration.RegistryVersion + 1);
            registration.Fence = checked(registration.Fence + 1);
            registration.OwnerId = owner;
            registration.Operation = OperationStatus.Provisioning;
            operation.OwnerId = owner;
            operation.OwnerHost = lease.HostId;
            operation.Fence = registration.Fence;
            operation.Failed = false;
            operation.ErrorCode = null;
            database.Audit.Add(Audit(registration.Id, request.OperationId, actor, "ProvisionClaimed", registration.RegistryVersion));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            stamp = new(request.OperationId, owner, registration.Fence, registration.RegistryVersion);
        }
        var taggedServer = new SqlProvisioner(TenancyConnections.ForOperation(connections.Provisioner(), request.OperationId, owner));
        var migrator = TenancyConnections.ForOperation(connections.Customer(request.TenantId, "migrator"), request.OperationId, owner);
        try
        {
            var checkpoint = await CheckpointAsync();
            var created = await taggedServer.CreateDatabaseAsync(request.DatabaseName, checkpoint == ProvisioningCheckpoint.Registered, cancellationToken);
            if (!created) await TenantDatabaseVerifier.VerifyIdentityAsync(registration, migrator, cancellationToken);
            await AssertOwnedAsync();
            await taggedServer.EnsurePrincipalAsync(migrator, true, cancellationToken);
            await using (var customer = TenancyConnections.CustomerContext(migrator))
            {
                await customer.Database.MigrateAsync(cancellationToken);
                if (created)
                {
                    customer.Identity.Add(new TenantDatabaseIdentity
                    {
                        Id = 1, TenantId = registration.Id, DatabaseNonce = registration.DatabaseNonce,
                        InitialOperationId = request.OperationId, CreatedAt = DateTimeOffset.UtcNow
                    });
                    await customer.SaveChangesAsync(cancellationToken);
                }
                await TenancyConnections.VerifySchemaAsync(customer, cancellationToken);
            }
            await TenantDatabaseVerifier.VerifyIdentityAsync(registration, migrator, cancellationToken);
            await AdvanceAsync(ProvisioningCheckpoint.DatabaseReady);
            var native = await orchard.InitializeAsync(registration.Slug, registration.Slug, registration.Id, migrator, cancellationToken);
            await AssertOwnedAsync();
            await using (var customer = TenancyConnections.CustomerContext(migrator))
            {
                var seed = await customer.Baseline.SingleOrDefaultAsync(cancellationToken);
                if (seed is null)
                {
                    if (checkpoint >= ProvisioningCheckpoint.OrchardReady) throw new TenantContractException("tenant_baseline_missing");
                    customer.Baseline.Add(new TenantBaseline { Id = 1, SeedVersion = 1, Recipe = TenantDatabaseVerifier.Recipe, FeatureHash = native.FeatureHash, OrchardMigrationHash = native.MigrationHash });
                    await customer.SaveChangesAsync(cancellationToken);
                }
                else if (seed.SeedVersion != 1 || seed.Recipe != TenantDatabaseVerifier.Recipe || seed.FeatureHash != native.FeatureHash || seed.OrchardMigrationHash != native.MigrationHash)
                    throw new TenantContractException("tenant_baseline_mismatch");
            }
            await AdvanceAsync(ProvisioningCheckpoint.OrchardReady);
            await taggedServer.GrantRuntimeAsync(connections.Customer(request.TenantId, "runtime"), false, cancellationToken);
            await VerifyRuntimeAsync(registration, cancellationToken);
            await AdvanceAsync(ProvisioningCheckpoint.RuntimeVerified);
            await VerifyRuntimeAsync(registration, cancellationToken);
            await AssertOwnedAsync();
            database.ChangeTracker.Clear();
            await using var complete = await database.Database.BeginTransactionAsync(cancellationToken);
            var current = await database.Tenants.SingleAsync(x => x.Id == registration.Id, cancellationToken);
            _ = TenantPolicy.CompleteProvisioning(current.Status(), stamp, DateTimeOffset.UtcNow);
            // Server-side expiry and the complete common-version predicate are checked at the actual write.
            var affected = await database.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [platform].[Tenants]
                SET [Operation] = 0, [OperationId] = NULL, [OwnerId] = NULL, [RegistryVersion] = [RegistryVersion] + 1
                WHERE [Id] = {registration.Id} AND [RegistryVersion] = {stamp.RegistryVersion}
                    AND [OperationId] = {stamp.OperationId} AND [OwnerId] = {stamp.OwnerId} AND [Fence] = {stamp.Fence}
                    AND [Subscription] = 1 AND [Operation] = 1 AND [SecurityRevalidationRequired] = 0
                    AND [ExpiresAt] > SYSUTCDATETIME()
                """, cancellationToken);
            if (affected != 1) throw new TenantContractException("operation_reconciliation_required");
            var operation = await database.Operations.SingleAsync(x => x.Id == request.OperationId, cancellationToken);
            if (operation.OwnerId != owner || operation.Fence != stamp.Fence) throw new TenantContractException("operation_reconciliation_required");
            operation.Checkpoint = ProvisioningCheckpoint.Completed;
            operation.OwnerId = null;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            database.Audit.Add(Audit(registration.Id, request.OperationId, actor, "ProvisionCompleted", stamp.RegistryVersion + 1));
            await database.SaveChangesAsync(cancellationToken);
            await complete.CommitAsync(cancellationToken);
        }
        catch (Exception error)
        {
            // A failed audit/checkpoint never opens readiness. Preserve a newer lifecycle/version verbatim.
            await MarkFailedAsync(error is TenantContractException contract ? contract.Code : "provision_step_failed");
            throw;
        }

        async Task AssertOwnedAsync()
        {
            await lease.AssertHeldAsync(cancellationToken);
            database.ChangeTracker.Clear();
            var current = await database.Tenants.AsNoTracking().SingleAsync(x => x.Id == registration.Id, cancellationToken);
            _ = TenantPolicy.CompleteProvisioning(current.Status(), stamp, DateTimeOffset.UtcNow);
        }
        async Task<ProvisioningCheckpoint> CheckpointAsync()
        {
            await AssertOwnedAsync();
            var operation = await database.Operations.AsNoTracking().SingleAsync(x => x.Id == request.OperationId, cancellationToken);
            if (operation.OwnerId != owner || operation.Fence != stamp.Fence || !Enum.IsDefined(operation.Checkpoint))
                throw new TenantContractException("operation_reconciliation_required");
            return operation.Checkpoint;
        }
        async Task AdvanceAsync(ProvisioningCheckpoint next)
        {
            if (await CheckpointAsync() >= next) return;
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var current = await database.Tenants.SingleAsync(x => x.Id == registration.Id, cancellationToken);
            _ = TenantPolicy.CompleteProvisioning(current.Status(), stamp, DateTimeOffset.UtcNow);
            var operation = await database.Operations.SingleAsync(x => x.Id == request.OperationId, cancellationToken);
            if (operation.OwnerId != owner || operation.Fence != stamp.Fence || (int)operation.Checkpoint != (int)next - 1)
                throw new TenantContractException("operation_reconciliation_required");
            operation.Checkpoint = next;
            current.RegistryVersion = checked(current.RegistryVersion + 1);
            database.Audit.Add(Audit(current.Id, request.OperationId, actor, next.ToString(), current.RegistryVersion));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            stamp = stamp with { RegistryVersion = current.RegistryVersion };
        }
        async Task MarkFailedAsync(string code)
        {
            try
            {
                await lease.AssertHeldAsync(CancellationToken.None);
                database.ChangeTracker.Clear();
                await using var transaction = await database.Database.BeginTransactionAsync(CancellationToken.None);
                var current = await database.Tenants.SingleAsync(x => x.Id == registration.Id, CancellationToken.None);
                if (current.RegistryVersion != stamp.RegistryVersion || current.OperationId != stamp.OperationId || current.OwnerId != owner || current.Fence != stamp.Fence) return;
                var operation = await database.Operations.SingleAsync(x => x.Id == request.OperationId, CancellationToken.None);
                current.Operation = OperationStatus.Failed;
                current.OwnerId = null;
                current.RegistryVersion++;
                operation.OwnerId = null;
                operation.Failed = true;
                operation.ErrorCode = code;
                database.Audit.Add(Audit(current.Id, request.OperationId, actor, "ProvisionFailed", current.RegistryVersion));
                await database.SaveChangesAsync(CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch { /* Original failure is retained; absence of a successful checkpoint leaves the gate closed. */ }
        }
    }

    private async Task VerifyRuntimeAsync(TenantRegistration registration, CancellationToken cancellationToken)
    {
        var runtime = connections.Customer(registration.Id, "runtime");
        var baseline = await orchard.VerifyAsync(registration.Slug, registration.Slug, registration.Id, runtime, cancellationToken);
        await TenantDatabaseVerifier.VerifyAsync(registration, runtime, baseline, cancellationToken);
    }
    private static void DemandEligible(TenantRegistration registration)
    {
        if (registration.Subscription != SubscriptionStatus.Active || registration.ExpiresAt <= DateTimeOffset.UtcNow || registration.SecurityRevalidationRequired)
            throw new TenantContractException("tenant_not_operational");
    }
    private static OperationalAudit Audit(Guid? tenantId, Guid operationId, string actor, string action, long version) => new()
    {
        TenantId = tenantId, OperationId = operationId, Actor = actor, Action = action, RegistryVersion = version, OccurredAt = DateTimeOffset.UtcNow
    };
}
