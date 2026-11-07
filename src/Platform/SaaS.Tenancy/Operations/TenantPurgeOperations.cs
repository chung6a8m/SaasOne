using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Operations;

public sealed partial class TenantOperations
{
    public Task<ResourceResult> PurgeAsync(Guid tenantId, Guid operationId, long expectedVersion, CancellationToken ct)
        => throw new NotImplementedException();
}
