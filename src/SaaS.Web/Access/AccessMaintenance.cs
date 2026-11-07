using SaaS.AccessControl;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
namespace SaaS.Web.Access;

/// <summary>Private maintenance composition; never registered in an HTTP service container.</summary>
public sealed class AccessMaintenance
{
    public AccessMaintenance(TenancyConnections connections) => ArgumentNullException.ThrowIfNull(connections);
    public TenantOperations Operations => throw new NotImplementedException();
    public Task<ResourceResult> InstallAsync(Guid tenant, Guid operation, long version, CancellationToken ct) => throw new NotImplementedException();
    public Task<ResourceResult> ApproveAsync(Guid tenant, Guid operation, long version, AclDocument document, string reason, bool upgrade, CancellationToken ct) => throw new NotImplementedException();
    public Task<ResourceResult> ResumeRecoveryAsync(Guid tenant, Guid operation, long version, CancellationToken ct) => throw new NotImplementedException();
}
