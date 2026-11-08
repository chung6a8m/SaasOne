using Microsoft.EntityFrameworkCore;
using SaaS.AccessControl;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
namespace SaaS.Web.Access;

/// <summary>Private maintenance composition; never registered in an HTTP service container.</summary>
public sealed class AccessMaintenance(TenancyConnections connections, Func<ResourceCheckpoint, CancellationToken, Task>? progress = null)
{
    public TenantOperations Operations => new(connections, new NativePlanReconciler(connections), new FoundationObligationReader(), progress: progress);
    public async Task<ResourceResult> InstallAsync(Guid tenant, Guid operation, long version, CancellationToken ct)
    {
        await using var db = TenancyConnections.PlatformContext(connections.Platform("operator"));
        var current = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant, ct);
        return await Operations.ChangePlanAsync(tenant, operation, version, current.Plan, ct);
    }
    public Task<ResourceResult> ApproveAsync(Guid tenant, Guid operation, long version, AclDocument document, string reason, bool upgrade, CancellationToken ct)
    {
        ApplicationAccessProfile.Catalog.Validate(document);
        return Operations.ApproveRecoveryAsync(tenant, operation, version,
            new(document.CanonicalJson(), document.Hash(), reason, upgrade), ct);
    }
    public Task<ResourceResult> ResumeRecoveryAsync(Guid tenant, Guid operation, long version, CancellationToken ct) =>
        Operations.ResumeRecoveryAsync(tenant, operation, version, new RecoveryAclApplier(ApplicationAccessProfile.Catalog,
            (id, users, token) => ApplicationAccessProfile.DemandMembershipsAsync(connections, "operator", id, users, token)), ct);
}
