using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Operations;

public sealed class TenantTransactionCoordinator(string connection) : ITenantTransactionCoordinator
{
    public Task<T> ExecuteAsync<T>(Guid tenantId, long gateVersion, Func<TenantTransaction, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        _ = connection;
        throw new NotImplementedException();
    }
}
