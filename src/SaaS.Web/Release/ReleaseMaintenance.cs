using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using SaaS.Web.Access;
using SaaS.Web.Orchard;
namespace SaaS.Web.Release;

public sealed class ReleaseMaintenance(TenancyConnections connections, ReleasePackage package,
    Func<ResourceCheckpoint, CancellationToken, Task>? progress = null)
{
    public Task<ResourceResult> ApplyAsync(Guid tenantId, Guid operationId, long expectedVersion, bool resume, CancellationToken ct)
    {
        ReleaseCatalog.DemandCompatible(package);
        var operations = new TenantOperations(connections, new ApplicationPlanReconciler(connections), new FoundationObligationReader(), progress: progress);
        return operations.ReleaseAsync(tenantId, operationId, expectedVersion, new BundleReleaseInstaller(connections, package), resume, ct);
    }
    public async Task VerifyAsync(Guid tenantId, CancellationToken ct)
    {
        ReleaseCatalog.DemandCompatible(package);
        await using var db = TenancyConnections.PlatformContext(connections.Platform("operator"));
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId, ct);
        TenantDatabaseVerifier.ValidateRegistration(tenant);
        await new BundleReleaseInstaller(connections, package).VerifyAsync(new(tenant.Id, tenant.Slug, tenant.Slug), tenant.Plan,
            connections.Customer(tenantId, "migrator"), ct);
    }
}

internal sealed class BundleReleaseInstaller(TenancyConnections connections, ReleasePackage package) : IReleaseInstaller
{
    public string ManifestSha256 => package.ManifestSha256;
    public async Task ApplyAsync(TenantContext tenant, string plan, string connection, Guid operationId, Guid ownerId,
        Func<CancellationToken, Task> demandClosed, CancellationToken ct)
    {
        ReleaseCatalog.DemandCompatible(package);
        _ = ApplicationAccessProfile.Entitlements(plan);
        var runtime = package.Manifest.RuntimeIdentifier;
        foreach (var entry in ReleaseCatalog.Describe(runtime).Where(x => x.Stream.Target == "tenant"))
        {
            await demandClosed(ct);
            var stream = entry.Stream;
            await package.VerifyFileAsync(stream.Bundle, ct);
            await OperationChildProcess.RunAsync(package.FilePath(stream.Bundle), Path.GetDirectoryName(package.FilePath("host/SaaS.Web.dll"))!,
                connection, "tenant-" + tenant.TenantId.ToString("N"), operationId, ownerId, ct);
            await demandClosed(ct);
            await using var database = ReleaseCatalog.Open(stream.Id, connection);
            await TenancyConnections.VerifySchemaAsync(database, ct);
        }
        await demandClosed(ct);
        await new OrchardMaintenance().UpgradeAsync(tenant.ShellName, tenant.UrlPrefix, tenant.TenantId, connection, ct);
        await demandClosed(ct);
    }
    public async Task VerifyAsync(TenantContext tenant, string plan, string connection, CancellationToken ct)
    {
        await using var platform = TenancyConnections.PlatformContext(connections.Platform("operator"));
        var current = await platform.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.TenantId, ct);
        if (current.Plan != plan || current.Slug != tenant.ShellName || current.Slug != tenant.UrlPrefix)
            throw new TenantContractException("release_target_changed");
        var native = await new OrchardMaintenance().VerifyAsync(tenant.ShellName, tenant.UrlPrefix, tenant.TenantId, connection, ct);
        var runtime = connections.Customer(tenant.TenantId, "runtime");
        await TenantDatabaseVerifier.VerifyAsync(current, runtime, native, ct);
        await using var sql = await SqlOperations.OpenAsync(runtime, ct);
        await ApplicationAccessProfile.VerifyAsync(tenant.TenantId, plan, sql, ct);
    }
}
