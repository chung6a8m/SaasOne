using System.Data.Common;
using SaaS.Abstractions.Tenancy;
namespace SaaS.Orders;

// Recovery reader contract-first regression skeleton; no clear decision until its real SQL checks exist.
public sealed class OrdersObligationReader : IApprovalObligationReader
{
    public Task<ObligationResult> ReadAsync(TenantContext tenant, DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(new ObligationResult(ObligationState.Unknown, 0));
}
