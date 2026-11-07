using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using SaaS.Web.Orchard;
namespace SaaS.Web.Release;

public sealed record PlatformReleaseResult(Guid OperationId, string ManifestSha256, bool Completed);

/// <summary>Offline platform upgrade. Never starts runtime hosts or changes customer lifecycle.</summary>
public sealed class PlatformReleaseMaintenance(TenancyConnections connections, ReleasePackage package)
{
    private sealed record Binding(string ManifestSha256, Guid OwnerId, string OwnerHost);
    public async Task<PlatformReleaseResult> ApplyAsync(Guid operationId, bool resume, CancellationToken ct)
    {
        if (operationId == Guid.Empty || Environment.GetEnvironmentVariable("SAAS_PLATFORM_RELEASE_ACK") != "all-runtime-hosts-stopped-v1")
            throw new TenantContractException("platform_offline_ack_required");
        ReleaseCatalog.DemandCompatible(package);
        var provisioner = new SqlProvisioner(connections.Provisioner());
        await provisioner.AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), PlatformReleaseBarrier.Resource, ct);
        var actor = await AuthenticateAsync(lease, ct);
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        var bootstrap = await db.Bootstrap.AsNoTracking().SingleAsync(ct);
        if (!bootstrap.Completed || bootstrap.BaselineVersion != 1 || bootstrap.OperationHost != lease.HostId || bootstrap.InstallationId == Guid.Empty)
            throw new TenantContractException("platform_not_ready");
        var bound = await db.Audit.AsNoTracking().Where(x => x.TenantId == null && x.OperationId == operationId
            && x.Action == PlatformReleaseBarrier.Bound).ToArrayAsync(ct);
        if (bound.Length > 1) throw new TenantContractException("platform_release_binding_invalid");
        Binding binding;
        if (bound.Length == 1)
        {
            binding = JsonSerializer.Deserialize<Binding>(bound[0].Details, ReleasePackage.Json) ?? throw new TenantContractException("platform_release_binding_invalid");
            if (bound[0].Actor != actor || binding.OwnerId == Guid.Empty || binding.OwnerHost != lease.HostId
                || !Digest.EqualsSha256(binding.ManifestSha256, package.ManifestSha256)) throw new TenantContractException("platform_release_binding_mismatch");
            if (await db.Audit.AnyAsync(x => x.TenantId == null && x.OperationId == operationId && x.Action == PlatformReleaseBarrier.Completed, ct))
            {
                await VerifyAsync(bootstrap.InstallationId, ct);
                return new(operationId, package.ManifestSha256, true);
            }
            if (!resume) throw new TenantContractException("platform_release_resume_required");
            await provisioner.AssertPriorOwnerStoppedAsync(operationId, binding.OwnerId, binding.OwnerHost, lease.HostId, ct);
        }
        else
        {
            if (resume) throw new TenantContractException("platform_release_not_found");
            await PlatformReleaseBarrier.DemandClearAsync(lease.Connection, ct);
            if (await db.Audit.AnyAsync(x => x.OperationId == operationId, ct)) throw new TenantContractException("operation_request_conflict");
            await DemandIdleAsync(lease, actor, ct);
            binding = new(package.ManifestSha256, Guid.NewGuid(), lease.HostId);
            db.Audit.Add(Audit(PlatformReleaseBarrier.Bound));
            await db.SaveChangesAsync(ct);
        }
        await DemandIdleAsync(lease, actor, ct);
        var stream = ReleaseCatalog.Describe(package.Manifest.RuntimeIdentifier).Single(x => x.Stream.Target == "platform").Stream;
        // Only a known ordered prefix may be upgraded. Unknown/newer history is never rolled back.
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        if (applied.Length == 0 || applied.Length > stream.Migrations.Length
            || !applied.SequenceEqual(stream.Migrations.Take(applied.Length), StringComparer.Ordinal))
            throw new TenantContractException("platform_release_history_mismatch");
        var connection = TenancyConnections.ForOperation(connections.Platform("migrator"), operationId, binding.OwnerId);
        await package.VerifyFileAsync(stream.Bundle, ct);
        await OperationChildProcess.RunAsync(package.FilePath(stream.Bundle), Path.GetDirectoryName(package.FilePath("host/SaaS.Web.dll"))!,
            connection, PlatformReleaseBarrier.Resource, operationId, binding.OwnerId, ct);
        await DemandIdleAsync(lease, actor, ct);
        await new OrchardMaintenance().UpgradeAsync("Default", "", bootstrap.InstallationId, connection, ct);
        await VerifyAsync(bootstrap.InstallationId, ct);
        await DemandIdleAsync(lease, actor, ct);
        db.Audit.Add(Audit(PlatformReleaseBarrier.Completed));
        await db.SaveChangesAsync(ct);
        return new(operationId, package.ManifestSha256, true);

        OperationalAudit Audit(string action) => new()
        {
            OperationId = operationId, Actor = actor, Action = action, OccurredAt = DateTimeOffset.UtcNow,
            Details = JsonSerializer.Serialize(binding, ReleasePackage.Json)
        };
    }
    private async Task VerifyAsync(Guid installationId, CancellationToken ct)
    {
        var connection = connections.Platform("runtime");
        await SqlOperations.RequireRuntimeAsync(connection, ct);
        await using var db = TenancyConnections.PlatformContext(connection);
        await TenancyConnections.VerifySchemaAsync(db, ct);
        await new OrchardMaintenance().VerifyAsync("Default", "", installationId, connection, ct);
    }
    private static async Task<string> AuthenticateAsync(OperationLease lease, CancellationToken ct) =>
        await SqlOperations.ScalarAsync(lease.Connection,
            "SELECT CASE WHEN IS_ROLEMEMBER('saas_operator') = 1 AND IS_SRVROLEMEMBER('sysadmin') = 0 THEN ORIGINAL_LOGIN() ELSE NULL END", ct) as string
            ?? throw new TenantContractException("operator_not_authorized");
    private async Task DemandIdleAsync(OperationLease lease, string actor, CancellationToken ct)
    {
        await lease.AssertHeldAsync(ct);
        if (await AuthenticateAsync(lease, ct) != actor) throw new TenantContractException("operator_not_authorized");
        await using var db = TenancyConnections.PlatformContext(lease.Connection);
        if (await db.Tenants.AnyAsync(x => x.Operation != OperationStatus.None, ct)) throw new TenantContractException("tenant_operation_incomplete");
        await using var server = await SqlOperations.OpenAsync(connections.Provisioner(), ct);
        var count = await SqlOperations.ScalarAsync(server, """
            SELECT COUNT(*) FROM sys.dm_exec_sessions
            WHERE login_name = 'saas_platform_runtime' OR login_name LIKE 'saas[_]rt[_]%'
            """, ct);
        if (count is not int value || value != 0) throw new TenantContractException("runtime_sql_sessions_active");
    }
}
